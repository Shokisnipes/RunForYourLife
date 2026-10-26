using UnityEditor;
using UnityEngine;

// Adds the things that make a box look like a building:
// windows (some lit, a few flickering), a roof parapet, rooftop clutter, and sidewalks.
// Used by the Building Generator, and by Tools -> RunForYourLife -> Upgrade Existing Buildings.
public static class BuildingDetails
{
    public struct Options
    {
        public bool windows, parapet, roofClutter, sidewalk;
        public float litChance, flickerChance, sidewalkWidth;

        public static Options Default => new Options
        {
            windows = true, parapet = true, roofClutter = true, sidewalk = true,
            litChance = 0.2f, flickerChance = 0.08f, sidewalkWidth = 2f,
        };
    }

    const float Storey = 3.2f;
    const float WindowW = 1.0f, WindowH = 1.3f;
    const float WindowSpacing = 2.6f;

    public static void Add(Transform building, float width, float depth, float height, float wallT,
                           float doorOffset, float doorWidth, Options o)
    {
        var details = new GameObject("Details").transform;
        details.SetParent(building, false);

        float halfW = width / 2f, halfD = depth / 2f;

        if (o.windows)
        {
            WindowsOnFace(details, Vector3.back,    new Vector3(0, 0, -halfD), Vector3.right,   width, height, o, true,  doorOffset, doorWidth);
            WindowsOnFace(details, Vector3.forward, new Vector3(0, 0,  halfD), Vector3.right,   width, height, o, false, 0, 0);
            WindowsOnFace(details, Vector3.left,    new Vector3(-halfW, 0, 0), Vector3.forward, depth, height, o, false, 0, 0);
            WindowsOnFace(details, Vector3.right,   new Vector3( halfW, 0, 0), Vector3.forward, depth, height, o, false, 0, 0);
        }

        float roofTop = height + wallT;

        if (o.parapet)
        {
            const float ph = 0.6f, pt = 0.2f;
            float y = roofTop + ph / 2f;
            Part(details, "Wall_Parapet_F", PrimitiveType.Cube, new Vector3(0, y, -halfD + pt / 2f), new Vector3(width, ph, pt), null, true);
            Part(details, "Wall_Parapet_B", PrimitiveType.Cube, new Vector3(0, y,  halfD - pt / 2f), new Vector3(width, ph, pt), null, true);
            Part(details, "Wall_Parapet_L", PrimitiveType.Cube, new Vector3(-halfW + pt / 2f, y, 0), new Vector3(pt, ph, depth - pt * 2f), null, true);
            Part(details, "Wall_Parapet_R", PrimitiveType.Cube, new Vector3( halfW - pt / 2f, y, 0), new Vector3(pt, ph, depth - pt * 2f), null, true);
        }

        if (o.roofClutter) RoofClutter(details, width, depth, roofTop);

        if (o.sidewalk)
        {
            float sw = o.sidewalkWidth, sh = 0.15f;
            var mat = Mat("Sidewalk", new Color(0.42f, 0.42f, 0.42f), Color.black, 0.15f);
            Part(details, "Sidewalk_F", PrimitiveType.Cube, new Vector3(0, sh / 2f, -halfD - sw / 2f), new Vector3(width + sw * 2f, sh, sw), mat, true);
            Part(details, "Sidewalk_B", PrimitiveType.Cube, new Vector3(0, sh / 2f,  halfD + sw / 2f), new Vector3(width + sw * 2f, sh, sw), mat, true);
            Part(details, "Sidewalk_L", PrimitiveType.Cube, new Vector3(-halfW - sw / 2f, sh / 2f, 0), new Vector3(sw, sh, depth), mat, true);
            Part(details, "Sidewalk_R", PrimitiveType.Cube, new Vector3( halfW + sw / 2f, sh / 2f, 0), new Vector3(sw, sh, depth), mat, true);
        }
    }

    static void WindowsOnFace(Transform parent, Vector3 normal, Vector3 faceCenter, Vector3 along, float length, float height,
                              Options o, bool isFront, float doorOffset, float doorWidth)
    {
        int cols = Mathf.FloorToInt((length - 1.2f) / WindowSpacing);
        if (cols < 1) { if (length > 2.2f) cols = 1; else return; }
        float spacing = length / cols;

        int rows = Mathf.Max(1, Mathf.FloorToInt((height - 0.6f) / Storey));
        Quaternion rot = Quaternion.LookRotation(normal);

        var frameMat = Mat("Window_Frame", new Color(0.08f, 0.08f, 0.09f), Color.black, 0.3f);
        var darkMat = Mat("Window_Dark", new Color(0.03f, 0.035f, 0.045f), Color.black, 0.92f);
        var litMat = Mat("Window_Lit", new Color(1f, 0.85f, 0.6f), new Color(1f, 0.72f, 0.42f) * 1.6f, 0.6f);

        for (int r = 0; r < rows; r++)
        {
            float y = r * Storey + 1.75f;
            if (y + WindowH / 2f > height - 0.3f) break;

            for (int c = 0; c < cols; c++)
            {
                float s = -length / 2f + spacing * (c + 0.5f);
                if (isFront && r == 0 && Mathf.Abs(s - doorOffset) < doorWidth / 2f + 0.9f) continue;   // keep clear of the door

                Vector3 pos = faceCenter + along * s + Vector3.up * y;
                bool lit = Random.value < o.litChance;

                var frame = Part(parent, "Window_Frame", PrimitiveType.Cube, pos + normal * 0.025f,
                                 new Vector3(WindowW + 0.16f, WindowH + 0.16f, 0.05f), frameMat, false);
                frame.localRotation = rot;

                var glass = Part(parent, lit ? "Window_Lit" : "Window_Dark", PrimitiveType.Cube, pos + normal * 0.045f,
                                 new Vector3(WindowW, WindowH, 0.03f), lit ? litMat : darkMat, false);
                glass.localRotation = rot;
                if (lit && Random.value < o.flickerChance / Mathf.Max(o.litChance, 0.01f))
                    glass.gameObject.AddComponent<WindowFlicker>();
            }
        }
    }

    static void RoofClutter(Transform parent, float width, float depth, float roofTop)
    {
        var metal = Mat("Prop_Metal", new Color(0.35f, 0.36f, 0.37f), Color.black, 0.35f);
        int count = Random.Range(1, 4) + (width * depth > 120f ? 1 : 0);
        float mx = Mathf.Max(0.5f, width / 2f - 1.2f), mz = Mathf.Max(0.5f, depth / 2f - 1.2f);

        for (int i = 0; i < count; i++)
        {
            Vector3 p = new Vector3(Random.Range(-mx, mx), 0f, Random.Range(-mz, mz));
            switch (Random.Range(0, 4))
            {
                case 0: // AC unit
                    Part(parent, "Prop_AC", PrimitiveType.Cube, p + Vector3.up * (roofTop + 0.45f), new Vector3(1.4f, 0.9f, 1.0f), metal, false);
                    break;
                case 1: // water tank
                    Part(parent, "Prop_Tank", PrimitiveType.Cylinder, p + Vector3.up * (roofTop + 0.9f), new Vector3(1.6f, 0.9f, 1.6f), metal, false);
                    break;
                case 2: // vent
                    Part(parent, "Prop_Vent", PrimitiveType.Cube, p + Vector3.up * (roofTop + 0.3f), new Vector3(0.5f, 0.6f, 0.5f), metal, false);
                    break;
                default: // antenna
                    Part(parent, "Prop_Antenna", PrimitiveType.Cylinder, p + Vector3.up * (roofTop + 1.4f), new Vector3(0.06f, 1.4f, 0.06f), metal, false);
                    break;
            }
        }
    }

    static Transform Part(Transform parent, string name, PrimitiveType type, Vector3 localPos, Vector3 scale, Material mat, bool keepCollider)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = scale;
        go.isStatic = true;
        if (!keepCollider) Object.DestroyImmediate(go.GetComponent<Collider>());
        if (mat != null) go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        if (type == PrimitiveType.Cube && !name.StartsWith("Window")) go.AddComponent<BoxUV>();
        return go.transform;
    }

    public static Material Mat(string name, Color color, Color emission, float smoothness)
    {
        if (!AssetDatabase.IsValidFolder("Assets/Materials")) AssetDatabase.CreateFolder("Assets", "Materials");
        string path = $"Assets/Materials/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) return mat;

        mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetColor("_BaseColor", color);
        mat.SetFloat("_Smoothness", smoothness);
        if (emission != Color.black)
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", emission);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    // ---------- Upgrade buildings made before details existed ----------

    [MenuItem("Tools/RunForYourLife/Upgrade Existing Buildings")]
    static void UpgradeExisting()
    {
        int upgraded = 0;
        foreach (var top in Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (top.name != "Wall_Front_Top" || top.parent == null) continue;
            var b = top.parent;
            if (b.Find("Details") != null) continue;

            var back = b.Find("Wall_Back");
            var left = b.Find("Wall_Left");
            if (back == null || left == null) continue;

            float width = back.localScale.x;
            float height = back.localScale.y;
            float t = back.localScale.z;
            float depth = left.localScale.z + t * 2f;
            float doorOffset = top.localPosition.x;
            float doorWidth = top.localScale.x;

            Add(b, width, depth, height, t, doorOffset, doorWidth, Options.Default);
            Undo.RegisterCreatedObjectUndo(b.Find("Details").gameObject, "Upgrade Building");
            upgraded++;
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"Upgraded {upgraded} building(s). Re-bake the NavMesh, then run the Building Texturer again.");
    }
}
