using System.Collections.Generic;
using UnityEngine;

public enum FenceStyle { Railing, Wire }

// Fences: iron railings (parks) and welded-wire security fencing (shops, parking, passages between buildings).
// Each straight run is ONE mesh (all its bars and posts combined) with one thin BoxCollider, on the "Fence" layer.
//  - They block players and the Climber (the Climber's NavMesh is baked around them).
//  - The giant steps over them: CityManager leaves the Fence layer out of the giant's NavMesh bake, and the giant's
//    sight lines and foot placement ignore it (you can see through bars).
public static class FenceBuilder
{
    // The layer fences go on ("Fence" in Tags and Layers); Default if it doesn't exist
    public static int Layer
    {
        get
        {
            int l = LayerMask.NameToLayer("Fence");
            return l < 0 ? 0 : l;
        }
    }

    // Everything except fences, for raycasts that should see (or step) through them
    public static int AllButFences => Layer == 0 ? ~0 : ~(1 << Layer);

    const float PostSpacing = 2.4f;

    // A straight fence from a to b (parent-local, y = the ground it stands on)
    public static GameObject Run(Transform parent, Vector3 a, Vector3 b, FenceStyle style, float height)
    {
        Vector3 d = b - a;
        d.y = 0f;
        float length = d.magnitude;
        if (length < 0.2f) return null;

        var go = new GameObject(style == FenceStyle.Railing ? "Fence_Railing" : "Fence_Wire");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = a;
        go.transform.localRotation = Quaternion.LookRotation(d / length);
        go.layer = Layer;
        go.isStatic = true;

        var verts = new List<Vector3>();
        var tris = new List<int>();
        Panel(verts, tris, 0f, length, style, height);

        var mesh = new Mesh { name = go.name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = Material(style);

        var box = go.AddComponent<BoxCollider>();
        box.center = new Vector3(0f, height / 2f, length / 2f);
        box.size = new Vector3(0.1f, height, length);
        return go;
    }

    // A gate standing open: hinged at 'hinge', 'width' long, swung 'angle' degrees from the line it closes
    // (closedDir) toward 'swingToward'
    public static GameObject OpenGate(Transform parent, Vector3 hinge, Vector3 closedDir, Vector3 swingToward, float width,
                                      float angle, FenceStyle style, float height)
    {
        float a = angle * Mathf.Deg2Rad;
        Vector3 dir = (closedDir.normalized * Mathf.Cos(a) + swingToward.normalized * Mathf.Sin(a)).normalized;
        var gate = Run(parent, hinge, hinge + dir * width, style, height);
        if (gate != null) gate.name = "Fence_Gate";
        return gate;
    }

    // Posts, top and bottom rails and the infill, from z0 to z1 along the run
    static void Panel(List<Vector3> v, List<int> t, float z0, float z1, FenceStyle style, float h)
    {
        float len = z1 - z0;
        int posts = Mathf.Max(1, Mathf.CeilToInt(len / PostSpacing));
        for (int i = 0; i <= posts; i++)
            Box(v, t, new Vector3(0f, h / 2f, z0 + len * i / posts), new Vector3(0.07f, h, 0.07f));

        Box(v, t, new Vector3(0f, h - 0.04f, z0 + len / 2f), new Vector3(0.04f, 0.04f, len));
        Box(v, t, new Vector3(0f, 0.12f, z0 + len / 2f), new Vector3(0.04f, 0.04f, len));

        if (style == FenceStyle.Railing)
        {
            // Upright bars with a point on top
            for (float z = z0 + 0.14f; z < z1 - 0.05f; z += 0.14f)
            {
                Box(v, t, new Vector3(0f, (h + 0.08f) / 2f, z), new Vector3(0.022f, h - 0.04f, 0.022f));
                Box(v, t, new Vector3(0f, h + 0.06f, z), new Vector3(0.012f, 0.1f, 0.012f));
            }
        }
        else
        {
            // Welded wire: a square grid
            for (float z = z0 + 0.2f; z < z1 - 0.05f; z += 0.2f)
                Box(v, t, new Vector3(0f, h / 2f, z), new Vector3(0.012f, h - 0.1f, 0.012f));
            for (float y = 0.3f; y < h - 0.1f; y += 0.2f)
                Box(v, t, new Vector3(0f, y, z0 + len / 2f), new Vector3(0.012f, 0.012f, len));
        }
    }

    // An axis-aligned box (separate vertices per side, so it shades flat)
    static void Box(List<Vector3> v, List<int> t, Vector3 c, Vector3 s)
    {
        Vector3 e = s * 0.5f;
        Vector3[] corners =
        {
            c + new Vector3(-e.x, -e.y, -e.z), c + new Vector3(e.x, -e.y, -e.z), c + new Vector3(e.x, e.y, -e.z), c + new Vector3(-e.x, e.y, -e.z),
            c + new Vector3(-e.x, -e.y, e.z), c + new Vector3(e.x, -e.y, e.z), c + new Vector3(e.x, e.y, e.z), c + new Vector3(-e.x, e.y, e.z),
        };
        int[][] faces = { new[] { 0, 3, 2, 1 }, new[] { 5, 6, 7, 4 }, new[] { 4, 7, 3, 0 }, new[] { 1, 2, 6, 5 }, new[] { 3, 7, 6, 2 }, new[] { 4, 0, 1, 5 } };
        foreach (var f in faces)
        {
            int start = v.Count;
            foreach (int i in f) v.Add(corners[i]);
            t.Add(start); t.Add(start + 1); t.Add(start + 2);
            t.Add(start); t.Add(start + 2); t.Add(start + 3);
        }
    }

    static Material Material(FenceStyle style) => style == FenceStyle.Railing
        ? BuildingBuilder.Fallback("Fence_Iron", new Color(0.06f, 0.07f, 0.06f), Color.black, 0.35f)
        : BuildingBuilder.Fallback("Fence_Wire", new Color(0.42f, 0.44f, 0.45f), Color.black, 0.5f);
}
