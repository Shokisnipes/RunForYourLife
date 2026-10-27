using System;
using Unity.AI.Navigation;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

// Builds the city for every run (CityGenerator) and keeps everyone on the same one. Lives on the GameFlow object.
//  - In the menu: a lobby city (fixed seed) behind the menu camera.
//  - Host: NetworkUI calls PrepareHost() just before StartHost, so the new city and its NavMesh (both agent types,
//    host only, because only the host runs the monsters) exist before anything spawns. The seed is then synced.
//  - Clients: build the same seed when they join. Anything that caches map stuff listens to Built.
public class CityManager : NetworkBehaviour
{
    [Tooltip("City size, streets, lamps, materials (Assets/Settings/CitySettings.asset).")]
    public CitySettings settings;
    [Tooltip("The city behind the main menu.")]
    public int lobbySeed = 1;
    [Tooltip("Testing: above 0 = always build this seed instead of a random one.")]
    public int fixedSeed = 0;

    [Header("Moved onto the new city")]
    [Tooltip("Players spawn here (FirstPersonController finds it by name).")]
    public Transform playerSpawn;
    [Tooltip("The monsters wander around this (city centre).")]
    public Transform patrolCenter;
    [Tooltip("Monster wander radius as a fraction of the city's half-size.")]
    [Range(0.2f, 1f)] public float wanderFraction = 0.8f;
    [Tooltip("Menu background camera. Placed looking down a street of the lobby city.")]
    public Transform lobbyCamera;

    public static CityManager Instance { get; private set; }
    public static CityGenerator.City Current { get; private set; }
    // Fired on every machine after a new city is built (and, on the host, baked)
    public static event Action Built;

    readonly NetworkVariable<int> netSeed = new NetworkVariable<int>(0);
    int builtSeed, preparedSeed;

    void Awake()
    {
        Instance = this;
        Build(lobbySeed, false);
        PlaceLobbyCamera();
    }

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    // Host: call right before NetworkManager.StartHost()
    public void PrepareHost()
    {
        preparedSeed = fixedSeed > 0 ? fixedSeed : new System.Random().Next(1, int.MaxValue);
        Build(preparedSeed, true);
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            if (preparedSeed == 0) PrepareHost();   // started some other way than the Host button
            netSeed.Value = preparedSeed;
        }
        else
        {
            netSeed.OnValueChanged += OnSeedChanged;
            if (netSeed.Value != 0 && netSeed.Value != builtSeed) Build(netSeed.Value, false);
        }
    }

    public override void OnNetworkDespawn()
    {
        netSeed.OnValueChanged -= OnSeedChanged;
        preparedSeed = 0;
    }

    void OnSeedChanged(int previous, int seed)
    {
        if (seed != 0 && seed != builtSeed) Build(seed, false);
    }

    void Build(int seed, bool bakeNavMesh)
    {
        if (settings == null) { Debug.LogError("CityManager: no CitySettings assigned."); return; }

        // Gone right now (not at the end of the frame), so the NavMesh bake doesn't see the old city
        if (Current != null && Current.root != null) DestroyImmediate(Current.root);

        float t0 = Time.realtimeSinceStartup;
        Current = CityGenerator.Generate(settings, seed, Vector3.zero);
        builtSeed = seed;

        if (playerSpawn != null) playerSpawn.SetPositionAndRotation(Current.playerSpawn, Current.playerSpawnRotation);
        if (patrolCenter != null) patrolCenter.position = Current.patrolCenter;

        if (bakeNavMesh)
        {
            // Colliders only: litter, windows and leaves (no colliders) stay out of the NavMesh.
            // The giant steps over fences, so they're left out of its NavMesh (the Climber has to go round them).
            foreach (var surface in FindObjectsByType<NavMeshSurface>())
            {
                surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
                if (NavMesh.GetSettingsNameFromID(surface.agentTypeID) == "Giant") surface.layerMask &= FenceBuilder.AllButFences;
                surface.layerMask &= Door.AllButDoors;   // doorways bake open; a closed Door cuts itself out at runtime
                surface.BuildNavMesh();
            }
            PlaceMonsters();
        }
        Debug.Log($"CityManager: city seed {seed}, {Current.buildings.Count} buildings, {ReachPoint.All.Count} doorways, {Current.lamps.Count} lamps " +
                  $"({(Time.realtimeSinceStartup - t0) * 1000f:0} ms{(bakeNavMesh ? " incl. NavMesh bake" : "")})");
        Built?.Invoke();
    }

    // Host: put each monster on its NavMesh at a far street, and point its wandering at the new city
    void PlaceMonsters()
    {
        float radius = Current.patrolRadius * wanderFraction;
        int i = 0;

        var giant = FindAnyObjectByType<MonsterAI>();
        if (giant != null)
        {
            Place(giant.GetComponent<NavMeshAgent>(), Current.monsterSpawns[i++ % Current.monsterSpawns.Count]);
            giant.SetHome(giant.transform.position);
            if (patrolCenter != null) giant.patrolCenter = patrolCenter;
            giant.wanderRadius = radius;
        }

        var climber = FindAnyObjectByType<ClimberAI>();
        if (climber != null)
        {
            Place(climber.GetComponent<NavMeshAgent>(), Current.monsterSpawns[i++ % Current.monsterSpawns.Count]);
            climber.SetHome(climber.transform.position);
            if (patrolCenter != null) climber.patrolCenter = patrolCenter;
            climber.wanderRadius = radius;
        }
    }

    static void Place(NavMeshAgent agent, Vector3 point)
    {
        if (agent == null) return;
        var filter = new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = NavMesh.AllAreas };
        if (NavMesh.SamplePosition(point, out var hit, 15f, filter)) point = hit.position;
        if (agent.enabled && agent.isOnNavMesh) agent.Warp(point);
        else agent.transform.position = point;   // the AI turns its agent on when it spawns
    }

    // Menu camera: eye height in front of the spawn building, looking down the street
    void PlaceLobbyCamera()
    {
        if (lobbyCamera == null || Current == null) return;
        Quaternion r = Current.playerSpawnRotation;
        Vector3 street = r * Vector3.right;
        lobbyCamera.SetPositionAndRotation(Current.playerSpawn - r * Vector3.forward * 3f + Vector3.up * 2.5f,
                                           Quaternion.LookRotation(street + Vector3.down * 0.08f));
    }
}
