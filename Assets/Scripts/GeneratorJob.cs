using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

// Night job: a generator next to the building you start at powers the street lamps around it. It runs dry every
// few minutes (only at night) and the lamps die with it. Fuel cans lie around the city, out in the open: press E to
// pick one up. Alone you carry it at half speed and it sloshes (the monsters hear it); a second player presses E on
// it to help and you both move almost normally (stay close or they let go). Hold E at the generator to pour it in;
// the generator starts up again (loud). G drops the can.
// The host owns everything (fuel, who holds what); every machine shows the cans, lamps and sounds from synced state.
// Place ONE of these on the same scene object as GameFlow (it needs the NetworkObject).
public class GeneratorJob : NetworkBehaviour
{
    [Header("Prefabs (Tools > Vehicle Setup makes them)")]
    public GameObject generatorPrefab;
    public GameObject canPrefab;

    [Header("Generator")]
    [Tooltip("Street lamps within this distance of the generator are on its circuit (metres).")]
    public float powerRadius = 40f;
    [Tooltip("Fuel in the tank when the run starts (seconds of running at night).")]
    public float startFuel = 150f;
    [Tooltip("Seconds of running each can adds.")]
    public float fuelPerCan = 180f;
    [Tooltip("Most fuel the tank holds (seconds).")]
    public float maxFuel = 300f;
    [Tooltip("Starting up again after a refuel is heard this far away (metres).")]
    public float startNoiseRadius = 25f;

    [Header("Fuel cans")]
    [Tooltip("Cans placed around the city each run.")]
    public int canCount = 3;
    [Tooltip("Cans lie at least this far from the generator (metres).")]
    public float minCanDistance = 40f;
    [Tooltip("...and at most this far (metres).")]
    public float maxCanDistance = 140f;
    [Tooltip("How close you must be to pick up or help carry a can (metres).")]
    public float useRange = 2.2f;
    [Tooltip("How close to the generator you must be to refuel it (metres).")]
    public float refuelRange = 2.5f;
    [Tooltip("Seconds of holding E to pour a can in.")]
    public float refuelSeconds = 3f;

    [Header("Carrying")]
    [Range(0.1f, 1f), Tooltip("Speed while carrying a can alone (1 = normal). No sprinting.")]
    public float soloSpeed = 0.5f;
    [Range(0.1f, 1f), Tooltip("Speed while two players carry it. No sprinting.")]
    public float pairSpeed = 0.9f;
    [Tooltip("The helper lets go when the two carriers get further apart than this (metres).")]
    public float pairBreakDistance = 3f;
    [Tooltip("Carrying alone and moving: the sloshing is heard this far away (metres).")]
    public float sloshNoiseRadius = 14f;
    [Tooltip("How often the sloshing makes that noise (seconds).")]
    public float sloshNoiseInterval = 1.2f;

    [Header("Sounds (levels in AudioMix.cs)")]
    public AudioClip humClip;
    [Range(0f, 1f), Tooltip("Running loop, -26 dB.")] public float humVolume = 0.2f;
    public AudioClip startClip;
    [Range(0f, 1f), Tooltip("Starting up, -18 dB.")] public float startVolume = 0.46f;
    public AudioClip stallClip;
    [Range(0f, 1f), Tooltip("Sputtering out, -20 dB.")] public float stallVolume = 0.49f;
    public AudioClip pourClip;
    [Range(0f, 1f), Tooltip("Pouring fuel, -28 dB.")] public float pourVolume = 0.23f;
    [Tooltip("Sloshing while one player carries a can and moves (3D, from the can: the carrier and anyone close hear it).")]
    public AudioClip sloshClip;
    [Range(0f, 1f), Tooltip("Sloshing, -27 dB (just above footsteps).")] public float sloshVolume = 0.56f;
    [Tooltip("Sloshing fades out at this distance (metres), about as far as the monsters hear it.")]
    public float sloshRange = 15f;
    [Tooltip("Generator sounds fade out at this distance (metres).")]
    public float soundRange = 60f;

    public const ulong Nobody = ulong.MaxValue;

    public struct FuelCan : INetworkSerializable, System.IEquatable<FuelCan>
    {
        public Vector3 pos;    // where it lies (when nobody holds it)
        public ulong a, b;     // holders' client ids (Nobody = free hand)
        public bool used;      // poured into the generator: gone

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref pos);
            s.SerializeValue(ref a);
            s.SerializeValue(ref b);
            s.SerializeValue(ref used);
        }

        public bool Equals(FuelCan o) => pos == o.pos && a == o.a && b == o.b && used == o.used;
        public bool Carried => a != Nobody;
        public bool Holds(ulong id) => id != Nobody && (a == id || b == id);
    }

    readonly NetworkList<FuelCan> cans = new NetworkList<FuelCan>();
    readonly NetworkVariable<Vector3> genPos = new NetworkVariable<Vector3>();
    readonly NetworkVariable<float> genYaw = new NetworkVariable<float>();
    readonly NetworkVariable<bool> running = new NetworkVariable<bool>(true);
    readonly NetworkVariable<float> fuel = new NetworkVariable<float>();   // seconds left (host writes it about once a second)

    public static GeneratorJob Instance { get; private set; }

    GameObject generator;
    GameObject[] canObjects = new GameObject[0];
    AudioSource hum, oneShot;
    AudioSource[] sloshes = new AudioSource[0];
    Vector3[] canLastPos = new Vector3[0];
    bool[] sloshStarted = new bool[0];
    StreetLamp[] circuit = new StreetLamp[0];
    float hostFuel, nextSlosh, refuelProgress;
    Vector3[] lastHolderPos = new Vector3[0];

    void Awake() => Instance = this;

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer) SetUpRun();
        running.OnValueChanged += OnRunningChanged;
        genPos.OnValueChanged += OnGenMoved;
        CityManager.Built += Show;
        Show();
    }

    public override void OnNetworkDespawn()
    {
        running.OnValueChanged -= OnRunningChanged;
        genPos.OnValueChanged -= OnGenMoved;
        CityManager.Built -= Show;
        foreach (var lamp in circuit) if (lamp != null) lamp.SetPowered(true);
        if (generator != null) Destroy(generator);
        foreach (var c in canObjects) if (c != null) Destroy(c);
        canObjects = new GameObject[0];
        var me = Me();
        if (me != null) { me.SpeedScale = 1f; me.CanSprint = true; }
    }

    // ---------- Host: a new run ----------

    void SetUpRun()
    {
        var city = CityManager.Current;
        Vector3 spawn = city != null ? city.playerSpawn : transform.position;
        Quaternion facing = city != null ? city.playerSpawnRotation : Quaternion.identity;
        PlaceGenerator(spawn, facing, out var pos, out float yaw);
        genPos.Value = pos;
        genYaw.Value = yaw;
        hostFuel = startFuel;
        fuel.Value = hostFuel;
        running.Value = true;

        // Cans: out-of-the-way spots (parks, parking lots, empty lots) in a ring round the generator
        cans.Clear();
        var rng = new System.Random((city != null ? city.seed : 0) ^ 0x6e6e);
        var spots = new System.Collections.Generic.List<Vector3>();
        if (city != null)
            foreach (var s in city.hidingSpots)
            {
                float d = Vector3.Distance(Flat(s), Flat(pos));
                if (d >= minCanDistance && d <= maxCanDistance) spots.Add(s);
            }
        for (int i = 0; i < canCount && spots.Count > 0; i++)
        {
            int k = rng.Next(spots.Count);
            Vector3 p = spots[k] + new Vector3((float)rng.NextDouble() * 2f - 1f, 0f, (float)rng.NextDouble() * 2f - 1f);
            spots.RemoveAt(k);
            cans.Add(new FuelCan { pos = Ground(p), a = Nobody, b = Nobody });
        }
        if (cans.Count < canCount) Debug.LogWarning($"GeneratorJob: only {cans.Count} of {canCount} fuel cans placed (no spots {minCanDistance}-{maxCanDistance} m away)");
    }

    // Against the wall beside the door you start at, out of the doorway
    void PlaceGenerator(Vector3 spawn, Quaternion facing, out Vector3 pos, out float yaw)
    {
        Vector3 f = facing * Vector3.forward, r = facing * Vector3.right;
        Vector3 half = GeneratorHalfSize();
        foreach (float side in new[] { 3.5f, -3.5f, 5f, -5f, 2.5f, -2.5f })
        {
            Vector3 c = spawn + r * side + Vector3.up * 0.6f + f * 2f;
            if (!Physics.Raycast(c, -f, out var hit, 4f, ~0, QueryTriggerInteraction.Ignore)) continue;
            Vector3 p = Ground(hit.point + f * (half.x + 0.15f));   // turned along the wall: its X sticks out
            var rot = Quaternion.LookRotation(r);
            if (Physics.CheckBox(p + Vector3.up * (half.y + 0.05f), half, rot, ~0, QueryTriggerInteraction.Ignore)) continue;
            pos = p;
            yaw = rot.eulerAngles.y;
            return;
        }
        pos = Ground(spawn + f * 2.5f);
        yaw = facing.eulerAngles.y + 90f;
    }

    Vector3 GeneratorHalfSize()
    {
        var box = generatorPrefab != null ? generatorPrefab.GetComponent<BoxCollider>() : null;
        if (box == null) return new Vector3(0.3f, 0.35f, 0.5f);
        // the prefab's long side is its Z; we turn it to run along the wall, so its X sticks out from the wall
        return new Vector3(box.size.x / 2f, box.size.y / 2f, box.size.z / 2f);
    }

    static Vector3 Ground(Vector3 p)
    {
        if (Physics.Raycast(p + Vector3.up * 2f, Vector3.down, out var hit, 6f, ~0, QueryTriggerInteraction.Ignore))
            return hit.point;
        return p;
    }

    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    // ---------- Every machine: show it ----------

    void OnGenMoved(Vector3 a, Vector3 b) => Show();

    void Show()
    {
        if (generatorPrefab != null)
        {
            if (generator == null)
            {
                generator = Instantiate(generatorPrefab);
                generator.name = "Generator";
                hum = generator.AddComponent<AudioSource>();
                AudioMix.Setup3D(hum, 1.5f, soundRange);
                hum.clip = humClip;
                hum.loop = true;
                hum.volume = humVolume;
                var o = new GameObject("Generator One-Shots");
                o.transform.SetParent(generator.transform, false);
                oneShot = o.AddComponent<AudioSource>();
                AudioMix.Setup3D(oneShot, 1.5f, soundRange);
            }
            generator.transform.SetPositionAndRotation(genPos.Value, Quaternion.Euler(0f, genYaw.Value, 0f));
        }

        // The lamps on its circuit
        foreach (var lamp in circuit) if (lamp != null) lamp.SetPowered(true);
        var list = new System.Collections.Generic.List<StreetLamp>();
        foreach (var lamp in StreetLamp.All)
            if (Vector3.Distance(Flat(lamp.transform.position), Flat(genPos.Value)) <= powerRadius) list.Add(lamp);
        circuit = list.ToArray();
        ApplyPower(false);
    }

    void OnRunningChanged(bool was, bool now) => ApplyPower(true);

    void ApplyPower(bool withSound)
    {
        foreach (var lamp in circuit) if (lamp != null) lamp.SetPowered(running.Value);
        if (hum == null) return;
        if (running.Value)
        {
            if (withSound && startClip != null) oneShot.PlayOneShot(startClip, startVolume);
            if (humClip != null && !hum.isPlaying)
            {
                hum.time = withSound ? 0f : Random.Range(0f, humClip.length * 0.9f);
                hum.PlayDelayed(withSound && startClip != null ? startClip.length * 0.6f : 0f);
            }
        }
        else
        {
            hum.Stop();
            if (withSound && stallClip != null) oneShot.PlayOneShot(stallClip, stallVolume);
        }
    }

    void Update()
    {
        if (!IsSpawned) return;
        if (IsServer) HostUpdate();
        ShowCans();
        LocalPlayer();
    }

    void ShowCans()
    {
        if (canPrefab == null) return;
        if (canObjects.Length != cans.Count)
        {
            foreach (var c in canObjects) if (c != null) Destroy(c);
            canObjects = new GameObject[cans.Count];
            sloshes = new AudioSource[cans.Count];
            canLastPos = new Vector3[cans.Count];
            sloshStarted = new bool[cans.Count];
            for (int i = 0; i < cans.Count; i++)
            {
                canObjects[i] = Instantiate(canPrefab);
                canObjects[i].name = "Fuel Can";
                if (sloshClip == null) continue;
                var s = sloshes[i] = canObjects[i].AddComponent<AudioSource>();
                AudioMix.Setup3D(s, 0.6f, sloshRange);
                s.clip = sloshClip;
                s.loop = true;
                s.volume = sloshVolume;
                s.time = Random.Range(0f, sloshClip.length * 0.8f);
            }
        }
        for (int i = 0; i < cans.Count; i++)
        {
            var can = cans[i];
            var go = canObjects[i];
            go.SetActive(!can.used);
            if (can.used) continue;
            var a = Player(can.a);
            var b = Player(can.b);
            if (a != null && b != null)
            {
                // Between the two of them, at hand height
                Vector3 pa = a.transform.position, pb = b.transform.position;
                Vector3 along = Flat(pb - pa);
                go.transform.SetPositionAndRotation((pa + pb) / 2f + Vector3.up * 0.55f,
                    Quaternion.LookRotation(along.sqrMagnitude > 0.01f ? along : a.transform.forward));
            }
            else if (a != null)
            {
                // Hanging from the right hand
                Transform t = a.transform;
                go.transform.SetPositionAndRotation(t.position + t.right * 0.32f + t.forward * 0.12f + Vector3.up * 0.42f, t.rotation);
            }
            else go.transform.SetPositionAndRotation(can.pos, Quaternion.Euler(0f, i * 73f, 0f));

            // Sloshes while one player carries it and walks
            Vector3 now = go.transform.position;
            bool moving = Time.deltaTime > 0f && Vector3.Distance(Flat(now), Flat(canLastPos[i])) / Time.deltaTime > 0.3f;
            canLastPos[i] = now;
            var s = sloshes[i];
            if (s == null) continue;
            bool on = a != null && b == null && moving;
            if (on && !s.isPlaying) { if (sloshStarted[i]) s.UnPause(); else { s.Play(); sloshStarted[i] = true; } }
            else if (!on && s.isPlaying) s.Pause();
        }
    }

    // ---------- Host ----------

    void HostUpdate()
    {
        // Fuel only burns at night
        if (running.Value && GameFlow.Instance != null && GameFlow.Instance.Phase == GamePhase.Night)
        {
            hostFuel -= Time.deltaTime;
            if (hostFuel <= 0f)
            {
                hostFuel = 0f;
                running.Value = false;
                Debug.Log("GeneratorJob: out of fuel, lamps off");
            }
            if (Mathf.Abs(fuel.Value - hostFuel) >= 1f || hostFuel == 0f) fuel.Value = hostFuel;
        }

        if (lastHolderPos.Length != cans.Count) lastHolderPos = new Vector3[cans.Count];
        bool sloshNow = Time.time >= nextSlosh;
        if (sloshNow) nextSlosh = Time.time + sloshNoiseInterval;
        for (int i = 0; i < cans.Count; i++)
        {
            var can = cans[i];
            if (can.used || !can.Carried) continue;
            var changed = false;
            // Holders who got caught, died or left let go
            if (!CanHold(Player(can.b)) && can.b != Nobody) { can.b = Nobody; changed = true; }
            if (!CanHold(Player(can.a)))
            {
                var p = Player(can.a);
                if (p != null) can.pos = Ground(p.transform.position + p.transform.right * 0.4f);
                else can.pos = canObjects.Length > i && canObjects[i] != null ? Ground(canObjects[i].transform.position) : can.pos;
                can.a = can.b;
                can.b = Nobody;
                changed = true;
            }
            // The helper drifted away
            var pa = Player(can.a);
            var pb = Player(can.b);
            if (pa != null && pb != null && Vector3.Distance(pa.transform.position, pb.transform.position) > pairBreakDistance)
            {
                can.b = Nobody;
                changed = true;
            }
            // Alone and moving: slosh
            if (pa != null && can.b == Nobody)
            {
                Vector3 now = pa.transform.position;
                bool moving = Vector3.Distance(Flat(now), Flat(lastHolderPos[i])) > 0.3f * sloshNoiseInterval;
                if (sloshNow)
                {
                    if (moving) NoiseSystem.Emit(now, sloshNoiseRadius);
                    lastHolderPos[i] = now;
                }
            }
            if (changed) cans[i] = can;
        }
    }

    static bool CanHold(FirstPersonController p) => p != null && p.IsSpawned && !p.IsDead && !p.IsGrabbed && !p.IsHidden;

    bool HoldingAny(ulong id)
    {
        foreach (var c in cans) if (!c.used && c.Holds(id)) return true;
        return false;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PickRpc(int i, RpcParams rpc = default)
    {
        ulong id = rpc.Receive.SenderClientId;
        var me = Player(id);
        if (i < 0 || i >= cans.Count || !CanHold(me) || HoldingAny(id)) return;
        var can = cans[i];
        if (can.used) return;
        Vector3 at = canObjects.Length > i && canObjects[i] != null ? canObjects[i].transform.position : can.pos;
        if (Vector3.Distance(me.transform.position, at) > useRange + 1.5f) return;
        if (!can.Carried) can.a = id;
        else if (can.b == Nobody) can.b = id;
        else return;
        cans[i] = can;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void DropRpc(RpcParams rpc = default)
    {
        ulong id = rpc.Receive.SenderClientId;
        for (int i = 0; i < cans.Count; i++)
        {
            var can = cans[i];
            if (can.used || !can.Holds(id)) continue;
            if (can.b == id) can.b = Nobody;
            else
            {
                var p = Player(id);
                if (p != null) can.pos = Ground(p.transform.position + p.transform.forward * 0.6f);
                can.a = can.b;
                can.b = Nobody;
            }
            cans[i] = can;
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RefuelRpc(RpcParams rpc = default)
    {
        ulong id = rpc.Receive.SenderClientId;
        var me = Player(id);
        if (me == null || Vector3.Distance(me.transform.position, genPos.Value) > refuelRange + 1.5f) return;
        for (int i = 0; i < cans.Count; i++)
        {
            var can = cans[i];
            if (can.used || !can.Holds(id)) continue;
            can.used = true;
            can.a = can.b = Nobody;
            cans[i] = can;
            hostFuel = Mathf.Min(maxFuel, hostFuel + fuelPerCan);
            fuel.Value = hostFuel;
            if (!running.Value)
            {
                running.Value = true;
                NoiseSystem.Emit(genPos.Value, startNoiseRadius);
            }
            Debug.Log($"GeneratorJob: player {id} refuelled, {hostFuel:0} s of fuel");
            return;
        }
    }

    // ---------- Your own player ----------

    void LocalPlayer()
    {
        var me = Me();
        if (me == null) return;
        int held = HeldBy(me.OwnerClientId);
        bool alone = held >= 0 && cans[held].b == Nobody && Player(cans[held].a) == me;
        bool helped = held >= 0 && !alone;
        me.SpeedScale = alone ? soloSpeed : helped ? pairSpeed : 1f;
        me.CanSprint = held < 0;

        var kb = Keyboard.current;
        bool blocked = kb == null || NetworkUI.MenuOpen || !CanHold(me);
        if (held >= 0 && NearGenerator(me))
        {
            // Hold E to pour
            if (!blocked && kb.eKey.isPressed)
            {
                if (refuelProgress == 0f && pourClip != null && oneShot != null) oneShot.PlayOneShot(pourClip, pourVolume);
                refuelProgress += Time.deltaTime / Mathf.Max(0.1f, refuelSeconds);
                if (refuelProgress >= 1f) { RefuelRpc(); refuelProgress = 0f; oneShot?.Stop(); }
                return;
            }
        }
        if (refuelProgress > 0f && oneShot != null) oneShot.Stop();   // let go early: stop pouring
        refuelProgress = 0f;
        if (blocked) return;
        if (held >= 0 && kb.gKey.wasPressedThisFrame) DropRpc();
        else if (held < 0 && kb.eKey.wasPressedThisFrame)
        {
            int i = Aimed(me);
            if (i >= 0) PickRpc(i);
        }
    }

    // True while E means "fuel can" for you right now, so DoorManager leaves the key alone
    public bool ClaimsUse(FirstPersonController me)
    {
        if (!IsSpawned || me == null) return false;
        int held = HeldBy(me.OwnerClientId);
        return held >= 0 ? NearGenerator(me) : Aimed(me) >= 0;
    }

    bool NearGenerator(FirstPersonController me) => Vector3.Distance(Flat(me.transform.position), Flat(genPos.Value)) <= refuelRange;

    int HeldBy(ulong id)
    {
        for (int i = 0; i < cans.Count; i++) if (!cans[i].used && cans[i].Holds(id)) return i;
        return -1;
    }

    // The can you're looking at, close enough to pick up (or to help carry)
    int Aimed(FirstPersonController me)
    {
        Transform cam = me.cameraPivot;
        int best = -1;
        float bestScore = 0.6f;
        for (int i = 0; i < cans.Count && i < canObjects.Length; i++)
        {
            var can = cans[i];
            if (can.used || can.b != Nobody || canObjects[i] == null) continue;
            Vector3 to = canObjects[i].transform.position + Vector3.up * 0.2f - cam.position;
            if (to.magnitude > useRange) continue;
            float score = Vector3.Dot(cam.forward, to.normalized);
            if (score > bestScore) { best = i; bestScore = score; }
        }
        return best;
    }

    static FirstPersonController Me()
    {
        foreach (var p in FirstPersonController.All) if (p.IsOwner) return p;
        return null;
    }

    static FirstPersonController Player(ulong id)
    {
        if (id == Nobody) return null;
        foreach (var p in FirstPersonController.All) if (p.OwnerClientId == id) return p;
        return null;
    }

    // ---------- Hints ----------

    void OnGUI()
    {
        if (!IsSpawned) return;
        var me = Me();
        if (me == null || !CanHold(me) || NetworkUI.MenuOpen) return;
        string msg = null;
        int held = HeldBy(me.OwnerClientId);
        if (held >= 0)
        {
            bool alone = cans[held].b == Nobody;
            msg = NearGenerator(me)
                ? (refuelProgress > 0f ? $"Pouring... {Mathf.RoundToInt(refuelProgress * 100f)}%" : "Hold E - refuel the generator")
                : alone ? "Carrying fuel alone: slow and noisy   G - drop" : "Carrying fuel together   G - let go";
        }
        else
        {
            int i = Aimed(me);
            if (i >= 0) msg = cans[i].Carried ? "E - help carry" : "E - pick up fuel can";
            else if (Vector3.Distance(me.transform.position, genPos.Value) < 6f)
                msg = running.Value ? $"Generator: {Mathf.FloorToInt(fuel.Value / 60f)}:{Mathf.FloorToInt(fuel.Value % 60f):00} of fuel left"
                                    : "Generator: out of fuel - find a fuel can";
        }
        if (msg == null) return;
        var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 18 };
        style.normal.textColor = Color.white;
        GUI.Label(new Rect((Screen.width - 520f) / 2f, Screen.height - 110f, 520f, 26f), msg, style);
    }

    // Tests
    public Vector3 GeneratorPosition => genPos.Value;
    public bool Running => running.Value;
    public float Fuel => fuel.Value;
    public int CanCount => cans.Count;
    public Vector3 CanPosition(int i) => cans[i].pos;
    public void DebugSetFuel(float seconds) { if (IsServer) { hostFuel = seconds; fuel.Value = seconds; } }
}
