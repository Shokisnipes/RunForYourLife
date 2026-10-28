using Unity.Netcode;
using UnityEngine;

// Syncs every ground-floor window (BreakableWindow): intact, smashed by the Climber, or boarded up. The host owns
// the state; the Climber calls Break on its way in or out. Smashing glass is loud: every monster hears it.
// Boarding windows up is a later task (players nailing planks over them, for the small monsters); Board() is ready.
// Place ONE of these on the same scene object as GameFlow (it needs the NetworkObject).
public class WindowManager : NetworkBehaviour
{
    [Header("Holed windows")]
    [Tooltip("Share of ground-floor windows that start with a scout-sized hole in the glass.")]
    [Range(0f, 1f)] public float holedChance = 0.15f;

    [Header("Smashing")]
    [Tooltip("Breaking glass is heard this far away (metres).")]
    public float noiseRadius = 30f;

    [Header("Sound (optional)")]
    public AudioClip smashClip;
    [Tooltip("Set to the level in AudioMix.cs (glass smash -14 dB).")]
    [Range(0f, 1f)] public float smashVolume = 0.8f;
    [Tooltip("Smash fades out at this distance.")]
    public float soundRange = 45f;

    // One entry per window (BreakableWindow.State), in window order (sorted by position so every machine agrees)
    readonly NetworkList<byte> states = new NetworkList<byte>();
    BreakableWindow[] windows = new BreakableWindow[0];

    public static WindowManager Instance { get; private set; }

    void Awake() => Instance = this;

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        Refresh();
        if (IsServer)
        {
            states.Clear();
            for (int i = 0; i < windows.Length; i++)
                states.Add((byte)(!windows[i].shuttered && Random.value < holedChance
                    ? BreakableWindow.State.Holed : BreakableWindow.State.Intact));
        }
        states.OnListChanged += OnStatesChanged;
        CityManager.Built += OnCityBuilt;
        ShowAll();
    }

    public override void OnNetworkDespawn()
    {
        states.OnListChanged -= OnStatesChanged;
        CityManager.Built -= OnCityBuilt;
    }

    void OnCityBuilt()
    {
        Refresh();
        ShowAll();
    }

    void ShowAll()
    {
        for (int i = 0; i < windows.Length && i < states.Count; i++) windows[i].Show((BreakableWindow.State)states[i]);
    }

    void OnStatesChanged(NetworkListEvent<byte> e)
    {
        if (e.Type != NetworkListEvent<byte>.EventType.Value || e.Index < 0 || e.Index >= windows.Length) { ShowAll(); return; }
        var w = windows[e.Index];
        var state = (BreakableWindow.State)e.Value;
        w.Show(state, true);
        if ((state == BreakableWindow.State.Broken || state == BreakableWindow.State.BrokenOutward) && smashClip != null)
        {
            var go = new GameObject("Glass Smash");
            go.transform.position = w.Center;
            var s = go.AddComponent<AudioSource>();
            AudioMix.Setup3D(s, 4f, soundRange);
            s.PlayOneShot(smashClip, smashVolume);
            Destroy(go, smashClip.length + 0.1f);
        }
    }

    // Same order on every machine: sort by position
    void Refresh()
    {
        windows = BreakableWindow.All.ToArray();
        System.Array.Sort(windows, (a, b) =>
        {
            Vector3 p = a.transform.position, q = b.transform.position;
            int c = p.x.CompareTo(q.x);
            if (c != 0) return c;
            c = p.z.CompareTo(q.z);
            return c != 0 ? c : p.y.CompareTo(q.y);
        });
    }

    // Host: the Climber smashes through this window (no-op if it's already broken). fromInside = it's getting out,
    // so the glass flies into the street instead of the room.
    public void Break(BreakableWindow w, bool fromInside = false)
    {
        if (!IsServer) return;
        int i = System.Array.IndexOf(windows, w);
        var now = i >= 0 && i < states.Count ? (BreakableWindow.State)states[i] : BreakableWindow.State.Broken;
        if (now != BreakableWindow.State.Intact && now != BreakableWindow.State.Holed) return;
        states[i] = (byte)(fromInside ? BreakableWindow.State.BrokenOutward : BreakableWindow.State.Broken);
        NoiseSystem.Emit(w.Center, noiseRadius);
    }

    // Host: boards across a window (for the window-boarding task)
    public void Board(BreakableWindow w)
    {
        if (!IsServer) return;
        int i = System.Array.IndexOf(windows, w);
        if (i >= 0 && i < states.Count) states[i] = (byte)BreakableWindow.State.Boarded;
    }
}
