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

    [Header("Body on the wall / in the drop")]
    [Tooltip("Height of its body above its feet point while climbing. Lower = hugs the wall lower (less floaty).")]
    public float climbBodyHeight = 1.9f;
    [Tooltip("How far its body sits from its climbing point toward the wall (+) or away from it (-).")]
    public float climbBodyDepth = 0f;
    [Tooltip("Higher = the climb clips' own bobbing is removed more. Low keeps the motion, just re-centres it.")]
    public float climbRecentre = 2f;
    [Tooltip("How fast the drop clip's forward lunge is cancelled (it would carry the body ~2 m forward).")]
    public float dropRecentre = 14f;

    [Header("Limb collision (hands and feet)")]
    [Tooltip("Hands/feet stop this far from walls and floors.")]
    public float limbRadius = 0.08f;
    [Tooltip("While climbing, hands and feet stick to the wall in front if it's within this distance.")]
    public float wallGripRange = 0.9f;
    [Tooltip("Seconds for a limb to blend onto / off a surface.")]
    public float limbBlend = 0.12f;

    Animator anim;
    ClimberAI ai;
    Vector3 lastPos;
    float flatSpeed, verticalSpeed;
    float grabUntil = -1f, grabWeight;
    int grabLayer = -1;

    Transform leftHand, rightHand, head;
    Vector3 smoothedBody, bodyOffset;
    bool bodyStarted;
    readonly float[] limbWeights = new float[4];
    readonly RaycastHit[] hits = new RaycastHit[8];
    static readonly AvatarIKGoal[] Limbs = { AvatarIKGoal.LeftHand, AvatarIKGoal.RightHand, AvatarIKGoal.LeftFoot, AvatarIKGoal.RightFoot };

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

    // ---------- Body re-centring and limb collision (needs IK Pass on the base layer) ----------

    void OnAnimatorIK(int layerIndex)
    {
        if (layerIndex != 0 || ai == null) return;
        Transform root = ai.transform;
        float dt = Time.deltaTime;

        // Where the clip puts the body, relative to the climber's real position
        Vector3 body = root.InverseTransformPoint(anim.bodyPosition);
        if (!bodyStarted) { smoothedBody = body; bodyStarted = true; }
        float follow = Mode == 3 ? dropRecentre : climbRecentre;
        smoothedBody = Vector3.Lerp(smoothedBody, body, 1f - Mathf.Exp(-follow * dt));

        // Climbing: body held at a set height and depth on the wall. Dropping: no forward lunge.
        Vector3 offset = Vector3.zero;
        if (Mode == 1 || Mode == 2) offset = new Vector3(0f, climbBodyHeight, climbBodyDepth) - smoothedBody;
        else if (Mode == 3) offset = new Vector3(-smoothedBody.x, 0f, -smoothedBody.z);
        bodyOffset = Vector3.Lerp(bodyOffset, offset, 1f - Mathf.Exp(-8f * dt));
        Vector3 delta = root.TransformVector(bodyOffset);
        anim.bodyPosition += delta;

        for (int i = 0; i < Limbs.Length; i++) Limb(i, delta, root, dt);
    }

    // One hand/foot: never through a wall or the floor, and on the wall while climbing
    void Limb(int i, Vector3 delta, Transform root, float dt)
    {
        var goal = Limbs[i];
        Vector3 p = anim.GetIKPosition(goal) + delta;   // where the clip (plus the body shift) puts it
        Vector3 want = p;
        bool touching = false;

        // Body -> limb passes through something? Stop the limb at the surface.
        Vector3 from = anim.bodyPosition;
        Vector3 toLimb = p - from;
        if (toLimb.sqrMagnitude > 0.0001f && Cast(from, toLimb.normalized, toLimb.magnitude + limbRadius, out var block))
        {
            want = block.point + block.normal * limbRadius;
            touching = true;
        }

        bool climbing = Mode == 1 || Mode == 2;
        if (climbing)
        {
            // Press onto the wall it's facing: no floating off it, no sinking into it
            Vector3 f = root.forward;
            if (Cast(want - f * 0.6f, f, 0.6f + wallGripRange, out var wall) && Vector3.Dot(wall.normal, -f) > 0.5f)
            {
                want = wall.point + wall.normal * limbRadius;
                touching = true;
            }
        }
        else if (Cast(want + Vector3.up * 0.5f, Vector3.down, 1f, out var floor) && want.y < floor.point.y + limbRadius)
        {
            want.y = floor.point.y + limbRadius;   // not under the floor
            touching = true;
        }

        float rate = 1f / Mathf.Max(0.01f, limbBlend);
        limbWeights[i] = Mathf.MoveTowards(limbWeights[i], touching ? 1f : 0f, rate * dt);
        anim.SetIKPositionWeight(goal, limbWeights[i]);
        anim.SetIKPosition(goal, touching ? want : p);
    }

    // Raycast that ignores players (and the climber, which has no colliders)
    bool Cast(Vector3 from, Vector3 dir, float distance, out RaycastHit best)
    {
        best = default;
        int count = Physics.RaycastNonAlloc(from, dir, hits, distance, ~0, QueryTriggerInteraction.Ignore);
        float bestDist = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            if (hits[i].distance >= bestDist) continue;
            if (hits[i].collider.GetComponentInParent<FirstPersonController>() != null) continue;
            best = hits[i];
            bestDist = hits[i].distance;
        }
        return bestDist < float.MaxValue;
    }
}
