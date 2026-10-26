using UnityEngine;

// Makes a lit window's glow stutter now and then (bad wiring, dying bulb).
public class WindowFlicker : MonoBehaviour
{
    public float speed = 6f;
    [Range(0f, 1f)] public float outChance = 0.3f;

    Renderer rend;
    MaterialPropertyBlock mpb;
    Color baseEmission;
    float seed;
    static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

    void Start()
    {
        rend = GetComponent<Renderer>();
        mpb = new MaterialPropertyBlock();
        seed = Random.value * 100f;
        if (rend != null && rend.sharedMaterial != null && rend.sharedMaterial.HasProperty(EmissionId))
            baseEmission = rend.sharedMaterial.GetColor(EmissionId);
    }

    void Update()
    {
        if (rend == null) return;
        float n = Mathf.PerlinNoise(seed, Time.time * speed);
        float level = n < outChance ? 0.05f : Mathf.Lerp(0.7f, 1f, n);
        rend.GetPropertyBlock(mpb);
        mpb.SetColor(EmissionId, baseEmission * level);
        rend.SetPropertyBlock(mpb);
    }
}
