using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

public enum MonsterState { Patrol, Investigate, Notice, Chase, Reach, Search }

// The giant. Put on the Monster root together with a NavMeshAgent (Giant agent type),
// NetworkObject and NetworkTransform. The brain only runs on the host; everyone else just sees it move.
//
// Patrol -> hears a noise -> Investigate
// sees you -> Notice (freezes and stares) -> Chase (lunges, then pursues)
// you're somewhere it can't walk (inside a building) -> Reach (crouches at the opening, peeks in, grabs)
// loses you -> Search -> Patrol
[RequireComponent(typeof(NavMeshAgent))]
public class MonsterAI : NetworkBehaviour
{
    [Header("Current state (read only)")]
    public MonsterState state = MonsterState.Patrol;

    // The host writes the state, everyone reads it (used for sounds/animation)
    readonly NetworkVariable<MonsterState> netState = new NetworkVariable<MonsterState>(MonsterState.Patrol);
    public MonsterState SyncedState => netState.Value;

    [Header("Movement")]
    public float walkSpeed = 2.5f;        // slow, lumbering patrol
    public float huntSpeed = 4f;          // investigating / searching
    public float pursuitSpeed = 7f;       // chasing after the lunge
    public float lungeSpeed = 12f;        // the sudden burst when a chase starts
    public float lungeTime = 1.5f;
    public float turnSpeed = 90f;         // degrees/sec when it turns to stare

    [Header("Vision")]
    public float eyeHeightStanding = 13f;   // sees over walls and low buildings
    public float eyeHeightPeeking = 3f;     // crouched at a doorway, peering in
    public float sightRange = 35f;
    public float flashlightSightRange = 50f;
    public float viewAngle = 120f;          // full cone width (horizontal)
    public float crouchVisibility = 0.6f;   // crouching shrinks how far it can see you
    public float feelRange = 6f;            // notices you this close even from behind

    [Header("Hearing")]
    public float hearingMultiplier = 1f;

    [Header("Patrol")]
    public Transform[] waypoints;           // optional; empty = wander randomly
    public Transform patrolCenter;          // optional; wanders around this (defaults to spawn point)
    public float wanderRadius = 30f;
    public float patrolPause = 3f;

    [Header("Notice (freeze and stare)")]
    public float noticeTime = 1.5f;         // hide during the stare and it loses you

    [Header("Chase")]
    public float loseSightTime = 2f;
    public float grabRange = 5f;            // how far its hand reaches in the open
    public float catchCooldown = 4f;

    [Header("Reach (you're somewhere it can't walk, e.g. inside a building)")]
    public float reachRange = 7f;           // how far its arm gets into an opening
    public float reachWindup = 0.8f;        // time between seeing you in range and grabbing
    public float peekDuration = 6f;         // how long it waits at the opening without seeing you

    [Header("Search")]
    public float searchDuration = 15f;
    public float searchArea = 15f;

    NavMeshAgent agent;
    NavMeshPath path;
    FirstPersonController target;
    Vector3 pointOfInterest;
    Vector3 lastSeenPosition;
    float lastSeenTime = -999f;
    float stateStartTime;
    float pauseUntil;
    int waypointIndex;
    Vector3 homePosition;
    float ignorePlayersUntil;
    float nextPathCheck;
    bool targetReachable = true;
    float windupStart = -1f;
    float arrivedTime = -1f;

    // Read by animation/IK later
    public bool IsPeeking => state == MonsterState.Reach && arrivedTime >= 0f;
    float EyeHeight => IsPeeking ? eyeHeightPeeking : eyeHeightStanding;

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        path = new NavMeshPath();
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
        agent.angularSpeed = 120f;
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
            lastSeenPosition = seen.transform.position;
            if (state == MonsterState.Patrol || state == MonsterState.Investigate || state == MonsterState.Search)
                SetState(MonsterState.Notice);
        }

        switch (state)
        {
            case MonsterState.Patrol:      UpdatePatrol(); break;
            case MonsterState.Investigate: UpdateInvestigate(); break;
            case MonsterState.Notice:      UpdateNotice(); break;
            case MonsterState.Chase:       UpdateChase(); break;
            case MonsterState.Reach:       UpdateReach(); break;
            case MonsterState.Search:      UpdateSearch(); break;
        }
    }

    // ---------- States ----------

    void SetState(MonsterState newState)
    {
        state = newState;
        netState.Value = newState;
        stateStartTime = Time.time;
        pauseUntil = 0f;
        windupStart = -1f;
        arrivedTime = -1f;
        agent.isStopped = false;
        agent.updateRotation = true;

        switch (newState)
        {
            case MonsterState.Patrol:
                agent.speed = walkSpeed;
                agent.acceleration = 4f;
                GoToNextPatrolPoint();
                break;
            case MonsterState.Investigate:
                agent.speed = huntSpeed;
                agent.acceleration = 6f;
                agent.SetDestination(pointOfInterest);
                break;
            case MonsterState.Notice:
                // Dead stop. Turn. Stare.
                agent.ResetPath();
                agent.velocity = Vector3.zero;
                agent.isStopped = true;
                agent.updateRotation = false;
                break;
            case MonsterState.Chase:
                agent.acceleration = 30f;
                targetReachable = true;
                nextPathCheck = 0f;
                break;
            case MonsterState.Reach:
                agent.speed = pursuitSpeed;
                agent.acceleration = 10f;
                agent.SetDestination(pointOfInterest);   // ends at the closest spot it can stand
                break;
            case MonsterState.Search:
                agent.speed = huntSpeed;
                agent.acceleration = 6f;
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
        if (!Arrived()) return;
        // Noise came from somewhere it can't walk (inside a building)? Go peek in.
        SetState(IsReachable(pointOfInterest) ? MonsterState.Search : MonsterState.Reach);
    }

    void UpdateNotice()
    {
        FaceTowards(lastSeenPosition);
        if (Time.time - stateStartTime < noticeTime) return;

        // Still see you after the stare? Lunge. Hid in time? It goes looking.
        if (Time.time - lastSeenTime <= loseSightTime)
        {
            SetState(MonsterState.Chase);
        }
        else
        {
            pointOfInterest = lastSeenPosition;
            SetState(IsReachable(pointOfInterest) ? MonsterState.Search : MonsterState.Reach);
        }
    }

    void UpdateChase()
    {
        if (target == null || !target.IsSpawned) { SetState(MonsterState.Search); return; }

        Vector3 playerPos = target.transform.position;

        if (CanGrab(target, grabRange, transform.position + Vector3.up * 4f))
        {
            CatchPlayer();
            return;
        }

        bool lunging = Time.time - stateStartTime < lungeTime;
        agent.speed = lunging ? lungeSpeed : pursuitSpeed;

        if (Time.time >= nextPathCheck)
        {
            nextPathCheck = Time.time + 0.3f;
            targetReachable = IsReachable(playerPos);
        }
        agent.SetDestination(playerPos);   // if unreachable, this walks to the closest point

        // Lost sight: search where it thinks you went, or peek in if you went indoors
        if (Time.time - lastSeenTime > loseSightTime)
        {
            pointOfInterest = playerPos;
            SetState(targetReachable ? MonsterState.Search : MonsterState.Reach);
            return;
        }

        // Can see you but can't walk to you (you're in a doorway / building): reach for you
        if (!targetReachable && Arrived())
        {
            pointOfInterest = playerPos;
            SetState(MonsterState.Reach);
        }
    }

    void UpdateReach()
    {
        // Walking to the opening
        if (arrivedTime < 0f)
        {
            if (!Arrived())
            {
                if (Time.time - stateStartTime > 15f) SetState(MonsterState.Search);   // couldn't get there
                return;
            }
            arrivedTime = Time.time;
            agent.isStopped = true;
            agent.updateRotation = false;
        }

        // Crouched at the opening, peering in
        FaceTowards(pointOfInterest);

        bool seesTarget = target != null && target.IsSpawned && Time.time - lastSeenTime < 0.25f;
        if (seesTarget)
        {
            pointOfInterest = lastSeenPosition;

            if (CanGrab(target, reachRange, transform.position + Vector3.up * 2.5f))
            {
                if (windupStart < 0f) windupStart = Time.time;
                else if (Time.time - windupStart >= reachWindup) { CatchPlayer(); return; }
            }
            else windupStart = -1f;

            // You ran back out into the street? Chase again.
            if (Time.time >= nextPathCheck)
            {
                nextPathCheck = Time.time + 0.3f;
                if (IsReachable(target.transform.position)) { SetState(MonsterState.Chase); return; }
            }
        }
        else windupStart = -1f;

        // Nothing for a while: give up and search the area around the building
        if (Time.time - Mathf.Max(arrivedTime, lastSeenTime) > peekDuration)
            SetState(MonsterState.Search);
    }

    void UpdateSearch()
    {
        if (Time.time - stateStartTime > searchDuration)
        {
            SetState(MonsterState.Patrol);
            return;
        }

        if (!Arrived()) return;

        if (pauseUntil == 0f) pauseUntil = Time.time + Random.Range(1f, 2.5f);
        if (Time.time < pauseUntil) return;

        pauseUntil = 0f;
        if (RandomReachablePoint(pointOfInterest, searchArea, out var p))
            agent.SetDestination(p);
    }

    // ---------- Senses ----------

    void HearNoise(Vector3 position, float radius)
    {
        if (!IsServer || !IsSpawned) return;
        if (state == MonsterState.Chase || state == MonsterState.Notice) return;

        float dist = Vector3.Distance(transform.position, position);
        if (dist > radius * hearingMultiplier) return;

        pointOfInterest = position;
        if (state == MonsterState.Reach) return;   // already peeking: just look toward the sound
        SetState(MonsterState.Investigate);
    }

    FirstPersonController FindVisiblePlayer()
    {
        if (Time.time < ignorePlayersUntil) return null;

        Vector3 eye = transform.position + Vector3.up * EyeHeight;
        FirstPersonController best = null;
        float bestDist = float.MaxValue;

        foreach (var player in FirstPersonController.All)
        {
            if (!player.IsSpawned) continue;

            Vector3 chest = Chest(player);
            Vector3 toPlayer = chest - eye;
            float dist = toPlayer.magnitude;

            var light = player.GetComponentInChildren<Flashlight>();
            float range = (light != null && light.IsOn) ? flashlightSightRange : sightRange;
            if (player.IsCrouching) range *= crouchVisibility;
            if (dist > range) continue;

            // Cone is horizontal, so it can look straight down at you
            Vector3 flat = new Vector3(toPlayer.x, 0f, toPlayer.z);
            bool inCone = flat.sqrMagnitude < 0.01f || Vector3.Angle(transform.forward, flat) <= viewAngle / 2f;
            if (!inCone && flat.magnitude > feelRange) continue;

            if (!LineClear(eye, player)) continue;

            if (dist < bestDist) { best = player; bestDist = dist; }
        }
        return best;
    }

    bool CanGrab(FirstPersonController player, float range, Vector3 handOrigin)
    {
        Vector3 p = player.transform.position;
        float flatDist = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(transform.position.x, transform.position.z));
        if (flatDist > range) return false;
        return LineClear(handOrigin, player);   // no grabbing through walls
    }

    // Is there a clear line from 'from' to the player's chest?
    static bool LineClear(Vector3 from, FirstPersonController player)
    {
        Vector3 to = Chest(player) - from;
        float dist = to.magnitude;
        if (dist < 0.01f) return true;
        if (Physics.Raycast(from, to / dist, out var hit, dist + 0.5f, ~0, QueryTriggerInteraction.Ignore))
            return hit.collider.GetComponentInParent<FirstPersonController>() == player;
        return true;
    }

    static Vector3 Chest(FirstPersonController p) =>
        p.transform.position + Vector3.up * (p.IsCrouching ? 0.7f : 1.4f);

    // ---------- Navigation helpers ----------

    NavMeshQueryFilter Filter => new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = agent.areaMask };

    // Can the giant actually walk all the way to this spot?
    bool IsReachable(Vector3 position)
    {
        if (!NavMesh.SamplePosition(position, out var hit, 1.5f, Filter)) return false;
        return agent.CalculatePath(hit.position, path) && path.status == NavMeshPathStatus.PathComplete;
    }

    bool RandomReachablePoint(Vector3 center, float radius, out Vector3 result)
    {
        for (int i = 0; i < 15; i++)
        {
            Vector3 random = center + Random.insideUnitSphere * radius;
            if (NavMesh.SamplePosition(random, out var hit, 4f, Filter) &&
                agent.CalculatePath(hit.position, path) && path.status == NavMeshPathStatus.PathComplete)
            {
                result = hit.position;
                return true;
            }
        }
        result = center;
        return false;
    }

    void GoToNextPatrolPoint()
    {
        if (waypoints != null && waypoints.Length > 0)
        {
            agent.SetDestination(waypoints[waypointIndex].position);
            waypointIndex = (waypointIndex + 1) % waypoints.Length;
        }
        else if (RandomReachablePoint(patrolCenter != null ? patrolCenter.position : homePosition, wanderRadius, out var p))
        {
            agent.SetDestination(p);
        }
    }

    bool Arrived()
    {
        return !agent.pathPending && agent.remainingDistance <= agent.stoppingDistance + 0.5f;
    }

    void FaceTowards(Vector3 position)
    {
        Vector3 dir = position - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.01f) return;
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, Quaternion.LookRotation(dir), turnSpeed * Time.deltaTime);
    }

    // ---------- Catching ----------

    void CatchPlayer()
    {
        Debug.Log($"Giant grabbed player {target.OwnerClientId}");
        CaughtFxRpc(target.transform.position, target.OwnerClientId);   // death sounds for everyone
        target.Caught();                         // that player respawns at PlayerSpawn
        target = null;
        ignorePlayersUntil = Time.time + catchCooldown;
        SetState(MonsterState.Patrol);
    }

    [Rpc(SendTo.Everyone)]
    void CaughtFxRpc(Vector3 victimPosition, ulong victimClientId)
    {
        bool iAmTheVictim = NetworkManager.LocalClientId == victimClientId;
        var monsterAudio = GetComponent<MonsterAudio>();
        if (monsterAudio != null) monsterAudio.PlayCatch(victimPosition, iAmTheVictim);
    }

    // ---------- Debug drawing in the Scene view ----------

    void OnDrawGizmos()
    {
        Color c = state switch
        {
            MonsterState.Patrol => Color.green,
            MonsterState.Investigate => Color.yellow,
            MonsterState.Notice => Color.magenta,
            MonsterState.Chase => Color.red,
            MonsterState.Reach => Color.cyan,
            _ => new Color(1f, 0.5f, 0f),   // Search = orange
        };
        Gizmos.color = c;

        float eyeH = Application.isPlaying ? EyeHeight : eyeHeightStanding;
        Vector3 eye = transform.position + Vector3.up * eyeH;
        Vector3 fwd = transform.forward;
        Gizmos.DrawLine(eye, eye + Quaternion.Euler(0, -viewAngle / 2f, 0) * fwd * sightRange);
        Gizmos.DrawLine(eye, eye + Quaternion.Euler(0, viewAngle / 2f, 0) * fwd * sightRange);
        Gizmos.DrawWireSphere(eye, 0.5f);

        // Grab / reach range on the ground
        DrawCircle(transform.position, state == MonsterState.Reach ? reachRange : grabRange);

        if (state == MonsterState.Investigate || state == MonsterState.Search || state == MonsterState.Reach)
            Gizmos.DrawWireSphere(pointOfInterest, 0.5f);
    }

    static void DrawCircle(Vector3 center, float radius)
    {
        const int segments = 32;
        Vector3 prev = center + new Vector3(radius, 0.1f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float a = i * Mathf.PI * 2f / segments;
            Vector3 next = center + new Vector3(Mathf.Cos(a) * radius, 0.1f, Mathf.Sin(a) * radius);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }
}
