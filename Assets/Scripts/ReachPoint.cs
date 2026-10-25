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

    // A spot inside the doorway the giant looks at while peeking
    public Vector3 LookInPoint => transform.position + transform.forward * 5f + Vector3.up * 1.5f;

    void OnDrawGizmos()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, 0.6f);
        Gizmos.DrawLine(transform.position, transform.position + transform.forward * 4f);
        Gizmos.DrawWireSphere(transform.position + transform.forward * 4f, 0.2f);
    }
}
