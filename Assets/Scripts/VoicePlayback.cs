using UnityEngine;

// Plays one player's incoming voice (added by VoiceChat on the "Voice" object at their head, next to its AudioSource).
// The AudioSource plays a steady 1.0 tone, which Unity fades with distance and pans in 3D like any other sound;
// OnAudioFilterRead multiplies that by the voice, sample by sample, on the audio thread. That way the audio thread
// only ever takes what has really arrived (a streamed AudioClip reads ~85 ms ahead and bakes in silence = drop-outs).
[RequireComponent(typeof(AudioSource))]
public class VoicePlayback : MonoBehaviour
{
    [Header("Stats (read only)")]
    public int packets;      // voice frames received
    public int underruns;    // times the buffer ran dry mid-speech (a gap you can hear)
    public int dropped;      // samples thrown away because playback fell too far behind

    readonly object ringLock = new object();
    float[] ring = new float[1];
    int ringRead, ringCount;
    bool primed, talking;
    int voiceRate = 16000, outRate = 48000, prime, maxLag;
    double readPos;   // fractional position between two voice samples (resampling voice rate -> output rate)
    float lastSample;

    static AudioClip ones;

    // Main thread: get ready for voice at this sample rate
    public void Setup(int rate, float bufferMs)
    {
        outRate = AudioSettings.outputSampleRate;
        lock (ringLock)
        {
            voiceRate = rate;
            ring = new float[rate];   // 1 s
            ringRead = ringCount = 0;
            primed = talking = false;
            readPos = 0;
            prime = Mathf.Max(1, (int)(rate * bufferMs / 1000f));
            maxLag = prime * 4;
        }

        var source = GetComponent<AudioSource>();
        if (ones == null)
        {
            ones = AudioClip.Create("VoiceCarrier", outRate, 1, outRate, false);
            var data = new float[outRate];
            for (int i = 0; i < data.Length; i++) data[i] = 1f;
            ones.SetData(data, 0);
        }
        source.clip = ones;
        source.loop = true;
        if (!source.isPlaying) source.Play();
    }

    // Main thread: a decoded frame arrived
    public void Push(short[] pcm, int count)
    {
        lock (ringLock)
        {
            packets++;
            int cap = ring.Length;
            for (int i = 0; i < count; i++)
            {
                if (ringCount == cap) { ringRead = (ringRead + 1) % cap; ringCount--; dropped++; }
                ring[(ringRead + ringCount) % cap] = pcm[i] / 32768f;
                ringCount++;
            }
            // Fell far behind (a burst after a hitch): skip ahead so the voice doesn't lag
            if (ringCount > maxLag)
            {
                int drop = ringCount - prime * 2;
                ringRead = (ringRead + drop) % cap;
                ringCount -= drop;
                dropped += drop;
            }
        }
    }

    // Audio thread
    void OnAudioFilterRead(float[] data, int channels)
    {
        lock (ringLock)
        {
            if (!primed && ringCount >= prime) { primed = true; talking = true; }
            double step = (double)voiceRate / outRate;
            int cap = ring.Length;
            for (int i = 0; i < data.Length; i += channels)
            {
                float s = 0f;
                if (primed)
                {
                    if (ringCount >= 2)
                    {
                        // Linear blend between the two voice samples around this output sample
                        float a = ring[ringRead], b = ring[(ringRead + 1) % cap];
                        s = a + (b - a) * (float)readPos;
                        readPos += step;
                        while (readPos >= 1.0 && ringCount >= 2) { readPos -= 1.0; ringRead = (ringRead + 1) % cap; ringCount--; }
                        lastSample = s;
                    }
                    else
                    {
                        // Ran dry: fade the last sample out quickly (no click) and wait to build up again
                        primed = false;
                        if (talking) underruns++;
                        talking = false;
                    }
                }
                if (!primed) { lastSample *= 0.95f; s = lastSample; }
                for (int c = 0; c < channels; c++) data[i + c] *= s;
            }
        }
    }
}
