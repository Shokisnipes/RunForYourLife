using System.Collections.Generic;
using UnityEngine;

// A ground-floor window: a real opening in the wall (BuildingBuilder cuts it) with a pane of glass in it. The Climber
// smashes its way in and out through these (WindowManager syncs which are broken). A boarded window it can't use.
// Sits on the glass; the glass faces outward (+Z is out of the building).
public class BreakableWindow : MonoBehaviour
{
    // Broken = smashed from outside (glass flies into the room), BrokenOutward = smashed from inside (into the street)
    public enum State : byte { Intact, Broken, Boarded, BrokenOutward }

    public static readonly List<BreakableWindow> All = new List<BreakableWindow>();

    [Tooltip("A roll-down shutter covers it (shops): the Climber can't get through.")]
    public bool shuttered;
    [Tooltip("Pieces of glass that fly into the room when it's smashed.")]
    public int shardCount = 18;
    [Tooltip("How hard they fly in (m/s, random between these).")]
    public Vector2 shardSpeed = new Vector2(2.5f, 6f);
    [Tooltip("Seconds the pieces lie on the floor before they're cleared away.")]
    public float shardLifetime = 40f;

    public State Current { get; private set; } = State.Intact;

    Renderer glass;
    Collider glassCollider;
    readonly List<GameObject> planks = new List<GameObject>();

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    void Awake()
    {
        glass = GetComponent<Renderer>();
        glassCollider = GetComponent<Collider>();
    }

    public Vector3 Center => transform.position;
    public Vector3 Outward => transform.forward;
    public Vector2 Size => new Vector2(transform.lossyScale.x, transform.lossyScale.y);
    public float SillY => transform.position.y - transform.lossyScale.y / 2f;

    public bool IsBroken => Current == State.Broken || Current == State.BrokenOutward;

    // The Climber can squeeze through
    public bool Usable => Current != State.Boarded && !shuttered;

    // Called on every machine by WindowManager. smashed = it just broke (not catching up on a join): glass flies in.
    public void Show(State state, bool smashed = false)
    {
        if (smashed && Current == State.Intact && (state == State.Broken || state == State.BrokenOutward))
            Shatter(state == State.BrokenOutward ? Outward : -Outward);
        Current = state;
        if (glass != null) glass.enabled = state == State.Intact;
        if (glassCollider != null) glassCollider.enabled = state == State.Intact;

        // Boards across the opening (nailed up by players; see WindowManager)
        foreach (var p in planks) if (p != null) Destroy(p);
        planks.Clear();
        if (state != State.Boarded) return;
        Vector2 size = Size;
        for (int i = 0; i < 3; i++)
        {
            var plank = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plank.name = "Window_Plank";
            plank.transform.SetPositionAndRotation(
                Center + Outward * 0.06f + Vector3.up * (size.y * (i - 1) * 0.33f),
                transform.rotation * Quaternion.Euler(0f, 0f, i % 2 == 0 ? 4f : -4f));
            plank.transform.localScale = new Vector3(size.x * 1.15f, 0.2f, 0.05f);
            plank.GetComponent<Renderer>().sharedMaterial = BuildingBuilder.Fallback("Plank", new Color(0.45f, 0.3f, 0.15f), Color.black, 0.15f);
            planks.Add(plank);
        }
    }

    // Glass bursts away from whoever smashed it: small tumbling pieces with physics, on the Ignore Raycast layer so
    // they never block anyone's sight. Each machine makes its own (they're only for show).
    void Shatter(Vector3 away)
    {
        var mat = glass != null ? glass.sharedMaterial : null;
        Vector2 size = Size;
        Vector3 right = transform.right;
        for (int i = 0; i < shardCount; i++)
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Cube);
            s.name = "Glass_Shard";
            s.layer = 2;   // Ignore Raycast
            s.transform.position = Center + right * Random.Range(-0.45f, 0.45f) * size.x + Vector3.up * Random.Range(-0.45f, 0.45f) * size.y;
            s.transform.rotation = Random.rotation;
            float k = Random.Range(0.04f, 0.16f);
            s.transform.localScale = new Vector3(k, k * Random.Range(0.4f, 1.6f), 0.008f);
            var r = s.GetComponent<MeshRenderer>();
            if (mat != null) r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var rb = s.AddComponent<Rigidbody>();
            rb.mass = 0.05f;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.linearVelocity = away * Random.Range(shardSpeed.x, shardSpeed.y) + Vector3.up * Random.Range(0f, 1.5f) + right * Random.Range(-1.2f, 1.2f);
            rb.angularVelocity = Random.insideUnitSphere * 20f;
            Destroy(s, shardLifetime * Random.Range(0.8f, 1.2f));
        }
    }
}
