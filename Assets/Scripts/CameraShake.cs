using System.Collections.Generic;
using UnityEngine;

// Put on the Main Camera inside the Player prefab.
// Anything can shake nearby cameras: CameraShake.ShakeFrom(position, strength, range).
// Only the camera you're actually looking through reacts.
[RequireComponent(typeof(Camera))]
public class CameraShake : MonoBehaviour
{
    public float maxOffset = 0.12f;      // meters
    public float maxAngle = 2.5f;        // degrees
    public float frequency = 22f;        // how fast it jitters
    public float recovery = 1.6f;        // how fast it settles
    [Tooltip("Overall multiplier. Turn it up or down to taste.")]
    [Range(0f, 5f)] public float intensity = 2.5f;

    static readonly List<CameraShake> all = new List<CameraShake>();

    Camera cam;
    float trauma;                        // 0..1
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

    // strength 0..1 at the source, fading to nothing at 'range' meters
    public static void ShakeFrom(Vector3 source, float strength, float range)
    {
        foreach (var s in all)
        {
            if (!s.cam.enabled) continue;   // other players' cameras
            float falloff = 1f - Vector3.Distance(source, s.transform.position) / range;
            if (falloff <= 0f) continue;
            s.trauma = Mathf.Clamp01(s.trauma + strength * falloff);
        }
    }

    void LateUpdate()
    {
        trauma = Mathf.MoveTowards(trauma, 0f, recovery * Time.deltaTime);
        float shake = Mathf.Pow(trauma, 1.3f) * intensity;   // big hits are noticeably more violent

        float t = Time.time * frequency;
        float nx = Mathf.PerlinNoise(seed, t) * 2f - 1f;
        float ny = Mathf.PerlinNoise(seed + 1f, t) * 2f - 1f;
        float nr = Mathf.PerlinNoise(seed + 2f, t) * 2f - 1f;

        transform.localPosition = basePos + new Vector3(nx, ny, 0f) * maxOffset * shake;
        transform.localRotation = baseRot * Quaternion.Euler(ny * maxAngle * shake, nx * maxAngle * shake, nr * maxAngle * shake);
    }
}
