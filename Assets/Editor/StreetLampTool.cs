using UnityEditor;
using UnityEngine;

// Tools -> RunForYourLife -> Street Lamps
// Places single lamps or rows of lamps. Some flicker, some are broken.
public class StreetLampTool : EditorWindow
{
    float height = 6f;
    Color lightColor = new Color(1f, 0.68f, 0.38f);   // sodium orange
    float intensity = 6f;
    float range = 12f;
    bool shadows = false;
    float litRadius = 5f;
    [Range(0f, 1f)] float flickerChance = 0.25f;
    [Range(0f, 1f)] float brokenChance = 0.15f;

    int rowCount = 5;
    float rowSpacing = 15f;
    enum Axis { X, Z }
    Axis rowAxis = Axis.Z;
    bool armFacesPositive = true;

    [MenuItem("Tools/RunForYourLife/Street Lamps")]
    static void Open() => GetWindow<StreetLampTool>("Street Lamps");

    void OnGUI()
    {
        GUILayout.Label("Lamp", EditorStyles.boldLabel);
        height = EditorGUILayout.FloatField("Height", height);
        lightColor = EditorGUILayout.ColorField("Light Color", lightColor);
        intensity = EditorGUILayout.FloatField("Intensity", intensity);
        range = EditorGUILayout.FloatField("Range", range);
        shadows = EditorGUILayout.Toggle("Shadows (costly)", shadows);
        litRadius = EditorGUILayout.FloatField("Lit Radius (giant sees you)", litRadius);
        flickerChance = EditorGUILayout.Slider("Flicker Chance", flickerChance, 0f, 1f);
        brokenChance = EditorGUILayout.Slider("Broken Chance", brokenChance, 0f, 1f);

        GUILayout.Space(8);
        if (GUILayout.Button("Create Lamp", GUILayout.Height(28)))
        {
            var lamp = CreateLamp(SpawnPoint(), Quaternion.identity);
            Selection.activeGameObject = lamp;
        }

        GUILayout.Space(8);
        GUILayout.Label("Row", EditorStyles.boldLabel);
        rowCount = EditorGUILayout.IntField("Count", rowCount);
        rowSpacing = EditorGUILayout.FloatField("Spacing (m)", rowSpacing);
        rowAxis = (Axis)EditorGUILayout.EnumPopup("Along Axis", rowAxis);
        armFacesPositive = EditorGUILayout.Toggle("Arm Points + Side", armFacesPositive);
        if (GUILayout.Button("Create Row", GUILayout.Height(28))) CreateRow();

        EditorGUILayout.HelpBox(
            "Spawns where the middle of the Scene view hits the ground.\n" +
            "Put lamps on the sidewalk within ~2 m of buildings so they don't narrow the giant's streets. " +
            "The arm (and light) points along the lamp's blue arrow, so rotate lamps to hang over the road.",
            MessageType.Info);
    }

    void CreateRow()
    {
        Vector3 start = SpawnPoint();
        Vector3 dir = rowAxis == Axis.Z ? Vector3.forward : Vector3.right;
        Vector3 side = rowAxis == Axis.Z ? Vector3.right : Vector3.forward;
        if (!armFacesPositive) side = -side;
        Quaternion rot = Quaternion.LookRotation(side);

        var parent = new GameObject("StreetLamps");
        Undo.RegisterCreatedObjectUndo(parent, "Create Lamp Row");
        parent.transform.position = start;

        for (int i = 0; i < rowCount; i++)
        {
            var lamp = CreateLamp(start + dir * rowSpacing * i, rot);
            Undo.SetTransformParent(lamp.transform, parent.transform, "Parent Lamp");
        }
        Selection.activeGameObject = parent;
    }

    GameObject CreateLamp(Vector3 position, Quaternion rotation)
    {
        var root = new GameObject("StreetLamp");
        Undo.RegisterCreatedObjectUndo(root, "Create Street Lamp");
        root.transform.SetPositionAndRotation(position, rotation);

        var poleMat = GetMaterial("LampPole", new Color(0.12f, 0.12f, 0.13f), Color.black);
        var bulbMat = GetMaterial("LampBulb", lightColor, lightColor * 3f);

        // Pole (keeps its collider)
        var pole = Part(root, PrimitiveType.Cylinder, "Pole", new Vector3(0, height / 2f, 0), new Vector3(0.18f, height / 2f, 0.18f), poleMat);
        pole.isStatic = true;

        // Arm + head (no colliders)
        Part(root, PrimitiveType.Cube, "Arm", new Vector3(0, height - 0.1f, 0.65f), new Vector3(0.1f, 0.1f, 1.3f), poleMat, false);
        Part(root, PrimitiveType.Cube, "Head", new Vector3(0, height - 0.2f, 1.3f), new Vector3(0.45f, 0.15f, 0.6f), poleMat, false);
        var bulb = Part(root, PrimitiveType.Cube, "Bulb", new Vector3(0, height - 0.3f, 1.3f), new Vector3(0.35f, 0.05f, 0.5f), bulbMat, false);
        bulb.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        // Light pointing down
        var lightGo = new GameObject("Light");
        lightGo.transform.SetParent(root.transform, false);
        lightGo.transform.localPosition = new Vector3(0, height - 0.4f, 1.3f);
        lightGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Spot;
        light.color = lightColor;
        light.intensity = intensity;
        light.range = range;
        light.spotAngle = 120f;
        light.innerSpotAngle = 60f;
        light.shadows = shadows ? LightShadows.Soft : LightShadows.None;

        var lamp = root.AddComponent<StreetLamp>();
        lamp.lamp = light;
        lamp.bulb = bulb.GetComponent<Renderer>();
        lamp.litRadius = litRadius;
        float r = Random.value;
        lamp.mode = r < brokenChance ? StreetLamp.Mode.Broken
                  : r < brokenChance + flickerChance ? StreetLamp.Mode.Flickering
                  : StreetLamp.Mode.Normal;
        return root;
    }

    static GameObject Part(GameObject parent, PrimitiveType type, string name, Vector3 pos, Vector3 scale, Material mat, bool keepCollider = true)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        if (!keepCollider) Object.DestroyImmediate(go.GetComponent<Collider>());
        return go;
    }

    static Material GetMaterial(string name, Color color, Color emission)
    {
        string folder = "Assets/Materials";
        if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets", "Materials");
        string path = $"{folder}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) return mat;

        mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetColor("_BaseColor", color);
        mat.SetFloat("_Smoothness", 0.3f);
        if (emission != Color.black)
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", emission);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    static Vector3 SpawnPoint()
    {
        var view = SceneView.lastActiveSceneView;
        if (view == null) return Vector3.zero;
        var cam = view.camera.transform;
        var ray = new Ray(cam.position, cam.forward);
        if (Physics.Raycast(ray, out var hit, 1000f)) return hit.point;
        var plane = new Plane(Vector3.up, Vector3.zero);
        return plane.Raycast(ray, out float d) ? ray.GetPoint(d) : view.pivot;
    }
}
