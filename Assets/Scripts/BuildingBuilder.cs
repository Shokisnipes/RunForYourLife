using System.Collections.Generic;
using UnityEngine;

public enum BuildingKind { House, Shop, Warehouse }

// Builds an enterable box building out of plain cubes with BoxColliders: walls, roof, doorway, optional back room,
// windows, roof edge, rooftop clutter, sidewalk, a roof hatch (the Climber drops in through it) and a ReachPoint
// (the giant crouches there and reaches in). Runs in the game (the city generator) and in the editor (Building
// Generator window). Everything random comes from the System.Random you pass in, so the same seed builds the same
// building on every machine.
//
// The rest of the game finds buildings by their part names (Wall_Front_Top, Roof, Roof_*, Interior_*, Floor),
// so keep those names when changing things here.
public static class BuildingBuilder
{
    // Default doorway between the rooms: wide enough for the Climber (NavMesh radius 0.5) to crawl through
    public const float InteriorGap = 1.8f;
    // How far outside the door the giant stands
    public const float GiantStandDistance = 3.5f;

    [System.Serializable]
    public struct DetailOptions
    {
        public bool windows, parapet, roofClutter, sidewalk;
        [Tooltip("Shop: one big window each side of the door instead of small ground-floor windows.")]
        public bool shopfront;
        [Tooltip("Warehouse: a single row of windows high up under the roof.")]
        public bool highWindows;
        public float litChance, flickerChance, sidewalkWidth;

        public static DetailOptions Default => new DetailOptions
        {
            windows = true, parapet = true, roofClutter = true, sidewalk = true,
            litChance = 0.2f, flickerChance = 0.08f, sidewalkWidth = 2f,
        };
    }

    // Everything about one building. Front (the door) is the -Z side.
    [System.Serializable]
    public struct Spec
    {
        public BuildingKind kind;
        public float width, depth, height, wallThickness;   // X, Z, Y in meters; one storey ~ 3-4 m
        public float doorWidth;    // wide enough for the giant's forearm to reach through
        public float doorHeight;   // low enough that the Climber can't walk in from the street
        public float doorOffset;   // slides the door left/right along the front wall
        public bool interiorWall;  // a back room to hide in
        public float interiorGap;
        public bool roofHatch, reverb;
        public DetailOptions details;

        public static Spec Default => new Spec
        {
            kind = BuildingKind.House, width = 8f, depth = 10f, height = 4f, wallThickness = 0.25f,
            doorWidth = 2.2f, doorHeight = 2.3f, doorOffset = 0f, interiorWall = false, interiorGap = InteriorGap,
            roofHatch = true, reverb = true, details = DetailOptions.Default,
        };
    }

    // A random building of this kind that fits in maxWidth x maxDepth (the lot, sidewalk not included)
    public static Spec RandomSpec(BuildingKind kind, System.Random rng, float maxWidth = 99f, float maxDepth = 99f)
    {
        var s = Spec.Default;
        s.kind = kind;
        switch (kind)
        {
            case BuildingKind.House:
                s.width = Range(rng, 7f, 9f);
                s.depth = Range(rng, 8f, 10.5f);
                s.height = Range(rng, 3.8f, 4.3f);
                s.interiorWall = rng.NextDouble() < 0.75;
                s.details.litChance = 0.25f;
                break;
            case BuildingKind.Shop:
                s.width = Range(rng, 10f, 14f);
                s.depth = Range(rng, 9f, 12f);
                s.height = Range(rng, 4.5f, 5.5f);
                s.interiorWall = true;              // stock room at the back
                s.details.shopfront = true;
                s.details.litChance = 0.3f;
                break;
            case BuildingKind.Warehouse:
                s.width = Range(rng, 14f, 18f);
                s.depth = Range(rng, 14f, 19f);
                s.height = Range(rng, 5.5f, 6.5f);
                s.interiorWall = rng.NextDouble() < 0.5;
                s.details.highWindows = true;
                s.details.litChance = 0.08f;
                break;
        }
        s.width = Mathf.Min(s.width, maxWidth);
        s.depth = Mathf.Min(s.depth, maxDepth);
        float maxOffset = Mathf.Max(0f, (s.width - s.doorWidth) / 2f - s.wallThickness - 0.6f);
        s.doorOffset = Range(rng, -maxOffset, maxOffset) * 0.7f;
        return s;
    }

    // Builds the building with its front door facing 'rotation * back'. wallOverride = force this outside wall material.
    public static GameObject Build(Spec s, Vector3 position, Quaternion rotation, Transform parent,
                                   BuildingPalette pal, System.Random rng, Material wallOverride = null)
    {
        // Clamp so the door always fits
        s.doorWidth = Mathf.Min(s.doorWidth, s.width - s.wallThickness * 2f - 0.2f);
        s.doorHeight = Mathf.Min(s.doorHeight, s.height - 0.2f);
        float maxOffset = (s.width - s.doorWidth) / 2f - s.wallThickness;
        s.doorOffset = Mathf.Clamp(s.doorOffset, -maxOffset, maxOffset);

        var root = new GameObject("Building");
        root.transform.SetParent(parent, false);
        root.transform.SetPositionAndRotation(position, rotation);

        Material wall = wallOverride != null ? wallOverride : PickWall(pal, rng);
        Material inner = pal != null && pal.interiorWallMaterial != null ? pal.interiorWallMaterial : wall;
        Material roof = pal != null ? pal.roofMaterial : null;
        Material floor = pal != null ? pal.floorMaterial : null;
        float tile = pal != null ? pal.metersPerTile : 2.5f;

        float t = s.wallThickness, height = s.height;
        float halfW = s.width / 2f, halfD = s.depth / 2f;
        var b = root.transform;

        // Back wall and side walls
        Box(b, "Wall_Back",  new Vector3(0, height / 2f, halfD - t / 2f), new Vector3(s.width, height, t), wall, tile);
        Box(b, "Wall_Left",  new Vector3(-halfW + t / 2f, height / 2f, 0), new Vector3(t, height, s.depth - t * 2f), wall, tile);
        Box(b, "Wall_Right", new Vector3(halfW - t / 2f, height / 2f, 0), new Vector3(t, height, s.depth - t * 2f), wall, tile);

        // Front wall split around the doorway
        float frontZ = -halfD + t / 2f;
        float doorLeft = s.doorOffset - s.doorWidth / 2f;
        float doorRight = s.doorOffset + s.doorWidth / 2f;

        float leftLen = doorLeft - (-halfW);
        if (leftLen > 0.01f)
            Box(b, "Wall_Front_L", new Vector3(-halfW + leftLen / 2f, height / 2f, frontZ), new Vector3(leftLen, height, t), wall, tile);

        float rightLen = halfW - doorRight;
        if (rightLen > 0.01f)
            Box(b, "Wall_Front_R", new Vector3(doorRight + rightLen / 2f, height / 2f, frontZ), new Vector3(rightLen, height, t), wall, tile);

        float lintelH = height - s.doorHeight;
        Box(b, "Wall_Front_Top", new Vector3(s.doorOffset, s.doorHeight + lintelH / 2f, frontZ), new Vector3(s.doorWidth, lintelH, t), wall, tile);

        // Roof and floor
        Box(b, "Roof",  new Vector3(0, height + t / 2f, 0), new Vector3(s.width, t, s.depth), roof, tile);
        Box(b, "Floor", new Vector3(0, 0.025f, 0), new Vector3(s.width - t * 2f, 0.05f, s.depth - t * 2f), floor, tile);

        // Interior wall with a gap, makes a back room to hide in
        if (s.interiorWall)
        {
            float gap = s.interiorGap;
            float innerW = s.width - t * 2f;
            float segment = (innerW - gap) / 2f;
            float z = s.depth * 0.15f;
            Box(b, "Interior_L", new Vector3(-innerW / 2f + segment / 2f, height / 2f, z), new Vector3(segment, height, t), inner, tile);
            Box(b, "Interior_R", new Vector3(innerW / 2f - segment / 2f, height / 2f, z), new Vector3(segment, height, t), inner, tile);
        }

        // Windows, roof edge, rooftop clutter, sidewalk
        AddDetails(b, s.width, s.depth, height, t, s.doorOffset, s.doorWidth, s.details, pal, rng, wall);

        // Hatch in the roof: the Climber drops in through it
        if (s.roofHatch) AddRoofHatch(b, Metal(pal));

        // Doorway marker for the giant: it crouches here and reaches in
        AddReachPoint(b, s.doorOffset, frontZ);

        // Echo when you're inside
        if (s.reverb)
        {
            var zone = root.AddComponent<AudioReverbZone>();
            zone.reverbPreset = AudioReverbPreset.Room;
            zone.minDistance = Mathf.Min(s.width, s.depth) * 0.4f;
            zone.maxDistance = Mathf.Max(s.width, s.depth) * 0.5f;
        }
        return root;
    }

    public static GameObject AddReachPoint(Transform building, float doorX, float frontZ)
    {
        var rp = new GameObject("ReachPoint");
        rp.transform.SetParent(building, false);
        rp.transform.localPosition = new Vector3(doorX, 0f, frontZ - GiantStandDistance);
        rp.transform.localRotation = Quaternion.identity;   // forward (+Z) points into the building
        rp.AddComponent<ReachPoint>();
        return rp;
    }

    // ---------- Details: windows (some lit, a few flickering), roof parapet, rooftop clutter, sidewalks ----------

    const float Storey = 3.2f;
    const float WindowW = 1.0f, WindowH = 1.3f;
    const float WindowSpacing = 2.6f;

    // wall = the outside wall material (for the roof edge); null leaves it for the Building Texturer
    public static Transform AddDetails(Transform building, float width, float depth, float height, float wallT,
                                       float doorOffset, float doorWidth, DetailOptions o,
                                       BuildingPalette pal, System.Random rng, Material wall = null)
    {
        var details = new GameObject("Details").transform;
        details.SetParent(building, false);

        float halfW = width / 2f, halfD = depth / 2f;
        float tile = pal != null ? pal.metersPerTile : 2.5f;

        if (o.windows)
        {
            var m = new WindowMats(pal);
            WindowsOnFace(details, Vector3.back,    new Vector3(0, 0, -halfD), Vector3.right,   width, height, o, m, rng, true,  doorOffset, doorWidth);
            WindowsOnFace(details, Vector3.forward, new Vector3(0, 0,  halfD), Vector3.right,   width, height, o, m, rng, false, 0, 0);
            WindowsOnFace(details, Vector3.left,    new Vector3(-halfW, 0, 0), Vector3.forward, depth, height, o, m, rng, false, 0, 0);
            WindowsOnFace(details, Vector3.right,   new Vector3( halfW, 0, 0), Vector3.forward, depth, height, o, m, rng, false, 0, 0);
            if (o.shopfront) Shopfront(details, halfW, halfD, doorOffset, doorWidth, o, m, rng);
        }

        float roofTop = height + wallT;

        if (o.parapet)
        {
            const float ph = 0.6f, pt = 0.2f;
            float y = roofTop + ph / 2f;
            Part(details, "Wall_Parapet_F", PrimitiveType.Cube, new Vector3(0, y, -halfD + pt / 2f), new Vector3(width, ph, pt), wall, true, tile);
            Part(details, "Wall_Parapet_B", PrimitiveType.Cube, new Vector3(0, y,  halfD - pt / 2f), new Vector3(width, ph, pt), wall, true, tile);
            Part(details, "Wall_Parapet_L", PrimitiveType.Cube, new Vector3(-halfW + pt / 2f, y, 0), new Vector3(pt, ph, depth - pt * 2f), wall, true, tile);
            Part(details, "Wall_Parapet_R", PrimitiveType.Cube, new Vector3( halfW - pt / 2f, y, 0), new Vector3(pt, ph, depth - pt * 2f), wall, true, tile);
        }

        if (o.roofClutter) RoofClutter(details, width, depth, roofTop, Metal(pal), rng);

        if (o.sidewalk)
        {
            float sw = o.sidewalkWidth, sh = 0.15f;
            var mat = pal != null && pal.sidewalkMaterial != null ? pal.sidewalkMaterial
                : Fallback("Sidewalk", new Color(0.42f, 0.42f, 0.42f), Color.black, 0.15f);
            Part(details, "Sidewalk_F", PrimitiveType.Cube, new Vector3(0, sh / 2f, -halfD - sw / 2f), new Vector3(width + sw * 2f, sh, sw), mat, true, tile);
            Part(details, "Sidewalk_B", PrimitiveType.Cube, new Vector3(0, sh / 2f,  halfD + sw / 2f), new Vector3(width + sw * 2f, sh, sw), mat, true, tile);
            Part(details, "Sidewalk_L", PrimitiveType.Cube, new Vector3(-halfW - sw / 2f, sh / 2f, 0), new Vector3(sw, sh, depth), mat, true, tile);
            Part(details, "Sidewalk_R", PrimitiveType.Cube, new Vector3( halfW + sw / 2f, sh / 2f, 0), new Vector3(sw, sh, depth), mat, true, tile);
        }
        return details;
    }

    struct WindowMats
    {
        public Material frame, dark, lit;
        public WindowMats(BuildingPalette pal)
        {
            frame = pal != null && pal.windowFrame != null ? pal.windowFrame : Fallback("Window_Frame", new Color(0.08f, 0.08f, 0.09f), Color.black, 0.3f);
            dark = pal != null && pal.windowDark != null ? pal.windowDark : Fallback("Window_Dark", new Color(0.03f, 0.035f, 0.045f), Color.black, 0.92f);
            lit = pal != null && pal.windowLit != null ? pal.windowLit : Fallback("Window_Lit", new Color(1f, 0.85f, 0.6f), new Color(1f, 0.72f, 0.42f) * 1.6f, 0.6f);
        }
    }

    static void WindowsOnFace(Transform parent, Vector3 normal, Vector3 faceCenter, Vector3 along, float length, float height,
                              DetailOptions o, WindowMats m, System.Random rng, bool isFront, float doorOffset, float doorWidth)
    {
        int cols = Mathf.FloorToInt((length - 1.2f) / WindowSpacing);
        if (cols < 1) { if (length > 2.2f) cols = 1; else return; }
        float spacing = length / cols;

        int rows = Mathf.Max(1, Mathf.FloorToInt((height - 0.6f) / Storey));
        if (o.highWindows) rows = 1;
        Vector2 size = o.highWindows ? new Vector2(WindowW * 1.6f, WindowH * 0.6f) : new Vector2(WindowW, WindowH);

        for (int r = 0; r < rows; r++)
        {
            float y = o.highWindows ? height - 1.3f : r * Storey + 1.75f;
            if (y + size.y / 2f > height - 0.3f) break;
            if (isFront && r == 0 && o.shopfront && !o.highWindows) continue;   // shop windows go here instead

            for (int c = 0; c < cols; c++)
            {
                float s = -length / 2f + spacing * (c + 0.5f);
                if (isFront && r == 0 && !o.highWindows && Mathf.Abs(s - doorOffset) < doorWidth / 2f + 0.9f) continue;   // keep clear of the door
                Window(parent, faceCenter + along * s + Vector3.up * y, normal, size, o, m, rng);
            }
        }
    }

    // One big window each side of a shop's door
    static void Shopfront(Transform parent, float halfW, float halfD, float doorOffset, float doorWidth,
                          DetailOptions o, WindowMats m, System.Random rng)
    {
        float doorL = doorOffset - doorWidth / 2f - 0.6f, doorR = doorOffset + doorWidth / 2f + 0.6f;
        float edgeL = -halfW + 0.7f, edgeR = halfW - 0.7f;
        foreach (var (a, z) in new[] { (edgeL, doorL), (doorR, edgeR) })
        {
            float len = Mathf.Min(z - a, 4.5f);
            if (len < 1.4f) continue;
            float mid = (a + z) / 2f;
            Window(parent, new Vector3(mid, 1.45f, -halfD), Vector3.back, new Vector2(len, 1.9f), o, m, rng);
        }
    }

    static void Window(Transform parent, Vector3 pos, Vector3 normal, Vector2 size, DetailOptions o, WindowMats m, System.Random rng)
    {
        Quaternion rot = Quaternion.LookRotation(normal);
        bool lit = rng.NextDouble() < o.litChance;

        var frame = Part(parent, "Window_Frame", PrimitiveType.Cube, pos + normal * 0.025f,
                         new Vector3(size.x + 0.16f, size.y + 0.16f, 0.05f), m.frame, false, 0f);
        frame.localRotation = rot;

        var glass = Part(parent, lit ? "Window_Lit" : "Window_Dark", PrimitiveType.Cube, pos + normal * 0.045f,
                         new Vector3(size.x, size.y, 0.03f), lit ? m.lit : m.dark, false, 0f);
        glass.localRotation = rot;
        if (lit && rng.NextDouble() < o.flickerChance / Mathf.Max(o.litChance, 0.01f))
            glass.gameObject.AddComponent<WindowFlicker>();
    }

    static void RoofClutter(Transform parent, float width, float depth, float roofTop, Material metal, System.Random rng)
    {
        int count = rng.Next(1, 4) + (width * depth > 120f ? 1 : 0) + (width * depth > 250f ? 1 : 0);
        float mx = Mathf.Max(0.5f, width / 2f - 1.2f), mz = Mathf.Max(0.5f, depth / 2f - 1.2f);

        for (int i = 0; i < count; i++)
        {
            Vector3 p = new Vector3(Range(rng, -mx, mx), 0f, Range(rng, -mz, mz));
            switch (rng.Next(0, 4))
            {
                case 0: // AC unit
                    Part(parent, "Prop_AC", PrimitiveType.Cube, p + Vector3.up * (roofTop + 0.45f), new Vector3(1.4f, 0.9f, 1.0f), metal, false, 0f);
                    break;
                case 1: // water tank
                    Part(parent, "Prop_Tank", PrimitiveType.Cylinder, p + Vector3.up * (roofTop + 0.9f), new Vector3(1.6f, 0.9f, 1.6f), metal, false, 0f);
                    break;
                case 2: // vent
                    Part(parent, "Prop_Vent", PrimitiveType.Cube, p + Vector3.up * (roofTop + 0.3f), new Vector3(0.5f, 0.6f, 0.5f), metal, false, 0f);
                    break;
                default: // antenna
                    Part(parent, "Prop_Antenna", PrimitiveType.Cylinder, p + Vector3.up * (roofTop + 1.4f), new Vector3(0.06f, 1.4f, 0.06f), metal, false, 0f);
                    break;
            }
        }
    }

    // ---------- Roof hatch ----------
    // The roof slab is split into four pieces around the hole, with a rim and a hinged lid on top (RoofHatch).

    const float HatchSize = 1.3f;
    const float RimHeight = 0.12f, RimThickness = 0.1f;

    // created (optional) collects every new object, so the editor can register them for Undo
    public static RoofHatch AddRoofHatch(Transform building, Material metal, List<GameObject> created = null)
    {
        var roof = building.Find("Roof");
        if (roof == null) return null;
        Vector3 rp = roof.localPosition, rs = roof.localScale;   // roof is a cube scaled to width x thickness x depth
        float halfW = rs.x / 2f, halfD = rs.z / 2f, s = HatchSize;
        if (rs.x < s + 2.5f || rs.z < s + 2.5f) return null;   // too small to cut a hole in

        if (!PickHatchSpot(building, rp, halfW, halfD, out float hx, out float hz)) return null;

        // Roof split around the hole (positions relative to the roof's centre). The original stays as "Roof".
        float x0 = hx - s / 2f, x1 = hx + s / 2f, z0 = hz - s / 2f, z1 = hz + s / 2f;
        RoofPiece(building, roof, null,     rp, rs, -halfW, halfW, -halfD, z0, created);
        RoofPiece(building, roof, "Roof_N", rp, rs, -halfW, halfW, z1, halfD, created);
        RoofPiece(building, roof, "Roof_W", rp, rs, -halfW, x0, z0, z1, created);
        RoofPiece(building, roof, "Roof_E", rp, rs, x1, halfW, z0, z1, created);

        // The hatch: rim around the hole and a lid on a hinge along one edge
        float top = rp.y + rs.y / 2f;
        var hatch = new GameObject("RoofHatch").transform;
        hatch.SetParent(building, false);
        hatch.localPosition = new Vector3(rp.x + hx, top, rp.z + hz);
        float r = RimThickness, half = s / 2f + r / 2f;
        Part(hatch, "Rim_N", PrimitiveType.Cube, new Vector3(0, RimHeight / 2f,  half), new Vector3(s + r * 2f, RimHeight, r), metal, false, 0f);
        Part(hatch, "Rim_S", PrimitiveType.Cube, new Vector3(0, RimHeight / 2f, -half), new Vector3(s + r * 2f, RimHeight, r), metal, false, 0f);
        Part(hatch, "Rim_W", PrimitiveType.Cube, new Vector3(-half, RimHeight / 2f, 0), new Vector3(r, RimHeight, s), metal, false, 0f);
        Part(hatch, "Rim_E", PrimitiveType.Cube, new Vector3( half, RimHeight / 2f, 0), new Vector3(r, RimHeight, s), metal, false, 0f);

        var hinge = new GameObject("Hinge").transform;
        hinge.SetParent(hatch, false);
        hinge.localPosition = new Vector3(-s / 2f - r, RimHeight, 0f);
        var lid = Part(hinge, "Lid", PrimitiveType.Cube, new Vector3(s / 2f + r, 0.03f, 0f), new Vector3(s + r * 2f, 0.06f, s + r * 2f), metal, false, 0f);
        lid.gameObject.isStatic = false;   // the lid moves

        var rh = hatch.gameObject.AddComponent<RoofHatch>();
        rh.hinge = hinge;
        rh.size = s;
        created?.Add(hatch.gameObject);
        return rh;
    }

    // Where on the roof to put the hatch (relative to the roof's centre): in the back room if there is one,
    // away from the edges, the interior wall and the rooftop clutter
    static bool PickHatchSpot(Transform building, Vector3 rp, float halfW, float halfD, out float hx, out float hz)
    {
        var wall = building.Find("Interior_L");
        bool hasWall = wall != null;
        float wallZ = hasWall ? wall.localPosition.z - rp.z : 0f;
        float preferZ = hasWall ? (wallZ + halfD) / 2f : 0f;   // middle of the back room

        var props = new List<Vector2>();
        var details = building.Find("Details");
        if (details != null)
            foreach (Transform p in details)
                if (p.name.StartsWith("Prop_"))
                {
                    Vector3 local = building.InverseTransformPoint(p.position) - rp;
                    props.Add(new Vector2(local.x, local.z));
                }

        float margin = HatchSize / 2f + 0.8f;
        float best = float.MaxValue;
        hx = hz = 0f;
        for (float x = -halfW + margin; x <= halfW - margin; x += 0.25f)
            for (float z = -halfD + margin; z <= halfD - margin; z += 0.25f)
            {
                if (hasWall && Mathf.Abs(z - wallZ) < HatchSize / 2f + 0.6f) continue;
                bool clear = true;
                foreach (var p in props)
                    if (Vector2.Distance(p, new Vector2(x, z)) < 1.8f) { clear = false; break; }
                if (!clear) continue;

                float score = Mathf.Abs(x) + Mathf.Abs(z - preferZ) + (hasWall && z < wallZ ? 100f : 0f);
                if (score < best) { best = score; hx = x; hz = z; }
            }
        return best < float.MaxValue;
    }

    // One slab of the split roof. name == null reuses the original "Roof" object; others are copies of it
    // (same material, BoxUV, static flags).
    static void RoofPiece(Transform building, Transform roof, string name, Vector3 rp, Vector3 rs,
                          float xMin, float xMax, float zMin, float zMax, List<GameObject> created)
    {
        if (xMax - xMin < 0.01f || zMax - zMin < 0.01f) return;
        Transform piece = roof;
        if (name != null)
        {
            piece = Object.Instantiate(roof.gameObject, building).transform;
            piece.name = name;
            created?.Add(piece.gameObject);
        }
        piece.localPosition = new Vector3(rp.x + (xMin + xMax) / 2f, rp.y, rp.z + (zMin + zMax) / 2f);
        piece.localRotation = roof.localRotation;
        piece.localScale = new Vector3(xMax - xMin, rs.y, zMax - zMin);
        if (piece.TryGetComponent(out BoxUV uv)) uv.Rebuild();
    }

    // ---------- Parts and materials ----------

    // A wall/roof/floor slab: a cube with a collider, textures tiled at real-world size
    static void Box(Transform parent, string name, Vector3 localPos, Vector3 size, Material mat, float tile)
        => Part(parent, name, PrimitiveType.Cube, localPos, size, mat, true, tile);

    // tile > 0 adds BoxUV (real-world texture tiling) to a cube
    static Transform Part(Transform parent, string name, PrimitiveType type, Vector3 localPos, Vector3 scale,
                          Material mat, bool keepCollider, float tile)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = scale;   // plain cubes are 1x1x1, so scale = size in meters
        go.isStatic = true;                // lets lighting + NavMesh treat it as level geometry
        if (!keepCollider) Object.DestroyImmediate(go.GetComponent<Collider>());
        if (mat != null) go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        if (type == PrimitiveType.Cube && tile > 0f)
        {
            var uv = go.AddComponent<BoxUV>();
            if (!Mathf.Approximately(uv.metersPerTile, tile)) { uv.metersPerTile = tile; uv.Rebuild(); }
        }
        return go.transform;
    }

    static Material PickWall(BuildingPalette pal, System.Random rng)
    {
        if (pal == null || pal.wallMaterials == null || pal.wallMaterials.Length == 0) return null;
        return pal.wallMaterials[rng.Next(pal.wallMaterials.Length)];
    }

    static Material Metal(BuildingPalette pal) => pal != null && pal.propMetal != null ? pal.propMetal
        : Fallback("Prop_Metal", new Color(0.35f, 0.36f, 0.37f), Color.black, 0.35f);

    // Plain URP materials for slots the palette leaves empty (made once, shared)
    static readonly Dictionary<string, Material> fallbacks = new Dictionary<string, Material>();

    public static Material Fallback(string name, Color color, Color emission, float smoothness)
    {
        if (fallbacks.TryGetValue(name, out var cached) && cached != null) return cached;
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
        mat.SetColor("_BaseColor", color);
        mat.SetFloat("_Smoothness", smoothness);
        if (emission != Color.black)
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", emission);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        fallbacks[name] = mat;
        return mat;
    }

    static float Range(System.Random rng, float min, float max) => min + (float)rng.NextDouble() * (max - min);
}
