using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Tools -> RunForYourLife -> Vehicle Setup
// One click: turns the car models in Assets/Models/Vehicle into prefabs in Assets/Prefabs/Vehicles, each scaled to real
// size, facing +Z, sitting on y = 0, with URP materials made from the model's own colours, one box collider and a
// Vehicle component. Then puts them in CitySettings.vehicles so CityGenerator parks them instead of grey boxes.
// Also makes the generator night job's props (Assets/Prefabs/Generator and GasCan). Safe to run again. (The truck came as a .glb, which Unity can't read: it was converted to Truck.fbx with Blender.)
public static class VehicleSetup
{
    const string ModelFolder = "Assets/Models/Vehicle";
    const string MaterialFolder = ModelFolder + "/Materials";
    const string PrefabFolder = "Assets/Prefabs/Vehicles";
    const string SettingsPath = "Assets/Settings/CitySettings.asset";

    // name, model file, turn (degrees, so its front faces +Z), length in metres (0 = already real size),
    // paint materials (get a random colour), texture for untextured "Atlas"-style materials, too big for a stall
    static readonly (string name, string model, float yaw, float length, string[] paint, string texture, bool big)[] Cars =
    {
        ("Car_Sedan",     "Car 1/NormalCar1.obj",        0f,  0f,    new[] { "Blue" },      null,                        false),
        ("Car_Pickup",    "Car 2/Vehicle_Pickup.fbx",    0f,  0f,    new string[0],         "Car 4/Zombie_Atlas.png",    true),
        ("Car_Terrano",   "Car 3/Terrano.obj",           90f, 4.25f, new[] { "Color_I07" }, null,                        false),
        ("Car_Truck",     "Car 4/Truck.fbx",             0f,  0f,    new string[0],         null,                        true),
        ("Car_DMC",       "Car 5/DMC.obj",               180f, 4.2f,  new string[0],         null,                        false),
        ("Car_Ambulance", "Car 6/1373 Ambulance.obj",    0f,  6f,    new string[0],         "Car 6/1373 Ambulance.png",  true),
    };

    [MenuItem("Tools/RunForYourLife/Vehicle Setup")]
    public static void Run()
    {
        Directory.CreateDirectory(MaterialFolder);
        Directory.CreateDirectory(PrefabFolder);
        AssetDatabase.Refresh();

        var made = new List<Vehicle>();
        foreach (var c in Cars)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>($"{ModelFolder}/{c.model}");
            if (model == null) { Debug.LogWarning($"Vehicle Setup: missing {c.model}"); continue; }
            Texture2D tex = c.texture != null ? PaletteTexture($"{ModelFolder}/{c.texture}") : null;

            var root = new GameObject(c.name);
            var body = (GameObject)PrefabUtility.InstantiatePrefab(model);
            body.name = "Model";
            body.transform.SetParent(root.transform, false);
            body.transform.localRotation = Quaternion.Euler(0f, c.yaw, 0f);

            // Scale to the wanted length, then sit it on the ground, centred
            var b = Bounds(root);
            float scale = c.length > 0f ? c.length / b.size.z : 1f;
            body.transform.localScale = Vector3.one * scale;
            b = Bounds(root);
            body.transform.localPosition = new Vector3(-b.center.x, -b.min.y, -b.center.z);
            b = Bounds(root);

            // Our own URP materials, so they can be tuned (glass, tyres, lights) and recoloured at runtime
            var paint = new List<Material>();
            var copies = new Dictionary<Material, Material>();
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    if (!copies.TryGetValue(mats[i], out var m))
                    {
                        m = MakeMaterial(c.name, mats[i], tex);
                        copies[mats[i]] = m;
                        if (c.paint.Contains(mats[i].name)) paint.Add(m);
                    }
                    mats[i] = m;
                }
                r.sharedMaterials = mats;
            }
            foreach (var t in root.GetComponentsInChildren<Transform>()) t.gameObject.isStatic = true;

            var box = root.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, b.size.y / 2f, 0f);
            box.size = b.size;
            var v = root.AddComponent<Vehicle>();
            v.size = b.size;
            v.paint = paint.ToArray();
            v.big = c.big;

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, $"{PrefabFolder}/{c.name}.prefab");
            Object.DestroyImmediate(root);
            made.Add(prefab.GetComponent<Vehicle>());
            Debug.Log($"Vehicle Setup: {c.name} {b.size.x:0.00} x {b.size.y:0.00} x {b.size.z:0.00} m, {copies.Count} materials, {paint.Count} paint");
        }

        var settings = AssetDatabase.LoadAssetAtPath<CitySettings>(SettingsPath);
        if (settings != null)
        {
            settings.vehicles = made.ToArray();
            EditorUtility.SetDirty(settings);
        }

        // The generator night job's props (GeneratorJob): a ~0.9 m portable generator and a 0.5 m jerry can
        Prop("Generator", "Assets/Generator&GasCan/Generator.fbx", 90f, 0.9f, 0f, true);
        Prop("GasCan", "Assets/Generator&GasCan/GasCan.fbx", 90f, 0f, 0.5f, false);
        AssetDatabase.SaveAssets();
    }

    // A prop prefab in Assets/Prefabs: turned, scaled to a length (Z) or a height, sitting on y = 0, URP materials
    static void Prop(string name, string modelPath, float yaw, float length, float height, bool collider)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        if (model == null) { Debug.LogWarning($"Vehicle Setup: missing {modelPath}"); return; }
        string folder = Path.GetDirectoryName(modelPath).Replace('\\', '/') + "/Materials";
        Directory.CreateDirectory(folder);
        AssetDatabase.Refresh();

        var root = new GameObject(name);
        var body = (GameObject)PrefabUtility.InstantiatePrefab(model);
        body.name = "Model";
        body.transform.SetParent(root.transform, false);
        body.transform.localRotation = Quaternion.Euler(0f, yaw, 0f) * body.transform.localRotation;
        var b = Bounds(root);
        float scale = length > 0f ? length / b.size.z : height / b.size.y;
        body.transform.localScale *= scale;
        b = Bounds(root);
        body.transform.localPosition += new Vector3(-b.center.x, -b.min.y, -b.center.z);
        b = Bounds(root);

        var copies = new Dictionary<Material, Material>();
        foreach (var r in root.GetComponentsInChildren<Renderer>())
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) continue;
                if (!copies.TryGetValue(mats[i], out var m)) copies[mats[i]] = m = MakeMaterial(name, mats[i], null, folder);
                mats[i] = m;
            }
            r.sharedMaterials = mats;
        }
        if (collider)
        {
            var box = root.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, b.size.y / 2f, 0f);
            box.size = b.size;
        }
        PrefabUtility.SaveAsPrefabAsset(root, $"Assets/Prefabs/{name}.prefab");
        Object.DestroyImmediate(root);
        Debug.Log($"Vehicle Setup: {name} {b.size.x:0.00} x {b.size.y:0.00} x {b.size.z:0.00} m");
    }

    static Bounds Bounds(GameObject go)
    {
        var rs = go.GetComponentsInChildren<Renderer>();
        var b = rs[0].bounds;
        foreach (var r in rs) b.Encapsulate(r.bounds);
        return b;
    }

    // Small colour-palette textures: no blur between the colour squares, no mipmaps bleeding them together
    static Texture2D PaletteTexture(string path)
    {
        if (AssetImporter.GetAtPath(path) is TextureImporter ti && (ti.filterMode != FilterMode.Point || ti.mipmapEnabled))
        {
            ti.filterMode = FilterMode.Point;
            ti.mipmapEnabled = false;
            ti.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // A URP Lit copy of an imported material, with smoothness/metal guessed from its name
    static Material MakeMaterial(string car, Material src, Texture2D tex, string folder = MaterialFolder)
    {
        string path = $"{folder}/{car}_{src.name.Replace('.', '_')}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(m, path);
        }
        Color col = src.HasProperty("_BaseColor") ? src.GetColor("_BaseColor") : src.HasProperty("_Color") ? src.GetColor("_Color") : Color.grey;
        Texture main = src.mainTexture;
        if (main == null && tex != null && (src.name.Contains("Atlas") || src.name.Contains("Ambulance"))) { main = tex; col = Color.white; }

        string n = src.name.ToLowerInvariant();
        float smooth = 0.35f, metal = 0f;
        if (n.Contains("glass") || n.Contains("window")) { col = new Color(0.03f, 0.035f, 0.04f); smooth = 0.92f; }
        else if (n.Contains("tire") || n.Contains("ban") || n == "black") smooth = 0.15f;
        else if (n.Contains("trim") || n.Contains("chasis")) { smooth = 0.65f; metal = 0.8f; }
        else if (n.Contains("light") || n.Contains("lampu") || n.Contains("riting") || n == "rem") smooth = 0.85f;
        else if (main == null) smooth = 0.55f;   // flat-colour bodywork: a bit of shine

        m.SetColor("_BaseColor", col);
        m.SetTexture("_BaseMap", main);
        m.SetFloat("_Smoothness", smooth);
        m.SetFloat("_Metallic", metal);
        m.enableInstancing = true;
        EditorUtility.SetDirty(m);
        return m;
    }
}
