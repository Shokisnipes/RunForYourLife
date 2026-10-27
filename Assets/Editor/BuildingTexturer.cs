using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Tools -> RunForYourLife -> Building Texturer
// 1) Turns downloaded texture folders into materials.
// 2) Puts materials on every building (random wall material per building) with real-world tiling.
public class BuildingTexturer : EditorWindow
{
    [SerializeField] string textureFolder = "Assets/Textures";
    [SerializeField] List<Material> wallMaterials = new List<Material>();
    [SerializeField] Material roofMaterial;
    [SerializeField] Material floorMaterial;
    [SerializeField] Material interiorWallMaterial;
    [SerializeField] Material sidewalkMaterial;
    [SerializeField] float metersPerTile = 2.5f;

    SerializedObject so;
    Vector2 scroll;

    [MenuItem("Tools/RunForYourLife/Building Texturer")]
    static void Open() => GetWindow<BuildingTexturer>("Building Texturer");

    void OnEnable() => so = new SerializedObject(this);

    void OnGUI()
    {
        so.Update();
        scroll = EditorGUILayout.BeginScrollView(scroll);

        GUILayout.Label("Quick start", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Uses the texture set in Assets/Textures (brick, plaster, concrete, interior paint, roof, " +
                                "wood floor, sidewalk, asphalt, windows): makes the materials, fills the slots below, " +
                                "textures the street and windows, and applies everything to all buildings.", MessageType.None);
        if (GUILayout.Button("Use Texture Set (one click)", GUILayout.Height(34))) UseTextureSet();

        GUILayout.Space(12);
        GUILayout.Label("1. Make materials from textures", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(so.FindProperty("textureFolder"));
        EditorGUILayout.HelpBox("Put each texture in its own subfolder (e.g. Assets/Textures/red_brick/). " +
                                "Each subfolder becomes one material in Assets/Materials.", MessageType.None);
        if (GUILayout.Button("Create Materials", GUILayout.Height(26))) CreateMaterials();

        GUILayout.Space(12);
        GUILayout.Label("2. Apply to buildings", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(so.FindProperty("wallMaterials"), true);
        EditorGUILayout.PropertyField(so.FindProperty("roofMaterial"));
        EditorGUILayout.PropertyField(so.FindProperty("floorMaterial"));
        EditorGUILayout.PropertyField(so.FindProperty("interiorWallMaterial"), new GUIContent("Interior Wall (optional)"));
        EditorGUILayout.PropertyField(so.FindProperty("sidewalkMaterial"), new GUIContent("Sidewalk (optional)"));
        EditorGUILayout.PropertyField(so.FindProperty("metersPerTile"));

        if (GUILayout.Button("Apply To All Buildings", GUILayout.Height(26))) ApplyToBuildings();
        if (GUILayout.Button("Apply Wall Material To Selected Objects")) ApplyToSelected();

        EditorGUILayout.EndScrollView();
        so.ApplyModifiedProperties();
    }

    // ---------- 1. Materials ----------

    void CreateMaterials()
    {
        if (!AssetDatabase.IsValidFolder(textureFolder)) { Debug.LogWarning($"No folder at {textureFolder}"); return; }
        if (!AssetDatabase.IsValidFolder("Assets/Materials")) AssetDatabase.CreateFolder("Assets", "Materials");

        var shader = Shader.Find("Universal Render Pipeline/Lit");
        int made = 0;

        foreach (var folder in AssetDatabase.GetSubFolders(textureFolder))
        {
            if (Path.GetFileName(folder).StartsWith("window")) continue;   // windows are handled separately
            Texture2D albedo = null, normal = null;
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string n = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

                if (n.Contains("nor") || n.Contains("normal"))
                {
                    if (n.Contains("_dx") && normal != null) continue;   // prefer the OpenGL (_gl) normal map
                    var ti = (TextureImporter)AssetImporter.GetAtPath(path);
                    if (ti.textureType != TextureImporterType.NormalMap)
                    {
                        ti.textureType = TextureImporterType.NormalMap;
                        ti.SaveAndReimport();
                    }
                    normal = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                }
                else if (n.Contains("diff") || n.Contains("albedo") || n.Contains("color") || n.Contains("col") || n.Contains("basecolor"))
                {
                    albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                }
            }

            if (albedo == null) { Debug.LogWarning($"No color/diffuse texture found in {folder}"); continue; }

            string matPath = $"Assets/Materials/{Path.GetFileName(folder)}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            bool isNew = mat == null;
            if (isNew) mat = new Material(shader);

            mat.SetTexture("_BaseMap", albedo);
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Smoothness", 0.15f);
            if (normal != null)
            {
                mat.SetTexture("_BumpMap", normal);
                mat.SetFloat("_BumpScale", 1f);
                mat.EnableKeyword("_NORMALMAP");
            }

            if (isNew) AssetDatabase.CreateAsset(mat, matPath);
            else EditorUtility.SetDirty(mat);
            made++;
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"Created/updated {made} material(s) in Assets/Materials.");
    }

    // ---------- Quick start ----------

    void UseTextureSet()
    {
        textureFolder = "Assets/Textures";
        CreateMaterials();

        wallMaterials = new List<Material>();
        foreach (var n in new[] { "brick_wall", "plaster_wall", "concrete_wall" })
            if (LoadMat(n) != null) wallMaterials.Add(LoadMat(n));
        roofMaterial = LoadMat("roof_tar");
        floorMaterial = LoadMat("wood_floor");
        interiorWallMaterial = LoadMat("interior_paint");
        sidewalkMaterial = LoadMat("sidewalk");

        SetSmooth(roofMaterial, 0.25f);
        SetSmooth(floorMaterial, 0.3f);

        // Windows: grimy dark glass, and lit ones with half-drawn blinds that glow
        var darkTex = LoadTex("window_dark");
        var litTex = LoadTex("window_lit");
        var dark = BuildingDetails.Mat("Window_Dark", new Color(0.03f, 0.035f, 0.045f), Color.black, 0.92f);
        var lit = BuildingDetails.Mat("Window_Lit", new Color(1f, 0.85f, 0.6f), new Color(1f, 0.72f, 0.42f) * 1.6f, 0.6f);
        if (darkTex != null)
        {
            dark.SetTexture("_BaseMap", darkTex);
            dark.SetColor("_BaseColor", Color.white);
            dark.SetFloat("_Smoothness", 0.92f);
            EditorUtility.SetDirty(dark);
        }
        if (litTex != null)
        {
            lit.SetTexture("_BaseMap", litTex);
            lit.SetColor("_BaseColor", Color.white);
            lit.EnableKeyword("_EMISSION");
            lit.SetTexture("_EmissionMap", litTex);
            lit.SetColor("_EmissionColor", Color.white * 1.4f);
            lit.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            EditorUtility.SetDirty(lit);
        }

        // Street: wet asphalt, tiled so one copy covers about 5 m
        var asphalt = LoadMat("asphalt");
        var plane = GameObject.Find("Plane");
        if (asphalt != null && plane != null && plane.TryGetComponent(out Renderer pr))
        {
            Undo.RecordObject(pr, "Street Material");
            pr.sharedMaterial = asphalt;
            float tiles = Mathf.Max(1f, plane.transform.lossyScale.x * 10f / 5f);
            asphalt.SetTextureScale("_BaseMap", new Vector2(tiles, tiles));
            asphalt.SetFloat("_Smoothness", 0.55f);
            EditorUtility.SetDirty(asphalt);
        }

        AssetDatabase.SaveAssets();
        so.Update();
        ApplyToBuildings();
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
        Debug.Log("Texture set applied. Save the scene (Ctrl+S).");
    }

    static Material LoadMat(string name) => AssetDatabase.LoadAssetAtPath<Material>($"Assets/Materials/{name}.mat");

    static Texture2D LoadTex(string folder)
    {
        foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { $"Assets/Textures/{folder}" }))
            return AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(guid));
        return null;
    }

    static void SetSmooth(Material m, float v)
    {
        if (m == null) return;
        m.SetFloat("_Smoothness", v);
        EditorUtility.SetDirty(m);
    }

    // ---------- 2. Apply ----------

    void ApplyToBuildings()
    {
        if (wallMaterials.Count == 0) { Debug.LogWarning("Add at least one wall material."); return; }

        int count = 0;
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (t.name != "Wall_Front_Top" || t.parent == null) continue;
            var building = t.parent;

            // Same building always gets the same wall material (based on where it is)
            Vector3 p = building.position;
            int pick = Mathf.Abs(Mathf.RoundToInt(p.x * 7.1f + p.z * 3.3f)) % wallMaterials.Count;
            Material wall = wallMaterials[pick];

            foreach (var r in building.GetComponentsInChildren<MeshRenderer>(true))
            {
                var part = r.transform;

                // Windows and rooftop props keep their own materials
                Material m = null;
                if (part.name.StartsWith("Wall_")) m = wall;                       // walls + roof edge
                else if (part.name.StartsWith("Interior_")) m = interiorWallMaterial != null ? interiorWallMaterial : wall;
                else if (part.name == "Roof" || part.name.StartsWith("Roof_")) m = roofMaterial;   // split around a hatch
                else if (part.name == "Floor") m = floorMaterial;
                else if (part.name.StartsWith("Sidewalk")) m = sidewalkMaterial;
                if (m == null) continue;

                Undo.RecordObject(r, "Texture Building");
                r.sharedMaterial = m;
                AddBoxUV(part.gameObject);
            }
            count++;
        }
        Debug.Log($"Textured {count} building(s).");
    }

    void ApplyToSelected()
    {
        if (wallMaterials.Count == 0) return;
        foreach (var go in Selection.gameObjects)
        {
            var r = go.GetComponent<MeshRenderer>();
            if (r == null) continue;
            Undo.RecordObject(r, "Texture Object");
            r.sharedMaterial = wallMaterials[0];
            // Plain cubes get real-world tiling; ProBuilder objects already have their own UVs
            var mf = go.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && mf.sharedMesh.name == "Cube") AddBoxUV(go);
        }
    }

    void AddBoxUV(GameObject go)
    {
        var uv = go.GetComponent<BoxUV>();
        if (uv == null) uv = Undo.AddComponent<BoxUV>(go);
        uv.metersPerTile = metersPerTile;
    }
}
