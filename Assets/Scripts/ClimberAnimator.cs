using UnityEngine;

// On the climber's Model child; runs on every client. Picks crawl / run / climb from ClimberAI's synced state and
// speeds the clips up or down to match how fast the climber really moves (the Mixamo clips are "In Place").
[RequireComponent(typeof(Animator))]
public class ClimberAnimator : MonoBehaviour
{
    [Tooltip("Metres per second the crawl clip looks right at normal speed.")]
    public float crawlClipSpeed = 2.5f;
    [Tooltip("Metres per second the run clip looks right at normal speed.")]
    public float runClipSpeed = 5f;
    [Tooltip("Metres per second up/down the wall the climb clips look right at.")]
    public float climbClipSpeed = 1.2f;
    [Tooltip("Clip playback speed never goes outside this range.")]
    public Vector2 playbackRange = new Vector2(0.4f, 2.2f);
    [Tooltip("Higher = reacts to speed changes faster.")]
    public float smoothing = 8f;

    Animator anim;
    ClimberAI ai;
    Vector3 lastPos;
    float flatSpeed, verticalSpeed;

    static readonly int SpeedId = Animator.StringToHash("Speed");
    static readonly int ModeId = Animator.StringToHash("Mode");
    static readonly int RunningId = Animator.StringToHash("Running");
    static readonly int PlaybackId = Animator.StringToHash("Playback");

    void Awake()
    {
        anim = GetComponent<Animator>();
        anim.applyRootMotion = false;   // ClimberAI moves the body
        ai = GetComponentInParent<ClimberAI>();
        lastPos = ai != null ? ai.transform.position : transform.position;
    }

    void Update()
    {
        if (ai == null || Time.deltaTime <= 0f) return;

        // Speed from how far it actually moved (works the same on host and clients)
        Vector3 pos = ai.transform.position;
        Vector3 v = (pos - lastPos) / Time.deltaTime;
        lastPos = pos;
        float k = 1f - Mathf.Exp(-smoothing * Time.deltaTime);
        flatSpeed = Mathf.Lerp(flatSpeed, new Vector2(v.x, v.z).magnitude, k);
        verticalSpeed = Mathf.Lerp(verticalSpeed, Mathf.Abs(v.y), k);

        var state = ai.SyncedState;
        int mode = state == ClimberState.ClimbUp ? 1 : state == ClimberState.ClimbDown ? 2 : 0;
        bool running = mode == 0 && ai.SyncedRunning;

        float playback = 1f;
        if (mode != 0) playback = verticalSpeed / climbClipSpeed;
        else if (flatSpeed > 0.3f) playback = flatSpeed / (running ? runClipSpeed : crawlClipSpeed);
        if (mode != 0 && verticalSpeed < 0.2f) playback = 1f;   // pulling itself over the edge

        anim.SetInteger(ModeId, mode);
        anim.SetBool(RunningId, running);
        anim.SetFloat(SpeedId, flatSpeed);
        anim.SetFloat(PlaybackId, Mathf.Clamp(playback, playbackRange.x, playbackRange.y));
    }
}
