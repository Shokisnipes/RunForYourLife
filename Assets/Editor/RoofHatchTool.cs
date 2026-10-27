using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Tools -> RunForYourLife -> Add Roof Hatches To Existing Buildings. Cuts a hatch into a building's roof (the roof
// slab is split around the hole, with a rim and a hinged lid on top: RoofHatch). The Climber drops in through it.
public static class RoofHatchTool
{
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

    // The hatch itself is made by BuildingBuilder (Scripts); this registers everything it makes for Undo
    public static bool Add(Transform building)
    {
        Undo.RegisterFullObjectHierarchyUndo(building.gameObject, "Add Roof Hatch");
        var metal = BuildingDetails.Mat("Prop_Metal", new Color(0.35f, 0.36f, 0.37f), Color.black, 0.35f);
        var created = new List<GameObject>();
        var hatch = BuildingBuilder.AddRoofHatch(building, metal, created);
        foreach (var go in created) Undo.RegisterCreatedObjectUndo(go, "Add Roof Hatch");
        return hatch != null;
    }
}
