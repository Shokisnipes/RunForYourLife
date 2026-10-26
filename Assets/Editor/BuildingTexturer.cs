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
                else if (part.name == "Roof") m = roofMaterial;
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
