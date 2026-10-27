using UnityEngine;

// Put on any empty object in the scene (e.g. "Ambience"). Runs on every computer.
// - Evening bed: calm, sparse birds while it's still light. Fades out as night falls.
// - Night bed: a constant drone/wind loop. Fades in as SkyCycle darkens the evening into night.
// - Random distant one-shots (clangs, barks, far-off screams) from random directions around you, only at night.
// Both beds are crossfaded into themselves at the loop seam so you can't hear where they start over.
public class AmbientAudio : MonoBehaviour
{
    [Header("Evening (prep)")]
    [Tooltip("Calm dusk ambience (sparse birds). 2D, gain-staged so volume 1 hits the mix target (see AudioMix).")]
    public AudioClip duskLoop;
    [Range(0f, 1f)] public float duskVolume = 1f;
    [Tooltip("The evening bed fades out over this stretch of the dusk-to-night darkness (0 = dusk, 1 = night).")]
    public Vector2 duskFade = new Vector2(0.25f, 0.7f);

    [Header("Night")]
    [Tooltip("The night sounds fade in over this stretch of the dusk-to-night darkness (0 = dusk, 1 = night).")]
    public Vector2 nightFade = new Vector2(0.35f, 0.9f);
    [Tooltip("Distant sounds only play once it's at least this dark.")]
    [Range(0f, 1f)] public float distantSoundsFrom = 0.75f;

    [Header("Night background loop")]
    public AudioClip ambienceLoop;
    [Range(0f, 1f)] public float ambienceVolume = 0.4f;
    [Tooltip("Seconds the end of a loop blends into its start. 0 = plain loop.")]
    [Range(0f, 15f)] public float crossfadeTime = 4f;
    [Tooltip("Seconds to fade the ambience in when the scene starts.")]
    [Range(0f, 15f)] public float fadeInTime = 3f;

    [Header("Random distant sounds")]
    public AudioClip[] distantSounds;
    [Range(0f, 1f)] public float distantVolume = 0.7f;
    public Vector2 interval = new Vector2(12f, 35f);   // seconds between sounds
    public Vector2 distance = new Vector2(25f, 60f);   // how far away they play

    Loop dusk, night;
    float startTime;
    float nextSoundTime;

    void Start()
    {
        if (duskLoop != null) dusk = new Loop(gameObject, duskLoop);
        if (ambienceLoop != null) night = new Loop(gameObject, ambienceLoop);
        startTime = Time.time;
        Schedule();
    }

    void Update()
    {
        // Overall fade-in at scene start, then each bed follows how dark it is
        float master = fadeInTime > 0f ? Mathf.Clamp01((Time.time - startTime) / fadeInTime) : 1f;
        float dark = SkyCycle.Darkness01;
        float nightAmount = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(nightFade.x, nightFade.y, dark));
        float duskAmount = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(duskFade.x, duskFade.y, dark));

        dusk?.Update(duskVolume * master * duskAmount, crossfadeTime);
        night?.Update(ambienceVolume * master * nightAmount, crossfadeTime);
        UpdateDistantSounds();
    }

    // A looping 2D bed: two players take turns and overlap at the seam (equal-power crossfade).
    // Silent beds are paused, so a 5-minute evening loop doesn't keep streaming all night.
    class Loop
    {
        readonly AudioClip clip;
        AudioSource current, next;
        bool crossfading;

        public Loop(GameObject host, AudioClip clip)
        {
            this.clip = clip;
            current = Make(host);
            next = Make(host);
        }

        AudioSource Make(GameObject host)
        {
            var s = host.AddComponent<AudioSource>();
            s.clip = clip;
            s.loop = false;
            s.playOnAwake = false;
            s.spatialBlend = 0f;
            s.volume = 0f;
            return s;
        }

        public void Update(float volume, float crossfadeTime)
        {
            if (volume <= 0.0001f)
            {
                if (current.isPlaying) current.Pause();
                if (next.isPlaying) next.Pause();
                return;
            }
            if (!current.isPlaying && !crossfading && current.time > 0f) current.UnPause();
            else if (!current.isPlaying && !crossfading) current.Play();
            if (crossfading && !next.isPlaying) next.UnPause();

            float length = clip.length;
            float fade = Mathf.Min(crossfadeTime, length * 0.45f);
            if (fade <= 0.01f)
            {
                current.loop = true;   // plain loop
                current.volume = volume;
                return;
            }

            float remaining = length - current.time;

            // Start the second copy before the first one ends
            if (!crossfading && remaining <= fade)
            {
                next.time = 0f;
                next.Play();
                crossfading = true;
            }

            if (crossfading)
            {
                float p = current.isPlaying ? Mathf.Clamp01(1f - remaining / fade) : 1f;
                current.volume = volume * Mathf.Cos(p * Mathf.PI * 0.5f);
                next.volume = volume * Mathf.Sin(p * Mathf.PI * 0.5f);
                if (p >= 1f)
                {
                    current.Stop();
                    (current, next) = (next, current);   // swap roles
                    crossfading = false;
                }
            }
            else current.volume = volume;
        }
    }

    void UpdateDistantSounds()
    {
        if (Time.time < nextSoundTime) return;
        Schedule();

        if (distantSounds == null || distantSounds.Length == 0) return;
        if (SkyCycle.Darkness01 < distantSoundsFrom) return;   // still evening

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
        AudioMix.Setup3D(s, 5f, distance.y + 20f);
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
