using System.Collections.Generic;
using UnityEngine;

// Marks a doorway the giant can crouch at and reach into.
// Place it OUTSIDE the door, on the street, with the blue arrow (forward) pointing INTO the building.
// The Building Generator adds one automatically.
public class ReachPoint : MonoBehaviour
{
    public static readonly List<ReachPoint> All = new List<ReachPoint>();

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    [Tooltip("Distance from this marker to the door opening (the generator uses 3.5).")]
    public float doorDistance = 3.5f;
    [Tooltip("Height of the middle of the door opening above the floor.")]
    public float doorHeight = 1.2f;

    // The middle of the door opening: the hand passes through here
    public Vector3 DoorPoint => transform.position + transform.forward * doorDistance + Vector3.up * doorHeight;
    public Vector3 Inward => transform.forward;

    // A spot inside the doorway the giant looks at while peeking
    public Vector3 LookInPoint => DoorPoint + transform.forward * 3f;

    void OnDrawGizmos()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, 0.6f);
        Gizmos.DrawLine(transform.position, DoorPoint);
        Gizmos.DrawWireSphere(DoorPoint, 0.3f);
    }
}
