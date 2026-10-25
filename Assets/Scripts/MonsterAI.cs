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

    // Synced for animation on every computer: where its head looks / its hand reaches
    readonly NetworkVariable<Vector3> netLookTarget = new NetworkVariable<Vector3>();
    readonly NetworkVariable<bool> netHasLookTarget = new NetworkVariable<bool>();
    readonly NetworkVariable<bool> netPeeking = new NetworkVariable<bool>();
    readonly NetworkVariable<bool> netReaching = new NetworkVariable<bool>();
    public Vector3 SyncedLookTarget => netLookTarget.Value;
    public bool SyncedHasLookTarget => netHasLookTarget.Value;
    public bool SyncedPeeking => netPeeking.Value;
    public bool SyncedReaching => netReaching.Value;

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
    public float grabWindup = 0.5f;         // stops, hand comes down... still there after this? caught
    public float grabEscapeRange = 7f;      // you must get this far away during the wind-up to dodge
    public float grabHoldTime = 3.5f;       // stands still holding you up after a catch
    public float catchCooldown = 4f;

    [Header("Reach (you're somewhere it can't walk, e.g. inside a building)")]
    public float reachRange = 7f;           // how far its arm gets into an opening
    public float reachWindup = 0.8f;        // time between seeing you in range and grabbing
    public float peekDuration = 6f;         // how long it waits at the opening without seeing you
    public float doorSearchRadius = 15f;    // only uses doorways (ReachPoints) this close to where you are

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
    float grabStart = -1f;
    ReachPoint currentReachPoint;
    float holdUntil;
    bool holding;

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
        agent.angularSpeed = 70f;   // slow, heavy turns
        SetState(MonsterState.Patrol);
    }

    void Update()
    {
        if (!IsServer || !IsSpawned) return;

        // Standing still holding its victim after a catch
        if (holding)
        {
            if (Time.time < holdUntil) { UpdateSyncedAnimationState(); return; }
            holding = false;
            agent.isStopped = false;
            agent.updateRotation = true;
        }

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

        UpdateSyncedAnimationState();
    }

    void UpdateSyncedAnimationState()
    {
        Vector3 look = Vector3.zero;
        bool hasLook = false;

        bool recentlySeen = target != null && target.IsSpawned && Time.time - lastSeenTime < 2f;
        if (recentlySeen && (state == MonsterState.Notice || state == MonsterState.Chase || state == MonsterState.Reach))
        {
            look = Chest(target);          // stares right at you
            hasLook = true;
        }
        else if (state == MonsterState.Investigate || state == MonsterState.Search || state == MonsterState.Reach)
        {
            look = pointOfInterest + Vector3.up * 1.5f;   // looks where it thinks you are
            hasLook = true;
        }

        if (netHasLookTarget.Value != hasLook) netHasLookTarget.Value = hasLook;
        if (hasLook && (netLookTarget.Value - look).sqrMagnitude > 0.04f) netLookTarget.Value = look;

        bool peeking = IsPeeking;
        if (netPeeking.Value != peeking) netPeeking.Value = peeking;

        bool reaching = state == MonsterState.Reach && windupStart >= 0f;
        if (netReaching.Value != reaching) netReaching.Value = reaching;
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
        grabStart = -1f;
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
                // Only peek in through a doorway. No doorway near you? Back to patrolling.
                currentReachPoint = FindReachPoint(pointOfInterest);
                if (currentReachPoint == null) { SetState(MonsterState.Patrol); return; }
                agent.speed = pursuitSpeed;
                agent.acceleration = 10f;
                agent.SetDestination(StandPoint(currentReachPoint));
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
        Vector3 hand = transform.position + Vector3.up * 4f;

        // Mid-grab: stopped, turning to face you, hand coming down
        if (grabStart >= 0f)
        {
            FaceTowards(playerPos);
            if (Time.time - grabStart < grabWindup) return;

            grabStart = -1f;
            if (CanGrab(target, grabEscapeRange, hand)) { CatchPlayer(); return; }

            // Missed! Back to the chase
            agent.isStopped = false;
            agent.updateRotation = true;
        }
        else if (CanGrab(target, grabRange, hand))
        {
            grabStart = Time.time;
            agent.velocity = Vector3.zero;
            agent.isStopped = true;
            agent.updateRotation = false;
            GrabAnimRpc();
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
        if (currentReachPoint == null) { SetState(MonsterState.Patrol); return; }

        // Walking to the opening
        if (arrivedTime < 0f)
        {
            if (!Arrived())
            {
                if (Time.time - stateStartTime > 15f) SetState(MonsterState.Patrol);   // couldn't get there
                return;
            }
            arrivedTime = Time.time;
            agent.isStopped = true;
            agent.updateRotation = false;
        }

        bool seesTarget = target != null && target.IsSpawned && Time.time - lastSeenTime < 0.25f;

        // Crouched at the doorway, peering in (toward you if it can see you)
        FaceTowards(seesTarget ? pointOfInterest : currentReachPoint.LookInPoint);

        if (seesTarget)
        {
            pointOfInterest = lastSeenPosition;

            if (CanGrab(target, reachRange, transform.position + Vector3.up * 2.5f))
            {
                if (windupStart < 0f) windupStart = Time.time;
                else if (Time.time - windupStart >= reachWindup) { GrabAnimRpc(); CatchPlayer(); return; }
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

        // Nothing for a while: give up and go back to patrolling
        if (Time.time - Mathf.Max(arrivedTime, lastSeenTime) > peekDuration)
            SetState(MonsterState.Patrol);
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
            if (!player.IsSpawned || player.IsGrabbed) continue;

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

    // Nearest doorway to 'position' that the giant can actually walk to
    ReachPoint FindReachPoint(Vector3 position)
    {
        ReachPoint best = null;
        float bestDist = doorSearchRadius;
        foreach (var rp in ReachPoint.All)
        {
            float d = Vector3.Distance(position, rp.transform.position);
            if (d > bestDist) continue;
            if (!IsReachable(rp.transform.position, 4f)) continue;
            best = rp;
            bestDist = d;
        }
        return best;
    }

    Vector3 StandPoint(ReachPoint rp)
    {
        return NavMesh.SamplePosition(rp.transform.position, out var hit, 4f, Filter) ? hit.position : rp.transform.position;
    }

    NavMeshQueryFilter Filter => new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = agent.areaMask };

    // Can the giant actually walk all the way to this spot?
    bool IsReachable(Vector3 position, float snapDistance = 1.5f)
    {
        if (!NavMesh.SamplePosition(position, out var hit, snapDistance, Filter)) return false;
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
        target.Caught(NetworkObject);            // that player gets picked up, dies, then respawns
        target = null;
        ignorePlayersUntil = Time.time + catchCooldown;
        SetState(MonsterState.Patrol);

        // Stand still while the grab plays out, then go back to patrolling
        holding = true;
        holdUntil = Time.time + grabHoldTime;
        agent.velocity = Vector3.zero;
        agent.isStopped = true;
        agent.updateRotation = false;
    }

    [Rpc(SendTo.Everyone)]
    void GrabAnimRpc()
    {
        var anim = GetComponentInChildren<MonsterAnimator>();
        if (anim != null) anim.PlayGrab();
    }

    [Rpc(SendTo.Everyone)]
    void CaughtFxRpc(Vector3 victimPosition, ulong victimClientId)
    {
        bool iAmTheVictim = NetworkManager.LocalClientId == victimClientId;
        var monsterAudio = GetComponent<MonsterAudio>();
        if (monsterAudio != null) monsterAudio.PlayCatch(victimPosition, iAmTheVictim);
        var anim = GetComponentInChildren<MonsterAnimator>();
        if (anim != null) anim.StartCarry(victimPosition);
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
