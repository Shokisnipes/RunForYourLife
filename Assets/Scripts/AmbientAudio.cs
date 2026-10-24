using UnityEngine;

// Put on any empty object in the scene (e.g. "Ambience"). Runs on every computer.
// - A constant background drone/wind loop
// - Random distant one-shots (clangs, barks, far-off screams) from random directions around you
public class AmbientAudio : MonoBehaviour
{
    [Header("Background loop")]
    public AudioClip ambienceLoop;
    [Range(0f, 1f)] public float ambienceVolume = 0.4f;

    [Header("Random distant sounds")]
    public AudioClip[] distantSounds;
    [Range(0f, 1f)] public float distantVolume = 0.7f;
    public Vector2 interval = new Vector2(12f, 35f);   // seconds between sounds
    public Vector2 distance = new Vector2(25f, 60f);   // how far away they play

    AudioSource loop;
    float nextSoundTime;

    void Start()
    {
        if (ambienceLoop != null)
        {
            loop = gameObject.AddComponent<AudioSource>();
            loop.clip = ambienceLoop;
            loop.loop = true;
            loop.spatialBlend = 0f;
            loop.volume = ambienceVolume;
            loop.Play();
        }
        Schedule();
    }

    void Update()
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
