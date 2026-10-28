using Concentus.Enums;
using Concentus.Structs;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

// Proximity voice chat. Lives on the Player prefab next to FirstPersonController, runs on every computer.
// - Your own player: records the default microphone, cuts it into 20 ms frames, squeezes each one with Opus
//   (Concentus, Assets/Plugins) and sends it to everyone else, only while you're actually talking. Always on;
//   the mute key toggles it. Talking loud is a noise the monster can hear (like footsteps).
// - Everyone else's player: plays their voice from their head as a 3D sound, so you hear teammates from where they
//   are, fading with distance. A player the monster has hidden calls out from the hiding spot: their voice is the clue.
// Dead players go quiet.
public class VoiceChat : NetworkBehaviour
{
    [Header("Microphone")]
    [Tooltip("Toggles your microphone on and off.")]
    public Key muteKey = Key.M;
    [Tooltip("Mic level (RMS, 0-1) below which you count as silent and nothing is sent.")]
    public float gate = 0.008f;
    [Tooltip("Keeps sending this long (s) after you go quiet, so the ends of words aren't cut off.")]
    public float holdOpen = 0.35f;
    [Tooltip("Opus bitrate (bits per second). 24000 is clear speech for ~3 KB/s per talking player.")]
    public int bitrate = 24000;

    [Header("Playback (other players)")]
    [Tooltip("Their voice is at full volume this close (m)...")]
    public float fullVolumeDistance = 2f;
    [Tooltip("...and fades out completely at this distance (m).")]
    public float maxDistance = 40f;
    [Range(0f, 2f)] public float volume = 1f;
    [Tooltip("Voice is held back this long (ms) before playing, to smooth out uneven network delivery.")]
    public float bufferMs = 80f;

    [Header("The monster hears you")]
    [Tooltip("Talking louder than this (mic RMS) makes a noise...")]
    public float loudLevel = 0.04f;
    [Tooltip("...and at this level you're shouting.")]
    public float shoutLevel = 0.25f;
    [Tooltip("Noise radius (m) of normal talking...")]
    public float talkRadius = 7f;
    [Tooltip("...and of shouting.")]
    public float shoutRadius = 28f;
    [Tooltip("At most one talking noise this often (s).")]
    public float noiseInterval = 0.5f;

    /// <summary>Your own mic is muted (shown on the HUD).</summary>
    public static bool Muted { get; private set; }

    // ---- Which microphone (MIC button in the main / pause menu, saved on this PC; "" = the Windows default) ----
    const string DevicePref = "MicDevice";
    static VoiceChat local;   // your own player's VoiceChat, to switch the mic live

    /// <summary>The chosen microphone, or "" for the Windows default (also when the saved one is unplugged).</summary>
    public static string ChosenDevice
    {
        get
        {
            string d = PlayerPrefs.GetString(DevicePref, "");
            return d != "" && System.Array.IndexOf(Microphone.devices, d) >= 0 ? d : "";
        }
    }

    /// <summary>Next microphone in the list (Default, then each device), saved and switched to right away.</summary>
    public static void NextDevice()
    {
        var devices = Microphone.devices;
        int i = System.Array.IndexOf(devices, ChosenDevice);   // -1 = Default
        string next = i + 1 < devices.Length ? devices[i + 1] : "";
        PlayerPrefs.SetString(DevicePref, next);
        PlayerPrefs.Save();
        if (local != null) local.RestartMic();
    }

    public static string DeviceLabel
    {
        get
        {
            string d = ChosenDevice;
            if (d == "") return "MIC: DEFAULT";
            // "Microphone (Volt 2)" -> "VOLT 2"
            int open = d.IndexOf('('), close = d.LastIndexOf(')');
            if (open >= 0 && close > open) d = d.Substring(open + 1, close - open - 1);
            if (d.Length > 18) d = d.Substring(0, 18);
            return "MIC: " + d.ToUpperInvariant();
        }
    }

    FirstPersonController player;

    // ---- Sending (owner) ----
    string micDevice;
    AudioClip micClip;
    int micRate, frameSize, micReadPos;
    float[] frame;
    short[] frameShorts;
    byte[] packet = new byte[1275];   // largest possible Opus packet
    OpusEncoder encoder;
    float lastVoiceTime = -10f, nextNoise;
    bool noMic;

    // ---- Receiving (other players) ----
    AudioSource voiceSource;
    OpusDecoder decoder;
    int playRate;
    short[] decoded;
    readonly object ringLock = new object();
    float[] ring;
    int ringRead, ringCount;
    bool primed;

    void Awake() => player = GetComponent<FirstPersonController>();

    public override void OnNetworkSpawn()
    {
        if (!IsOwner) return;
        local = this;
        StartMic();
    }

    public override void OnNetworkDespawn()
    {
        StopMic();
        if (local == this) local = null;
    }

    void StopMic()
    {
        if (micClip != null) Microphone.End(micDevice);
        micClip = null;
    }

    void RestartMic()
    {
        StopMic();
        StartMic();
    }

    // ---------- Sending ----------

    void StartMic()
    {
        noMic = Microphone.devices.Length == 0;
        if (noMic) { Debug.Log("VoiceChat: no microphone found"); return; }
        micDevice = ChosenDevice == "" ? null : ChosenDevice;   // null = the Windows default
        Microphone.GetDeviceCaps(micDevice, out int min, out int max);
        micRate = 48000;
        foreach (int r in new[] { 16000, 24000, 48000, 12000, 8000 })   // rates Opus works at, voice-friendly first
            if ((min == 0 && max == 0) || (r >= min && r <= max)) { micRate = r; break; }

        micClip = Microphone.Start(micDevice, true, 1, micRate);
        frameSize = micRate / 50;   // 20 ms
        frame = new float[frameSize];
        frameShorts = new short[frameSize];
        encoder = OpusEncoder.Create(micRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = bitrate;
        micReadPos = 0;
        Debug.Log($"VoiceChat: microphone '{micDevice ?? "default"}' on at {micRate} Hz");
    }

    void Update()
    {
        if (!IsSpawned || !IsOwner) return;

        var kb = Keyboard.current;
        if (kb != null && kb[muteKey].wasPressedThisFrame && !NetworkUI.MenuOpen) Muted = !Muted;

        if (micClip == null) return;
        int pos = Microphone.GetPosition(micDevice);
        int len = micClip.samples;
        int available = (pos - micReadPos + len) % len;
        while (available >= frameSize)
        {
            micClip.GetData(frame, micReadPos);   // wraps round the end of the looping clip
            micReadPos = (micReadPos + frameSize) % len;
            available -= frameSize;
            SendFrame();
        }
    }

    void SendFrame()
    {
        if (Muted || player == null || player.IsDead) return;

        float sum = 0f;
        for (int i = 0; i < frameSize; i++) sum += frame[i] * frame[i];
        float rms = Mathf.Sqrt(sum / frameSize);
        if (rms > gate) lastVoiceTime = Time.time;
        if (Time.time - lastVoiceTime > holdOpen) return;   // silence: send nothing

        for (int i = 0; i < frameSize; i++) frameShorts[i] = (short)(Mathf.Clamp(frame[i], -1f, 1f) * 32767f);
        int bytes = encoder.Encode(frameShorts, 0, frameSize, packet, 0, packet.Length);
        if (bytes > 0)
        {
            var data = new byte[bytes];
            System.Array.Copy(packet, data, bytes);
            VoiceRpc(micRate, data);
        }

        // Loud enough for the monster to hear
        if (rms > loudLevel && Time.time >= nextNoise)
        {
            nextNoise = Time.time + noiseInterval;
            float radius = Mathf.Lerp(talkRadius, shoutRadius, Mathf.InverseLerp(loudLevel, shoutLevel, rms));
            NoiseRpc(player.cameraPivot.position, radius);
        }
    }

    [Rpc(SendTo.Server)]
    void NoiseRpc(Vector3 position, float radius) => NoiseSystem.Emit(position, radius);

    // Everyone else gets each frame; lost frames are just skipped (a tiny gap)
    [Rpc(SendTo.NotMe, Delivery = RpcDelivery.Unreliable)]
    void VoiceRpc(int rate, byte[] data)
    {
        if (data == null || data.Length == 0 || rate <= 0) return;
        if (decoder == null || rate != playRate) SetUpPlayback(rate);
        int n = decoder.Decode(data, 0, data.Length, decoded, 0, decoded.Length, false);
        if (n <= 0) return;

        lock (ringLock)
        {
            int cap = ring.Length;
            for (int i = 0; i < n; i++)
            {
                if (ringCount == cap) { ringRead = (ringRead + 1) % cap; ringCount--; }   // full: drop the oldest
                ring[(ringRead + ringCount) % cap] = decoded[i] / 32768f;
                ringCount++;
            }
            // Never let it lag far behind: past 4x the buffer, skip ahead
            int maxLag = Mathf.Max(1, (int)(playRate * bufferMs / 1000f) * 4);
            if (ringCount > maxLag) { int drop = ringCount - maxLag / 2; ringRead = (ringRead + drop) % cap; ringCount -= drop; }
        }
    }

    // ---------- Receiving ----------

    void SetUpPlayback(int rate)
    {
        playRate = rate;
        decoder = OpusDecoder.Create(rate, 1);
        decoded = new short[rate / 50 * 6];   // room for the longest Opus frame (120 ms)
        lock (ringLock) { ring = new float[rate]; ringRead = ringCount = 0; primed = false; }

        if (voiceSource == null)
        {
            var go = new GameObject("Voice");
            go.transform.SetParent(player != null && player.cameraPivot != null ? player.cameraPivot : transform, false);
            voiceSource = go.AddComponent<AudioSource>();
        }
        AudioMix.Setup3D(voiceSource, fullVolumeDistance, maxDistance);
        voiceSource.volume = volume;
        voiceSource.loop = true;
        voiceSource.clip = AudioClip.Create("Voice", rate, 1, rate, true, ReadVoice);
        voiceSource.Play();
    }

    // Audio thread: hand over buffered voice, or silence while waiting for enough to play smoothly
    void ReadVoice(float[] data)
    {
        lock (ringLock)
        {
            int prime = (int)(playRate * bufferMs / 1000f);
            if (!primed && ringCount >= prime) primed = true;
            for (int i = 0; i < data.Length; i++)
            {
                if (primed && ringCount > 0)
                {
                    data[i] = ring[ringRead];
                    ringRead = (ringRead + 1) % ring.Length;
                    ringCount--;
                }
                else
                {
                    data[i] = 0f;
                    primed = false;   // ran dry: wait to build up the buffer again
                }
            }
        }
    }

    // Dead: their voice stops on every computer
    void LateUpdate()
    {
        if (voiceSource != null) voiceSource.mute = player != null && player.IsDead;
    }

    void OnGUI()
    {
        if (!IsSpawned || !IsOwner || NetworkUI.MenuOpen) return;
        if (!Muted && !noMic) return;
        GUI.color = new Color(1f, 1f, 1f, 0.75f);
        GUI.Label(new Rect(16, Screen.height - 34, 300, 24), noMic ? "NO MICROPHONE" : $"MIC MUTED ({muteKey})");
        GUI.color = Color.white;
    }
}
