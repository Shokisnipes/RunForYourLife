using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

// Tools -> RunForYourLife -> Scout Setup
// One click: import settings for the scout model, its clips (plus the Climber's wall climbs, reused) and its sounds,
// builds the Animator Controller, measures the crawl speed and makes Assets/Prefabs/Scout.prefab. Safe to run again.
// Also: the networked brain (ScoutAI on the Climber's NavMesh) and a ScoutDirector on the NetworkManager object.
public class ScoutSetup : EditorWindow
{
    const string Folder = "Assets/Monsters/Scout";
    const string ClimberFolder = "Assets/Monsters/Climber";
    const string AudioFolder = "Assets/Audio/Scout";
    const string ControllerPath = Folder + "/ScoutAnimator.controller";
    const string PrefabPath = "Assets/Prefabs/Scout.prefab";

    string modelFile = "Ch25_nonPBR.fbx";
    // The model is 1.9 m tall standing; scouts are tiny (~0.6 m), and crawl even lower
    float modelScale = 0.32f;

    // FBX file -> clip name, loops?
    static readonly (string file, string clip, bool loop)[] Clips =
    {
        ("Crawling.fbx",    "Crawl", true),
        ("Prone Death.fbx", "Death", false),
    };

    [MenuItem("Tools/RunForYourLife/Scout Setup")]
    static void Open() => GetWindow<ScoutSetup>("Scout Setup");

    void OnGUI()
    {
        modelFile = EditorGUILayout.TextField("Model FBX (in " + Folder + ")", modelFile);
        modelScale = EditorGUILayout.FloatField("Model Scale", modelScale);
        if (GUILayout.Button("Set Up Scout", GUILayout.Height(30))) Run();
    }

    public void Run()
    {
        ImportModel(Folder + "/" + modelFile);
        FixClips();
        FixAudio();
        var controller = BuildController();
        BuildPrefab(controller);
        AddDirector();
        Debug.Log("Scout Setup done: " + PrefabPath);
    }

    // Humanoid rig, and the textures Mixamo embeds pulled out so the materials find them
    static void ImportModel(string path)
    {
        if (!(AssetImporter.GetAtPath(path) is ModelImporter mi)) { Debug.LogError("Missing " + path); return; }
        mi.animationType = ModelImporterAnimationType.Human;
        mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        mi.importAnimation = false;
        string texFolder = Folder + "/Textures";
        if (!AssetDatabase.IsValidFolder(texFolder))
        {
            AssetDatabase.CreateFolder(Folder, "Textures");
            mi.ExtractTextures(texFolder);
            AssetDatabase.Refresh();
        }
        foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { texFolder }))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (p.ToLowerInvariant().Contains("normal") && AssetImporter.GetAtPath(p) is TextureImporter ti &&
                ti.textureType != TextureImporterType.NormalMap)
            {
                ti.textureType = TextureImporterType.NormalMap;
                ti.SaveAndReimport();
            }
        }
        mi.SaveAndReimport();
    }

    // Humanoid, renamed from "mixamo.com", loop the crawl, root motion baked into the pose (the AI moves it).
    // Height stays as authored ("Original"): the crawl has hands on the ground as well as feet, and the death
    // ends lying flat, so "Feet" would lift or sink the body.
    static void FixClips()
    {
        foreach (var c in Clips)
        {
            string path = Folder + "/" + c.file;
            if (!(AssetImporter.GetAtPath(path) is ModelImporter mi)) { Debug.LogWarning("Missing " + path); continue; }
            mi.animationType = ModelImporterAnimationType.Human;
            mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            var clips = mi.clipAnimations;
            if (clips == null || clips.Length == 0) clips = mi.defaultClipAnimations;
            if (clips.Length == 0) continue;
            var a = clips[0];
            a.name = c.clip;
            a.loopTime = c.loop;
            a.lockRootRotation = true;
            a.lockRootHeightY = true;
            a.lockRootPositionXZ = true;
            a.keepOriginalOrientation = true;
            a.keepOriginalPositionY = true;
            a.keepOriginalPositionXZ = false;
            clips[0] = a;
            mi.clipAnimations = clips;
            mi.SaveAndReimport();
        }
    }

    // Every scout sound is 3D: mono, decompressed (they're short), Vorbis
    static void FixAudio()
    {
        foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { AudioFolder }))
        {
            if (!(AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid)) is AudioImporter ai)) continue;
            ai.forceToMono = true;
            var s = ai.defaultSampleSettings;
            s.loadType = AudioClipLoadType.DecompressOnLoad;
            s.compressionFormat = AudioCompressionFormat.Vorbis;
            s.quality = 0.7f;
            ai.defaultSampleSettings = s;
            ai.SaveAndReimport();
        }
    }

    static AnimationClip LoadClip(string folder, string file)
    {
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(folder + "/" + file))
            if (o is AnimationClip clip && !clip.name.StartsWith("__preview")) return clip;
        Debug.LogWarning("No clip in " + folder + "/" + file);
        return null;
    }

    static AnimatorController BuildController()
    {
        AssetDatabase.DeleteAsset(ControllerPath);
        var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        controller.AddParameter(new AnimatorControllerParameter
            { name = "Playback", type = AnimatorControllerParameterType.Float, defaultFloat = 1f });
        controller.AddParameter("Climb", AnimatorControllerParameterType.Int);   // 0 ground, 1 up, -1 down
        controller.AddParameter("Dead", AnimatorControllerParameterType.Bool);
        // IK pass on: ScoutAnimator rears the head for the screech and fixes the arms in the death clip
        var layers = controller.layers;
        layers[0].iKPass = true;
        controller.layers = layers;
        var sm = controller.layers[0].stateMachine;

        // Idle is the crawl played very slowly (ScoutAnimator), so there's no pop between standing and moving
        var crawl = AddState(sm, "Crawl", LoadClip(Folder, "Crawling.fbx"), true);
        sm.defaultState = crawl;
        var up = AddState(sm, "ClimbUp", LoadClip(ClimberFolder, "Climbing Up Wall.fbx"), true);
        var down = AddState(sm, "ClimbDown", LoadClip(ClimberFolder, "Climbing Down Wall.fbx"), true);
        var death = AddState(sm, "Death", LoadClip(Folder, "Prone Death.fbx"), false);

        foreach (var (state, climb) in new[] { (up, 1), (down, -1) })
        {
            var t = sm.AddAnyStateTransition(state);
            t.hasExitTime = false;
            t.duration = 0.2f;
            t.canTransitionToSelf = false;
            t.AddCondition(AnimatorConditionMode.Equals, climb, "Climb");
            t.AddCondition(AnimatorConditionMode.IfNot, 0f, "Dead");
            var back = state.AddTransition(crawl);
            back.hasExitTime = false;
            back.duration = 0.25f;
            back.AddCondition(AnimatorConditionMode.Equals, 0, "Climb");
        }

        // Death from anywhere. The clip starts a little in (past the soldier settling onto the rifle), and the
        // blend is long enough to carry the body from all fours down to the ground smoothly.
        var die = sm.AddAnyStateTransition(death);
        die.hasExitTime = false;
        die.duration = 0.35f;
        die.offset = 0.08f;
        die.canTransitionToSelf = false;
        die.AddCondition(AnimatorConditionMode.If, 0f, "Dead");

        AssetDatabase.SaveAssets();
        return controller;
    }

    static AnimatorState AddState(AnimatorStateMachine sm, string name, AnimationClip clip, bool playback)
    {
        var s = sm.AddState(name);
        s.motion = clip;
        s.writeDefaultValues = true;
        if (playback) { s.speedParameter = "Playback"; s.speedParameterActive = true; }
        return s;
    }

    // How fast the crawl travels at playback 1: how quickly whichever hand/foot is lowest (planted) slides back
    float MeasureCrawlSpeed(GameObject model, Animator animator)
    {
        var clip = LoadClip(Folder, "Crawling.fbx");
        if (clip == null) return 0.5f;
        var limbs = new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, HumanBodyBones.LeftHand, HumanBodyBones.RightHand };
        const int samples = 60;
        float dt = clip.length / samples, total = 0f;
        int count = 0;
        for (int i = 0; i < samples; i++)
        {
            clip.SampleAnimation(model, i * dt);
            Transform low = null;
            foreach (var b in limbs)
            {
                var t = animator.GetBoneTransform(b);
                if (t != null && (low == null || t.position.y < low.position.y)) low = t;
            }
            if (low == null) continue;
            Vector3 a = low.position;
            clip.SampleAnimation(model, i * dt + dt);
            float back = a.z - low.position.z;   // the planted limb slides backward as the body goes forward
            if (back > 0f) { total += back / dt; count++; }
        }
        return count > 0 ? total / count : 0.5f;
    }

    // How far the clip's lowest vertex dips below the floor (model at its final scale): that's how much to lift it.
    // A looping clip is checked over its whole cycle; the death clip only at its last frame (where it lies still).
    static float MeasureLift(GameObject model, AnimationClip clip, bool lastFrameOnly)
    {
        var smr = model.GetComponentInChildren<SkinnedMeshRenderer>();
        if (clip == null || smr == null) return 0f;
        var mesh = new Mesh();
        float lowest = float.MaxValue;
        int samples = lastFrameOnly ? 1 : 20;
        for (int i = 0; i < samples; i++)
        {
            clip.SampleAnimation(model, lastFrameOnly ? clip.length : clip.length * i / samples);
            smr.BakeMesh(mesh, false);
            foreach (var v in mesh.vertices)
                lowest = Mathf.Min(lowest, smr.transform.TransformPoint(v).y - model.transform.position.y);
        }
        DestroyImmediate(mesh);
        return Mathf.Max(0f, -lowest + 0.005f);
    }

    static int ClimberAgentType()
    {
        for (int i = 0; i < NavMesh.GetSettingsCount(); i++)
        {
            var st = NavMesh.GetSettingsByIndex(i);
            if (NavMesh.GetSettingsNameFromID(st.agentTypeID) == "Climber") return st.agentTypeID;
        }
        Debug.LogWarning("No \"Climber\" NavMesh agent type: run Climber Setup first.");
        return 0;
    }

    // The Director lives next to GenomeManager (which holds Allow Scouts / Scout Count)
    static void AddDirector()
    {
        var genomes = FindAnyObjectByType<GenomeManager>();
        if (genomes == null) { Debug.LogWarning("No GenomeManager in the scene: add the ScoutDirector by hand."); return; }
        var director = genomes.GetComponent<ScoutDirector>();
        if (director == null) director = Undo.AddComponent<ScoutDirector>(genomes.gameObject);
        Undo.RecordObject(director, "Scout prefab");
        director.scoutPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        EditorSceneManager.MarkSceneDirty(genomes.gameObject.scene);
    }

    void BuildPrefab(AnimatorController controller)
    {
        var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "/" + modelFile);
        if (modelAsset == null) return;

        var root = new GameObject("Scout");
        try
        {
            var model = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset, root.transform);
            model.name = "Model";
            var animator = model.GetComponent<Animator>();
            if (animator == null) animator = model.AddComponent<Animator>();
            animator.avatar = AssetDatabase.LoadAssetAtPath<Avatar>(Folder + "/" + modelFile);
            animator.applyRootMotion = false;

            float crawlSpeed = MeasureCrawlSpeed(model, animator) * modelScale;
            model.transform.localScale = Vector3.one * modelScale;
            animator.runtimeAnimatorController = controller;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;

            var sa = model.AddComponent<ScoutAnimator>();
            sa.crawlClipSpeed = crawlSpeed;
            sa.crawlLift = MeasureLift(model, LoadClip(Folder, "Crawling.fbx"), false);
            sa.deathLift = MeasureLift(model, LoadClip(Folder, "Prone Death.fbx"), true);
            foreach (var r in model.GetComponentsInChildren<Renderer>())
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;   // tiny, and keeps shadow casters down

            var audio = root.AddComponent<ScoutAudio>();
            audio.skitterLoop = AssetDatabase.LoadAssetAtPath<AudioClip>(AudioFolder + "/Scout_Footsteps.wav");
            audio.sniffClips = new[] { AssetDatabase.LoadAssetAtPath<AudioClip>(AudioFolder + "/Scout_Sniffing.mp3") };
            audio.screechClips = new[] { AssetDatabase.LoadAssetAtPath<AudioClip>(AudioFolder + "/Scout_Scream.wav") };
            audio.deathClips = new[] { AssetDatabase.LoadAssetAtPath<AudioClip>(AudioFolder + "/Scout_Death.mp3") };

            // Networked: the host runs ScoutAI and moves it; everyone else sees the synced position and state
            root.AddComponent<NetworkObject>();
            var nt = root.AddComponent<NetworkTransform>();
            nt.SyncRotAngleX = false;
            nt.SyncRotAngleZ = false;
            nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
            nt.Interpolate = true;
            var agent = root.AddComponent<NavMeshAgent>();
            agent.agentTypeID = ClimberAgentType();   // streets + open doorways (the Climber's NavMesh)
            agent.radius = 0.25f;
            agent.height = 0.5f;
            agent.acceleration = 30f;
            agent.angularSpeed = 540f;
            agent.stoppingDistance = 0.3f;
            agent.autoBraking = true;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance;
            agent.enabled = false;
            root.AddComponent<ScoutAI>();

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log("Scout crawl speed at playback 1: " + crawlSpeed.ToString("F2") + " m/s");
        }
        finally
        {
            DestroyImmediate(root);
        }
    }
}
