using System.Collections.Generic;
using UnityEngine;

// Street furniture (task 13a): the stuff along every sidewalk and at the street corners, built from simple shapes.
// Each prop has its own function, so a model can replace it later without touching the placement.
//
// Collision, so the giant doesn't get stuck (its NavMesh is baked from colliders, radius 2.5):
//  - small things (hydrants, bins, mailboxes, newspaper boxes, signs, bus shelters, road barriers) go on the Fence
//    layer: they block players and the Climber, the giant steps over them (left out of its NavMesh, like fences)
//  - tall things (power poles, traffic lights) are solid for the giant too, so they stand on the same curb line as
//    the street lamps: the street gets no narrower for it than the lamps already make it
//  - cones, meters and wires have no collider at all
public static partial class CityGenerator
{
    // Stretches of the current block side already used (x = distance along the side, y = half width)
    static readonly List<Vector2> taken = new List<Vector2>();

    static bool Free(float t, float half)
    {
        foreach (var o in taken) if (Mathf.Abs(o.x - t) < o.y + half + 0.3f) return false;
        return true;
    }

    static void Take(float t, float half) => taken.Add(new Vector2(t, half));

    // A free spot along the side for something this wide (tries a few), or -1
    static float FreeSpot(float length, float half)
    {
        for (int i = 0; i < 8; i++)
        {
            float t = Range(2.5f + half, length - 2.5f - half);
            if (Free(t, half)) { Take(t, half); return t; }
        }
        return -1f;
    }

    // Everything along one street side of a building block
    static void SidewalkFurniture(Block b, int side)
    {
        Edge(b, side, out Vector3 p0, out Vector3 along, out Vector3 outward, out float length);
        taken.Clear();
        Take(0f, 2f);        // the corners get their own things (CornerFurniture)
        Take(length, 2f);

        // Keep doorways clear
        foreach (var bld in city.buildings)
            foreach (var rp in bld.GetComponentsInChildren<ReachPoint>())
            {
                Vector3 d = root.InverseTransformPoint(rp.DoorPoint) - p0;
                float inward = -Vector3.Dot(d, outward);
                float t = Vector3.Dot(d, along);
                if (inward > -0.5f && inward < 4f && t > -2f && t < length + 2f) Take(t, rp.doorWidth / 2f + 0.6f);
            }

        Vector3 OnCurb(float t, float fromEdge) => p0 + along * t - outward * fromEdge + Vector3.up * Curb;
        bool main = b.road[side] >= s.mainRoadWidth - 0.01f;

        // Street lamps
        for (float t = Range(3f, s.lampSpacing * 0.6f); t < length - 2f; t += s.lampSpacing * Range(0.85f, 1.15f))
            if (Rand() < s.lampChance && Free(t, 0.3f))
            {
                Take(t, 0.4f);
                Lamp(OnCurb(t, 0.6f), outward);
            }

        // A power line down one side of each street (the block sides facing +X and +Z)
        if (s.powerLines && (side == 1 || side == 3))
        {
            // A pole near each end of the block and evenly spaced between, so the wires run the whole way along
            // (they stop at the corners: wires across a junction would cut through the giant as it walks by)
            var tops = new List<Vector3[]>();
            int spans = Mathf.Max(1, Mathf.CeilToInt((length - 6f) / s.poleSpacing));
            for (int i = 0; i <= spans; i++)
            {
                float t = Mathf.Lerp(3f, length - 3f, i / (float)spans) + (i > 0 && i < spans ? Range(-1.5f, 1.5f) : 0f);
                float pt = t;
                for (int k = 0; k < 5 && !Free(pt, 0.3f); k++) pt += 1.2f;   // step past a lamp or a door
                if (!Free(pt, 0.3f))   // try the other way
                    for (int k = 0; k < 5 && !Free(pt, 0.3f); k++) pt = t - 1.2f * (k + 1);
                if (!Free(pt, 0.3f) || pt < 2.8f || pt > length - 2.8f) continue;
                Take(pt, 0.4f);
                tops.Add(UtilityPole(OnCurb(pt, 0.5f), along, outward));
            }
            PowerLine(tops);
        }

        for (int i = 0, n = Count(length * s.hydrantsPer100m / 100f); i < n; i++)
        {
            float t = FreeSpot(length, 0.3f);
            if (t >= 0f) Hydrant(OnCurb(t, 0.5f), outward);
        }
        for (int i = 0, n = Count(length * s.trashCansPer100m / 100f); i < n; i++)
        {
            float t = FreeSpot(length, 0.35f);
            if (t >= 0f) TrashCan(OnCurb(t, 0.55f));
        }
        for (int i = 0, n = Count(length * s.mailboxesPer100m / 100f); i < n; i++)
        {
            float t = FreeSpot(length, 0.35f);
            if (t >= 0f) Mailbox(OnCurb(t, 0.55f), outward);
        }

        // A bus stop on a long enough side
        if (length > 24f && Rand() < s.busStopChance)
        {
            float t = FreeSpot(length, 2.2f);
            if (t >= 0f) BusStop(OnCurb(t, 0f), along, outward);
        }

        // A few newspaper boxes in a row near one end
        if (Rand() < s.newspaperBoxChance)
        {
            float t = Rand() < 0.5f ? 3.5f : length - 5f;
            if (Free(t + 0.7f, 1.2f))
            {
                Take(t + 0.7f, 1.2f);
                for (int i = 0, n = 2 + (int)(Rand() * 2); i < n; i++)
                    NewspaperBox(OnCurb(t + i * 0.6f, 0.5f), outward);
            }
        }

        // Parking meters along the main road
        if (main && s.parkingMeters)
            for (float t = 4f; t < length - 4f; t += 5.5f)
                if (Rand() < 0.8f && Free(t, 0.15f))
                {
                    Take(t, 0.15f);
                    ParkingMeter(OnCurb(t, 0.35f), outward);
                }
    }

    // Traffic lights where the main road crosses, stop signs and street name signs at the other corners
    static void CornerFurniture(Block b)
    {
        // (x side, z side) of each corner: 0 -X, 1 +X, 2 -Z, 3 +Z
        int[,] corners = { { 0, 2 }, { 1, 2 }, { 0, 3 }, { 1, 3 } };
        for (int c = 0; c < 4; c++)
        {
            int sx = corners[c, 0], sz = corners[c, 1];
            float rx = b.road[sx], rz = b.road[sz];
            if (rx < StreetMin || rz < StreetMin) continue;   // an alley corner: nothing
            Vector3 outX = sx == 0 ? Vector3.left : Vector3.right, outZ = sz == 2 ? Vector3.back : Vector3.forward;
            Vector3 p = new Vector3(sx == 0 ? b.x0 : b.x1, Curb, sz == 2 ? b.z0 : b.z1) - outX * 0.7f - outZ * 0.7f;

            bool main = rx >= s.mainRoadWidth - 0.01f || rz >= s.mainRoadWidth - 0.01f;
            if (main && s.trafficLights) TrafficLight(p, outX, outZ);
            else if (Rand() < s.stopSignChance) StopSign(p, Rand() < 0.5f ? outX : outZ, Rand() < s.streetSignChance);
        }
    }

    // Road works now and then: cones round a spot in the lane, sometimes a striped barrier
    static void RoadWorks(Road road, Vector3 start, Vector3 along, Vector3 across, float length, float width)
    {
        for (int i = 0, n = Count(length * s.roadWorksPer100m / 100f); i < n; i++)
        {
            Vector3 c = start + along * Range(6f, length - 6f) + across * (Rand() < 0.5f ? -1f : 1f) * Range(1f, width / 2f - 1.5f);
            if (InOtherRoad(road, c)) continue;
            int cones = 3 + (int)(Rand() * 4);
            float r = Range(1f, 1.8f);
            for (int k = 0; k < cones; k++)
            {
                float a = k * Mathf.PI * 2f / cones + Range(-0.2f, 0.2f);
                bool fallen = Rand() < 0.2f;
                Cone(c + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r, fallen);
            }
            if (Rand() < 0.6f) RoadBarrier(c + along * (r + 0.6f), across);
            if (Rand() < 0.5f) Prim(PrimitiveType.Cylinder, "Road_Hole", c + Vector3.up * 0.004f, new Vector3(1.2f, 0.004f, 1.2f),
                                    Mat("Road_Hole", new Color(0.02f, 0.02f, 0.02f), 0.2f), false);
        }
    }

    // ---------- The props (swap these for models later) ----------

    // An empty holder for a multi-part prop, standing at p (root-local), facing 'forward'
    static Transform Prop(string name, Vector3 p, Vector3 forward)
    {
        var t = new GameObject(name).transform;
        t.SetParent(root, false);
        t.localPosition = p;
        forward.y = 0f;
        t.localRotation = forward.sqrMagnitude > 0.001f ? Quaternion.LookRotation(forward) : Quaternion.identity;
        t.gameObject.isStatic = true;
        return t;
    }

    // Collider on the Fence layer: blocks players and the Climber, the giant steps over it
    static Transform StepOver(Transform t)
    {
        t.gameObject.layer = FenceBuilder.Layer;
        return t;
    }

    // Wooden pole with a crossarm; returns the three wire points at the top (root-local)
    static Vector3[] UtilityPole(Vector3 p, Vector3 along, Vector3 outward)
    {
        var wood = Mat("City_PoleWood", new Color(0.2f, 0.15f, 0.1f), 0.1f);
        var dark = Mat("City_Metal", new Color(0.1f, 0.1f, 0.11f), 0.4f);
        var pole = Prop("UtilityPole", p, along);
        float h = Range(8.5f, 9.5f);
        Part(pole, PrimitiveType.Cylinder, "Pole", new Vector3(0f, h / 2f, 0f), new Vector3(0.28f, h / 2f, 0.28f), wood, true);
        // Crossarm across the street direction (local X), insulators on top
        Part(pole, PrimitiveType.Cube, "Pole_Arm", new Vector3(0f, h - 0.5f, 0f), new Vector3(1.8f, 0.12f, 0.12f), wood, false);
        var tops = new Vector3[3];
        for (int i = 0; i < 3; i++)
        {
            float x = (i - 1) * 0.75f;
            Part(pole, PrimitiveType.Cylinder, "Pole_Insulator", new Vector3(x, h - 0.36f, 0f), new Vector3(0.08f, 0.08f, 0.08f),
                 Mat("City_Insulator", new Color(0.35f, 0.4f, 0.35f), 0.7f), false);
            tops[i] = pole.TransformPoint(new Vector3(x, h - 0.3f, 0f));
            tops[i] = root.InverseTransformPoint(tops[i]);
        }
        if (Rand() < 0.25f)   // a transformer can
            Part(pole, PrimitiveType.Cylinder, "Pole_Transformer", new Vector3(0f, h - 1.6f, -0.32f), new Vector3(0.45f, 0.4f, 0.45f), dark, false);
        return tops;
    }

    // Sagging wires between consecutive poles: one mesh for the whole run, no collider
    static void PowerLine(List<Vector3[]> poles)
    {
        if (poles.Count < 2) return;
        var verts = new List<Vector3>();
        var tris = new List<int>();
        const int Segments = 10;
        const float R = 0.025f;
        for (int p = 0; p + 1 < poles.Count; p++)
            for (int w = 0; w < 3; w++)
            {
                Vector3 a = poles[p][w], b = poles[p + 1][w];
                float span = Vector3.Distance(a, b);
                float sag = 0.35f + span * 0.012f;
                Vector3 dir = (b - a).normalized;
                Vector3 side = Vector3.Cross(Vector3.up, dir).normalized;
                int baseIndex = verts.Count;
                for (int i = 0; i <= Segments; i++)
                {
                    float u = i / (float)Segments;
                    Vector3 c = Vector3.Lerp(a, b, u) + Vector3.down * sag * 4f * u * (1f - u);   // a parabola is close enough
                    verts.Add(c + Vector3.up * R);
                    verts.Add(c + side * R);
                    verts.Add(c + Vector3.down * R);
                    verts.Add(c - side * R);
                }
                for (int i = 0; i < Segments; i++)
                    for (int k = 0; k < 4; k++)
                    {
                        int a0 = baseIndex + i * 4 + k, a1 = baseIndex + i * 4 + (k + 1) % 4;
                        int b0 = a0 + 4, b1 = a1 + 4;
                        tris.AddRange(new[] { a0, b0, a1, a1, b0, b1 });
                    }
            }
        var mesh = new Mesh { name = "PowerLine" };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        var go = new GameObject("PowerLine");
        go.transform.SetParent(root, false);
        go.isStatic = true;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = Mat("City_Wire", new Color(0.03f, 0.03f, 0.03f), 0.3f);
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    static void Hydrant(Vector3 p, Vector3 outward)
    {
        var paint = Rand() < 0.7f ? Mat("City_HydrantRed", new Color(0.4f, 0.06f, 0.04f), 0.35f)
                                  : Mat("City_HydrantYellow", new Color(0.5f, 0.38f, 0.05f), 0.35f);
        var t = Prop("Hydrant", p, outward);
        StepOver(Part(t, PrimitiveType.Cylinder, "Hydrant_Body", new Vector3(0f, 0.35f, 0f), new Vector3(0.28f, 0.35f, 0.28f), paint, true));
        Part(t, PrimitiveType.Sphere, "Hydrant_Cap", new Vector3(0f, 0.72f, 0f), new Vector3(0.3f, 0.2f, 0.3f), paint, false);
        Part(t, PrimitiveType.Cylinder, "Hydrant_Nozzle", new Vector3(0f, 0.48f, 0.17f), new Vector3(0.12f, 0.06f, 0.12f), paint, false)
            .localRotation = Quaternion.Euler(90f, 0f, 0f);
        Part(t, PrimitiveType.Cylinder, "Hydrant_Nozzle", new Vector3(0f, 0.48f, 0f), new Vector3(0.1f, 0.22f, 0.1f), paint, false)
            .localRotation = Quaternion.Euler(0f, 0f, 90f);
    }

    static void TrashCan(Vector3 p)
    {
        var green = Mat("City_BinGreen", new Color(0.07f, 0.13f, 0.08f), 0.3f);
        var t = Prop("TrashCan", p, Quaternion.Euler(0f, Range(0f, 360f), 0f) * Vector3.forward);
        bool tipped = Rand() < 0.15f;
        if (tipped)
        {
            t.localRotation *= Quaternion.Euler(90f, 0f, 0f);
            t.localPosition += Vector3.up * 0.28f;
        }
        StepOver(Part(t, PrimitiveType.Cylinder, "Bin_Body", new Vector3(0f, 0.45f, 0f), new Vector3(0.55f, 0.45f, 0.55f), green, true));
        Part(t, PrimitiveType.Cylinder, "Bin_Lid", new Vector3(0f, 0.92f, 0f), new Vector3(0.6f, 0.03f, 0.6f),
             Mat("City_Metal", new Color(0.1f, 0.1f, 0.11f), 0.4f), false);
    }

    static void Mailbox(Vector3 p, Vector3 outward)
    {
        var blue = Mat("City_MailBlue", new Color(0.05f, 0.09f, 0.22f), 0.4f);
        var t = Prop("Mailbox", p, outward);
        StepOver(Part(t, PrimitiveType.Cube, "Mailbox_Body", new Vector3(0f, 0.6f, 0f), new Vector3(0.5f, 0.85f, 0.45f), blue, true));
        Part(t, PrimitiveType.Cylinder, "Mailbox_Top", new Vector3(0f, 1.02f, 0f), new Vector3(0.5f, 0.225f, 0.45f), blue, false)
            .localRotation = Quaternion.Euler(0f, 0f, 90f);
        Part(t, PrimitiveType.Cube, "Mailbox_Slot", new Vector3(0f, 0.92f, 0.23f), new Vector3(0.3f, 0.04f, 0.02f),
             Mat("City_Metal", new Color(0.1f, 0.1f, 0.11f), 0.4f), false);
    }

    static void NewspaperBox(Vector3 p, Vector3 outward)
    {
        Color[] colors = { new Color(0.35f, 0.06f, 0.05f), new Color(0.06f, 0.12f, 0.3f), new Color(0.5f, 0.4f, 0.08f), new Color(0.3f, 0.3f, 0.3f) };
        int c = (int)(Rand() * colors.Length) % colors.Length;
        var t = Prop("NewspaperBox", p, outward);
        StepOver(Part(t, PrimitiveType.Cube, "Newsbox_Body", new Vector3(0f, 0.5f, 0f), new Vector3(0.45f, 1f, 0.4f),
                      Mat($"City_Newsbox{c}", colors[c], 0.4f), true));
        Part(t, PrimitiveType.Cube, "Newsbox_Window", new Vector3(0f, 0.7f, 0.205f), new Vector3(0.35f, 0.3f, 0.01f),
             Mat("City_CarGlass", new Color(0.02f, 0.025f, 0.03f), 0.9f), false);
    }

    static void ParkingMeter(Vector3 p, Vector3 outward)
    {
        var metal = Mat("City_Metal", new Color(0.1f, 0.1f, 0.11f), 0.4f);
        var t = Prop("ParkingMeter", p, outward);
        Part(t, PrimitiveType.Cylinder, "Meter_Pole", new Vector3(0f, 0.55f, 0f), new Vector3(0.06f, 0.55f, 0.06f), metal, false);
        Part(t, PrimitiveType.Cube, "Meter_Head", new Vector3(0f, 1.22f, 0f), new Vector3(0.18f, 0.28f, 0.14f),
             Mat("City_MeterGrey", new Color(0.3f, 0.3f, 0.32f), 0.5f), false);
    }

    // Shelter against the back of the sidewalk, open to the street, bus stop sign at the curb
    static void BusStop(Vector3 curbEdge, Vector3 along, Vector3 outward)
    {
        var metal = Mat("City_Metal", new Color(0.1f, 0.1f, 0.11f), 0.4f);
        var glass = Mat("City_ShelterGlass", new Color(0.05f, 0.06f, 0.07f), 0.9f);
        var t = Prop("BusStop", curbEdge - outward * 1.25f, outward);   // shelter centre, front facing the street
        StepOver(Part(t, PrimitiveType.Cube, "Shelter_Back", new Vector3(0f, 1.2f, -0.6f), new Vector3(3f, 2.1f, 0.05f), glass, true));
        StepOver(Part(t, PrimitiveType.Cube, "Shelter_Side", new Vector3(-1.5f, 1.2f, -0.15f), new Vector3(0.05f, 2.1f, 0.9f), glass, true));
        StepOver(Part(t, PrimitiveType.Cube, "Shelter_Side", new Vector3(1.5f, 1.2f, -0.15f), new Vector3(0.05f, 2.1f, 0.9f), glass, true));
        Part(t, PrimitiveType.Cube, "Shelter_Roof", new Vector3(0f, 2.3f, -0.1f), new Vector3(3.2f, 0.08f, 1.3f), metal, false);
        foreach (float x in new[] { -1.5f, 1.5f })
            Part(t, PrimitiveType.Cube, "Shelter_Post", new Vector3(x, 1.15f, -0.6f), new Vector3(0.08f, 2.3f, 0.08f), metal, false);
        Part(t, PrimitiveType.Cube, "Shelter_Bench", new Vector3(0f, 0.45f, -0.4f), new Vector3(2.4f, 0.06f, 0.35f),
             Mat("City_Wood", new Color(0.25f, 0.17f, 0.1f), 0.2f), false);
        // The sign by the curb
        var sign = Prop("BusStop_Sign", curbEdge - outward * 0.4f + along * 2.3f, outward);
        StepOver(Part(sign, PrimitiveType.Cylinder, "Sign_Pole", new Vector3(0f, 1.3f, 0f), new Vector3(0.07f, 1.3f, 0.07f), metal, true));
        Part(sign, PrimitiveType.Cube, "Sign_Plate", new Vector3(0f, 2.4f, 0f), new Vector3(0.5f, 0.5f, 0.03f),
             Mat("City_SignBlue", new Color(0.08f, 0.15f, 0.35f), 0.4f), false);
    }

    // Stop sign on a post (facing the traffic on 'faceRoad'), sometimes with street name blades on top
    static void StopSign(Vector3 p, Vector3 faceRoad, bool nameBlades)
    {
        var metal = Mat("City_Metal", new Color(0.1f, 0.1f, 0.11f), 0.4f);
        var t = Prop("StopSign", p, faceRoad);
        StepOver(Part(t, PrimitiveType.Cylinder, "Sign_Pole", new Vector3(0f, 1.4f, 0f), new Vector3(0.07f, 1.4f, 0.07f), metal, true));
        var face = Part(t, PrimitiveType.Cylinder, "Sign_Stop", new Vector3(0f, 2.2f, 0.05f), new Vector3(0.75f, 0.015f, 0.75f),
                        Mat("City_StopRed", new Color(0.45f, 0.04f, 0.03f), 0.4f), false);
        face.localRotation = Quaternion.Euler(90f, 0f, 22.5f);
        if (Rand() < 0.2f) t.localRotation *= Quaternion.Euler(0f, 0f, Range(-8f, 8f));   // knocked crooked
        if (!nameBlades) return;
        var green = Mat("City_SignGreen", new Color(0.04f, 0.2f, 0.1f), 0.4f);
        Part(t, PrimitiveType.Cube, "Sign_Name", new Vector3(0f, 2.9f, 0f), new Vector3(1.2f, 0.18f, 0.03f), green, false);
        Part(t, PrimitiveType.Cube, "Sign_Name", new Vector3(0f, 3.1f, 0f), new Vector3(0.03f, 0.18f, 1.2f), green, false);
    }

    // Signal pole at a main road corner: one dead signal head facing each road (the power's out)
    static void TrafficLight(Vector3 p, Vector3 outX, Vector3 outZ)
    {
        var metal = Mat("City_Metal", new Color(0.1f, 0.1f, 0.11f), 0.4f);
        var t = Prop("TrafficLight", p, outX);
        Part(t, PrimitiveType.Cylinder, "Signal_Pole", new Vector3(0f, 1.9f, 0f), new Vector3(0.18f, 1.9f, 0.18f), metal, true);
        Color[] lens = { new Color(0.15f, 0.02f, 0.02f), new Color(0.15f, 0.1f, 0.02f), new Color(0.02f, 0.12f, 0.05f) };
        foreach (var face in new[] { outX, outZ })
        {
            var head = new GameObject("Signal_Head").transform;
            head.SetParent(t, false);
            head.position = t.TransformPoint(new Vector3(0f, 3.1f, 0f)) + root.TransformDirection(face) * 0.25f;
            head.rotation = Quaternion.LookRotation(root.TransformDirection(face));
            head.gameObject.isStatic = true;
            Part(head, PrimitiveType.Cube, "Signal_Box", Vector3.zero, new Vector3(0.35f, 1f, 0.28f), metal, false);
            for (int i = 0; i < 3; i++)
                Part(head, PrimitiveType.Cylinder, "Signal_Lens", new Vector3(0f, 0.3f - i * 0.3f, 0.15f), new Vector3(0.2f, 0.01f, 0.2f),
                     Mat($"City_Lens{i}", lens[i], 0.9f), false).localRotation = Quaternion.Euler(90f, 0f, 0f);
        }
    }

    static void Cone(Vector3 p, bool fallen)
    {
        var orange = Mat("City_ConeOrange", new Color(0.55f, 0.18f, 0.03f), 0.3f);
        var t = Prop("Cone", p, Quaternion.Euler(0f, Range(0f, 360f), 0f) * Vector3.forward);
        if (fallen) { t.localRotation *= Quaternion.Euler(90f, 0f, 0f); t.localPosition += Vector3.up * 0.15f; }
        Part(t, PrimitiveType.Cube, "Cone_Base", new Vector3(0f, 0.02f, 0f), new Vector3(0.38f, 0.04f, 0.38f),
             Mat("City_Metal", new Color(0.1f, 0.1f, 0.11f), 0.4f), false);
        Part(t, PrimitiveType.Cylinder, "Cone_Body", new Vector3(0f, 0.25f, 0f), new Vector3(0.18f, 0.22f, 0.18f), orange, false);
        Part(t, PrimitiveType.Cylinder, "Cone_Stripe", new Vector3(0f, 0.32f, 0f), new Vector3(0.16f, 0.04f, 0.16f),
             Mat("City_Reflective", new Color(0.6f, 0.6f, 0.58f), 0.6f), false);
    }

    // Striped A-frame barrier across 'across'
    static void RoadBarrier(Vector3 p, Vector3 across)
    {
        var t = Prop("RoadBarrier", p, Vector3.Cross(Vector3.up, across));
        var white = Mat("City_Reflective", new Color(0.6f, 0.6f, 0.58f), 0.6f);
        var red = Mat("City_BarrierRed", new Color(0.45f, 0.05f, 0.04f), 0.4f);
        for (int i = 0; i < 4; i++)   // stripes along the board
            StepOver(Part(t, PrimitiveType.Cube, "Barrier_Board", new Vector3(-0.6f + i * 0.4f, 0.85f, 0f), new Vector3(0.4f, 0.22f, 0.04f),
                          i % 2 == 0 ? white : red, true));
        foreach (float x in new[] { -0.7f, 0.7f })
            foreach (float z in new[] { -0.2f, 0.2f })
                Part(t, PrimitiveType.Cube, "Barrier_Leg", new Vector3(x, 0.45f, z * 0.5f), new Vector3(0.05f, 0.95f, 0.05f), white, false)
                    .localRotation = Quaternion.Euler(z > 0f ? 12f : -12f, 0f, 0f);
    }
}
