using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

// Task 5: three clues about tonight's monster, out in the streets near where the players start, for them to find
// during prep. Each one can lie (Inspector chance), so no single clue is proof.
//  1. A wall: Climber = claw gouges high up a wall. Giant = a front door torn off its hinges, a huge grimy
//     handprint above it.
//  2. A dead dog on the pavement: eyes gone = it hunts by sight; eyes untouched = it doesn't.
//  3. A radio on the pavement, hissing static: get close and a garbled broadcast (subtitles) hints at how it
//     senses OR how it gets in (picked at random).
// Where the clues go comes from the city seed, so every machine places them the same. Only the host knows the
// monster (GenomeManager), so it decides what each clue shows and syncs that in one number.
// Place ONE of these on the same scene object as GameFlow (it needs the NetworkObject).
public class ClueManager : NetworkBehaviour
{
    [Header("Where")]
    [Tooltip("Clues go on buildings / pavements between these distances from where the players start (metres).")]
    public Vector2 distanceFromSpawn = new Vector2(18f, 70f);

    [Header("Unreliable clues")]
    [Range(0f, 1f), Tooltip("Chance the wall clue shows the wrong monster.")]
    public float wallLieChance = 0.15f;
    [Range(0f, 1f), Tooltip("Chance the carcass shows the wrong sense.")]
    public float carcassLieChance = 0.15f;
    [Range(0f, 1f), Tooltip("Chance the radio says the wrong thing.")]
    public float radioLieChance = 0.2f;

    [Header("Radio")]
    [Tooltip("You read the broadcast within this distance.")]
    public float radioHearRange = 6f;
    [Tooltip("The static is heard this far away.")]
    public float radioSoundRange = 18f;
    [Range(0f, 1f), Tooltip("Static volume (AudioMix.cs: radio static -30 dB).")]
    public float radioVolume = 0.35f;
    [Tooltip("Seconds between broadcasts.")]
    public float broadcastEvery = 14f;
    [Tooltip("Seconds each broadcast's subtitle stays up.")]
    public float broadcastShowTime = 6f;

    // bit 0: decided, bit 1: wall shows a Climber, bit 2: carcass eyes gone (sight), bit 3: radio talks about the
    // access (else the sense), bit 4: radio says climber / sight
    readonly NetworkVariable<int> netClues = new NetworkVariable<int>(0);

    // Broadcast lines: [topic, value] -> a few variants. Words in ~tildes~ get eaten by the static.
    static readonly string[][] Lines =
    {
        // sense, hearing (value false)
        new[] { "...don't ~make~ a sound... it found ~us~ by the noise... the generator, it ~heard~ the generator...",
                "...whisper... ~it~ hears everything... we ~kept~ quiet and it walked right ~past~..." },
        // sense, sight (value true)
        new[] { "...lights off... ~turn~ your lights off... it ~saw~ the torches from the ~end~ of the street...",
                "...stay in the ~dark~... it watched the ~windows~... anyone in the light, it ~saw~..." },
        // access, giant (value false)
        new[] { "...taller than the ~houses~... it bent down and ~reached~ in through the door... ~the~ door...",
                "...its arm ~came~ through the front door... ~bar~ the doors, it can't get ~inside~..." },
        // access, climber (value true)
        new[] { "...on the ~roofs~... it went straight ~up~ the wall... came in from ~above~...",
                "...it climbs... we ~heard~ it on the roof... the hatch, ~lock~ the ~hatch~..." },
    };

    public static ClueManager Instance { get; private set; }

    int builtSeed = int.MinValue;
    Transform clueRoot;
    Transform radio;
    AudioSource radioAudio;
    string radioLine = "";
    float broadcastStart = -999f;

    void Awake() => Instance = this;

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    void Update()
    {
        if (!IsSpawned || CityManager.Current == null) return;

        // Host: decide once tonight's monster is known
        if (IsServer && netClues.Value == 0 && GenomeManager.Current != null) Decide(GenomeManager.Current);

        // Everyone: build them once per city, as soon as the host has decided
        if (netClues.Value != 0 && builtSeed != CityManager.Current.seed) Build(CityManager.Current, netClues.Value);

        UpdateRadio();
    }

    public override void OnNetworkDespawn()
    {
        if (clueRoot != null) Destroy(clueRoot.gameObject);
        builtSeed = int.MinValue;
    }

    void Decide(MonsterGenome g)
    {
        bool climber = g.access == GenomeAccess.Climber, sight = g.sense == GenomeSense.Sight;
        bool wallClimber = Random.value < wallLieChance ? !climber : climber;
        bool eyesGone = Random.value < carcassLieChance ? !sight : sight;
        bool aboutAccess = Random.value < 0.5f;
        bool truth = aboutAccess ? climber : sight;
        bool said = Random.value < radioLieChance ? !truth : truth;
        netClues.Value = 1 | (wallClimber ? 2 : 0) | (eyesGone ? 4 : 0) | (aboutAccess ? 8 : 0) | (said ? 16 : 0);
    }

    // ---------- Building the clues (every machine, same places from the city seed) ----------

    void Build(CityGenerator.City city, int bits)
    {
        builtSeed = city.seed;
        if (clueRoot != null) Destroy(clueRoot.gameObject);
        clueRoot = new GameObject("Clues").transform;
        clueRoot.SetParent(city.root.transform, true);

        var rng = new System.Random(city.seed ^ 0x5eed);
        var pal = FindAnyObjectByType<CityManager>()?.settings?.palette;

        // Buildings at a good distance from the spawn, in a fixed order (city order), then shuffled by the seed
        var near = new List<GameObject>();
        foreach (var b in city.buildings)
        {
            float d = Vector3.Distance(b.transform.position, city.playerSpawn);
            if (d >= distanceFromSpawn.x && d <= distanceFromSpawn.y && b.GetComponentInChildren<Door>() != null) near.Add(b);
        }
        if (near.Count < 3) near = new List<GameObject>(city.buildings);
        for (int i = near.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (near[i], near[j]) = (near[j], near[i]); }
        if (near.Count < 3) return;

        WallClue(near[0], (bits & 2) != 0, pal, rng);
        Carcass(near[1], (bits & 4) != 0, pal, rng);
        Radio(near[2], (bits & 8) != 0, (bits & 16) != 0, rng);
    }

    // On the outside face of a building's front wall (the door sits in the middle of the wall's thickness);
    // -forward is out to the street
    static Vector3 FrontPoint(GameObject b, float along, float up)
    {
        var door = b.GetComponentInChildren<Door>();
        Vector3 basePos = door != null ? door.transform.position : b.transform.position;
        return basePos + b.transform.right * along + Vector3.up * up - b.transform.forward * 0.16f;
    }

    void WallClue(GameObject b, bool climber, BuildingPalette pal, System.Random rng)
    {
        var door = b.GetComponentInChildren<Door>();
        float wallTop = WallHeight(b);
        if (climber)
        {
            // Two sets of gouges high on the front wall, beside the door
            for (int i = 0; i < 2; i++)
            {
                float side = (i == 0 ? -1f : 1f) * (1.8f + (float)rng.NextDouble());
                float y = Mathf.Max(3f, wallTop - 1.4f - (float)rng.NextDouble() * 0.8f);
                Decal(pal, ProcTextures.Decal.Claws, FrontPoint(b, side, y), -b.transform.forward, new Vector2(0.9f, 1.3f), (float)rng.NextDouble() * 20f - 10f);
            }
        }
        else
        {
            // Door torn off its hinges (host; synced by DoorManager) and a giant handprint above the doorway
            if (IsServer && DoorManager.Instance != null && door != null) DoorManager.Instance.Rip(door.Center);
            float y = Mathf.Min(wallTop - 1.1f, 4.2f);
            Decal(pal, ProcTextures.Decal.Handprint, FrontPoint(b, 0.3f, y), -b.transform.forward, new Vector2(2.2f, 2.2f), (float)rng.NextDouble() * 30f - 15f);
        }
    }

    static float WallHeight(GameObject b)
    {
        float top = 4f;
        foreach (Transform c in b.transform)
            if (c.name.StartsWith("Wall_Back") && c.TryGetComponent(out BoxCollider _))
                top = Mathf.Max(top, c.localPosition.y + c.localScale.y / 2f);
        return top;
    }

    void Decal(BuildingPalette pal, ProcTextures.Decal kind, Vector3 pos, Vector3 normal, Vector2 size, float roll)
    {
        if (pal == null || pal.decalMaterial == null) return;
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        q.name = "Clue_" + kind;
        Destroy(q.GetComponent<Collider>());
        q.transform.SetParent(clueRoot, false);
        q.transform.SetPositionAndRotation(pos + normal * 0.03f, Quaternion.LookRotation(-normal) * Quaternion.Euler(0f, 0f, roll));
        q.transform.localScale = new Vector3(size.x, size.y, 1f);
        var r = q.GetComponent<MeshRenderer>();
        r.sharedMaterial = BuildingDressing.DecalMaterial(pal, ProcTextures.DecalTexture(kind, 0));
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    // A dead dog on the pavement in front of a building, in a pool of blood. Eyes gone or still there.
    void Carcass(GameObject b, bool eyesGone, BuildingPalette pal, System.Random rng)
    {
        Vector3 spot = FrontPoint(b, (rng.NextDouble() < 0.5 ? -1f : 1f) * 2.5f, 0f) - b.transform.forward * 1.2f;
        if (Physics.Raycast(spot + Vector3.up * 2f, Vector3.down, out var hit, 4f)) spot = hit.point;
        var dog = new GameObject("Clue_Carcass").transform;
        dog.SetParent(clueRoot, false);
        dog.SetPositionAndRotation(spot, Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f));

        var fur = BuildingBuilder.Fallback("Clue_Fur", new Color(0.24f, 0.18f, 0.12f), Color.black, 0.15f);
        var flesh = BuildingBuilder.Fallback("Clue_Flesh", new Color(0.35f, 0.04f, 0.04f), Color.black, 0.5f);
        var eyeWhite = BuildingBuilder.Fallback("Clue_Eye", new Color(0.85f, 0.8f, 0.65f), Color.black, 0.8f);
        var pupil = BuildingBuilder.Fallback("Clue_Pupil", new Color(0.02f, 0.02f, 0.02f), Color.black, 0.9f);

        // Lying on its side: body, head, snout, legs sticking out, a gash in the belly
        Bit(dog, PrimitiveType.Capsule, "Body", new Vector3(0f, 0.2f, 0f), new Vector3(0f, 0f, 90f), new Vector3(0.38f, 0.5f, 0.32f), fur);
        Bit(dog, PrimitiveType.Sphere, "Head", new Vector3(0.62f, 0.17f, 0.05f), Vector3.zero, new Vector3(0.28f, 0.24f, 0.26f), fur);
        Bit(dog, PrimitiveType.Capsule, "Snout", new Vector3(0.82f, 0.13f, 0.05f), new Vector3(0f, 0f, 90f), new Vector3(0.12f, 0.1f, 0.12f), fur);
        Bit(dog, PrimitiveType.Sphere, "Ear", new Vector3(0.56f, 0.28f, -0.06f), Vector3.zero, new Vector3(0.1f, 0.05f, 0.1f), fur);
        Bit(dog, PrimitiveType.Capsule, "Tail", new Vector3(-0.62f, 0.12f, 0f), new Vector3(0f, 30f, 80f), new Vector3(0.06f, 0.18f, 0.06f), fur);
        foreach (var (x, z, a) in new[] { (0.3f, 0.2f, 20f), (0.22f, 0.26f, 40f), (-0.32f, 0.22f, 10f), (-0.4f, 0.28f, 35f) })
            Bit(dog, PrimitiveType.Capsule, "Leg", new Vector3(x, 0.12f, z), new Vector3(90f - a, 0f, 0f), new Vector3(0.08f, 0.2f, 0.08f), fur);
        Bit(dog, PrimitiveType.Sphere, "Wound", new Vector3(-0.05f, 0.24f, 0.17f), Vector3.zero, new Vector3(0.36f, 0.14f, 0.06f), flesh);

        // The upturned eye (and the other one, just visible): whole, or a dark red empty socket
        foreach (float side in new[] { 1f, -1f })
        {
            Vector3 eye = new Vector3(0.72f, 0.22f, 0.05f + side * 0.1f);
            if (eyesGone) Bit(dog, PrimitiveType.Sphere, "Socket", eye, Vector3.zero, new Vector3(0.065f, 0.06f, 0.04f), flesh);
            else
            {
                Bit(dog, PrimitiveType.Sphere, "Eye", eye, Vector3.zero, new Vector3(0.06f, 0.055f, 0.04f), eyeWhite);
                Bit(dog, PrimitiveType.Sphere, "Pupil", eye + new Vector3(0.015f, 0f, side * 0.015f), Vector3.zero, new Vector3(0.03f, 0.03f, 0.025f), pupil);
            }
        }

        // Blood pool under it (a decal lying flat)
        if (pal != null && pal.decalMaterial != null)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = "Clue_Blood";
            Destroy(q.GetComponent<Collider>());
            q.transform.SetParent(dog, false);
            q.transform.localPosition = new Vector3(0.1f, 0.012f, 0.15f);
            q.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            q.transform.localScale = new Vector3(1.8f, 1.4f, 1f);
            q.GetComponent<MeshRenderer>().sharedMaterial = BuildingDressing.DecalMaterial(pal, ProcTextures.DecalTexture(ProcTextures.Decal.Blood, 0));
        }
    }

    static void Bit(Transform parent, PrimitiveType type, string name, Vector3 pos, Vector3 euler, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localRotation = Quaternion.Euler(euler);
        go.transform.localScale = scale;
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
    }

    // ---------- Radio ----------

    void Radio(GameObject b, bool aboutAccess, bool said, System.Random rng)
    {
        int topic = (aboutAccess ? 2 : 0) + (said ? 1 : 0);
        radioLine = Lines[topic][rng.Next(Lines[topic].Length)];

        Vector3 spot = FrontPoint(b, (rng.NextDouble() < 0.5 ? -1f : 1f) * 3f, 0f) - b.transform.forward * 0.6f;
        if (Physics.Raycast(spot + Vector3.up * 2f, Vector3.down, out var hit, 4f)) spot = hit.point;
        radio = new GameObject("Clue_Radio").transform;
        radio.SetParent(clueRoot, false);
        radio.SetPositionAndRotation(spot, Quaternion.LookRotation(-b.transform.forward) * Quaternion.Euler(0f, (float)rng.NextDouble() * 50f - 25f, 0f));

        var casing = BuildingBuilder.Fallback("Clue_Radio", new Color(0.18f, 0.14f, 0.1f), Color.black, 0.4f);
        var metal = BuildingBuilder.Fallback("Clue_RadioMetal", new Color(0.5f, 0.5f, 0.5f), Color.black, 0.7f);
        var dial = BuildingBuilder.Fallback("Clue_RadioDial", new Color(0.9f, 0.6f, 0.2f), new Color(1f, 0.55f, 0.15f) * 0.8f, 0.6f);
        Bit(radio, PrimitiveType.Cube, "Crate", new Vector3(0f, 0.2f, 0f), Vector3.zero, new Vector3(0.5f, 0.4f, 0.4f),
            BuildingBuilder.Fallback("Clue_Crate", new Color(0.3f, 0.22f, 0.13f), Color.black, 0.1f));
        Bit(radio, PrimitiveType.Cube, "Radio", new Vector3(0f, 0.52f, 0f), Vector3.zero, new Vector3(0.36f, 0.22f, 0.13f), casing);
        Bit(radio, PrimitiveType.Cylinder, "Speaker", new Vector3(-0.08f, 0.52f, -0.066f), new Vector3(90f, 0f, 0f), new Vector3(0.12f, 0.005f, 0.12f), metal);
        Bit(radio, PrimitiveType.Cube, "Dial", new Vector3(0.09f, 0.55f, -0.066f), Vector3.zero, new Vector3(0.1f, 0.05f, 0.005f), dial);
        Bit(radio, PrimitiveType.Cylinder, "Antenna", new Vector3(0.14f, 0.85f, 0.03f), new Vector3(0f, 0f, -12f), new Vector3(0.01f, 0.25f, 0.01f), metal);

        radioAudio = radio.gameObject.AddComponent<AudioSource>();
        radioAudio.clip = StaticClip();
        radioAudio.loop = true;
        AudioMix.Setup3D(radioAudio, 2f, radioSoundRange);
        radioAudio.volume = radioVolume;
        radioAudio.Play();
        broadcastStart = Time.time + 2f;
    }

    // Broadcasts every so often: the static swells and the line comes through (subtitle, when you're close)
    void UpdateRadio()
    {
        if (radio == null || radioAudio == null) return;
        if (Time.time > broadcastStart + broadcastEvery) broadcastStart = Time.time;
        bool talking = Time.time - broadcastStart < broadcastShowTime;
        radioAudio.volume = radioVolume * (talking ? 1.6f : 1f);
        radioAudio.pitch = talking ? 0.85f + Mathf.PerlinNoise(Time.time * 3f, 0f) * 0.3f : 1f;
    }

    void OnGUI()
    {
        if (radio == null || radioLine.Length == 0 || Time.time - broadcastStart > broadcastShowTime || Time.time < broadcastStart) return;
        var me = LocalPlayer();
        if (me == null || me.IsDead || NetworkUI.MenuOpen) return;
        if (Vector3.Distance(me.transform.position, radio.position) > radioHearRange) return;

        // The line comes through word by word; ~words~ are lost in the static
        float shown = Mathf.Clamp01((Time.time - broadcastStart) / (broadcastShowTime * 0.7f));
        var words = radioLine.Split(' ');
        int count = Mathf.CeilToInt(words.Length * shown);
        var text = new System.Text.StringBuilder("RADIO: ");
        for (int i = 0; i < count; i++)
        {
            string w = words[i];
            text.Append(w.Contains("~") ? new string('-', Mathf.Max(3, w.Length - 2)) : w).Append(' ');
        }
        var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 18, wordWrap = true };
        style.normal.textColor = new Color(0.85f, 0.9f, 0.7f);
        GUI.Label(new Rect(Screen.width * 0.15f, Screen.height - 200f, Screen.width * 0.7f, 50f), text.ToString(), style);
    }

    // A few seconds of radio static: hiss, crackle, and a wobbling hum, made in code (no sound file needed)
    static AudioClip staticClip;
    static AudioClip StaticClip()
    {
        if (staticClip != null) return staticClip;
        const int rate = 22050, seconds = 4;
        var data = new float[rate * seconds];
        var r = new System.Random(77);
        float low = 0f;
        for (int i = 0; i < data.Length; i++)
        {
            float t = i / (float)rate;
            float white = (float)r.NextDouble() * 2f - 1f;
            low += (white - low) * 0.15f;                                    // softer hiss
            float crackle = r.NextDouble() < 0.0008 ? ((float)r.NextDouble() * 2f - 1f) * 3f : 0f;
            float hum = Mathf.Sin(t * Mathf.PI * 2f * (180f + Mathf.Sin(t * 1.3f) * 25f)) * 0.15f;
            float swell = 0.6f + 0.4f * Mathf.PerlinNoise(t * 2f, 0.5f);
            data[i] = Mathf.Clamp((low * 0.9f * swell + crackle + hum * (1f - swell)) * 0.6f, -1f, 1f);
        }
        // Fade the ends together so the loop doesn't click
        for (int i = 0; i < 400; i++) { float k = i / 400f; data[i] *= k; data[data.Length - 1 - i] *= k; }
        staticClip = AudioClip.Create("RadioStatic", data.Length, 1, rate, false);
        staticClip.SetData(data, 0);
        return staticClip;
    }

    static FirstPersonController LocalPlayer()
    {
        foreach (var p in FirstPersonController.All) if (p.IsOwner) return p;
        return null;
    }
}
