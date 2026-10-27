using Unity.AI.Navigation;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

// Tools -> RunForYourLife -> Climber Setup
// One click: fixes the Mixamo clip imports, adds the "Climber" NavMesh agent type, builds the Animator Controller,
// puts a Climber in the scene (or updates the one there) and bakes its NavMesh. Safe to run again.
public class ClimberSetup : EditorWindow
{
    const string Folder = "Assets/Monsters/Climber";
    const string ControllerPath = Folder + "/ClimberAnimator.controller";
    const string AgentName = "Climber";
    const string AudioFolder = "Assets/Audio/Climber";

    string modelFile = "Zombiegirl W Kurniawan.fbx";
    float modelScale = 1.7f;
    // Taller than the doorways (2.3 m), so the street NavMesh never leads inside: it has to climb
    float agentRadius = 0.5f, agentHeight = 2.6f;

    // FBX file -> clip name, loops?
    static readonly (string file, string clip, bool loop)[] Clips =
    {
        ("Injured Idle.fbx",       "Idle",      true),
        ("Running Crawl.fbx",      "Crawl",     true),
        ("Zombie Run.fbx",         "Run",       true),
        ("Climbing Up Wall.fbx",   "ClimbUp",   true),   // looped: it climbs as long as the wall is tall
        ("Climbing Down Wall.fbx", "ClimbDown", true),
        ("Jumping Down.fbx",       "DropDown",  false),
        ("Male Action Pose.fbx",   "Grab",      true),    // one-frame pose, held on its own layer
    };

    [MenuItem("Tools/RunForYourLife/Climber Setup")]
    static void Open() => GetWindow<ClimberSetup>("Climber Setup");

    void OnGUI()
    {
        modelFile = EditorGUILayout.TextField("Model FBX (in " + Folder + ")", modelFile);
        modelScale = EditorGUILayout.FloatField("Model Scale", modelScale);
        agentRadius = EditorGUILayout.FloatField("Agent Radius", agentRadius);
        agentHeight = EditorGUILayout.FloatField("Agent Height", agentHeight);
        EditorGUILayout.HelpBox("Agent Height must be taller than the doorways, or the climber walks in through them.",
            MessageType.Info);
        if (GUILayout.Button("Set Up Climber", GUILayout.Height(30))) Run();
        if (GUILayout.Button("Re-bake Climber NavMesh only")) Bake(GetAgentType());
    }

    public void Run()
    {
        ExtractTextures(Folder + "/" + modelFile);
        FixClips();
        int agentType = GetAgentType();
        var controller = BuildController();
        var climber = PlaceClimber(agentType, controller);
        Bake(agentType);

        var genomes = FindAnyObjectByType<GenomeManager>();
        if (genomes != null) { Undo.RecordObject(genomes, "Allow Climber"); genomes.allowClimber = true; }

        EditorSceneManager.MarkSceneDirty(climber.scene);
        Selection.activeGameObject = climber;
        Debug.Log("Climber Setup done. Save the scene.");
    }

    // Mixamo embeds the textures in the FBX; pull them out so the materials pick them up (and the normal map
    // is flagged as one)
    static void ExtractTextures(string modelPath)
    {
        if (!(AssetImporter.GetAtPath(modelPath) is ModelImporter mi)) return;
        string texFolder = Folder + "/Textures";
        if (!AssetDatabase.IsValidFolder(texFolder))
        {
            AssetDatabase.CreateFolder(Folder, "Textures");
            mi.ExtractTextures(texFolder);
            AssetDatabase.Refresh();
        }
        foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { texFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.ToLowerInvariant().Contains("normal") && AssetImporter.GetAtPath(path) is TextureImporter ti &&
                ti.textureType != TextureImporterType.NormalMap)
            {
                ti.textureType = TextureImporterType.NormalMap;
                ti.SaveAndReimport();
            }
        }
        mi.SaveAndReimport();   // materials re-import and find the textures next to the model
    }

    // Rename "mixamo.com" clips, loop the right ones, bake root motion into the pose (clips are In Place)
    static void FixClips()
    {
        foreach (var c in Clips)
        {
            string path = Folder + "/" + c.file;
            if (!(AssetImporter.GetAtPath(path) is ModelImporter mi)) { Debug.LogWarning("Missing " + path); continue; }
            var clips = mi.clipAnimations;
            if (clips == null || clips.Length == 0) clips = mi.defaultClipAnimations;
            if (clips.Length == 0) continue;
            var a = clips[0];
            a.name = c.clip;
            a.loopTime = c.loop;
            a.lockRootRotation = true;
            a.lockRootHeightY = true;
            a.lockRootPositionXZ = true;
            a.keepOriginalOrientation = false;
            a.keepOriginalPositionY = false;
            a.keepOriginalPositionXZ = false;
            a.heightFromFeet = true;
            clips[0] = a;
            mi.clipAnimations = clips;
            mi.SaveAndReimport();
        }
    }

    static AnimationClip LoadClip(string file)
    {
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(Folder + "/" + file))
            if (o is AnimationClip clip && !clip.name.StartsWith("__preview")) return clip;
        return null;
    }

    // The "Climber" NavMesh agent type: found by name, created if missing
    int GetAgentType()
    {
        for (int i = 0; i < NavMesh.GetSettingsCount(); i++)
        {
            var s = NavMesh.GetSettingsByIndex(i);
            if (NavMesh.GetSettingsNameFromID(s.agentTypeID) == AgentName) return s.agentTypeID;
        }

        var created = NavMesh.CreateSettings();
        var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/NavMeshAreas.asset")[0];
        var so = new SerializedObject(asset);
        var settings = so.FindProperty("m_Settings");
        var names = so.FindProperty("m_SettingNames");
        for (int i = 0; i < settings.arraySize; i++)
        {
            var s = settings.GetArrayElementAtIndex(i);
            if (s.FindPropertyRelative("agentTypeID").intValue != created.agentTypeID) continue;
            s.FindPropertyRelative("agentRadius").floatValue = agentRadius;
            s.FindPropertyRelative("agentHeight").floatValue = agentHeight;
            s.FindPropertyRelative("agentSlope").floatValue = 45f;
            s.FindPropertyRelative("agentClimb").floatValue = 0.5f;
            if (names.arraySize <= i) names.arraySize = i + 1;
            names.GetArrayElementAtIndex(i).stringValue = AgentName;
        }
        so.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        Debug.Log("Added NavMesh agent type '" + AgentName + "'");
        return created.agentTypeID;
    }

    // Ground (idle/crawl blend, or run) and the climbs, chosen by "Mode" and "Running" from ClimberAnimator
    static AnimatorController BuildController()
    {
        AssetDatabase.DeleteAsset(ControllerPath);
        var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
        controller.AddParameter("Mode", AnimatorControllerParameterType.Int);   // 0 ground, 1 climb up, 2 climb down, 3 drop
        controller.AddParameter("Running", AnimatorControllerParameterType.Bool);
        controller.AddParameter(new AnimatorControllerParameter
            { name = "Playback", type = AnimatorControllerParameterType.Float, defaultFloat = 1f });
        var sm = controller.layers[0].stateMachine;

        var ground = controller.CreateBlendTreeInController("Ground", out BlendTree tree, 0);
        tree.blendType = BlendTreeType.Simple1D;
        tree.blendParameter = "Speed";
        tree.useAutomaticThresholds = false;
        tree.AddChild(LoadClip("Injured Idle.fbx"), 0f);
        tree.AddChild(LoadClip("Running Crawl.fbx"), 0.8f);
        sm.defaultState = ground;

        var run = AddState(sm, "Run", LoadClip("Zombie Run.fbx"));
        var up = AddState(sm, "ClimbUp", LoadClip("Climbing Up Wall.fbx"));
        var down = AddState(sm, "ClimbDown", LoadClip("Climbing Down Wall.fbx"));
        var drop = AddState(sm, "DropDown", LoadClip("Jumping Down.fbx"));
        foreach (var s in new[] { ground, run, up, down, drop }) { s.speedParameter = "Playback"; s.speedParameterActive = true; }
        drop.speedParameterActive = false;

        // Ground <-> Run
        Transition(ground, run, 0.25f).AddCondition(AnimatorConditionMode.If, 0f, "Running");
        Transition(run, ground, 0.25f).AddCondition(AnimatorConditionMode.IfNot, 0f, "Running");

        // Any ground state -> climb / drop, and back to ground when Mode returns to 0
        var modeStates = new[] { (up, 1), (down, 2), (drop, 3) };
        foreach (var (state, mode) in modeStates)
        {
            var t = sm.AddAnyStateTransition(state);
            t.hasExitTime = false;
            t.duration = 0.25f;
            t.canTransitionToSelf = false;
            t.AddCondition(AnimatorConditionMode.Equals, mode, "Mode");
            Transition(state, ground, 0.3f).AddCondition(AnimatorConditionMode.Equals, 0, "Mode");
        }
        // Grab layer: the lunge pose over everything. ClimberAnimator fades its weight in and out.
        var grabClip = LoadClip("Male Action Pose.fbx");
        if (grabClip != null)
        {
            controller.AddLayer("Grab");
            var grabLayers = controller.layers;
            grabLayers[1].defaultWeight = 0f;
            grabLayers[1].blendingMode = AnimatorLayerBlendingMode.Override;
            controller.layers = grabLayers;
            controller.layers[1].stateMachine.defaultState = AddState(controller.layers[1].stateMachine, "Grab", grabClip);
        }
        AssetDatabase.SaveAssets();
        return controller;
    }

    static AnimatorState AddState(AnimatorStateMachine sm, string name, AnimationClip clip)
    {
        var s = sm.AddState(name);
        s.motion = clip;
        return s;
    }

    static AnimatorStateTransition Transition(AnimatorState from, AnimatorState to, float duration)
    {
        var t = from.AddTransition(to);
        t.hasExitTime = false;
        t.duration = duration;
        return t;
    }

    GameObject PlaceClimber(int agentType, AnimatorController controller)
    {
        var existing = FindAnyObjectByType<ClimberAI>();
        GameObject root;
        if (existing != null) root = existing.gameObject;
        else
        {
            root = new GameObject("Climber");
            Undo.RegisterCreatedObjectUndo(root, "Create Climber");
            var giant = FindAnyObjectByType<MonsterAI>();
            root.transform.position = giant != null ? giant.transform.position + new Vector3(6f, 0f, 0f) : Vector3.zero;
            root.AddComponent<NavMeshAgent>();
            root.AddComponent<NetworkObject>();
            root.AddComponent<NetworkTransform>();
            root.AddComponent<ClimberAI>();
        }

        var agent = root.GetComponent<NavMeshAgent>();
        agent.agentTypeID = agentType;
        agent.radius = 0.4f;
        agent.height = 1.6f;
        agent.angularSpeed = 400f;
        agent.acceleration = 20f;
        agent.stoppingDistance = 0.3f;

        // Model child: the Mixamo character, scaled up, animated by ClimberAnimator
        var model = root.transform.Find("Model");
        if (model == null)
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "/" + modelFile);
            if (fbx == null) { Debug.LogError("Climber model not found: " + Folder + "/" + modelFile); return root; }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(fbx, root.transform);
            go.name = "Model";
            model = go.transform;
        }
        model.localPosition = Vector3.zero;
        model.localRotation = Quaternion.identity;
        model.localScale = Vector3.one * modelScale;
        var anim = model.GetComponent<Animator>();
        if (anim == null) anim = model.gameObject.AddComponent<Animator>();
        anim.runtimeAnimatorController = controller;
        anim.applyRootMotion = false;
        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        if (model.GetComponent<ClimberAnimator>() == null) model.gameObject.AddComponent<ClimberAnimator>();

        // Sounds, found by name in the audio folder: "Footstep" = steps, "Grab" = catch, anything else = its call
        var sounds = root.GetComponent<ClimberAudio>();
        if (sounds == null) sounds = root.AddComponent<ClimberAudio>();
        var stepClips = new System.Collections.Generic.List<AudioClip>();
        var grabClips = new System.Collections.Generic.List<AudioClip>();
        var callClips = new System.Collections.Generic.List<AudioClip>();
        foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { AudioFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (AssetImporter.GetAtPath(path) is AudioImporter ai && !ai.forceToMono)
            {
                ai.forceToMono = true;   // 3D sounds
                ai.SaveAndReimport();
            }
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            string n = clip.name.ToLowerInvariant();
            if (n.Contains("footstep") || n.Contains("step")) stepClips.Add(clip);
            else if (n.Contains("grab")) grabClips.Add(clip);
            else callClips.Add(clip);
        }
        sounds.footstepClips = stepClips.ToArray();
        sounds.grabClips = grabClips.ToArray();
        sounds.callClips = callClips.ToArray();
        EditorUtility.SetDirty(sounds);
        Debug.Log($"Climber sounds: {stepClips.Count} steps, {grabClips.Count} grab, {callClips.Count} calls");
        return root;
    }

    // Called after building changes (e.g. roof hatches) so the climber's NavMesh matches the roofs
    public static void BakeClimberNavMesh()
    {
        for (int i = 0; i < NavMesh.GetSettingsCount(); i++)
        {
            var s = NavMesh.GetSettingsByIndex(i);
            if (NavMesh.GetSettingsNameFromID(s.agentTypeID) == AgentName) { Bake(s.agentTypeID); return; }
        }
        Debug.LogWarning("No Climber agent type yet: run Tools > RunForYourLife > Climber Setup.");
    }

    // Its own NavMeshSurface, same settings as the giant's, baked for the Climber agent type
    static void Bake(int agentType)
    {
        NavMeshSurface surface = null, reference = null;
        foreach (var s in FindObjectsByType<NavMeshSurface>())
        {
            if (s.agentTypeID == agentType) surface = s;
            else reference = s;
        }
        if (surface == null)
        {
            var go = new GameObject("NavMesh Climber");
            Undo.RegisterCreatedObjectUndo(go, "Climber NavMesh");
            surface = go.AddComponent<NavMeshSurface>();
            surface.agentTypeID = agentType;
            if (reference != null)
            {
                surface.collectObjects = reference.collectObjects;
                surface.layerMask = reference.layerMask;
                surface.useGeometry = reference.useGeometry;
            }
        }

        surface.BuildNavMesh();
        var scenePath = surface.gameObject.scene.path;
        string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(scenePath),
            System.IO.Path.GetFileNameWithoutExtension(scenePath)).Replace('\\', '/');
        if (!AssetDatabase.IsValidFolder(dir))
            AssetDatabase.CreateFolder(System.IO.Path.GetDirectoryName(dir).Replace('\\', '/'), System.IO.Path.GetFileName(dir));
        string assetPath = dir + "/NavMesh-Climber.asset";
        AssetDatabase.DeleteAsset(assetPath);
        AssetDatabase.CreateAsset(surface.navMeshData, assetPath);
        EditorUtility.SetDirty(surface);
        EditorSceneManager.MarkSceneDirty(surface.gameObject.scene);
        Debug.Log("Baked Climber NavMesh -> " + assetPath);
    }
}
