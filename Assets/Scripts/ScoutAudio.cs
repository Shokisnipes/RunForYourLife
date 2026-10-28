using UnityEngine;

// On the Scout root; runs on every client. A quiet skitter loop that follows how fast it moves, a sniff now and
// then when it stops, the relay screech (cut short if it's killed mid-call) and its death squeal.
// The scout's AI calls Screech / StopScreech / Die; the movement comes from ScoutAnimator.velocity.
public class ScoutAudio : MonoBehaviour
{
    [Header("Clips")]
    [Tooltip("Looping skitter of claws while it moves.")]
    public AudioClip skitterLoop;
    public AudioClip[] sniffClips;
    [Tooltip("The relay call: if it finishes, the monster learns where you were.")]
    public AudioClip[] screechClips;
    public AudioClip[] deathClips;

    [Header("Volume and range (levels in AudioMix)")]
    [Range(0f, 1f)] public float skitterVolume = 0.34f;
    [Tooltip("Crawl speed (m/s) at which the skitter is at full volume.")]
    public float skitterFullSpeed = 3f;
    [Range(0f, 1f)] public float sniffVolume = 0.35f;
    [Range(0f, 1f)] public float screechVolume = 0.62f;
    [Range(0f, 1f)] public float deathVolume = 1f;
    [Tooltip("How far its skitter and sniffing carry.")]
    public float quietRange = 18f;
    [Tooltip("How far the screech carries (players hear it too: it's the warning).")]
    public float screechRange = 70f;
    [Tooltip("Sounds are at full volume this close (see AudioMix).")]
    public float fullVolumeDistance = 1f;

    [Header("Timing")]
    [Tooltip("Seconds between sniffs while it stands still (random in this range).")]
    public Vector2 sniffInterval = new Vector2(3f, 7f);

    AudioSource skitter, voice;
    ScoutAnimator anim;
    float nextSniff;
    bool dead;

    void Awake()
    {
        anim = GetComponentInChildren<ScoutAnimator>();
        skitter = Make3D(fullVolumeDistance, quietRange);
        skitter.clip = skitterLoop;
        skitter.loop = true;
        skitter.volume = 0f;
        voice = Make3D(fullVolumeDistance, quietRange);
        nextSniff = Time.time + Random.Range(sniffInterval.x, sniffInterval.y);
    }

    AudioSource Make3D(float full, float range)
    {
        var s = gameObject.AddComponent<AudioSource>();
        AudioMix.Setup3D(s, full, range);
        return s;
    }

    void Update()
    {
        if (dead || anim == null) return;
        float speed = anim.velocity.magnitude;
        float target = Mathf.Clamp01(speed / Mathf.Max(0.1f, skitterFullSpeed)) * skitterVolume;
        skitter.volume = Mathf.MoveTowards(skitter.volume, target, Time.deltaTime * 2f);
        if (skitter.clip != null && skitter.volume > 0f && !skitter.isPlaying)
        {
            skitter.time = Random.Range(0f, skitter.clip.length);   // not every scout in step
            skitter.Play();
        }
        else if (skitter.volume <= 0f && skitter.isPlaying) skitter.Stop();

        // Sniffing: only while still and not mid-call
        if (speed < 0.2f && !voice.isPlaying && Time.time > nextSniff)
        {
            PlayVoice(sniffClips, sniffVolume, quietRange);
            nextSniff = Time.time + Random.Range(sniffInterval.x, sniffInterval.y);
        }
    }

    public void Screech()
    {
        if (!dead) PlayVoice(screechClips, screechVolume, screechRange);
    }

    public void StopScreech()
    {
        if (voice.isPlaying && voice.maxDistance >= screechRange) voice.Stop();
    }

    public void Die()
    {
        if (dead) return;
        dead = true;
        skitter.Stop();
        voice.Stop();
        PlayVoice(deathClips, deathVolume, quietRange * 1.5f);
    }

    void PlayVoice(AudioClip[] clips, float volume, float range)
    {
        if (clips == null || clips.Length == 0) return;
        var clip = clips[Random.Range(0, clips.Length)];
        if (clip == null) return;
        AudioMix.Setup3D(voice, fullVolumeDistance, range);
        voice.clip = clip;
        voice.volume = volume;
        voice.pitch = Random.Range(0.95f, 1.08f);
        voice.Play();
    }
}
