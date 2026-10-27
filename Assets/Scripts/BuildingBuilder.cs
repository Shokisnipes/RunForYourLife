using System.Collections.Generic;
using UnityEngine;

public enum BuildingKind { House, Shop, Warehouse, Apartment }

// How the inside is split up. Every doorway between rooms is wide enough for the Climber to crawl through.
public enum InteriorLayout { Open, BackRoom, SideRoom, TwoBackRooms }

// Builds an enterable box building out of plain cubes with BoxColliders: walls, roof, doorway, optional back room,
// windows, roof edge, rooftop clutter, sidewalk, a roof hatch (the Climber drops in through it) and a ReachPoint
// (the giant crouches there and reaches in). Houses may get a sloped roof (RoofBuilder; no hatch, and the flat slab
// under it is called "Ceiling" so the Climber leaves them alone). Apartment blocks are 3-4 storeys tall: you can
// only get into the ground floor (a ceiling over it, with a stairwell hole under the roof hatch). BuildingDressing
// adds the outside variety (decals, tint, awnings, shutters, balconies). Runs in the game (the city generator) and in the editor (Building
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
    // Apartment blocks: height of the ground floor you can walk into
    public const float ApartmentCeiling = 3.4f;

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
        public float doorHeight;   // tall enough for the Climber (NavMesh height 2.6) when the door is open
        public float doorOffset;   // slides the door left/right along the front wall
        public InteriorLayout layout;   // rooms to hide in
        public float interiorGap;       // width of the doorways between rooms
        public bool furniture;          // crates, shelves, tables... cover to hide behind
        public bool roofHatch, reverb;
        public RoofStyle roof;          // sloped roofs never get a hatch
        public bool decals;             // grime, stains, posters, graffiti on the outside walls
        public DetailOptions details;

        public static Spec Default => new Spec
        {
            kind = BuildingKind.House, width = 8f, depth = 10f, height = 4f, wallThickness = 0.25f,
            doorWidth = 2.2f, doorHeight = 2.8f, doorOffset = 0f, layout = InteriorLayout.Open, interiorGap = InteriorGap, furniture = true,
            roofHatch = true, reverb = true, roof = RoofStyle.Flat, decals = true, details = DetailOptions.Default,
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
                s.layout = Pick(rng, InteriorLayout.Open, 0.2f, InteriorLayout.BackRoom, 0.45f, InteriorLayout.SideRoom, 0.35f);
                s.details.litChance = 0.25f;
                // Most houses get a Western sloped roof (safe from the Climber); a few stay flat with a hatch
                double r = rng.NextDouble();
                s.roof = r < 0.5 ? RoofStyle.Gable : r < 0.85 ? RoofStyle.Hip : RoofStyle.Flat;
                break;
            case BuildingKind.Shop:
                s.width = Range(rng, 10f, 14f);
                s.depth = Range(rng, 9f, 12f);
                s.height = Range(rng, 4.5f, 5.5f);
                s.layout = Pick(rng, InteriorLayout.BackRoom, 0.5f, InteriorLayout.TwoBackRooms, 0.3f, InteriorLayout.SideRoom, 0.2f);   // stock rooms
                s.details.shopfront = true;
                s.details.litChance = 0.3f;
                break;
            case BuildingKind.Warehouse:
                s.width = Range(rng, 14f, 18f);
                s.depth = Range(rng, 14f, 19f);
                s.height = Range(rng, 5.5f, 6.5f);
                s.layout = Pick(rng, InteriorLayout.Open, 0.35f, InteriorLayout.SideRoom, 0.3f, InteriorLayout.TwoBackRooms, 0.35f);
                s.details.highWindows = true;
                s.details.litChance = 0.08f;
                break;
            case BuildingKind.Apartment:
                // Eastern European panel block: 3-4 storeys, rows of windows, balconies, flat roof with a hatch
                s.width = Range(rng, 14f, 20f);
                s.depth = Range(rng, 10f, 13f);
                s.height = 3.2f * rng.Next(3, 5) + 0.4f;
                s.layout = Pick(rng, InteriorLayout.BackRoom, 0.5f, InteriorLayout.SideRoom, 0.3f, InteriorLayout.TwoBackRooms, 0.2f);
                s.details.litChance = 0.3f;
                break;
        }
        s.width = Mathf.Min(s.width, maxWidth);
        s.depth = Mathf.Min(s.depth, maxDepth);
        if (s.roof != RoofStyle.Flat)
        {
            s.roofHatch = false;
            s.details.parapet = false;
            s.details.roofClutter = false;
        }
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

        // The front door (DoorManager opens and closes it)
        AddDoor(b, s.doorOffset, frontZ, s.doorWidth, s.doorHeight, t);

        // Roof and floor. Under a sloped roof the flat slab is just a ceiling (the Climber only uses "Roof").
        bool sloped = s.roof != RoofStyle.Flat;
        Box(b, sloped ? "Ceiling" : "Roof", new Vector3(0, height + t / 2f, 0), new Vector3(s.width, t, s.depth), sloped ? inner : roof, tile);
        Box(b, "Floor", new Vector3(0, 0.025f, 0), new Vector3(s.width - t * 2f, 0.05f, s.depth - t * 2f), floor, tile);

        // Inside walls and furniture
        var gaps = Interior(b, s, inner, tile, rng);
        if (s.furniture) Furnish(b, s, gaps, Metal(pal), rng);

        // Apartment block: a ceiling over the ground floor (the floors above are shut off)
        bool apartment = s.kind == BuildingKind.Apartment;
        if (apartment)
            Box(b, "Ceiling_Inside", new Vector3(0, ApartmentCeiling + 0.1f, 0), new Vector3(s.width - t * 2f, 0.2f, s.depth - t * 2f), inner, tile);

        // Windows, roof edge, rooftop clutter, sidewalk
        AddDetails(b, s.width, s.depth, height, t, s.doorOffset, s.doorWidth, s.details, pal, rng, wall);
        CutWindows(b);   // real openings behind the ground-floor windows (the Climber smashes its way through)

        // Sloped roof on top, then the outside variety
        if (sloped) RoofBuilder.Build(b, s.roof, s.width, s.depth, height + t, Range(rng, 28f, 40f), wall, tile, rng);
        BuildingDressing.Dress(b, s, pal, rng, wall);

        // Hatch in the roof: the Climber drops in through it (in an apartment block, down a stairwell hole)
        if (s.roofHatch && !sloped)
        {
            var hatch = AddRoofHatch(b, Metal(pal));
            if (apartment && hatch != null) CutCeiling(b, hatch.transform.localPosition);
        }

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

    // Double doors in the doorway, swinging inward, on the Door layer, plus the NavMeshObstacle that shuts the
    // doorway for the Climber while they're closed
    public static Door AddDoor(Transform building, float doorX, float frontZ, float width, float height, float wallT)
    {
        var root = new GameObject("Door");
        root.transform.SetParent(building, false);
        root.transform.localPosition = new Vector3(doorX, 0f, frontZ);
        int layer = Door.Layer;
        var wood = Fallback("Door_Wood", new Color(0.24f, 0.16f, 0.1f), Color.black, 0.25f);
        var brass = Fallback("Door_Handle", new Color(0.55f, 0.5f, 0.4f), Color.black, 0.7f);
        float leafW = width / 2f - 0.02f, leafH = height - 0.04f;

        Transform Leaf(string name, float side)   // side -1 = left (hinge on the left edge), +1 = right
        {
            var hinge = new GameObject(name).transform;
            hinge.SetParent(root.transform, false);
            hinge.localPosition = new Vector3(side * width / 2f, 0f, 0f);
            var leaf = GameObject.CreatePrimitive(PrimitiveType.Cube);
            leaf.name = "Leaf";
            leaf.layer = layer;
            leaf.transform.SetParent(hinge, false);
            leaf.transform.localPosition = new Vector3(-side * (leafW / 2f + 0.01f), leafH / 2f + 0.02f, 0f);
            leaf.transform.localScale = new Vector3(leafW, leafH, 0.06f);
            leaf.GetComponent<MeshRenderer>().sharedMaterial = wood;
            foreach (float face in new[] { -1f, 1f })
            {
                var knob = GameObject.CreatePrimitive(PrimitiveType.Cube);
                knob.name = "Handle";
                Object.DestroyImmediate(knob.GetComponent<Collider>());
                knob.transform.SetParent(hinge, false);
                knob.transform.localPosition = new Vector3(-side * (leafW - 0.12f), 1.05f, face * 0.06f);
                knob.transform.localScale = new Vector3(0.14f, 0.03f, 0.04f);
                knob.GetComponent<MeshRenderer>().sharedMaterial = brass;
            }
            return hinge;
        }

        var door = root.AddComponent<Door>();
        door.leftHinge = Leaf("Hinge_L", -1f);
        door.rightHinge = Leaf("Hinge_R", 1f);

        var block = new GameObject("DoorBlock");
        block.transform.SetParent(root.transform, false);
        var obstacle = block.AddComponent<UnityEngine.AI.NavMeshObstacle>();
        obstacle.shape = UnityEngine.AI.NavMeshObstacleShape.Box;
        obstacle.center = new Vector3(0f, height / 2f, 0f);
        obstacle.size = new Vector3(width, height, wallT + 0.6f);
        obstacle.carving = true;
        obstacle.carveOnlyStationary = false;
        door.blocker = obstacle;
        return door;
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

    // ---------- Inside: rooms and furniture ----------

    // Walls between rooms (all named Interior_..., which the tools and the texturer look for). Returns the middle of
    // each doorway between rooms (local XZ), so furniture keeps out of the way.
    static List<Vector2> Interior(Transform b, Spec s, Material mat, float tile, System.Random rng)
    {
        var gaps = new List<Vector2>();
        float t = s.wallThickness, gap = s.interiorGap;
        float h = s.kind == BuildingKind.Apartment ? ApartmentCeiling : s.height;   // up to the ceiling
        float left = -s.width / 2f + t, right = s.width / 2f - t, front = -s.depth / 2f + t, back = s.depth / 2f - t;

        switch (s.layout)
        {
            case InteriorLayout.BackRoom:
            case InteriorLayout.TwoBackRooms:
            {
                // Wall across the building; the back room is 35-50% of the depth, its doorway anywhere along it
                float z = Mathf.Lerp(front, back, Range(rng, 0.5f, 0.65f));
                float gx = Range(rng, left + gap / 2f + 0.6f, right - gap / 2f - 0.6f);
                WallAcross(b, "Interior_L", left, gx - gap / 2f, z, t, h, mat, tile);
                WallAcross(b, "Interior_R", gx + gap / 2f, right, z, t, h, mat, tile);
                gaps.Add(new Vector2(gx, z));

                if (s.layout == InteriorLayout.TwoBackRooms && right - left > 9f)
                {
                    // Split the back room in two, on the far side from its doorway. It stops short of the cross
                    // wall, leaving a passage between the two rooms.
                    float x = gx < 0f ? Range(rng, Mathf.Max(gx + gap, 0.2f * right), 0.6f * right)
                                      : Range(rng, 0.6f * left, Mathf.Min(gx - gap, 0.2f * left));
                    WallAlong(b, "Interior_Div", x, z + t / 2f + gap, back, t, h, mat, tile);
                    gaps.Add(new Vector2(x, z + t / 2f + gap / 2f));
                }
                break;
            }
            case InteriorLayout.SideRoom:
            {
                // A long room down one side, with its doorway somewhere along it
                float roomW = Range(rng, 3f, 4.2f);
                bool onRight = rng.NextDouble() < 0.5;
                float x = onRight ? right - roomW : left + roomW;
                // Keep the room's wall out of the front doorway: try the other side, else move it just past the door
                float doorL = s.doorOffset - s.doorWidth / 2f - 0.9f, doorR = s.doorOffset + s.doorWidth / 2f + 0.9f;
                if (x > doorL && x < doorR)
                {
                    float other = onRight ? left + roomW : right - roomW;
                    if (other <= doorL || other >= doorR) { x = other; onRight = !onRight; }
                    else x = onRight ? doorR : doorL;
                }
                float gz = Range(rng, front + gap / 2f + 0.8f, back - gap / 2f - 0.8f);
                WallAlong(b, "Interior_SideA", x, front, gz - gap / 2f, t, h, mat, tile);
                WallAlong(b, "Interior_SideB", x, gz + gap / 2f, back, t, h, mat, tile);
                gaps.Add(new Vector2(x, gz));
                break;
            }
        }
        return gaps;
    }

    // Interior wall running across the building (along X) from x0 to x1, at z
    static void WallAcross(Transform b, string name, float x0, float x1, float z, float t, float h, Material mat, float tile)
    {
        if (x1 - x0 < 0.3f) return;
        Box(b, name, new Vector3((x0 + x1) / 2f, h / 2f, z), new Vector3(x1 - x0, h, t), mat, tile);
    }

    // Interior wall running front to back (along Z) from z0 to z1, at x
    static void WallAlong(Transform b, string name, float x, float z0, float z1, float t, float h, Material mat, float tile)
    {
        if (z1 - z0 < 0.3f) return;
        Box(b, name, new Vector3(x, h / 2f, (z0 + z1) / 2f), new Vector3(t, h, z1 - z0), mat, tile);
    }

    // Crates, shelves, tables, mattresses and drums against the walls: cover to hide behind. Kept clear of the front
    // door (the giant reaches in there) and of the doorways between rooms (the Climber crawls through).
    static void Furnish(Transform b, Spec s, List<Vector2> gaps, Material metal, System.Random rng)
    {
        var parent = new GameObject("Furniture").transform;
        parent.SetParent(b, false);
        var wood = Fallback("Furniture_Wood", new Color(0.26f, 0.18f, 0.11f), Color.black, 0.15f);
        var cloth = Fallback("Furniture_Cloth", new Color(0.32f, 0.3f, 0.27f), Color.black, 0.05f);

        float t = s.wallThickness;
        float left = -s.width / 2f + t, right = s.width / 2f - t, front = -s.depth / 2f + t, back = s.depth / 2f - t;
        var walls = new List<Rect>();   // interior walls in local XZ
        foreach (Transform w in b)
            if (w.name.StartsWith("Interior_"))
                walls.Add(new Rect(w.localPosition.x - w.localScale.x / 2f, w.localPosition.z - w.localScale.z / 2f, w.localScale.x, w.localScale.z));
        var placed = new List<Rect>();

        int wanted = 2 + Mathf.RoundToInt(s.width * s.depth / 35f);
        for (int tries = 0; tries < wanted * 4 && placed.Count < wanted; tries++)
        {
            // What, and how big (x = along the wall, y = out from the wall)
            int type = rng.Next(5);
            Vector2 size = type == 0 ? new Vector2(1.1f, 1.1f)    // crates
                         : type == 1 ? new Vector2(1.8f, 0.5f)    // shelf
                         : type == 2 ? new Vector2(1.6f, 0.9f)    // table
                         : type == 3 ? new Vector2(1.9f, 0.95f)   // mattress
                         : new Vector2(0.7f, 0.7f);               // oil drum

            // Against which outside wall (left, right or back)
            int side = rng.Next(3);
            Vector2 c; float yaw;
            if (side == 0) { c = new Vector2(left + size.y / 2f + 0.05f, Range(rng, front + size.x / 2f, back - size.x / 2f)); yaw = 90f; }
            else if (side == 1) { c = new Vector2(right - size.y / 2f - 0.05f, Range(rng, front + size.x / 2f, back - size.x / 2f)); yaw = -90f; }
            else { c = new Vector2(Range(rng, left + size.x / 2f, right - size.x / 2f), back - size.y / 2f - 0.05f); yaw = 180f; }
            Vector2 extent = side < 2 ? new Vector2(size.y, size.x) : size;
            var rect = new Rect(c - extent / 2f, extent);

            // Keep clear of the front door, the doorways between rooms, interior walls and other furniture
            if (Mathf.Abs(c.x - s.doorOffset) < s.doorWidth / 2f + 1.4f + extent.x / 2f && c.y < front + 3f) continue;
            bool blocked = false;
            foreach (var g in gaps) if (Vector2.Distance(g, c) < s.interiorGap / 2f + 1.3f + Mathf.Max(extent.x, extent.y) / 2f) { blocked = true; break; }
            foreach (var w in walls) if (Grow(w, 0.1f).Overlaps(rect)) { blocked = true; break; }
            foreach (var p in placed) if (Grow(p, 0.3f).Overlaps(rect)) { blocked = true; break; }
            if (blocked) continue;
            placed.Add(rect);

            var item = new GameObject("Prop_Furniture").transform;
            item.SetParent(parent, false);
            item.localPosition = new Vector3(c.x, 0.05f, c.y);
            item.localRotation = Quaternion.Euler(0f, yaw + Range(rng, -4f, 4f), 0f);
            switch (type)
            {
                case 0: // stacked crates
                    Part(item, "Crate", PrimitiveType.Cube, new Vector3(0, 0.45f, 0), new Vector3(1f, 0.9f, 1f), wood, true, 0f);
                    if (rng.NextDouble() < 0.6)
                        Part(item, "Crate", PrimitiveType.Cube, new Vector3(Range(rng, -0.1f, 0.1f), 1.25f, Range(rng, -0.1f, 0.1f)),
                             new Vector3(0.75f, 0.7f, 0.75f), wood, true, 0f).localRotation = Quaternion.Euler(0f, Range(rng, -20f, 20f), 0f);
                    break;
                case 1: // tall shelf
                    Part(item, "Shelf", PrimitiveType.Cube, new Vector3(0, 0.95f, 0), new Vector3(1.8f, 1.9f, 0.45f), metal, true, 0f);
                    break;
                case 2: // table (a slab on a block, enough to crouch behind)
                    Part(item, "Table", PrimitiveType.Cube, new Vector3(0, 0.74f, 0), new Vector3(1.6f, 0.06f, 0.9f), wood, true, 0f);
                    Part(item, "Table_Base", PrimitiveType.Cube, new Vector3(0, 0.36f, 0), new Vector3(1.3f, 0.7f, 0.1f), wood, false, 0f);
                    break;
                case 3: // old mattress on the floor
                    Part(item, "Mattress", PrimitiveType.Cube, new Vector3(0, 0.1f, 0), new Vector3(1.9f, 0.2f, 0.95f), cloth, false, 0f);
                    break;
                default: // oil drum
                    Part(item, "Barrel", PrimitiveType.Cylinder, new Vector3(0, 0.45f, 0), new Vector3(0.6f, 0.45f, 0.6f), metal, true, 0f);
                    break;
            }
        }
    }

    static Rect Grow(Rect r, float m) => new Rect(r.x - m, r.y - m, r.width + m * 2f, r.height + m * 2f);

    static InteriorLayout Pick(System.Random rng, InteriorLayout a, float wa, InteriorLayout b, float wb, InteriorLayout c, float wc)
    {
        float r = (float)rng.NextDouble() * (wa + wb + wc);
        return r < wa ? a : r < wa + wb ? b : c;
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
        public Material frame, dark, lit, glass;
        public WindowMats(BuildingPalette pal)
        {
            glass = pal != null && pal.windowGlass != null ? pal.windowGlass : null;   // null: ground floor uses dark/lit too
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

        // High-windowed buildings (warehouses) get a low row too, every other column: a way in for the Climber
        if (o.highWindows)
            for (int c = 0; c < cols; c += 2)
            {
                float s = -length / 2f + spacing * (c + 0.5f);
                if (isFront && Mathf.Abs(s - doorOffset) < doorWidth / 2f + 0.9f) continue;
                Window(parent, faceCenter + along * s + Vector3.up * 1.75f, normal, new Vector2(WindowW, WindowH), o, m, rng);
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
        bool ground = pos.y - size.y / 2f < 2.2f;   // sill low enough to climb in from the street

        Transform glass;
        if (!ground)
        {
            // Upper floors: a frame and glass on the face of a solid wall
            var frame = Part(parent, "Window_Frame", PrimitiveType.Cube, pos + normal * 0.025f,
                             new Vector3(size.x + 0.16f, size.y + 0.16f, 0.05f), m.frame, false, 0f);
            frame.localRotation = rot;
            glass = Part(parent, lit ? "Window_Lit" : "Window_Dark", PrimitiveType.Cube, pos + normal * 0.045f,
                         new Vector3(size.x, size.y, 0.03f), lit ? m.lit : m.dark, false, 0f);
        }
        else
        {
            // Ground floor: a real opening (CutWindows), a frame round its edge and a pane of glass that can break.
            // "Window_Frame" stays as an empty marker of the frame's size (BuildingDressing keeps decals off it).
            var marker = new GameObject("Window_Frame").transform;
            marker.SetParent(parent, false);
            marker.localPosition = pos + normal * 0.025f;
            marker.localRotation = rot;
            marker.localScale = new Vector3(size.x + 0.16f, size.y + 0.16f, 0.05f);
            Vector3 right = rot * Vector3.right;
            foreach (float side in new[] { -1f, 1f })
            {
                Part(parent, "Window_Bar", PrimitiveType.Cube, pos + Vector3.up * side * (size.y / 2f + 0.04f) + normal * 0.03f,
                     new Vector3(size.x + 0.16f, 0.08f, 0.06f), m.frame, false, 0f).localRotation = rot;
                Part(parent, "Window_Bar", PrimitiveType.Cube, pos + right * side * (size.x / 2f + 0.04f) + normal * 0.03f,
                     new Vector3(0.08f, size.y, 0.06f), m.frame, false, 0f).localRotation = rot;
            }
            // Clear glass you can see through (lit-window glow is for the upper floors)
            bool clear = m.glass != null;
            glass = Part(parent, clear ? "Window_Glass" : lit ? "Window_Lit" : "Window_Dark", PrimitiveType.Cube, pos + normal * 0.02f,
                         new Vector3(size.x, size.y, 0.03f), clear ? m.glass : lit ? m.lit : m.dark, true, 0f);
            glass.gameObject.isStatic = false;   // it breaks
            if (clear) lit = false;
        }
        glass.localRotation = rot;
        if (ground) glass.gameObject.AddComponent<BreakableWindow>();
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

    // Cuts a real opening in the outside wall behind every ground-floor window: the wall cube it sits on is replaced
    // by the pieces around the hole (left, right, below the sill, above the head), keeping its name, material and tiling
    static void CutWindows(Transform b)
    {
        var details = b.Find("Details");
        if (details == null) return;
        foreach (var w in details.GetComponentsInChildren<BreakableWindow>())
        {
            Transform g = w.transform;
            Vector3 n = g.localRotation * Vector3.forward;
            bool alongX = Mathf.Abs(n.z) > 0.5f;   // front/back walls run along X, side walls along Z
            float c = alongX ? g.localPosition.x : g.localPosition.z;
            float half = g.localScale.x / 2f;
            float y0 = g.localPosition.y - g.localScale.y / 2f, y1 = g.localPosition.y + g.localScale.y / 2f;
            float plane = alongX ? g.localPosition.z : g.localPosition.x;

            foreach (Transform wall in b)
            {
                if (!wall.name.StartsWith("Wall_") || wall.GetComponent<BoxCollider>() == null) continue;
                Vector3 p = wall.localPosition, s = wall.localScale;
                float thick = alongX ? s.z : s.x, wp = alongX ? p.z : p.x;
                if (Mathf.Abs(Mathf.Abs(plane - wp) - thick / 2f) > 0.15f) continue;   // the glass isn't on this wall's face
                float a0 = alongX ? p.x - s.x / 2f : p.z - s.z / 2f, a1 = alongX ? p.x + s.x / 2f : p.z + s.z / 2f;
                float b0 = p.y - s.y / 2f, b1 = p.y + s.y / 2f;
                if (c - half < a0 - 0.01f || c + half > a1 + 0.01f || y0 < b0 - 0.01f || y1 > b1 + 0.01f) continue;
                SplitAroundHole(b, wall, alongX, wp, thick, a0, a1, b0, b1, c - half, c + half, y0, y1);
                break;
            }
        }
    }

    static void SplitAroundHole(Transform b, Transform wall, bool alongX, float wp, float thick,
                                float a0, float a1, float b0, float b1, float h0, float h1, float y0, float y1)
    {
        var mat = wall.GetComponent<MeshRenderer>().sharedMaterial;
        float tile = wall.TryGetComponent(out BoxUV uv) ? uv.metersPerTile : 2.5f;
        string name = wall.name;
        Object.DestroyImmediate(wall.gameObject);

        void Piece(string n, float u0, float u1, float v0, float v1)
        {
            if (u1 - u0 < 0.01f || v1 - v0 < 0.01f) return;
            float u = (u0 + u1) / 2f, v = (v0 + v1) / 2f;
            Vector3 pos = alongX ? new Vector3(u, v, wp) : new Vector3(wp, v, u);
            Vector3 size = alongX ? new Vector3(u1 - u0, v1 - v0, thick) : new Vector3(thick, v1 - v0, u1 - u0);
            Box(b, n, pos, size, mat, tile);
        }
        Piece(name, a0, h0, b0, b1);            // left of the opening, full height
        Piece(name + "_R", h1, a1, b0, b1);     // right of it
        Piece(name + "_Sill", h0, h1, b0, y0);  // under it
        Piece(name + "_Head", h0, h1, y1, b1);  // over it
    }

    // Apartment block: a stairwell hole in the ground-floor ceiling right under the roof hatch, so the Climber can drop
    // all the way down (and climb back out)
    static void CutCeiling(Transform building, Vector3 hatchPos)
    {
        var ceiling = building.Find("Ceiling_Inside");
        if (ceiling == null) return;
        Vector3 cp = ceiling.localPosition, cs = ceiling.localScale;
        float s = HatchSize + 0.5f, hx = hatchPos.x - cp.x, hz = hatchPos.z - cp.z;
        float halfW = cs.x / 2f, halfD = cs.z / 2f;
        float x0 = hx - s / 2f, x1 = hx + s / 2f, z0 = hz - s / 2f, z1 = hz + s / 2f;
        RoofPiece(building, ceiling, null, cp, cs, -halfW, halfW, -halfD, z0, null);
        RoofPiece(building, ceiling, "Ceiling_Inside_N", cp, cs, -halfW, halfW, z1, halfD, null);
        RoofPiece(building, ceiling, "Ceiling_Inside_W", cp, cs, -halfW, x0, z0, z1, null);
        RoofPiece(building, ceiling, "Ceiling_Inside_E", cp, cs, x1, halfW, z0, z1, null);
    }

    // Where on the roof to put the hatch (relative to the roof's centre): in the back room if there is one,
    // away from the edges, the interior wall and the rooftop clutter
    static bool PickHatchSpot(Transform building, Vector3 rp, float halfW, float halfD, out float hx, out float hz)
    {
        var wall = building.Find("Interior_L");
        bool hasWall = wall != null;
        float wallZ = hasWall ? wall.localPosition.z - rp.z : 0f;
        float preferZ = hasWall ? (wallZ + halfD) / 2f : 0f;   // middle of the back room

        // Rooftop clutter, and furniture inside (the Climber lands under the hatch)
        var props = new List<Vector2>();
        foreach (var group in new[] { building.Find("Details"), building.Find("Furniture") })
            if (group != null)
                foreach (Transform p in group)
                    if (p.name.StartsWith("Prop_"))
                    {
                        Vector3 local = building.InverseTransformPoint(p.position) - rp;
                        props.Add(new Vector2(local.x, local.z));
                    }
        // Interior walls, so the hatch doesn't open right above one
        var walls = new List<Rect>();
        foreach (Transform c in building)
            if (c.name.StartsWith("Interior_"))
                walls.Add(new Rect(c.localPosition.x - rp.x - c.localScale.x / 2f - 0.5f, c.localPosition.z - rp.z - c.localScale.z / 2f - 0.5f,
                                   c.localScale.x + 1f, c.localScale.z + 1f));

        float margin = HatchSize / 2f + 0.8f;
        float best = float.MaxValue;
        hx = hz = 0f;
        for (float x = -halfW + margin; x <= halfW - margin; x += 0.25f)
            for (float z = -halfD + margin; z <= halfD - margin; z += 0.25f)
            {
                if (hasWall && Mathf.Abs(z - wallZ) < HatchSize / 2f + 0.6f) continue;
                var hole = new Rect(x - HatchSize / 2f, z - HatchSize / 2f, HatchSize, HatchSize);
                bool clear = true;
                foreach (var w in walls) if (w.Overlaps(hole)) { clear = false; break; }
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
