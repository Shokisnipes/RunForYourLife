using UnityEngine;

// On the Scout's Model child; runs on every client. Drives the crawl from how fast the scout really moves (so the
// hands and feet keep up with the ground), the wall climbs, the relay screech (it rears its head up) and its death.
// The death clip (Mixamo "Prone Death") was made for a soldier lying with a rifle, so both hands stick out in front
// of the face. While dying, hand IK puts the hands flat on the ground beside the shoulders instead, the way they
// sit in the crawl, and the IK fades in over the same time as the blend from the crawl.
// It also wears its genome (DESIGN: what a dead one has reveals the monster's traits): a Sight scout has big eyes
// that catch the flashlight; a Hearing scout has large ears and small milky, blind eyes.
// The Animator Controller comes from Tools > RunForYourLife > Scout Setup.
[RequireComponent(typeof(Animator))]
public class ScoutAnimator : MonoBehaviour
{
    [Header("Crawl")]
    [Tooltip("How fast the crawl clip moves (m/s at playback 1, at this model's scale). Measured by Scout Setup.")]
    public float crawlClipSpeed = 0.5f;
    [Tooltip("Playback when standing still: a slow breathing sway instead of a frozen mid-step.")]
    public float idlePlayback = 0.08f;
    [Tooltip("Never plays the crawl faster than this.")]
    public float maxPlayback = 4f;
    [Tooltip("The crawl clip dips a little below its own floor; the body is lifted this much (m) to keep knees and feet above ground. Measured by Scout Setup.")]
    public float crawlLift = 0.03f;
    [Tooltip("Same for the death clip's final pose (lying flat).")]
    public float deathLift = 0.025f;

    [Header("Screech")]
    [Tooltip("How far (degrees) the head tips up while it screeches.")]
    public float screechHeadAngle = 45f;

    [Header("Death")]
    [Tooltip("How long the crawl takes to blend into the death clip (also how fast the arm fix fades in).")]
    public float deathBlend = 0.35f;
    [Tooltip("Hands go this far out to the side of each shoulder, in shoulder widths.")]
    public float deathHandSide = 0.6f;
    [Tooltip("...and this far in front of the shoulders, in shoulder widths.")]
    public float deathHandForward = 0.5f;
    [Tooltip("How much the arm fix wins over the clip (1 = hands exactly where they're put).")]
    [Range(0f, 1f)] public float deathArmWeight = 1f;

    [Header("Genome features")]
    [Tooltip("Sight scout: eye size (fraction of the head's height, exaggerated so they read at a distance) and colour; " +
             "they glint like eyeshine.")]
    public float sightEyeSize = 0.42f;
    public Color sightEyeColor = new Color(0.85f, 0.7f, 0.15f);
    public Color sightEyeGlow = new Color(0.9f, 0.6f, 0.05f);
    [Tooltip("Hearing scout: ear length (fraction of the head's height), and its small blind eyes.")]
    public float hearingEarSize = 1.6f;
    public float hearingEyeSize = 0.16f;
    public Color blindEyeColor = new Color(0.82f, 0.84f, 0.8f);

    /// <summary>Set by the scout's AI (or from its synced state on clients).</summary>
    [System.NonSerialized] public Vector3 velocity;
    [System.NonSerialized] public int climb;          // 0 ground, 1 climbing up, -1 climbing down
    [System.NonSerialized] public bool screeching;

    public bool Dead { get; private set; }

    Animator animator;
    float armWeight, screech, deathT;
    int senseToShow = -1;   // 0 sight, 1 hearing: built once the crawl has posed the head
    int framesAnimated;
    Transform leftShoulder, rightShoulder;

    static readonly int PlaybackId = Animator.StringToHash("Playback");
    static readonly int ClimbId = Animator.StringToHash("Climb");
    static readonly int DeadId = Animator.StringToHash("Dead");

    void Awake()
    {
        animator = GetComponent<Animator>();
        animator.applyRootMotion = false;
        leftShoulder = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        rightShoulder = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
    }

    /// <summary>Every client: give it the eyes or ears of its genome.</summary>
    public void ShowSense(bool sight) => senseToShow = sight ? 0 : 1;

    void LateUpdate()
    {
        if (senseToShow < 0 || ++framesAnimated < 3) return;   // wait for a real pose (the prefab's is a leftover)
        BuildSense(senseToShow == 0);
        senseToShow = -1;
    }

    // Parts on the head bone, placed from the head's own shape: up the skull (head -> head top), and the scout's
    // forward flattened onto that.
    void BuildSense(bool sight)
    {
        var head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (head == null) return;
        Transform top = null;
        foreach (var t in head.GetComponentsInChildren<Transform>())
            if (t.name.Contains("HeadTop")) { top = t; break; }
        var neck = animator.GetBoneTransform(HumanBodyBones.Neck);
        Vector3 up = top != null ? top.position - head.position : head.position - (neck != null ? neck.position : head.position - Vector3.up * 0.05f);
        float h = top != null ? up.magnitude : up.magnitude * 1.4f;
        if (h < 0.001f) return;
        up /= up.magnitude;
        Vector3 fwd = Vector3.ProjectOnPlane(transform.parent != null ? transform.parent.forward : transform.forward, up).normalized;
        Vector3 right = Vector3.Cross(up, fwd);
        Vector3 mid = head.position + up * h * 0.5f;

        var skin = GetComponentInChildren<SkinnedMeshRenderer>();
        Material eyeMat = sight
            ? BuildingBuilder.Fallback("Scout_EyeSight", sightEyeColor, sightEyeGlow, 0.95f)
            : BuildingBuilder.Fallback("Scout_EyeBlind", blindEyeColor, Color.black, 0.25f);
        float eye = (sight ? sightEyeSize : hearingEyeSize) * h;
        for (int side = -1; side <= 1; side += 2)
            Part("Scout_Eye", head, mid + fwd * h * 0.62f + right * side * h * 0.28f + up * h * 0.1f, Quaternion.LookRotation(fwd, up),
                 new Vector3(eye, eye, eye * 0.8f), eyeMat);

        if (sight) return;
        var earMat = skin != null && skin.sharedMaterial != null ? skin.sharedMaterial : eyeMat;
        float ear = hearingEarSize * h;
        for (int side = -1; side <= 1; side += 2)
        {
            // Long, thin, flat ears swept up and back off the sides of the skull, like a bat's
            Quaternion rot = Quaternion.LookRotation(fwd, up) * Quaternion.Euler(-25f, 0f, side * -35f);
            Part("Scout_Ear", head, mid + right * side * h * 0.55f + up * h * 0.6f - fwd * h * 0.1f, rot,
                 new Vector3(ear * 0.12f, ear, ear * 0.5f), earMat);
        }
    }

    static void Part(string name, Transform parent, Vector3 pos, Quaternion rot, Vector3 size, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        Object.Destroy(go.GetComponent<Collider>());
        go.transform.SetPositionAndRotation(pos, rot);
        go.transform.localScale = size;
        go.transform.SetParent(parent, true);
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    public void Die()
    {
        if (Dead) return;
        Dead = true;
        animator.SetBool(DeadId, true);
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (Dead)
        {
            animator.SetFloat(PlaybackId, 1f);
            armWeight = Mathf.MoveTowards(armWeight, deathArmWeight, dt / Mathf.Max(0.01f, deathBlend));
            deathT = Mathf.MoveTowards(deathT, 1f, dt / Mathf.Max(0.01f, deathBlend));
            transform.localPosition = new Vector3(0f, Mathf.Lerp(crawlLift, deathLift, deathT), 0f);
            screech = 0f;
            return;
        }

        transform.localPosition = new Vector3(0f, crawlLift, 0f);
        float speed = new Vector3(velocity.x, 0f, velocity.z).magnitude;
        if (climb != 0) speed = Mathf.Abs(velocity.y);
        float playback = climb != 0 ? Mathf.Clamp(speed / 0.6f, 0.5f, maxPlayback)
                       : Mathf.Clamp(speed / Mathf.Max(0.05f, crawlClipSpeed), idlePlayback, maxPlayback);
        animator.SetFloat(PlaybackId, screeching ? 0f : playback, 0.1f, dt);
        animator.SetInteger(ClimbId, climb);
        screech = Mathf.MoveTowards(screech, screeching ? 1f : 0f, dt * 4f);
    }

    void OnAnimatorIK(int layer)
    {
        // Screech: the head tips back and up toward the sky
        if (screech > 0f)
        {
            Vector3 head = animator.GetBoneTransform(HumanBodyBones.Head).position;
            Vector3 dir = Quaternion.AngleAxis(-screechHeadAngle, transform.right) * transform.forward;
            animator.SetLookAtWeight(screech, 0.3f, 1f, 0f, 0.6f);
            animator.SetLookAtPosition(head + dir * 2f);
        }
        else animator.SetLookAtWeight(0f);

        if (armWeight <= 0f || leftShoulder == null || rightShoulder == null) return;
        PlaceHand(AvatarIKGoal.LeftHand, leftShoulder.position, -1f);
        PlaceHand(AvatarIKGoal.RightHand, rightShoulder.position, 1f);
    }

    // Puts one hand flat on the ground out beside its shoulder, elbow pointing out and back like in the crawl
    void PlaceHand(AvatarIKGoal goal, Vector3 shoulder, float side)
    {
        float width = Vector3.Distance(leftShoulder.position, rightShoulder.position);
        Vector3 flatForward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        Vector3 flatRight = Vector3.Cross(Vector3.up, flatForward);
        Vector3 target = shoulder + (flatRight * side * deathHandSide + flatForward * deathHandForward) * width;

        // Down onto whatever it's lying on (the floor under it, never through it)
        float floor = transform.position.y;
        if (Physics.Raycast(target + Vector3.up * width, Vector3.down, out var hit, width * 4f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            floor = hit.point.y;
        target.y = floor + width * 0.08f;

        animator.SetIKPositionWeight(goal, armWeight);
        animator.SetIKRotationWeight(goal, armWeight * 0.7f);
        animator.SetIKPosition(goal, target);
        animator.SetIKRotation(goal, Quaternion.LookRotation(flatForward + flatRight * side * 0.4f, Vector3.up));

        var hint = goal == AvatarIKGoal.LeftHand ? AvatarIKHint.LeftElbow : AvatarIKHint.RightElbow;
        animator.SetIKHintPositionWeight(hint, armWeight);
        animator.SetIKHintPosition(hint, shoulder + (flatRight * side * 1.2f - flatForward * 0.3f + Vector3.up * 0.3f) * width);
    }
}
