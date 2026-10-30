using System.IO;
using UnityEditor;
using UnityEngine;

// Tools > RunForYourLife > Crack Decal Textures
// Paints the ground-slam crater decal from code: a height map (shallow sunken bowl, a pushed-up lip, smashed slabs
// in the middle, cracks running out, a few ring cracks) turned into a normal map, plus a colour+alpha map.
// The bowl lives in the normal map, so flashlights and street lamps make the flat ground look dented.
// Writes Assets/Textures/Decals/GroundCrack_Albedo.png and GroundCrack_Normal.png. Change the seed for a new one.
public class CrackDecalGenerator : EditorWindow
{
    int seed = 7;
    int size = 1024;
    float bowlDepth = 1f;        // how sunken the middle looks
    float lipHeight = 0.35f;     // the pushed-up ring around it
    float crackDepth = 1.2f;
    int radialCracks = 12;
    float normalStrength = 6f;

    const string Folder = "Assets/Textures/Decals";

    [MenuItem("Tools/RunForYourLife/Crack Decal Textures")]
    static void Open() => GetWindow<CrackDecalGenerator>("Crack Decal");

    void OnGUI()
    {
        seed = EditorGUILayout.IntField("Seed", seed);
        size = EditorGUILayout.IntPopup("Size", size, new[] { "512", "1024", "2048" }, new[] { 512, 1024, 2048 });
        bowlDepth = EditorGUILayout.Slider("Bowl depth", bowlDepth, 0f, 3f);
        lipHeight = EditorGUILayout.Slider("Lip height", lipHeight, 0f, 1f);
        crackDepth = EditorGUILayout.Slider("Crack depth", crackDepth, 0f, 3f);
        radialCracks = EditorGUILayout.IntSlider("Radial cracks", radialCracks, 4, 24);
        normalStrength = EditorGUILayout.Slider("Normal strength", normalStrength, 1f, 20f);
        if (GUILayout.Button("Generate")) Generate(seed, size, bowlDepth, lipHeight, crackDepth, radialCracks, normalStrength);
    }

    public static void Generate(int seed, int n, float bowlDepth, float lipHeight, float crackDepth, int radialCracks, float normalStrength)
    {
        var rnd = new System.Random(seed);
        float R() => (float)rnd.NextDouble();

        var height = new float[n * n];
        var crack = new float[n * n];   // 0-1: how much of a crack this pixel is
        var crush = new float[n * n];   // 0-1: smashed centre (dusty, lighter)

        // Smashed slabs in the middle: Voronoi cells, each tilted a little, with cracks along the cell edges
        int siteCount = 22;
        var sites = new Vector2[siteCount];
        var tilts = new Vector2[siteCount];
        for (int i = 0; i < siteCount; i++)
        {
            // Keep the cell centres apart: two almost on top of each other make a cell that's all crack
            for (int tries = 0; tries < 30; tries++)
            {
                float a = R() * Mathf.PI * 2f, d = Mathf.Sqrt(R()) * 0.34f;
                sites[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * d;
                bool clear = true;
                for (int j = 0; j < i; j++) if ((sites[j] - sites[i]).magnitude < 0.08f) { clear = false; break; }
                if (clear) break;
            }
            tilts[i] = new Vector2(R() - 0.5f, R() - 0.5f) * 0.9f;
        }

        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int i = y * n + x;
            var p = new Vector2(x / (n - 1f) * 2f - 1f, y / (n - 1f) * 2f - 1f);
            float r = p.magnitude;

            // Bowl + lip
            float bowl = Mathf.Clamp01(1f - r / 0.42f);
            float h = -bowlDepth * bowl * bowl * (3f - 2f * bowl);
            h += lipHeight * Mathf.Exp(-Mathf.Pow((r - 0.43f) / 0.06f, 2f));

            // Slabs: nearest and second-nearest site
            float c = Mathf.Clamp01(1f - (r - 0.3f) / 0.1f);   // fades out past the bowl
            if (c > 0f)
            {
                int best = 0; float d1 = float.MaxValue, d2 = float.MaxValue;
                for (int s = 0; s < siteCount; s++)
                {
                    float d = (p - sites[s]).sqrMagnitude;
                    if (d < d1) { d2 = d1; d1 = d; best = s; } else if (d < d2) d2 = d;
                }
                float edge = Mathf.Sqrt(d2) - Mathf.Sqrt(d1);
                h += Vector2.Dot(p - sites[best], tilts[best]) * 0.25f * c;
                crack[i] = Mathf.Max(crack[i], Mathf.Clamp01(1f - edge / 0.012f) * c);
                crush[i] = c;
            }
            height[i] = h;
        }

        // Radial cracks: jagged random walks out from the bowl, thinning as they go, sometimes splitting
        for (int k = 0; k < radialCracks; k++)
        {
            float a = (k + R() * 0.7f) / radialCracks * Mathf.PI * 2f;
            Walk(new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (0.15f + R() * 0.1f), a, 0.55f + R() * 0.4f, 0.011f, 2);
        }
        // Ring cracks: broken arcs around the bowl
        for (int k = 0; k < 3; k++)
        {
            float rr = 0.36f + R() * 0.2f, a0 = R() * Mathf.PI * 2f, span = 0.8f + R() * 1.8f;
            for (float t = 0f; t < span; t += 0.0015f)
            {
                float rj = rr + Mathf.Sin(t * 23f + k) * 0.008f + (Mathf.PerlinNoise(t * 40f, k * 3.1f) - 0.5f) * 0.02f;
                Stamp(new Vector2(Mathf.Cos(a0 + t), Mathf.Sin(a0 + t)) * rj, 0.006f * (1f - Mathf.Abs(t / span * 2f - 1f) * 0.6f));
            }
        }

        void Walk(Vector2 p, float dir, float length, float width, int depth)
        {
            float travelled = 0f;
            while (travelled < length && p.magnitude < 0.97f)
            {
                dir += (R() - 0.5f) * 0.5f;
                float outward = Mathf.Atan2(p.y, p.x);
                dir = Mathf.LerpAngle(dir * Mathf.Rad2Deg, outward * Mathf.Rad2Deg, 0.05f) * Mathf.Deg2Rad;   // keep heading out
                float step = 0.002f;
                p += new Vector2(Mathf.Cos(dir), Mathf.Sin(dir)) * step;
                travelled += step;
                float w = width * (1f - travelled / length * 0.8f) * Mathf.Clamp01(0.3f + travelled / 0.04f);   // sharp start, thinning end
                Stamp(p, w);
                if (depth > 0 && R() < 0.004f)
                    Walk(p, dir + (R() < 0.5f ? -1f : 1f) * (0.4f + R() * 0.6f), (length - travelled) * (0.4f + R() * 0.4f), w * 0.7f, depth - 1);
            }
        }

        void Stamp(Vector2 p, float radius)
        {
            float pr = radius * (n - 1) / 2f;
            float cx = (p.x + 1f) * (n - 1) / 2f, cy = (p.y + 1f) * (n - 1) / 2f;
            int r0 = Mathf.CeilToInt(pr + 1);
            for (int y = (int)cy - r0; y <= (int)cy + r0; y++)
            for (int x = (int)cx - r0; x <= (int)cx + r0; x++)
            {
                if (x < 0 || y < 0 || x >= n || y >= n) continue;
                float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                float v = Mathf.Clamp01(1f - d / Mathf.Max(pr, 0.7f));
                v = v * v * (3f - 2f * v);   // rounded groove, not a cone (cones in a row look beaded)
                int i = y * n + x;
                crack[i] = Mathf.Max(crack[i], v);
            }
        }

        // Grain so the slabs aren't glassy
        float ox = R() * 100f, oy = R() * 100f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int i = y * n + x;
            float g = Mathf.PerlinNoise(ox + x * 0.08f, oy + y * 0.08f) - 0.5f;
            height[i] += g * 0.04f - crackDepth * crack[i] * 0.15f;
        }

        // Colour + alpha, and the normal map from the height slopes
        var albedo = new Color32[n * n];
        var normal = new Color32[n * n];
        var preview = new Color32[n * n];
        var light = new Vector3(-0.6f, 0.5f, 0.62f).normalized;   // low light from the top-left, like a flashlight
        float texel = 2f / (n - 1);
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int i = y * n + x;
            var p = new Vector2(x / (n - 1f) * 2f - 1f, y / (n - 1f) * 2f - 1f);
            float r = p.magnitude;

            float hl = height[y * n + Mathf.Max(x - 1, 0)], hr = height[y * n + Mathf.Min(x + 1, n - 1)];
            float hd = height[Mathf.Max(y - 1, 0) * n + x], hu = height[Mathf.Min(y + 1, n - 1) * n + x];
            var nrm = new Vector3(-(hr - hl) / (2f * texel) * normalStrength * 0.01f,
                                  -(hu - hd) / (2f * texel) * normalStrength * 0.01f, 1f).normalized;
            float fade = Mathf.Clamp01((0.98f - r) / 0.1f);   // nothing reaches the decal's edge
            nrm = Vector3.Slerp(Vector3.forward, nrm, fade);
            normal[i] = new Color(nrm.x * 0.5f + 0.5f, nrm.y * 0.5f + 0.5f, nrm.z * 0.5f + 0.5f, 1f);

            // Opacity: solid over the bowl (so its shading shows), then only the cracks further out
            float bowlA = Mathf.Clamp01(1f - (r - 0.4f) / 0.15f) * 0.9f;
            float a = Mathf.Max(bowlA, crack[i]) * fade;
            float grey = Mathf.Lerp(0.22f, 0.34f, crush[i]);                  // dusty smashed centre
            grey = Mathf.Lerp(grey, 0.03f, Mathf.Clamp01(crack[i] * 1.3f));   // dark gaps
            albedo[i] = new Color(grey, grey * 0.97f, grey * 0.93f, a);

            // Lit preview on a plain asphalt grey, to judge the dent
            float lit = 0.25f + 0.75f * Mathf.Max(0f, Vector3.Dot(nrm, light));
            float baseGrey = 0.25f;
            float col = Mathf.Lerp(baseGrey, grey, a) * lit * 1.6f;
            preview[i] = new Color(col, col, col, 1f);
        }

        Directory.CreateDirectory(Folder);
        string albedoPath = Folder + "/GroundCrack_Albedo.png";
        string normalPath = Folder + "/GroundCrack_Normal.png";
        Save(albedoPath, albedo, n, true);
        Save(normalPath, normal, n, false);
        Save(Path.Combine(Path.GetTempPath(), "GroundCrack_Preview.png"), preview, n, false, false);

        AssetDatabase.Refresh();
        Import(albedoPath, false);
        Import(normalPath, true);
        Debug.Log($"Crack decal written to {Folder} (seed {seed}). Lit preview: {Path.Combine(Path.GetTempPath(), "GroundCrack_Preview.png")}");
    }

    static void Save(string path, Color32[] pixels, int n, bool alpha, bool linear = true)
    {
        var tex = new Texture2D(n, n, alpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, false, linear);
        tex.SetPixels32(pixels);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        DestroyImmediate(tex);
    }

    static void Import(string path, bool isNormal)
    {
        var imp = (TextureImporter)AssetImporter.GetAtPath(path);
        if (imp == null) return;
        imp.textureType = isNormal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        imp.wrapMode = TextureWrapMode.Clamp;
        imp.alphaIsTransparency = !isNormal;
        imp.sRGBTexture = !isNormal;
        imp.mipmapEnabled = true;
        imp.SaveAndReimport();
    }
}
