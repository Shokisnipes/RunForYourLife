using Unity.Netcode;
using UnityEngine;

// Taking cover against walls. Lives on the Player prefab next to FirstPersonController.
// Crouch next to a wall and you slide into cover: your back goes to the wall (PlayerAnimator turns the body and
// plays the cover sneak), and the view eases so you can't stare into the wall, but the mouse stays free.
// At the end of a wall, keep moving toward the edge and your head leans out past the corner to peek.
// The owner works it all out; the cover wall and the peek are synced so others see you lean out.
[DefaultExecutionOrder(50)]   // after FirstPersonController has placed the camera this frame
public class PlayerCover : NetworkBehaviour
{
    [Header("Finding a wall")]
    [Tooltip("Slide into cover when a wall is this close to your centre (m). The capsule radius is 0.5.")]
    public float enterDistance = 0.75f;
    [Tooltip("Leave cover once the wall is further than this (m).")]
    public float exitDistance = 1f;
    [Tooltip("Height (m) above your feet the wall is looked for (crouched chest height).")]
    public float probeHeight = 0.5f;
    [Tooltip("Speed along the wall compared to crouch-walking (the cover sneak clip moves ~0.9 m/s).")]
    public float coverSpeedScale = 0.6f;

    [Header("View")]
    [Tooltip("How far (degrees) you can look away from straight out of the wall. 90 = along the wall.")]
    public float maxLookAngle = 100f;
    [Tooltip("How quickly the view eases back when it points into the wall.")]
    public float lookEaseSpeed = 6f;

    [Header("Peeking")]
    [Tooltip("The wall counts as ending when there's no wall this far (m) to the side of your centre.")]
    public float edgeProbe = 0.35f;
    [Tooltip("How far (m) your head leans out past the corner.")]
    public float peekDistance = 0.45f;
    [Tooltip("Degrees the view tilts while leaning out.")]
    public float peekRoll = 8f;
    [Tooltip("How fast you lean out and back (full lean per second).")]
    public float peekSpeed = 3f;
    [Tooltip("While leaning out, how far (degrees from straight out of the wall) you can look on the corner side. " +
             "180 = straight back along the wall's other face, around the corner.")]
    public float peekLookAngle = 175f;

    // Owner-written, read by everyone (PlayerAnimator turns the body; the camera lean is shown on other players)
    readonly NetworkVariable<bool> netInCover = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    readonly NetworkVariable<float> netWallYaw = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    readonly NetworkVariable<float> netPeek = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    FirstPersonController player;
    CharacterController controller;
    bool inCover;
    Vector3 wallNormal = Vector3.forward;   // flat, pointing out of the wall toward us
    float wallDistance;
    bool edgeRight, edgeLeft;               // the wall ends on that side (right = along WallRight)
    float peek;                             // -1 = leaning out left, +1 = right
    Vector3 lastMove;

    public bool InCover => IsOwner ? inCover : netInCover.Value;
    public Vector3 WallNormal => IsOwner ? wallNormal : Quaternion.Euler(0f, netWallYaw.Value, 0f) * Vector3.forward;
    // Your right while your back is to the wall
    public Vector3 WallRight => Vector3.Cross(Vector3.up, WallNormal);
    public float Peek => IsOwner ? peek : netPeek.Value;

    void Awake()
    {
        player = GetComponent<FirstPersonController>();
        controller = GetComponent<CharacterController>();
    }

    // Called by FirstPersonController.Move: in cover you sneak slower, and you stop at the end of the wall
    // (pushing on just makes you lean out instead)
    public Vector3 FilterMove(Vector3 move)
    {
        lastMove = move;
        if (!inCover) return move;
        float along = Vector3.Dot(move, WallRight);
        if ((edgeRight && along > 0f) || (edgeLeft && along < 0f)) move -= WallRight * along;
        return move * coverSpeedScale;
    }

    void Update()
    {
        if (!IsSpawned) return;
        if (IsOwner) OwnerUpdate();
        ApplyCamera();
    }

    void OwnerUpdate()
    {
        float dt = Time.deltaTime;
        bool canCover = player.IsCrouching && controller.isGrounded && !player.IsGrabbed && !player.IsDead &&
                        !NetworkUI.MenuOpen;

        Vector3 normal = wallNormal;
        bool found = canCover && FindWall(out normal, out wallDistance);
        if (!inCover && found && wallDistance < enterDistance) inCover = true;
        else if (inCover && (!found || wallDistance > exitDistance)) inCover = false;

        if (inCover)
        {
            // Smooth turn around corners and along curved walls
            if (found) wallNormal = Vector3.Slerp(wallNormal, normal, 1f - Mathf.Exp(-12f * dt)).normalized;
            edgeRight = !WallBeside(WallRight);
            edgeLeft = !WallBeside(-WallRight);

            // Push toward the end of the wall to lean out. Once out, the lean holds while you're still at that end,
            // even if you stop pushing or turn the mouse (turning changes which way your keys point).
            // Step back along the wall and the end isn't beside you any more, so the lean goes.
            float along = Vector3.Dot(lastMove, WallRight);
            float target = edgeRight && (along > 0.3f || peek > 0f) ? 1f
                         : edgeLeft && (along < -0.3f || peek < 0f) ? -1f : 0f;
            peek = Mathf.MoveTowards(peek, target, peekSpeed * dt);

            // You can look along the wall and out, but the view eases away from staring into the wall.
            // On the side you're leaning out, you can look right around the corner.
            float angle = Vector3.SignedAngle(wallNormal, Vector3.ProjectOnPlane(transform.forward, Vector3.up), Vector3.up);
            bool peekSide = peek != 0f && Mathf.Sign(angle) == Mathf.Sign(peek);   // + angle = toward WallRight
            float limit = peekSide ? Mathf.Lerp(maxLookAngle, peekLookAngle, Mathf.Abs(peek)) : maxLookAngle;
            if (Mathf.Abs(angle) > limit)
            {
                float over = angle - Mathf.Sign(angle) * limit;
                transform.Rotate(0f, -over * (1f - Mathf.Exp(-lookEaseSpeed * dt)), 0f);
            }
        }
        else
        {
            edgeLeft = edgeRight = false;
            peek = Mathf.MoveTowards(peek, 0f, peekSpeed * 2f * dt);
        }
        // Grabbed or dead: FirstPersonController stops resetting the camera, so the lean must go at once
        if (player.IsGrabbed || player.IsDead) peek = 0f;

        // Sync (only when it changed enough to matter)
        if (netInCover.Value != inCover) netInCover.Value = inCover;
        float yaw = Quaternion.LookRotation(wallNormal).eulerAngles.y;
        if (Mathf.Abs(Mathf.DeltaAngle(netWallYaw.Value, yaw)) > 1f) netWallYaw.Value = yaw;
        if (Mathf.Abs(netPeek.Value - peek) > 0.02f || (peek == 0f && netPeek.Value != 0f)) netPeek.Value = peek;
    }

    // Nearest wall around us at crouched chest height. While in cover, the current wall is checked first so
    // we don't flip to another wall at an inside corner.
    bool FindWall(out Vector3 normal, out float distance)
    {
        Vector3 origin = transform.position + Vector3.up * probeHeight;
        float range = exitDistance + 0.2f;
        normal = Vector3.zero;
        distance = float.MaxValue;

        if (inCover && Hit(origin, -wallNormal, range, out var h))
        {
            normal = Flat(h.normal);
            distance = h.distance;
            return true;
        }

        const int rays = 16;
        for (int i = 0; i < rays; i++)
        {
            Vector3 dir = Quaternion.Euler(0f, i * 360f / rays, 0f) * Vector3.forward;
            if (Hit(origin, dir, range, out var hit) && hit.distance < distance)
            {
                distance = hit.distance;
                normal = Flat(hit.normal);
            }
        }
        return distance < float.MaxValue;
    }

    // Is the wall still there a little to this side of us?
    bool WallBeside(Vector3 side)
    {
        Vector3 origin = transform.position + Vector3.up * probeHeight + side * edgeProbe;
        return Hit(origin, -wallNormal, wallDistance + 0.3f, out var hit) && Vector3.Dot(Flat(hit.normal), wallNormal) > 0.7f;
    }

    // A wall: something solid and upright that isn't a player
    bool Hit(Vector3 origin, Vector3 dir, float range, out RaycastHit hit)
    {
        if (!Physics.Raycast(origin, dir, out hit, range, ~0, QueryTriggerInteraction.Ignore)) return false;
        if (Mathf.Abs(hit.normal.y) > 0.3f) return false;
        return !(hit.collider is CharacterController);
    }

    static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.forward;
    }

    // Lean the head out past the corner (FirstPersonController has already placed the camera this frame)
    void ApplyCamera()
    {
        float p = Peek;
        if (Mathf.Abs(p) < 0.001f || player.cameraPivot == null) return;
        Vector3 leanDir = WallRight * Mathf.Sign(p);
        float amount = Mathf.SmoothStep(0f, 1f, Mathf.Abs(p));
        player.cameraPivot.position += (leanDir * peekDistance + WallNormal * 0.05f) * amount;
        float roll = -Vector3.Dot(leanDir, transform.right) * peekRoll * amount;
        player.cameraPivot.localRotation *= Quaternion.Euler(0f, 0f, roll);
    }
}
