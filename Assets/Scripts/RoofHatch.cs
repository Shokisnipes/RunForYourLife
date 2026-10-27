using System.Collections.Generic;
using UnityEngine;

// A hatch in a building's roof (added by the Building Generator). The Climber drops in through it, and boards on
// the doors don't help. The lid swings open on every client while ClimberAI says it's using this hatch.
public class RoofHatch : MonoBehaviour
{
    public static readonly List<RoofHatch> All = new List<RoofHatch>();

    [Tooltip("The lid's hinge. It turns around its local Z axis to open.")]
    public Transform hinge;
    [Tooltip("Width of the opening (metres).")]
    public float size = 1.3f;
    [Tooltip("How far the lid swings open (degrees).")]
    public float openAngle = 110f;
    [Tooltip("Degrees per second the lid swings.")]
    public float swingSpeed = 220f;

    bool open;
    float angle;

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    // Opens the hatch at 'position' (the one ClimberAI synced) and closes all the others
    public static void SetOpenAt(Vector3 position, bool isOpen)
    {
        foreach (var h in All)
            h.open = isOpen && (h.transform.position - position).sqrMagnitude < 1f;
    }

    void Update()
    {
        if (hinge == null) return;
        float target = open ? openAngle : 0f;
        if (Mathf.Approximately(angle, target)) return;
        angle = Mathf.MoveTowards(angle, target, swingSpeed * Time.deltaTime);
        hinge.localRotation = Quaternion.Euler(0f, 0f, angle);
    }
}
