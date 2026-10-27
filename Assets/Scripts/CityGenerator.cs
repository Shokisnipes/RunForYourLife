using System.Collections.Generic;
using UnityEngine;

// Builds a whole city from a seed: a ring road with a wall around it, a wide main road through the middle, streets
// cut at random (so blocks come in different sizes and streets meet in T-junctions), alleys too narrow for the giant
// (some walled off into dead ends), and blocks filled with rows of buildings facing the street, or a plaza, park,
// parking lot or empty lot. Street lamps along the streets, none in the alleys.
//
// Everything random comes from one System.Random, and nothing depends on physics or scene order, so the same seed
// builds the same city on every machine (task 4.5c: the host picks the seed, everyone builds it).
public static class CityGenerator
{
    // What got built, and the spots the rest of the game needs
    public class City
    {
        public GameObject root;
        public int seed;
        public readonly List<GameObject> buildings = new List<GameObject>();
        public readonly List<StreetLamp> lamps = new List<StreetLamp>();
        public Vector3 playerSpawn;
        public Quaternion playerSpawnRotation = Quaternion.identity;
        public readonly List<Vector3> monsterSpawns = new List<Vector3>();   // on streets, far from the players
        public Vector3 patrolCenter;
        public float patrolRadius;
    }

    enum Use { Buildings, Plaza, Park, Parking, Empty }

    // One block between streets. road[side] = width of the street on that side (0 = -X, 1 = +X, 2 = -Z, 3 = +Z)
    class Block
    {
        public float x0, z0, x1, z1;
        public float[] road = new float[4];
        public Use use = Use.Buildings;
        public float W => x1 - x0;
        public float D => z1 - z0;
        public Vector3 Center => new Vector3((x0 + x1) / 2f, 0f, (z0 + z1) / 2f);
        public Block Copy() => new Block { x0 = x0, z0 = z0, x1 = x1, z1 = z1, road = (float[])road.Clone() };
    }

    // Wider than this counts as a street (doors and lamps go there); narrower is an alley
    const float StreetMin = 5f;
    const float Curb = 0.15f;   // sidewalks, plazas and parks are raised this much; buildings stand on top

    static CitySettings s;
    static System.Random rng;
    static City city;
    static Transform root;
    static readonly List<Block> blocks = new List<Block>();
    static readonly List<(Vector3 a, Vector3 b)> deadEnds = new List<(Vector3, Vector3)>();

    public static City Generate(CitySettings settings, int seed, Vector3 origin, Transform parent = null)
    {
        s = settings;
        rng = new System.Random(seed);
        city = new City { seed = seed };
        blocks.Clear();
        deadEnds.Clear();

        var go = new GameObject($"City (seed {seed})");
        go.transform.SetParent(parent, false);
        go.transform.position = origin;
        root = go.transform;
        city.root = go;

        float hw = s.citySize.x / 2f, hd = s.citySize.y / 2f;
        Ground(hw, hd);

        // Cut the area inside the ring road into blocks
        var all = new Block { x0 = -hw + s.streetWidth, x1 = hw - s.streetWidth, z0 = -hd + s.streetWidth, z1 = hd - s.streetWidth };
        for (int i = 0; i < 4; i++) all.road[i] = s.streetWidth;
        Split(all, 0);

        AssignUses();
        foreach (var b in blocks)
        {
            switch (b.use)
            {
                case Use.Buildings: BuildingBlock(b); break;
                case Use.Plaza: Plaza(b); break;
                case Use.Park: Park(b); break;
                case Use.Parking: Parking(b); break;
                default: EmptyBlock(b); break;
            }
        }
        foreach (var (a, b) in deadEnds) Wall("Wall_DeadEnd", a, b, 3f, 0.3f, ConcreteWall());

        PickSpawns(hw, hd);
        city.patrolCenter = root.position;
        city.patrolRadius = Mathf.Max(hw, hd);

        var result = city;
        s = null; rng = null; city = null; root = null;
        return result;
    }

    // ---------- Layout ----------

    static void Split(Block b, int depth)
    {
        bool cutX;   // true = the new road runs along Z (cuts the width)
        float road;
        if (depth == 0)
        {
            cutX = b.D >= b.W;   // main road runs the long way through the city
            road = s.mainRoadWidth;
        }
        else
        {
            bool tooWide = b.W > s.maxBlockSize, tooDeep = b.D > s.maxBlockSize;
            if (!tooWide && !tooDeep) { MaybeAlley(b); return; }
            cutX = tooWide && (!tooDeep || b.W >= b.D);
            road = s.streetWidth;
        }

        float len = cutX ? b.W : b.D;
        float min = s.minBlockSize, max = len - road - s.minBlockSize;
        if (max < min) { MaybeAlley(b); return; }
        float pos = depth == 0
            ? Mathf.Clamp((len - road) * Range(0.42f, 0.58f), min, max)
            : Mathf.Lerp(min, max, (Rand() + Rand()) / 2f);   // two dice: cuts lean toward the middle

        Cut(b, cutX, pos, road, out var a, out var c);
        Split(a, depth + 1);
        Split(c, depth + 1);
    }

    static void Cut(Block b, bool cutX, float pos, float road, out Block a, out Block c)
    {
        a = b.Copy();
        c = b.Copy();
        if (cutX) { a.x1 = b.x0 + pos; a.road[1] = road; c.x0 = a.x1 + road; c.road[0] = road; }
        else      { a.z1 = b.z0 + pos; a.road[3] = road; c.z0 = a.z1 + road; c.road[2] = road; }
    }

    // Cut a block in two with an alley across its long side (sometimes walled off at one end)
    static void MaybeAlley(Block b)
    {
        bool cutX = b.W >= b.D;
        float len = cutX ? b.W : b.D;
        float w = Range(s.alleyWidth.x, s.alleyWidth.y);
        const float minHalf = 13f;   // room for a row of buildings each side
        if (Rand() >= s.alleyChance || len < minHalf * 2f + w) { blocks.Add(b); return; }

        Cut(b, cutX, Range(minHalf, len - w - minHalf), w, out var a, out var c);
        blocks.Add(a);
        blocks.Add(c);

        if (Rand() < s.deadEndChance)
        {
            bool startEnd = Rand() < 0.5f;
            if (cutX)
            {
                float z = startEnd ? b.z0 + 0.4f : b.z1 - 0.4f;
                deadEnds.Add((new Vector3(a.x1, 0, z), new Vector3(c.x0, 0, z)));
            }
            else
            {
                float x = startEnd ? b.x0 + 0.4f : b.x1 - 0.4f;
                deadEnds.Add((new Vector3(x, 0, a.z1), new Vector3(x, 0, c.z0)));
            }
        }
    }

    static void AssignUses()
    {
        // Plaza: the big-enough block nearest the centre
        if (s.plaza)
        {
            Block best = null;
            foreach (var b in blocks)
                if (Mathf.Min(b.W, b.D) >= 20f && (best == null || b.Center.sqrMagnitude < best.Center.sqrMagnitude)) best = b;
            if (best != null) best.use = Use.Plaza;
        }

        var free = new List<Block>();
        foreach (var b in blocks) if (b.use == Use.Buildings && Mathf.Min(b.W, b.D) >= 18f) free.Add(b);
        Shuffle(free);
        int i = 0;
        for (int n = 0; n < s.parks && i < free.Count; n++) free[i++].use = Use.Park;
        for (int n = 0; n < s.parkingLots && i < free.Count; n++) free[i++].use = Use.Parking;
        for (int n = 0; n < s.emptyBlocks && i < free.Count; n++) free[i++].use = Use.Empty;
    }

    // ---------- Block contents ----------

    static void BuildingBlock(Block b)
    {
        Slab("Sidewalk_Block", b, Curb, SidewalkMat(), 2.5f);

        // Rows of buildings along the two long sides, doors facing the street. A side on an alley gets no doors.
        bool alongX = b.W >= b.D;
        int sideA = alongX ? 2 : 0, sideB = alongX ? 3 : 1;
        float across = alongX ? b.D : b.W;
        bool streetA = b.road[sideA] >= StreetMin, streetB = b.road[sideB] >= StreetMin;
        float sw = s.sidewalkWidth;

        if (!streetA && !streetB)
        {
            // Both long sides are alleys: face the short sides instead, if they're streets
            sideA = alongX ? 0 : 2; sideB = alongX ? 1 : 3;
            across = alongX ? b.W : b.D;
            streetA = b.road[sideA] >= StreetMin; streetB = b.road[sideB] >= StreetMin;
        }

        if (streetA && streetB && across >= 2f * (8f + sw) + 1.5f)
        {
            float maxDepth = Mathf.Min(19f, (across - 2f * sw - 1.5f) / 2f);
            Row(b, sideA, maxDepth);
            Row(b, sideB, maxDepth);
        }
        else if (streetA || streetB)
            Row(b, streetA ? sideA : sideB, Mathf.Min(19f, across - 2f * sw - 1f));

        for (int side = 0; side < 4; side++)
            if (b.road[side] >= StreetMin) LampsAlong(b, side);
    }

    static void Row(Block b, int side, float maxDepth)
    {
        Edge(b, side, out Vector3 p0, out Vector3 along, out Vector3 outward, out float length);
        Vector3 inward = -outward;
        float sw = s.sidewalkWidth;
        float t = sw, end = length - sw;

        while (end - t >= 7f)
        {
            float room = end - t;
            if (Rand() < s.emptyLotChance && room > 10f)
            {
                float lot = Mathf.Min(room, Range(8f, 12f));
                EmptyLot(p0 + along * (t + lot / 2f) + inward * (sw + maxDepth / 2f), along, lot, maxDepth);
                t += lot + 1f;
                continue;
            }

            if (!PickKind(room, maxDepth, out var kind)) break;
            var spec = BuildingBuilder.RandomSpec(kind, rng, room, maxDepth);
            spec.details.sidewalk = false;   // the whole block has one sidewalk slab
            if (spec.width < 6.5f) break;

            Vector3 c = p0 + along * (t + spec.width / 2f) + inward * (sw + spec.depth / 2f);
            c.y = Curb;
            var building = BuildingBuilder.Build(spec, root.TransformPoint(c), root.rotation * Quaternion.LookRotation(inward),
                                                 root, s.palette, rng);   // building +Z points into the block, door to the street
            city.buildings.Add(building);

            t += spec.width + (Rand() < s.passageChance ? Range(2f, 3.2f) : Range(0.3f, 1f));
        }
    }

    static bool PickKind(float room, float maxDepth, out BuildingKind kind)
    {
        float wh = s.kindWeights.x, ws = room >= 10f && maxDepth >= 9f ? s.kindWeights.y : 0f;
        float ww = room >= 14f && maxDepth >= 14f ? s.kindWeights.z : 0f;
        if (room < 7f || maxDepth < 8f) wh = 0f;
        kind = BuildingKind.House;
        float total = wh + ws + ww;
        if (total <= 0f) return false;
        float r = Rand() * total;
        kind = r < wh ? BuildingKind.House : r < wh + ws ? BuildingKind.Shop : BuildingKind.Warehouse;
        return true;
    }

    static void Plaza(Block b)
    {
        Slab("Plaza", b, Curb, s.pavingMaterial != null ? s.pavingMaterial : SidewalkMat(), 2.5f);
        Vector3 c = b.Center;
        float top = Curb;

        // Fountain
        var stone = Mat("City_Stone", new Color(0.45f, 0.44f, 0.42f), 0.2f);
        Prim(PrimitiveType.Cylinder, "Fountain_Basin", c + Vector3.up * (top + 0.35f), new Vector3(6f, 0.35f, 6f), stone, true);
        Prim(PrimitiveType.Cylinder, "Fountain_Water", c + Vector3.up * (top + 0.62f), new Vector3(5.4f, 0.02f, 5.4f),
             Mat("City_Water", new Color(0.05f, 0.07f, 0.08f), 0.95f), false);
        Prim(PrimitiveType.Cylinder, "Fountain_Pillar", c + Vector3.up * (top + 1.2f), new Vector3(0.7f, 0.9f, 0.7f), stone, true);

        // Benches facing the fountain, lamps at the corners
        for (int i = 0; i < 6; i++)
        {
            float a = i * Mathf.PI * 2f / 6f + Range(-0.15f, 0.15f);
            Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            Bench(c + dir * 6.5f + Vector3.up * top, -dir);
        }
        float ix = b.W / 2f - 2f, iz = b.D / 2f - 2f;
        foreach (var off in new[] { new Vector3(-ix, 0, -iz), new Vector3(ix, 0, -iz), new Vector3(-ix, 0, iz), new Vector3(ix, 0, iz) })
            Lamp(c + off + Vector3.up * top, -off.normalized);
    }

    static void Park(Block b)
    {
        Slab("Park_Grass", b, Curb, s.grassMaterial != null ? s.grassMaterial : Mat("City_Grass", new Color(0.22f, 0.32f, 0.14f), 0.05f), 3f);
        Vector3 c = b.Center;
        var paving = s.pavingMaterial != null ? s.pavingMaterial : SidewalkMat();

        // Two crossing paths, slightly above the grass
        Cube("Park_Path", c + Vector3.up * (Curb + 0.01f), new Vector3(b.W, 0.02f, 2.6f), paving, false, 2.5f);
        Cube("Park_Path", c + Vector3.up * (Curb + 0.01f), new Vector3(2.6f, 0.02f, b.D), paving, false, 2.5f);

        // Trees and bushes off the paths (bushes have no collider: you can crouch in them)
        int trees = Mathf.RoundToInt(b.W * b.D / 70f);
        for (int i = 0; i < trees * 3 && trees > 0; i++)
        {
            Vector3 p = c + new Vector3(Range(-b.W / 2f + 1.5f, b.W / 2f - 1.5f), 0f, Range(-b.D / 2f + 1.5f, b.D / 2f - 1.5f));
            if (Mathf.Abs(p.x - c.x) < 2.8f || Mathf.Abs(p.z - c.z) < 2.8f) continue;
            p.y = Curb;
            if (Rand() < 0.7f) { Tree(p); trees--; }
            else Prim(PrimitiveType.Sphere, "Bush", p + Vector3.up * 0.5f, new Vector3(1.6f, 1.1f, 1.6f) * Range(0.8f, 1.3f),
                      Mat("City_Leaves", new Color(0.12f, 0.22f, 0.09f), 0.1f), false);
        }

        // Benches along the paths, a couple of lamps (parks are dark)
        Bench(c + new Vector3(3.5f, Curb, 4f), Vector3.left);
        Bench(c + new Vector3(-3.5f, Curb, -4f), Vector3.right);
        Lamp(c + new Vector3(1.8f, Curb, b.D / 4f), Vector3.left);
        Lamp(c + new Vector3(-1.8f, Curb, -b.D / 4f), Vector3.right);
    }

    static void Parking(Block b)
    {
        Slab("Parking_Lot", b, 0.06f, Ground(), 5f);
        bool alongX = b.W >= b.D;
        Vector3 along = alongX ? Vector3.right : Vector3.forward, across = alongX ? Vector3.forward : Vector3.right;
        float length = alongX ? b.W : b.D, width = alongX ? b.D : b.W;
        var line = Mat("City_Line", new Color(0.75f, 0.75f, 0.7f), 0.2f);

        // Bays: a row of stalls each side of a 6 m aisle
        int bays = Mathf.Max(1, Mathf.FloorToInt((width - 4f) / 16f));
        float bayW = (width - 4f) / bays;
        for (int bay = 0; bay < bays; bay++)
        {
            float mid = -width / 2f + 2f + bayW * (bay + 0.5f);
            foreach (int sign in new[] { -1, 1 })
            {
                float rowOff = mid + sign * 5.5f;   // stall centre: half the aisle + half a stall
                int stalls = Mathf.FloorToInt((length - 6f) / 2.7f);
                for (int i = 0; i < stalls; i++)
                {
                    float a = -length / 2f + 3f + 2.7f * (i + 0.5f);
                    Vector3 p = b.Center + along * a + across * rowOff;
                    Cube("Parking_Line", p + along * 1.35f + Vector3.up * 0.065f,
                         alongX ? new Vector3(0.1f, 0.01f, 4.8f) : new Vector3(4.8f, 0.01f, 0.1f), line, false, 0f);
                    if (Rand() < 0.55f) Car(p + Vector3.up * 0.06f, across * -sign);
                }
            }
        }
        LampsAlong(b, alongX ? 2 : 0);
        LampsAlong(b, alongX ? 3 : 1);
    }

    static void EmptyBlock(Block b)
    {
        Slab("Empty_Lot", b, 0.08f, Dirt(), 3f);
        int junk = Mathf.RoundToInt(b.W * b.D / 120f);
        for (int i = 0; i < junk; i++)
        {
            Vector3 p = b.Center + new Vector3(Range(-b.W / 2f + 2f, b.W / 2f - 2f), 0.08f, Range(-b.D / 2f + 2f, b.D / 2f - 2f));
            if (Rand() < 0.25f) Dumpster(p, Range(0f, 360f));
            else if (Rand() < 0.4f) Car(p, Quaternion.Euler(0, Range(0f, 360f), 0) * Vector3.forward, true);
            else Rubble(p);
        }
    }

    // An empty lot in a row of buildings
    static void EmptyLot(Vector3 c, Vector3 along, float width, float depth)
    {
        c.y = Curb;
        var rot = Quaternion.LookRotation(along);
        var dirt = Cube("Empty_Lot", c + Vector3.up * 0.01f, new Vector3(depth, 0.02f, width), Dirt(), false, 3f);
        dirt.localRotation = rot;
        if (Rand() < 0.6f) Dumpster(c + along * Range(-width / 4f, width / 4f), Range(0f, 360f));
        for (int i = 0, n = 1 + (int)(Rand() * 3); i < n; i++)
            Rubble(c + along * Range(-width / 2f + 1f, width / 2f - 1f) + Quaternion.Euler(0, 90, 0) * along * Range(-depth / 3f, depth / 3f));
    }

    // ---------- Lamps ----------

    static void LampsAlong(Block b, int side)
    {
        Edge(b, side, out Vector3 p0, out Vector3 along, out Vector3 outward, out float length);
        for (float t = Range(3f, s.lampSpacing * 0.6f); t < length - 2f; t += s.lampSpacing * Range(0.85f, 1.15f))
            if (Rand() < s.lampChance)
                Lamp(p0 + along * t - outward * 0.6f + Vector3.up * Curb, outward);
    }

    static void Lamp(Vector3 p, Vector3 armDir)
    {
        float r = Rand();   // always roll, so the dice stay in step even without a prefab
        if (s.streetLampPrefab == null) return;
        armDir.y = 0f;
        var go = Object.Instantiate(s.streetLampPrefab, root.TransformPoint(p), root.rotation * Quaternion.LookRotation(armDir), root);
        go.name = "StreetLamp";
        var lamp = go.GetComponent<StreetLamp>();
        if (lamp == null) return;
        lamp.mode = r < s.brokenLampChance ? StreetLamp.Mode.Broken
                  : r < s.brokenLampChance + s.flickeringLampChance ? StreetLamp.Mode.Flickering
                  : StreetLamp.Mode.Normal;
        city.lamps.Add(lamp);
    }

    // ---------- Spawns ----------

    static void PickSpawns(float hw, float hd)
    {
        // Players: in front of a building door near the -Z end. Monsters: on streets at the far (+Z) end.
        var near = new List<GameObject>();
        foreach (var bld in city.buildings)
            if (root.InverseTransformPoint(bld.transform.position).z < -hd * 0.4f) near.Add(bld);
        if (near.Count == 0) near.AddRange(city.buildings);

        if (near.Count > 0)
        {
            var bld = near[(int)(Rand() * near.Count) % near.Count];
            var rp = bld.GetComponentInChildren<ReachPoint>();
            Vector3 p = rp != null ? rp.transform.position : bld.transform.position;
            city.playerSpawn = new Vector3(p.x, root.position.y + Curb, p.z);
            city.playerSpawnRotation = Quaternion.LookRotation(bld.transform.forward);
        }
        else city.playerSpawn = root.TransformPoint(new Vector3(0, 0, -hd + s.streetWidth / 2f));

        // Ring road at the far end, plus the far end of the main road
        float z = hd - s.streetWidth / 2f;
        city.monsterSpawns.Add(root.TransformPoint(new Vector3(-hw * 0.5f, 0, z)));
        city.monsterSpawns.Add(root.TransformPoint(new Vector3(hw * 0.5f, 0, z)));
        city.monsterSpawns.Add(root.TransformPoint(new Vector3(0, 0, z)));
    }

    // ---------- Pieces ----------

    static void Ground(float hw, float hd)
    {
        float m = s.outerMargin;
        Cube("Ground", new Vector3(0, -0.25f, 0), new Vector3(hw * 2f + m * 2f, 0.5f, hd * 2f + m * 2f), Ground(), true, 5f);

        // Wall around the city
        var wall = ConcreteWall();
        float h = s.boundaryWallHeight;
        Wall("Wall_Boundary", new Vector3(-hw, 0, -hd), new Vector3(hw, 0, -hd), h, 0.5f, wall);
        Wall("Wall_Boundary", new Vector3(-hw, 0, hd), new Vector3(hw, 0, hd), h, 0.5f, wall);
        Wall("Wall_Boundary", new Vector3(-hw, 0, -hd), new Vector3(-hw, 0, hd), h, 0.5f, wall);
        Wall("Wall_Boundary", new Vector3(hw, 0, -hd), new Vector3(hw, 0, hd), h, 0.5f, wall);
    }

    static void Wall(string name, Vector3 a, Vector3 b, float height, float thickness, Material mat)
    {
        Vector3 d = b - a;
        var t = Cube(name, (a + b) / 2f + Vector3.up * height / 2f, new Vector3(thickness, height, d.magnitude + thickness), mat, true, 2.5f);
        t.localRotation = Quaternion.LookRotation(d.normalized);
        if (t.TryGetComponent(out BoxUV uv)) uv.Rebuild();
    }

    // A raised slab covering the whole block (sidewalk, plaza, grass...)
    static void Slab(string name, Block b, float height, Material mat, float tile)
        => Cube(name, b.Center + Vector3.up * height / 2f, new Vector3(b.W, height, b.D), mat, true, tile);

    // A block side as a line: starts at p0, runs 'along' for 'length', 'outward' points at the street
    static void Edge(Block b, int side, out Vector3 p0, out Vector3 along, out Vector3 outward, out float length)
    {
        switch (side)
        {
            case 0: p0 = new Vector3(b.x0, 0, b.z0); along = Vector3.forward; outward = Vector3.left; length = b.D; break;
            case 1: p0 = new Vector3(b.x1, 0, b.z0); along = Vector3.forward; outward = Vector3.right; length = b.D; break;
            case 2: p0 = new Vector3(b.x0, 0, b.z0); along = Vector3.right; outward = Vector3.back; length = b.W; break;
            default: p0 = new Vector3(b.x0, 0, b.z1); along = Vector3.right; outward = Vector3.forward; length = b.W; break;
        }
    }

    static void Tree(Vector3 p)
    {
        float k = Range(0.8f, 1.3f);
        Prim(PrimitiveType.Cylinder, "Tree_Trunk", p + Vector3.up * 1.6f * k, new Vector3(0.35f, 1.6f, 0.35f) * k,
             Mat("City_Bark", new Color(0.17f, 0.12f, 0.08f), 0.1f), true);
        var leaves = Mat("City_Leaves", new Color(0.12f, 0.22f, 0.09f), 0.1f);
        Prim(PrimitiveType.Sphere, "Tree_Crown", p + Vector3.up * 3.9f * k, Vector3.one * 3.2f * k, leaves, false);
        Prim(PrimitiveType.Sphere, "Tree_Crown", p + new Vector3(0.6f, 4.6f, -0.4f) * k, Vector3.one * 2.2f * k, leaves, false);
    }

    static void Bench(Vector3 p, Vector3 facing)
    {
        var wood = Mat("City_Wood", new Color(0.25f, 0.17f, 0.1f), 0.2f);
        var rot = Quaternion.LookRotation(facing);
        var seat = Cube("Bench", p + Vector3.up * 0.45f, new Vector3(1.8f, 0.08f, 0.5f), wood, true, 0f);
        seat.localRotation = rot;
        var back = Cube("Bench_Back", p + Vector3.up * 0.75f - facing * 0.25f, new Vector3(1.8f, 0.5f, 0.06f), wood, false, 0f);
        back.localRotation = rot;
    }

    static readonly Color[] CarColors =
    {
        new Color(0.25f, 0.05f, 0.05f), new Color(0.08f, 0.12f, 0.2f), new Color(0.6f, 0.6f, 0.58f),
        new Color(0.08f, 0.08f, 0.08f), new Color(0.3f, 0.32f, 0.2f), new Color(0.45f, 0.4f, 0.32f),
    };

    // A boxy parked car: cover to hide behind. wreck = rusted, sitting at an angle
    static void Car(Vector3 p, Vector3 facing, bool wreck = false)
    {
        int c = (int)(Rand() * CarColors.Length) % CarColors.Length;
        var paint = wreck ? Mat("City_Rust", new Color(0.24f, 0.13f, 0.07f), 0.1f) : Mat($"City_Car{c}", CarColors[c], 0.55f);
        var glass = Mat("City_CarGlass", new Color(0.02f, 0.025f, 0.03f), 0.9f);
        var rot = Quaternion.LookRotation(facing) * Quaternion.Euler(wreck ? Range(-4f, 4f) : 0f, Range(-6f, 6f), wreck ? Range(-5f, 5f) : 0f);
        var car = new GameObject("Car").transform;
        car.SetParent(root, false);
        car.localPosition = p;
        car.localRotation = rot;
        Part(car, PrimitiveType.Cube, "Car_Body", new Vector3(0, 0.55f, 0), new Vector3(1.8f, 0.75f, 4.2f), paint, true);
        Part(car, PrimitiveType.Cube, "Car_Cabin", new Vector3(0, 1.2f, -0.2f), new Vector3(1.6f, 0.6f, 2.2f), glass, true);
    }

    static void Dumpster(Vector3 p, float yaw)
    {
        var t = Cube("Dumpster", p + Vector3.up * 0.7f, new Vector3(2f, 1.4f, 1.2f), Mat("City_Dumpster", new Color(0.1f, 0.2f, 0.12f), 0.3f), true, 0f);
        t.localRotation = Quaternion.Euler(0, yaw, 0);
    }

    static void Rubble(Vector3 p)
    {
        var concrete = ConcreteWall();
        for (int i = 0, n = 2 + (int)(Rand() * 3); i < n; i++)
        {
            var t = Cube("Rubble", p + new Vector3(Range(-1f, 1f), Range(0.1f, 0.3f), Range(-1f, 1f)),
                         new Vector3(Range(0.4f, 1.4f), Range(0.2f, 0.6f), Range(0.4f, 1.2f)), concrete, i == 0, 2.5f);
            t.localRotation = Quaternion.Euler(Range(-20f, 20f), Range(0f, 360f), Range(-20f, 20f));
        }
    }

    static Transform Cube(string name, Vector3 localPos, Vector3 size, Material mat, bool collider, float tile)
    {
        var t = Part(root, PrimitiveType.Cube, name, localPos, size, mat, collider);
        if (tile > 0f)
        {
            var uv = t.gameObject.AddComponent<BoxUV>();
            uv.metersPerTile = tile;
            uv.Rebuild();
        }
        return t;
    }

    static Transform Prim(PrimitiveType type, string name, Vector3 localPos, Vector3 scale, Material mat, bool collider)
        => Part(root, type, name, localPos, scale, mat, collider);

    static Transform Part(Transform parent, PrimitiveType type, string name, Vector3 localPos, Vector3 scale, Material mat, bool collider)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = scale;
        go.isStatic = true;
        if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
        if (mat != null) go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        return go.transform;
    }

    // ---------- Materials ----------

    static Material Ground() => s.groundMaterial != null ? s.groundMaterial : Mat("City_Asphalt", new Color(0.09f, 0.09f, 0.1f), 0.5f);
    static Material Dirt() => s.dirtMaterial != null ? s.dirtMaterial : Mat("City_Dirt", new Color(0.2f, 0.16f, 0.12f), 0.05f);
    static Material SidewalkMat() => s.palette != null && s.palette.sidewalkMaterial != null ? s.palette.sidewalkMaterial
        : Mat("Sidewalk", new Color(0.42f, 0.42f, 0.42f), 0.15f);

    static Material ConcreteWall()
    {
        if (s.palette != null && s.palette.wallMaterials != null)
            foreach (var m in s.palette.wallMaterials)
                if (m != null && m.name.Contains("concrete")) return m;
        return Mat("City_Concrete", new Color(0.4f, 0.4f, 0.4f), 0.1f);
    }

    static Material Mat(string name, Color color, float smoothness) => BuildingBuilder.Fallback(name, color, Color.black, smoothness);

    // ---------- Dice ----------

    static float Rand() => (float)rng.NextDouble();
    static float Range(float min, float max) => min + Rand() * (max - min);

    static void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
