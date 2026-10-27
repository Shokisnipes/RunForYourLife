using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// A building's front door: two leaves that swing inward, on the "Door" layer. Made by BuildingBuilder; the state
// (closed / open / torn open by the giant) is synced by DoorManager and shown here on every machine.
//  - Closed, the leaves block players physically, and a NavMeshObstacle cuts the doorway out of the NavMesh, so the
//    Climber (which can't open doors) has to go round to the roof hatch instead.
//  - The NavMesh bake ignores the Door layer, so an open doorway is always walkable for the Climber.
//  - Torn open: the giant ripped it; it hangs open and can't be closed again.
public class Door : MonoBehaviour
{
    public enum State : byte { Closed, Open, Torn }

    public static readonly List<Door> All = new List<Door>();

    // The layer doors go on ("Door" in Tags and Layers); Default if it doesn't exist
    public static int Layer
    {
        get
        {
            int l = LayerMask.NameToLayer("Door");
            return l < 0 ? 0 : l;
        }
    }

    [Tooltip("Left and right leaves (hinge pivots). Closed = rotation 0.")]
    public Transform leftHinge, rightHinge;
    [Tooltip("Cuts the doorway out of the NavMesh while closed.")]
    public NavMeshObstacle blocker;
    [Tooltip("Degrees per second the leaves swing.")]
    public float swingSpeed = 220f;
    [Tooltip("How far the leaves open (degrees).")]
    public float openAngle = 100f;

    public State Current { get; private set; } = State.Closed;

    // The middle of the doorway, at chest height (players aim for this to open it)
    public Vector3 Center => transform.TransformPoint(new Vector3(0f, 1.2f, 0f));
    public Vector3 Inward => transform.forward;

    float leftAngle, rightAngle;
    Vector3 leftRest, rightRest;

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    void Awake()
    {
        if (leftHinge != null) leftRest = leftHinge.localPosition;
        if (rightHinge != null) rightRest = rightHinge.localPosition;
    }

    // Called on every machine by DoorManager. instant = no swing (first sync, or a client catching up)
    public void Show(State state, bool instant)
    {
        Current = state;
        if (blocker != null) blocker.enabled = state == State.Closed;
        if (instant) { leftAngle = LeftTarget; rightAngle = RightTarget; Pose(); }
    }

    // Left leaf swings -Y (its free end comes inward), right leaf +Y
    float LeftTarget => Current == State.Closed ? 0f : Current == State.Open ? -openAngle : -125f;
    float RightTarget => Current == State.Closed ? 0f : Current == State.Open ? openAngle : 112f;

    void Update()
    {
        float speed = Current == State.Torn ? swingSpeed * 4f : swingSpeed;   // ripped: it slams open
        leftAngle = Mathf.MoveTowards(leftAngle, LeftTarget, speed * Time.deltaTime);
        rightAngle = Mathf.MoveTowards(rightAngle, RightTarget, speed * Time.deltaTime);
        Pose();
    }

    void Pose()
    {
        // A torn door hangs crooked off one hinge
        bool torn = Current == State.Torn;
        if (leftHinge != null)
        {
            leftHinge.localRotation = Quaternion.Euler(0f, leftAngle, torn ? 9f : 0f);
            leftHinge.localPosition = leftRest + (torn ? Vector3.down * 0.12f : Vector3.zero);
        }
        if (rightHinge != null) rightHinge.localRotation = Quaternion.Euler(0f, rightAngle, torn ? -4f : 0f);
    }

    // Everything except doors: for raycasts that should pass through them
    public static int AllButDoors => Layer == 0 ? ~0 : ~(1 << Layer);
}
