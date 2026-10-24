using UnityEngine;

// Put on any empty object in the scene (e.g. "Ambience"). Runs on every computer.
// - A constant background drone/wind loop, crossfaded into itself so you can't hear the seam
// - Random distant one-shots (clangs, barks, far-off screams) from random directions around you
public class AmbientAudio : MonoBehaviour
{
    [Header("Background loop")]
    public AudioClip ambienceLoop;
    [Range(0f, 1f)] public float ambienceVolume = 0.4f;
    [Tooltip("Seconds the end of the loop blends into its start. 0 = plain loop.")]
    [Range(0f, 15f)] public float crossfadeTime = 4f;
    [Tooltip("Seconds to fade the ambience in when the scene starts.")]
    [Range(0f, 15f)] public float fadeInTime = 3f;

    [Header("Random distant sounds")]
    public AudioClip[] distantSounds;
    [Range(0f, 1f)] public float distantVolume = 0.7f;
    public Vector2 interval = new Vector2(12f, 35f);   // seconds between sounds
    public Vector2 distance = new Vector2(25f, 60f);   // how far away they play

    AudioSource current, next;     // two players that take turns, overlapping at the seam
    bool crossfading;
    float startTime;
    float nextSoundTime;

    void Start()
    {
        if (ambienceLoop != null)
        {
            current = MakeLoopSource();
            next = MakeLoopSource();
            current.Play();
        }
        startTime = Time.time;
        Schedule();
    }

    void Update()
    {
        UpdateAmbience();
        UpdateDistantSounds();
    }

    void UpdateAmbience()
    {
        if (current == null) return;

        // Overall fade-in at scene start
        float master = fadeInTime > 0f ? Mathf.Clamp01((Time.time - startTime) / fadeInTime) : 1f;
        float baseVol = ambienceVolume * master;

        float length = ambienceLoop.length;
        float fade = Mathf.Min(crossfadeTime, length * 0.45f);

        if (fade <= 0.01f)
        {
            // Plain loop
            current.loop = true;
            if (!current.isPlaying) current.Play();
            current.volume = baseVol;
            return;
        }
        current.loop = false;

        float remaining = length - current.time;

        // Start the second copy before the first one ends
        if (!crossfading && (remaining <= fade || !current.isPlaying))
        {
            next.time = 0f;
            next.Play();
            crossfading = true;
        }

        if (crossfading)
        {
            // Equal-power crossfade: keeps loudness steady through the blend
            float p = current.isPlaying ? Mathf.Clamp01(1f - remaining / fade) : 1f;
            current.volume = baseVol * Mathf.Cos(p * Mathf.PI * 0.5f);
            next.volume = baseVol * Mathf.Sin(p * Mathf.PI * 0.5f);

            if (p >= 1f)
            {
                current.Stop();
                (current, next) = (next, current);   // swap roles
                crossfading = false;
            }
        }
        else
        {
            current.volume = baseVol;
        }
    }

    AudioSource MakeLoopSource()
    {
        var s = gameObject.AddComponent<AudioSource>();
        s.clip = ambienceLoop;
        s.loop = false;
        s.playOnAwake = false;
        s.spatialBlend = 0f;
        s.volume = 0f;
        return s;
    }

    void UpdateDistantSounds()
    {
        if (Time.time < nextSoundTime) return;
        Schedule();

        if (distantSounds == null || distantSounds.Length == 0) return;

        // Pick a random spot around whoever is listening
        Vector3 center = Camera.main != null ? Camera.main.transform.position : transform.position;
        Vector2 dir = Random.insideUnitCircle.normalized;
        float d = Random.Range(distance.x, distance.y);
        Vector3 pos = center + new Vector3(dir.x, 0f, dir.y) * d + Vector3.up * Random.Range(0f, 8f);

        PlayAt(distantSounds[Random.Range(0, distantSounds.Length)], pos);
    }

    void PlayAt(AudioClip clip, Vector3 pos)
    {
        var go = new GameObject("DistantSound");
        go.transform.position = pos;
        var s = go.AddComponent<AudioSource>();
        s.clip = clip;
        s.spatialBlend = 1f;
        s.rolloffMode = AudioRolloffMode.Linear;
        s.minDistance = 5f;
        s.maxDistance = distance.y + 20f;
        s.dopplerLevel = 0f;
        s.pitch = Random.Range(0.9f, 1.05f);
        s.volume = distantVolume;
        s.Play();
        Destroy(go, clip.length / s.pitch + 0.1f);
    }

    void Schedule()
    {
        nextSoundTime = Time.time + Random.Range(interval.x, interval.y);
    }
}
