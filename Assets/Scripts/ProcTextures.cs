using System.Collections.Generic;
using UnityEngine;

// Textures made in code, for building variety without image files: roof tiles, wall grime and stains, moss, posters,
// graffiti and awning stripes. Each one is made once (from its own fixed seed, so every machine gets the same
// picture) and shared by every building that uses it.
public static class ProcTextures
{
    public enum Decal { Streak, Stain, Moss, Poster, Graffiti, Claws, Handprint, Blood }

    // How many different pictures of each decal there are
    public static int Variants(Decal kind) => kind == Decal.Poster || kind == Decal.Graffiti ? 6
        : kind == Decal.Claws || kind == Decal.Handprint || kind == Decal.Blood ? 1 : 3;

    // Width / height of each decal picture (the quad on the wall uses the same shape)
    public static Vector2 Aspect(Decal kind) => kind switch
    {
        Decal.Streak => new Vector2(0.35f, 1f),
        Decal.Moss => new Vector2(4f, 1f),
        Decal.Poster => new Vector2(0.7f, 1f),
        Decal.Graffiti => new Vector2(2f, 1f),
        Decal.Claws => new Vector2(0.8f, 1f),
        _ => Vector2.one,
    };

    static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>();

    public static Texture2D DecalTexture(Decal kind, int variant)
    {
        string key = kind + "_" + variant;
        if (cache.TryGetValue(key, out var tex) && tex != null) return tex;
        var rng = new System.Random(1000 + (int)kind * 97 + variant * 13);
        tex = kind switch
        {
            Decal.Streak => Streak(rng),
            Decal.Stain => Stain(rng),
            Decal.Moss => Moss(rng),
            Decal.Poster => Poster(rng, variant),
            Decal.Claws => Claws(rng),
            Decal.Handprint => Handprint(rng),
            Decal.Blood => Stain(rng, new Color(0.18f, 0.02f, 0.02f), 0.85f),
            _ => Graffiti(rng, variant),
        };
        tex.name = "Decal_" + key;
        cache[key] = tex;
        return tex;
    }

    // ---------- Roof tiles ----------

    // Rows of overlapping tiles, staggered. One texture covers 2 x 2 m. variant 0 = terracotta, 1 = dark slate.
    public static Texture2D RoofTiles(int variant)
    {
        string key = "Roof_" + variant;
        if (cache.TryGetValue(key, out var cached) && cached != null) return cached;
        const int size = 256, rows = 8, cols = 7;
        var rng = new System.Random(500 + variant);
        Color baseCol = variant == 0 ? new Color(0.45f, 0.2f, 0.14f) : new Color(0.17f, 0.18f, 0.2f);
        var px = new Color[size * size];
        float rowH = size / (float)rows, colW = size / (float)cols;
        var shade = new float[rows * (cols + 1)];
        for (int i = 0; i < shade.Length; i++) shade[i] = 0.8f + (float)rng.NextDouble() * 0.35f;

        for (int y = 0; y < size; y++)
        {
            int row = (int)(y / rowH);
            float inRow = (y - row * rowH) / rowH;            // 0 at the bottom edge of the tile, 1 at the top
            float offset = (row % 2) * colW * 0.5f;
            for (int x = 0; x < size; x++)
            {
                float xs = x + offset;
                int col = (int)(xs / colW);
                float inCol = (xs - col * colW) / colW;
                float s = shade[row * (cols + 1) + col % (cols + 1)];
                // Bottom lip of each tile is lit, the top is in the shadow of the row above; dark gaps between
                float light = Mathf.Lerp(1.15f, 0.7f, inRow);
                if (inRow > 0.92f) light *= 0.45f;
                if (inCol < 0.04f || inCol > 0.96f) light *= 0.55f;
                float grain = 0.92f + Mathf.PerlinNoise(x * 0.09f + variant * 10f, y * 0.09f) * 0.16f;
                px[y * size + x] = baseCol * (s * light * grain);
                px[y * size + x].a = 1f;
            }
        }
        var tex = Make(size, size, px, TextureWrapMode.Repeat);
        tex.name = key;
        cache[key] = tex;
        return tex;
    }

    // ---------- Awning ----------

    // Vertical stripes in two colours, with a darker scalloped hem at the bottom
    public static Texture2D AwningStripes(Color a, Color b)
    {
        string key = "Awning_" + ColorUtility.ToHtmlStringRGB(a) + ColorUtility.ToHtmlStringRGB(b);
        if (cache.TryGetValue(key, out var cached) && cached != null) return cached;
        const int w = 128, h = 64;
        var px = new Color[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color c = (x / 16) % 2 == 0 ? a : b;
                float fade = 0.85f + Mathf.PerlinNoise(x * 0.05f, y * 0.07f) * 0.2f;
                if (y < 6) c *= 0.7f;
                px[y * w + x] = c * fade;
                px[y * w + x].a = 1f;
            }
        var tex = Make(w, h, px, TextureWrapMode.Repeat);
        tex.name = key;
        cache[key] = tex;
        return tex;
    }

    // ---------- Decals ----------

    // Dark dirt running down from a window sill or the roof edge: strong at the top, thinning out as it runs
    static Texture2D Streak(System.Random rng)
    {
        const int w = 48, h = 160;
        var px = new Color[w * h];
        float seed = (float)rng.NextDouble() * 100f;
        Color dirt = new Color(0.08f, 0.07f, 0.06f);
        for (int x = 0; x < w; x++)
        {
            float u = x / (float)(w - 1);
            float column = Mathf.PerlinNoise(seed, u * 6f);                         // some drips run further
            float edge = Mathf.SmoothStep(0f, 1f, Mathf.Min(u, 1f - u) * 4f);
            for (int y = 0; y < h; y++)
            {
                float down = 1f - y / (float)(h - 1);                                // 0 at the top, 1 at the bottom
                float length = 0.35f + column * 0.65f;
                float a = Mathf.Clamp01(1f - down / length) * edge;
                a *= 0.55f + Mathf.PerlinNoise(seed + u * 9f, down * 5f) * 0.45f;
                px[y * w + x] = new Color(dirt.r, dirt.g, dirt.b, a * 0.75f);
            }
        }
        return Make(w, h, px, TextureWrapMode.Clamp);
    }

    // A blotchy damp patch with a darker rim (or a pool of blood: colour and strength given)
    static Texture2D Stain(System.Random rng, Color? colour = null, float strength = 0.55f)
    {
        const int size = 128;
        var px = new Color[size * size];
        float seed = (float)rng.NextDouble() * 100f;
        Color col = colour ?? (rng.NextDouble() < 0.5 ? new Color(0.12f, 0.11f, 0.08f) : new Color(0.2f, 0.17f, 0.1f));
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x / (float)size - 0.5f, dy = y / (float)size - 0.5f;
                float r = Mathf.Sqrt(dx * dx + dy * dy) * 2f;
                float n = Fbm(seed, x / (float)size * 3f, y / (float)size * 3f);
                float shape = Mathf.Clamp01((1f - r) * 1.6f + (n - 0.5f) * 1.4f);
                float a = Mathf.SmoothStep(0f, 1f, shape) * strength;
                a += Mathf.Clamp01(1f - Mathf.Abs(shape - 0.25f) * 10f) * 0.2f;   // tide mark
                px[y * size + x] = new Color(col.r, col.g, col.b, Mathf.Clamp01(a));
            }
        return Make(size, size, px, TextureWrapMode.Clamp);
    }

    // Damp dirt and moss along the bottom of a wall, with a ragged top edge
    static Texture2D Moss(System.Random rng)
    {
        const int w = 256, h = 64;
        var px = new Color[w * h];
        float seed = (float)rng.NextDouble() * 100f;
        for (int x = 0; x < w; x++)
        {
            float top = 0.45f + Mathf.PerlinNoise(seed, x * 0.035f) * 0.5f;
            for (int y = 0; y < h; y++)
            {
                float v = y / (float)(h - 1);
                float a = Mathf.Clamp01((top - v) / 0.25f);
                float n = Fbm(seed + 5f, x / (float)w * 8f, v * 2f);
                Color c = Color.Lerp(new Color(0.07f, 0.08f, 0.05f), new Color(0.13f, 0.17f, 0.07f), n);
                px[y * w + x] = new Color(c.r, c.g, c.b, a * (0.5f + n * 0.45f));
            }
        }
        return Make(w, h, px, TextureWrapMode.Clamp);
    }

    // Four deep gouges raked down a wall: dark grooves with pale torn edges, deepest in the middle of each stroke
    static Texture2D Claws(System.Random rng)
    {
        const int w = 128, h = 160;
        var px = new Color[w * h];
        float seed = (float)rng.NextDouble() * 100f;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float v = y / (float)h, u = x / (float)w;
                float a = 0f; Color c = Color.black;
                for (int k = 0; k < 4; k++)
                {
                    // Each stroke slants and curves a little, starting and ending at slightly different heights
                    float start = 0.08f + k * 0.03f, end = 0.92f - (3 - k) * 0.04f;
                    if (v < start || v > end) continue;
                    float t = (v - start) / (end - start);
                    float centre = 0.18f + k * 0.2f + (t - 0.5f) * 0.18f + Mathf.Sin(t * 3f + k) * 0.02f;
                    float width = 0.035f * Mathf.Sin(t * Mathf.PI) + 0.006f;
                    float d = Mathf.Abs(u - centre);
                    if (d < width) { a = Mathf.Max(a, 0.95f); c = new Color(0.05f, 0.04f, 0.035f); }
                    else if (d < width + 0.02f && a < 0.5f) { a = 0.45f; c = new Color(0.72f, 0.68f, 0.6f); }   // torn edge
                }
                a *= 0.75f + Mathf.PerlinNoise(seed + u * 20f, v * 20f) * 0.25f;
                px[y * w + x] = new Color(c.r, c.g, c.b, a);
            }
        return Make(w, h, px, TextureWrapMode.Clamp);
    }

    // A huge grimy hand smeared on a wall: a palm, four long fingers and a thumb, dragged downward
    static Texture2D Handprint(System.Random rng)
    {
        const int size = 192;
        var px = new Color[size * size];
        float seed = (float)rng.NextDouble() * 100f;
        Vector2 palm = new Vector2(0.5f, 0.38f);
        Vector2[] tips = { new Vector2(0.22f, 0.88f), new Vector2(0.4f, 0.95f), new Vector2(0.58f, 0.94f), new Vector2(0.75f, 0.85f), new Vector2(0.88f, 0.45f) };
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Vector2 p = new Vector2(x, y) / size;
                Vector2 q = new Vector2((p.x - palm.x) / 0.24f, (p.y - palm.y) / 0.22f);
                float shape = 1f - q.magnitude;                                   // palm
                foreach (var tip in tips)                                        // fingers: capsules from the palm
                {
                    Vector2 a = Vector2.Lerp(palm, tip, 0.35f), ab = tip - a;
                    float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                    float d = Vector2.Distance(p, a + ab * t);
                    shape = Mathf.Max(shape, 1f - d / 0.06f);
                }
                // Smeared downward: below the palm, streaks fade out
                float smear = p.y < palm.y ? Mathf.PerlinNoise(seed + p.x * 25f, 0f) * Mathf.Clamp01(1f - (palm.y - p.y) * 3f) : 0f;
                float a2 = Mathf.Clamp01(Mathf.Max(shape * 3f, smear * 0.8f * (Mathf.Abs(p.x - 0.5f) < 0.22f ? 1f : 0f)));
                a2 *= 0.55f + Mathf.PerlinNoise(seed + p.x * 12f, p.y * 12f) * 0.4f;
                px[y * size + x] = new Color(0.07f, 0.06f, 0.05f, a2 * 0.85f);
            }
        return Make(size, size, px, TextureWrapMode.Clamp);
    }

    // A faded paper poster: a coloured picture block, a title bar, lines of "text", torn corners, peeling
    static Texture2D Poster(System.Random rng, int variant)
    {
        const int w = 112, h = 160;
        var px = new Color[w * h];
        Color[] inks = { new Color(0.7f, 0.15f, 0.12f), new Color(0.12f, 0.3f, 0.6f), new Color(0.85f, 0.65f, 0.1f),
                         new Color(0.15f, 0.45f, 0.25f), new Color(0.1f, 0.1f, 0.1f), new Color(0.55f, 0.2f, 0.5f) };
        Color paper = Color.Lerp(new Color(0.85f, 0.82f, 0.7f), new Color(0.75f, 0.7f, 0.55f), (float)rng.NextDouble());
        Color ink = inks[variant % inks.Length];
        Color ink2 = inks[(variant + 2) % inks.Length];
        int picTop = (int)(h * 0.45f), titleY = (int)(h * 0.78f);
        float tornA = (float)rng.NextDouble(), tornB = (float)rng.NextDouble();
        float seed = (float)rng.NextDouble() * 100f;

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = x / (float)w, v = y / (float)h;
                Color c = paper;
                if (v > 0.12f && v < 0.45f && u > 0.1f && u < 0.9f)                   // the picture
                    c = Color.Lerp(ink, ink2, Mathf.PerlinNoise(u * 3f + seed, v * 3f));
                if (Mathf.Abs(y - titleY) < h * 0.06f && u > 0.08f && u < 0.92f) c = ink; // title bar
                if (v > 0.5f && v < 0.7f && u > 0.12f && u < 0.88f)                   // lines of text
                {
                    int line = (int)((v - 0.5f) / 0.04f);
                    float lineEnd = 0.55f + Mathf.PerlinNoise(line * 3.1f, seed) * 0.33f;
                    if ((y % 6) < 3 && u < lineEnd) c = Color.Lerp(paper, Color.black, 0.65f);
                }
                if (v < 0.08f && u > 0.25f && u < 0.75f && (y % 5) < 2) c = Color.Lerp(paper, Color.black, 0.5f);
                // Faded by the weather, dirtier toward the bottom
                float fade = Mathf.PerlinNoise(u * 4f + seed, v * 4f);
                c = Color.Lerp(c, paper, 0.25f + fade * 0.25f);
                c *= 0.85f + v * 0.15f;

                float a = 1f;
                if (u + (1f - v) < 0.12f + tornA * 0.18f) a = 0f;                     // torn corners
                if ((1f - u) + v < 0.1f + tornB * 0.2f) a = 0f;
                float edge = Mathf.Min(Mathf.Min(u, 1f - u), Mathf.Min(v, 1f - v));
                if (edge < 0.03f && Mathf.PerlinNoise(x * 0.5f, y * 0.5f) < 0.45f) a = 0f; // ragged edge
                px[y * w + x] = new Color(c.r, c.g, c.b, a);
            }
        return Make(w, h, px, TextureWrapMode.Clamp);
    }

    // A spray-painted tag: a few thick looping strokes in a bright colour with a dark outline, and drips
    static Texture2D Graffiti(System.Random rng, int variant)
    {
        const int w = 256, h = 128;
        var col = new Color[w * h];
        var alpha = new float[w * h];
        Color[] paints = { new Color(0.9f, 0.2f, 0.5f), new Color(0.2f, 0.6f, 0.95f), new Color(0.95f, 0.85f, 0.15f),
                           new Color(0.3f, 0.85f, 0.35f), new Color(0.95f, 0.45f, 0.1f), new Color(0.85f, 0.85f, 0.85f) };
        Color paint = paints[variant % paints.Length];
        Color outline = new Color(0.05f, 0.05f, 0.08f);
        int letters = 3 + rng.Next(3);

        // Each "letter" is a looping stroke; draw the outline first (wider), then the paint
        for (int pass = 0; pass < 2; pass++)
        {
            var r = new System.Random(variant * 31 + 7);
            for (int l = 0; l < letters; l++)
            {
                float cx = (l + 0.5f) / letters * w, cy = h * (0.45f + ((float)r.NextDouble() - 0.5f) * 0.2f);
                float sx = w / (float)letters * 0.42f, sy = h * 0.3f;
                float f1 = 1f + r.Next(3), f2 = 1f + r.Next(3), ph = (float)r.NextDouble() * 6.28f;
                float thick = (pass == 0 ? 9f : 6f) * (0.8f + (float)r.NextDouble() * 0.4f);
                for (float t = 0f; t < 6.28f; t += 0.02f)
                {
                    float x = cx + Mathf.Sin(t * f1 + ph) * sx;
                    float y = cy + Mathf.Cos(t * f2) * sy + Mathf.Sin(t * 3f) * sy * 0.2f;
                    Dot(col, alpha, w, h, x, y, thick, pass == 0 ? outline : paint);
                }
                // A drip or two running down
                if (pass == 1 && r.NextDouble() < 0.6)
                {
                    float dx = cx + ((float)r.NextDouble() - 0.5f) * sx;
                    float len = h * (0.1f + (float)r.NextDouble() * 0.25f);
                    for (float d = 0f; d < len; d += 1f) Dot(col, alpha, w, h, dx, cy - sy * 0.5f - d, 1.6f, paint);
                }
            }
        }
        var px = new Color[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            float wear = 0.75f + Mathf.PerlinNoise((i % w) * 0.08f, (i / w) * 0.08f) * 0.25f;
            px[i] = new Color(col[i].r, col[i].g, col[i].b, alpha[i] * wear);
        }
        return Make(w, h, px, TextureWrapMode.Clamp);
    }

    static void Dot(Color[] col, float[] alpha, int w, int h, float cx, float cy, float radius, Color c)
    {
        int r = Mathf.CeilToInt(radius);
        for (int y = (int)cy - r; y <= (int)cy + r; y++)
            for (int x = (int)cx - r; x <= (int)cx + r; x++)
            {
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                float a = Mathf.Clamp01(radius - d);   // soft spray edge
                if (a <= 0f) continue;
                int i = y * w + x;
                col[i] = Color.Lerp(col[i], c, a);
                alpha[i] = Mathf.Max(alpha[i], a);
            }
    }

    static float Fbm(float seed, float x, float y) =>
        Mathf.PerlinNoise(seed + x, y) * 0.55f + Mathf.PerlinNoise(seed + x * 2.1f, y * 2.1f) * 0.3f +
        Mathf.PerlinNoise(seed + x * 4.3f, y * 4.3f) * 0.15f;

    static Texture2D Make(int w, int h, Color[] px, TextureWrapMode wrap)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { wrapMode = wrap, filterMode = FilterMode.Bilinear };
        tex.SetPixels(px);
        tex.Apply(true, true);
        return tex;
    }
}
