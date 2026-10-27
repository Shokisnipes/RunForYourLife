using UnityEngine;

// The flashlight in the player's right hand. On the Player's Model child, runs on every client.
//  - A small flashlight (built from simple shapes, no model needed) sits in the right fist, fingers closed round it.
//  - Switched on, the arm comes up and points it where the player looks (arm IK toward a point in front of the
//    camera), so everyone sees the beam coming from the hand. Switched off, the arm hangs and the animation plays.
//  - The Flashlight's Spot Light (a child of CameraPivot) is moved to the lens every frame; it still aims where you
//    look, with its own lag (Flashlight.followSpeed).
// Runs after PlayerAnimator, which moves the body behind the camera in its LateUpdate.
[DefaultExecutionOrder(100)]
[RequireComponent(typeof(Animator))]
public class FlashlightHand : MonoBehaviour
{
    [Header("Aiming (flashlight on)")]
    [Tooltip("Where the hand goes, from the camera: x = right, y = up, z = forward (m).")]
    public Vector3 aimOffset = new Vector3(0.2f, -0.24f, 0.38f);
    [Tooltip("Where the elbow points, from the camera (x = right, y = up, z = forward).")]
    public Vector3 elbowHint = new Vector3(0.45f, -0.6f, -0.1f);
    [Tooltip("Seconds for the arm to come up when it's switched on (and drop when switched off).")]
    public float raiseTime = 0.25f;

    [Header("Grip")]
    [Tooltip("How far each finger joint curls round the flashlight (knuckle, middle, tip; degrees).")]
    public Vector3 fingerCurl = new Vector3(65f, 75f, 45f);
    [Tooltip("Same for the thumb.")]
    public Vector3 thumbCurl = new Vector3(15f, 25f, 25f);
    [Tooltip("Where the flashlight sits in the fist: along the palm toward the fingers (0 = wrist, 1 = knuckles).")]
    [Range(0f, 1.5f)] public float alongPalm = 0.85f;
    [Tooltip("How far it sits in front of the palm (m).")]
    public float palmDepth = 0.035f;

    [Header("Look")]
    [Tooltip("Body length (m). The head and lens are added at the front.")]
    public float length = 0.16f;
    [Tooltip("Body thickness (m).")]
    public float thickness = 0.032f;
    public Color bodyColour = new Color(0.12f, 0.12f, 0.13f);

    static readonly HumanBodyBones[] Fingers =
    {
        HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal,
        HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal,
        HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal,
        HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal,
        HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal,
    };

    Animator animator;
    FirstPersonController player;
    Flashlight flashlight;
    PlayerRagdoll ragdoll;
    Transform hand, index, middle, little;
    Transform prop, lens;
    Material bodyMaterial, lensMaterial;
    float raise;

    void Awake()
    {
        animator = GetComponent<Animator>();
        player = GetComponentInParent<FirstPersonController>();
        ragdoll = GetComponent<PlayerRagdoll>();
        if (player != null) flashlight = player.GetComponentInChildren<Flashlight>(true);
        if (!animator.isHuman) return;
        hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        index = animator.GetBoneTransform(HumanBodyBones.RightIndexProximal);
        middle = animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
        little = animator.GetBoneTransform(HumanBodyBones.RightLittleProximal);
        if (hand != null) BuildProp();
    }

    void OnDestroy()
    {
        if (bodyMaterial != null) Destroy(bodyMaterial);
        if (lensMaterial != null) Destroy(lensMaterial);
    }

    bool On => flashlight != null && flashlight.IsOn;
    bool Limp => ragdoll != null && ragdoll.Active;

    void Update()
    {
        bool up = On && !Limp && player != null && !player.IsGrabbed && !player.IsDead;
        raise = Mathf.MoveTowards(raise, up ? 1f : 0f, Time.deltaTime / Mathf.Max(0.01f, raiseTime));
        if (lensMaterial != null) lensMaterial.SetColor("_EmissionColor", On ? Color.white * 3f : Color.black);
    }

    // Arm up toward a point in front of the camera
    void OnAnimatorIK(int layerIndex)
    {
        if (layerIndex != 0 || player == null || player.cameraPivot == null) return;
        float w = Mathf.SmoothStep(0f, 1f, raise);
        Transform cam = player.cameraPivot;
        animator.SetIKPositionWeight(AvatarIKGoal.RightHand, w);
        animator.SetIKPosition(AvatarIKGoal.RightHand, cam.TransformPoint(aimOffset));
        animator.SetIKHintPositionWeight(AvatarIKHint.RightElbow, w);
        animator.SetIKHintPosition(AvatarIKHint.RightElbow, cam.TransformPoint(elbowHint));
    }

    void LateUpdate()
    {
        if (prop == null || index == null || middle == null || little == null) return;

        if (!Limp)
        {
            // Point the fist (thumb side, where the flashlight comes out) where you look
            float w = Mathf.SmoothStep(0f, 1f, raise);
            if (w > 0f && player != null && player.cameraPivot != null)
            {
                Vector3 across = index.position - little.position;
                Quaternion turn = Quaternion.FromToRotation(across, player.cameraPivot.forward);
                hand.rotation = Quaternion.Slerp(Quaternion.identity, turn, w) * hand.rotation;
            }
            CloseFingers();
            PlaceProp();
        }

        // The beam comes out of the lens (its aim is the Flashlight's own)
        if (flashlight != null) flashlight.transform.position = lens.position;
    }

    // Fingers wrapped round the handle, every frame after the animation
    void CloseFingers()
    {
        Vector3 palm = Palm();
        for (int f = 0; f < 5; f++)
        {
            Vector3 curl = f == 4 ? thumbCurl : fingerCurl;
            for (int j = 0; j < 3; j++)
            {
                Transform bone = animator.GetBoneTransform(Fingers[f * 3 + j]);
                if (bone == null) continue;
                Vector3 dir = bone.childCount > 0 ? bone.GetChild(0).position - bone.position : bone.position - bone.parent.position;
                Vector3 axis = Vector3.Cross(dir, palm);   // turning about this swings the joint toward the palm
                if (axis.sqrMagnitude < 1e-8f) continue;
                bone.rotation = Quaternion.AngleAxis(curl[j], axis.normalized) * bone.rotation;
            }
        }
    }

    // Which way the right palm faces (the knuckles don't move when the fingers curl)
    Vector3 Palm() => -Vector3.Cross(index.position - little.position, middle.position - hand.position).normalized;

    // In the fist, across the palm, sticking out on the thumb side
    void PlaceProp()
    {
        Vector3 across = (index.position - little.position).normalized;
        Vector3 grip = Vector3.Lerp(hand.position, middle.position, alongPalm) + Palm() * palmDepth;
        prop.SetPositionAndRotation(grip, Quaternion.LookRotation(across, middle.position - hand.position));
    }

    void BuildProp()
    {
        // Kept on the hand bone, so it stays in the hand when the body goes limp
        prop = new GameObject("Flashlight Prop").transform;
        prop.SetParent(hand, false);

        var body = bodyMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = bodyColour };
        body.SetFloat("_Smoothness", 0.6f);
        body.SetFloat("_Metallic", 0.4f);
        lensMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = new Color(0.9f, 0.9f, 0.8f) };
        lensMaterial.EnableKeyword("_EMISSION");

        // Handle (centred in the fist), wider head, glowing lens
        Part("Handle", body, new Vector3(0f, 0f, length * 0.1f), new Vector3(thickness, length * 0.5f, thickness));
        Part("Head", body, new Vector3(0f, 0f, length * 0.6f + 0.02f), new Vector3(thickness * 1.5f, 0.025f, thickness * 1.5f));
        Part("Lens", lensMaterial, new Vector3(0f, 0f, length * 0.6f + 0.046f), new Vector3(thickness * 1.3f, 0.002f, thickness * 1.3f));
        lens = new GameObject("Lens Point").transform;
        lens.SetParent(prop, false);
        lens.localPosition = new Vector3(0f, 0f, length * 0.6f + 0.05f);
    }

    // A cylinder along the prop's forward (z), no collider (it mustn't touch physics, the NavMesh or sight lines)
    void Part(string name, Material mat, Vector3 position, Vector3 scale)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = name;
        DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(prop, false);
        go.transform.localPosition = position;
        go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        go.transform.localScale = scale;
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }
}
