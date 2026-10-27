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

    [Header("Barricade")]
    [Tooltip("Width of the door opening, used to size the planks.")]
    public float doorWidth = 2f;

    int boardCount;
    int boardsNeeded = 3;
    readonly List<GameObject> planks = new List<GameObject>();

    // A fully boarded doorway can't be reached through: the giant ignores it (MonsterAI.FindReachPoint)
    public bool Blocked => boardCount >= boardsNeeded;

    // Called on every machine by BarricadeManager when the board count changes
    public void SetBoards(int count, int needed)
    {
        boardCount = count;
        boardsNeeded = Mathf.Max(1, needed);
        foreach (var p in planks) if (p != null) Destroy(p);
        planks.Clear();
        for (int i = 0; i < count; i++)
        {
            var plank = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plank.name = "Plank";
            plank.transform.SetPositionAndRotation(
                transform.position + transform.forward * doorDistance + Vector3.up * (0.5f + i * 0.7f) + Vector3.up * 0.1f * (i % 2),
                Quaternion.LookRotation(transform.forward) * Quaternion.Euler(0f, 0f, (i % 2 == 0 ? 3f : -3f)));
            plank.transform.localScale = new Vector3(doorWidth * 1.1f, 0.25f, 0.08f);
            plank.GetComponent<Renderer>().material.color = new Color(0.45f, 0.3f, 0.15f);
            planks.Add(plank);
        }
    }

    void OnDrawGizmos()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, 0.6f);
        Gizmos.DrawLine(transform.position, DoorPoint);
        Gizmos.DrawWireSphere(DoorPoint, 0.3f);
    }
}
