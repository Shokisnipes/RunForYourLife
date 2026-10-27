using UnityEngine;

// Put on the monster's MODEL (the child object with the Animator), under the Monster root.
// Runs on every computer:
// - Blends idle/walk/run smoothly from how fast the giant is actually moving
// - Matches animation playback speed to movement so the feet don't slide
// - Detects real foot contacts and fires MonsterAudio.Footstep() (sound + camera shake) on each plant
// - Head slowly turns to track you, arm reaches for you when it's peeking into a building
[RequireComponent(typeof(Animator))]
[DefaultExecutionOrder(100)]   // after PoseAdjuster, so the doorway arm solve is the last word on the pose
public class MonsterAnimator : MonoBehaviour, IMonsterGrabber
{
    [Header("Speed mapping (monster speed in m/s)")]
    public float walkSpeed = 2.5f;          // the walk clip plays fully at this speed
    public float runSpeed = 9f;             // fully running at this speed
    public float speedDamping = 0.35f;      // seconds to blend down when slowing (higher = smoother)
    public float accelerationDamping = 0.06f; // seconds to blend up when speeding up (low = legs react to the lunge instantly)

    [Header("Stride matching (stops the feet sliding)")]
    [Tooltip("Measures the clip's real speed from the planted foot. Turn off to use the clip speeds below instead.")]
    public bool autoStrideMatch = true;
    [Tooltip("How fast the walk clip moves a normal-sized human (m/s). Raise it if the feet slide forward, lower it if they slide back.")]
    public float walkClipSpeed = 1.3f;
    [Tooltip("Same, for the run clip.")]
    public float runClipSpeed = 3.6f;
    [Tooltip("Extra leg speed while running. 1 = perfectly matched (no sliding). Higher = legs pump faster, more frantic.")]
    [Range(1f, 2.5f)] public float runCadence = 1.3f;
    public Vector2 playbackLimits = new Vector2(0.3f, 2.5f);
    [Tooltip("Playback speed for Grab and Peek (not tied to movement).")]
    public float actionPlaybackSpeed = 1.1f;

    [Header("Head tracking")]
    [Range(0f, 1f)] public float lookWeight = 1f;
    [Range(0f, 1f)] public float lookBodyWeight = 0.3f;
    [Range(0f, 1f)] public float lookHeadWeight = 1f;
    [Range(0f, 1f)] public float lookClamp = 0.5f;
    public float lookSmoothing = 2.5f;      // lower = slower, creepier head turns

    [Header("Reach (arm goes in after you at a doorway)")]
    public AvatarIKGoal reachHand = AvatarIKGoal.RightHand;
    public float reachSpeed = 2.5f;         // how fast the arm blends into reaching
    public float handSmoothing = 12f;       // smooths the synced hand position
    [Tooltip("How far the head stays outside the doorway while reaching (normal human meters, scaled).")]
    public float headClearance = 0.1f;

    [Header("Doorway posture (kneels, bows and threads its arm through the door)")]
    [Tooltip("At a doorway it drops to its knees and bows so its arm can fit through the opening. " +
             "The arm always passes through the door, never the wall; the hand only goes as deep as the pose allows.")]
    public bool doorwayPosture = true;
    [Tooltip("Hip height when kneeling (normal human meters).")]
    public float kneelHipHeight = 0.5f;
    [Tooltip("How far behind the hips the feet go when kneeling (normal human meters).")]
    public float kneelFootBack = 0.35f;
    [Tooltip("How far in front of the hips the knees go when kneeling (normal human meters).")]
    public float kneelKneeForward = 0.25f;
    [Tooltip("Most it bows its upper body forward (degrees).")]
    public float maxBow = 75f;
    [Tooltip("Most it shifts its whole body in toward the door (normal human meters).")]
    public float maxLeanIn = 0.45f;
    [Tooltip("How fast it gets into / out of the doorway posture (0-1 per second).")]
    public float postureSpeed = 1.2f;
    [Tooltip("Thickness of its arm (normal human meters). The arm keeps this far from the door frame.")]
    public float armRadius = 0.055f;
    [Tooltip("Half the thickness of building walls, in meters.")]
    public float wallHalfThickness = 0.15f;
    [Tooltip("Puts its other hand flat on the ground while it kneels at a door.")]
    public bool plantFreeHand = true;

    [Header("Pick-up (after a catch)")]
    public float pullTime = 0.9f;           // caught inside: seconds to drag you to the doorway
    public float throughDoorTime = 0.35f;   // seconds to pull you out through the frame
    public float liftTime = 1.2f;           // seconds to raise you from the ground to its face
    public float carryDuration = 3f;        // how long it holds you up
    [Tooltip("Where it holds you, in front of its face (normal human meters, scaled automatically).")]
    public Vector3 holdOffset = new Vector3(0.05f, -0.05f, 0.3f);
    [Tooltip("How fast the hand closes around you on a catch and opens when it lets go (full close per second).")]
    public float gripCloseSpeed = 4f;
    [Tooltip("How far each finger joint bends toward the palm when gripping (degrees: knuckle, middle, tip). " +
             "A full fist is ~90; less wraps around a body instead of crushing through it.")]
    public Vector3 gripFingerCurl = new Vector3(55f, 65f, 45f);
    [Tooltip("Same for the thumb, which folds across the front of the grip.")]
    public Vector3 gripThumbCurl = new Vector3(20f, 30f, 25f);
    [Tooltip("Where the held body sits along the hand: 0 = wrist, 1 = knuckles.")]
    [Range(0f, 1.5f)] public float gripAlongPalm = 0.8f;
    [Tooltip("How far out from the palm the held body sits (normal human meters, scaled automatically).")]
    public float gripPalmDepth = 0.04f;

    [Header("Foot plants (sound + shake land on contact)")]
    [Tooltip("Steps are detected by learning each foot's lowest/highest point. A foot counts as planted in the bottom part of its swing (0-1).")]
    [Range(0.05f, 0.5f)] public float plantPoint = 0.25f;
    [Tooltip("...and as lifted once it rises past this part of its swing (0-1). Keep it above Plant Point.")]
    [Range(0.2f, 0.9f)] public float liftPoint = 0.55f;
    [Tooltip("Ignore feet that move less than this (normal human meters), so idle swaying isn't a step.")]
    public float minimumSwing = 0.008f;
    // Used by the stride measurement to decide if a foot is on the ground
    float plantMargin = 0.03f;
    public bool showFootDebug = false;
    [Tooltip("Prints a line in the Console every time a foot plants.")]
    public bool logFootsteps = false;

    [Header("Foot grounding (IK keeps planted feet on the ground)")]
    public bool groundFeet = true;
    [Tooltip("Measure the ankle height from the model's standing pose at startup (recommended).")]
    public bool autoAnkleHeight = true;
    [Tooltip("Ankle height above the sole, in normal human meters. Set automatically if Auto Ankle Height is on.")]
    public float ankleHeight = 0.09f;
    [Tooltip("Most the hips may drop to plant a foot (normal human meters).")]
    public float maxHipDrop = 0.1f;
    [Tooltip("Feet closer to the ground than this (normal human meters) get pinned to it.")]
    public float groundingZone = 0.2f;
    [Tooltip("How fast the hips move down/up to let both feet reach the ground.")]
    public float hipAdjustSpeed = 6f;
    [Tooltip("Pin each planted foot to the exact spot it landed on until the animation lifts it (no sliding).")]
    public bool lockPlantedFeet = true;
    [Tooltip("A locked foot lets go if the body moves this far past it (normal human meters) - keeps the leg from overstretching.")]
    public float maxLockStretch = 0.18f;
    [Tooltip("Seconds a foot takes to blend back to the animation when it lets go.")]
    public float lockReleaseTime = 0.12f;
    public LayerMask groundLayers = ~0;

    [Header("Real floor (the NavMesh floats a bit above the actual ground)")]
    [Tooltip("Keeps the model standing on the real floor instead of on the NavMesh.")]
    public bool snapToRealFloor = true;
    public float floorSnapSpeed = 8f;
    [Tooltip("Never let knees, shins, ankles or toes sink below the floor (lifts the body just enough). Works in every pose, including kneeling.")]
    public bool keepLegsAboveFloor = true;
    [Tooltip("Thickness of the knee/shin, in normal human meters (how far the joint sits above the floor when touching it).")]
    public float kneeThickness = 0.06f;
    [Tooltip("How far the toe joint sits above the floor when the toes touch it (normal human meters).")]
    public float toeThickness = 0.015f;
    public float liftSpeed = 10f;

    [Header("Debug (read only, watch these in Play mode)")]
    [Tooltip("How far the NavMesh is above (+) or below (-) the real floor under the giant, in meters.")]
    public float debugNavMeshGap;
    public float debugMoveSpeed;
    public float debugAnimSpeed;
    public float debugMeasuredClipSpeed;
    public float debugFallbackClipSpeed;
    [Tooltip("Doorway posture: how far its bow / lean-in / reach currently are.")]
    public float debugBow;
    public float debugLeanIn;
    [Tooltip("How far (meters) the hand is from where the AI wants it, because of the doorway. 0 = got there.")]
    public float debugReachShortfall;
    [Tooltip("How far the leg lift is currently raising the body (meters).")]
    public float debugLegLift;
    [Tooltip("Foot locking: is each foot pinned to the ground right now?")]
    public bool debugLeftFootLocked, debugRightFootLocked;
    [HideInInspector] public int debugLockReleasedByLift, debugLockReleasedByStretch, debugLockCount;
    [Tooltip("0 = not reaching, 1 = hand outside, 2 = arm threaded through the door, 3 = can't get through (hand rests at the threshold)")]
    public int debugArmMode;

    [Header("Uncanny proportions")]
    public float armScale = 1.25f;
    public float headScale = 1.1f;

    static readonly int SpeedId = Animator.StringToHash("Speed");
    static readonly int PeekingId = Animator.StringToHash("Peeking");
    static readonly int GrabId = Animator.StringToHash("Grab");

    Animator animator;
    MonsterAI ai;
    MonsterAudio monsterAudio;
    Transform root;
    Transform leftFoot, rightFoot;
    bool leftPlanted = true, rightPlanted = true;

    // Learned swing range per foot (normal human meters above standing height)
    class FootTrack { public float lo = 0f, hi = 0.05f; }
    readonly FootTrack leftTrack = new FootTrack(), rightTrack = new FootTrack();

    // Foot locking (worked out from the animated feet, before IK, so a pinned foot can still tell when it lifts)
    class FootLock
    {
        public bool locked, canLock = true, hasPrev;
        public Vector3 pos, prevAnimated;
        public float weight, groundSpeed;
        public float lo = 0f, hi = 0.05f;
    }
    readonly FootLock leftLock = new FootLock(), rightLock = new FootLock();
    float modelScale = 1f;

    Vector3 lastRootPos;
    float smoothedSpeed;
    Vector3 lookPos;
    float currentLookWeight;
    float currentReachWeight;
    float hipOffset;
    float baseLocalY;
    float floorOffset;
    float liftOffset;
    float floorY;
    bool hasFloor;
    float smoothedBaseY;
    bool hasBaseHeight;
    Transform leftKnee, rightKnee, leftToes, rightToes;

    // Auto stride
    float measuredNaturalSpeed = -1f;       // m/s the current animation "wants" to move at playback 1
    Vector3 leftLastLocal, rightLastLocal;
    bool hasLastFeet;
    float lastYaw;
    float fallbackNatural;

    // Carry
    bool carrying;
    float carryStart;
    Vector3 carryFrom;
    Vector3 carryTarget;
    float carryWeight;
    bool carryThroughDoor;
    Vector3 carryDoor;
    Transform head, leftHandBone, rightHandBone;
    Transform shoulder, elbow;
    float armLength;
    Vector3 smoothedHand;

    // Doorway posture
    Transform spine, chest, neck, freeShoulder, freeElbow, freeHand, hipsBone;
    Vector3 hipsFromBody;   // hips bone relative to the IK body position (the body position is the center of mass)
    Vector3 lastBodyPosition;
    Vector3 headLocalForward;
    float upperArmLength, foreArmLength;
    float postureWeight, armWeight;
    float bow, bowTarget, leanIn, leanInTarget;
    Vector3 openingFor = new Vector3(float.NaN, 0f, 0f);
    Vector3 openingCenter;
    float openingHalfWidth, openingTop, openingBottom, roofTop, roomCeiling;

    // Read by the player being carried
    public Vector3 HandPosition
    {
        get
        {
            var hand = reachHand == AvatarIKGoal.LeftHand ? leftHandBone : rightHandBone;
            return hand != null ? hand.position : root.position + Vector3.up * 8f;
        }
    }
    public Vector3 HeadPosition => head != null ? head.position : root.position + Vector3.up * 13f;

    // Where the held body sits: in the middle of the palm, not at the wrist (on a giant that's nearly a metre apart)
    public Vector3 GripPosition
    {
        get
        {
            if (!PalmFrame(out Transform hand, out Transform middle, out Vector3 palm)) return HandPosition;
            return Vector3.Lerp(hand.position, middle.position, gripAlongPalm) + palm * gripPalmDepth * modelScale;
        }
    }

    // How far the reaching shoulder sits to the side of the body center (meters, + = right).
    // The giant sidesteps by this much at a doorway so the arm goes straight through the opening.
    public float ReachShoulderSide { get; private set; }
    readonly RaycastHit[] rayHits = new RaycastHit[8];

    void Awake()
    {
        animator = GetComponent<Animator>();
        animator.applyRootMotion = false;   // the NavMeshAgent moves the giant, not the animation
        ai = GetComponentInParent<MonsterAI>();
        monsterAudio = GetComponentInParent<MonsterAudio>();
        root = ai != null ? ai.transform : transform;
    }

    void OnEnable()
    {
        if (monsterAudio != null) monsterAudio.AnimatedFeet = true;
    }

    void OnDisable()
    {
        if (monsterAudio != null) monsterAudio.AnimatedFeet = false;
    }

    void Start()
    {
        if (monsterAudio == null)
            Debug.LogWarning("MonsterAnimator: no MonsterAudio found on the Monster root - footsteps will be silent. Add Monster Audio to the Monster object.");
        else if (monsterAudio.footstepClips == null || monsterAudio.footstepClips.Length == 0)
            Debug.LogWarning("MonsterAnimator: Monster Audio has no Footstep Clips - steps will shake the camera but make no sound.");

        modelScale = transform.lossyScale.y;
        baseLocalY = transform.localPosition.y;
        lastRootPos = root.position;
        lookPos = root.position + root.forward * 10f + Vector3.up * 10f;

        if (!animator.isHuman)
        {
            Debug.LogWarning("MonsterAnimator: the model's Rig must be set to Humanoid for feet, head tracking and reach.");
            return;
        }

        leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
        leftKnee = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);    // the knee joint
        rightKnee = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
        leftToes = animator.GetBoneTransform(HumanBodyBones.LeftToes);       // may be null on some rigs
        rightToes = animator.GetBoneTransform(HumanBodyBones.RightToes);
        head = animator.GetBoneTransform(HumanBodyBones.Head);
        leftHandBone = animator.GetBoneTransform(HumanBodyBones.LeftHand);
        rightHandBone = animator.GetBoneTransform(HumanBodyBones.RightHand);

        // The model starts in its standing T-pose: the feet's height above the root IS the ankle height
        if (autoAnkleHeight && leftFoot != null && rightFoot != null)
        {
            float measured = Mathf.Min(leftFoot.position.y, rightFoot.position.y) - transform.position.y;
            measured /= modelScale;
            if (measured > 0.02f && measured < 0.3f) ankleHeight = measured;
            else Debug.LogWarning($"MonsterAnimator: couldn't measure ankle height (got {measured:0.000}), using {ankleHeight}");
        }

        ScaleBone(HumanBodyBones.LeftUpperArm, armScale);
        ScaleBone(HumanBodyBones.RightUpperArm, armScale);
        ScaleBone(HumanBodyBones.Head, headScale);

        // Measure the reaching arm (after scaling) so it can lean in when the hand needs more reach
        bool left = reachHand == AvatarIKGoal.LeftHand;
        shoulder = animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
        elbow = animator.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
        var hand = left ? leftHandBone : rightHandBone;
        if (shoulder != null && elbow != null && hand != null)
        {
            upperArmLength = Vector3.Distance(shoulder.position, elbow.position);
            foreArmLength = Vector3.Distance(elbow.position, hand.position);
            armLength = upperArmLength + foreArmLength;
        }
        if (shoulder != null)
            ReachShoulderSide = root.InverseTransformPoint(shoulder.position).x;

        hipsBone = animator.GetBoneTransform(HumanBodyBones.Hips);
        spine = animator.GetBoneTransform(HumanBodyBones.Spine);
        chest = animator.GetBoneTransform(HumanBodyBones.Chest);
        neck = animator.GetBoneTransform(HumanBodyBones.Neck);
        freeShoulder = animator.GetBoneTransform(left ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm);
        freeElbow = animator.GetBoneTransform(left ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm);
        freeHand = left ? rightHandBone : leftHandBone;
        // Still in the T-pose here, facing the root's forward: remember which way the face points in head space
        if (head != null) headLocalForward = Quaternion.Inverse(head.rotation) * root.forward;
    }

    void ScaleBone(HumanBodyBones bone, float scale)
    {
        var t = animator.GetBoneTransform(bone);
        if (t != null && scale > 0f) t.localScale = Vector3.one * scale;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        SnapToRealFloor(dt);

        // How fast is the giant really moving? (works on every computer)
        Vector3 delta = root.position - lastRootPos;
        delta.y = 0f;
        lastRootPos = root.position;
        float rawSpeed = delta.magnitude / dt;
        float speedRate = rawSpeed > smoothedSpeed ? 20f : 6f;   // react fast to a lunge, settle slowly
        smoothedSpeed = Mathf.Lerp(smoothedSpeed, rawSpeed, 1f - Mathf.Exp(-speedRate * dt));

        // Speed parameter: 0 = idle, 0.5 = walk, 1 = run
        float blend;
        if (smoothedSpeed < 0.15f) blend = 0f;
        else if (smoothedSpeed <= walkSpeed) blend = 0.5f * smoothedSpeed / walkSpeed;
        else blend = 0.5f + 0.5f * Mathf.InverseLerp(walkSpeed, runSpeed, smoothedSpeed);
        float current = animator.GetFloat(SpeedId);
        animator.SetFloat(SpeedId, blend, blend > current ? accelerationDamping : speedDamping, dt);

        // Play the animation exactly as fast as the body moves, so feet stay planted
        float b = animator.GetFloat(SpeedId);
        float natural = b <= 0.5f
            ? walkClipSpeed * (b / 0.5f)
            : Mathf.Lerp(walkClipSpeed, runClipSpeed, (b - 0.5f) / 0.5f);
        natural *= modelScale;
        fallbackNatural = natural;
        if (autoStrideMatch && measuredNaturalSpeed > 0f && b > 0.1f) natural = measuredNaturalSpeed;
        float cadence = Mathf.Lerp(1f, runCadence, Mathf.InverseLerp(0.5f, 1f, b));   // only boosts the run
        float playback = natural > 0.1f ? Mathf.Clamp(smoothedSpeed / natural * cadence, playbackLimits.x, playbackLimits.y) : 1f;

        // Grab / Peek play at their own speed, not the walking speed
        if (InAction()) playback = actionPlaybackSpeed;
        animator.speed = Mathf.Lerp(animator.speed, playback, 1f - Mathf.Exp(-10f * dt));

        debugMoveSpeed = smoothedSpeed;
        debugAnimSpeed = animator.speed;
        debugMeasuredClipSpeed = measuredNaturalSpeed;
        debugFallbackClipSpeed = fallbackNatural;

        if (ai == null) return;

        animator.SetBool(PeekingId, ai.SyncedPeeking);

        // Head turns slowly toward what it's looking at
        bool hasLook = ai.SyncedHasLookTarget;
        currentLookWeight = Mathf.MoveTowards(currentLookWeight, hasLook ? lookWeight : 0f, dt * 1.2f);
        if (hasLook) lookPos = Vector3.Lerp(lookPos, ai.SyncedLookTarget, 1f - Mathf.Exp(-lookSmoothing * dt));

        // Arm blends in while reaching through a doorway; hand follows the host's hand position smoothly
        currentReachWeight = Mathf.MoveTowards(currentReachWeight, ai.SyncedReaching ? 1f : 0f, dt * reachSpeed);
        if (ai.SyncedReaching)
        {
            if (smoothedHand == Vector3.zero) smoothedHand = HandPosition;
            smoothedHand = Vector3.Lerp(smoothedHand, ai.SyncedHandTarget, 1f - Mathf.Exp(-handSmoothing * dt));
        }
        else smoothedHand = HandPosition;

        UpdateCarry(dt);
    }

    // ---------- Pick-up ----------

    // Extra time a doorway catch takes (drag to the door + out through it)
    public float DoorPullExtraTime => pullTime + throughDoorTime;

    // Called on every computer when it catches someone
    public void StartCarry(Vector3 victimPosition, bool throughDoor, Vector3 doorPoint)
    {
        carrying = true;
        carryStart = Time.time;
        carryThroughDoor = throughDoor;
        carryDoor = doorPoint;

        // If the arm was already reaching, start the carry from where the hand is (no snapping)
        bool handWasIn = currentReachWeight > 0.5f;
        carryFrom = handWasIn ? HandPosition : victimPosition + Vector3.up * 1f;   // where the hand really is
        carryTarget = carryFrom;
        if (handWasIn) carryWeight = currentReachWeight;
    }

    void UpdateCarry(float dt)
    {
        float extra = carryThroughDoor ? DoorPullExtraTime : 0f;
        if (carrying && Time.time - carryStart > carryDuration + extra) carrying = false;

        carryWeight = Mathf.MoveTowards(carryWeight, carrying ? 1f : 0f, dt * 3f);
        if (!carrying && carryWeight <= 0f) return;

        Vector3 hold = HeadPosition
            + root.right * holdOffset.x * modelScale
            + Vector3.up * holdOffset.y * modelScale
            + root.forward * holdOffset.z * modelScale;

        float e = Time.time - carryStart;

        if (carryThroughDoor)
        {
            // Inward = from the giant toward the door
            Vector3 inward = carryDoor - root.position;
            inward.y = 0f;
            inward = inward.sqrMagnitude > 0.01f ? inward.normalized : root.forward;
            Vector3 outside = carryDoor - inward * 2.5f + Vector3.up * 0.5f;

            if (e < pullTime)
            {
                // Dragged across the floor to the door: slow start, then a yank
                float t = e / pullTime;
                carryTarget = Vector3.Lerp(carryFrom, carryDoor, t * t);
            }
            else if (e < pullTime + throughDoorTime)
            {
                // Out through the frame
                carryTarget = Vector3.Lerp(carryDoor, outside, (e - pullTime) / throughDoorTime);
            }
            else
            {
                // Up to its face
                float t = Mathf.SmoothStep(0f, 1f, (e - pullTime - throughDoorTime) / liftTime);
                carryTarget = Vector3.Lerp(outside, hold, t);
            }
        }
        else
        {
            // Hand rises smoothly from where you were to in front of its face
            float t = Mathf.SmoothStep(0f, 1f, e / liftTime);
            carryTarget = Vector3.Lerp(carryFrom, hold, t);
        }

        // It watches what it's holding
        if (carrying)
        {
            lookPos = Vector3.Lerp(lookPos, carryTarget, 1f - Mathf.Exp(-6f * dt));
            currentLookWeight = Mathf.MoveTowards(currentLookWeight, lookWeight, dt * 3f);
        }
    }

    // The agent stands the giant on the NavMesh, which sits a little off the real floor.
    // Find the real floor under it and shift the model to stand on that.
    void SnapToRealFloor(float dt)
    {
        if (!GroundBelow(root.position + Vector3.up * 3f, 8f, out RaycastHit hit)) return;

        floorY = hit.point.y;
        hasFloor = true;
        float gap = root.position.y - hit.point.y;   // + = NavMesh above the floor
        debugNavMeshGap = gap;

        // Ease the body's height toward the floor in world space, so a curb or a NavMesh step under the
        // root becomes a smooth rise instead of a one-frame hop
        float target = snapToRealFloor ? hit.point.y : root.position.y;
        if (!hasBaseHeight) { smoothedBaseY = target; hasBaseHeight = true; }
        smoothedBaseY = Mathf.Lerp(smoothedBaseY, target, 1f - Mathf.Exp(-floorSnapSpeed * dt));
        floorOffset = Mathf.Clamp(smoothedBaseY - root.position.y, -2f, 2f);

        var lp = transform.localPosition;
        lp.y = baseLocalY + floorOffset + liftOffset;
        transform.localPosition = lp;
    }

    // Lift the body just enough that no leg joint sinks below the floor, and ease back down
    // once there's room again (with a small dead zone so it doesn't bob).
    void UpdateLegLift()
    {
        if (!keepLegsAboveFloor || !hasFloor) { liftOffset = Mathf.MoveTowards(liftOffset, 0f, Time.deltaTime); return; }

        float s = modelScale;
        float clearance = float.MaxValue;   // smallest gap between a joint and the floor (negative = sinking)
        clearance = Mathf.Min(clearance, Clearance(leftKnee, kneeThickness * s));
        clearance = Mathf.Min(clearance, Clearance(rightKnee, kneeThickness * s));
        // Walking/running: foot IK keeps the feet on the floor and the toe clamp tips the feet, so a toe
        // dipping at push-off doesn't lift the whole giant. Kneeling, grabbing, peeking: every joint counts.
        if (postureWeight > 0f || InAction())
        {
            clearance = Mathf.Min(clearance, Clearance(leftFoot, ankleHeight * s * 0.9f));
            clearance = Mathf.Min(clearance, Clearance(rightFoot, ankleHeight * s * 0.9f));
            clearance = Mathf.Min(clearance, Clearance(leftToes, 0.015f * s));
            clearance = Mathf.Min(clearance, Clearance(rightToes, 0.015f * s));
        }
        if (clearance == float.MaxValue) return;

        float deadZone = 0.02f * s;
        float target = liftOffset;
        if (clearance < 0f) target = liftOffset - clearance;                          // sinking: rise by the overlap
        else if (clearance > deadZone) target = Mathf.Max(0f, liftOffset - (clearance - deadZone));   // room to spare: come back down

        target = Mathf.Clamp(target, 0f, 2f * s);
        float rate = target > liftOffset ? liftSpeed : liftSpeed * 0.3f;   // rise fast, settle slowly
        liftOffset = Mathf.Lerp(liftOffset, target, 1f - Mathf.Exp(-rate * Time.deltaTime));
        debugLegLift = liftOffset;
    }

    float Clearance(Transform bone, float thickness)
    {
        if (bone == null) return float.MaxValue;
        return bone.position.y - (floorY + thickness);
    }

    bool InAction()
    {
        if (!animator.GetCurrentAnimatorStateInfo(0).IsName("Locomotion")) return true;
        return animator.IsInTransition(0) && !animator.GetNextAnimatorStateInfo(0).IsName("Locomotion");
    }

    void LateUpdate()
    {
        KneelFeet();       // before the leg lift measures anything
        ClampToes();
        UpdateLegLift();

        // Bones are posed by now: check for foot contacts
        CheckFoot(leftFoot, ref leftPlanted, leftTrack);
        CheckFoot(rightFoot, ref rightPlanted, rightTrack);
        MeasureStride();

        UpdateDoorwayPosture();
        CloseHand();
    }

    // ---------- Grip: the holding hand closes around its victim (after the arm is placed) ----------

    float gripWeight;
    static readonly HumanBodyBones[] LeftFingers =
    {
        HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal,
        HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal,
        HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal,
        HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal,
        HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal,
    };
    static readonly HumanBodyBones[] RightFingers =
    {
        HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal,
        HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal,
        HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal,
        HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal,
        HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal,
    };

    // The holding hand's wrist, middle knuckle and which way its palm faces, from the hand's own shape:
    // fingers point out of the hand, index to little spans it, the palm is perpendicular to both
    // (mirrored between left and right hands). The knuckles don't move when the fingers curl.
    bool PalmFrame(out Transform hand, out Transform middle, out Vector3 palm)
    {
        bool left = reachHand == AvatarIKGoal.LeftHand;
        var ids = left ? LeftFingers : RightFingers;
        hand = HandBone;
        middle = animator != null && animator.isHuman ? animator.GetBoneTransform(ids[3]) : null;
        Transform index = middle != null ? animator.GetBoneTransform(ids[0]) : null;
        Transform little = middle != null ? animator.GetBoneTransform(ids[9]) : null;
        palm = Vector3.zero;
        if (hand == null || middle == null || index == null || little == null) return false;
        Vector3 across = index.position - little.position;
        palm = Vector3.Cross(across, middle.position - hand.position).normalized * (left ? 1f : -1f);
        return true;
    }

    void CloseHand()
    {
        gripWeight = Mathf.MoveTowards(gripWeight, carrying ? 1f : 0f, Time.deltaTime * gripCloseSpeed);
        if (gripWeight <= 0f || !animator.isHuman) return;

        if (!PalmFrame(out _, out _, out Vector3 palm)) return;
        var ids = reachHand == AvatarIKGoal.LeftHand ? LeftFingers : RightFingers;

        float w = Mathf.SmoothStep(0f, 1f, gripWeight);
        for (int f = 0; f < 5; f++)
        {
            Vector3 curl = f == 4 ? gripThumbCurl : gripFingerCurl;
            for (int j = 0; j < 3; j++)
            {
                Transform bone = animator.GetBoneTransform(ids[f * 3 + j]);
                if (bone == null) continue;
                // This joint's direction: toward its child, or carried on from its parent for the tip
                Vector3 dir = bone.childCount > 0 ? bone.GetChild(0).position - bone.position : bone.position - bone.parent.position;
                Vector3 axis = Vector3.Cross(dir, palm);   // turning about this swings the joint toward the palm
                if (axis.sqrMagnitude < 1e-8f) continue;
                bone.rotation = Quaternion.AngleAxis(curl[j] * w, axis.normalized) * bone.rotation;
            }
        }
    }

    // ---------- Doorway posture: bow + arm through the door (after animation and IK) ----------

    // Kneeling: lay the feet back along the ground (toes pointing behind, tops of the feet down),
    // and remember where the hips sit relative to the IK body position for the next IK pass
    void KneelFeet()
    {
        if (hipsBone != null) { hipsFromBody = hipsBone.position - lastBodyPosition; hipsFromBody.y = 0f; }
        if (postureWeight <= 0f || !animator.isHuman) return;
        Vector3 back = -Flat(root.forward);
        Vector3 want = (back * Mathf.Cos(15f * Mathf.Deg2Rad) + Vector3.down * Mathf.Sin(15f * Mathf.Deg2Rad)).normalized;
        LayFoot(leftFoot, leftToes, want);
        LayFoot(rightFoot, rightToes, want);
    }

    // A foot whose toes would dip into the floor (push-off, heel strike, blended clips) tips up at the
    // ankle until the toes rest on the floor - like a real heel lifting - instead of sinking or lifting the body
    void ClampToes()
    {
        float w = 1f - postureWeight;   // the kneel lays the feet itself
        if (w <= 0f || !animator.isHuman) return;
        ClampToe(leftFoot, leftToes, w);
        ClampToe(rightFoot, rightToes, w);
    }

    void ClampToe(Transform foot, Transform toes, float w)
    {
        if (foot == null || toes == null) return;
        float s = modelScale;
        if (!GroundBelow(toes.position + Vector3.up * 0.5f * s, 1.5f * s, out RaycastHit hit)) return;
        float minY = hit.point.y + toeThickness * s;
        if (toes.position.y >= minY) return;

        Vector3 dir = toes.position - foot.position;
        float len = dir.magnitude;
        float dy = minY - foot.position.y;            // how high the toes need to be relative to the ankle
        if (len < 0.0001f || Mathf.Abs(dy) >= len) return;
        Vector3 flat = new Vector3(dir.x, 0f, dir.z);
        if (flat.sqrMagnitude < 0.0001f) return;
        Vector3 want = flat.normalized * Mathf.Sqrt(len * len - dy * dy) + Vector3.up * dy;
        foot.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(dir, want), w) * foot.rotation;
    }

    void LayFoot(Transform foot, Transform toes, Vector3 want)
    {
        if (foot == null || toes == null) return;
        Vector3 dir = toes.position - foot.position;
        if (dir.sqrMagnitude < 0.0001f) return;
        foot.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(dir, want), postureWeight) * foot.rotation;
    }

    // Caught inside: the hand is dragging you to the door and out through it
    bool DraggingThroughDoor => carrying && carryThroughDoor && Time.time - carryStart < pullTime + throughDoorTime + 0.25f;

    void UpdateDoorwayPosture()
    {
        float dt = Time.deltaTime;
        bool usable = doorwayPosture && animator.isHuman && ai != null && spine != null && shoulder != null && elbow != null && head != null;
        bool dragging = DraggingThroughDoor;
        bool hasDoor = dragging || ai == null || ai.SyncedDoorPoint != Vector3.zero;   // not synced yet = not at a door
        usable &= hasDoor;
        bool atDoor = usable && (ai.SyncedPeeking || ai.SyncedReaching || dragging);
        postureWeight = Mathf.MoveTowards(postureWeight, atDoor ? 1f : 0f, dt * postureSpeed);
        armWeight = Mathf.MoveTowards(armWeight, usable && (ai.SyncedReaching || dragging) ? 1f : 0f, dt * reachSpeed);
        if (!usable || (postureWeight <= 0f && armWeight <= 0f))
        {
            bow = bowTarget = 0f;
            leanIn = leanInTarget = 0f;
            debugBow = debugLeanIn = debugReachShortfall = 0f;
            return;
        }

        float s = modelScale;
        Vector3 door = dragging ? carryDoor : ai.SyncedDoorPoint;
        Vector3 n = Flat(root.forward), right = Vector3.Cross(Vector3.up, n);
        MeasureOpening(door, n, right);
        Vector3 handGoal = dragging ? carryTarget : smoothedHand;
        bool reaching = armWeight > 0f;

        // Search for the bow (and lean-in) that gets the hand where it wants to be without any
        // part of the head, neck, chest or shoulders entering the building
        Vector3 pivot = spine.position;
        Vector3 s0 = shoulder.position, n0 = neck != null ? neck.position : head.position, c0 = chest != null ? chest.position : spine.position;
        Vector3 h0 = head.position, f0 = freeShoulder != null ? freeShoulder.position : s0;
        float step = 0.08f * s;
        float bestCost = float.MaxValue, bestBow = 0f, bestLean = leanIn;
        for (int li = -2; li <= 2; li++)
        {
            float lean = Mathf.Clamp(leanIn + li * step, 0f, maxLeanIn * s);
            Vector3 shift = n * (lean - leanIn) * postureWeight;
            for (float b = 0f; b <= maxBow + 0.01f; b += 5f)
            {
                Quaternion q = Quaternion.AngleAxis(b * postureWeight, right);
                Vector3 S = pivot + shift + q * (s0 - pivot);
                Vector3 N = pivot + shift + q * (n0 - pivot);
                Vector3 C = pivot + shift + q * (c0 - pivot);
                Vector3 F = pivot + shift + q * (f0 - pivot);
                Vector3 H = pivot + shift + q * (h0 - pivot) + Vector3.up * 0.08f * s;   // middle of the skull
                if (!Clear(door, n, S, 0.07f * s) || !Clear(door, n, F, 0.07f * s) || !Clear(door, n, N, 0.06f * s) ||
                    !Clear(door, n, C, 0.13f * s) || !Clear(door, n, H, 0.11f * s * headScale + headClearance * s))
                    continue;

                float cost;
                if (reaching)
                {
                    SolveArmThroughDoor(door, n, right, S, handGoal, out float shortfall, out _, out _);
                    cost = shortfall;
                }
                else
                {
                    // Peeking: bring its eye down to the top of the doorway, just outside
                    float eyeDepth = Vector3.Dot(H - door, n);
                    cost = Mathf.Abs(eyeDepth + 0.25f * s) * 0.5f + Mathf.Abs(H.y - (openingTop + 0.1f * s));
                }
                cost += b * 0.004f + Mathf.Abs(lean - leanIn) * 0.05f;
                if (cost < bestCost) { bestCost = cost; bestBow = b; bestLean = lean; }
            }
        }
        bowTarget = bestBow;
        leanInTarget = bestLean;
        bow = Mathf.MoveTowards(bow, bowTarget, 70f * dt);
        leanIn = Mathf.MoveTowards(leanIn, leanInTarget, 0.25f * s * dt);   // applied to the hips next IK pass

        // Bow the upper body
        spine.rotation = Quaternion.AngleAxis(bow * postureWeight, right) * spine.rotation;

        // The free hand goes flat on the ground beside it, outside the building
        if (plantFreeHand && freeShoulder != null && freeElbow != null && freeHand != null)
        {
            Vector3 fs = freeShoulder.position;
            float sideSign = Mathf.Sign(Vector3.Dot(fs - shoulder.position, right));
            Vector3 plant = fs + n * 0.1f * s + right * sideSign * 0.06f * s;
            plant.y = transform.position.y + 0.04f * s;
            float maxDepth = -(wallHalfThickness + 0.08f * s);
            float pd = Vector3.Dot(plant - door, n);
            if (pd > maxDepth) plant -= n * (pd - maxDepth);
            Vector3 pole = fs + right * sideSign * 0.3f * s - n * 0.1f * s;
            TwoBoneIK(freeShoulder, freeElbow, freeHand, plant, pole, postureWeight);
        }

        // The reaching arm. Every pose is checked against the wall (both bones, with their thickness):
        // the arm may only pass through the door opening and stay under the ceiling once inside.
        if (reaching)
        {
            Vector3 S = shoulder.position;
            float shortfall = 0f;
            bool found = false;
            Vector3 elbowPos = default, handPos = default;

            // 1. The ideal pose: forearm threaded through the opening toward the goal
            if (SolveArmThroughDoor(door, n, right, S, handGoal, out shortfall, out elbowPos, out handPos) &&
                ArmClear(door, n, right, S, elbowPos, handPos))
            {
                found = true;
                debugArmMode = Vector3.Dot(handGoal - door, n) < NearDoorDepth ? 1 : 2;
            }

            // 2. Otherwise: try other elbow angles, easing the hand back toward the doorway, then outside it
            if (!found)
            {
                Vector3 outside = new Vector3(openingCenter.x, Mathf.Clamp(handGoal.y, openingBottom + 0.3f, openingTop - 0.6f), openingCenter.z)
                                  - n * (wallHalfThickness + armRadius * s + 0.3f);
                Vector3 inDoor = outside + n * (wallHalfThickness + armRadius * s + 0.3f);
                for (int k = 0; k <= 6 && !found; k++)
                {
                    // k 0..3: goal -> middle of the doorway, k 4..6: doorway -> just outside
                    Vector3 h = k <= 3 ? Vector3.Lerp(handGoal, inDoor, k / 3f) : Vector3.Lerp(inDoor, outside, (k - 3) / 3f);
                    if (k > 0 && k <= 3 &&
                        SolveArmThroughDoor(door, n, right, S, h, out float sf, out Vector3 te, out Vector3 th) && ArmClear(door, n, right, S, te, th))
                    {
                        found = true;
                        elbowPos = te; handPos = th;
                        shortfall = Vector3.Distance(th, handGoal);
                        debugArmMode = 2;
                        break;
                    }
                    for (int sw = 0; sw < 12 && !found; sw++)
                    {
                        Vector3 axis = (h - S).normalized;
                        Vector3 basePole = Vector3.down * s - n * 0.5f * s;
                        Vector3 pole = S + Quaternion.AngleAxis(sw * 30f, axis) * basePole;
                        Vector3 e = ElbowFor(S, h, upperArmLength, foreArmLength, pole);
                        Vector3 hh = S + (h - S).normalized * Mathf.Min(Vector3.Distance(S, h), upperArmLength + foreArmLength - 0.001f);
                        if (!ArmClear(door, n, right, S, e, hh)) continue;
                        found = true;
                        elbowPos = e; handPos = hh;
                        shortfall = Vector3.Distance(hh, handGoal);
                        debugArmMode = k <= 3 ? 2 : 3;
                    }
                }
            }

            // 3. Nothing fits from this pose: keep the hand outside, elbow low (it'll try again next frame)
            if (!found)
            {
                Vector3 outside = openingCenter - n * (wallHalfThickness + armRadius * s + 0.6f);
                Vector3 pole = S + Vector3.down * s - n * 0.5f * s;
                elbowPos = ElbowFor(S, outside, upperArmLength, foreArmLength, pole);
                handPos = S + (outside - S).normalized * Mathf.Min(Vector3.Distance(S, outside), upperArmLength + foreArmLength - 0.001f);
                shortfall = Vector3.Distance(handPos, handGoal);
                debugArmMode = 3;
            }

            ApplyArm(shoulder, elbow, HandBone, elbowPos, handPos, armWeight);
            debugReachShortfall = shortfall;
        }
        else { debugReachShortfall = 0f; debugArmMode = 0; }

        // Face whatever it's looking at (the bow pitched the head down with the body)
        Vector3 lookAt = reaching ? HandPosition : lookPos;
        AimHead(lookAt, postureWeight * 0.85f);

        debugBow = bow * postureWeight;
        debugLeanIn = leanIn * postureWeight;
    }

    Transform HandBone => reachHand == AvatarIKGoal.LeftHand ? leftHandBone : rightHandBone;
    const float NearDoorDepth = 0.6f;   // goals shallower than this (meters past the wall) are reached directly

    // Is this body part (a sphere) outside the building, or above its roof?
    bool Clear(Vector3 door, Vector3 n, Vector3 p, float radius)
    {
        bool outside = Vector3.Dot(p - door, n) <= -(wallHalfThickness + radius);
        bool overRoof = p.y >= roofTop + radius;
        bool overFloor = p.y >= transform.position.y + radius * 0.5f;
        return (outside || overRoof) && overFloor;
    }

    // Find where the arm can go: the forearm passes through a point in the door opening, the elbow
    // stays outside, and the bones keep their real lengths. Returns false if the shoulder is too far
    // away for the hand to get through at all. 'shortfall' = how far short of the goal the hand ends up.
    bool SolveArmThroughDoor(Vector3 door, Vector3 n, Vector3 right, Vector3 S, Vector3 goal,
                             out float shortfall, out Vector3 elbowPos, out Vector3 handPos)
    {
        float l1 = upperArmLength, l2 = foreArmLength, s = modelScale;
        float rArm = armRadius * s;
        elbowPos = handPos = goal;

        float goalDepth = Vector3.Dot(goal - door, n);
        if (goalDepth < NearDoorDepth)
        {
            // Hand outside or right in the doorway (coming in / pulling someone out): plain reach with the
            // elbow low and outside, so the forearm runs out through the opening rather than over the lintel
            shortfall = Mathf.Max(0f, Vector3.Distance(S, goal) - (l1 + l2));
            Vector3 pole = goal - n * 2f * s * 0.1f + Vector3.down * 0.3f * s;
            elbowPos = ElbowFor(S, goal, l1, l2, pole);
            handPos = S + (goal - S).normalized * Mathf.Min(Vector3.Distance(S, goal), l1 + l2 - 0.001f);
            return true;
        }

        // Where the shoulder->goal line crosses the wall, kept inside the opening (minus the arm's thickness)
        float shoulderDepth = Vector3.Dot(S - door, n);
        float t = shoulderDepth < 0f ? shoulderDepth / (shoulderDepth - goalDepth) : 0f;
        Vector3 cross = S + (goal - S) * t;
        // Close to the door, cross the wall at the goal's own height (so whatever it's holding fits through)
        float nearDoor = Mathf.Clamp01(1f - goalDepth / 1.5f);
        cross = Vector3.Lerp(cross, goal, nearDoor);
        float side = Mathf.Clamp(Vector3.Dot(cross - openingCenter, right), -(openingHalfWidth - rArm), openingHalfWidth - rArm);
        float y = Mathf.Clamp(cross.y, openingBottom + rArm, openingTop - rArm);
        Vector3 P = openingCenter + right * side;
        P.y = y;
        P += n * Vector3.Dot(door - P, n);   // on the wall's center plane

        Vector3 d = Vector3.forward;
        float inward = 1f;
        for (int i = 0; i < 3; i++)
        {
            d = (goal - P).normalized;
            inward = Vector3.Dot(d, n);
            if (inward < 0.3f) { d = (d + n * (0.3f - inward)).normalized; inward = Vector3.Dot(d, n); }
            // A sloped arm is higher on one face of the wall than at its middle: keep both faces in the opening
            float rise = Mathf.Abs(d.y) * (wallHalfThickness + rArm) / inward + 0.02f;
            float yMax = openingTop - rArm - rise, yMin = openingBottom + rArm + rise;
            if (yMax < yMin) { shortfall = 30f; return false; }   // opening too small for the arm at this angle
            float clamped = Mathf.Clamp(P.y, yMin, yMax);
            if (Mathf.Approximately(clamped, P.y)) break;
            P.y = clamped;
        }

        // Elbow at distance a outside P along -d, with |S - elbow| = l1:  a^2 + 2a(d.w) + |w|^2 - l1^2 = 0
        Vector3 w = S - P;
        float bq = Vector3.Dot(d, w);
        float disc = bq * bq - (w.sqrMagnitude - l1 * l1);
        if (disc < 0f)
        {
            shortfall = 20f + (w.magnitude - l1);   // shoulder too far: hand can't get through
            return false;
        }
        float root = Mathf.Sqrt(disc);
        float aMin = (wallHalfThickness + rArm * 1.2f) / inward;   // elbow fully outside the wall
        float want = Vector3.Distance(goal, P);
        float wantA = l2 - want;
        float best = float.NaN;
        foreach (float a in new[] { -bq - root, -bq + root })
        {
            if (a < aMin || a > l2) continue;
            if (float.IsNaN(best) || Mathf.Abs(a - wantA) < Mathf.Abs(best - wantA)) best = a;
        }
        if (float.IsNaN(best))
        {
            shortfall = 10f + want;   // elbow would have to be inside the wall: no good
            return false;
        }

        float reach = l2 - best;
        shortfall = Mathf.Max(0f, want - reach) + Mathf.Max(0f, reach - want) * 0.5f;
        elbowPos = P - d * best;
        handPos = P + d * reach;
        return true;
    }

    // Does this arm pose stay out of the building? Each bone is a capsule; where it passes through the
    // front wall it has to be inside the door opening, and inside the room it has to stay under the ceiling.
    bool ArmClear(Vector3 door, Vector3 n, Vector3 right, Vector3 shoulderPos, Vector3 elbowPos, Vector3 handPos)
    {
        float r = armRadius * modelScale;
        return BoneClear(door, n, right, shoulderPos, elbowPos, r * 1.15f) && BoneClear(door, n, right, elbowPos, handPos, r);
    }

    bool BoneClear(Vector3 door, Vector3 n, Vector3 right, Vector3 a, Vector3 b, float r)
    {
        float da = Vector3.Dot(a - door, n), db = Vector3.Dot(b - door, n);
        float slab = wallHalfThickness + r;
        // Portion of the bone inside the (thickened) wall: must be within the opening
        if (RangeOnSegment(da, db, -slab, slab, out float t0, out float t1) &&
            (!InOpening(a + (b - a) * t0, door, right, r) || !InOpening(a + (b - a) * t1, door, right, r)))
            return false;
        // Portion inside the room: under the ceiling
        if (RangeOnSegment(da, db, slab, float.MaxValue, out t0, out t1))
        {
            float ya = Mathf.Lerp(a.y, b.y, t0), yb = Mathf.Lerp(a.y, b.y, t1);
            if (Mathf.Max(ya, yb) > roomCeiling - r) return false;
        }
        return true;
    }

    bool InOpening(Vector3 p, Vector3 door, Vector3 right, float r)
    {
        float side = Vector3.Dot(p - openingCenter, right);
        return Mathf.Abs(side) <= openingHalfWidth - r && p.y >= openingBottom + r * 0.5f && p.y <= openingTop - r;
    }

    // The part of a segment (parameter t in 0..1) where a linear value going from va to vb is within [lo, hi]
    static bool RangeOnSegment(float va, float vb, float lo, float hi, out float t0, out float t1)
    {
        t0 = 0f; t1 = 1f;
        if (Mathf.Approximately(va, vb)) return va >= lo && va <= hi;
        float ta = (lo - va) / (vb - va), tb = (hi - va) / (vb - va);
        if (ta > tb) { float tmp = ta; ta = tb; tb = tmp; }
        t0 = Mathf.Max(0f, ta); t1 = Mathf.Min(1f, tb);
        return t0 <= t1;
    }

    static Vector3 ElbowFor(Vector3 a, Vector3 target, float l1, float l2, Vector3 pole)
    {
        Vector3 toT = target - a;
        float dist = Mathf.Clamp(toT.magnitude, 0.001f, l1 + l2 - 0.001f);
        Vector3 dir = toT.normalized;
        float cosA = Mathf.Clamp((l1 * l1 + dist * dist - l2 * l2) / (2f * l1 * dist), -1f, 1f);
        Vector3 bend = Vector3.ProjectOnPlane(pole - a, dir);
        bend = bend.sqrMagnitude > 0.0001f ? bend.normalized : Vector3.Cross(dir, Vector3.right).normalized;
        return a + dir * (l1 * cosA) + bend * (l1 * Mathf.Sqrt(1f - cosA * cosA));
    }

    static void TwoBoneIK(Transform a, Transform b, Transform c, Vector3 target, Vector3 pole, float w)
    {
        float l1 = Vector3.Distance(a.position, b.position), l2 = Vector3.Distance(b.position, c.position);
        Vector3 elbowPos = ElbowFor(a.position, target, l1, l2, pole);
        Vector3 handPos = a.position + (target - a.position).normalized * Mathf.Min(Vector3.Distance(a.position, target), l1 + l2 - 0.001f);
        ApplyArm(a, b, c, elbowPos, handPos, w);
    }

    // Rotate the upper bone so its joint lands on elbowPos, then the lower bone so its end lands on handPos
    static void ApplyArm(Transform a, Transform b, Transform c, Vector3 elbowPos, Vector3 handPos, float w)
    {
        Quaternion qa = Quaternion.FromToRotation(b.position - a.position, elbowPos - a.position);
        a.rotation = Quaternion.Slerp(Quaternion.identity, qa, w) * a.rotation;
        Quaternion qb = Quaternion.FromToRotation(c.position - b.position, handPos - b.position);
        b.rotation = Quaternion.Slerp(Quaternion.identity, qb, w) * b.rotation;
    }

    void AimHead(Vector3 target, float w)
    {
        if (head == null || w <= 0f || headLocalForward == Vector3.zero) return;
        Vector3 fwd = head.rotation * headLocalForward;
        Vector3 want = target - head.position;
        if (want.sqrMagnitude < 0.01f) return;
        Quaternion q = Quaternion.FromToRotation(fwd, want.normalized);
        q.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f) angle -= 360f;
        angle = Mathf.Clamp(angle, -70f, 70f);
        head.rotation = Quaternion.AngleAxis(angle * w, axis) * head.rotation;
    }

    // Size of the doorway, found once per door: top of the opening, its width and the roof height
    void MeasureOpening(Vector3 door, Vector3 n, Vector3 right)
    {
        if ((door - openingFor).sqrMagnitude < 0.01f) return;
        openingFor = door;
        float l = Cast(door, -right, 4f, out var hl) ? hl.distance : 1.1f;
        float r = Cast(door, right, 4f, out var hr) ? hr.distance : 1.1f;
        openingHalfWidth = (l + r) / 2f;
        openingCenter = door + right * ((r - l) / 2f);
        openingTop = Cast(door, Vector3.up, 8f, out var hu) ? hu.point.y : door.y + 1.1f;
        openingBottom = Cast(door, Vector3.down, 4f, out var hd) ? hd.point.y : door.y - 1.2f;
        roofTop = Cast(door + Vector3.up * 40f, Vector3.down, 40f, out var hroof) ? hroof.point.y : openingTop;
        roomCeiling = Cast(door + n * 1f, Vector3.up, 10f, out var hc) ? hc.point.y : openingTop;
    }

    // Raycast ignoring the giant itself and players
    bool Cast(Vector3 from, Vector3 dir, float distance, out RaycastHit best)
    {
        best = default;
        int count = Physics.RaycastNonAlloc(from, dir, rayHits, distance, groundLayers, QueryTriggerInteraction.Ignore);
        float bestDist = float.MaxValue;
        bool found = false;
        for (int i = 0; i < count; i++)
        {
            var h = rayHits[i];
            if (h.collider.transform.IsChildOf(root)) continue;
            if (h.collider.GetComponentInParent<FirstPersonController>() != null) continue;
            if (h.distance < bestDist) { bestDist = h.distance; best = h; found = true; }
        }
        return found;
    }

    // An in-place run moves the planted foot backward at exactly the speed the body should travel.
    // Measure that, and we know how fast to play the clip so the feet stick.
    void MeasureStride()
    {
        if (leftFoot == null || rightFoot == null || Time.deltaTime <= 0f) return;

        Vector3 l = root.InverseTransformPoint(leftFoot.position);
        Vector3 r = root.InverseTransformPoint(rightFoot.position);
        float yaw = root.eulerAngles.y;
        float yawRate = Mathf.Abs(Mathf.DeltaAngle(lastYaw, yaw)) / Time.deltaTime;

        bool usable = hasLastFeet
            && animator.speed > 0.05f
            && !InAction()
            && yawRate < 20f                    // turning swings the feet sideways: ignore
            && smoothedSpeed > 0.5f
            && fallbackNatural > 0.1f;

        if (usable)
        {
            // Only the foot that's actually on the ground tells us the stride speed
            bool leftDown = l.y < r.y;
            float footY = leftDown ? leftFoot.position.y : rightFoot.position.y;
            float footHeight = (footY - transform.position.y) / modelScale - ankleHeight;   // above standing height
            if (footHeight < plantMargin * 1.5f)
            {
                float backward = leftDown ? (leftLastLocal.z - l.z) : (rightLastLocal.z - r.z);
                float natural = (backward / Time.deltaTime) / animator.speed;   // m/s at playback 1

                // Throw out anything wildly different from what the clip settings expect
                if (natural > fallbackNatural * 0.4f && natural < fallbackNatural * 2.5f)
                {
                    measuredNaturalSpeed = measuredNaturalSpeed < 0f
                        ? natural
                        : Mathf.Lerp(measuredNaturalSpeed, natural, 0.05f);
                }
            }
        }

        leftLastLocal = l;
        rightLastLocal = r;
        lastYaw = yaw;
        hasLastFeet = true;
    }

    void CheckFoot(Transform foot, ref bool planted, FootTrack track)
    {
        if (foot == null) return;
        // Height above its standing height, measured from the model itself (so lifting the model doesn't matter)
        float h = (foot.position.y - transform.position.y) / modelScale - ankleHeight;   // normal human meters

        // Learn how low and high this foot goes: snaps to new extremes, slowly forgets old ones
        float forget = 1f - Mathf.Exp(-0.5f * Time.deltaTime);
        track.lo = h < track.lo ? h : Mathf.Lerp(track.lo, h, forget);
        track.hi = h > track.hi ? h : Mathf.Lerp(track.hi, h, forget);
        float swing = track.hi - track.lo;
        if (swing < minimumSwing) return;   // barely moving (idle sway)

        if (!planted && h <= track.lo + swing * plantPoint)
        {
            planted = true;
            if (logFootsteps) Debug.Log($"Monster footstep ({foot.name}) speed {smoothedSpeed:0.0} swing {swing:0.000}");
            if (smoothedSpeed > 0.2f && monsterAudio != null)
                monsterAudio.Footstep(foot.position);
        }
        else if (planted && h >= track.lo + swing * liftPoint)
        {
            planted = false;
        }
    }

    void OnAnimatorIK(int layerIndex)
    {
        if (postureWeight > 0f && animator.isHuman) { ReleaseFootLocks(); Kneel(); }
        else if (groundFeet && animator.isHuman) GroundFeet();
        lastBodyPosition = animator.bodyPosition;   // only readable in here; KneelFeet uses it after the pose is applied

        // While reaching, only the head follows the hand; the body stays still
        float bodyWeight = lookBodyWeight * (1f - Mathf.Max(currentReachWeight, carryWeight));
        animator.SetLookAtWeight(currentLookWeight, bodyWeight, lookHeadWeight, 1f, lookClamp);
        animator.SetLookAtPosition(lookPos);

        if (carryWeight > 0f)
        {
            // Lifting you to its face. While it's still dragging you out through a door, the
            // doorway arm solver (LateUpdate) has the hand instead.
            float w = carryWeight * (doorwayPosture ? 1f - armWeight : 1f);
            animator.SetIKPositionWeight(reachHand, w);
            animator.SetIKPosition(reachHand, carryTarget);
        }
        else if (doorwayPosture)
        {
            animator.SetIKPositionWeight(reachHand, 0f);   // LateUpdate threads the arm through the door
        }
        else
        {
            animator.SetIKPositionWeight(reachHand, currentReachWeight);
            if (currentReachWeight > 0f && ai != null) animator.SetIKPosition(reachHand, smoothedHand);
        }
    }

    // ---------- Doorway posture: kneel (IK pass) ----------

    // Drops onto its knees and shifts in toward the door. Everything is relative to the model, so
    // the floor snap and leg lift still work on top of it.
    void Kneel()
    {
        float w = postureWeight, s = modelScale;
        Vector3 n = Flat(root.forward), right = Vector3.Cross(Vector3.up, n);
        float baseY = transform.position.y;

        Vector3 bp = animator.bodyPosition;
        Vector3 target = bp + n * leanIn;
        target.y = baseY + kneelHipHeight * s;
        Vector3 body = Vector3.Lerp(bp, target, w);
        animator.bodyPosition = body;
        Vector3 hips = body + hipsFromBody;   // where the hip joints actually are

        KneelLeg(AvatarIKGoal.LeftFoot, AvatarIKHint.LeftKnee, bp, hips, n, right, baseY, w);
        KneelLeg(AvatarIKGoal.RightFoot, AvatarIKHint.RightKnee, bp, hips, n, right, baseY, w);
        hipOffset = Mathf.Lerp(hipOffset, 0f, 1f - Mathf.Exp(-hipAdjustSpeed * Time.deltaTime));
    }

    void KneelLeg(AvatarIKGoal foot, AvatarIKHint knee, Vector3 animatedHips, Vector3 hips, Vector3 n, Vector3 right, float baseY, float w)
    {
        float s = modelScale;
        float side = Vector3.Dot(animator.GetIKPosition(foot) - animatedHips, right);
        side = Mathf.Sign(side) * Mathf.Clamp(Mathf.Abs(side), 0.08f * s, 0.16f * s);   // knees a hip-width apart

        Vector3 footPos = hips + right * side - n * kneelFootBack * s;
        footPos.y = baseY + ankleHeight * s;
        animator.SetIKPositionWeight(foot, w);
        animator.SetIKPosition(foot, footPos);
        animator.SetIKRotationWeight(foot, 0f);   // toes are laid flat from the toe bones in LateUpdate

        Vector3 kneePos = hips + right * side * 1.1f + n * kneelKneeForward * s;
        kneePos.y = baseY;
        animator.SetIKHintPositionWeight(knee, w);
        animator.SetIKHintPosition(knee, kneePos);
    }

    static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.forward;
    }

    // ---------- Foot grounding ----------

    void GroundFeet()
    {
        // Grab / peek poses handle their own legs: let them, except a foot never sinks into the floor
        if (InAction())
        {
            ReleaseFootLocks();
            KeepFootAboveFloor(AvatarIKGoal.LeftFoot);
            KeepFootAboveFloor(AvatarIKGoal.RightFoot);
            animator.SetIKRotationWeight(AvatarIKGoal.LeftFoot, 0f);
            animator.SetIKRotationWeight(AvatarIKGoal.RightFoot, 0f);
            hipOffset = Mathf.Lerp(hipOffset, 0f, 1f - Mathf.Exp(-hipAdjustSpeed * Time.deltaTime));
            animator.bodyPosition += Vector3.up * hipOffset;
            return;
        }

        float leftNeed = GroundFoot(AvatarIKGoal.LeftFoot);
        float rightNeed = GroundFoot(AvatarIKGoal.RightFoot);

        // If a planted foot can't reach the ground, drop the hips so it can (never raise above the animation)
        float targetOffset = Mathf.Min(0f, Mathf.Min(leftNeed, rightNeed));
        targetOffset = Mathf.Max(targetOffset, -maxHipDrop * modelScale);
        hipOffset = Mathf.Lerp(hipOffset, targetOffset, 1f - Mathf.Exp(-hipAdjustSpeed * Time.deltaTime));
        animator.bodyPosition += Vector3.up * hipOffset;
    }

    void UpdateFootLock(FootLock fl, float heightAbove, Vector3 animated, float targetY)
    {
        float dt = Time.deltaTime;
        if (!lockPlantedFeet || smoothedSpeed < 0.2f)
        {
            fl.locked = false;
            fl.weight = Mathf.MoveTowards(fl.weight, 0f, dt / Mathf.Max(lockReleaseTime, 0.01f));
            return;
        }

        // Learn this foot's swing from the animation (same idea as the footstep detection)
        float forget = 1f - Mathf.Exp(-0.5f * dt);
        fl.lo = heightAbove < fl.lo ? heightAbove : Mathf.Lerp(fl.lo, heightAbove, forget);
        fl.hi = heightAbove > fl.hi ? heightAbove : Mathf.Lerp(fl.hi, heightAbove, forget);
        float swing = Mathf.Max(fl.hi - fl.lo, 0.005f);

        Vector3 flatAnim = new Vector3(animated.x, 0f, animated.z), flatLock = new Vector3(fl.pos.x, 0f, fl.pos.z);

        // How fast the animated foot moves over the ground. A planted foot barely moves; a swinging one moves
        // faster than the body. (Height alone can't tell them apart in a shuffling walk.)
        if (fl.hasPrev && dt > 0f)
        {
            Vector3 v = animated - fl.prevAnimated; v.y = 0f;
            fl.groundSpeed = Mathf.Lerp(fl.groundSpeed, v.magnitude / dt, 1f - Mathf.Exp(-20f * dt));
        }
        fl.prevAnimated = animated;
        fl.hasPrev = true;
        bool slowOnGround = fl.groundSpeed < smoothedSpeed * 0.55f;
        bool swinging = fl.groundSpeed > smoothedSpeed * 1.1f;

        bool lifted = heightAbove >= fl.lo + swing * liftPoint || swinging;
        if (lifted) fl.canLock = true;   // a new step can lock once the foot has actually come up / swung
        if (!fl.locked && fl.canLock && slowOnGround && heightAbove <= fl.lo + swing * 0.5f)
        {
            fl.locked = true;
            fl.canLock = false;
            fl.pos = new Vector3(animated.x, targetY, animated.z);
            debugLockCount++;
        }
        else if (fl.locked && (lifted || Vector3.Distance(flatAnim, flatLock) > maxLockStretch * modelScale))
        {
            fl.locked = false;   // the animation is lifting it (or the leg would overstretch): let go smoothly
            if (lifted) debugLockReleasedByLift++; else debugLockReleasedByStretch++;
        }
        fl.weight = fl.locked ? 1f : Mathf.MoveTowards(fl.weight, 0f, dt / Mathf.Max(lockReleaseTime, 0.01f));
        debugLeftFootLocked = leftLock.locked;
        debugRightFootLocked = rightLock.locked;
    }

    void ReleaseFootLocks()
    {
        leftLock.locked = rightLock.locked = false;
        leftLock.weight = rightLock.weight = 0f;
    }

    void KeepFootAboveFloor(AvatarIKGoal goal)
    {
        Vector3 animated = animator.GetIKPosition(goal);
        float s = modelScale;
        if (GroundBelow(animated + Vector3.up * (0.6f * s), 2f * s, out RaycastHit hit) && animated.y < hit.point.y + ankleHeight * s)
        {
            animator.SetIKPositionWeight(goal, 1f);
            animator.SetIKPosition(goal, new Vector3(animated.x, hit.point.y + ankleHeight * s, animated.z));
        }
        else animator.SetIKPositionWeight(goal, 0f);
    }

    // Pins the foot to the ground when it's close to it. Returns how far down the foot needs to go (negative = down).
    float GroundFoot(AvatarIKGoal goal)
    {
        Vector3 animated = animator.GetIKPosition(goal);
        float s = modelScale;

        if (!GroundBelow(animated + Vector3.up * (0.6f * s), 2f * s, out RaycastHit hit))
        {
            animator.SetIKPositionWeight(goal, 0f);
            animator.SetIKRotationWeight(goal, 0f);
            return 0f;
        }

        float targetY = hit.point.y + ankleHeight * s;
        float heightAbove = (animated.y - targetY) / s;                 // normal human meters
        float weight = Mathf.Clamp01(1f - heightAbove / groundingZone); // 1 when planted, 0 mid-stride

        Vector3 target = new Vector3(animated.x, targetY + Mathf.Max(0f, heightAbove) * s * (1f - weight), animated.z);

        // Planted: hold the foot where it landed until the animation lifts it
        var fl = goal == AvatarIKGoal.LeftFoot ? leftLock : rightLock;
        UpdateFootLock(fl, heightAbove, animated, targetY);
        if (fl.weight > 0f)
        {
            target = Vector3.Lerp(target, new Vector3(fl.pos.x, targetY, fl.pos.z), fl.weight);
            weight = Mathf.Max(weight, fl.weight);
        }
        animator.SetIKPositionWeight(goal, weight);
        animator.SetIKPosition(goal, target);

        // Tilt the foot to match slopes
        Quaternion slope = Quaternion.FromToRotation(Vector3.up, hit.normal);
        animator.SetIKRotationWeight(goal, weight * 0.6f);
        animator.SetIKRotation(goal, slope * animator.GetIKRotation(goal));

        // Planted foot floating above the ground -> hips need to come down by that much
        return weight > 0.5f ? Mathf.Min(0f, targetY - animated.y) : 0f;
    }

    // Raycast down, ignoring the giant's own colliders and players
    bool GroundBelow(Vector3 from, float distance, out RaycastHit best)
    {
        best = default;
        int count = Physics.RaycastNonAlloc(from, Vector3.down, rayHits, distance, groundLayers, QueryTriggerInteraction.Ignore);
        float bestDist = float.MaxValue;
        bool found = false;
        for (int i = 0; i < count; i++)
        {
            var h = rayHits[i];
            if (h.collider.transform.IsChildOf(root)) continue;
            if (h.collider.GetComponentInParent<FirstPersonController>() != null) continue;
            if (h.distance < bestDist) { bestDist = h.distance; best = h; found = true; }
        }
        return found;
    }

    // Called on every computer when it catches someone
    public void PlayGrab()
    {
        animator.SetTrigger(GrabId);
    }

    // Foot debug: green = planted, red = lifted, with height in normal human meters
    void OnDrawGizmos()
    {
        if (!showFootDebug || !Application.isPlaying) return;
        DrawFoot(leftFoot, leftPlanted);
        DrawFoot(rightFoot, rightPlanted);
    }

    void DrawFoot(Transform foot, bool planted)
    {
        if (foot == null) return;
        Gizmos.color = planted ? Color.green : Color.red;
        Gizmos.DrawSphere(foot.position, 0.3f * modelScale / 8f + 0.2f);
#if UNITY_EDITOR
        float h = (foot.position.y - transform.position.y) / modelScale - ankleHeight;
        var track = foot == leftFoot ? leftTrack : rightTrack;
        UnityEditor.Handles.Label(foot.position + Vector3.up, $"{h:0.000}  [{track.lo:0.000} - {track.hi:0.000}]");
#endif
    }
}
