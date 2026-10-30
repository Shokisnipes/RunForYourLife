using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

// Tools > RunForYourLife > Ground Slam FX Setup
// Everything the near-miss crater (GroundSlamFx) needs:
//  1. the crack textures (Crack Decal Textures, default settings) if they aren't there yet
//  2. a soft dust puff texture (made here)
//  3. materials: GroundCrack_Decal (URP decal shader) and SlamDust (lit transparent particles)
//  4. the Decal renderer feature on every URP renderer in Assets (PC and Mobile), or decals don't draw at all
//  5. GroundSlamFx on the scene's MonsterAI object with the materials filled in (rubble = asphalt)
public static class GroundSlamSetup
{
    const string Decals = "Assets/Textures/Decals";
    const string PuffPath = Decals + "/DustPuff.png";
    const string DecalMatPath = "Assets/Materials/GroundCrack_Decal.mat";
    const string DustMatPath = "Assets/Materials/SlamDust.mat";
    const string RubbleMatPath = "Assets/Materials/asphalt.mat";

    [MenuItem("Tools/RunForYourLife/Ground Slam FX Setup")]
    public static void Run()
    {
        // 1. Crack textures
        if (AssetDatabase.LoadAssetAtPath<Texture2D>(Decals + "/GroundCrack_Albedo.png") == null)
            CrackDecalGenerator.Generate(7, 1024, 1f, 0.35f, 1.2f, 12, 6f);
        var crackAlbedo = AssetDatabase.LoadAssetAtPath<Texture2D>(Decals + "/GroundCrack_Albedo.png");
        var crackNormal = AssetDatabase.LoadAssetAtPath<Texture2D>(Decals + "/GroundCrack_Normal.png");

        // 2. Dust puff: a soft lumpy blob
        if (AssetDatabase.LoadAssetAtPath<Texture2D>(PuffPath) == null)
        {
            const int n = 128;
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = x / (n - 1f) * 2f - 1f, v = y / (n - 1f) * 2f - 1f;
                float r = Mathf.Sqrt(u * u + v * v);
                float lump = Mathf.PerlinNoise(x * 0.06f + 3.7f, y * 0.06f + 1.3f) * 0.6f + Mathf.PerlinNoise(x * 0.15f, y * 0.15f) * 0.4f;
                float a = Mathf.Clamp01(1f - r) * Mathf.Clamp01(1f - r);
                a *= Mathf.Lerp(0.5f, 1.2f, lump);
                px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
            }
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            tex.Apply();
            Directory.CreateDirectory(Decals);
            File.WriteAllBytes(PuffPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(PuffPath);
            var imp = (TextureImporter)AssetImporter.GetAtPath(PuffPath);
            imp.alphaIsTransparency = true;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.SaveAndReimport();
        }
        var puff = AssetDatabase.LoadAssetAtPath<Texture2D>(PuffPath);

        // 3. Materials
        var decalMat = AssetDatabase.LoadAssetAtPath<Material>(DecalMatPath);
        if (decalMat == null)
        {
            decalMat = new Material(Shader.Find("Shader Graphs/Decal"));
            AssetDatabase.CreateAsset(decalMat, DecalMatPath);
        }
        decalMat.SetTexture("Base_Map", crackAlbedo);
        decalMat.SetTexture("Normal_Map", crackNormal);
        decalMat.SetFloat("Normal_Blend", 1f);
        decalMat.enableInstancing = true;
        EditorUtility.SetDirty(decalMat);

        var dustMat = AssetDatabase.LoadAssetAtPath<Material>(DustMatPath);
        if (dustMat == null)
        {
            dustMat = new Material(Shader.Find("Universal Render Pipeline/Particles/Simple Lit"));
            AssetDatabase.CreateAsset(dustMat, DustMatPath);
        }
        dustMat.SetTexture("_BaseMap", puff);
        dustMat.SetColor("_BaseColor", Color.white);
        // Transparent, alpha blended, lit by the lamps and flashlights (unlit dust would glow in the dark)
        dustMat.SetFloat("_Surface", 1f);
        dustMat.SetFloat("_Blend", 0f);
        dustMat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        dustMat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        dustMat.SetFloat("_ZWrite", 0f);
        dustMat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        dustMat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        EditorUtility.SetDirty(dustMat);

        // 4. Decal renderer feature (same bookkeeping as URP's "Add Renderer Feature" button)
        int added = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData", new[] { "Assets" }))
        {
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(AssetDatabase.GUIDToAssetPath(guid));
            if (data == null || data.rendererFeatures.Exists(f => f is DecalRendererFeature)) continue;
            var feature = ScriptableObject.CreateInstance<DecalRendererFeature>();
            feature.name = "Decal";
            AssetDatabase.AddObjectToAsset(feature, data);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out string _, out long localId);
            var so = new SerializedObject(data);
            var list = so.FindProperty("m_RendererFeatures");
            var map = so.FindProperty("m_RendererFeatureMap");
            list.arraySize++;
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = feature;
            map.arraySize++;
            map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(data);
            added++;
        }

        // 5. The component on the monster
        var monster = Object.FindAnyObjectByType<MonsterAI>(FindObjectsInactive.Include);
        if (monster != null)
        {
            var fx = monster.GetComponent<GroundSlamFx>();
            if (fx == null) fx = Undo.AddComponent<GroundSlamFx>(monster.gameObject);
            fx.crackDecalMaterial = decalMat;
            fx.dustMaterial = dustMat;
            fx.rubbleMaterial = AssetDatabase.LoadAssetAtPath<Material>(RubbleMatPath);
            EditorUtility.SetDirty(fx);
            EditorSceneManager.MarkSceneDirty(monster.gameObject.scene);
            EditorSceneManager.SaveScene(monster.gameObject.scene);
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"Ground Slam FX Setup: decal feature added to {added} renderer(s), decal shader ok: " +
                  $"{decalMat.HasProperty("Base_Map") && decalMat.HasProperty("Normal_Map")}, monster: {(monster != null ? monster.name : "NOT FOUND")}");
    }
}
