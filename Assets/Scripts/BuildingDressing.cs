using System.Collections.Generic;
using UnityEngine;

// Variety on the outside of a building, added after BuildingBuilder has put up the walls, windows and roof:
//  - every building: a slight tint on its outside walls, and decals (grime running down from window sills and the
//    roof edge, damp stains, moss along the bottom, posters, graffiti), kept off windows and the door
//  - shops: a striped awning over the front, roll-down shutters over some windows (never the door)
//  - apartment blocks (Eastern European panel housing): panel seams at every storey, balconies, a canopy over the
//    entrance, satellite dishes, a stairwell housing on the roof
// Nothing here has a collider except the stairwell housing, so it never changes the NavMesh, sight lines or the
// giant's reach. Decal pictures come from ProcTextures; the see-through material is BuildingPalette.decalMaterial.
public static class BuildingDressing
{
    // One outside wall: centre at the foot of the wall, outward normal, direction along it, its length
    struct Face
    {
        public Vector3 center, normal, along;
        public float length;
        public List<Rect> taken;   // (along, up) rectangles already used: windows, door, other decals
        public Rect door;          // the doorway on this face (empty if none): nothing goes over it (the door swings)
    }

    // Lowest a decal hanging down at 'u' (half width 'half') may reach without covering the doorway
    static float ClearAboveDoor(Face f, float u, float half) =>
        f.door.width > 0f && u + half > f.door.xMin && u - half < f.door.xMax ? f.door.yMax + 0.1f : 0.1f;

    static readonly Dictionary<Texture, Material> decalMats = new Dictionary<Texture, Material>();
    static readonly Dictionary<(Material, int), Material> tintedMats = new Dictionary<(Material, int), Material>();
    static readonly Dictionary<string, Material> plainMats = new Dictionary<string, Material>();

    public static void Dress(Transform building, BuildingBuilder.Spec s, BuildingPalette pal, System.Random rng, Material wall)
    {
        var dressing = new GameObject("Dressing").transform;
        dressing.SetParent(building, false);
        var faces = Faces(building, s);

        Tint(building, s, rng);

        if (s.kind == BuildingKind.Apartment) Apartment(dressing, building, s, faces, pal, rng, wall);
        if (s.kind == BuildingKind.Shop) Shop(dressing, building, s, faces, pal, rng);
        if (s.decals && pal != null && pal.decalMaterial != null) Decals(dressing, building, s, faces, pal, rng);
    }

    // ---------- Faces ----------

    static Face[] Faces(Transform building, BuildingBuilder.Spec s)
    {
        float hw = s.width / 2f, hd = s.depth / 2f;
        var faces = new[]
        {
            new Face { center = new Vector3(0, 0, -hd), normal = Vector3.back,    along = Vector3.right,   length = s.width },
            new Face { center = new Vector3(0, 0,  hd), normal = Vector3.forward, along = Vector3.left,    length = s.width },
            new Face { center = new Vector3(-hw, 0, 0), normal = Vector3.left,    along = Vector3.back,    length = s.depth },
            new Face { center = new Vector3( hw, 0, 0), normal = Vector3.right,   along = Vector3.forward, length = s.depth },
        };
        for (int i = 0; i < faces.Length; i++) faces[i].taken = new List<Rect>();

        // The door
        faces[0].door = new Rect(s.doorOffset - s.doorWidth / 2f - 0.3f, 0f, s.doorWidth + 0.6f, s.doorHeight + 0.4f);
        faces[0].taken.Add(faces[0].door);

        // Every window frame, on whichever face it's on
        var details = building.Find("Details");
        if (details != null)
            foreach (Transform w in details)
            {
                if (w.name != "Window_Frame") continue;
                Vector3 n = w.localRotation * Vector3.forward;
                for (int i = 0; i < faces.Length; i++)
                {
                    if (Vector3.Dot(n, faces[i].normal) < 0.9f) continue;
                    float u = Vector3.Dot(w.localPosition - faces[i].center, faces[i].along);
                    faces[i].taken.Add(new Rect(u - w.localScale.x / 2f, w.localPosition.y - w.localScale.y / 2f, w.localScale.x, w.localScale.y));
                }
            }
        return faces;
    }

    static bool Free(Face f, Rect r, float margin)
    {
        if (r.xMin < -f.length / 2f + 0.2f || r.xMax > f.length / 2f - 0.2f || r.yMin < 0f) return false;
        var grown = new Rect(r.x - margin, r.y - margin, r.width + margin * 2f, r.height + margin * 2f);
        foreach (var t in f.taken) if (t.Overlaps(grown)) return false;
        return true;
    }

    // ---------- Tint ----------

    // Each building's outside walls a little warmer, cooler, dirtier or paler than the next
    static void Tint(Transform building, BuildingBuilder.Spec s, System.Random rng)
    {
        Color[] tints =
        {
            new Color(1f, 0.93f, 0.82f), new Color(0.85f, 0.9f, 1f), new Color(0.95f, 1f, 0.88f),
            new Color(1f, 0.86f, 0.84f), new Color(0.8f, 0.78f, 0.74f), Color.white,
        };
        float amount = s.kind == BuildingKind.Apartment ? Range(rng, 0.3f, 0.7f) : Range(rng, 0f, 0.45f);
        int pick = rng.Next(tints.Length);
        // A few strengths only, so all the buildings share a handful of tinted copies of each wall material: a
        // per-building colour override would stop Unity batching the walls (that cost ~1500 extra draw calls)
        int level = Mathf.Clamp(Mathf.RoundToInt(amount * 4f), 0, 3);
        if (level == 0 || tints[pick] == Color.white) return;
        Color tint = Color.Lerp(Color.white, tints[pick], level / 4f);

        foreach (var r in building.GetComponentsInChildren<MeshRenderer>())
        {
            if (!r.name.StartsWith("Wall_") && r.name != "Chimney") continue;
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) continue;
                var key = (mats[i], pick * 10 + level);
                if (!tintedMats.TryGetValue(key, out var m) || m == null)
                {
                    m = new Material(mats[i]) { name = mats[i].name + "_Tint" + key.Item2 };
                    m.SetColor("_BaseColor", mats[i].GetColor("_BaseColor") * tint);
                    tintedMats[key] = m;
                }
                mats[i] = m;
            }
            r.sharedMaterials = mats;
        }
    }

    // ---------- Decals ----------

    static void Decals(Transform parent, Transform building, BuildingBuilder.Spec s, Face[] faces, BuildingPalette pal, System.Random rng)
    {
        float depthStep = 0f;   // each decal a hair further out, so overlapping ones don't flicker

        // Grime running down from some window sills, and from the roof edge
        var details = building.Find("Details");
        if (details != null)
            foreach (Transform w in details)
            {
                if (w.name != "Window_Frame" || rng.NextDouble() > 0.35) continue;
                Vector3 n = w.localRotation * Vector3.forward;
                float len = Range(rng, 0.8f, 1.8f);
                float width = w.localScale.x * Range(rng, 0.7f, 1f);
                Vector3 top = w.localPosition - Vector3.up * (w.localScale.y / 2f);
                float lowest = 0.1f;
                if (Vector3.Dot(n, faces[0].normal) > 0.9f)
                    lowest = ClearAboveDoor(faces[0], Vector3.Dot(w.localPosition - faces[0].center, faces[0].along), width / 2f);
                if (top.y - len < lowest) len = top.y - lowest;
                if (len < 0.3f) continue;
                Quad(parent, pal, ProcTextures.Decal.Streak, rng, top - Vector3.up * len / 2f + n * (0.03f + Next(ref depthStep)), n, new Vector2(width, len));
            }
        foreach (var f in faces)
        {
            int streaks = rng.Next(0, 3);
            for (int i = 0; i < streaks; i++)
            {
                float len = Range(rng, 1.2f, Mathf.Min(3f, s.height * 0.6f));
                float u = Range(rng, -f.length / 2f + 0.6f, f.length / 2f - 0.6f);
                len = Mathf.Min(len, s.height - ClearAboveDoor(f, u, 0.7f));
                if (len < 0.4f) continue;
                Vector3 p = f.center + f.along * u + Vector3.up * (s.height - len / 2f);
                Quad(parent, pal, ProcTextures.Decal.Streak, rng, p + f.normal * (0.015f + Next(ref depthStep)), f.normal, new Vector2(Range(rng, 0.6f, 1.4f), len));
            }
        }

        // Moss and damp along the bottom of some walls
        foreach (var f in faces)
        {
            if (rng.NextDouble() > 0.45) continue;
            float h = Range(rng, 0.45f, 0.9f), w = Mathf.Min(h * 4f, f.length);
            for (float u = -f.length / 2f + w / 2f; u < f.length / 2f + w / 2f - 0.5f; u += w * 0.95f)
            {
                float uu = Mathf.Min(u, f.length / 2f - w / 2f);
                if (f.door.width > 0f && uu + w / 2f > f.door.xMin && uu - w / 2f < f.door.xMax) continue;   // the doorway
                Quad(parent, pal, ProcTextures.Decal.Moss, rng, f.center + f.along * uu + Vector3.up * (h / 2f) + f.normal * (0.02f + Next(ref depthStep)), f.normal, new Vector2(w, h));
            }
        }

        // Damp stains
        int stains = rng.Next(0, 4);
        for (int i = 0; i < stains; i++)
        {
            var f = faces[rng.Next(faces.Length)];
            float size = Range(rng, 0.8f, 2.2f);
            Place(parent, pal, ProcTextures.Decal.Stain, rng, f, new Vector2(size, size), 0.3f, s.height - 0.3f, 0.05f, ref depthStep, true);
        }

        // Posters near the street (front), more on shops; graffiti low down, mostly on the sides and back
        int posters = s.kind == BuildingKind.Shop ? rng.Next(1, 5) : rng.Next(0, 3);
        for (int i = 0; i < posters; i++)
        {
            var f = i == 0 || rng.NextDouble() < 0.6 ? faces[0] : faces[rng.Next(faces.Length)];
            float h = Range(rng, 0.7f, 0.95f);
            Place(parent, pal, ProcTextures.Decal.Poster, rng, f, new Vector2(h * 0.7f, h), 1.1f, 2.4f, 0.05f, ref depthStep, false);
        }
        if (rng.NextDouble() < 0.45)
        {
            int tags = rng.Next(1, 3);
            for (int i = 0; i < tags; i++)
            {
                var f = faces[rng.NextDouble() < 0.3 ? 0 : 1 + rng.Next(3)];
                float h = Range(rng, 0.8f, 1.5f);
                Place(parent, pal, ProcTextures.Decal.Graffiti, rng, f, new Vector2(h * 2f, h), 0.3f, 2.6f, 0.1f, ref depthStep, false);
            }
        }
    }

    static float Next(ref float step) { step += 0.0015f; return step; }

    // Somewhere free on this face between yMin and yMax (the decal's bottom edge); ignore = may cover windows
    static void Place(Transform parent, BuildingPalette pal, ProcTextures.Decal kind, System.Random rng, Face f, Vector2 size,
                      float yMin, float yMax, float margin, ref float depthStep, bool ignoreTaken)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            float u = Range(rng, -f.length / 2f + size.x / 2f, f.length / 2f - size.x / 2f);
            float y = Range(rng, yMin, Mathf.Max(yMin, yMax - size.y));
            var r = new Rect(u - size.x / 2f, y, size.x, size.y);
            if (!ignoreTaken && !Free(f, r, margin)) continue;
            if (f.door.width > 0f && f.door.Overlaps(r)) continue;   // never over the doorway (the door swings)
            if (!ignoreTaken) f.taken.Add(r);
            Vector3 p = f.center + f.along * u + Vector3.up * (y + size.y / 2f);
            Quad(parent, pal, kind, rng, p + f.normal * (0.02f + Next(ref depthStep)), f.normal, size);
            return;
        }
    }

    // A flat picture on the wall facing 'normal' (a Quad shows its face toward -Z)
    static void Quad(Transform parent, BuildingPalette pal, ProcTextures.Decal kind, System.Random rng, Vector3 pos, Vector3 normal, Vector2 size)
    {
        var tex = ProcTextures.DecalTexture(kind, rng.Next(ProcTextures.Variants(kind)));
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        q.name = "Decal_" + kind;
        Object.DestroyImmediate(q.GetComponent<Collider>());
        q.transform.SetParent(parent, false);
        q.transform.localPosition = pos;
        q.transform.localRotation = Quaternion.LookRotation(-normal);
        q.transform.localScale = new Vector3(size.x, size.y, 1f);
        var r = q.GetComponent<MeshRenderer>();
        r.sharedMaterial = DecalMaterial(pal, tex);
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        q.isStatic = true;
    }

    public static Material DecalMaterial(BuildingPalette pal, Texture tex)
    {
        if (decalMats.TryGetValue(tex, out var m) && m != null) return m;
        m = new Material(pal.decalMaterial) { name = tex.name };
        m.SetTexture("_BaseMap", tex);
        decalMats[tex] = m;
        return m;
    }

    // ---------- Shops ----------

    static void Shop(Transform parent, Transform building, BuildingBuilder.Spec s, Face[] faces, BuildingPalette pal, System.Random rng)
    {
        float hd = s.depth / 2f;
        var metal = pal != null && pal.propMetal != null ? pal.propMetal : Plain("Shutter", new Color(0.4f, 0.41f, 0.42f), 0.4f);

        // Awning along the front, tilted down toward the street
        if (rng.NextDouble() < 0.6)
        {
            Color[] colours = { new Color(0.55f, 0.1f, 0.1f), new Color(0.1f, 0.3f, 0.2f), new Color(0.12f, 0.2f, 0.45f), new Color(0.5f, 0.35f, 0.1f) };
            var awningMat = Plain("Awning_" + rng.Next(colours.Length), Color.white, 0.1f, colours);
            float w = s.width - 0.8f, depth = Range(rng, 1.1f, 1.5f), y = Mathf.Min(s.height - 0.6f, 3.1f);
            var a = Part(parent, "Awning", PrimitiveType.Cube, new Vector3(0f, y - depth * 0.15f, -hd - depth / 2f * 0.95f), new Vector3(w, 0.05f, depth), awningMat);
            a.localRotation = Quaternion.Euler(-18f, 0f, 0f);
            Part(parent, "Awning_Hem", PrimitiveType.Cube, new Vector3(0f, y - depth * 0.33f - 0.12f, -hd - depth * 0.93f), new Vector3(w, 0.25f, 0.03f), awningMat);
        }

        // Roll-down shutters over some of the shop windows: some right down, some half up (the door stays clear)
        var details = building.Find("Details");
        if (details == null) return;
        foreach (Transform w in details)
        {
            if (w.name != "Window_Frame" || w.localPosition.y > 2.6f || w.localPosition.z > -hd + 0.2f) continue;
            if (rng.NextDouble() > 0.4) continue;
            float wide = w.localScale.x, tall = w.localScale.y;
            float down = rng.NextDouble() < 0.5 ? 1f : Range(rng, 0.3f, 0.7f);
            float top = w.localPosition.y + tall / 2f;
            float h = tall * down;
            if (down >= 1f)   // fully down: the Climber can't get in this way
                foreach (var bw in details.GetComponentsInChildren<BreakableWindow>())
                    if ((bw.transform.localPosition - w.localPosition).sqrMagnitude < 0.05f) bw.shuttered = true;
            Part(parent, "Shutter_Box", PrimitiveType.Cube, new Vector3(w.localPosition.x, top + 0.12f, -hd - 0.14f), new Vector3(wide + 0.2f, 0.25f, 0.25f), metal);
            Part(parent, "Shutter", PrimitiveType.Cube, new Vector3(w.localPosition.x, top - h / 2f, -hd - 0.09f), new Vector3(wide, h, 0.03f), metal);
            // Slats: thin dark lines every 12 cm
            var groove = Plain("Shutter_Groove", new Color(0.12f, 0.12f, 0.13f), 0.2f);
            for (float g = top - 0.12f; g > top - h + 0.05f; g -= 0.12f)
                Part(parent, "Shutter_Slat", PrimitiveType.Cube, new Vector3(w.localPosition.x, g, -hd - 0.11f), new Vector3(wide, 0.012f, 0.01f), groove);
        }
    }

    // ---------- Apartment blocks ----------

    static void Apartment(Transform parent, Transform building, BuildingBuilder.Spec s, Face[] faces, BuildingPalette pal,
                          System.Random rng, Material wall)
    {
        const float storey = 3.2f;
        float hw = s.width / 2f, hd = s.depth / 2f;
        var concrete = Plain("Panel_Concrete", new Color(0.55f, 0.54f, 0.5f), 0.1f);
        var seam = Plain("Panel_Seam", new Color(0.18f, 0.18f, 0.18f), 0.1f);
        var metal = pal != null && pal.propMetal != null ? pal.propMetal : Plain("Apt_Metal", new Color(0.35f, 0.36f, 0.37f), 0.35f);

        // Seams between the prefab panels, at every floor
        for (float y = storey; y < s.height - 0.5f; y += storey)
            foreach (var f in faces)
            {
                var strip = Part(parent, "Panel_Seam", PrimitiveType.Cube, f.center + Vector3.up * y + f.normal * 0.01f,
                                 Vector3.one, seam);
                strip.localScale = Abs(f.along * f.length) + Vector3.up * 0.07f + Abs(f.normal) * 0.03f;
            }

        // Canopy over the entrance
        Part(parent, "Entrance_Canopy", PrimitiveType.Cube, new Vector3(s.doorOffset, s.doorHeight + 0.45f, -hd - 0.6f),
             new Vector3(s.doorWidth + 1.4f, 0.14f, 1.2f), concrete);

        // Balconies under some upper-floor windows on the front and back: whole columns, like real blocks
        var details = building.Find("Details");
        if (details != null)
        {
            var columns = new Dictionary<int, bool>();
            foreach (Transform w in details)
            {
                if (w.name != "Window_Frame" || w.localPosition.y < storey) continue;
                Vector3 n = w.localRotation * Vector3.forward;
                if (Mathf.Abs(n.z) < 0.9f) continue;   // front and back only
                int key = Mathf.RoundToInt(w.localPosition.x * 10f) * (n.z < 0 ? 1 : -1);
                if (!columns.TryGetValue(key, out bool has)) columns[key] = has = rng.NextDouble() < 0.45;
                if (!has) continue;

                float floorY = w.localPosition.y - w.localScale.y / 2f - 0.8f;
                float face = n.z < 0 ? -hd : hd;
                float outZ = face + n.z * 0.6f;
                Part(parent, "Balcony_Slab", PrimitiveType.Cube, new Vector3(w.localPosition.x, floorY, outZ), new Vector3(2f, 0.15f, 1.2f), concrete);
                Part(parent, "Balcony_Front", PrimitiveType.Cube, new Vector3(w.localPosition.x, floorY + 0.5f, face + n.z * 1.15f), new Vector3(2f, 0.9f, 0.08f), concrete);
                foreach (float side in new[] { -1f, 1f })
                    Part(parent, "Balcony_Side", PrimitiveType.Cube, new Vector3(w.localPosition.x + side * 0.97f, floorY + 0.5f, outZ), new Vector3(0.06f, 0.9f, 1.2f), concrete);
                if (rng.NextDouble() < 0.25)   // a satellite dish on the balcony rail
                {
                    var dish = Part(parent, "Satellite_Dish", PrimitiveType.Cylinder, new Vector3(w.localPosition.x + 0.6f, floorY + 1.2f, face + n.z * 1.1f),
                                    new Vector3(0.6f, 0.03f, 0.6f), metal);
                    dish.localRotation = Quaternion.FromToRotation(Vector3.up, (n + Vector3.up * 0.6f).normalized);   // tilted up and out
                }
            }
        }

        // Stairwell housing and antennas on the roof, in Details with the other rooftop props (the roof hatch keeps
        // clear of anything named Prop_ there). The housing has a collider: the Climber walks round it.
        var roofProps = building.Find("Details") != null ? building.Find("Details") : parent;
        float roofTop = s.height + s.wallThickness;
        float sx = Range(rng, -hw + 2.5f, hw - 2.5f);
        var housing = Part(roofProps, "Prop_Stairwell", PrimitiveType.Cube, new Vector3(sx, roofTop + 1.2f, hd - 2f), new Vector3(2.6f, 2.4f, 2.4f), wall);
        housing.gameObject.AddComponent<BoxCollider>();
        for (int i = rng.Next(2, 5); i > 0; i--)
            Part(roofProps, "Prop_Antenna", PrimitiveType.Cylinder,
                 new Vector3(Range(rng, -hw + 1f, hw - 1f), roofTop + 1.6f, Range(rng, -hd + 1f, hd - 1f)), new Vector3(0.05f, 1.6f, 0.05f), metal);
    }

    // ---------- Parts ----------

    static Transform Part(Transform parent, string name, PrimitiveType type, Vector3 pos, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        if (mat != null) go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        go.isStatic = true;
        return go.transform;
    }

    // Plain lit material, made once. stripes = make it a striped awning in that colour instead.
    static Material Plain(string name, Color color, float smoothness, Color[] stripes = null)
    {
        if (plainMats.TryGetValue(name, out var m) && m != null) return m;
        m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
        m.SetColor("_BaseColor", color);
        m.SetFloat("_Smoothness", smoothness);
        if (stripes != null)
        {
            int i = int.Parse(name.Substring(name.LastIndexOf('_') + 1));
            m.SetTexture("_BaseMap", ProcTextures.AwningStripes(stripes[i], new Color(0.85f, 0.82f, 0.75f)));
            m.SetTextureScale("_BaseMap", new Vector2(4f, 1f));
        }
        plainMats[name] = m;
        return m;
    }

    static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

    static float Range(System.Random rng, float min, float max) => min + (float)rng.NextDouble() * (max - min);
}
