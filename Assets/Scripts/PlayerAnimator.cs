using UnityEngine;

// Animates the player's body (the Model child of the Player prefab). Runs on every client:
// movement is read from how the player object actually moves, so it works the same for your own player
// and for other players (whose position comes over the network).
// Your own player: the head is shrunk away (you look out from where it would be) and the body sits a bit
// behind the camera, so looking down shows your arms, chest and feet instead of the inside of your neck.
// Other players: the head and upper body turn to where they're looking (synced pitch).
// In cover (PlayerCover) the body turns its back to the wall and plays the cover sneak.
// The Animator Controller and the natural clip speeds come from Tools > RunForYourLife > Player Model Setup.
[RequireComponent(typeof(Animator))]
public class PlayerAnimator : MonoBehaviour
{
    [Header("Fastest clip in each direction (m/s at playback 1; measured by Player Model Setup)")]
    [Tooltip("Standing: fastest forward clip (Running).")]
    public float standForwardSpeed = 3.1f;
    [Tooltip("Standing: fastest backward clip.")]
    public float standBackSpeed = 1.7f;
    [Tooltip("Standing: fastest sideways clip.")]
    public float standSideSpeed = 1.5f;
    [Tooltip("Crouched: fastest forward clip.")]
    public float crouchForwardSpeed = 1.6f;
    [Tooltip("Crouched: fastest backward clip.")]
    public float crouchBackSpeed = 1.6f;
    [Tooltip("Crouched: fastest sideways clip.")]
    public float crouchSideSpeed = 1.2f;
    [Tooltip("In cover: how fast the cover sneak moves along the wall.")]
    public float coverSideSpeed = 0.7f;

    [Header("Cover")]
    [Tooltip("How quickly the body turns its back to the wall (and back again).")]
    public float coverTurnSpeed = 8f;

    [Header("Feel")]
    [Tooltip("Seconds to smooth the movement input to the animator (higher = softer starts and stops).")]
    public float moveDamping = 0.12f;
    [Tooltip("Above this speed (m/s) the player counts as moving.")]
    public float moveThreshold = 0.15f;
    [Tooltip("Clips never play faster than this (past it the feet slide a little instead of looking frantic).")]
    public float maxPlayback = 2f;

    [Header("First person (your own player only)")]
    [Tooltip("How far (m) the body sits behind the camera when standing.")]
    public float standBackOffset = 0.18f;
    [Tooltip("How far (m) the body sits behind the camera when crouched.")]
    public float crouchBackOffset = 0.3f;
    [Tooltip("Extra distance (m) the body slides back when looking straight down, so you don't see into the collar.")]
    public float lookDownBackOffset = 0.25f;
    [Tooltip("Looking down past this angle (degrees) starts sliding the body back.")]
    public float lookDownStartAngle = 45f;

    [Header("Look (other players)")]
    [Tooltip("How much the upper body bends toward where they're looking (0-1).")]
    public float bodyLookWeight = 0.3f;
    [Tooltip("How much the head turns toward where they're looking (0-1).")]
    public float headLookWeight = 0.8f;

    static readonly int MoveXId = Animator.StringToHash("MoveX");
    static readonly int MoveZId = Animator.StringToHash("MoveZ");
    static readonly int CrouchId = Animator.StringToHash("Crouch");
    static readonly int CoverId = Animator.StringToHash("Cover");

    Animator animator;
    FirstPersonController player;
    PlayerCover cover;
    Transform head;
    Vector3 lastPos;
    Vector3 smoothVelocity;
    float crouchBlend;

    void Awake()
    {
        animator = GetComponent<Animator>();
        animator.applyRootMotion = false;
        player = GetComponentInParent<FirstPersonController>();
        cover = GetComponentInParent<PlayerCover>();
        // Shrinking from the neck (not the head) folds the collar away too, so looking down shows no stump
        head = animator.GetBoneTransform(HumanBodyBones.Neck);
        if (head == null) head = animator.GetBoneTransform(HumanBodyBones.Head);

        // Bodies never cast shadows, otherwise they block their own flashlight
        foreach (var r in GetComponentsInChildren<Renderer>(true))
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    void OnEnable() => lastPos = player != null ? player.transform.position : transform.position;

    bool IsMine => player != null && player.IsSpawned && player.IsOwner;

    void Update()
    {
        if (player == null) return;
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // Our own player keeps animating even when the body is off screen (we only see bits of it)
        animator.cullingMode = IsMine ? AnimatorCullingMode.AlwaysAnimate : AnimatorCullingMode.CullUpdateTransforms;

        // Velocity from how far the player moved this frame (works for remote players too)
        Vector3 root = player.transform.position;
        Vector3 v = (root - lastPos) / dt;
        lastPos = root;
        v.y = 0f;
        if (player.IsGrabbed || player.IsDead || v.sqrMagnitude > 15f * 15f) v = Vector3.zero;   // carried or teleported
        smoothVelocity = Vector3.Lerp(smoothVelocity, v, 1f - Mathf.Exp(-12f * dt));

        // In cover the body turns its back to the wall (faces out of it). Otherwise it faces where the player faces.
        bool inCover = cover != null && cover.InCover;
        Quaternion facing = inCover
            ? Quaternion.Inverse(player.transform.rotation) * Quaternion.LookRotation(cover.WallNormal)
            : Quaternion.identity;
        transform.localRotation = Quaternion.Slerp(transform.localRotation, facing, 1f - Mathf.Exp(-coverTurnSpeed * dt));

        // Velocity in the body's own frame (the blend trees are laid out that way)
        Vector3 local = transform.InverseTransformDirection(smoothVelocity);
        Vector2 flat = new Vector2(local.x, local.z);
        float speed = flat.magnitude;
        bool crouched = player.IsCrouching;

        // The blend trees have clips placed at the velocities they really move at. Up to the fastest clip in this
        // direction we blend; beyond it we play that clip faster, so the feet keep up with the ground.
        Vector2 dir = speed > moveThreshold ? flat / speed : Vector2.zero;
        float edge = inCover ? coverSideSpeed
                   : crouched ? EdgeSpeed(dir, crouchForwardSpeed, crouchBackSpeed, crouchSideSpeed)
                   : EdgeSpeed(dir, standForwardSpeed, standBackSpeed, standSideSpeed);
        float blendSpeed = speed > moveThreshold ? Mathf.Min(speed, edge) : 0f;
        float playback = speed > edge && edge > 0.01f ? Mathf.Min(speed / edge, maxPlayback) : 1f;

        animator.SetFloat(MoveXId, dir.x * blendSpeed, moveDamping, dt);
        animator.SetFloat(MoveZId, dir.y * blendSpeed, moveDamping, dt);
        animator.SetBool(CrouchId, crouched);
        animator.SetBool(CoverId, inCover);
        animator.speed = Mathf.Lerp(animator.speed, playback, 1f - Mathf.Exp(-8f * dt));

        crouchBlend = Mathf.MoveTowards(crouchBlend, crouched ? 1f : 0f, dt * 4f);
    }

    // Fastest clip speed in this direction, blended between the forward/backward and sideways ones
    static float EdgeSpeed(Vector2 dir, float forward, float back, float side)
    {
        float ax = Mathf.Abs(dir.x), az = Mathf.Abs(dir.y);
        if (ax + az < 0.001f) return forward;
        float along = dir.y >= 0f ? forward : back;
        return (az * along + ax * side) / (ax + az);
    }

    void LateUpdate()
    {
        if (player == null) return;
        bool mine = IsMine;

        // Our own head would fill the screen: shrink it (hair and eyelashes are skinned to it, so they go too).
        // Run in LateUpdate so it's after the Animator posed the bones; the IK look doesn't scale it back.
        if (head != null) head.localScale = mine ? Vector3.one * 0.001f : Vector3.one;

        float back = 0f;
        if (mine)
        {
            float pitch = Mathf.DeltaAngle(0f, player.cameraPivot.localEulerAngles.x);   // + = looking down
            float down = Mathf.InverseLerp(lookDownStartAngle, player.maxPitch, pitch);
            back = Mathf.Lerp(standBackOffset, crouchBackOffset, crouchBlend) + lookDownBackOffset * down;
        }
        transform.localPosition = new Vector3(0f, 0f, -back);
    }

    // Head and upper body follow where the player looks (needs IK Pass on the base layer)
    void OnAnimatorIK(int layerIndex)
    {
        if (player == null || player.cameraPivot == null) return;
        Transform cam = player.cameraPivot;
        bool mine = IsMine;
        // Our own body doesn't bend toward the camera (it would lean into the view); the head is hidden anyway
        animator.SetLookAtWeight(1f, mine ? 0f : bodyLookWeight, mine ? 0f : headLookWeight, 0f, 0.5f);
        animator.SetLookAtPosition(cam.position + cam.forward * 10f);
    }
}
