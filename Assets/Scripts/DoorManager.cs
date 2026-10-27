using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

// Syncs every building's front door (Door). Press E while looking at a door to open or close it. The host owns the
// state: it picks which doors start open each run, flips them when players ask, and tears them open when the giant
// reaches in (MonsterAI calls Rip). Opening/closing makes a little noise the monsters can hear.
// Place ONE of these on the same scene object as GameFlow (it needs the NetworkObject).
public class DoorManager : NetworkBehaviour
{
    [Header("Start of each run")]
    [Range(0f, 1f), Tooltip("Chance each door starts open (the rest start closed).")]
    public float startOpenChance = 0.3f;

    [Header("Players")]
    [Tooltip("How close you must be to a door to use it (metres, to the middle of the doorway).")]
    public float useRange = 2.6f;
    [Tooltip("Opening or closing a door is heard this far away (metres).")]
    public float noiseRadius = 8f;

    [Header("Sounds (optional)")]
    public AudioClip openClip;
    public AudioClip closeClip;
    [Tooltip("The giant tearing a door open.")]
    public AudioClip tornClip;
    [Tooltip("Door sounds fade out at this distance.")]
    public float soundRange = 35f;
    [Tooltip("Volumes, set to the levels in AudioMix.cs (opening -28 dB, closing -27 dB, torn -14 dB).")]
    [Range(0f, 1f)] public float openVolume = 0.19f;
    [Range(0f, 1f)] public float closeVolume = 0.55f;
    [Range(0f, 1f)] public float tornVolume = 1f;

    // One entry per door (Door.State), in Door order (sorted by position so every machine agrees)
    readonly NetworkList<byte> states = new NetworkList<byte>();
    Door[] doors = new Door[0];

    public static DoorManager Instance { get; private set; }

    void Awake() => Instance = this;

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        RefreshDoors();
        if (IsServer)
        {
            var rng = new System.Random(CityManager.Current != null ? CityManager.Current.seed : 0);
            states.Clear();
            for (int i = 0; i < doors.Length; i++)
                states.Add((byte)(rng.NextDouble() < startOpenChance ? Door.State.Open : Door.State.Closed));
        }
        states.OnListChanged += OnStatesChanged;
        CityManager.Built += OnCityBuilt;
        ShowAll();
    }

    public override void OnNetworkDespawn()
    {
        states.OnListChanged -= OnStatesChanged;
        CityManager.Built -= OnCityBuilt;
    }

    // A client built its copy of the city after we spawned: pick up the new doors
    void OnCityBuilt()
    {
        RefreshDoors();
        ShowAll();
    }

    void ShowAll()
    {
        for (int i = 0; i < doors.Length && i < states.Count; i++) doors[i].Show((Door.State)states[i], true);
    }

    void OnStatesChanged(NetworkListEvent<byte> e)
    {
        if (e.Type != NetworkListEvent<byte>.EventType.Value || e.Index < 0 || e.Index >= doors.Length) { ShowAll(); return; }
        var door = doors[e.Index];
        var state = (Door.State)e.Value;
        door.Show(state, false);
        if (state == Door.State.Torn) Play(tornClip, tornVolume, door.Center);
        else if (state == Door.State.Open) Play(openClip, openVolume, door.Center);
        else Play(closeClip, closeVolume, door.Center);
    }

    // Same order on every machine: sort by position
    void RefreshDoors()
    {
        doors = Door.All.ToArray();
        System.Array.Sort(doors, (a, b) =>
        {
            Vector3 p = a.transform.position, q = b.transform.position;
            int c = p.x.CompareTo(q.x);
            if (c != 0) return c;
            c = p.z.CompareTo(q.z);
            return c != 0 ? c : p.y.CompareTo(q.y);
        });
    }

    void Update()
    {
        if (!IsSpawned) return;
        var me = LocalPlayer();
        var kb = Keyboard.current;
        if (me == null || kb == null || !kb.eKey.wasPressedThisFrame || NetworkUI.MenuOpen || me.IsDead || me.IsGrabbed) return;
        int door = Aimed(me);
        if (door >= 0) ToggleRpc(door);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ToggleRpc(int door)
    {
        if (door < 0 || door >= states.Count) return;
        var state = (Door.State)states[door];
        if (state == Door.State.Torn) return;   // hanging off its hinges
        if (state == Door.State.Open && InTheWay(doors[door])) return;   // won't shut on the giant's arm / the Climber
        states[door] = (byte)(state == Door.State.Closed ? Door.State.Open : Door.State.Closed);
        NoiseSystem.Emit(doors[door].Center, noiseRadius);
    }

    // Something stopping this door from closing: the giant's arm reaching through it, or the Climber in the doorway.
    // Uses synced state, so every machine agrees (and can show the hint).
    static MonsterAI[] giants;
    static ClimberAI[] climbers;
    public static bool InTheWay(Door door)
    {
        if (giants == null || giants.Length == 0 || giants[0] == null) giants = FindObjectsByType<MonsterAI>();
        if (climbers == null || climbers.Length == 0 || climbers[0] == null) climbers = FindObjectsByType<ClimberAI>();
        foreach (var g in giants)
            if (g != null && g.IsSpawned && g.SyncedReaching && Vector3.Distance(g.SyncedDoorPoint, door.Center) < 2f) return true;
        foreach (var c in climbers)
        {
            if (c == null || !c.IsSpawned) continue;
            Vector3 d = door.transform.InverseTransformPoint(c.transform.position);
            if (Mathf.Abs(d.x) < 1.6f && Mathf.Abs(d.z) < 1.4f && d.y > -1f && d.y < 3f) return true;
        }
        return false;
    }

    // Host: the door in this doorway is closed?
    public bool IsClosed(Vector3 doorPoint)
    {
        int i = Nearest(doorPoint, 2f);
        return i >= 0 && i < states.Count && (Door.State)states[i] == Door.State.Closed;
    }

    // Host: the giant tears the door in this doorway open (if it's closed)
    public void Rip(Vector3 doorPoint)
    {
        if (!IsServer) return;
        int i = Nearest(doorPoint, 2f);
        if (i < 0 || i >= states.Count || (Door.State)states[i] != Door.State.Closed) return;
        states[i] = (byte)Door.State.Torn;
        NoiseSystem.Emit(doors[i].Center, noiseRadius * 2f);
    }

    int Nearest(Vector3 point, float range)
    {
        int best = -1;
        float bestDist = range;
        for (int i = 0; i < doors.Length; i++)
        {
            if (doors[i] == null) continue;
            float d = Vector3.Distance(point, doors[i].Center);
            if (d < bestDist) { best = i; bestDist = d; }
        }
        return best;
    }

    // The door you're looking at, close enough to use
    int Aimed(FirstPersonController me)
    {
        Transform cam = me.cameraPivot;
        int best = -1;
        float bestScore = 0.5f;   // must be roughly in front of you
        for (int i = 0; i < doors.Length && i < states.Count; i++)
        {
            if (doors[i] == null) continue;
            Vector3 to = doors[i].Center - cam.position;
            if (to.magnitude > useRange) continue;
            float score = Vector3.Dot(cam.forward, to.normalized);
            if (score > bestScore) { best = i; bestScore = score; }
        }
        return best;
    }

    void Play(AudioClip clip, float volume, Vector3 at)
    {
        if (clip == null) return;
        var go = new GameObject("Door Sound");
        go.transform.position = at;
        var s = go.AddComponent<AudioSource>();
        AudioMix.Setup3D(s, 3f, soundRange);
        s.PlayOneShot(clip, volume);
        Destroy(go, clip.length + 0.1f);
    }

    static FirstPersonController LocalPlayer()
    {
        foreach (var p in FirstPersonController.All) if (p.IsOwner) return p;
        return null;
    }

    // "E - open door" hint near the bottom of the screen
    void OnGUI()
    {
        if (!IsSpawned) return;
        var me = LocalPlayer();
        if (me == null || me.IsDead || me.IsGrabbed || NetworkUI.MenuOpen) return;
        int door = Aimed(me);
        if (door < 0) return;
        var state = (Door.State)states[door];
        string msg = state == Door.State.Torn ? "Door torn off its hinges"
                   : state == Door.State.Open ? (InTheWay(doors[door]) ? "Something's in the way!" : "E - close door")
                   : "E - open door";
        var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 18 };
        style.normal.textColor = Color.white;
        GUI.Label(new Rect((Screen.width - 420f) / 2f, Screen.height - 140f, 420f, 26f), msg, style);
    }
}
