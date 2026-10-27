using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Cuts a hatch into a building's roof: the roof slab is split into four pieces around the hole, with a rim and a
// hinged lid on top (RoofHatch). The Climber drops in through it. Used by the Building Generator and by
// Tools -> RunForYourLife -> Add Roof Hatches To Existing Buildings.
public static class RoofHatchTool
{
    const float HatchSize = 1.3f;
    const float RimHeight = 0.12f, RimThickness = 0.1f;

    [MenuItem("Tools/RunForYourLife/Add Roof Hatches To Existing Buildings")]
    static void AddToExisting()
    {
        int added = 0;
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude))
        {
            if (t.name != "Roof" || t.parent == null) continue;
            if (t.parent.GetComponentInChildren<RoofHatch>() != null) continue;
            if (Add(t.parent)) added++;
        }
        Debug.Log($"Added {added} roof hatch(es).");
        if (added > 0) ClimberSetup.BakeClimberNavMesh();
    }

    public static bool Add(Transform building)
    {
        var roof = building.Find("Roof");
        if (roof == null) return false;
        Vector3 rp = roof.localPosition, rs = roof.localScale;   // roof is a cube scaled to width x thickness x depth
        float halfW = rs.x / 2f, halfD = rs.z / 2f, s = HatchSize;
        if (rs.x < s + 2.5f || rs.z < s + 2.5f) return false;   // too small to cut a hole in

        if (!PickSpot(building, rp, halfW, halfD, out float hx, out float hz)) return false;

        // Roof split around the hole (positions relative to the roof's centre). The original stays as "Roof".
        float x0 = hx - s / 2f, x1 = hx + s / 2f, z0 = hz - s / 2f, z1 = hz + s / 2f;
        Undo.RegisterFullObjectHierarchyUndo(building.gameObject, "Add Roof Hatch");
        Piece(building, roof, null,     rp, rs, -halfW, halfW, -halfD, z0);
        Piece(building, roof, "Roof_N", rp, rs, -halfW, halfW, z1, halfD);
        Piece(building, roof, "Roof_W", rp, rs, -halfW, x0, z0, z1);
        Piece(building, roof, "Roof_E", rp, rs, x1, halfW, z0, z1);

        // The hatch: rim around the hole and a lid on a hinge along one edge
        var metal = BuildingDetails.Mat("Prop_Metal", new Color(0.35f, 0.36f, 0.37f), Color.black, 0.35f);
        float top = rp.y + rs.y / 2f;
        var hatch = new GameObject("RoofHatch").transform;
        hatch.SetParent(building, false);
        hatch.localPosition = new Vector3(rp.x + hx, top, rp.z + hz);
        float r = RimThickness, half = s / 2f + r / 2f;
        Cube(hatch, "Rim_N", new Vector3(0, RimHeight / 2f,  half), new Vector3(s + r * 2f, RimHeight, r), metal, true);
        Cube(hatch, "Rim_S", new Vector3(0, RimHeight / 2f, -half), new Vector3(s + r * 2f, RimHeight, r), metal, true);
        Cube(hatch, "Rim_W", new Vector3(-half, RimHeight / 2f, 0), new Vector3(r, RimHeight, s), metal, true);
        Cube(hatch, "Rim_E", new Vector3( half, RimHeight / 2f, 0), new Vector3(r, RimHeight, s), metal, true);

        var hinge = new GameObject("Hinge").transform;
        hinge.SetParent(hatch, false);
        hinge.localPosition = new Vector3(-s / 2f - r, RimHeight, 0f);
        Cube(hinge, "Lid", new Vector3(s / 2f + r, 0.03f, 0f), new Vector3(s + r * 2f, 0.06f, s + r * 2f), metal, false);

        var rh = hatch.gameObject.AddComponent<RoofHatch>();
        rh.hinge = hinge;
        rh.size = s;
        Undo.RegisterCreatedObjectUndo(hatch.gameObject, "Add Roof Hatch");
        return true;
    }

    // Where on the roof to put the hatch (relative to the roof's centre): in the back room if there is one,
    // away from the edges, the interior wall and the rooftop clutter
    static bool PickSpot(Transform building, Vector3 rp, float halfW, float halfD, out float hx, out float hz)
    {
        var wall = building.Find("Interior_L");
        bool hasWall = wall != null;
        float wallZ = hasWall ? wall.localPosition.z - rp.z : 0f;
        float preferZ = hasWall ? (wallZ + halfD) / 2f : 0f;   // middle of the back room

        var props = new List<Vector2>();
        var details = building.Find("Details");
        if (details != null)
            foreach (Transform p in details)
                if (p.name.StartsWith("Prop_"))
                {
                    Vector3 local = building.InverseTransformPoint(p.position) - rp;
                    props.Add(new Vector2(local.x, local.z));
                }

        float margin = HatchSize / 2f + 0.8f;
        float best = float.MaxValue;
        hx = hz = 0f;
        for (float x = -halfW + margin; x <= halfW - margin; x += 0.25f)
            for (float z = -halfD + margin; z <= halfD - margin; z += 0.25f)
            {
                if (hasWall && Mathf.Abs(z - wallZ) < HatchSize / 2f + 0.6f) continue;
                bool clear = true;
                foreach (var p in props)
                    if (Vector2.Distance(p, new Vector2(x, z)) < 1.8f) { clear = false; break; }
                if (!clear) continue;

                float score = Mathf.Abs(x) + Mathf.Abs(z - preferZ) + (hasWall && z < wallZ ? 100f : 0f);
                if (score < best) { best = score; hx = x; hz = z; }
            }
        return best < float.MaxValue;
    }

    // One slab of the split roof. name == null reuses the original "Roof" object; others are copies of it
    // (same material, BoxUV, static flags).
    static void Piece(Transform building, Transform roof, string name, Vector3 rp, Vector3 rs,
                      float xMin, float xMax, float zMin, float zMax)
    {
        if (xMax - xMin < 0.01f || zMax - zMin < 0.01f) return;
        Transform piece = roof;
        if (name != null)
        {
            piece = Object.Instantiate(roof.gameObject, building).transform;
            piece.name = name;
            Undo.RegisterCreatedObjectUndo(piece.gameObject, "Add Roof Hatch");
        }
        piece.localPosition = new Vector3(rp.x + (xMin + xMax) / 2f, rp.y, rp.z + (zMin + zMax) / 2f);
        piece.localRotation = roof.localRotation;
        piece.localScale = new Vector3(xMax - xMin, rs.y, zMax - zMin);
    }

    static void Cube(Transform parent, string name, Vector3 localPos, Vector3 scale, Material mat, bool isStatic)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = scale;
        go.isStatic = isStatic;   // the lid moves, so it isn't static
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
    }
}
