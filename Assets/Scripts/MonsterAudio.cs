using UnityEngine;

// Put on the Monster next to MonsterAI. Runs on every computer.
// - 3D: breathing loop, heavy footsteps, random growls, a scream when it starts a chase
// - Just for you: chase music while it's hunting near you, and a heartbeat that speeds up as it gets close
[RequireComponent(typeof(MonsterAI))]
public class MonsterAudio : MonoBehaviour
{
    [Header("3D sounds (on the monster)")]
    public AudioClip breathingLoop;
    public AudioClip[] footstepClips;
    public AudioClip[] growlClips;
    public AudioClip chaseScream;          // plays when it spots someone

    [Header("Death")]
    public AudioClip[] attackClips;        // 3D on the monster: the roar/crunch when it gets someone
    public AudioClip[] playerDeathScreams; // 3D where the victim was: what the other players hear
    public AudioClip deathStinger;         // only the victim hears this, right in their head
    [Range(0f, 1f)] public float deathVolume = 1f;
    public float silenceAfterDeath = 3f;   // victim: heartbeat/music cut out for this long

    [Header("Your ears only")]
    public AudioClip chaseMusicLoop;
    public AudioClip heartbeatLoop;

    [Header("Distances")]
    public float hearDistance = 40f;       // how far its breathing/steps/growls carry
    public float heartbeatRange = 25f;     // heartbeat starts when it's this close
    public float chaseMusicRange = 45f;    // chase music only if the chase is near you

    [Header("Volumes")]
    [Range(0f, 1f)] public float breathingVolume = 0.6f;
    [Range(0f, 1f)] public float footstepVolume = 0.9f;
    [Range(0f, 1f)] public float growlVolume = 1f;
    [Range(0f, 1f)] public float screamVolume = 1f;
    [Range(0f, 1f)] public float chaseMusicVolume = 0.6f;
    [Range(0f, 1f)] public float heartbeatVolume = 0.8f;

    [Header("Timing")]
    public float strideLength = 1.6f;      // big creature, long strides
    public Vector2 growlInterval = new Vector2(8f, 20f);
    [Tooltip("Minimum seconds between chase screams, so it doesn't scream every time it re-spots you.")]
    public float screamCooldown = 30f;

    MonsterAI ai;
    AudioSource breath, steps, voice, music, heartbeat;
    MonsterState lastState;
    Vector3 lastPosition;
    float smoothedSpeed;
    float distanceSinceStep;
    float nextGrowlTime;
    float silencedUntil;
    float nextScreamAllowed;

    void Awake()
    {
        ai = GetComponent<MonsterAI>();

        breath = Make3D(true);
        steps = Make3D(false);
        voice = Make3D(false);
        music = Make2D();
        heartbeat = Make2D();

        lastPosition = transform.position;
        ScheduleGrowl();
    }

    void Start()
    {
        StartLoop(breath, breathingLoop, breathingVolume);
        StartLoop(music, chaseMusicLoop, 0f);
        StartLoop(heartbeat, heartbeatLoop, 0f);
    }

    void Update()
    {
        if (Time.deltaTime <= 0f) return;
        MonsterState state = ai.SyncedState;

        // How fast is it moving? (works on every computer, not just the host)
        Vector3 delta = transform.position - lastPosition;
        delta.y = 0f;
        smoothedSpeed = Mathf.Lerp(smoothedSpeed, delta.magnitude / Time.deltaTime, Time.deltaTime * 8f);
        lastPosition = transform.position;

        // Heavy footsteps
        if (smoothedSpeed > 0.3f)
        {
            distanceSinceStep += smoothedSpeed * Time.deltaTime;
            if (distanceSinceStep >= strideLength)
            {
                distanceSinceStep = 0f;
                PlayRandom(steps, footstepClips, footstepVolume, 0.85f, 1.05f);
            }
        }

        // Breathing gets faster and heavier while chasing
        bool chasing = state == MonsterState.Chase;
        breath.pitch = Mathf.MoveTowards(breath.pitch, chasing ? 1.3f : 1f, Time.deltaTime);
        breath.volume = Mathf.MoveTowards(breath.volume, breathingVolume * (chasing ? 1f : 0.7f), Time.deltaTime);

        // Scream the moment a chase starts
        if (chasing && lastState != MonsterState.Chase && chaseScream != null && Time.time >= nextScreamAllowed)
        {
            voice.PlayOneShot(chaseScream, screamVolume);
            nextScreamAllowed = Time.time + screamCooldown;
        }
        lastState = state;

        // Random growls while it's prowling
        if (!chasing && Time.time >= nextGrowlTime)
        {
            PlayRandom(voice, growlClips, growlVolume, 0.9f, 1.1f);
            ScheduleGrowl();
        }

        // Personal tension: heartbeat + chase music, based on how close it is to YOU
        float dist = Time.time < silencedUntil ? float.MaxValue : DistanceToLocalPlayer();

        float closeness = Mathf.Clamp01(1f - dist / heartbeatRange);   // 0 far, 1 on top of you
        heartbeat.volume = Mathf.MoveTowards(heartbeat.volume, heartbeatVolume * closeness, Time.deltaTime);
        heartbeat.pitch = Mathf.Lerp(0.85f, 1.6f, closeness);

        float musicTarget = (chasing && dist < chaseMusicRange) ? chaseMusicVolume : 0f;
        float fadeSpeed = musicTarget > music.volume ? 1.5f : 0.25f;   // fast in, slow out
        music.volume = Mathf.MoveTowards(music.volume, musicTarget, Time.deltaTime * fadeSpeed);
    }

    // Called on every computer when the monster catches someone
    public void PlayCatch(Vector3 victimPosition, bool iAmTheVictim)
    {
        PlayRandom(voice, attackClips, deathVolume, 0.9f, 1.05f);

        if (iAmTheVictim)
        {
            if (deathStinger != null) music.PlayOneShot(deathStinger, deathVolume);   // 2D, in your head
            heartbeat.volume = 0f;                    // sudden silence
            music.volume = 0f;
            silencedUntil = Time.time + silenceAfterDeath;
        }
        else if (playerDeathScreams != null && playerDeathScreams.Length > 0)
        {
            var clip = playerDeathScreams[Random.Range(0, playerDeathScreams.Length)];
            var go = new GameObject("DeathScream");
            go.transform.position = victimPosition + Vector3.up * 1.5f;
            var s = go.AddComponent<AudioSource>();
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 3f;
            s.maxDistance = hearDistance * 1.5f;   // screams carry
            s.dopplerLevel = 0f;
            s.PlayOneShot(clip, deathVolume);
            Destroy(go, clip.length + 0.1f);
        }
    }

    // ---------- Helpers ----------

    float DistanceToLocalPlayer()
    {
        foreach (var p in FirstPersonController.All)
            if (p.IsSpawned && p.IsOwner)
                return Vector3.Distance(transform.position, p.transform.position);
        return float.MaxValue;   // not in a game yet (lobby)
    }

    void ScheduleGrowl()
    {
        nextGrowlTime = Time.time + Random.Range(growlInterval.x, growlInterval.y);
    }

    static void PlayRandom(AudioSource src, AudioClip[] clips, float vol, float minPitch, float maxPitch)
    {
        if (clips == null || clips.Length == 0) return;
        src.pitch = Random.Range(minPitch, maxPitch);
        src.PlayOneShot(clips[Random.Range(0, clips.Length)], vol);
    }

    static void StartLoop(AudioSource src, AudioClip clip, float vol)
    {
        if (clip == null) return;
        src.clip = clip;
        src.loop = true;
        src.volume = vol;
        src.Play();
    }

    AudioSource Make3D(bool loop)
    {
        var s = gameObject.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.loop = loop;
        s.spatialBlend = 1f;
        s.rolloffMode = AudioRolloffMode.Linear;
        s.minDistance = 2f;
        s.maxDistance = hearDistance;
        s.dopplerLevel = 0f;
        return s;
    }

    AudioSource Make2D()
    {
        var s = gameObject.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.loop = true;
        s.spatialBlend = 0f;   // plays "in your head", not from a position
        s.volume = 0f;
        return s;
    }
}
