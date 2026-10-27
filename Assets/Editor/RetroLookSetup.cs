using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Tools -> RunForYourLife -> Retro Look Setup
// One click, safe to run again: the RetroPost material, a full-screen pass for it on every URP renderer, the retro
// Volume profile (glow, grain, vignette), and a "Retro Look" object in the scene (RetroLook + a global Volume).
public static class RetroLookSetup
{
    const string MaterialPath = "Assets/Materials/RetroPost.mat";
    const string ProfilePath = "Assets/Settings/RetroVolumeProfile.asset";
    const string FeatureName = "RetroPost";

    [MenuItem("Tools/RunForYourLife/Retro Look Setup")]
    public static void Setup()
    {
        // 1. Material for the colour-steps/dither shader
        var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (mat == null)
        {
            mat = new Material(Shader.Find("RunForYourLife/RetroPost"));
            AssetDatabase.CreateAsset(mat, MaterialPath);
        }

        // 2. A full-screen pass on every URP renderer of the project (PC and Mobile), after post-processing.
        //    Only under Assets: the URP package has its own default renderer, which must never be edited.
        int added = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData", new[] { "Assets" }))
        {
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(AssetDatabase.GUIDToAssetPath(guid));
            if (data == null || data.rendererFeatures.Exists(f => f != null && f.name == FeatureName)) continue;

            var feature = ScriptableObject.CreateInstance<FullScreenPassRendererFeature>();
            feature.name = FeatureName;
            feature.passMaterial = mat;
            feature.injectionPoint = FullScreenPassRendererFeature.InjectionPoint.AfterRenderingPostProcessing;
            feature.fetchColorBuffer = true;
            feature.requirements = ScriptableRenderPassInput.Color;
            AssetDatabase.AddObjectToAsset(feature, data);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out string _, out long localId);

            // Same bookkeeping as URP's own "Add Renderer Feature" button
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

        // 3. Retro volume profile: more glow, grain and vignette, a little less washed-out than the normal grade
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, ProfilePath);
            var bloom = Add<Bloom>(profile);
            bloom.intensity.Override(1.6f);
            bloom.threshold.Override(0.75f);
            bloom.scatter.Override(0.75f);
            var grain = Add<FilmGrain>(profile);
            grain.type.Override(FilmGrainLookup.Medium3);
            grain.intensity.Override(0.3f);
            grain.response.Override(0.7f);
            var vig = Add<Vignette>(profile);
            vig.intensity.Override(0.3f);
            vig.smoothness.Override(0.5f);
            var color = Add<ColorAdjustments>(profile);
            color.saturation.Override(-12f);
            color.contrast.Override(8f);    // more crushes the night to black
            EditorUtility.SetDirty(profile);
        }

        // 4. Scene object
        var look = Object.FindAnyObjectByType<RetroLook>();
        if (look == null)
        {
            var go = new GameObject("Retro Look");
            Undo.RegisterCreatedObjectUndo(go, "Retro Look Setup");
            var vol = go.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 10f;          // above the normal Global Volume
            vol.weight = 0f;             // RetroLook fades it in
            vol.sharedProfile = profile;
            look = go.AddComponent<RetroLook>();
            look.retroVolume = vol;
        }

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log($"Retro Look Setup: full-screen pass added to {added} renderer(s). Switch it in the main or pause menu (LOOK). Save the scene.");
    }

    static T Add<T>(VolumeProfile profile) where T : VolumeComponent
    {
        var c = profile.Add<T>(true);
        c.name = typeof(T).Name;
        c.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
        AssetDatabase.AddObjectToAsset(c, profile);
        return c;
    }
}
