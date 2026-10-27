using UnityEditor;
using UnityEngine;

// Tools -> RunForYourLife -> Building Generator
// Spawns an enterable box building (walls, roof, doorway) made of plain cubes with BoxColliders.
public class BuildingGenerator : EditorWindow
{
    float width = 8f;           // X
    float depth = 10f;          // Z
    float height = 4f;          // Y (one storey ~ 3-4 m)
    float wallThickness = 0.25f;
    float doorWidth = 2.2f;     // wide enough for the giant's forearm to reach through
    float doorHeight = 2.3f;
    float doorOffset = 0f;      // slide the door left/right along the front wall
    bool addInteriorWall = false;
    bool addRoofHatch = true;   // hatch the Climber drops in through
    bool addReverb = true;
    Material wallMaterial;
    BuildingDetails.Options details = BuildingDetails.Options.Default;

    [MenuItem("Tools/RunForYourLife/Building Generator")]
    static void Open() => GetWindow<BuildingGenerator>("Building Generator");

    void OnGUI()
    {
        GUILayout.Label("Size (meters)", EditorStyles.boldLabel);
        width = EditorGUILayout.FloatField("Width (X)", width);
        depth = EditorGUILayout.FloatField("Depth (Z)", depth);
        height = EditorGUILayout.FloatField("Height (Y)", height);
        wallThickness = EditorGUILayout.FloatField("Wall Thickness", wallThickness);

        GUILayout.Space(6);
        GUILayout.Label("Doorway (front = -Z side)", EditorStyles.boldLabel);
        doorWidth = EditorGUILayout.FloatField("Door Width", doorWidth);
        doorHeight = EditorGUILayout.FloatField("Door Height", doorHeight);
        doorOffset = EditorGUILayout.FloatField("Door Offset", doorOffset);

        GUILayout.Space(6);
        addInteriorWall = EditorGUILayout.Toggle("Interior Wall", addInteriorWall);
        addReverb = EditorGUILayout.Toggle("Indoor Echo (Reverb)", addReverb);
        wallMaterial = (Material)EditorGUILayout.ObjectField("Material", wallMaterial, typeof(Material), false);

        GUILayout.Space(6);
        GUILayout.Label("Details", EditorStyles.boldLabel);
        details.windows = EditorGUILayout.Toggle("Windows", details.windows);
        if (details.windows)
        {
            EditorGUI.indentLevel++;
            details.litChance = EditorGUILayout.Slider("Lit Window Chance", details.litChance, 0f, 1f);
            details.flickerChance = EditorGUILayout.Slider("Flickering Chance", details.flickerChance, 0f, 1f);
            EditorGUI.indentLevel--;
        }
        details.parapet = EditorGUILayout.Toggle("Roof Edge (Parapet)", details.parapet);
        details.roofClutter = EditorGUILayout.Toggle("Rooftop Clutter", details.roofClutter);
        addRoofHatch = EditorGUILayout.Toggle(new GUIContent("Roof Hatch", "A hatch the Climber can drop in through"), addRoofHatch);
        details.sidewalk = EditorGUILayout.Toggle("Sidewalk", details.sidewalk);
        if (details.sidewalk)
        {
            EditorGUI.indentLevel++;
            details.sidewalkWidth = EditorGUILayout.FloatField("Sidewalk Width", details.sidewalkWidth);
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
        // Clamp so the door always fits
        doorWidth = Mathf.Min(doorWidth, width - wallThickness * 2f - 0.2f);
        doorHeight = Mathf.Min(doorHeight, height - 0.2f);
        float maxOffset = (width - doorWidth) / 2f - wallThickness;
        doorOffset = Mathf.Clamp(doorOffset, -maxOffset, maxOffset);

        var root = new GameObject("Building");
        Undo.RegisterCreatedObjectUndo(root, "Create Building");
        root.transform.position = SpawnPoint();

        float t = wallThickness;
        float halfW = width / 2f, halfD = depth / 2f;

        // Back wall and side walls
        Box(root, "Wall_Back",  new Vector3(0, height / 2f, halfD - t / 2f), new Vector3(width, height, t));
        Box(root, "Wall_Left",  new Vector3(-halfW + t / 2f, height / 2f, 0), new Vector3(t, height, depth - t * 2f));
        Box(root, "Wall_Right", new Vector3(halfW - t / 2f, height / 2f, 0), new Vector3(t, height, depth - t * 2f));

        // Front wall split around the doorway
        float frontZ = -halfD + t / 2f;
        float doorLeft = doorOffset - doorWidth / 2f;
        float doorRight = doorOffset + doorWidth / 2f;

        float leftLen = doorLeft - (-halfW);
        if (leftLen > 0.01f)
            Box(root, "Wall_Front_L", new Vector3(-halfW + leftLen / 2f, height / 2f, frontZ), new Vector3(leftLen, height, t));

        float rightLen = halfW - doorRight;
        if (rightLen > 0.01f)
            Box(root, "Wall_Front_R", new Vector3(doorRight + rightLen / 2f, height / 2f, frontZ), new Vector3(rightLen, height, t));

        float lintelH = height - doorHeight;
        Box(root, "Wall_Front_Top", new Vector3(doorOffset, doorHeight + lintelH / 2f, frontZ), new Vector3(doorWidth, lintelH, t));

        // Roof and floor
        Box(root, "Roof",  new Vector3(0, height + t / 2f, 0), new Vector3(width, t, depth));
        Box(root, "Floor", new Vector3(0, 0.025f, 0), new Vector3(width - t * 2f, 0.05f, depth - t * 2f));

        // Optional interior wall with a gap, makes a back room to hide in
        if (addInteriorWall)
        {
            float gap = 1.2f;
            float innerW = width - t * 2f;
            float segment = (innerW - gap) / 2f;
            float z = depth * 0.15f;
            Box(root, "Interior_L", new Vector3(-innerW / 2f + segment / 2f, height / 2f, z), new Vector3(segment, height, t));
            Box(root, "Interior_R", new Vector3(innerW / 2f - segment / 2f, height / 2f, z), new Vector3(segment, height, t));
        }

        // Windows, roof edge, rooftop clutter, sidewalk
        BuildingDetails.Add(root.transform, width, depth, height, t, doorOffset, doorWidth, details);

        // Hatch in the roof: the Climber drops in through it
        if (addRoofHatch) RoofHatchTool.Add(root.transform);

        // Doorway marker for the giant: it crouches here and reaches in
        AddReachPoint(root.transform, doorOffset, frontZ);

        // Echo when you're inside
        if (addReverb)
        {
            var zone = root.AddComponent<AudioReverbZone>();
            zone.reverbPreset = AudioReverbPreset.Room;
            zone.minDistance = Mathf.Min(width, depth) * 0.4f;
            zone.maxDistance = Mathf.Max(width, depth) * 0.5f;
        }

        Selection.activeGameObject = root;
    }

    const float GiantStandDistance = 3.5f;   // how far outside the door the giant stands

    static void AddReachPoint(Transform building, float doorX, float frontZ)
    {
        var rp = new GameObject("ReachPoint");
        rp.transform.SetParent(building, false);
        rp.transform.localPosition = new Vector3(doorX, 0f, frontZ - GiantStandDistance);
        rp.transform.localRotation = Quaternion.identity;   // forward (+Z) points into the building
        rp.AddComponent<ReachPoint>();
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

    void Box(GameObject parent, string name, Vector3 localPos, Vector3 size)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = size;   // plain cubes are 1x1x1, so scale = size in meters
        go.isStatic = true;               // lets lighting + NavMesh treat it as level geometry
        go.AddComponent<BoxUV>();         // textures tile at real-world size
        if (wallMaterial != null)
            go.GetComponent<MeshRenderer>().sharedMaterial = wallMaterial;
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
