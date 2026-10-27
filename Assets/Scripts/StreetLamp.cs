using System.Collections.Generic;
using UnityEngine;

// A street lamp. Made by Tools -> RunForYourLife -> Street Lamps.
// Normal lamps glow steadily, Flickering ones stutter, Broken ones are dark.
// Standing under a lit lamp makes you visible to the giant from much further away.
// A soft cone of light (RunForYourLife/LightShaft) hangs under each working lamp, so you can see the beam in the fog.
public class StreetLamp : MonoBehaviour
{
    public enum Mode { Normal, Flickering, Broken }

    public static readonly List<StreetLamp> All = new List<StreetLamp>();

    public Mode mode = Mode.Normal;
    public Light lamp;
    public Renderer bulb;
    [Tooltip("Players within this distance of the pole (on the ground) count as lit.")]
    public float litRadius = 5f;

    [Header("Light beam")]
    [Tooltip("Material for the visible cone of light (RunForYourLife/LightShaft). Empty = no beam.")]
    public Material shaftMaterial;
    [Tooltip("Radius of the beam where it reaches the ground.")]
    public float shaftGroundRadius = 3.6f;

    [Header("Flicker")]
    public float flickerSpeed = 8f;
    [Range(0f, 1f)] public float flickerOffChance = 0.25f;   // how much of the time it's out

    float baseIntensity;
    Color baseEmission;
    MaterialPropertyBlock mpb, shaftMpb;
    Renderer shaft;
    static Mesh coneMesh;
    static readonly int ShaftLevelId = Shader.PropertyToID("_ShaftLevel");
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

        MakeShaft();
        if (mode == Mode.Broken) SetLevel(0f);
    }

    // The visible beam: a cone from the bulb down to the ground
    void MakeShaft()
    {
        if (shaftMaterial == null || lamp == null) return;
        Vector3 top = lamp.transform.position;
        float height = top.y - transform.position.y;
        if (height < 0.5f) return;

        var go = new GameObject("Shaft", typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(transform, false);
        go.transform.position = top;
        go.transform.rotation = Quaternion.identity;
        go.transform.localScale = new Vector3(shaftGroundRadius, height, shaftGroundRadius);
        go.GetComponent<MeshFilter>().sharedMesh = ConeMesh();
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = shaftMaterial;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        shaft = r;
        shaftMpb = new MaterialPropertyBlock();
    }

    // Open cone, 1 m tall, pointing down from (0,0,0): radius 0.07 at the top (the bulb), 1 at the bottom.
    // uv.y = 0 at the top, 1 at the bottom (the shader fades towards the ground with it).
    static Mesh ConeMesh()
    {
        if (coneMesh != null) return coneMesh;
        const int sides = 24;
        var verts = new Vector3[(sides + 1) * 2];
        var normals = new Vector3[verts.Length];
        var uvs = new Vector2[verts.Length];
        var tris = new int[sides * 6];
        const float topR = 0.07f;
        for (int i = 0; i <= sides; i++)
        {
            float a = i * Mathf.PI * 2f / sides;
            var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            verts[i * 2] = dir * topR;
            verts[i * 2 + 1] = dir + Vector3.down;
            var n = (dir + Vector3.up * (1f - topR)).normalized;   // outward, tilted for the slope
            normals[i * 2] = normals[i * 2 + 1] = n;
            uvs[i * 2] = new Vector2(i / (float)sides, 0f);
            uvs[i * 2 + 1] = new Vector2(i / (float)sides, 1f);
        }
        for (int i = 0; i < sides; i++)
        {
            int b = i * 2, t = i * 6;
            tris[t] = b; tris[t + 1] = b + 2; tris[t + 2] = b + 1;
            tris[t + 3] = b + 1; tris[t + 4] = b + 2; tris[t + 5] = b + 3;
        }
        coneMesh = new Mesh { name = "LightShaftCone", vertices = verts, normals = normals, uv = uvs, triangles = tris };
        coneMesh.RecalculateBounds();
        return coneMesh;
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
        if (shaft != null)
        {
            shaft.enabled = l > 0.02f;
            shaftMpb.SetFloat(ShaftLevelId, l);
            shaft.SetPropertyBlock(shaftMpb);
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
