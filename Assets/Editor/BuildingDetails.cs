using UnityEditor;
using UnityEngine;

// Editor side of building details. The details themselves (windows, roof edge, rooftop clutter, sidewalks) are made
// by BuildingBuilder (Scripts) so the game can build buildings too. This file makes the materials as assets and keeps
// Tools -> RunForYourLife -> Upgrade Existing Buildings.
public static class BuildingDetails
{
    const string PalettePath = "Assets/Materials/BuildingPalette.asset";

    public static void Add(Transform building, float width, float depth, float height, float wallT,
                           float doorOffset, float doorWidth, BuildingBuilder.DetailOptions o)
        => BuildingBuilder.AddDetails(building, width, depth, height, wallT, doorOffset, doorWidth, o,
                                      Palette(), new System.Random());

    // The materials buildings get (Assets/Materials/BuildingPalette.asset). Made on first use from the materials the
    // Building Texturer creates; empty slots are filled in again each time, so it catches up after Use Texture Set.
    public static BuildingPalette Palette()
    {
        var pal = AssetDatabase.LoadAssetAtPath<BuildingPalette>(PalettePath);
        bool isNew = pal == null;
        if (isNew)
        {
            if (!AssetDatabase.IsValidFolder("Assets/Materials")) AssetDatabase.CreateFolder("Assets", "Materials");
            pal = ScriptableObject.CreateInstance<BuildingPalette>();
        }

        bool changed = isNew;
        if (pal.wallMaterials == null || pal.wallMaterials.Length == 0)
        {
            var walls = new System.Collections.Generic.List<Material>();
            foreach (var n in new[] { "brick_wall", "plaster_wall", "concrete_wall" })
                if (Load(n) != null) walls.Add(Load(n));
            pal.wallMaterials = walls.ToArray();
            changed |= walls.Count > 0;
        }
        changed |= Fill(ref pal.roofMaterial, Load("roof_tar"));
        changed |= Fill(ref pal.floorMaterial, Load("wood_floor"));
        changed |= Fill(ref pal.interiorWallMaterial, Load("interior_paint"));
        changed |= Fill(ref pal.sidewalkMaterial, Load("sidewalk") != null ? Load("sidewalk")
            : Mat("Sidewalk", new Color(0.42f, 0.42f, 0.42f), Color.black, 0.15f));
        changed |= Fill(ref pal.windowFrame, Mat("Window_Frame", new Color(0.08f, 0.08f, 0.09f), Color.black, 0.3f));
        changed |= Fill(ref pal.windowDark, Mat("Window_Dark", new Color(0.03f, 0.035f, 0.045f), Color.black, 0.92f));
        changed |= Fill(ref pal.windowLit, Mat("Window_Lit", new Color(1f, 0.85f, 0.6f), new Color(1f, 0.72f, 0.42f) * 1.6f, 0.6f));
        changed |= Fill(ref pal.propMetal, Mat("Prop_Metal", new Color(0.35f, 0.36f, 0.37f), Color.black, 0.35f));

        if (isNew) AssetDatabase.CreateAsset(pal, PalettePath);
        else if (changed) EditorUtility.SetDirty(pal);
        if (changed) AssetDatabase.SaveAssets();
        return pal;
    }

    static Material Load(string name) => AssetDatabase.LoadAssetAtPath<Material>($"Assets/Materials/{name}.mat");

    static bool Fill(ref Material slot, Material value)
    {
        if (slot != null || value == null) return false;
        slot = value;
        return true;
    }

    public static Material Mat(string name, Color color, Color emission, float smoothness)
    {
        if (!AssetDatabase.IsValidFolder("Assets/Materials")) AssetDatabase.CreateFolder("Assets", "Materials");
        string path = $"Assets/Materials/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) return mat;

        mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetColor("_BaseColor", color);
        mat.SetFloat("_Smoothness", smoothness);
        if (emission != Color.black)
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", emission);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    // ---------- Upgrade buildings made before details existed ----------

    [MenuItem("Tools/RunForYourLife/Upgrade Existing Buildings")]
    static void UpgradeExisting()
    {
        int upgraded = 0;
        foreach (var top in Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (top.name != "Wall_Front_Top" || top.parent == null) continue;
            var b = top.parent;
            if (b.Find("Details") != null) continue;

            var back = b.Find("Wall_Back");
            var left = b.Find("Wall_Left");
            if (back == null || left == null) continue;

            float width = back.localScale.x;
            float height = back.localScale.y;
            float t = back.localScale.z;
            float depth = left.localScale.z + t * 2f;
            float doorOffset = top.localPosition.x;
            float doorWidth = top.localScale.x;

            Add(b, width, depth, height, t, doorOffset, doorWidth, BuildingBuilder.DetailOptions.Default);
            Undo.RegisterCreatedObjectUndo(b.Find("Details").gameObject, "Upgrade Building");
            upgraded++;
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"Upgraded {upgraded} building(s). Re-bake the NavMesh, then run the Building Texturer again.");
    }
}
