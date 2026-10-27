using System.Collections.Generic;
using UnityEngine;

public enum RoofStyle { Flat, Gable, Hip }

// Sloped roofs for houses, built as one mesh (tiles on the slopes, the wall material on the gable ends) with a
// MeshCollider. They sit on the building's flat ceiling slab, which is then called "Ceiling" rather than "Roof":
// the Climber only climbs onto and drops in through flat "Roof" boxes, so sloped-roof houses are safe from it.
public static class RoofBuilder
{
    const float Overhang = 0.45f;     // how far the eaves stick out past the walls
    const float Thickness = 0.14f;    // slope thickness (the underside is visible from the street)
    const float TileMeters = 2f;      // one copy of the tile texture covers this much roof

    static readonly Dictionary<int, Material> tileMats = new Dictionary<int, Material>();

    // A sloped roof on top of the walls. baseY = top of the ceiling slab. Returns the roof's highest point.
    public static float Build(Transform building, RoofStyle style, float width, float depth, float baseY, float pitchDegrees,
                              Material wall, float wallTile, System.Random rng)
    {
        var go = new GameObject("RoofSlope");
        go.transform.SetParent(building, false);
        go.isStatic = true;

        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tiles = new List<int>();
        var ends = new List<int>();

        float slope = Mathf.Tan(pitchDegrees * Mathf.Deg2Rad);
        float hw = width / 2f, hd = depth / 2f;
        // The ridge runs along the long side for a hip roof; a gable roof may run either way
        bool ridgeAlongX = style == RoofStyle.Hip ? width >= depth : rng.NextDouble() < 0.7;
        float halfLong = ridgeAlongX ? hw : hd, halfShort = ridgeAlongX ? hd : hw;
        float b = halfShort + Overhang;                 // eave to ridge, measured flat
        float ridgeY = baseY + slope * b;
        float wallLineY = baseY + slope * Overhang;     // roof height right above the walls

        // Work in a frame where L = along the ridge and S = across it, then turn into building space
        Vector3 L = ridgeAlongX ? Vector3.right : Vector3.forward;
        Vector3 S = ridgeAlongX ? Vector3.forward : Vector3.right;
        Vector3 P(float l, float s, float y) => L * l + S * s + Vector3.up * y;

        if (style == RoofStyle.Gable)
        {
            float a = halfLong + Overhang;
            foreach (float side in new[] { -1f, 1f })
                Face(verts, uvs, tiles, new[] { P(-a, side * b, baseY), P(a, side * b, baseY), P(a, 0f, ridgeY), P(-a, 0f, ridgeY) },
                     S * side + Vector3.up, L);
            // Gable ends: the wall carried up to the roof line
            foreach (float end in new[] { -1f, 1f })
                Face(verts, uvs, ends, new[] { P(end * halfLong, -halfShort, baseY), P(end * halfLong, halfShort, baseY),
                     P(end * halfLong, halfShort, wallLineY), P(end * halfLong, 0f, ridgeY), P(end * halfLong, -halfShort, wallLineY) },
                     L * end, S, wallTile, true);
        }
        else
        {
            float a = halfLong + Overhang;
            float ridgeHalf = Mathf.Max(0f, a - b);
            foreach (float side in new[] { -1f, 1f })
                Face(verts, uvs, tiles, new[] { P(-a, side * b, baseY), P(a, side * b, baseY), P(ridgeHalf, 0f, ridgeY), P(-ridgeHalf, 0f, ridgeY) },
                     S * side + Vector3.up, L);
            foreach (float end in new[] { -1f, 1f })
                Face(verts, uvs, tiles, new[] { P(end * a, -b, baseY), P(end * a, b, baseY), P(end * ridgeHalf, 0f, ridgeY) },
                     L * end + Vector3.up, S);
        }

        // Fascia: fills the gap between the ceiling slab and the eaves along the walls under the slopes
        float fasciaH = wallLineY - baseY;
        if (fasciaH > 0.02f)
            foreach (float side in new[] { -1f, 1f })
            {
                Fascia(building, S * (side * (halfShort - 0.1f)) + Vector3.up * (baseY + fasciaH / 2f), L * halfLong * 2f + S * 0.2f, fasciaH, wall);
                if (style == RoofStyle.Hip)   // a hip roof has eaves all round
                    Fascia(building, L * (side * (halfLong - 0.1f)) + Vector3.up * (baseY + fasciaH / 2f), S * halfShort * 2f + L * 0.2f, fasciaH, wall);
            }

        var mesh = new Mesh { name = "RoofSlope" };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.subMeshCount = 2;
        mesh.SetTriangles(tiles, 0);
        mesh.SetTriangles(ends, 1);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterials = new[] { TileMaterial(rng.NextDouble() < 0.6 ? 0 : 1), wall };
        go.AddComponent<MeshCollider>().sharedMesh = mesh;

        // A brick chimney poking out of one slope
        if (rng.NextDouble() < 0.7)
        {
            float l = ((float)rng.NextDouble() - 0.5f) * halfLong;
            float s = (rng.NextDouble() < 0.5 ? -1f : 1f) * b * 0.35f;
            float roofAt = ridgeY - slope * Mathf.Abs(s);
            float top = ridgeY + 0.6f;
            var c = GameObject.CreatePrimitive(PrimitiveType.Cube);
            c.name = "Chimney";
            c.transform.SetParent(building, false);
            c.transform.localPosition = P(l, s, (roofAt - 0.3f + top) / 2f);
            c.transform.localScale = new Vector3(0.7f, top - roofAt + 0.3f, 0.7f);
            Object.DestroyImmediate(c.GetComponent<Collider>());
            if (wall != null) c.GetComponent<MeshRenderer>().sharedMaterial = wall;
            c.isStatic = true;
        }
        return ridgeY;
    }

    static void Fascia(Transform building, Vector3 pos, Vector3 flatSize, float height, Material wall)
    {
        var f = GameObject.CreatePrimitive(PrimitiveType.Cube);
        f.name = "Wall_Fascia";
        f.transform.SetParent(building, false);
        f.transform.localPosition = pos;
        f.transform.localScale = new Vector3(flatSize.x, height, flatSize.z);
        Object.DestroyImmediate(f.GetComponent<Collider>());
        if (wall != null) f.GetComponent<MeshRenderer>().sharedMaterial = wall;
        f.isStatic = true;
    }

    // One flat face (convex polygon), facing 'outward', with its underside. UVs in meters along 'along' and up the slope.
    static void Face(List<Vector3> verts, List<Vector2> uvs, List<int> tris, Vector3[] pts, Vector3 outward, Vector3 along,
                     float tile = TileMeters, bool singleSided = false)
    {
        Vector3 n = Vector3.Cross(pts[1] - pts[0], pts[2] - pts[0]);
        if (Vector3.Dot(n, outward) < 0f) System.Array.Reverse(pts);
        n = Vector3.Cross(pts[1] - pts[0], pts[2] - pts[0]).normalized;
        Vector3 up = Vector3.Cross(n, along).normalized;   // up the face, at right angles to 'along'
        if (Vector3.Dot(up, Vector3.up) < 0f) up = -up;
        Polygon(verts, uvs, tris, pts, along, up, tile, false);
        if (singleSided) return;

        // Underside: the same face a little lower, facing down
        var under = new Vector3[pts.Length];
        for (int i = 0; i < pts.Length; i++) under[i] = pts[i] - Vector3.up * Thickness;
        Polygon(verts, uvs, tris, under, along, up, tile, true);
    }

    static void Polygon(List<Vector3> verts, List<Vector2> uvs, List<int> tris, Vector3[] pts, Vector3 along, Vector3 up,
                        float tile, bool flip)
    {
        int start = verts.Count;
        foreach (var p in pts)
        {
            verts.Add(p);
            uvs.Add(new Vector2(Vector3.Dot(p, along), Vector3.Dot(p, up)) / tile);
        }
        for (int i = 1; i < pts.Length - 1; i++)
        {
            tris.Add(start);
            tris.Add(flip ? start + i + 1 : start + i);
            tris.Add(flip ? start + i : start + i + 1);
        }
    }

    // Terracotta (0) or slate (1) tiles, made once
    static Material TileMaterial(int variant)
    {
        if (tileMats.TryGetValue(variant, out var m) && m != null) return m;
        m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = variant == 0 ? "Roof_Terracotta" : "Roof_Slate" };
        m.SetTexture("_BaseMap", ProcTextures.RoofTiles(variant));
        m.SetColor("_BaseColor", Color.white);
        m.SetFloat("_Smoothness", variant == 0 ? 0.15f : 0.3f);
        tileMats[variant] = m;
        return m;
    }
}
