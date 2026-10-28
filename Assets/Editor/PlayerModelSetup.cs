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
    const string CoverIdlePath = Folder + "/CoverIdle.anim";
    const string PlayerPrefab = "Assets/Prefabs/Player.prefab";

    // FBX file -> clip name, loops?, optional mirrored copy (same file, other side), and the direction the clip
    // should travel in the body's frame (x = right, y = forward; zero = doesn't travel / not used for moving).
    // Mixamo clips are authored all sorts of ways (the "Left" sneaks face backwards, the crouch walk drifts 30
    // degrees); FixClips turns each one so it travels this way, keeping its natural body twist.
    // Not all are used yet: carrying, jumping, stairs and turns come later.
    static readonly (string file, string clip, bool loop, string mirror, Vector2 dir)[] Clips =
    {
        ("Idle.fbx",                          "Idle",              true,  null,             Vector2.zero),
        ("Walking (1).fbx",                   "Walk",              true,  null,             Vector2.up),
        ("Running.fbx",                       "Run",               true,  null,             Vector2.up),
        ("Walking Backwards.fbx",             "WalkBack",          true,  null,             Vector2.down),
        ("Running Backward.fbx",              "RunBack",           true,  null,             Vector2.down),
        ("Left Strafe Walking.fbx",           "StrafeLeft",        true,  null,             Vector2.left),
        ("Right Strafe Walking.fbx",          "StrafeRight",       true,  null,             Vector2.right),
        ("Strafe.fbx",                        "RunStrafeRight",    true,  "RunStrafeLeft",  Vector2.right),
        ("Crouching Idle (1).fbx",            "CrouchIdle",        true,  null,             Vector2.zero),
        ("Crouch Walk.fbx",                   "CrouchWalk",        true,  null,             Vector2.up),
        ("Crouched Sneaking Left.fbx",        "CrouchStrafeLeft",  true,  null,             Vector2.left),
        ("Crouched Sneaking Right.fbx",       "CrouchStrafeRight", true,  null,             Vector2.right),
        ("Walk Crouching Backward Right.fbx", "CrouchBackRight",   true,  "CrouchBackLeft", new Vector2(1f, -1f).normalized),
        ("Left Cover Sneak.fbx",              "CoverLeft",         true,  null,             Vector2.left),
        ("Right Cover Sneak.fbx",             "CoverRight",        true,  null,             Vector2.right),
        ("Left Turn.fbx",                     "TurnLeft",          false, "TurnRight",      Vector2.zero),
        ("Crouch Torch Turn Left 90.fbx",     "CrouchTurnLeft",    false, "CrouchTurnRight", Vector2.zero),
        ("Standing To Crouched (1).fbx",      "StandToCrouch",     false, null,             Vector2.zero),
        ("Standing W_Briefcase Idle.fbx",     "CarryIdle",         true,  null,             Vector2.zero),
        ("Walk With Briefcase.fbx",           "CarryWalk",         true,  null,             Vector2.up),
        ("Running Jump.fbx",                  "RunJump",           false, null,             Vector2.zero),
        ("Walking Up The Stairs.fbx",         "WalkStairs",        true,  null,             Vector2.up),
        ("Running Up Stairs.fbx",             "RunStairs",         true,  null,             Vector2.up),
        ("Walking Left Turn.fbx",             "WalkTurnLeft",      false, null,             Vector2.zero),
        ("Running Right Turn.fbx",            "RunTurnRight",      false, null,             Vector2.zero),
        ("Walking Turn 180.fbx",              "WalkTurn180",       false, null,             Vector2.zero),
        ("Running Turn 180.fbx",              "RunTurn180",        false, null,             Vector2.zero),
    };

    // Turn-on-the-spot clips: their turning is NOT baked into the pose (with root motion off it's dropped), so the
    // feet step round while the body keeps facing forward; PlayerAnimator turns the body at the clip's pace.
    // They loop, so a long turn keeps stepping.
    static readonly HashSet<string> TurnClips = new HashSet<string> { "TurnLeft", "CrouchTurnLeft" };

    // Clip name -> velocity at playback 1 (m/s, body frame), filled by FixClips
    static readonly Dictionary<string, Vector2> velocities = new Dictionary<string, Vector2>();

    [MenuItem("Tools/RunForYourLife/Player Model Setup")]
    public static void Run()
    {
        string modelPath = Folder + "/" + ModelFile;
        ExtractTextures(modelPath);
        var avatar = SetUpModel(modelPath);
        if (avatar == null || !avatar.isHuman) { Debug.LogError("Player Model Setup: " + modelPath + " didn't make a Humanoid avatar"); return; }
        log.Clear();
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        FixClips(model);
        var crouchBack = MakeReversedClip(LoadClip("CrouchWalk"), CrouchBackPath);
        var coverIdle = MakePoseClip(LoadClip("CoverRight"), CoverIdlePath);
        if (crouchBack != null) velocities[crouchBack.name] = -velocities["CrouchWalk"];
        if (coverIdle != null) velocities[coverIdle.name] = Vector2.zero;
        var controller = BuildController(model, crouchBack, coverIdle, out var rims);
        UpdatePrefab(model, controller, rims);
        AssetDatabase.SaveAssets();
        Debug.Log("Player Model Setup done.\n" + log);
    }

    static readonly System.Text.StringBuilder log = new System.Text.StringBuilder();

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
    static void FixClips(GameObject model)
    {
        var go = Object.Instantiate(model);
        go.hideFlags = HideFlags.HideAndDontSave;
        foreach (var c in Clips)
        {
            string path = Folder + "/" + c.file;
            if (!(AssetImporter.GetAtPath(path) is ModelImporter mi)) { Debug.LogWarning("Missing " + path); continue; }
            mi.animationType = ModelImporterAnimationType.Human;
            mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            var defaults = mi.defaultClipAnimations;
            if (defaults.Length == 0) continue;
            var a = defaults[0];
            a.name = c.clip;
            a.loopTime = c.loop;
            bool turn = TurnClips.Contains(c.clip);
            if (turn) a.loopTime = true;
            a.lockRootRotation = !turn;
            a.lockRootHeightY = true;
            a.lockRootPositionXZ = false;
            a.keepOriginalOrientation = true;    // Mixamo faces +Z; "Body Orientation" skews strafes diagonally
            a.keepOriginalPositionY = false;
            a.keepOriginalPositionXZ = false;
            a.heightFromFeet = true;
            a.mirror = false;
            a.rotationOffset = 0f;
            var clips = new List<ModelImporterClipAnimation> { a };
            if (c.mirror != null)
            {
                var m = Copy(a);
                m.name = c.mirror;
                m.mirror = true;
                clips.Add(m);
            }
            mi.clipAnimations = clips.ToArray();
            mi.SaveAndReimport();

            bool changed = false;
            if (Travel(LoadClip(c.clip)) < 0.1f)
            {
                foreach (var clip in clips) clip.lockRootPositionXZ = true;   // in place
                changed = true;
            }

            // Turn the clip so it travels the way its name says (a positive offset turns the clip clockwise
            // back by that many degrees; the mirrored copy turns the other way)
            if (c.dir != Vector2.zero)
            {
                Vector2 v = Velocity(go, LoadClip(c.clip));
                float measured = Mathf.Atan2(v.x, v.y) * Mathf.Rad2Deg;
                float wanted = Mathf.Atan2(c.dir.x, c.dir.y) * Mathf.Rad2Deg;
                float offset = Mathf.DeltaAngle(wanted, measured);
                if (Mathf.Abs(offset) > 3f)
                {
                    clips[0].rotationOffset = offset;
                    if (clips.Count > 1) clips[1].rotationOffset = -offset;
                    changed = true;
                }
                velocities[c.clip] = c.dir * v.magnitude;
                if (c.mirror != null) velocities[c.mirror] = new Vector2(-c.dir.x, c.dir.y) * v.magnitude;
                log.AppendFormat("  {0}: travelled at {1:F0} deg, turned by {2:F0}, {3:F2} m/s\n", c.clip, measured, -offset, v.magnitude);
            }
            else
            {
                velocities[c.clip] = Vector2.zero;
                if (c.mirror != null) velocities[c.mirror] = Vector2.zero;
            }
            if (changed)
            {
                mi.clipAnimations = clips.ToArray();
                mi.SaveAndReimport();
            }
        }
        Object.DestroyImmediate(go);
    }

    static ModelImporterClipAnimation Copy(ModelImporterClipAnimation a) => new ModelImporterClipAnimation
    {
        takeName = a.takeName, firstFrame = a.firstFrame, lastFrame = a.lastFrame, loopTime = a.loopTime,
        lockRootRotation = a.lockRootRotation, lockRootHeightY = a.lockRootHeightY, lockRootPositionXZ = a.lockRootPositionXZ,
        keepOriginalOrientation = a.keepOriginalOrientation, keepOriginalPositionY = a.keepOriginalPositionY,
        keepOriginalPositionXZ = a.keepOriginalPositionXZ, heightFromFeet = a.heightFromFeet,
        rotationOffset = a.rotationOffset,
    };

    // m/s the clip moves along the ground (0 for In Place clips)
    static float Travel(AnimationClip clip)
    {
        if (clip == null) return 0f;
        Vector3 v = clip.averageSpeed;
        return new Vector2(v.x, v.z).magnitude;
    }

    // A clip by the name FixClips gave it (e.g. "Run", or a mirrored copy like "RunStrafeLeft")
    static AnimationClip LoadClip(string name)
    {
        foreach (var c in Clips)
        {
            if (c.clip != name && c.mirror != name) continue;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(Folder + "/" + c.file))
                if (o is AnimationClip clip && clip.name == name) return clip;
        }
        Debug.LogWarning("No clip named " + name);
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

    // One still frame of a clip as a looping pose (there's no cover idle clip: the cover sneak's first frame)
    static AnimationClip MakePoseClip(AnimationClip source, string path)
    {
        if (source == null) return null;
        var clip = new AnimationClip { name = Path.GetFileNameWithoutExtension(path), frameRate = source.frameRate };
        foreach (var binding in AnimationUtility.GetCurveBindings(source))
        {
            float v = AnimationUtility.GetEditorCurve(source, binding).Evaluate(0f);
            AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0f, 1f, v));
        }
        var settings = AnimationUtility.GetAnimationClipSettings(source);
        settings.stopTime = 1f;
        settings.loopTime = true;
        AnimationUtility.SetAnimationClipSettings(clip, settings);

        var old = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        if (old != null) { EditorUtility.CopySerialized(clip, old); Object.DestroyImmediate(clip); return old; }
        AssetDatabase.CreateAsset(clip, path);
        return clip;
    }

    // How a clip moves at playback 1, in m/s (x = right, y = forward): its root motion, or for In Place clips
    // the opposite of how the planted foot slides under the body
    static Vector2 Velocity(GameObject go, AnimationClip clip)
    {
        if (clip == null) return Vector2.zero;
        if (Travel(clip) >= 0.1f) return new Vector2(clip.averageSpeed.x, clip.averageSpeed.z);
        return FootVelocity(go, clip);
    }

    static Vector2 FootVelocity(GameObject go, AnimationClip clip)
    {
        var anim = go.GetComponent<Animator>();
        var feet = new[] { anim.GetBoneTransform(HumanBodyBones.LeftFoot), anim.GetBoneTransform(HumanBodyBones.RightFoot) };
        const int samples = 120;
        var pos = new Vector3[2, samples + 1];
        for (int i = 0; i <= samples; i++)
        {
            clip.SampleAnimation(go, clip.length * i / samples);
            for (int f = 0; f < 2; f++) pos[f, i] = go.transform.InverseTransformPoint(feet[f].position);
        }

        // Direction: only count moments where one foot is clearly the one on the ground (2 cm lower than the
        // other) and see which way it slides
        Vector3 slide = Vector3.zero;
        for (int i = 0; i < samples; i++)
        {
            float a = pos[1, i].y - pos[0, i].y, b = pos[1, i + 1].y - pos[0, i + 1].y;
            int f;
            if (a > 0.02f && b > 0.02f) f = 0;
            else if (a < -0.02f && b < -0.02f) f = 1;
            else continue;
            slide += pos[f, i + 1] - pos[f, i];
        }
        Vector2 dir = new Vector2(-slide.x, -slide.z);
        if (dir.sqrMagnitude < 0.0001f) return Vector2.zero;
        dir.Normalize();

        // Speed: follow whichever foot is lower all the way round, along that direction
        float travel = 0f;
        for (int i = 0; i < samples; i++)
        {
            int f = pos[0, i].y + pos[0, i + 1].y < pos[1, i].y + pos[1, i + 1].y ? 0 : 1;
            Vector3 d = pos[f, i + 1] - pos[f, i];
            travel += Mathf.Abs(d.x * dir.x + d.z * dir.y);
        }
        return dir * (travel / clip.length);
    }

    // Fastest clip speed in each direction of a blend tree (PlayerAnimator plays clips faster beyond these)
    struct Rim { public float forward, back, side; }

    static Rim AddClips(BlendTree tree, GameObject go, params AnimationClip[] clips)
    {
        var rim = new Rim();
        foreach (var clip in clips)
        {
            if (clip == null) continue;
            velocities.TryGetValue(clip.name, out Vector2 v);
            tree.AddChild(clip, v);
            log.AppendFormat("  {0} / {1}: ({2:F2}, {3:F2}) m/s\n", tree.name, clip.name, v.x, v.y);
            if (Mathf.Abs(v.x) > Mathf.Abs(v.y)) rim.side = Mathf.Max(rim.side, Mathf.Abs(v.x));
            else if (v.y > 0f) rim.forward = Mathf.Max(rim.forward, v.y);
            else rim.back = Mathf.Max(rim.back, -v.y);
        }
        return rim;
    }

    struct Rims { public Rim stand, crouch, cover; public float standTurn, crouchTurn; }

    // Degrees per second a turn clip turns (its root turning, which isn't baked into the pose)
    static float TurnSpeed(AnimationClip clip)
    {
        if (clip == null) return 90f;
        float s = Mathf.Abs(clip.averageAngularSpeed);
        if (s < 7f) s *= Mathf.Rad2Deg;   // in case it's reported in radians
        log.AppendFormat("  {0}: turns {1:F0} deg/s\n", clip.name, s);
        return s > 1f ? s : 90f;
    }

    // Three 2D blend trees on MoveX / MoveZ (velocity in the model's own frame, m/s): standing, crouched and in
    // cover (back to a wall; in those clips she faces -Z). Each clip sits at the velocity it really moves at.
    static AnimatorController BuildController(GameObject model, AnimationClip crouchBack, AnimationClip coverIdle, out Rims rims)
    {
        var go = Object.Instantiate(model);
        go.hideFlags = HideFlags.HideAndDontSave;

        AssetDatabase.DeleteAsset(ControllerPath);
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        ctrl.AddParameter("MoveX", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("MoveZ", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("Crouch", AnimatorControllerParameterType.Bool);
        ctrl.AddParameter("Cover", AnimatorControllerParameterType.Bool);
        ctrl.AddParameter("Turning", AnimatorControllerParameterType.Bool);
        ctrl.AddParameter("Turn", AnimatorControllerParameterType.Float);   // -1 left, +1 right

        var layers = ctrl.layers;
        layers[0].iKPass = true;   // head looks where the player looks
        ctrl.layers = layers;

        var stand = ctrl.CreateBlendTreeInController("Stand", out var standTree, 0);
        Setup2D(standTree);
        rims.stand = AddClips(standTree, go, LoadClip("Idle"), LoadClip("Walk"), LoadClip("Run"), LoadClip("WalkBack"),
            LoadClip("RunBack"), LoadClip("StrafeLeft"), LoadClip("StrafeRight"), LoadClip("RunStrafeLeft"), LoadClip("RunStrafeRight"));

        var crouch = ctrl.CreateBlendTreeInController("Crouch", out var crouchTree, 0);
        Setup2D(crouchTree);
        rims.crouch = AddClips(crouchTree, go, LoadClip("CrouchIdle"), LoadClip("CrouchWalk"), crouchBack,
            LoadClip("CrouchStrafeLeft"), LoadClip("CrouchStrafeRight"), LoadClip("CrouchBackRight"), LoadClip("CrouchBackLeft"));

        var cover = ctrl.CreateBlendTreeInController("Cover", out var coverTree, 0);
        Setup2D(coverTree);
        rims.cover = AddClips(coverTree, go, coverIdle, LoadClip("CoverLeft"), LoadClip("CoverRight"));

        // Turning on the spot: left turn, idle, right turn on the Turn parameter
        var standTurn = ctrl.CreateBlendTreeInController("StandTurn", out var standTurnTree, 0);
        SetupTurn(standTurnTree, LoadClip("TurnLeft"), LoadClip("Idle"), LoadClip("TurnRight"));
        var crouchTurn = ctrl.CreateBlendTreeInController("CrouchTurn", out var crouchTurnTree, 0);
        SetupTurn(crouchTurnTree, LoadClip("CrouchTurnLeft"), LoadClip("CrouchIdle"), LoadClip("CrouchTurnRight"));
        rims.standTurn = TurnSpeed(LoadClip("TurnLeft"));
        rims.crouchTurn = TurnSpeed(LoadClip("CrouchTurnLeft"));
        Object.DestroyImmediate(go);

        var sm = ctrl.layers[0].stateMachine;
        sm.defaultState = stand;
        Link(stand, crouch, ("Crouch", true), ("Cover", false));
        Link(crouch, stand, ("Crouch", false));
        Link(stand, cover, ("Cover", true));
        Link(crouch, cover, ("Cover", true));
        Link(cover, crouch, ("Cover", false), ("Crouch", true));
        Link(cover, stand, ("Cover", false), ("Crouch", false));
        Link(stand, standTurn, 0.15f, ("Turning", true), ("Cover", false));
        Link(standTurn, stand, 0.2f, ("Turning", false));
        Link(standTurn, crouchTurn, ("Crouch", true));
        Link(standTurn, cover, ("Cover", true));
        Link(crouch, crouchTurn, 0.15f, ("Turning", true), ("Cover", false));
        Link(crouchTurn, crouch, 0.2f, ("Turning", false));
        Link(crouchTurn, standTurn, ("Crouch", false));
        Link(crouchTurn, cover, ("Cover", true));

        EditorUtility.SetDirty(ctrl);
        return ctrl;
    }

    static void Link(AnimatorState from, AnimatorState to, params (string param, bool value)[] conditions) =>
        Link(from, to, 0.3f, conditions);

    static void Link(AnimatorState from, AnimatorState to, float duration, params (string param, bool value)[] conditions)
    {
        var t = from.AddTransition(to);
        t.hasExitTime = false;
        t.duration = duration;
        foreach (var c in conditions)
            t.AddCondition(c.value ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0f, c.param);
    }

    static void SetupTurn(BlendTree tree, AnimationClip left, AnimationClip idle, AnimationClip right)
    {
        tree.blendType = BlendTreeType.Simple1D;
        tree.blendParameter = "Turn";
        tree.useAutomaticThresholds = false;
        if (left != null) tree.AddChild(left, -1f);
        if (idle != null) tree.AddChild(idle, 0f);
        if (right != null) tree.AddChild(right, 1f);
    }

    static void Setup2D(BlendTree tree)
    {
        tree.blendType = BlendTreeType.FreeformDirectional2D;
        tree.blendParameter = "MoveX";
        tree.blendParameterY = "MoveZ";
    }

    // Swap the capsule on the Player prefab for the model
    static void UpdatePrefab(GameObject model, AnimatorController controller, Rims rims)
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

            // Caught players go limp (builds its rigidbodies and joints from the skeleton at runtime)
            if (go.GetComponent<PlayerRagdoll>() == null) go.AddComponent<PlayerRagdoll>();

            var pa = go.GetComponent<PlayerAnimator>();
            if (pa == null) pa = go.AddComponent<PlayerAnimator>();
            pa.standForwardSpeed = rims.stand.forward;
            pa.standBackSpeed = rims.stand.back;
            pa.standSideSpeed = rims.stand.side;
            pa.crouchForwardSpeed = rims.crouch.forward;
            pa.crouchBackSpeed = rims.crouch.back;
            pa.crouchSideSpeed = rims.crouch.side;
            pa.standTurnSpeed = rims.standTurn;
            pa.crouchTurnSpeed = rims.crouchTurn;

            // The flashlight in her right hand
            if (go.GetComponent<FlashlightHand>() == null) go.AddComponent<FlashlightHand>();

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
