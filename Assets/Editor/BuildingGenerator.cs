using UnityEditor;
using UnityEngine;

// Tools -> RunForYourLife -> Building Generator
// Spawns an enterable box building (walls, roof, doorway) made of plain cubes with BoxColliders.
// The building itself is made by BuildingBuilder (Scripts), which the in-game city generator uses too.
public class BuildingGenerator : EditorWindow
{
    BuildingBuilder.Spec spec = BuildingBuilder.Spec.Default;
    Material wallMaterial;   // empty = random wall material from the palette

    [MenuItem("Tools/RunForYourLife/Building Generator")]
    static void Open() => GetWindow<BuildingGenerator>("Building Generator");

    void OnGUI()
    {
        GUILayout.Label("Preset", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        spec.kind = (BuildingKind)EditorGUILayout.EnumPopup(spec.kind);
        if (GUILayout.Button("Randomize", GUILayout.Width(90)))
            spec = BuildingBuilder.RandomSpec(spec.kind, new System.Random());
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(6);
        GUILayout.Label("Size (meters)", EditorStyles.boldLabel);
        spec.width = EditorGUILayout.FloatField("Width (X)", spec.width);
        spec.depth = EditorGUILayout.FloatField("Depth (Z)", spec.depth);
        spec.height = EditorGUILayout.FloatField("Height (Y)", spec.height);
        spec.wallThickness = EditorGUILayout.FloatField("Wall Thickness", spec.wallThickness);

        GUILayout.Space(6);
        GUILayout.Label("Doorway (front = -Z side)", EditorStyles.boldLabel);
        spec.doorWidth = EditorGUILayout.FloatField("Door Width", spec.doorWidth);
        spec.doorHeight = EditorGUILayout.FloatField("Door Height", spec.doorHeight);
        spec.doorOffset = EditorGUILayout.FloatField("Door Offset", spec.doorOffset);

        GUILayout.Space(6);
        spec.interiorWall = EditorGUILayout.Toggle("Interior Wall", spec.interiorWall);
        if (spec.interiorWall)
            spec.interiorGap = EditorGUILayout.FloatField(new GUIContent("Interior Doorway Width",
                "Gap between the rooms. 1.6 m or more lets the Climber crawl through."), spec.interiorGap);
        spec.reverb = EditorGUILayout.Toggle("Indoor Echo (Reverb)", spec.reverb);
        wallMaterial = (Material)EditorGUILayout.ObjectField(new GUIContent("Wall Material",
            "Empty = a random wall material from Assets/Materials/BuildingPalette"), wallMaterial, typeof(Material), false);

        GUILayout.Space(6);
        GUILayout.Label("Details", EditorStyles.boldLabel);
        ref var d = ref spec.details;
        d.windows = EditorGUILayout.Toggle("Windows", d.windows);
        if (d.windows)
        {
            EditorGUI.indentLevel++;
            d.litChance = EditorGUILayout.Slider("Lit Window Chance", d.litChance, 0f, 1f);
            d.flickerChance = EditorGUILayout.Slider("Flickering Chance", d.flickerChance, 0f, 1f);
            d.shopfront = EditorGUILayout.Toggle(new GUIContent("Shopfront", "Big windows each side of the door"), d.shopfront);
            d.highWindows = EditorGUILayout.Toggle(new GUIContent("High Windows", "One row high up (warehouse)"), d.highWindows);
            EditorGUI.indentLevel--;
        }
        d.parapet = EditorGUILayout.Toggle("Roof Edge (Parapet)", d.parapet);
        d.roofClutter = EditorGUILayout.Toggle("Rooftop Clutter", d.roofClutter);
        spec.roofHatch = EditorGUILayout.Toggle(new GUIContent("Roof Hatch", "A hatch the Climber can drop in through"), spec.roofHatch);
        d.sidewalk = EditorGUILayout.Toggle("Sidewalk", d.sidewalk);
        if (d.sidewalk)
        {
            EditorGUI.indentLevel++;
            d.sidewalkWidth = EditorGUILayout.FloatField("Sidewalk Width", d.sidewalkWidth);
            EditorGUI.indentLevel--;
        }

        GUILayout.Space(10);
        if (GUILayout.Button("Create Building", GUILayout.Height(30)))
            Create();

        EditorGUILayout.HelpBox(
            "Spawns at the point on the ground in the middle of the Scene view. " +
            "Rotate the parent object to face the door toward the street.", MessageType.Info);
    }

    void Create()
    {
        var root = BuildingBuilder.Build(spec, SpawnPoint(), Quaternion.identity, null,
                                         BuildingDetails.Palette(), new System.Random(), wallMaterial);
        Undo.RegisterCreatedObjectUndo(root, "Create Building");
        Selection.activeGameObject = root;
    }

    // Default doorway between the rooms: wide enough for the Climber (NavMesh radius 0.5) to crawl through
    const float InteriorGap = BuildingBuilder.InteriorGap;

    // Interior doorway lintels (added by hand on some buildings) are raised to this height, so the Climber
    // (NavMesh height 2.6 m) fits under them. The front door stays low, so it still can't walk in from the street.
    const float InteriorDoorHeight = 3f;

    // Lets the Climber crawl between the front and back room on every building: widens the gap between the
    // interior wall halves (any children named "Interior...") and raises a "Wall_Rear_Top" lintel over it.
    // Then re-bakes the climber's NavMesh.
    [MenuItem("Tools/RunForYourLife/Widen Interior Doorways")]
    static void WidenInteriorDoorways()
    {
        int widened = 0, raised = 0;
        foreach (var building in Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude))
        {
            if (building.Find("Wall_Front_Top") == null) continue;   // only building roots

            // The two interior wall halves: the left-most and right-most "Interior..." children
            Transform left = null, right = null;
            foreach (Transform ch in building)
            {
                if (!ch.name.StartsWith("Interior")) continue;
                if (left == null || ch.localPosition.x < left.localPosition.x) left = ch;
                if (right == null || ch.localPosition.x > right.localPosition.x) right = ch;
            }
            if (left != null && right != null && left != right)
            {
                float outerL = left.localPosition.x - left.localScale.x / 2f;
                float outerR = right.localPosition.x + right.localScale.x / 2f;
                float gapL = left.localPosition.x + left.localScale.x / 2f;
                float gapR = right.localPosition.x - right.localScale.x / 2f;
                float segment = (outerR - outerL - InteriorGap) / 2f;
                if (gapR - gapL < InteriorGap - 0.01f && segment >= 0.5f)
                {
                    Undo.RecordObjects(new Object[] { left, right }, "Widen Interior Doorways");
                    left.localScale = new Vector3(segment, left.localScale.y, left.localScale.z);
                    right.localScale = new Vector3(segment, right.localScale.y, right.localScale.z);
                    left.localPosition = new Vector3(outerL + segment / 2f, left.localPosition.y, left.localPosition.z);
                    right.localPosition = new Vector3(outerR - segment / 2f, right.localPosition.y, right.localPosition.z);
                    widened++;
                }
            }

            // Lintel over the interior doorway: keep its top, lift its bottom
            foreach (Transform ch in building)
            {
                if (ch.name.Trim() != "Wall_Rear_Top") continue;
                float top = ch.localPosition.y + ch.localScale.y / 2f;
                float bottom = ch.localPosition.y - ch.localScale.y / 2f;
                if (bottom >= InteriorDoorHeight - 0.01f || top - InteriorDoorHeight < 0.2f) continue;
                Undo.RecordObject(ch, "Raise Interior Lintel");
                ch.localScale = new Vector3(ch.localScale.x, top - InteriorDoorHeight, ch.localScale.z);
                ch.localPosition = new Vector3(ch.localPosition.x, (top + InteriorDoorHeight) / 2f, ch.localPosition.z);
                raised++;
            }
        }
        Debug.Log($"Interior doorways: widened {widened}, raised {raised} lintel(s).");
        if (widened + raised > 0) ClimberSetup.BakeClimberNavMesh();
    }

    static void AddReachPoint(Transform building, float doorX, float frontZ)
    {
        var rp = BuildingBuilder.AddReachPoint(building, doorX, frontZ);
        Undo.RegisterCreatedObjectUndo(rp, "Add Reach Point");
    }

    // For buildings made before reach points existed
    [MenuItem("Tools/RunForYourLife/Add Reach Points To Existing Buildings")]
    static void AddToExisting()
    {
        int added = 0;
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (t.name != "Wall_Front_Top" || t.parent == null) continue;
            var building = t.parent;
            if (building.GetComponentInChildren<ReachPoint>() != null) continue;

            AddReachPoint(building, t.localPosition.x, t.localPosition.z);
            added++;
        }
        Debug.Log($"Added {added} reach point(s).");
    }

    static Vector3 SpawnPoint()
    {
        var view = SceneView.lastActiveSceneView;
        if (view == null) return Vector3.zero;

        var cam = view.camera.transform;
        var ray = new Ray(cam.position, cam.forward);
        if (Physics.Raycast(ray, out var hit, 1000f))
            return new Vector3(hit.point.x, hit.point.y, hit.point.z);

        // Fall back to the ground plane (y = 0)
        var plane = new Plane(Vector3.up, Vector3.zero);
        if (plane.Raycast(ray, out float dist))
            return ray.GetPoint(dist);
        return view.pivot;
    }
}
