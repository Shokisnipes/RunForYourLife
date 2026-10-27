using System.Collections.Generic;
using UnityEngine;

// Turns the player's body into a ragdoll once a monster catches them. Lives on the Player's Model child next to
// PlayerAnimator and runs on every client from synced state (purely visual, each computer simulates its own).
// Held: the chest is pinned to that monster's hand (our own copy of it) by a joint, so the body stays firmly in
// its grip while the head, arms and legs flop. Dropped: it falls and lies where it lands, limp, until the player
// is back on their feet (respawn now; rescue later, task 8).
// The rigidbodies, colliders and joints are built from the humanoid skeleton when the game starts, so there's
// nothing to set up by hand and it keeps working if the model is swapped.
[RequireComponent(typeof(Animator))]
public class PlayerRagdoll : MonoBehaviour
{
    [Header("Body")]
    [Tooltip("Total mass (kg) spread over the body parts.")]
    public float totalMass = 60f;
    [Tooltip("Air drag on every body part (higher = limbs swing less wildly).")]
    public float drag = 0.2f;
    [Tooltip("Rotational drag on every body part (higher = joints settle sooner).")]
    public float angularDrag = 1.5f;

    [Header("Grip")]
    [Tooltip("How far (degrees) the torso can swing and twist in the monster's grip. Low = firmly held.")]
    public float gripSwing = 20f;
    [Tooltip("How quickly the grip point catches up with the hand when the catch starts (higher = snaps in faster).")]
    public float gripCatchUp = 10f;

    // Every ragdoll collider in the game, so floor checks can skip bodies
    static readonly HashSet<Collider> AllParts = new HashSet<Collider>();
    public static bool IsBodyPart(Collider c) => AllParts.Contains(c);

    // On while the body is a ragdoll (PlayerAnimator stops animating it)
    public bool Active { get; private set; }

    class Part
    {
        public Transform bone;
        public Rigidbody body;
        public Collider collider;
        public Vector3 localPos;
        public Quaternion localRot;
    }

    Animator animator;
    FirstPersonController player;
    readonly List<Part> parts = new List<Part>();
    Transform hips, hipsParent;
    Rigidbody chest;
    SkinnedMeshRenderer[] skins;
    Rigidbody grip;                 // kinematic, follows the monster's hand; the chest hangs from it
    ConfigurableJoint gripJoint;
    Vector3 gripGap;                // chest minus hand at the catch, shrinking to nothing

    void Awake()
    {
        animator = GetComponent<Animator>();
        player = GetComponentInParent<FirstPersonController>();
        skins = GetComponentsInChildren<SkinnedMeshRenderer>(true);
        Build();
    }

    void OnDestroy()
    {
        foreach (var p in parts) AllParts.Remove(p.collider);
        if (grip != null) Destroy(grip.gameObject);
        // While ragdolled the skeleton lives outside the player object; take it with us
        if (Active && hips != null) Destroy(hips.gameObject);
    }

    void Update()
    {
        if (player == null || !player.IsSpawned) return;

        // Caught covers held, dropped and dead, until the player respawns
        bool caught = player.IsGrabbed;
        if (caught && !Active) GoLimp();
        else if (!caught && Active) StandUp();
        if (!Active) return;

        var monster = player.Carrier;
        if (monster != null && gripJoint == null) Grab(monster.GripPosition);
        else if (monster == null && gripJoint != null) LetGo();
    }

    void FixedUpdate()
    {
        if (gripJoint == null) return;
        var monster = player.Carrier;
        if (monster == null) return;
        // Follow the hand exactly; only the gap from where the chest was at the catch eases away
        gripGap *= Mathf.Exp(-gripCatchUp * Time.fixedDeltaTime);
        grip.MovePosition(monster.GripPosition + gripGap);
    }

    // ---------- Switching ----------

    void GoLimp()
    {
        Active = true;
        animator.enabled = false;
        foreach (var s in skins) s.updateWhenOffscreen = true;   // bounds must follow the flying bones

        // Lift the skeleton out of the player object: the player object is pinned to the hand / moved by the network,
        // and dragging the bones along with it would fight the physics
        hipsParent = hips.parent;
        foreach (var p in parts)
        {
            p.localPos = p.bone.localPosition;
            p.localRot = p.bone.localRotation;
        }
        hips.SetParent(null, true);

        foreach (var p in parts)
        {
            p.body.isKinematic = false;
            p.body.interpolation = RigidbodyInterpolation.Interpolate;   // smooth between physics steps
            p.body.linearVelocity = Vector3.zero;
            p.body.angularVelocity = Vector3.zero;
            p.collider.enabled = true;
        }
    }

    void StandUp()
    {
        LetGo();
        foreach (var p in parts)
        {
            p.collider.enabled = false;
            p.body.interpolation = RigidbodyInterpolation.None;   // the animator moves the bones again
            p.body.isKinematic = true;
        }
        hips.SetParent(hipsParent, false);
        foreach (var p in parts)
        {
            p.bone.localPosition = p.localPos;
            p.bone.localRotation = p.localRot;
        }
        foreach (var s in skins) s.updateWhenOffscreen = false;
        animator.enabled = true;   // the next animated frame puts every bone back in pose
        Active = false;
    }

    void Grab(Vector3 hand)
    {
        if (grip == null)
        {
            var go = new GameObject($"{player.name} Grip");
            grip = go.AddComponent<Rigidbody>();
            grip.isKinematic = true;
            grip.interpolation = RigidbodyInterpolation.Interpolate;
        }
        // Start the grip on the chest so nothing snaps; FixedUpdate eases it onto the hand
        gripGap = chest.position - hand;
        grip.position = chest.position;
        grip.rotation = chest.rotation;
        grip.transform.SetPositionAndRotation(chest.position, chest.rotation);

        gripJoint = chest.gameObject.AddComponent<ConfigurableJoint>();
        gripJoint.connectedBody = grip;
        gripJoint.autoConfigureConnectedAnchor = false;
        gripJoint.anchor = Vector3.zero;
        gripJoint.connectedAnchor = Vector3.zero;
        gripJoint.xMotion = gripJoint.yMotion = gripJoint.zMotion = ConfigurableJointMotion.Locked;
        gripJoint.angularXMotion = gripJoint.angularYMotion = gripJoint.angularZMotion = ConfigurableJointMotion.Limited;
        gripJoint.lowAngularXLimit = new SoftJointLimit { limit = -gripSwing };
        gripJoint.highAngularXLimit = new SoftJointLimit { limit = gripSwing };
        gripJoint.angularYLimit = new SoftJointLimit { limit = gripSwing };
        gripJoint.angularZLimit = new SoftJointLimit { limit = gripSwing };
        gripJoint.projectionMode = JointProjectionMode.PositionAndRotation;   // never stretches, even when yanked
    }

    void LetGo()
    {
        if (gripJoint != null) Destroy(gripJoint);
        gripJoint = null;
    }

    // ---------- Building the ragdoll from the humanoid skeleton ----------

    void Build()
    {
        Transform B(HumanBodyBones b) => animator.GetBoneTransform(b);
        hips = B(HumanBodyBones.Hips);
        Transform spineTop = B(HumanBodyBones.Chest) ?? B(HumanBodyBones.Spine);
        Transform head = B(HumanBodyBones.Head);
        Transform lUpLeg = B(HumanBodyBones.LeftUpperLeg), rUpLeg = B(HumanBodyBones.RightUpperLeg);
        Transform lLeg = B(HumanBodyBones.LeftLowerLeg), rLeg = B(HumanBodyBones.RightLowerLeg);
        Transform lArm = B(HumanBodyBones.LeftUpperArm), rArm = B(HumanBodyBones.RightUpperArm);
        Transform lFore = B(HumanBodyBones.LeftLowerArm), rFore = B(HumanBodyBones.RightLowerArm);
        if (hips == null || spineTop == null || head == null || lUpLeg == null || rUpLeg == null || lLeg == null ||
            rLeg == null || lArm == null || rArm == null || lFore == null || rFore == null)
        {
            Debug.LogWarning("PlayerRagdoll: the model isn't a full humanoid, no ragdoll");
            enabled = false;
            return;
        }

        Vector3 up = transform.up, fwd = transform.forward;
        Transform neck = B(HumanBodyBones.Neck) ?? head;
        float hipWidth = Vector3.Distance(lUpLeg.position, rUpLeg.position);
        float shoulderWidth = Vector3.Distance(lArm.position, rArm.position);

        // Pelvis: from the hips up to the chest
        Vector3 spineVec = spineTop.position - hips.position;
        var pelvis = AddBox(hips, 0.16f, hips.position + spineVec * 0.3f,
            transform.right * hipWidth * 1.7f, spineVec.magnitude * 1.3f * up, fwd * 0.22f);

        // Chest: from the chest bone up to the neck, shoulder to shoulder
        Vector3 chestVec = neck.position - spineTop.position;
        chest = AddBox(spineTop, 0.26f, spineTop.position + chestVec * 0.5f,
            transform.right * shoulderWidth, chestVec.magnitude * up, fwd * 0.24f);
        AddBall(chest, pelvis, up, 15f, 20f);

        // Head
        var headBody = AddSphere(head, 0.08f, head.position + up * 0.09f, 0.11f);
        AddBall(headBody, chest, up, 35f, 35f);

        // Arms and legs
        LimbPair(lArm, lFore, B(HumanBodyBones.LeftHand), chest, 0.045f, 0.035f, 0.025f, 0.02f, true);
        LimbPair(rArm, rFore, B(HumanBodyBones.RightHand), chest, 0.045f, 0.035f, 0.025f, 0.02f, true);
        LimbPair(lUpLeg, lLeg, B(HumanBodyBones.LeftFoot), pelvis, 0.075f, 0.055f, 0.12f, 0.06f, false);
        LimbPair(rUpLeg, rLeg, B(HumanBodyBones.RightFoot), pelvis, 0.075f, 0.055f, 0.12f, 0.06f, false);

        // No self-collisions: limbs pass through the body instead of jamming against it
        for (int i = 0; i < parts.Count; i++)
            for (int j = i + 1; j < parts.Count; j++)
                Physics.IgnoreCollision(parts[i].collider, parts[j].collider);
        // Nor with our own player capsule
        var capsule = player != null ? player.GetComponent<CharacterController>() : null;
        if (capsule != null) foreach (var p in parts) Physics.IgnoreCollision(p.collider, capsule);
    }

    // Upper + lower arm or leg. Elbows bend forward, knees bend back.
    void LimbPair(Transform upper, Transform lower, Transform end, Rigidbody parent,
                  float upperRadius, float lowerRadius, float upperMass, float lowerMass, bool arm)
    {
        Vector3 endPos = end != null ? end.position : lower.position + (lower.position - upper.position);
        if (!arm) endPos += (endPos - lower.position).normalized * 0.05f;   // down to the sole
        var u = AddCapsule(upper, upperMass, upper.position, lower.position, upperRadius);
        var l = AddCapsule(lower, lowerMass, lower.position, endPos, lowerRadius);

        Vector3 dir = (lower.position - upper.position).normalized;
        AddBall(u, parent, dir, arm ? 70f : 30f, arm ? 80f : 60f);

        // Hinge across the limb; find which way round bends it the right way
        Vector3 lowerDir = (endPos - lower.position).normalized;
        Vector3 axis = Vector3.Cross(lowerDir, transform.forward).normalized;
        if (axis.sqrMagnitude < 0.01f) axis = transform.right;
        bool positiveBendsForward = Vector3.Dot(Quaternion.AngleAxis(30f, axis) * lowerDir, transform.forward) > 0f;
        bool positiveIsRight = positiveBendsForward == arm;   // elbows forward, knees back
        var hinge = l.gameObject.AddComponent<HingeJoint>();
        hinge.connectedBody = u;
        hinge.axis = lower.InverseTransformDirection(axis);
        hinge.useLimits = true;
        hinge.limits = positiveIsRight ? new JointLimits { min = -3f, max = 130f } : new JointLimits { min = -130f, max = 3f };
    }

    // ---------- Pieces ----------

    Rigidbody AddBody(Transform bone, float massShare, Collider col)
    {
        var rb = bone.gameObject.AddComponent<Rigidbody>();
        rb.mass = totalMass * massShare;
        rb.linearDamping = drag;
        rb.angularDamping = angularDrag;
        rb.isKinematic = true;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;   // dropped from 10 m, no tunnelling
        rb.solverIterations = 12;
        col.enabled = false;
        parts.Add(new Part { bone = bone, body = rb, collider = col });
        AllParts.Add(col);
        return rb;
    }

    // A box in the bone's own axes that spans the three world-space edges given
    Rigidbody AddBox(Transform bone, float massShare, Vector3 worldCenter, Vector3 a, Vector3 b, Vector3 c)
    {
        var box = bone.gameObject.AddComponent<BoxCollider>();
        box.center = bone.InverseTransformPoint(worldCenter);
        box.size = Abs(bone.InverseTransformVector(a)) + Abs(bone.InverseTransformVector(b)) + Abs(bone.InverseTransformVector(c));
        return AddBody(bone, massShare, box);
    }

    Rigidbody AddSphere(Transform bone, float massShare, Vector3 worldCenter, float radius)
    {
        var sphere = bone.gameObject.AddComponent<SphereCollider>();
        sphere.center = bone.InverseTransformPoint(worldCenter);
        sphere.radius = radius / Mathf.Max(bone.lossyScale.x, 0.0001f);
        return AddBody(bone, massShare, sphere);
    }

    // A capsule from one point to another, along whichever of the bone's axes points that way
    Rigidbody AddCapsule(Transform bone, float massShare, Vector3 from, Vector3 to, float radius)
    {
        var cap = bone.gameObject.AddComponent<CapsuleCollider>();
        Vector3 local = bone.InverseTransformVector(to - from);
        Vector3 abs = Abs(local);
        cap.direction = abs.x > abs.y && abs.x > abs.z ? 0 : abs.y > abs.z ? 1 : 2;
        float scale = Mathf.Max(bone.lossyScale.x, 0.0001f);
        cap.center = bone.InverseTransformPoint((from + to) * 0.5f);
        cap.radius = radius / scale;
        cap.height = local.magnitude + cap.radius * 2f;
        return AddBody(bone, massShare, cap);
    }

    // Ball-and-socket joint: twist around the bone, swing either way
    static void AddBall(Rigidbody child, Rigidbody parent, Vector3 worldAxis, float twist, float swing)
    {
        var joint = child.gameObject.AddComponent<CharacterJoint>();
        joint.connectedBody = parent;
        Vector3 axis = child.transform.InverseTransformDirection(worldAxis);
        joint.axis = axis;
        joint.swingAxis = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        joint.lowTwistLimit = new SoftJointLimit { limit = -twist };
        joint.highTwistLimit = new SoftJointLimit { limit = twist };
        joint.swing1Limit = new SoftJointLimit { limit = swing };
        joint.swing2Limit = new SoftJointLimit { limit = swing };
        joint.enableProjection = true;
    }

    static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
}
