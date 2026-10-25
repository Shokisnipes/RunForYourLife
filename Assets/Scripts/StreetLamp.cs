using System.Collections.Generic;
using UnityEngine;

// A street lamp. Made by Tools -> RunForYourLife -> Street Lamps.
// Normal lamps glow steadily, Flickering ones stutter, Broken ones are dark.
// Standing under a lit lamp makes you visible to the giant from much further away.
public class StreetLamp : MonoBehaviour
{
    public enum Mode { Normal, Flickering, Broken }

    public static readonly List<StreetLamp> All = new List<StreetLamp>();

    public Mode mode = Mode.Normal;
    public Light lamp;
    public Renderer bulb;
    [Tooltip("Players within this distance of the pole (on the ground) count as lit.")]
    public float litRadius = 5f;

    [Header("Flicker")]
    public float flickerSpeed = 8f;
    [Range(0f, 1f)] public float flickerOffChance = 0.25f;   // how much of the time it's out

    float baseIntensity;
    Color baseEmission;
    MaterialPropertyBlock mpb;
    float seed;
    float level = 1f;
    static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    void Start()
    {
        seed = (transform.position.x * 13.7f + transform.position.z * 7.3f) % 100f;
        mpb = new MaterialPropertyBlock();
        if (lamp != null) baseIntensity = lamp.intensity;
        if (bulb != null && bulb.sharedMaterial != null && bulb.sharedMaterial.HasProperty(EmissionId))
            baseEmission = bulb.sharedMaterial.GetColor(EmissionId);

        if (mode == Mode.Broken) SetLevel(0f);
    }

    void Update()
    {
        if (mode != Mode.Flickering) return;

        // Mostly on, with stuttering drop-outs
        float n = Mathf.PerlinNoise(seed, Time.time * flickerSpeed);
        float target = n < flickerOffChance ? Random.Range(0f, 0.15f) : Random.Range(0.85f, 1f);
        SetLevel(target);
    }

    void SetLevel(float l)
    {
        level = l;
        if (lamp != null)
        {
            lamp.intensity = baseIntensity * l;
            lamp.enabled = l > 0.02f;
        }
        if (bulb != null)
        {
            bulb.GetPropertyBlock(mpb);
            mpb.SetColor(EmissionId, baseEmission * l);
            bulb.SetPropertyBlock(mpb);
        }
    }

    public bool IsLit => mode != Mode.Broken && level > 0.5f;

    // Is this position standing in any lit lamp's pool of light?
    public static bool IsUnderLight(Vector3 position)
    {
        foreach (var l in All)
        {
            if (!l.IsLit) continue;
            Vector3 p = l.lamp != null ? l.lamp.transform.position : l.transform.position;
            float dx = position.x - p.x, dz = position.z - p.z;
            if (dx * dx + dz * dz <= l.litRadius * l.litRadius && position.y < p.y) return true;
        }
        return false;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.7f, 0.3f, 0.5f);
        Vector3 p = lamp != null ? lamp.transform.position : transform.position;
        p.y = transform.position.y + 0.05f;
        const int seg = 24;
        Vector3 prev = p + new Vector3(litRadius, 0, 0);
        for (int i = 1; i <= seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            Vector3 next = p + new Vector3(Mathf.Cos(a) * litRadius, 0, Mathf.Sin(a) * litRadius);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }
}
