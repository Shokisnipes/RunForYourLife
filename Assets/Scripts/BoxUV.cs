using UnityEngine;

// Put on a scaled cube (walls, roofs, floors). Rebuilds its mesh so textures tile at real-world size
// instead of stretching: a 10 m wall shows 4 bricks-tiles, a 2 m wall shows 1, etc.
[ExecuteAlways]
[RequireComponent(typeof(MeshFilter))]
public class BoxUV : MonoBehaviour
{
    [Tooltip("How many meters one copy of the texture covers.")]
    public float metersPerTile = 2.5f;

    Mesh mesh;
    Vector3 builtScale;
    float builtTile;

    void OnEnable() => Rebuild();

    void Update()
    {
        // In the editor, keep it right while you resize things
        if (!Application.isPlaying && (transform.lossyScale != builtScale || !Mathf.Approximately(builtTile, metersPerTile)))
            Rebuild();
    }

    void OnDestroy()
    {
        if (mesh != null)
        {
            if (Application.isPlaying) Destroy(mesh);
            else DestroyImmediate(mesh);
        }
    }

    // Public so code that resizes a cube at runtime (BuildingBuilder) can refresh it; the editor does it by itself
    public void Rebuild()
    {
        builtScale = transform.lossyScale;
        builtTile = metersPerTile;
        float tile = Mathf.Max(0.01f, metersPerTile);
        Vector3 size = new Vector3(Mathf.Abs(builtScale.x), Mathf.Abs(builtScale.y), Mathf.Abs(builtScale.z));

        if (mesh == null)
        {
            mesh = new Mesh { name = "BoxUV", hideFlags = HideFlags.HideAndDontSave };
        }

        var verts = new Vector3[24];
        var normals = new Vector3[24];
        var uvs = new Vector2[24];
        var tris = new int[36];

        // normal, u axis, v axis for each face
        Vector3[] n = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        Vector3[] u = { Vector3.forward, Vector3.back, Vector3.right, Vector3.right, Vector3.left, Vector3.right };
        Vector3[] v = { Vector3.up, Vector3.up, Vector3.forward, Vector3.back, Vector3.up, Vector3.up };

        for (int f = 0; f < 6; f++)
        {
            Vector3 c = n[f] * 0.5f;
            Vector3 hu = u[f] * 0.5f, hv = v[f] * 0.5f;
            Vector3[] corners = { c - hu - hv, c - hu + hv, c + hu + hv, c + hu - hv };

            for (int i = 0; i < 4; i++)
            {
                int vi = f * 4 + i;
                verts[vi] = corners[i];
                normals[vi] = n[f];
                // UV in meters along the face, divided by tile size
                float su = Vector3.Dot(corners[i], u[f]) * Mathf.Abs(Vector3.Dot(size, Abs(u[f])));
                float sv = Vector3.Dot(corners[i], v[f]) * Mathf.Abs(Vector3.Dot(size, Abs(v[f])));
                uvs[vi] = new Vector2(su, sv) / tile;
            }

            // Make sure the triangles face outward
            int b = f * 4, t = f * 6;
            Vector3 tri = Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]);
            if (Vector3.Dot(tri, n[f]) > 0f)
            {
                tris[t] = b; tris[t + 1] = b + 1; tris[t + 2] = b + 2;
                tris[t + 3] = b; tris[t + 4] = b + 2; tris[t + 5] = b + 3;
            }
            else
            {
                tris[t] = b; tris[t + 1] = b + 2; tris[t + 2] = b + 1;
                tris[t + 3] = b; tris[t + 4] = b + 3; tris[t + 5] = b + 2;
            }
        }

        mesh.Clear();
        mesh.vertices = verts;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateTangents();   // needed for normal maps
        mesh.RecalculateBounds();

        GetComponent<MeshFilter>().sharedMesh = mesh;
    }

    static Vector3 Abs(Vector3 a) => new Vector3(Mathf.Abs(a.x), Mathf.Abs(a.y), Mathf.Abs(a.z));
}
