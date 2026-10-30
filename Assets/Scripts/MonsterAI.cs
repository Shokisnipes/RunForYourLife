using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

public enum MonsterState { Patrol, Investigate, Notice, Chase, Reach, Search, Carry }

// The giant. Put on the Monster root together with a NavMeshAgent (Giant agent type),
// NetworkObject and NetworkTransform. The brain only runs on the host; everyone else just sees it move.
//
// Patrol -> hears a noise -> Investigate
// sees you -> Notice (freezes and stares) -> Chase (lunges, then pursues)
// you're somewhere it can't walk (inside a building) -> Reach (crouches at the opening, peeks in, grabs)
// loses you -> Search -> Patrol
// catches you -> Carry (walks off with you to a hiding spot, stoops and puts you down) -> Patrol
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
    // False when this run's genome isn't a Reacher: the giant stays hidden all game
    readonly NetworkVariable<bool> netInPlay = new NetworkVariable<bool>(true);
    bool inPlay = true;
    // Holding a caught player until it puts them down at a hiding spot (MonsterAnimator keeps the hand closed)
    readonly NetworkVariable<bool> netCarrying = new NetworkVariable<bool>();
    public bool SyncedCarrying => netCarrying.Value;

    [Header("Movement")]
    public float walkSpeed = 2.5f;        // slow, lumbering patrol
    public float huntSpeed = 4f;          // investigating / searching
    public float pursuitSpeed = 8.5f;     // chasing after the lunge (players sprint at 6: it gains on you)
    public float lungeSpeed = 12f;        // the sudden burst when a chase starts
    public float lungeTime = 1.5f;
    public float turnSpeed = 90f;         // degrees/sec when it turns to stare
    [Tooltip("Chasing with no building within openGroundRadius of it: it sprints this fast (players sprint at 6). " +
             "Out in the open you can't outrun it; get between buildings.")]
    public float openGroundSpeed = 16f;
    [Tooltip("No building this close (m) to the giant = open ground.")]
    public float openGroundRadius = 8f;

    [Header("Vision")]
    public float eyeHeightStanding = 13f;   // sees over walls and low buildings
    public float eyeHeightPeeking = 3f;     // crouched at a doorway, peering in
    public float sightRange = 35f;
    public float flashlightSightRange = 50f;
    public float litSightRange = 45f;       // standing under a lit street lamp
    public float viewAngle = 120f;          // full cone width (horizontal)
    public float crouchVisibility = 0.6f;   // crouching shrinks how far it can see you
    public float feelRange = 6f;            // notices you this close even from behind
    [Tooltip("Once it's staring at you or chasing you, it keeps track of you this many times further than it first " +
             "spots you. Without it, backing away during the stare puts you just out of range and it loses you, " +
             "searches, spots you again and stares again (run, stop, run). Hiding behind something still loses it.")]
    public float chaseSightMultiplier = 1.6f;

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
    [Tooltip("Seconds from starting the grab to the hand closing. It keeps charging straight ahead meanwhile.")]
    public float grabWindup = 0.5f;
    [Tooltip("Running grab: you're caught if you're this close (m) to its line of charge when the hand closes. " +
             "Sprint sideways out of its path during the wind-up to dodge.")]
    public float grabWidth = 2.5f;
    [Tooltip("Running grab: how far (m) past the reach range ahead of it the hand still catches you.")]
    public float grabOvershoot = 1.5f;
    [Tooltip("Chasing: you still count as out in the street when you're this close (m) to ground it can stand on, with " +
             "nothing solid in between. Covers the pavement along walls where its big body doesn't fit; without it the " +
             "chase stalls there (run, stop, run). Keep it small or it stands outside doorways instead of reaching in.")]
    public float chaseSnapDistance = 3f;
    [Tooltip("Running grab missed but you were still this close (m) to where its hand came down: it slams the " +
             "ground beside you (shake + thud for everyone).")]
    public float nearMissDistance = 6f;
    [Tooltip("After the slam it stays down with its hand on the ground this long (s) before it pushes up and chases " +
             "again: your window to get away. Gets shorter as it feeds (divided by its speed bonus).")]
    public float slamPause = 1.2f;
    public float grabEscapeRange = 7f;      // Reach (doorway) grabs only: get this far away to dodge
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
    [Tooltip("Extra distance it stands back from doorways. Raise it for a lying-down peek pose.")]
    public float peekStandBack = 0f;

    [Header("Carry off (after a catch)")]
    [Tooltip("Walking speed while it carries you off.")]
    public float carrySpeed = 4f;
    [Tooltip("It hides you at least this far (m) from where it caught you...")]
    public float minCarryDistance = 35f;
    [Tooltip("...and at most this far, if it can (m).")]
    public float maxCarryDistance = 110f;
    [Tooltip("A hiding spot counts only if it can stand this close (m) to it: its hand has to reach the ground there.")]
    public float putDownReach = 5f;
    [Tooltip("Seconds to stoop, lower you to the ground and let go.")]
    public float putDownTime = 2.5f;
    [Tooltip("Gives up walking after this long (s) and puts you down wherever it is.")]
    public float carryGiveUpTime = 60f;

    [Header("Search")]
    public float searchDuration = 15f;
    public float searchArea = 15f;

    [Header("It feeds (every catch makes it stronger for the rest of the night)")]
    [Range(0f, 0.5f), Tooltip("Walk, hunt, chase and lunge speed added per catch (0.1 = +10%). Carrying you off stays the same.")]
    public float feedSpeedBonus = 0.1f;
    [Range(0f, 0.5f), Tooltip("Sight and hearing range added per catch (0.1 = +10%).")]
    public float feedSenseBonus = 0.1f;
    [Range(0f, 0.5f), Tooltip("The stare before it charges gets this much shorter per catch (0.15 = 15% shorter each time).")]
    public float feedStareCut = 0.15f;
    [Tooltip("The stare never gets shorter than this (seconds): you always get a moment to hide.")]
    public float minNoticeTime = 0.5f;
    [Tooltip("It stops getting stronger after this many catches in one night.")]
    public int maxFeeds = 5;

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
    Vector3 grabDir;   // running grab: the line it charges along (locked when the grab starts)
    float slamRecoverUntil = -1f;   // near miss: down on its hand until then
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
    // Carry off
    FirstPersonController carryVictim;
    Vector3 carrySpot;
    float putDownStart = -1f;

    // Read by animation/IK later
    public bool IsPeeking => state == MonsterState.Reach && arrivedTime >= 0f;
    float EyeHeight => IsPeeking ? eyeHeightPeeking : eyeHeightStanding;

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        path = new NavMeshPath();
        homePosition = transform.position;
    }

    // Host: CityManager moved us onto a new city; this is where we started
    public void SetHome(Vector3 p) => homePosition = p;

    // Base senses are remembered the first time, so applying a new genome each run never stacks multipliers.
    bool baseSensesSaved;
    float baseSight, baseFlashSight, baseLitSight, baseHearing, baseNotice;
    MonsterGenome genome;
    int feeds;   // catches this night (It feeds)

    // Catches so far this night, for tests and the HUD
    public int Feeds => feeds;
    float FedSpeed => 1f + feedSpeedBonus * feeds;

    // Host only: called by GenomeManager at game start.
    public void ApplyGenome(MonsterGenome genome)
    {
        if (!baseSensesSaved)
        {
            baseSight = sightRange; baseFlashSight = flashlightSightRange;
            baseLitSight = litSightRange; baseHearing = hearingMultiplier;
            baseNotice = noticeTime;
            baseSensesSaved = true;
        }
        this.genome = genome;
        feeds = 0;   // a new run: it starts hungry again
        ApplySenses();
        inPlay = genome.access == GenomeAccess.Reacher;
    }

    // Genome senses, plus whatever it has gained from feeding tonight
    void ApplySenses()
    {
        float sight = genome != null ? genome.sightRangeMultiplier : 1f;
        float hearing = genome != null ? genome.hearingMultiplier : 1f;
        float fed = 1f + feedSenseBonus * feeds;
        sightRange = baseSight * sight * fed;
        flashlightSightRange = baseFlashSight * sight * fed;
        litSightRange = baseLitSight * sight * fed;
        hearingMultiplier = baseHearing * hearing * fed;
        noticeTime = Mathf.Max(minNoticeTime, baseNotice * Mathf.Pow(1f - feedStareCut, feeds));
    }

    // Host: it caught someone. Faster, sharper and quicker to charge for the rest of the night.
    void Feed()
    {
        if (!baseSensesSaved || feeds >= maxFeeds) return;
        feeds++;
        ApplySenses();
        Debug.Log($"Giant fed ({feeds}/{maxFeeds}): speed x{FedSpeed:0.00}, sight {sightRange:0.0} m, hearing x{hearingMultiplier:0.00}, stare {noticeTime:0.00} s");
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
        agent.enabled = true;       // may have been turned off by an earlier session as a client
        holding = false;
        carryVictim = null;
        netCarrying.Value = false;
        agent.angularSpeed = 70f;   // slow, heavy turns
        feeds = 0;
        if (baseSensesSaved) ApplySenses();
        SetState(MonsterState.Patrol);
    }

    bool shownActive = true;
    readonly System.Collections.Generic.List<Renderer> hiddenRenderers = new System.Collections.Generic.List<Renderer>();

    // During the prep phase the monster is asleep: hidden and silent on every client, frozen on the host.
    void UpdateAsleep(bool active)
    {
        if (active == shownActive) return;
        shownActive = active;
        // Only bring back the renderers that were on before (some, like a debug cylinder, are off on purpose)
        if (!active)
        {
            hiddenRenderers.Clear();
            foreach (var r in GetComponentsInChildren<Renderer>(true))
                if (r.enabled) { hiddenRenderers.Add(r); r.enabled = false; }
        }
        else
        {
            foreach (var r in hiddenRenderers) if (r != null) r.enabled = true;
            hiddenRenderers.Clear();
        }
        foreach (var a in GetComponentsInChildren<AudioSource>(true)) a.mute = !active;
        if (IsServer && agent.enabled && agent.isOnNavMesh)
        {
            agent.isStopped = !active;
            if (active) SetState(MonsterState.Patrol);
        }
    }

    void Update()
    {
        if (IsServer && IsSpawned && netInPlay.Value != inPlay) netInPlay.Value = inPlay;
        var flow = GameFlow.Instance;
        bool active = (flow == null || flow.MonsterActive) && netInPlay.Value;
        if (IsSpawned) UpdateAsleep(active);
        // Run over (dawn) or put to sleep while holding someone: let them go where they are
        if (IsServer && IsSpawned && !active && carryVictim != null) PutDownVictim(carryVictim.transform.position);
        if (!IsServer || !IsSpawned || !active) return;

        // Standing still holding its victim after a catch, then off it goes with them
        if (holding)
        {
            if (Time.time < holdUntil) { UpdateSyncedAnimationState(); return; }
            holding = false;
            agent.isStopped = false;
            agent.updateRotation = true;
            if (carryVictim != null) SetState(MonsterState.Carry);
        }

        // Busy carrying: it doesn't look for anyone else
        if (state == MonsterState.Carry)
        {
            UpdateCarry();
            UpdateSyncedAnimationState();
            return;
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
        if (reaching && (netHandTarget.Value - handPos).sqrMagnitude > 0.0025f) netHandTarget.Value = handPos;
        // The doorway posture needs the door while peeking too, not just once the arm goes in
        if ((peeking || reaching) && currentReachPoint != null && (netDoorPoint.Value - currentReachPoint.DoorPoint).sqrMagnitude > 0.01f)
            netDoorPoint.Value = currentReachPoint.DoorPoint;
    }

    // ---------- States ----------

    void SetState(MonsterState newState)
    {
        // Victim in hand: only carrying them off (or the short hold, in Patrol) until it puts them down
        if (carryVictim != null && newState != MonsterState.Carry && newState != MonsterState.Patrol) return;
        state = newState;
        netState.Value = newState;
        stateStartTime = Time.time;
        pauseUntil = 0f;
        windupStart = -1f;
        arrivedTime = -1f;
        grabStart = -1f;
        slamRecoverUntil = -1f;
        agent.isStopped = false;
        agent.updateRotation = true;
        agent.autoBraking = newState != MonsterState.Chase;   // chasing: full speed right up to you, no easing off

        switch (newState)
        {
            case MonsterState.Patrol:
                agent.speed = walkSpeed * FedSpeed;
                agent.acceleration = 4f;
                GoToNextPatrolPoint();
                break;
            case MonsterState.Investigate:
                agent.speed = huntSpeed * FedSpeed;
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
                agent.speed = pursuitSpeed * FedSpeed;
                agent.acceleration = 10f;
                agent.SetDestination(StandPoint(currentReachPoint));
                break;
            case MonsterState.Search:
                agent.speed = huntSpeed * FedSpeed;
                agent.acceleration = 6f;
                agent.SetDestination(pointOfInterest);
                break;
            case MonsterState.Carry:
                agent.speed = carrySpeed;
                agent.acceleration = 4f;
                putDownStart = -1f;
                if (PickHidingSpot(transform.position, out carrySpot, out Vector3 stand))
                    agent.SetDestination(stand);
                else
                {
                    // Nowhere to take them: put them down right here
                    carrySpot = carryVictim != null ? carryVictim.transform.position : transform.position;
                    agent.ResetPath();
                }
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

        // Down on its hand after a near miss: it doesn't move or look for you until it's back up
        if (slamRecoverUntil >= 0f)
        {
            if (Time.time < slamRecoverUntil) return;
            slamRecoverUntil = -1f;
            agent.isStopped = false;
            agent.updateRotation = true;
            nextPathCheck = 0f;
        }

        // Mid-grab: it doesn't stop. It charges on in a straight line while the hand swipes down,
        // so running straight away gets you caught; sprinting sideways out of its path dodges it.
        if (grabStart >= 0f)
        {
            if (Time.time - grabStart < grabWindup) return;

            grabStart = -1f;
            if (InGrabPath(target, hand)) { CatchPlayer(); return; }
            // Missed! The hand comes down beside you and slams the ground. Back to the chase (it re-paths below).
            // The hand lands on its line of charge, level with you (or as far ahead as it reaches)
            float along = Mathf.Clamp(Vector3.Dot(playerPos - transform.position, grabDir), 3f, grabRange);
            Vector3 slam = transform.position + grabDir * along;
            slam.y = playerPos.y;
            if (Vector3.Distance(slam, playerPos) < nearMissDistance)
            {
                if (NavMesh.SamplePosition(slam, out var ground, 2f, NavMesh.AllAreas)) slam.y = ground.position.y;
                float pause = slamPause / FedSpeed;
                slamRecoverUntil = Time.time + pause;
                agent.ResetPath();
                agent.velocity = Vector3.zero;   // the hand hitting the street stops it dead
                agent.isStopped = true;
                agent.updateRotation = false;
                SlamRpc(slam, pause);
                return;
            }
        }
        else if (CanGrab(target, grabRange, hand))
        {
            grabStart = Time.time;
            grabDir = playerPos - transform.position;
            grabDir.y = 0f;
            grabDir = grabDir.sqrMagnitude > 0.01f ? grabDir.normalized : transform.forward;
            // Commit to the line: aim well past where you are now and stop steering
            agent.SetDestination(transform.position + grabDir * (grabRange + 10f));
            SwipeRpc(target.NetworkObject, grabWindup);
            return;
        }

        bool lunging = Time.time - stateStartTime < lungeTime;
        if (Time.time >= nextOpenCheck)
        {
            nextOpenCheck = Time.time + 0.3f;
            onOpenGround = !BuildingWithin(transform.position, openGroundRadius);
        }
        float chaseSpeed = onOpenGround ? Mathf.Max(openGroundSpeed, pursuitSpeed) : pursuitSpeed;
        agent.speed = (lunging ? Mathf.Max(lungeSpeed, chaseSpeed) : chaseSpeed) * FedSpeed;

        // Run for the street spot nearest you (on the pavement it can't fit onto, that's the curb beside you)
        bool nearStreet = StreetPointNear(target, out var chasePoint);
        if (Time.time >= nextPathCheck)
        {
            nextPathCheck = Time.time + 0.3f;
            targetReachable = nearStreet && IsReachable(chasePoint, 0.5f);
        }
        agent.SetDestination(nearStreet ? chasePoint : playerPos);   // if unreachable, this walks to the closest point

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

    // Open ground check: is any building (its footprint) within this distance? Footprints cached per city.
    float nextOpenCheck;
    bool onOpenGround;
    CityGenerator.City boundsCity;
    readonly System.Collections.Generic.List<Bounds> buildingBounds = new System.Collections.Generic.List<Bounds>();
    bool BuildingWithin(Vector3 p, float radius)
    {
        var city = CityManager.Current;
        if (city == null) return true;   // no city: never sprint
        if (boundsCity != city)
        {
            boundsCity = city;
            buildingBounds.Clear();
            foreach (var b in city.buildings)
            {
                if (b == null) continue;
                var cols = b.GetComponentsInChildren<Collider>();
                if (cols.Length == 0) continue;
                var bounds = cols[0].bounds;
                foreach (var c in cols) bounds.Encapsulate(c.bounds);
                buildingBounds.Add(bounds);
            }
        }
        float r2 = radius * radius;
        foreach (var b in buildingBounds)
        {
            Vector3 flat = new Vector3(p.x, b.center.y, p.z);   // footprint only, ignore height
            if (b.SqrDistance(flat) < r2) return true;
        }
        return false;
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
            if (StreetPointNear(target, out var street) && IsReachable(street, 0.5f)) { SetState(MonsterState.Chase); return; }
        }

        Vector3 door = currentReachPoint.DoorPoint;
        // Face straight into the doorway so the shoulder stays lined up with it (the head/eyes still track you)
        FaceTowards(transform.position + currentReachPoint.Inward * 10f);

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
                    if (DoorManager.Instance != null) DoorManager.Instance.Rip(door);   // a closed door: torn open
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

                // Gives up after a while; a bit longer if it can see/hear you (its arm may just not reach that far)
                if (Time.time - reachPhaseStart > (knowsWhere ? feelDuration * 1.5f : feelDuration))
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

        int count = Physics.SphereCastNonAlloc(door, handRadius, dir, handHits, dist, Door.AllButDoors, QueryTriggerInteraction.Ignore);
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
        // Use the hand you can actually see: the arm can't always get as deep as the AI wants
        var anim = GiantAnimator();
        Vector3 hand = anim != null && anim.isActiveAndEnabled ? anim.HandPosition : handPos;
        foreach (var p in FirstPersonController.All)
        {
            if (!p.IsSpawned || p.IsGrabbed) continue;
            Vector3 chest = Chest(p);
            Vector3 feet = p.transform.position + Vector3.up * 0.3f;
            // Distance from the hand to the player's body (a line from feet to chest)
            Vector3 closest = ClosestPointOnSegment(feet, chest, hand);
            if (Vector3.Distance(closest, hand) > grabRadius) continue;
            if (!LineClear(hand, p)) continue;
            return p;
        }
        return null;
    }

    MonsterAnimator cachedAnimator;
    MonsterAnimator GiantAnimator() => cachedAnimator != null ? cachedAnimator : (cachedAnimator = GetComponentInChildren<MonsterAnimator>());

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
        if (!IsServer || !IsSpawned || !shownActive) return;
        if (Busy) return;   // holding or carrying off its victim; don't wander off with them in hand
        if (state == MonsterState.Chase || state == MonsterState.Notice) return;

        float dist = Vector3.Distance(transform.position, position);
        if (dist > radius * hearingMultiplier) return;

        pointOfInterest = position;
        if (state == MonsterState.Reach) { heardTime = Time.time; return; }   // hand goes for the sound
        SetState(MonsterState.Investigate);
    }

    // Holding a victim after a catch or carrying them off: noises and scout relays can't pull it away
    bool Busy => holding || carryVictim != null;

    /// <summary>Host: true while this monster is the one hunting tonight (its genome matches) and awake.</summary>
    public bool Hunting => IsServer && IsSpawned && inPlay && shownActive;

    // Host: a scout (or the Director's fallback hint) says a player was here. Like a noise it heard from anywhere:
    // it goes to look, unless it's already busy chasing or holding someone.
    public void Relay(Vector3 position)
    {
        if (!Hunting || Busy) return;
        if (state == MonsterState.Chase || state == MonsterState.Notice) return;
        pointOfInterest = position;
        if (state == MonsterState.Reach) { heardTime = Time.time; return; }
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
            if (StreetLamp.IsUnderLight(player.transform.position)) range = Mathf.Max(range, litSightRange);
            if (player.IsCrouching) range *= crouchVisibility;
            if (player == target && (state == MonsterState.Notice || state == MonsterState.Chase)) range *= chaseSightMultiplier;
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

    // Running grab: are you still in its path (ahead of it and close to its line of charge)?
    bool InGrabPath(FirstPersonController player, Vector3 handOrigin)
    {
        Vector3 local = player.transform.position - transform.position;
        float ahead = Vector3.Dot(local, grabDir);
        float side = Mathf.Abs(Vector3.Dot(local, Vector3.Cross(Vector3.up, grabDir)));
        if (ahead < -1f || ahead > grabRange + grabOvershoot || side > grabWidth) return false;
        return LineClear(handOrigin, player);
    }

    // The nearest ground it can stand on, if you're close to it and not behind a wall (indoors you're out of the street)
    bool StreetPointNear(FirstPersonController player, out Vector3 point)
    {
        point = player.transform.position;
        if (!NavMesh.SamplePosition(point, out var hit, chaseSnapDistance, Filter)) return false;
        point = hit.position;
        return LineClear(hit.position + Vector3.up * 1.5f, player);
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
        // Fences are see-through (and it reaches right over them)
        if (Physics.Raycast(from, to / dist, out var hit, dist + 0.5f, FenceBuilder.AllButFences, QueryTriggerInteraction.Ignore))
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
            if (rp.Blocked) continue;   // boarded up by the players
            if (!IsReachable(rp.transform.position, 4f)) continue;
            best = rp;
            bestDist = d;
        }
        return best;
    }

    // Where to stand so the REACHING SHOULDER (not the body center) lines up with the door
    Vector3 StandPoint(ReachPoint rp)
    {
        var anim = GetComponentInChildren<MonsterAnimator>();
        float side = anim != null ? anim.ReachShoulderSide : 0f;
        Vector3 p = rp.transform.position - rp.transform.right * side - rp.Inward * peekStandBack;
        return NavMesh.SamplePosition(p, out var hit, 4f, Filter) ? hit.position : p;
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

    // ---------- Carrying a caught player off ----------

    void UpdateCarry()
    {
        // They left the game: drop the carry pose and get back to work
        if (carryVictim == null || !carryVictim.IsSpawned)
        {
            carryVictim = null;
            netCarrying.Value = false;
            SetState(MonsterState.Patrol);
            return;
        }

        if (putDownStart < 0f)
        {
            bool there = !agent.pathPending && (!agent.hasPath || Arrived());
            if (!there && Time.time - stateStartTime < carryGiveUpTime) return;
            // Couldn't get there in time: the ground just ahead of it will do
            if (!there) carrySpot = transform.position + transform.forward * putDownReach * 0.8f;
            agent.ResetPath();
            agent.velocity = Vector3.zero;
            agent.isStopped = true;
            agent.updateRotation = false;
            putDownStart = Time.time;
            PutDownRpc(carrySpot, putDownTime);
            return;
        }

        // Stooping to put them down
        FaceTowards(carrySpot);
        if (Time.time - putDownStart >= putDownTime) PutDownVictim(carrySpot);
    }

    void PutDownVictim(Vector3 spot)
    {
        Debug.Log($"Giant hid player {carryVictim.OwnerClientId} at {spot}");
        carryVictim.Hide(spot);
        carryVictim = null;
        netCarrying.Value = false;
        ignorePlayersUntil = Time.time + catchCooldown;
        if (state == MonsterState.Carry) SetState(MonsterState.Patrol);
    }

    // A hiding spot from the city's list it can walk up to, out of the way: far enough from the catch and as far as
    // possible from everyone still free. Gives the spot and where to stand to reach it.
    bool PickHidingSpot(Vector3 from, out Vector3 spot, out Vector3 stand)
    {
        spot = stand = from;
        var city = CityManager.Current;
        if (city == null || city.hidingSpots.Count == 0) return false;

        var filter = new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = NavMesh.AllAreas };
        float bestScore = float.MinValue;
        bool found = false;
        foreach (var p in city.hidingSpots)
        {
            float d = Vector3.Distance(Flat(p), Flat(from));
            if (d < minCarryDistance) continue;
            if (!NavMesh.SamplePosition(p, out NavMeshHit hit, putDownReach, filter)) continue;
            if (!NavMesh.CalculatePath(agent.nextPosition, hit.position, filter, path) || path.status != NavMeshPathStatus.PathComplete) continue;

            // Away from the players still free, a bit of luck, and not too long a walk
            float nearestFree = 200f;
            foreach (var pl in FirstPersonController.All)
                if (pl.IsSpawned && pl != carryVictim && !pl.IsGrabbed && !pl.IsDead)
                    nearestFree = Mathf.Min(nearestFree, Vector3.Distance(Flat(pl.transform.position), Flat(p)));
            float score = nearestFree + Random.Range(0f, 25f) - (d > maxCarryDistance ? (d - maxCarryDistance) * 2f : 0f);
            if (score <= bestScore) continue;
            bestScore = score;
            spot = p;
            stand = hit.position;
            found = true;
        }
        return found;
    }

    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    // Everyone: stoop and lower the hand to the ground at the spot
    [Rpc(SendTo.Everyone)]
    void PutDownRpc(Vector3 spot, float duration)
    {
        var anim = GetComponentInChildren<MonsterAnimator>();
        if (anim != null) anim.StartPutDown(spot, duration);
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
        target.Caught(NetworkObject);     // that player gets dragged out / picked up, then carried off and hidden
        carryVictim = target;
        netCarrying.Value = true;
        Feed();
        target = null;
        ignorePlayersUntil = Time.time + catchCooldown;
        SetState(MonsterState.Patrol);

        // Stand still while the grab plays out, then carry them off (Carry)
        holding = true;
        holdUntil = Time.time + grabHoldTime + extra;
        agent.isStopped = true;   // skids to a halt from a run (acceleration 30) instead of freezing
        agent.updateRotation = false;
    }

    // Everyone: the arm swings out at that player while it runs
    [Rpc(SendTo.Everyone)]
    void SwipeRpc(NetworkObjectReference victim, float windup)
    {
        var anim = GetComponentInChildren<MonsterAnimator>();
        if (anim != null && victim.TryGet(out NetworkObject obj)) anim.StartSwipe(obj.transform, windup);
    }

    // Everyone: near miss, the hand slams the ground beside you
    [Rpc(SendTo.Everyone)]
    void SlamRpc(Vector3 point, float pause)
    {
        var anim = GetComponentInChildren<MonsterAnimator>();
        if (anim != null) anim.StartSlam(point, pause);
        var fx = GetComponent<GroundSlamFx>();
        float delay = fx != null ? fx.impactDelay : 0f;   // effects land when the hand does
        StartCoroutine(SlamImpact(point, delay));
    }

    System.Collections.IEnumerator SlamImpact(Vector3 point, float delay)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);
        var monsterAudio = GetComponent<MonsterAudio>();
        if (monsterAudio != null) monsterAudio.PlaySlam(point);
        var fx = GetComponent<GroundSlamFx>();
        if (fx != null) fx.Spawn(point, transform.position);
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
