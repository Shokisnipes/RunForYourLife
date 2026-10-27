using UnityEngine;

// On the Climber root; runs on every client. Hand/foot thumps timed to the animation (so they speed up and slow
// down with it), its call now and then and when it starts a chase, and the grab sound when it catches someone.
public class ClimberAudio : MonoBehaviour
{
    [Header("Clips")]
    public AudioClip[] footstepClips;
    [Tooltip("Its call: now and then while hunting, and when it starts chasing someone.")]
    public AudioClip[] callClips;
    public AudioClip[] grabClips;
    [Tooltip("Played at the roof hatch when the climber opens it (a warning for whoever is inside).")]
    public AudioClip[] hatchClips;

    [Header("Steps per animation loop (hands + feet hitting the ground/wall)")]
    public int crawlSteps = 4;
    public int runSteps = 2;
    public int climbSteps = 4;

    [Header("Volume and range")]
    [Range(0f, 1f)] public float footstepVolume = 0.3f;
    [Range(0f, 1f)] public float climbStepVolume = 0.22f;
    [Range(0f, 1f)] public float callVolume = 0.75f;
    [Range(0f, 1f)] public float grabVolume = 0.8f;
    [Range(0f, 1f)] public float hatchVolume = 0.85f;
    [Tooltip("How far the hatch creak carries.")]
    public float hatchRange = 35f;
    [Tooltip("Landing after dropping through a roof hatch.")]
    [Range(0f, 1f)] public float landVolume = 0.6f;
    [Tooltip("How far its steps carry.")]
    public float stepRange = 30f;
    [Tooltip("How far its call carries.")]
    public float callRange = 90f;
    [Tooltip("Steps and the hatch are at full volume this close (see AudioMix).")]
    public float stepFullVolumeDistance = 1.5f;
    [Tooltip("Its call and grab are at full volume this close.")]
    public float voiceFullVolumeDistance = 4f;

    [Header("Timing")]
    [Tooltip("Seconds between calls while it's out hunting (random in this range).")]
    public Vector2 callInterval = new Vector2(12f, 30f);
    [Tooltip("Never calls more often than this, even when chases keep starting.")]
    public float callCooldown = 6f;

    AudioSource steps, voice, hatchSource;
    bool lastHatchOpen;
    ClimberAI ai;
    ClimberAnimator climberAnim;
    int lastStateHash, lastStep;
    float nextCall, lastCall = -999f;
    ClimberState lastState;

    void Awake()
    {
        ai = GetComponent<ClimberAI>();
        climberAnim = GetComponentInChildren<ClimberAnimator>();
        steps = Make3D(stepFullVolumeDistance, stepRange);
        voice = Make3D(voiceFullVolumeDistance, callRange);

        // The hatch sound plays where the hatch is, not on the climber, so it gets its own object
        var hatchGo = new GameObject("Climber Hatch Audio");   // not parented: stays at the hatch
        hatchSource = hatchGo.AddComponent<AudioSource>();
        AudioMix.Setup3D(hatchSource, stepFullVolumeDistance, hatchRange);
        nextCall = Time.time + Random.Range(callInterval.x, callInterval.y);
    }

    void OnDestroy()
    {
        if (hatchSource != null) Destroy(hatchSource.gameObject);
    }

    void Update()
    {
        if (ai == null) return;
        UpdateSteps();

        // Creak at the hatch the moment its lid starts to open
        bool hatchOpen = ai.SyncedHatchOpen;
        if (hatchOpen && !lastHatchOpen)
        {
            hatchSource.transform.position = ai.SyncedHatchPos;
            PlayRandom(hatchSource, hatchClips, hatchVolume, 0.95f, 1.05f);
        }
        lastHatchOpen = hatchOpen;

        // Shrieks as it starts a chase, and calls out now and then
        var state = ai.SyncedState;
        if (state == ClimberState.Chase && lastState != ClimberState.Chase) Call();
        // Heavy thump when it lands inside after dropping through a hatch
        bool wasFalling = lastState == ClimberState.DropIn || lastState == ClimberState.LeapDown;
        if (wasFalling && state != ClimberState.DropIn && state != ClimberState.LeapDown)
            PlayRandom(steps, footstepClips, landVolume, 0.7f, 0.8f);
        lastState = state;
        if (Time.time >= nextCall) Call();
    }

    // One step each time the animation passes a step point in its loop
    void UpdateSteps()
    {
        if (climberAnim == null || climberAnim.Anim == null) return;
        var anim = climberAnim.Anim;
        if (anim.IsInTransition(0)) return;

        int mode = climberAnim.Mode;
        var info = anim.GetCurrentAnimatorStateInfo(0);
        if (mode == 3) return;   // falling through a hatch
        int count = mode != 0 ? climbSteps : climberAnim.Running ? runSteps : crawlSteps;
        bool moving = mode == 1 || mode == 2 || (mode == 0 && climberAnim.Moving);

        int step = Mathf.FloorToInt(info.normalizedTime * Mathf.Max(1, count));
        if (info.shortNameHash != lastStateHash) { lastStateHash = info.shortNameHash; lastStep = step; return; }
        if (step == lastStep) return;
        lastStep = step;
        if (!moving) return;

        PlayRandom(steps, footstepClips, mode != 0 ? climbStepVolume : footstepVolume, 0.85f, 1.15f);
    }

    void Call()
    {
        nextCall = Time.time + Random.Range(callInterval.x, callInterval.y);
        if (Time.time - lastCall < callCooldown) return;
        lastCall = Time.time;
        PlayRandom(voice, callClips, callVolume, 0.9f, 1.1f);
    }

    // Called on every client by ClimberAI when it catches someone
    public void PlayGrab()
    {
        PlayRandom(voice, grabClips, grabVolume, 0.95f, 1.05f);
        lastCall = Time.time;   // don't call over the top of it
    }

    static void PlayRandom(AudioSource src, AudioClip[] clips, float vol, float minPitch, float maxPitch)
    {
        if (clips == null || clips.Length == 0) return;
        var clip = clips[Random.Range(0, clips.Length)];
        if (clip == null) return;
        src.pitch = Random.Range(minPitch, maxPitch);
        src.PlayOneShot(clip, vol);
    }

    AudioSource Make3D(float fullVolumeDistance, float range)
    {
        var s = gameObject.AddComponent<AudioSource>();
        AudioMix.Setup3D(s, fullVolumeDistance, range);
        return s;
    }
}
