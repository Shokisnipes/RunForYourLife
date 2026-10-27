using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// Tools -> RunForYourLife -> Player Model Setup
// One click: fixes the Mixamo imports for the player character (Humanoid, textures, clip names, loops), makes a
// reversed crouch walk, measures how fast each clip walks, builds the Animator Controller and puts the model on
// the Player prefab (replacing the old capsule). Safe to run again, e.g. after swapping the character.
public static class PlayerModelSetup
{
    const string Folder = "Assets/Characters/Martha";
    const string ModelFile = "Martha.fbx";
    const string ControllerPath = Folder + "/PlayerAnimator.controller";
    const string CrouchBackPath = Folder + "/CrouchWalkBack.anim";
    const string PlayerPrefab = "Assets/Prefabs/Player.prefab";

    // FBX file -> clip name, loops?  (not all are used yet: carrying, jumping, stairs and turns come later)
    static readonly (string file, string clip, bool loop)[] Clips =
    {
        ("Idle.fbx",                      "Idle",           true),
        ("Walking (1).fbx",               "Walk",           true),
        ("Running.fbx",                   "Run",            true),
        ("Walking Backwards.fbx",         "WalkBack",       true),
        ("Running Backward.fbx",          "RunBack",        true),
        ("Left Strafe Walking.fbx",       "StrafeLeft",     true),
        ("Right Strafe Walking.fbx",      "StrafeRight",    true),
        ("Crouching Idle (1).fbx",        "CrouchIdle",     true),
        ("Crouch Walk.fbx",               "CrouchWalk",     true),
        ("Standing To Crouched (1).fbx",  "StandToCrouch",  false),
        ("Standing W_Briefcase Idle.fbx", "CarryIdle",      true),
        ("Walk With Briefcase.fbx",       "CarryWalk",      true),
        ("Running Jump.fbx",              "RunJump",        false),
        ("Walking Up The Stairs.fbx",     "WalkStairs",     true),
        ("Running Up Stairs.fbx",         "RunStairs",      true),
        ("Walking Left Turn.fbx",         "WalkTurnLeft",   false),
        ("Running Right Turn.fbx",        "RunTurnRight",   false),
        ("Walking Turn 180.fbx",          "WalkTurn180",    false),
        ("Running Turn 180.fbx",          "RunTurn180",     false),
    };

    [MenuItem("Tools/RunForYourLife/Player Model Setup")]
    public static void Run()
    {
        string modelPath = Folder + "/" + ModelFile;
        ExtractTextures(modelPath);
        var avatar = SetUpModel(modelPath);
        if (avatar == null || !avatar.isHuman) { Debug.LogError("Player Model Setup: " + modelPath + " didn't make a Humanoid avatar"); return; }
        FixClips();
        var crouchBack = MakeReversedClip(LoadClip("Crouch Walk.fbx"), CrouchBackPath);
        var speeds = MeasureSpeeds(AssetDatabase.LoadAssetAtPath<GameObject>(modelPath));
        var controller = BuildController(crouchBack, speeds);
        UpdatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modelPath), controller, speeds);
        AssetDatabase.SaveAssets();
        Debug.Log(string.Format("Player Model Setup done. Clip speeds: walk {0:F2}, run {1:F2}, back {2:F2}, run back {3:F2}, strafe {4:F2}, crouch {5:F2} m/s",
            speeds.walk, speeds.run, speeds.back, speeds.runBack, speeds.strafe, speeds.crouch));
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
    }

    // The character: Humanoid with its own avatar, no animation of its own
    static Avatar SetUpModel(string modelPath)
    {
        var mi = (ModelImporter)AssetImporter.GetAtPath(modelPath);
        mi.animationType = ModelImporterAnimationType.Human;
        mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        mi.importAnimation = false;
        mi.SaveAndReimport();
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(modelPath))
            if (o is Avatar a) return a;
        return null;
    }

    // Humanoid, rename "mixamo.com", loop the right ones. Clips downloaded without "In Place" walk off forward:
    // their travel is pulled out of the pose into root motion, which the Animator ignores (the controller moves
    // us). True in-place clips keep everything baked into the pose (keeps the natural hip sway).
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
            a.lockRootPositionXZ = false;
            a.keepOriginalOrientation = false;
            a.keepOriginalPositionY = false;
            a.keepOriginalPositionXZ = false;
            a.heightFromFeet = true;
            clips[0] = a;
            mi.clipAnimations = clips;
            mi.SaveAndReimport();

            if (Travel(LoadClip(c.file)) < 0.1f)
            {
                clips[0].lockRootPositionXZ = true;   // in place
                mi.clipAnimations = clips;
                mi.SaveAndReimport();
            }
        }
    }

    // m/s the clip moves along the ground (0 for In Place clips)
    static float Travel(AnimationClip clip)
    {
        if (clip == null) return 0f;
        Vector3 v = clip.averageSpeed;
        return new Vector2(v.x, v.z).magnitude;
    }

    static AnimationClip LoadClip(string file)
    {
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(Folder + "/" + file))
            if (o is AnimationClip clip && !clip.name.StartsWith("__preview")) return clip;
        Debug.LogWarning("No clip in " + file);
        return null;
    }

    // Plays a clip backwards (there's no crouch-walk-backwards clip)
    static AnimationClip MakeReversedClip(AnimationClip source, string path)
    {
        if (source == null) return null;
        var clip = new AnimationClip { name = Path.GetFileNameWithoutExtension(path), frameRate = source.frameRate };
        float len = source.length;
        foreach (var binding in AnimationUtility.GetCurveBindings(source))
        {
            var curve = AnimationUtility.GetEditorCurve(source, binding);
            var keys = curve.keys;
            var rev = new Keyframe[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                var k = keys[keys.Length - 1 - i];
                rev[i] = new Keyframe(len - k.time, k.value, -k.outTangent, -k.inTangent, k.outWeight, k.inWeight);
            }
            AnimationUtility.SetEditorCurve(clip, binding, new AnimationCurve(rev));
        }
        AnimationUtility.SetAnimationClipSettings(clip, AnimationUtility.GetAnimationClipSettings(source));

        var old = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        if (old != null) { EditorUtility.CopySerialized(clip, old); Object.DestroyImmediate(clip); return old; }
        AssetDatabase.CreateAsset(clip, path);
        return clip;
    }

    struct Speeds { public float walk, run, back, runBack, strafe, crouch; }

    // How fast each clip "walks" at playback 1: its root motion, or for In Place clips the speed a planted foot
    // slides backward under the body
    static Speeds MeasureSpeeds(GameObject model)
    {
        var go = Object.Instantiate(model);
        go.hideFlags = HideFlags.HideAndDontSave;
        var s = new Speeds
        {
            walk = Measure(go, LoadClip("Walking (1).fbx"), false, 1.4f),
            run = Measure(go, LoadClip("Running.fbx"), false, 3.6f),
            back = Measure(go, LoadClip("Walking Backwards.fbx"), false, 1.2f),
            runBack = Measure(go, LoadClip("Running Backward.fbx"), false, 3f),
            strafe = Measure(go, LoadClip("Right Strafe Walking.fbx"), true, 1.3f),
            crouch = Measure(go, LoadClip("Crouch Walk.fbx"), false, 1f),
        };
        Object.DestroyImmediate(go);
        return s;
    }

    static float Measure(GameObject go, AnimationClip clip, bool sideways, float fallback)
    {
        var anim = go.GetComponent<Animator>();
        if (clip == null || anim == null) return fallback;
        float travel0 = Travel(clip);
        if (travel0 >= 0.1f) return travel0;   // not In Place: the clip knows its own speed
        var feet = new[] { anim.GetBoneTransform(HumanBodyBones.LeftFoot), anim.GetBoneTransform(HumanBodyBones.RightFoot) };
        const int samples = 120;
        var pos = new Vector3[2, samples + 1];
        for (int i = 0; i <= samples; i++)
        {
            clip.SampleAnimation(go, clip.length * i / samples);
            for (int f = 0; f < 2; f++) pos[f, i] = go.transform.InverseTransformPoint(feet[f].position);
        }

        // The lower foot is the one on the ground; follow it as it slides back under the body
        float travel = 0f;
        for (int i = 0; i < samples; i++)
        {
            int f = pos[0, i].y + pos[0, i + 1].y < pos[1, i].y + pos[1, i + 1].y ? 0 : 1;
            Vector3 d = pos[f, i + 1] - pos[f, i];
            travel += Mathf.Abs(sideways ? d.x : d.z);
        }
        float speed = travel / clip.length;
        if (speed < 0.2f || speed > 10f) { Debug.LogWarning("Couldn't measure " + clip.name + ", using " + fallback); return fallback; }
        return speed;
    }

    // Two 2D blend trees on MoveX / MoveZ (local velocity, m/s): standing and crouched. Clips sit at their
    // natural speeds; PlayerAnimator plays them faster when we move faster than that.
    static AnimatorController BuildController(AnimationClip crouchBack, Speeds sp)
    {
        AssetDatabase.DeleteAsset(ControllerPath);
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        ctrl.AddParameter("MoveX", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("MoveZ", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("Crouch", AnimatorControllerParameterType.Bool);

        var layers = ctrl.layers;
        layers[0].iKPass = true;   // head looks where the player looks
        ctrl.layers = layers;

        var stand = ctrl.CreateBlendTreeInController("Stand", out var standTree, 0);
        Setup2D(standTree);
        standTree.AddChild(LoadClip("Idle.fbx"), Vector2.zero);
        standTree.AddChild(LoadClip("Walking (1).fbx"), new Vector2(0f, sp.walk));
        standTree.AddChild(LoadClip("Running.fbx"), new Vector2(0f, sp.run));
        standTree.AddChild(LoadClip("Walking Backwards.fbx"), new Vector2(0f, -sp.back));
        standTree.AddChild(LoadClip("Running Backward.fbx"), new Vector2(0f, -sp.runBack));
        standTree.AddChild(LoadClip("Left Strafe Walking.fbx"), new Vector2(-sp.strafe, 0f));
        standTree.AddChild(LoadClip("Right Strafe Walking.fbx"), new Vector2(sp.strafe, 0f));

        var crouch = ctrl.CreateBlendTreeInController("Crouch", out var crouchTree, 0);
        Setup2D(crouchTree);
        var crouchWalk = LoadClip("Crouch Walk.fbx");
        crouchTree.AddChild(LoadClip("Crouching Idle (1).fbx"), Vector2.zero);
        crouchTree.AddChild(crouchWalk, new Vector2(0f, sp.crouch));
        if (crouchBack != null) crouchTree.AddChild(crouchBack, new Vector2(0f, -sp.crouch));
        // No crouch strafe clips yet: the crouch walk stands in sideways
        crouchTree.AddChild(crouchWalk, new Vector2(-sp.crouch, 0f));
        crouchTree.AddChild(crouchWalk, new Vector2(sp.crouch, 0f));

        var sm = ctrl.layers[0].stateMachine;
        sm.defaultState = stand;
        var down = stand.AddTransition(crouch);
        down.hasExitTime = false; down.duration = 0.25f;
        down.AddCondition(AnimatorConditionMode.If, 0f, "Crouch");
        var up = crouch.AddTransition(stand);
        up.hasExitTime = false; up.duration = 0.25f;
        up.AddCondition(AnimatorConditionMode.IfNot, 0f, "Crouch");

        EditorUtility.SetDirty(ctrl);
        return ctrl;
    }

    static void Setup2D(BlendTree tree)
    {
        tree.blendType = BlendTreeType.FreeformDirectional2D;
        tree.blendParameter = "MoveX";
        tree.blendParameterY = "MoveZ";
    }

    // Swap the capsule on the Player prefab for the model
    static void UpdatePrefab(GameObject model, AnimatorController controller, Speeds sp)
    {
        var root = PrefabUtility.LoadPrefabContents(PlayerPrefab);
        try
        {
            foreach (var name in new[] { "Body", "Model" })
            {
                var old = root.transform.Find(name);
                if (old != null) Object.DestroyImmediate(old.gameObject);
            }

            var go = (GameObject)PrefabUtility.InstantiatePrefab(model, root.transform);
            go.name = "Model";
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            var anim = go.GetComponent<Animator>();
            if (anim == null) anim = go.AddComponent<Animator>();
            anim.runtimeAnimatorController = controller;
            anim.applyRootMotion = false;

            var pa = go.GetComponent<PlayerAnimator>();
            if (pa == null) pa = go.AddComponent<PlayerAnimator>();
            pa.walkClipSpeed = sp.walk;
            pa.runClipSpeed = sp.run;
            pa.walkBackClipSpeed = sp.back;
            pa.runBackClipSpeed = sp.runBack;
            pa.strafeClipSpeed = sp.strafe;
            pa.crouchClipSpeed = sp.crouch;

            var fpc = root.GetComponent<FirstPersonController>();
            if (fpc != null) fpc.body = null;   // the capsule is gone; PlayerAnimator handles the model

            PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefab);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
