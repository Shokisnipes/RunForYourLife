using UnityEngine;

// Put on the monster's MODEL (the child object with the Animator), under the Monster root.
// Runs on every computer:
// - Blends idle/walk/run smoothly from how fast the giant is actually moving
// - Matches animation playback speed to movement so the feet don't slide
// - Detects real foot contacts and fires MonsterAudio.Footstep() (sound + camera shake) on each plant
// - Head slowly turns to track you, arm reaches for you when it's peeking into a building
[RequireComponent(typeof(Animator))]
public class MonsterAnimator : MonoBehaviour
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
    [Tooltip("Max distance (normal human meters, scaled) it leans its body in when the hand is out of reach.")]
    public float maxLean = 0.2f;
    [Tooltip("How far the head stays outside the doorway while reaching (normal human meters, scaled).")]
    public float headClearance = 0.1f;

    [Header("Pick-up (after a catch)")]
    public float pullTime = 0.9f;           // caught inside: seconds to drag you to the doorway
    public float throughDoorTime = 0.35f;   // seconds to pull you out through the frame
    public float liftTime = 1.2f;           // seconds to raise you from the ground to its face
    public float carryDuration = 3f;        // how long it holds you up
    [Tooltip("Where it holds you, in front of its face (normal human meters, scaled automatically).")]
    public Vector3 holdOffset = new Vector3(0.05f, -0.05f, 0.3f);

    [Header("Foot plants (sound + shake land on contact)")]
    [Tooltip("Foot counts as planted below this height. In NORMAL human meters; scaled to the model automatically.")]
    public float plantHeight = 0.11f;
    [Tooltip("Foot counts as lifted above this height (normal human meters). Keep it above Plant Height.")]
    public float liftHeight = 0.18f;
    public bool showFootDebug = false;

    [Header("Foot grounding (IK keeps planted feet on the ground)")]
    public bool groundFeet = true;
    [Tooltip("Ankle height above the sole, in normal human meters.")]
    public float ankleHeight = 0.09f;
    [Tooltip("Feet closer to the ground than this (normal human meters) get pinned to it.")]
    public float groundingZone = 0.2f;
    [Tooltip("How fast the hips move down/up to let both feet reach the ground.")]
    public float hipAdjustSpeed = 6f;
    public LayerMask groundLayers = ~0;

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
    float modelScale = 1f;

    Vector3 lastRootPos;
    float smoothedSpeed;
    Vector3 lookPos;
    float currentLookWeight;
    float currentReachWeight;
    float hipOffset;

    // Auto stride
    float measuredNaturalSpeed = -1f;       // m/s the current animation "wants" to move at playback 1
    Vector3 leftLastLocal, rightLastLocal;
    bool hasLastFeet;

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
    Vector3 lean;

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
        modelScale = transform.lossyScale.y;
        lastRootPos = root.position;
        lookPos = root.position + root.forward * 10f + Vector3.up * 10f;

        if (!animator.isHuman)
        {
            Debug.LogWarning("MonsterAnimator: the model's Rig must be set to Humanoid for feet, head tracking and reach.");
            return;
        }

        leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
        head = animator.GetBoneTransform(HumanBodyBones.Head);
        leftHandBone = animator.GetBoneTransform(HumanBodyBones.LeftHand);
        rightHandBone = animator.GetBoneTransform(HumanBodyBones.RightHand);

        ScaleBone(HumanBodyBones.LeftUpperArm, armScale);
        ScaleBone(HumanBodyBones.RightUpperArm, armScale);
        ScaleBone(HumanBodyBones.Head, headScale);

        // Measure the reaching arm (after scaling) so it can lean in when the hand needs more reach
        bool left = reachHand == AvatarIKGoal.LeftHand;
        shoulder = animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
        elbow = animator.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
        var hand = left ? leftHandBone : rightHandBone;
        if (shoulder != null && elbow != null && hand != null)
            armLength = Vector3.Distance(shoulder.position, elbow.position) + Vector3.Distance(elbow.position, hand.position);
        if (shoulder != null)
            ReachShoulderSide = root.InverseTransformPoint(shoulder.position).x;
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
        if (autoStrideMatch && measuredNaturalSpeed > 0f && b > 0.1f) natural = measuredNaturalSpeed;
        float cadence = Mathf.Lerp(1f, runCadence, Mathf.InverseLerp(0.5f, 1f, b));   // only boosts the run
        float playback = natural > 0.1f ? Mathf.Clamp(smoothedSpeed / natural * cadence, playbackLimits.x, playbackLimits.y) : 1f;

        // Grab / Peek play at their own speed, not the walking speed
        if (InAction()) playback = actionPlaybackSpeed;
        animator.speed = Mathf.Lerp(animator.speed, playback, 1f - Mathf.Exp(-10f * dt));

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
        carryFrom = handWasIn ? smoothedHand : victimPosition + Vector3.up * 1f;
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

    bool InAction()
    {
        if (!animator.GetCurrentAnimatorStateInfo(0).IsName("Locomotion")) return true;
        return animator.IsInTransition(0) && !animator.GetNextAnimatorStateInfo(0).IsName("Locomotion");
    }

    void LateUpdate()
    {
        // Bones are posed by now: check for foot contacts
        CheckFoot(leftFoot, ref leftPlanted);
        CheckFoot(rightFoot, ref rightPlanted);
        MeasureStride();
    }

    // An in-place run moves the planted foot backward at exactly the speed the body should travel.
    // Measure that, and we know how fast to play the clip so the feet stick.
    void MeasureStride()
    {
        if (leftFoot == null || rightFoot == null || Time.deltaTime <= 0f) return;

        Vector3 l = root.InverseTransformPoint(leftFoot.position);
        Vector3 r = root.InverseTransformPoint(rightFoot.position);

        if (hasLastFeet && animator.speed > 0.05f && !InAction())
        {
            // Use whichever foot is lower (the one on the ground)
            bool leftDown = l.y < r.y;
            float backward = leftDown ? (leftLastLocal.z - l.z) : (rightLastLocal.z - r.z);
            float footSpeed = backward / Time.deltaTime;                       // m/s at the current playback
            float natural = footSpeed / animator.speed;                        // m/s at playback 1

            if (natural > 0.3f * modelScale && natural < 15f * modelScale)
            {
                measuredNaturalSpeed = measuredNaturalSpeed < 0f
                    ? natural
                    : Mathf.Lerp(measuredNaturalSpeed, natural, 0.08f);
            }
        }

        leftLastLocal = l;
        rightLastLocal = r;
        hasLastFeet = true;
    }

    void CheckFoot(Transform foot, ref bool planted)
    {
        if (foot == null) return;
        float h = (foot.position.y - root.position.y) / modelScale;   // in normal human meters

        if (!planted && h < plantHeight)
        {
            planted = true;
            if (smoothedSpeed > 0.2f && monsterAudio != null)
                monsterAudio.Footstep(foot.position);
        }
        else if (planted && h > liftHeight)
        {
            planted = false;
        }
    }

    void OnAnimatorIK(int layerIndex)
    {
        if (groundFeet && animator.isHuman) GroundFeet();

        animator.SetLookAtWeight(currentLookWeight, lookBodyWeight, lookHeadWeight, 1f, lookClamp);
        animator.SetLookAtPosition(lookPos);

        if (carryWeight > 0f)
        {
            animator.SetIKPositionWeight(reachHand, carryWeight);
            animator.SetIKPosition(reachHand, carryTarget);
        }
        else
        {
            animator.SetIKPositionWeight(reachHand, currentReachWeight);
            if (currentReachWeight > 0f && ai != null)
            {
                animator.SetIKPosition(reachHand, smoothedHand);

                // Palm faces where it's reaching
                Vector3 dir = smoothedHand - ai.SyncedDoorPoint;
                if (dir.sqrMagnitude > 0.01f)
                {
                    animator.SetIKRotationWeight(reachHand, currentReachWeight * 0.7f);
                    animator.SetIKRotation(reachHand, Quaternion.LookRotation(dir.normalized, Vector3.up));
                }

                // Elbow goes through the door opening, not through the wall next to it
                AvatarIKHint hint = reachHand == AvatarIKGoal.LeftHand ? AvatarIKHint.LeftElbow : AvatarIKHint.RightElbow;
                Vector3 inward = dir; inward.y = 0f;
                inward = inward.sqrMagnitude > 0.01f ? inward.normalized : root.forward;
                animator.SetIKHintPositionWeight(hint, currentReachWeight);
                animator.SetIKHintPosition(hint, ai.SyncedDoorPoint - inward * 1.5f + Vector3.up * 1.5f);
            }
        }

        // Lean the body in (horizontally only) if the hand is past arm's length,
        // but never far enough to push the head through the wall above the door
        Vector3 targetLean = Vector3.zero;
        if (currentReachWeight > 0f && shoulder != null && armLength > 0f && ai != null)
        {
            Vector3 toHand = smoothedHand - shoulder.position;
            float extra = toHand.magnitude - armLength * 0.95f;
            Vector3 flat = new Vector3(toHand.x, 0f, toHand.z);
            if (extra > 0f && flat.sqrMagnitude > 0.01f)
                targetLean = flat.normalized * Mathf.Min(extra, maxLean * modelScale) * currentReachWeight;

            Vector3 inward = ai.SyncedDoorPoint - root.position;
            inward.y = 0f;
            if (inward.sqrMagnitude > 0.01f && head != null)
            {
                inward.Normalize();
                float headDepth = Vector3.Dot(head.position - lean + targetLean - ai.SyncedDoorPoint, inward);
                float limit = -headClearance * modelScale;   // head stays this far outside the door
                if (headDepth > limit) targetLean -= inward * (headDepth - limit);
            }
        }
        lean = Vector3.Lerp(lean, targetLean, 1f - Mathf.Exp(-4f * Time.deltaTime));
        animator.bodyPosition += lean;
    }

    // ---------- Foot grounding ----------

    void GroundFeet()
    {
        // Kneeling / grabbing poses handle their own legs: let them, and ease the hips back
        if (InAction())
        {
            animator.SetIKPositionWeight(AvatarIKGoal.LeftFoot, 0f);
            animator.SetIKPositionWeight(AvatarIKGoal.RightFoot, 0f);
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
        hipOffset = Mathf.Lerp(hipOffset, targetOffset, 1f - Mathf.Exp(-hipAdjustSpeed * Time.deltaTime));
        animator.bodyPosition += Vector3.up * hipOffset;
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
        float h = (foot.position.y - root.position.y) / modelScale;
        UnityEditor.Handles.Label(foot.position + Vector3.up, h.ToString("0.00"));
#endif
    }
}
