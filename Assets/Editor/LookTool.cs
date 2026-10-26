using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Tools -> RunForYourLife -> Make It Look Good
// One click: cinematic color grading, glow, film look, sharp edges, wet streets, moonlight, creepy giant skin.
public class LookTool : EditorWindow
{
    bool grading = true;
    bool cameras = true;
    bool wetStreet = true;
    bool moonlight = true;
    bool monsterSkin = true;
    Color skinTint = new Color(0.80f, 0.71f, 0.67f);   // pale, slightly fleshy
    float skinGloss = 0.55f;

    [MenuItem("Tools/RunForYourLife/Make It Look Good")]
    static void Open() => GetWindow<LookTool>("Make It Look Good");

    void OnGUI()
    {
        GUILayout.Label("What to apply", EditorStyles.boldLabel);
        grading = EditorGUILayout.ToggleLeft("Color grading + glow + film look (Global Volume)", grading);
        cameras = EditorGUILayout.ToggleLeft("Sharp edges (SMAA) on all cameras + Player prefab", cameras);
        wetStreet = EditorGUILayout.ToggleLeft("Wet street (glossy Plane material)", wetStreet);
        moonlight = EditorGUILayout.ToggleLeft("Cold moonlight with soft shadows", moonlight);
        monsterSkin = EditorGUILayout.ToggleLeft("Pale, glossy skin on the giant", monsterSkin);
        if (monsterSkin)
        {
            EditorGUI.indentLevel++;
            skinTint = EditorGUILayout.ColorField("Skin Tint", skinTint);
            skinGloss = EditorGUILayout.Slider("Skin Gloss", skinGloss, 0f, 1f);
            EditorGUI.indentLevel--;
        }

        GUILayout.Space(10);
        if (GUILayout.Button("Apply", GUILayout.Height(32)))
        {
            if (grading) ApplyGrading();
            if (cameras) ApplyCameras();
            if (wetStreet) ApplyWetStreet();
            if (moonlight) ApplyMoonlight();
            if (monsterSkin) ApplyMonsterSkin();
            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("Look applied. Save the scene (Ctrl+S).");
        }

        EditorGUILayout.HelpBox(
            "Everything here can be tweaked afterwards: select Global Volume to adjust the grading, " +
            "and the materials in Assets/Materials for the street and the giant.", MessageType.Info);
    }

    // ---------- Color grading ----------

    static void ApplyGrading()
    {
        VolumeProfile profile = null;
        foreach (var v in Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
            if (v.isGlobal && v.sharedProfile != null) { profile = v.sharedProfile; break; }
        if (profile == null) { Debug.LogWarning("No Global Volume with a profile found in the scene."); return; }

        var ca = Get<ColorAdjustments>(profile);
        ca.postExposure.Override(0.2f);
        ca.contrast.Override(22f);
        ca.saturation.Override(-35f);

        var st = Get<SplitToning>(profile);
        st.shadows.Override(new Color(0.32f, 0.50f, 0.58f));     // cold teal shadows
        st.highlights.Override(new Color(1f, 0.72f, 0.48f));     // warm sodium highlights
        st.balance.Override(-15f);

        var wb = Get<WhiteBalance>(profile);
        wb.temperature.Override(-8f);

        var bloom = Get<Bloom>(profile);
        bloom.threshold.Override(1f);
        bloom.intensity.Override(0.7f);
        bloom.scatter.Override(0.65f);

        var vig = Get<Vignette>(profile);
        vig.color.Override(Color.black);
        vig.intensity.Override(0.38f);
        vig.smoothness.Override(0.45f);

        var grain = Get<FilmGrain>(profile);
        grain.type.Override(FilmGrainLookup.Medium1);
        grain.intensity.Override(0.35f);
        grain.response.Override(0.8f);

        var tone = Get<Tonemapping>(profile);
        tone.mode.Override(TonemappingMode.ACES);

        var chroma = Get<ChromaticAberration>(profile);
        chroma.intensity.Override(0.08f);

        if (profile.TryGet(out MotionBlur blur)) blur.active = false;   // makes first-person look smeary

        EditorUtility.SetDirty(profile);
        foreach (var c in profile.components) EditorUtility.SetDirty(c);
    }

    static T Get<T>(VolumeProfile profile) where T : VolumeComponent
    {
        if (profile.TryGet(out T existing))
        {
            existing.active = true;
            return existing;
        }
        var c = profile.Add<T>(false);
        c.name = typeof(T).Name;
        c.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
        if (AssetDatabase.Contains(profile)) AssetDatabase.AddObjectToAsset(c, profile);
        return c;
    }

    // ---------- Cameras ----------

    static void ApplyCameras()
    {
        foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            SetupCamera(cam);

        const string prefabPath = "Assets/Prefabs/Player.prefab";
        if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null)
        {
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            foreach (var cam in root.GetComponentsInChildren<Camera>(true)) SetupCamera(cam);
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
        }
        else Debug.LogWarning($"Player prefab not found at {prefabPath} - set Anti-aliasing on its camera by hand.");
    }

    static void SetupCamera(Camera cam)
    {
        var data = cam.GetUniversalAdditionalCameraData();
        Undo.RecordObject(data, "Camera Look");
        data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        data.antialiasingQuality = AntialiasingQuality.High;
        data.renderPostProcessing = true;
        EditorUtility.SetDirty(data);
    }

    // ---------- Wet street ----------

    static void ApplyWetStreet()
    {
        var plane = GameObject.Find("Plane");
        var r = plane != null ? plane.GetComponent<Renderer>() : null;
        if (r == null) { Debug.LogWarning("No object named 'Plane' found for the wet street."); return; }

        var mat = r.sharedMaterial;
        string path = mat != null ? AssetDatabase.GetAssetPath(mat) : "";
        if (mat == null || !path.StartsWith("Assets/"))
        {
            // Built-in default material can't be edited: make our own
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.SetColor("_BaseColor", new Color(0.12f, 0.12f, 0.13f));
            EnsureFolder();
            AssetDatabase.CreateAsset(mat, "Assets/Materials/WetStreet.mat");
            Undo.RecordObject(r, "Wet Street");
            r.sharedMaterial = mat;
        }

        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.72f);
        EditorUtility.SetDirty(mat);
    }

    // ---------- Moonlight ----------

    static void ApplyMoonlight()
    {
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (l.type != LightType.Directional) continue;
            Undo.RecordObject(l, "Moonlight");
            l.shadows = LightShadows.Soft;
            l.shadowStrength = 0.75f;
            if (l.intensity > 0.4f) l.intensity = 0.25f;
        }
    }

    // ---------- Giant skin ----------

    void ApplyMonsterSkin()
    {
        var monster = Object.FindAnyObjectByType<MonsterAnimator>();
        if (monster == null) { Debug.LogWarning("No MonsterAnimator in the scene."); return; }

        EnsureFolder();
        var lit = Shader.Find("Universal Render Pipeline/Lit");
        int i = 0;
        foreach (var r in monster.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.sharedMaterials;
            for (int m = 0; m < mats.Length; m++)
            {
                var src = mats[m];
                if (src == null) continue;

                // Copy the original so its textures are kept; reuse the copy if we made one before
                string path = $"Assets/Materials/Monster_Skin_{i++}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    mat = new Material(lit);
                    var tex = src.HasProperty("_BaseMap") ? src.GetTexture("_BaseMap") : src.mainTexture;
                    if (tex != null) mat.SetTexture("_BaseMap", tex);
                    if (src.HasProperty("_BumpMap") && src.GetTexture("_BumpMap") != null)
                    {
                        mat.SetTexture("_BumpMap", src.GetTexture("_BumpMap"));
                        mat.EnableKeyword("_NORMALMAP");
                    }
                    AssetDatabase.CreateAsset(mat, path);
                }
                mat.SetColor("_BaseColor", skinTint);
                mat.SetFloat("_Smoothness", skinGloss);
                EditorUtility.SetDirty(mat);
                mats[m] = mat;
            }
            Undo.RecordObject(r, "Monster Skin");
            r.sharedMaterials = mats;
        }
    }

    static void EnsureFolder()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Materials")) AssetDatabase.CreateFolder("Assets", "Materials");
    }
}
