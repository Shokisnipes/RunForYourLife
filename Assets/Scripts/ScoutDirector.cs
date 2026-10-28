using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

// The Director (DESIGN.md): on the NetworkManager object next to GenomeManager.
// Host: always knows where every player is, but never tells the monster directly. It spawns the scouts (if
// GenomeManager.allowScouts), and at night sends idle ones toward the players' general area, closer and more often
// the longer it's been calm (no relay, no chase). Scouts that spot someone screech and Relay() the spot to the
// monster. With every scout dead, it falls back to a vague hint now and then (the monster drifts your way).
// Every client: the stomp (left click with a scout at your feet) and its on-screen hint.
public class ScoutDirector : MonoBehaviour
{
    public static ScoutDirector Instance { get; private set; }

    [Tooltip("Assets/Prefabs/Scout.prefab (made by Tools > RunForYourLife > Scout Setup).")]
    public GameObject scoutPrefab;

    [Header("Spawning")]
    [Tooltip("Scouts spawn at least this far from the player spawn.")]
    public float minSpawnDistance = 35f;

    [Header("Directing (night)")]
    [Tooltip("How often (s) the Director thinks.")]
    public float thinkInterval = 5f;
    [Tooltip("After this many calm seconds it's sure to send a scout each think (it's less likely before).")]
    public float calmToSend = 40f;
    [Tooltip("How far from the player it aims a scout: wide right after a relay, narrowing as the calm goes on.")]
    public float searchRadiusWide = 35f;
    public float searchRadiusNarrow = 10f;
    [Tooltip("Calm seconds to go from the wide to the narrow search radius.")]
    public float narrowAfter = 120f;

    [Header("Fallback (no scouts left)")]
    [Tooltip("With no scouts alive, after this many calm seconds the monster gets a vague hint...")]
    public float fallbackDelay = 60f;
    [Tooltip("...this far off from a real player...")]
    public float fallbackOffset = 25f;
    [Tooltip("...and again every this many seconds while it stays calm.")]
    public float fallbackInterval = 45f;

    bool spawnedThisRun;
    float nextThink, lastInfoTime, nextFallback;
    ScoutAI stompTarget;
    bool wasLocked;   // the click that captures the mouse into the game isn't a stomp

    void Awake()
    {
        Instance = this;
        // Registered on every peer before hosting/joining, so the host can spawn it on clients
        var nm = GetComponent<NetworkManager>();
        if (nm != null && scoutPrefab != null) nm.AddNetworkPrefab(scoutPrefab);
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    void Update()
    {
        var nm = NetworkManager.Singleton;
        bool hosting = nm != null && nm.IsServer && nm.IsListening;
        if (!hosting) spawnedThisRun = false;
        else HostUpdate();
        UpdateStomp();
    }

    // ---------- Host ----------

    void HostUpdate()
    {
        var flow = GameFlow.Instance;
        var genomes = GetComponent<GenomeManager>();
        if (flow == null || !flow.IsSpawned || CityManager.Current == null || GenomeManager.Current == null) return;

        if (!spawnedThisRun)
        {
            spawnedThisRun = true;
            lastInfoTime = Time.time;
            if (genomes != null && genomes.allowScouts) SpawnScouts(Random.Range(genomes.scoutCount.x, genomes.scoutCount.y + 1));
            return;
        }

        if (flow.Phase != GamePhase.Night) { lastInfoTime = Time.time; return; }
        if (MonsterChasing()) lastInfoTime = Time.time;   // it knows where someone is already
        if (Time.time < nextThink) return;
        nextThink = Time.time + thinkInterval;

        var target = RandomLivePlayer();
        if (target == null) return;
        float calm = Time.time - lastInfoTime;

        int alive = 0;
        foreach (var s in ScoutAI.All) if (s.IsSpawned && !s.IsDead) alive++;

        if (alive > 0)
        {
            if (Random.value > Mathf.Clamp01(calm / calmToSend)) return;
            var idle = new List<ScoutAI>();
            foreach (var s in ScoutAI.All) if (s.IsSpawned && s.Idle) idle.Add(s);
            if (idle.Count == 0) return;
            float radius = Mathf.Lerp(searchRadiusWide, searchRadiusNarrow, calm / narrowAfter);
            Vector2 r = Random.insideUnitCircle * radius;
            var scout = idle[Random.Range(0, idle.Count)];
            scout.SendToward(target.transform.position + new Vector3(r.x, 0f, r.y));
        }
        else if (calm > fallbackDelay && Time.time >= nextFallback)
        {
            nextFallback = Time.time + fallbackInterval;
            Vector2 r = Random.insideUnitCircle.normalized * fallbackOffset * Random.Range(0.5f, 1f);
            Vector3 hint = target.transform.position + new Vector3(r.x, 0f, r.y);
            Debug.Log($"Director: no scouts left, vague hint {fallbackOffset:0} m from a player");
            TellMonsters(hint);
        }
    }

    /// <summary>Host: a scout finished its screech; the monster learns where that player was.</summary>
    public void Relay(Vector3 where, ScoutAI from)
    {
        Debug.Log($"Director: scout relayed a player at {where} to the monster");
        lastInfoTime = Time.time;
        TellMonsters(where);
    }

    static void TellMonsters(Vector3 where)
    {
        var giant = FindAnyObjectByType<MonsterAI>();
        if (giant != null) giant.Relay(where);
        var climber = FindAnyObjectByType<ClimberAI>();
        if (climber != null) climber.Relay(where);
    }

    static bool MonsterChasing()
    {
        var giant = FindAnyObjectByType<MonsterAI>();
        if (giant != null && giant.Hunting && giant.state == MonsterState.Chase) return true;
        var climber = FindAnyObjectByType<ClimberAI>();
        return climber != null && climber.Hunting && climber.state == ClimberState.Chase;
    }

    static FirstPersonController RandomLivePlayer()
    {
        var live = new List<FirstPersonController>();
        foreach (var p in FirstPersonController.All) if (p.IsSpawned && !p.IsDead) live.Add(p);
        return live.Count > 0 ? live[Random.Range(0, live.Count)] : null;
    }

    // Under street lamps far from the players' start (lamps are always on streets)
    void SpawnScouts(int count)
    {
        if (scoutPrefab == null) { Debug.LogWarning("ScoutDirector: no Scout prefab assigned."); return; }
        var city = CityManager.Current;
        var spots = new List<Vector3>();
        foreach (var lamp in city.lamps)
            if (lamp != null && Vector3.Distance(lamp.transform.position, city.playerSpawn) >= minSpawnDistance)
                spots.Add(lamp.transform.position);
        if (spots.Count == 0) foreach (var p in city.monsterSpawns) spots.Add(p);

        var agentType = scoutPrefab.GetComponent<NavMeshAgent>().agentTypeID;
        var filter = new NavMeshQueryFilter { agentTypeID = agentType, areaMask = NavMesh.AllAreas };
        int made = 0;
        for (int i = 0; i < count && spots.Count > 0; i++)
        {
            int k = Random.Range(0, spots.Count);
            Vector3 spot = spots[k];
            spots.RemoveAt(k);
            if (!NavMesh.SamplePosition(spot, out var hit, 8f, filter)) { i--; continue; }
            var go = Instantiate(scoutPrefab, hit.position, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
            go.GetComponent<ScoutAI>().Setup(GenomeManager.Current);
            go.GetComponent<NetworkObject>().Spawn(true);
            made++;
        }
        Debug.Log($"Director: spawned {made} scouts ({GenomeManager.Current.sense} genome)");
    }

    // ---------- Every client: stomping ----------

    void UpdateStomp()
    {
        stompTarget = null;
        FirstPersonController me = null;
        foreach (var p in FirstPersonController.All) if (p.IsOwner) me = p;
        if (me == null || me.IsDead || me.IsGrabbed || NetworkUI.MenuOpen) { wasLocked = false; return; }

        float best = float.MaxValue;
        foreach (var s in ScoutAI.All)
        {
            if (!s.IsSpawned || s.IsDead || !s.InStompRange(me.transform.position)) continue;
            float d = Vector3.Distance(s.transform.position, me.transform.position);
            if (d < best) { best = d; stompTarget = s; }
        }

        var mouse = Mouse.current;
        bool locked = Cursor.lockState == CursorLockMode.Locked;
        bool click = locked && wasLocked && mouse != null && mouse.leftButton.wasPressedThisFrame;
        wasLocked = locked;
        if (stompTarget != null && click)
        {
            CameraShake.ShakeFrom(stompTarget.transform.position, 0.25f, 3f);   // a small thump under your foot
            stompTarget.StompRpc();
        }
    }

    void OnGUI()
    {
        if (stompTarget == null || NetworkUI.MenuOpen) return;
        var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 18 };
        style.normal.textColor = Color.white;
        GUI.Label(new Rect((Screen.width - 420f) / 2f, Screen.height - 170f, 420f, 26f), "Click - stomp", style);
    }
}
