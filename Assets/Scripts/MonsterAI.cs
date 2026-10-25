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
    readonly NetworkVariable<Vector3> netHandTarget = new NetworkVariable<Vector3>();
    readonly NetworkVariable<Vector3> netDoorPoint = new NetworkVariable<Vector3>();
    public Vector3 SyncedLookTarget => netLookTarget.Value;
    public bool SyncedHasLookTarget => netHasLookTarget.Value;
    public bool SyncedPeeking => netPeeking.Value;
    public bool SyncedReaching => netReaching.Value;
    public Vector3 SyncedHandTarget => netHandTarget.Value;
    public Vector3 SyncedDoorPoint => netDoorPoint.Value;

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

    [Header("Reach (arm goes in through a doorway and feels around)")]
    public float peekBeforeReach = 1.2f;    // looks in first, then the arm comes in
    public float reachDepth = 6f;           // how far past the doorway the hand can go
    public float handSpeed = 2.5f;          // feeling around blind
    public float handLungeSpeed = 7f;       // going for you when it sees/hears you
    public float handRadius = 0.45f;        // size of the hand for hitting walls
    public float grabRadius = 1.1f;         // hand this close to you = caught
    public float feelDuration = 8f;         // gives up after feeling around this long
    public float peekDuration = 6f;         // waits this long at the door without seeing you (before reaching)
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

    enum ReachPhase { Peek, Enter, Feel, Retract }
    ReachPhase reachPhase;
    float reachPhaseStart;
    Vector3 handPos, handGoal;
    float nextProbeTime;
    float heardTime = -999f;
    readonly RaycastHit[] handHits = new RaycastHit[8];
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
        else if (state == MonsterState.Reach && arrivedTime >= 0f && reachPhase == ReachPhase.Feel)
        {
            look = handGoal;                               // watches where its hand is feeling
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

        bool reaching = state == MonsterState.Reach && arrivedTime >= 0f && reachPhase != ReachPhase.Peek;
        if (netReaching.Value != reaching) netReaching.Value = reaching;
        if (reaching)
        {
            if ((netHandTarget.Value - handPos).sqrMagnitude > 0.0025f) netHandTarget.Value = handPos;
            if (currentReachPoint != null && (netDoorPoint.Value - currentReachPoint.DoorPoint).sqrMagnitude > 0.01f)
                netDoorPoint.Value = currentReachPoint.DoorPoint;
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

        // Walking to the doorway
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
            SetReachPhase(ReachPhase.Peek);
            handPos = HandRest();
        }

        bool seesTarget = target != null && target.IsSpawned && !target.IsGrabbed && Time.time - lastSeenTime < 0.25f;
        if (seesTarget) pointOfInterest = lastSeenPosition;

        // You ran back out into the street? Chase again.
        if (seesTarget && Time.time >= nextPathCheck)
        {
            nextPathCheck = Time.time + 0.3f;
            if (IsReachable(target.transform.position)) { SetState(MonsterState.Chase); return; }
        }

        Vector3 door = currentReachPoint.DoorPoint;
        FaceTowards(seesTarget ? pointOfInterest : currentReachPoint.LookInPoint);

        switch (reachPhase)
        {
            case ReachPhase.Peek:
                // Crouched, eye at the door. Arm comes in after a beat (or right away if it sees you)
                if (seesTarget || Time.time - reachPhaseStart > peekBeforeReach)
                    SetReachPhase(ReachPhase.Enter);
                else if (Time.time - reachPhaseStart > peekDuration)
                    SetState(MonsterState.Patrol);
                break;

            case ReachPhase.Enter:
                // Hand goes to the door opening first, so the arm passes through the frame
                handPos = Vector3.MoveTowards(handPos, door, handLungeSpeed * Time.deltaTime);
                if ((handPos - door).sqrMagnitude < 0.04f)
                {
                    SetReachPhase(ReachPhase.Feel);
                    nextProbeTime = 0f;
                }
                break;

            case ReachPhase.Feel:
                bool knowsWhere = seesTarget || Time.time - heardTime < 1.5f;
                if (knowsWhere)
                {
                    handGoal = seesTarget ? Chest(target) : pointOfInterest + Vector3.up * 0.8f;
                }
                else if (Time.time >= nextProbeTime)
                {
                    handGoal = RandomProbe(door, currentReachPoint.Inward);
                    nextProbeTime = Time.time + Random.Range(0.8f, 1.6f);
                }

                float speed = knowsWhere ? handLungeSpeed : handSpeed;
                Vector3 desired = Vector3.MoveTowards(handPos, handGoal, speed * Time.deltaTime);
                desired = ClampToRoom(door, currentReachPoint.Inward, desired);
                handPos = CollideHand(door, desired);

                // Touched someone?
                var touched = PlayerTouchedByHand();
                if (touched != null)
                {
                    target = touched;
                    CatchPlayer();
                    return;
                }

                if (!knowsWhere && Time.time - reachPhaseStart > feelDuration)
                    SetReachPhase(ReachPhase.Retract);
                break;

            case ReachPhase.Retract:
                // Pull the hand back out through the door, then give up
                Vector3 outTarget = Time.time - reachPhaseStart < 0.5f ? door : HandRest();
                handPos = Vector3.MoveTowards(handPos, outTarget, handLungeSpeed * Time.deltaTime);
                if (Time.time - reachPhaseStart > 1.5f) SetState(MonsterState.Patrol);
                break;
        }
    }

    void SetReachPhase(ReachPhase phase)
    {
        reachPhase = phase;
        reachPhaseStart = Time.time;
    }

    Vector3 HandRest() => transform.position + Vector3.up * 4f + transform.forward * 2f;

    // A random spot in the room to feel around
    Vector3 RandomProbe(Vector3 door, Vector3 inward)
    {
        Vector3 right = Vector3.Cross(Vector3.up, inward);
        return door
            + inward * Random.Range(1f, reachDepth)
            + right * Random.Range(-reachDepth * 0.7f, reachDepth * 0.7f)
            + Vector3.up * Random.Range(-0.9f, 0.5f);
    }

    // Keep the hand inside the room, within arm's reach of the doorway, between floor and ceiling-ish
    Vector3 ClampToRoom(Vector3 door, Vector3 inward, Vector3 p)
    {
        Vector3 v = p - door;
        float along = Vector3.Dot(v, inward);
        if (along < 0f) v -= inward * along;
        if (v.magnitude > reachDepth) v = v.normalized * reachDepth;
        p = door + v;
        p.y = Mathf.Clamp(p.y, door.y - 1.0f, door.y + 0.8f);
        return p;
    }

    // Sweep the hand from the doorway to where it wants to go; stop against walls
    Vector3 CollideHand(Vector3 door, Vector3 desired)
    {
        Vector3 dir = desired - door;
        float dist = dir.magnitude;
        if (dist < 0.01f) return desired;
        dir /= dist;

        int count = Physics.SphereCastNonAlloc(door, handRadius, dir, handHits, dist, ~0, QueryTriggerInteraction.Ignore);
        float nearest = dist;
        for (int i = 0; i < count; i++)
        {
            var h = handHits[i];
            if (h.collider.transform.IsChildOf(transform)) continue;                       // its own body
            if (h.collider.GetComponentInParent<FirstPersonController>() != null) continue; // players don't block it
            if (h.distance <= 0f) continue;                                                // started touching
            nearest = Mathf.Min(nearest, h.distance);
        }

        Vector3 result = door + dir * nearest;
        // Tiny tremble so the hand looks alive while it feels around
        float t = Time.time * 3f;
        result += new Vector3(Mathf.PerlinNoise(t, 0f) - 0.5f, Mathf.PerlinNoise(0f, t) - 0.5f, Mathf.PerlinNoise(t, t) - 0.5f) * 0.15f;
        return result;
    }

    FirstPersonController PlayerTouchedByHand()
    {
        foreach (var p in FirstPersonController.All)
        {
            if (!p.IsSpawned || p.IsGrabbed) continue;
            Vector3 chest = Chest(p);
            Vector3 feet = p.transform.position + Vector3.up * 0.3f;
            // Distance from the hand to the player's body (a line from feet to chest)
            Vector3 closest = ClosestPointOnSegment(feet, chest, handPos);
            if (Vector3.Distance(closest, handPos) > grabRadius) continue;
            if (!LineClear(handPos, p)) continue;
            return p;
        }
        return null;
    }

    static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 p)
    {
        Vector3 ab = b - a;
        float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 0.0001f));
        return a + ab * t;
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
        if (state == MonsterState.Reach) { heardTime = Time.time; return; }   // hand goes for the sound
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
        // Caught through a doorway? The hand drags you out through the frame first
        bool throughDoor = state == MonsterState.Reach && currentReachPoint != null;
        Vector3 door = throughDoor ? currentReachPoint.DoorPoint : Vector3.zero;
        var anim = GetComponentInChildren<MonsterAnimator>();
        float extra = throughDoor && anim != null ? anim.DoorPullExtraTime : 0f;

        CaughtFxRpc(target.transform.position, target.OwnerClientId, throughDoor, door);   // sounds + carry for everyone
        target.Caught(NetworkObject, extra);     // that player gets dragged out / picked up, dies, then respawns
        target = null;
        ignorePlayersUntil = Time.time + catchCooldown;
        SetState(MonsterState.Patrol);

        // Stand still while the grab plays out, then go back to patrolling
        holding = true;
        holdUntil = Time.time + grabHoldTime + extra;
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
    void CaughtFxRpc(Vector3 victimPosition, ulong victimClientId, bool throughDoor, Vector3 doorPoint)
    {
        bool iAmTheVictim = NetworkManager.LocalClientId == victimClientId;
        var monsterAudio = GetComponent<MonsterAudio>();
        if (monsterAudio != null) monsterAudio.PlayCatch(victimPosition, iAmTheVictim);
        var carryAnim = GetComponentInChildren<MonsterAnimator>();
        if (carryAnim != null) carryAnim.StartCarry(victimPosition, throughDoor, doorPoint);
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
        DrawCircle(transform.position, grabRange);
        if (Application.isPlaying && state == MonsterState.Reach && arrivedTime >= 0f)
        {
            Gizmos.DrawWireSphere(handPos, handRadius);
            Gizmos.DrawWireSphere(handPos, grabRadius);
            Gizmos.DrawLine(handPos, handGoal);
        }

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
