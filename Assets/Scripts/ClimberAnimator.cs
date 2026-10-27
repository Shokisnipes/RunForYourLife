using UnityEngine;

// On the climber's Model child; runs on every client. Picks crawl / run / climb from ClimberAI's synced state and
// speeds the clips up or down to match how fast the climber really moves (the Mixamo clips are "In Place").
[RequireComponent(typeof(Animator))]
public class ClimberAnimator : MonoBehaviour, IMonsterGrabber
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
    [Tooltip("Seconds for the grab pose to blend in over the crawl.")]
    public float grabBlendIn = 0.2f;
    [Tooltip("Seconds for the grab pose to blend back out.")]
    public float grabBlendOut = 0.4f;

    Animator anim;
    ClimberAI ai;
    Vector3 lastPos;
    float flatSpeed, verticalSpeed;
    float grabUntil = -1f, grabWeight;
    int grabLayer = -1;

    Transform leftHand, rightHand, head;

    // Read by the player it caught: the hand that's furthest forward holds you, and you face its head
    public Vector3 HandPosition
    {
        get
        {
            Transform root = ai != null ? ai.transform : transform;
            if (leftHand == null || rightHand == null) return root.position + root.forward * 0.8f + Vector3.up * 1.2f;
            float l = Vector3.Dot(leftHand.position - root.position, root.forward);
            float r = Vector3.Dot(rightHand.position - root.position, root.forward);
            return (l > r ? leftHand : rightHand).position;
        }
    }
    public Vector3 HeadPosition => head != null ? head.position : (ai != null ? ai.transform.position : transform.position) + Vector3.up * 1.5f;

    // Read by ClimberAudio to time the steps
    public Animator Anim => anim;
    public int Mode { get; private set; }        // 0 ground, 1 climbing up, 2 climbing down, 3 dropping through a hatch
    public bool Running { get; private set; }
    public bool Moving => flatSpeed > 0.3f;

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
        grabLayer = anim.GetLayerIndex("Grab");
        if (anim.isHuman)
        {
            leftHand = anim.GetBoneTransform(HumanBodyBones.LeftHand);
            rightHand = anim.GetBoneTransform(HumanBodyBones.RightHand);
            head = anim.GetBoneTransform(HumanBodyBones.Head);
        }
    }

    // Called on every client by ClimberAI: hold the grab pose for this long, then let it fade out
    public void PlayGrab(float holdSeconds) => grabUntil = Mathf.Max(grabUntil, Time.time + holdSeconds);

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
        int mode = state == ClimberState.ClimbUp || state == ClimberState.ClimbOut ? 1
                 : state == ClimberState.ClimbDown ? 2
                 : state == ClimberState.DropIn ? 3 : 0;
        bool running = mode == 0 && ai.SyncedRunning;

        float playback = 1f;
        if (mode == 1 || mode == 2) playback = verticalSpeed / climbClipSpeed;
        else if (flatSpeed > 0.3f) playback = flatSpeed / (running ? runClipSpeed : crawlClipSpeed);
        if ((mode == 1 || mode == 2) && verticalSpeed < 0.2f) playback = 1f;   // pulling itself over the edge

        Mode = mode;
        Running = running;
        anim.SetInteger(ModeId, mode);
        anim.SetBool(RunningId, running);
        anim.SetFloat(SpeedId, flatSpeed);
        anim.SetFloat(PlaybackId, Mathf.Clamp(playback, playbackRange.x, playbackRange.y));

        // Grab pose fades in over the crawl, never while it's on a wall
        if (grabLayer >= 0)
        {
            bool grabbing = Time.time < grabUntil && mode == 0;
            float rate = 1f / Mathf.Max(0.01f, grabbing ? grabBlendIn : grabBlendOut);
            grabWeight = Mathf.MoveTowards(grabWeight, grabbing ? 1f : 0f, rate * Time.deltaTime);
            anim.SetLayerWeight(grabLayer, Mathf.SmoothStep(0f, 1f, grabWeight));
        }
    }
}
