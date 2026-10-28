using System.Collections.Generic;
using UnityEngine;

// A ground-floor window: a real opening in the wall (BuildingBuilder cuts it) with a pane of glass in it. The Climber
// smashes its way in and out through these (WindowManager syncs which are broken). A boarded window it can't use.
// Some start Holed: a small jagged hole low in the glass that a scout squeezes through and a person can't.
// Sits on the glass; the glass faces outward (+Z is out of the building).
public class BreakableWindow : MonoBehaviour
{
    // Broken = smashed from outside (glass flies into the room), BrokenOutward = smashed from inside (into the street),
    // Holed = the glass is still there but has a scout-sized hole near the sill
    public enum State : byte { Intact, Broken, Boarded, BrokenOutward, Holed }

    public static readonly List<BreakableWindow> All = new List<BreakableWindow>();

    [Tooltip("A roll-down shutter covers it (shops): the Climber can't get through.")]
    public bool shuttered;
    [Tooltip("Pieces of glass that fly into the room when it's smashed.")]
    public int shardCount = 18;
    [Tooltip("How hard they fly in (m/s, random between these).")]
    public Vector2 shardSpeed = new Vector2(2.5f, 6f);
    [Tooltip("Seconds the pieces lie on the floor before they're cleared away.")]
    public float shardLifetime = 40f;
    [Tooltip("Holed glass: how big the hole is (m across) and how far its middle sits above the sill.")]
    public float holeSize = 0.42f;
    public float holeHeight = 0.28f;

    public State Current { get; private set; } = State.Intact;

    Renderer glass;
    Collider glassCollider;
    readonly List<GameObject> planks = new List<GameObject>();   // boards, or the holed pane + its shards

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

    // A scout can get through without breaking anything: a hole or no glass at all
    public bool ScoutUsable => !shuttered && (IsBroken || Current == State.Holed);

    /// <summary>Where a scout passes through the glass (the hole's middle, or low in an empty frame).</summary>
    public Vector3 ScoutGap => new Vector3(Center.x, SillY + holeHeight, Center.z) + transform.right * HoleSide * Size.x;

    // The hole sits a little off-centre, the same way on every machine (from the window's position)
    float HoleSide => Mathf.Repeat(Mathf.Abs(Center.x * 7.13f + Center.z * 3.71f), 1f) * 0.4f - 0.2f;

    // Called on every machine by WindowManager. smashed = it just broke (not catching up on a join): glass flies in.
    public void Show(State state, bool smashed = false)
    {
        if (smashed && (Current == State.Intact || Current == State.Holed) && (state == State.Broken || state == State.BrokenOutward))
            Shatter(state == State.BrokenOutward ? Outward : -Outward);
        Current = state;
        if (glass != null) glass.enabled = state == State.Intact;
        // Holed glass still stops people (a scout's trip through the hole is scripted)
        if (glassCollider != null) glassCollider.enabled = state == State.Intact || state == State.Holed;

        // Boards across the opening (nailed up by players; see WindowManager)
        foreach (var p in planks) if (p != null) Destroy(p);
        planks.Clear();
        if (state == State.Holed) { MakeHoledPane(); return; }
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

    // The pane with a jagged hole: a ring of triangles between the glass edge and the hole's outline, built in the
    // glass's own unit space (it's a scaled cube), so the hole is squashed back to round by dividing by the size.
    // Plus a few bits of glass lying on the floor inside, under the hole.
    void MakeHoledPane()
    {
        var mat = glass != null ? glass.sharedMaterial : null;
        Vector2 size = Size;
        var rng = new System.Random(Mathf.RoundToInt(Center.x * 31f + Center.z * 17f));
        Vector2 c = new Vector2(HoleSide, holeHeight / size.y - 0.5f);
        const int n = 14;
        var verts = new List<Vector3>();
        for (int i = 0; i < n; i++)
        {
            float a = i * Mathf.PI * 2f / n;
            Vector2 d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            // Outer: where a ray from the hole's middle meets the edge of the pane
            float tx = d.x > 0.0001f ? (0.5f - c.x) / d.x : d.x < -0.0001f ? (-0.5f - c.x) / d.x : float.MaxValue;
            float ty = d.y > 0.0001f ? (0.5f - c.y) / d.y : d.y < -0.0001f ? (-0.5f - c.y) / d.y : float.MaxValue;
            float edge = Mathf.Min(tx, ty);
            Vector2 outer = c + d * edge;
            // Inner: a jagged outline (cracked glass), kept short of the pane's edge
            float rad = holeSize * 0.5f * (0.65f + (float)rng.NextDouble() * 0.7f);
            Vector2 off = new Vector2(d.x * rad / size.x, d.y * rad / size.y);
            if (off.magnitude > edge * 0.85f) off = off.normalized * edge * 0.85f;
            verts.Add(new Vector3(outer.x, outer.y, 0f));
            verts.Add(new Vector3(c.x + off.x, c.y + off.y, 0f));
        }
        var tris = new List<int>();
        for (int i = 0; i < n; i++)
        {
            int o0 = i * 2, i0 = o0 + 1, o1 = (o0 + 2) % (n * 2), i1 = o1 + 1;
            tris.AddRange(new[] { o0, o1, i0, i0, o1, i1 });
        }
        // The ring cuts across the pane's corners (between two rays): fill each corner with a triangle
        Vector2[] corners = { new Vector2(0.5f, 0.5f), new Vector2(-0.5f, 0.5f), new Vector2(-0.5f, -0.5f), new Vector2(0.5f, -0.5f) };
        foreach (var k in corners)
        {
            float ka = Mathf.Repeat(Mathf.Atan2(k.y - c.y, k.x - c.x), Mathf.PI * 2f);
            int a0 = Mathf.FloorToInt(ka / (Mathf.PI * 2f / n)) % n, a1 = (a0 + 1) % n;
            int v = verts.Count;
            verts.Add(new Vector3(k.x, k.y, 0f));
            // whichever winding matches the ring (corner is outside the a0-a1 edge as seen from the hole)
            Vector3 e = verts[a1 * 2] - verts[a0 * 2], f = verts[v] - verts[a0 * 2];
            if (Vector3.Cross(e, f).z > 0f) tris.AddRange(new[] { a0 * 2, a1 * 2, v });   // same way round as the ring
            else tris.AddRange(new[] { a0 * 2, v, a1 * 2 });
        }

        // Two separate faces (front and back) with their own vertices and normals. Sharing vertices between the two
        // windings makes the computed normals cancel to zero, and the lighting then outputs NaNs that bloom smears
        // over the whole screen.
        Vector3 ta = verts[tris[0]], tb = verts[tris[1]], tc = verts[tris[2]];
        float facing = Mathf.Sign(Vector3.Cross(tb - ta, tc - ta).z);   // which way the first winding faces (local z)
        int count = verts.Count;
        var allVerts = new List<Vector3>(verts);
        allVerts.AddRange(verts);
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        for (int i = 0; i < count * 2; i++)
        {
            // Unity's front face is clockwise: its normal is opposite the counter-clockwise cross product
            normals.Add(new Vector3(0f, 0f, (i < count ? -facing : facing)));
            uvs.Add(new Vector2(allVerts[i].x + 0.5f, allVerts[i].y + 0.5f));
        }
        var allTris = new List<int>(tris);
        for (int i = 0; i < tris.Count; i += 3)
            allTris.AddRange(new[] { tris[i] + count, tris[i + 2] + count, tris[i + 1] + count });   // back face, flipped

        var mesh = new Mesh { name = "Glass_Holed" };
        mesh.SetVertices(allVerts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(allTris, 0);
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();

        var pane = new GameObject("Glass_Holed");
        pane.transform.SetParent(transform, false);
        pane.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = pane.AddComponent<MeshRenderer>();
        if (mat != null) mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        planks.Add(pane);

        // Shards on the floor inside (no physics: they've been lying there a while)
        Vector3 under = ScoutGap - Outward * 0.5f;
        for (int i = 0; i < 5; i++)
        {
            Vector3 p = under - Outward * (float)rng.NextDouble() * 0.6f + transform.right * ((float)rng.NextDouble() - 0.5f) * 0.6f;
            if (!Physics.Raycast(p + Vector3.up * 0.3f, Vector3.down, out var hit, 3f, ~0, QueryTriggerInteraction.Ignore)) continue;
            var s = GameObject.CreatePrimitive(PrimitiveType.Cube);
            s.name = "Glass_Shard";
            Destroy(s.GetComponent<Collider>());
            s.transform.SetPositionAndRotation(hit.point + Vector3.up * 0.005f, Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f));
            float k = 0.05f + (float)rng.NextDouble() * 0.08f;
            s.transform.localScale = new Vector3(k, 0.006f, k * 0.6f);
            var r = s.GetComponent<MeshRenderer>();
            if (mat != null) r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            planks.Add(s);
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
