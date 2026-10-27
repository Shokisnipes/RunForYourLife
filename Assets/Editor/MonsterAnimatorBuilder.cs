using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// Tools -> RunForYourLife -> Monster Animator Builder
// Drag in your Mixamo clips and it builds the Animator Controller for the giant:
// Locomotion (idle/walk/run blend), Peek (crouched at a doorway), Grab.
public class MonsterAnimatorBuilder : EditorWindow
{
    AnimationClip idle, walk, run, peek, grab;
    Animator targetAnimator;
    string savePath = "Assets/Monsters/Monster/MonsterAnimator.controller";

    [MenuItem("Tools/RunForYourLife/Monster Animator Builder")]
    static void Open() => GetWindow<MonsterAnimatorBuilder>("Monster Animator");

    void OnGUI()
    {
        GUILayout.Label("Clips (expand each FBX and drag the clip inside)", EditorStyles.boldLabel);
        idle = Clip("Idle", idle);
        walk = Clip("Walk", walk);
        run = Clip("Run", run);
        peek = Clip("Peek (crouch/kneel idle)", peek);
        grab = Clip("Grab (optional)", grab);

        GUILayout.Space(8);
        targetAnimator = (Animator)EditorGUILayout.ObjectField("Monster Model Animator", targetAnimator, typeof(Animator), true);
        savePath = EditorGUILayout.TextField("Save As", savePath);

        GUILayout.Space(10);
        GUI.enabled = idle != null && walk != null && run != null;
        if (GUILayout.Button("Build Animator Controller", GUILayout.Height(30))) Build();
        GUI.enabled = true;

        EditorGUILayout.HelpBox("Idle, Walk and Run are required. Peek and Grab are optional.", MessageType.Info);
    }

    static AnimationClip Clip(string label, AnimationClip clip) =>
        (AnimationClip)EditorGUILayout.ObjectField(label, clip, typeof(AnimationClip), false);

    void Build()
    {
        string folder = System.IO.Path.GetDirectoryName(savePath).Replace('\\', '/');
        if (!AssetDatabase.IsValidFolder(folder))
        {
            string parent = System.IO.Path.GetDirectoryName(folder).Replace('\\', '/');
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(folder));
        }

        var controller = AnimatorController.CreateAnimatorControllerAtPath(savePath);
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
        controller.AddParameter("Peeking", AnimatorControllerParameterType.Bool);
        controller.AddParameter("Grab", AnimatorControllerParameterType.Trigger);

        // IK pass on: needed for head tracking and reaching
        var layers = controller.layers;
        layers[0].iKPass = true;
        controller.layers = layers;
        var sm = controller.layers[0].stateMachine;

        // Locomotion: 0 idle, 0.5 walk, 1 run
        var locomotion = controller.CreateBlendTreeInController("Locomotion", out BlendTree tree, 0);
        tree.blendType = BlendTreeType.Simple1D;
        tree.blendParameter = "Speed";
        tree.useAutomaticThresholds = false;
        tree.AddChild(idle, 0f);
        tree.AddChild(walk, 0.5f);
        tree.AddChild(run, 1f);
        sm.defaultState = locomotion;

        // Peek: slow, smooth crouch in and out
        if (peek != null)
        {
            var peekState = sm.AddState("Peek");
            peekState.motion = peek;

            var toPeek = locomotion.AddTransition(peekState);
            toPeek.hasExitTime = false;
            toPeek.duration = 0.6f;
            toPeek.AddCondition(AnimatorConditionMode.If, 0f, "Peeking");

            var fromPeek = peekState.AddTransition(locomotion);
            fromPeek.hasExitTime = false;
            fromPeek.duration = 0.8f;
            fromPeek.AddCondition(AnimatorConditionMode.IfNot, 0f, "Peeking");
        }

        // Grab: from anywhere, then ease back to locomotion
        if (grab != null)
        {
            var grabState = sm.AddState("Grab");
            grabState.motion = grab;

            var toGrab = sm.AddAnyStateTransition(grabState);
            toGrab.hasExitTime = false;
            toGrab.duration = 0.15f;
            toGrab.canTransitionToSelf = false;
            toGrab.AddCondition(AnimatorConditionMode.If, 0f, "Grab");

            var fromGrab = grabState.AddTransition(locomotion);
            fromGrab.hasExitTime = true;
            fromGrab.exitTime = 0.85f;
            fromGrab.duration = 0.5f;
        }

        AssetDatabase.SaveAssets();

        if (targetAnimator != null)
        {
            Undo.RecordObject(targetAnimator, "Assign Monster Animator");
            targetAnimator.runtimeAnimatorController = controller;
            targetAnimator.applyRootMotion = false;
            EditorUtility.SetDirty(targetAnimator);
        }

        Selection.activeObject = controller;
        Debug.Log($"Monster Animator Controller built at {savePath}");
    }
}
