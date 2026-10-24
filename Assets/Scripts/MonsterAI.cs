using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

public enum MonsterState { Patrol, Investigate, Chase, Search }

// Put on the monster together with a NavMeshAgent, NetworkObject and NetworkTransform.
// The brain only runs on the host; everyone else just sees it move.
// Patrol -> hears a noise -> Investigate -> sees you -> Chase -> loses you -> Search -> Patrol
[RequireComponent(typeof(NavMeshAgent))]
public class MonsterAI : NetworkBehaviour
{
    [Header("Current state (read only)")]
    public MonsterState state = MonsterState.Patrol;

    // The host writes the state, everyone reads it (used for sounds/animation)
    readonly NetworkVariable<MonsterState> netState = new NetworkVariable<MonsterState>(MonsterState.Patrol);
    public MonsterState SyncedState => netState.Value;

    [Header("Speeds")]
    public float patrolSpeed = 2f;
    public float investigateSpeed = 3.5f;
    public float chaseSpeed = 5.8f;

    [Header("Vision")]
    public float eyeHeight = 2.2f;
    public float viewDistance = 20f;
    public float viewAngle = 110f;               // full cone width in degrees
    public float flashlightViewDistance = 35f;   // a lit flashlight is visible from further away
    public float crouchVisibility = 0.6f;        // crouching shrinks how far it can see you
    public float closeSenseRange = 2.5f;         // notices you this close even from behind

    [Header("Hearing")]
    public float hearingMultiplier = 1f;

    [Header("Patrol")]
    public Transform[] waypoints;                // optional; empty = wander randomly
    public Transform patrolCenter;               // optional; wanders around this (defaults to spawn point)
    public float wanderRadius = 15f;
    public float patrolPause = 2f;

    [Header("Chase")]
    public float loseSightTime = 2f;             // keeps tracking you briefly after losing sight
    public float catchDistance = 1.6f;
    public float catchCooldown = 3f;             // ignores everyone briefly after a catch

    [Header("Search")]
    public float searchDuration = 12f;
    public float searchRadius = 8f;

    NavMeshAgent agent;
    FirstPersonController target;
    Vector3 pointOfInterest;
    float lastSeenTime;
    float stateStartTime;
    float pauseUntil;
    int waypointIndex;
    Vector3 homePosition;
    float ignorePlayersUntil;

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        homePosition = transform.position;
    }

    void OnEnable() => NoiseSystem.OnNoise += HearNoise;
    void OnDisable() => NoiseSystem.OnNoise -= HearNoise;

    public override void OnNetworkSpawn()
    {
        if (!IsServer)
        {
            agent.enabled = false;   // the host moves it; we just watch
            return;
        }
        SetState(MonsterState.Patrol);
    }

    void Update()
    {
        if (!IsServer || !IsSpawned) return;

        var seen = FindVisiblePlayer();
        if (seen != null)
        {
            target = seen;
            lastSeenTime = Time.time;
            if (state != MonsterState.Chase) SetState(MonsterState.Chase);
        }

        switch (state)
        {
            case MonsterState.Patrol:      UpdatePatrol(); break;
            case MonsterState.Investigate: UpdateInvestigate(); break;
            case MonsterState.Chase:       UpdateChase(); break;
            case MonsterState.Search:      UpdateSearch(); break;
        }
    }

    // ---------- States ----------

    void SetState(MonsterState newState)
    {
        state = newState;
        if (IsServer) netState.Value = newState;
        stateStartTime = Time.time;
        pauseUntil = 0f;

        switch (newState)
        {
            case MonsterState.Patrol:
                agent.speed = patrolSpeed;
                GoToNextPatrolPoint();
                break;
            case MonsterState.Investigate:
                agent.speed = investigateSpeed;
                agent.SetDestination(pointOfInterest);
                break;
            case MonsterState.Chase:
                agent.speed = chaseSpeed;
                break;
            case MonsterState.Search:
                agent.speed = investigateSpeed;
                agent.SetDestination(pointOfInterest);
                break;
        }
    }

    void UpdatePatrol()
    {
        if (!Arrived()) return;

        if (pauseUntil == 0f) pauseUntil = Time.time + patrolPause;   // stop and "listen"
        if (Time.time < pauseUntil) return;

        pauseUntil = 0f;
        GoToNextPatrolPoint();
    }

    void UpdateInvestigate()
    {
        if (Arrived()) SetState(MonsterState.Search);
    }

    void UpdateChase()
    {
        if (target == null) { SetState(MonsterState.Search); return; }

        agent.SetDestination(target.transform.position);

        if (Vector3.Distance(transform.position, target.transform.position) <= catchDistance)
        {
            CatchPlayer();
            return;
        }

        // Lost sight long enough: go search where it thinks you went
        if (Time.time - lastSeenTime > loseSightTime)
        {
            pointOfInterest = target.transform.position;
            SetState(MonsterState.Search);
        }
    }

    void UpdateSearch()
    {
        if (Time.time - stateStartTime > searchDuration)
        {
            SetState(MonsterState.Patrol);
            return;
        }

        if (!Arrived()) return;

        if (pauseUntil == 0f) pauseUntil = Time.time + Random.Range(0.5f, 1.5f);
        if (Time.time < pauseUntil) return;

        pauseUntil = 0f;
        if (RandomPointNear(pointOfInterest, searchRadius, out var p))
            agent.SetDestination(p);
    }

    // ---------- Senses ----------

    void HearNoise(Vector3 position, float radius)
    {
        if (!IsServer || !IsSpawned) return;
        if (state == MonsterState.Chase) return;

        float dist = Vector3.Distance(transform.position, position);
        if (dist > radius * hearingMultiplier) return;

        pointOfInterest = position;
        SetState(MonsterState.Investigate);
    }

    FirstPersonController FindVisiblePlayer()
    {
        if (Time.time < ignorePlayersUntil) return null;

        Vector3 eye = transform.position + Vector3.up * eyeHeight;
        FirstPersonController best = null;
        float bestDist = float.MaxValue;

        foreach (var player in FirstPersonController.All)
        {
            Vector3 chest = player.transform.position + Vector3.up * (player.IsCrouching ? 0.7f : 1.4f);
            Vector3 toPlayer = chest - eye;
            float dist = toPlayer.magnitude;

            var light = player.GetComponentInChildren<Flashlight>();
            float range = (light != null && light.IsOn) ? flashlightViewDistance : viewDistance;
            if (player.IsCrouching) range *= crouchVisibility;
            if (dist > range) continue;

            bool inCone = Vector3.Angle(transform.forward, toPlayer) <= viewAngle / 2f;
            if (!inCone && dist > closeSenseRange) continue;

            // Line of sight: is a wall in the way?
            if (Physics.Raycast(eye, toPlayer / dist, out var hit, dist + 0.5f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider.GetComponentInParent<FirstPersonController>() != player) continue;
            }

            if (dist < bestDist) { best = player; bestDist = dist; }
        }
        return best;
    }

    // ---------- Helpers ----------

    void GoToNextPatrolPoint()
    {
        if (waypoints != null && waypoints.Length > 0)
        {
            agent.SetDestination(waypoints[waypointIndex].position);
            waypointIndex = (waypointIndex + 1) % waypoints.Length;
        }
        else if (RandomPointNear(patrolCenter != null ? patrolCenter.position : homePosition, wanderRadius, out var p))
        {
            agent.SetDestination(p);
        }
    }

    bool Arrived()
    {
        return !agent.pathPending && agent.remainingDistance <= agent.stoppingDistance + 0.3f;
    }

    static bool RandomPointNear(Vector3 center, float radius, out Vector3 result)
    {
        for (int i = 0; i < 10; i++)
        {
            Vector3 random = center + Random.insideUnitSphere * radius;
            if (NavMesh.SamplePosition(random, out var hit, 3f, NavMesh.AllAreas))
            {
                result = hit.position;
                return true;
            }
        }
        result = center;
        return false;
    }

    void CatchPlayer()
    {
        Debug.Log($"Monster caught player {target.OwnerClientId}");
        target.Caught();                         // that player respawns at PlayerSpawn
        target = null;
        ignorePlayersUntil = Time.time + catchCooldown;
        SetState(MonsterState.Patrol);
    }

    // ---------- Debug drawing in the Scene view ----------

    void OnDrawGizmos()
    {
        Color c = state switch
        {
            MonsterState.Patrol => Color.green,
            MonsterState.Investigate => Color.yellow,
            MonsterState.Chase => Color.red,
            _ => new Color(1f, 0.5f, 0f),   // Search = orange
        };
        Gizmos.color = c;

        Vector3 eye = transform.position + Vector3.up * eyeHeight;
        Quaternion left = Quaternion.Euler(0, -viewAngle / 2f, 0);
        Quaternion right = Quaternion.Euler(0, viewAngle / 2f, 0);
        Gizmos.DrawLine(eye, eye + left * transform.forward * viewDistance);
        Gizmos.DrawLine(eye, eye + right * transform.forward * viewDistance);
        Gizmos.DrawWireSphere(transform.position, catchDistance);

        if (state == MonsterState.Investigate || state == MonsterState.Search)
            Gizmos.DrawWireSphere(pointOfInterest, 0.5f);
    }
}
