using System.Collections.Generic;
using UnityEngine;

// Put on the Main Camera inside the Player prefab.
// Anything can shake nearby cameras: CameraShake.ShakeFrom(position, strength, range).
// Only the camera you're actually looking through reacts.
[RequireComponent(typeof(Camera))]
public class CameraShake : MonoBehaviour
{
    [Header("Wobble")]
    public float maxOffset = 0.12f;      // meters
    public float maxAngle = 2.5f;        // degrees
    public float frequency = 22f;        // how fast it jitters
    public float recovery = 1.6f;        // how fast it settles

    [Header("Impact kick (the instant 'thud' on each hit)")]
    public float kickDrop = 0.06f;       // meters the view dips on impact
    public float kickPitch = 1.5f;       // degrees the view nods down on impact
    public float kickTilt = 1.2f;        // degrees of random side-tilt per hit, so no two feel the same
    public float kickRecovery = 14f;     // how fast it springs back (higher = snappier)

    [Header("Distance")]
    [Tooltip("X = how close the source is (0 = at max range, 1 = right on top of you). Y = shake multiplier.")]
    public AnimationCurve closenessResponse = new AnimationCurve(
        new Keyframe(0f, 0f, 0f, 0.3f),
        new Keyframe(0.5f, 0.25f),
        new Keyframe(0.8f, 0.8f),
        new Keyframe(1f, 2f, 6f, 0f));
    [Range(0f, 0.5f)] public float randomness = 0.25f;   // each hit is up to this much stronger/weaker

    [Tooltip("Overall multiplier. Turn it up or down to taste.")]
    [Range(0f, 5f)] public float intensity = 2.5f;

    static readonly List<CameraShake> all = new List<CameraShake>();

    Camera cam;
    float trauma;                        // 0..1+, drives the wobble
    float kick;                          // spikes instantly, decays fast
    float kickTiltDir;                   // random -1..1 per hit
    float kickYawDir;
    float seed;
    Vector3 basePos;
    Quaternion baseRot;

    void Awake()
    {
        cam = GetComponent<Camera>();
        seed = Random.value * 100f;
        basePos = transform.localPosition;
        baseRot = transform.localRotation;
    }

    void OnEnable() => all.Add(this);
    void OnDisable() => all.Remove(this);

    // strength 0..1 at the source; how much reaches you depends on distance and the closeness curve
    public static void ShakeFrom(Vector3 source, float strength, float range)
    {
        foreach (var s in all) s.Receive(source, strength, range);
    }

    void Receive(Vector3 source, float strength, float range)
    {
        if (!cam.enabled) return;   // other players' cameras

        float closeness = 1f - Vector3.Distance(source, transform.position) / range;
        if (closeness <= 0f) return;

        float amount = strength * closenessResponse.Evaluate(closeness);
        amount *= 1f + Random.Range(-randomness, randomness);
        if (amount <= 0f) return;

        trauma = Mathf.Clamp(trauma + amount, 0f, 1.5f);
        kick = Mathf.Clamp(Mathf.Max(kick, amount * 1.5f), 0f, 2f);
        kickTiltDir = Random.Range(-1f, 1f);
        kickYawDir = Random.Range(-1f, 1f);
    }

    void LateUpdate()
    {
        trauma = Mathf.MoveTowards(trauma, 0f, recovery * Time.deltaTime);
        float shake = Mathf.Pow(trauma, 1.3f) * intensity;

        float t = Time.time * frequency;
        float nx = Mathf.PerlinNoise(seed, t) * 2f - 1f;
        float ny = Mathf.PerlinNoise(seed + 1f, t) * 2f - 1f;
        float nr = Mathf.PerlinNoise(seed + 2f, t) * 2f - 1f;

        // The kick hits on the exact frame of the impact, then springs back
        kick = Mathf.Lerp(kick, 0f, 1f - Mathf.Exp(-kickRecovery * Time.deltaTime));
        float k = kick * intensity;

        transform.localPosition = basePos
            + new Vector3(nx, ny, 0f) * maxOffset * shake
            + Vector3.down * kickDrop * k;

        transform.localRotation = baseRot * Quaternion.Euler(
            ny * maxAngle * shake + kickPitch * k,
            nx * maxAngle * shake + kickYawDir * kickTilt * 0.5f * k,
            nr * maxAngle * shake + kickTiltDir * kickTilt * k);
    }
}
