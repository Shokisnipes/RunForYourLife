using UnityEditor;
using UnityEngine;

// Tools -> RunForYourLife -> City Preview: builds a CityGenerator city next to the current map so you can look at
// layouts without playing. Tuning lives in Assets/Settings/CitySettings.asset (made on first use, with the building
// palette and a street lamp prefab filled in).
public static class CityPreviewTool
{
    const string SettingsPath = "Assets/Settings/CitySettings.asset";
    const string LampPrefabPath = "Assets/Prefabs/StreetLamp.prefab";
    const string SeedKey = "RunForYourLife.CityPreviewSeed";
    static readonly Vector3 PreviewOrigin = new Vector3(600f, 0f, 0f);   // well clear of the hand-built map

    [MenuItem("Tools/RunForYourLife/City Preview/Generate (New Seed)")]
    static void GenerateNew() => Generate(new System.Random().Next(1, 1000000));

    [MenuItem("Tools/RunForYourLife/City Preview/Regenerate (Same Seed)")]
    static void Regenerate() => Generate(EditorPrefs.GetInt(SeedKey, 1));

    [MenuItem("Tools/RunForYourLife/City Preview/Clear")]
    static void Clear()
    {
        foreach (var go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            if (go.name.StartsWith("City (seed")) Undo.DestroyObjectImmediate(go);
    }

    [MenuItem("Tools/RunForYourLife/City Preview/Select Settings")]
    static void SelectSettings() => Selection.activeObject = Settings();

    static void Generate(int seed)
    {
        Clear();
        EditorPrefs.SetInt(SeedKey, seed);
        var settings = Settings();
        var t0 = System.DateTime.Now;
        var city = CityGenerator.Generate(settings, seed, PreviewOrigin);
        Undo.RegisterCreatedObjectUndo(city.root, "Generate City Preview");

        int houses = 0, shops = 0, warehouses = 0;
        foreach (var b in city.buildings)
        {
            var back = b.transform.Find("Wall_Back");
            float area = back != null ? back.localScale.x * (b.transform.Find("Wall_Left").localScale.z) : 0f;
            if (area > 200f) warehouses++; else if (area > 95f) shops++; else houses++;
        }
        Debug.Log($"City preview seed {seed}: {city.buildings.Count} buildings (~{houses} houses, {shops} shops, " +
                  $"{warehouses} warehouses), {city.lamps.Count} lamps, built in {(System.DateTime.Now - t0).TotalMilliseconds:0} ms. " +
                  "Tools > RunForYourLife > City Preview > Clear removes it.");

        Selection.activeGameObject = city.root;
        var view = SceneView.lastActiveSceneView;
        if (view != null)
        {
            var size = settings.citySize;
            view.Frame(new Bounds(PreviewOrigin, new Vector3(size.x, 40f, size.y)), false);
        }
    }

    // The settings asset, made and filled in on first use
    public static CitySettings Settings()
    {
        var s = AssetDatabase.LoadAssetAtPath<CitySettings>(SettingsPath);
        bool isNew = s == null;
        if (isNew)
        {
            if (!AssetDatabase.IsValidFolder("Assets/Settings")) AssetDatabase.CreateFolder("Assets", "Settings");
            s = ScriptableObject.CreateInstance<CitySettings>();
        }

        if (s.palette == null) s.palette = BuildingDetails.Palette();
        if (s.streetLampPrefab == null) s.streetLampPrefab = LampPrefab();
        if (s.groundMaterial == null) s.groundMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/asphalt.mat");
        if (s.pavingMaterial == null) s.pavingMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/sidewalk.mat");

        if (isNew) AssetDatabase.CreateAsset(s, SettingsPath);
        else EditorUtility.SetDirty(s);
        AssetDatabase.SaveAssets();
        return s;
    }

    // A prefab copied from a street lamp in the scene (Tools > RunForYourLife > Street Lamps makes them)
    static GameObject LampPrefab()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(LampPrefabPath);
        if (prefab != null) return prefab;

        var source = Object.FindAnyObjectByType<StreetLamp>();
        if (source == null)
        {
            Debug.LogWarning("City Preview: no StreetLamp in the scene to make a prefab from, so the city has no lamps. " +
                             "Make one with Tools > RunForYourLife > Street Lamps, then generate again.");
            return null;
        }
        if (!AssetDatabase.IsValidFolder("Assets/Prefabs")) AssetDatabase.CreateFolder("Assets", "Prefabs");
        var copy = Object.Instantiate(source.gameObject);
        copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        copy.GetComponent<StreetLamp>().mode = StreetLamp.Mode.Normal;   // the generator picks the mode per lamp
        prefab = PrefabUtility.SaveAsPrefabAsset(copy, LampPrefabPath);
        Object.DestroyImmediate(copy);
        return prefab;
    }
}
