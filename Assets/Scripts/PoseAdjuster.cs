using System.Collections.Generic;
using UnityEngine;

// Put on the monster's Model (next to the Animator). Adds your own rotations to bones
// ON TOP of whatever animation is playing: e.g. "while peeking, bend the spine 20° and drop the head".
// Tweak values live in Play mode, then right-click the component -> Copy Component,
// stop Play, right-click -> Paste Component Values to keep them.
[RequireComponent(typeof(Animator))]
public class PoseAdjuster : MonoBehaviour
{
    public enum When { Always, WhilePeeking, WhileNotPeeking }

    [System.Serializable]
    public class BoneTweak
    {
        public HumanBodyBones bone = HumanBodyBones.Spine;
        [Tooltip("Extra rotation in degrees, added to the animated pose.")]
        public Vector3 rotation;
        public When when = When.WhilePeeking;
    }

    public List<BoneTweak> tweaks = new List<BoneTweak>();
    [Tooltip("How quickly tweaks fade in/out when the giant starts or stops peeking.")]
    public float blendSpeed = 2f;

    Animator animator;
    MonsterAI ai;
    float peekWeight;

    void Awake()
    {
        animator = GetComponent<Animator>();
        ai = GetComponentInParent<MonsterAI>();
    }

    void LateUpdate()
    {
        if (animator == null || !animator.isHuman) return;

        bool peeking = ai != null && ai.SyncedPeeking;
        peekWeight = Mathf.MoveTowards(peekWeight, peeking ? 1f : 0f, Time.deltaTime * blendSpeed);

        foreach (var t in tweaks)
        {
            float w = t.when == When.Always ? 1f : t.when == When.WhilePeeking ? peekWeight : 1f - peekWeight;
            if (w <= 0f || t.rotation == Vector3.zero) continue;

            var b = animator.GetBoneTransform(t.bone);
            if (b == null) continue;
            b.localRotation *= Quaternion.Slerp(Quaternion.identity, Quaternion.Euler(t.rotation), w);
        }
    }
}
