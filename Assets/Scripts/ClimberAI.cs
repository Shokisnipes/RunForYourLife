using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

public enum ClimberState
{
    Patrol, Investigate, Chase, Search,
    ToWall, ClimbUp, OnRoof, ToEdge, ClimbDown,   // up and down the outside walls
    ToHatch, DropIn, ToUnderHatch, ClimbOut,      // in and out through a roof hatch
    Perch, LeapDown,                              // watching the door from the roof edge, jumping off a roof
    ToWindow, ThroughWindow,                      // in or out through a ground-floor window (smashing the glass)
}

// The Climber genome's monster: a lanky crawler that hunts on the streets and, when you hide inside, climbs the
// building's wall to the roof. Host runs all of this; everyone else sees it through NetworkTransform and the
// synced state (ClimberAnimator). It walks in through open front doors but can't open a closed one (Door cuts a
// closed doorway out of its NavMesh), so shutting the door behind you sends it up to the roof hatch or through a
// ground-floor window (it smashes the glass), whichever is quicker. Sloped-roof houses can't be climbed: windows only.
// A boarded window (or one behind a closed shutter) it can't use.
[RequireComponent(typeof(NavMeshAgent))]
public class ClimberAI : NetworkBehaviour
{
    [Header("Current state (read only)")]
    public ClimberState state = ClimberState.Patrol;

    readonly NetworkVariable<ClimberState> netState = new NetworkVariable<ClimberState>(ClimberState.Patrol);
    readonly NetworkVariable<bool> netRunning = new NetworkVariable<bool>();
    readonly NetworkVariable<bool> netInPlay = new NetworkVariable<bool>(false);
    // The roof hatch it's using: the lid opens on every client
    readonly NetworkVariable<bool> netHatchOpen = new NetworkVariable<bool>();
    readonly NetworkVariable<Vector3> netHatchPos = new NetworkVariable<Vector3>();
    public ClimberState SyncedState => netState.Value;
    public bool SyncedRunning => netRunning.Value;
    public bool SyncedHatchOpen => netHatchOpen.Value;
    public Vector3 SyncedHatchPos => netHatchPos.Value;

    [Header("Movement")]
    [Tooltip("Crawling around while patrolling.")]
    public float crawlSpeed = 3f;
    [Tooltip("Crawling to a noise or searching.")]
    public float huntSpeed = 4.5f;
    [Tooltip("Crawling after a player it can see.")]
    public float chaseSpeed = 6f;
    [Tooltip("Gets up and runs when chasing someone far away. Off = it always crawls.")]
    public bool canRun = true;
    public float runSpeed = 8f;
    [Tooltip("Runs instead of crawls when the player it chases is further than this.")]
    public float runWhenFartherThan = 15f;

    [Header("Vision")]
    public float eyeHeight = 1.2f;
    public float sightRange = 25f;
    public float flashlightSightRange = 40f;
    [Tooltip("Player standing under a lit street lamp.")]
    public float litSightRange = 35f;
    [Tooltip("Full cone width (horizontal).")]
    public float viewAngle = 140f;
    [Tooltip("Crouching shrinks how far it can see you.")]
    public float crouchVisibility = 0.6f;
    [Tooltip("Notices you this close even from behind.")]
    public float feelRange = 4f;

    [Header("Hearing")]
    public float hearingMultiplier = 1f;

    [Header("Patrol")]
    [Tooltip("Optional; wanders around this (defaults to where it starts).")]
    public Transform patrolCenter;
    public float wanderRadius = 40f;
    public float patrolPause = 2f;

    [Header("Chase and grab")]
    public float loseSightTime = 3f;
    [Tooltip("Lunges at you from this close.")]
    public float grabRange = 1.8f;
    [Tooltip("How long the lunge lasts before the grab lands. It doesn't stop: it springs at you in a straight line, so " +
             "backing or running straight away gets you caught; a sideways sprint out of its line can dodge it.")]
    public float grabWindup = 0.35f;
    [Tooltip("At the end of the lunge it catches you if you're no further ahead of it than this...")]
    public float grabEscapeRange = 2.6f;
    [Tooltip("...and within this distance of its line of lunge (sprint sideways further than this to dodge).")]
    public float lungeWidth = 1.2f;
    [Tooltip("Speed of the lunge (m/s). Faster than a sprinting player.")]
    public float lungeSpeed = 9f;
    [Tooltip("Stays still holding you this long after a catch, then leaves you there, hidden (death timer starts).")]
    public float grabHoldTime = 3.2f;
    public float catchCooldown = 4f;

    [Header("Climbing")]
    [Tooltip("Metres per second up or down a wall.")]
    public float climbSpeed = 1.6f;
    [Tooltip("Gap between its body and the wall while climbing.")]
    public float wallGap = 0.35f;
    [Tooltip("Stops climbing this far below the top of the wall, then pulls itself over the edge.")]
    public float climbFinishBelow = 1.2f;
    [Tooltip("Seconds to pull itself over the roof edge (or lower itself over it on the way down).")]
    public float mantleTime = 0.9f;
    [Tooltip("How far in from the roof edge it lands.")]
    public float roofInset = 1.5f;
    [Tooltip("On a roof with no new sign of anyone for this long? Climbs back down and patrols.")]
    public float roofGiveUpTime = 15f;

    [Header("Door camping (stops players trolling it in and out of a door)")]
    [Tooltip("This far out in front of a door still counts as inside that building.")]
    public float doorstepDepth = 3f;
    [Tooltip("Half the width of the doorstep zone.")]
    public float doorstepHalfWidth = 2f;
    [Tooltip("You must stay out of its building this long before it climbs down / out after you.")]
    public float leaveDelay = 1.5f;
    [Tooltip("On the roof and you're this close to the door (inside or out)? It perches on the edge above the door.")]
    public float doorNearDistance = 4f;
    [Tooltip("Longest it waits on the perch before it goes for the hatch instead (so you can't just stay inside).")]
    public float perchMaxTime = 8f;
    [Tooltip("After a perch times out it won't perch on that building again for this long.")]
    public float perchCooldown = 20f;
    [Tooltip("How far in from the roof edge it crouches while perched.")]
    public float perchInset = 0.8f;
    [Tooltip("Step out within this distance of the wall below it while it's perched and it pounces.")]
    public float pounceRange = 6f;

    [Header("Leaping off roofs (when it can see you)")]
    [Tooltip("How far out from the wall it lands.")]
    public float leapDistance = 2f;
    [Tooltip("Pause after landing before it moves.")]
    public float leapLandPause = 0.3f;

    [Header("Roof hatch")]
    [Tooltip("Crouches at the open hatch, peering in, this long before it drops.")]
    public float hatchPeekTime = 1.2f;
    [Tooltip("Pause after landing inside before it moves.")]
    public float landPause = 0.6f;
    [Tooltip("Lid closes this long after it's through.")]
    public float hatchCloseDelay = 1.5f;

    [Header("Windows")]
    [Tooltip("Seconds it spends punching out the glass before it climbs through.")]
    public float windowSmashTime = 0.7f;
    [Tooltip("Seconds to climb through a window, sill to floor.")]
    public float windowCrawlTime = 1.0f;

    [Header("Search")]
    public float searchDuration = 12f;
    public float searchArea = 12f;

    [Header("It feeds (every catch makes it stronger for the rest of the night)")]
    [Range(0f, 0.5f), Tooltip("Crawl, hunt, chase, run and lunge speed added per catch (0.1 = +10%). Climbing stays the same.")]
    public float feedSpeedBonus = 0.1f;
    [Range(0f, 0.5f), Tooltip("Sight, hearing and feel range added per catch (0.1 = +10%). Feel range is how a blind Climber finds you up close.")]
    public float feedSenseBonus = 0.1f;
    [Tooltip("It stops getting stronger after this many catches in one night.")]
    public int maxFeeds = 5;

    NavMeshAgent agent;
    NavMeshPath path;
    FirstPersonController target;
    Vector3 pointOfInterest, lastSeenPosition, homePosition;
    float lastSeenTime = -999f, heardTime = -999f, stateStartTime, pauseUntil, ignorePlayersUntil, nextRepath;
    float grabStart = -1f, holdUntil;
    Vector3 lungeDir;
    bool holding, inPlay;
    FirstPersonController heldVictim;   // let go of (hidden) when the hold ends

    // Climbing
    class Roof
    {
        public Vector3 center, right, fwd; public float halfX, halfZ, topY; public RoofHatch hatch;
        public bool hasDoor; public Vector3 door, doorOut;   // front door at ground level, and which way is outside
        public bool climbable = true;                          // false: a sloped roof (no climbing onto it)
        public readonly List<BreakableWindow> windows = new List<BreakableWindow>();   // ground-floor windows
    }
    struct ClimbPlan { public Vector3 foot, wallBase, wallTop, roofPoint, outward; }
    readonly List<Roof> roofs = new List<Roof>();
    Roof currentRoof;   // the roof it's on (null on the street)
    Roof indoorsRoof;   // the building it dropped into (null outside)
    Vector3 hatchFloor; // where it lands inside, under the hatch
    float hatchCloseAt = -1f;
    float outsideSince = -1f;   // when its prey left the building it's in / on (-1 = they haven't)
    bool leaping;               // the trip to the edge ends in a jump, not a climb down
    Vector3 leapLanding;
    Roof perchBlockedRoof;
    float perchBlockedUntil;
    ClimbPlan plan;
    int climbPhase;
    float phaseStart;
    Vector3 phaseFrom;
    bool givingUp;      // climbing down because it lost interest, not to chase someone

    bool OnRoof => currentRoof != null;
    bool Indoors => indoorsRoof != null;
    bool Climbing => state == ClimberState.ClimbUp || state == ClimberState.ClimbDown ||
                     state == ClimberState.DropIn || state == ClimberState.ClimbOut || state == ClimberState.LeapDown ||
                     state == ClimberState.ThroughWindow;

    // The window it's heading for / climbing through
    BreakableWindow useWindow;
    bool windowIn;                    // true = getting in, false = getting out
    Roof windowBuilding;
    Vector3 windowOutside, windowInside;

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
    float baseSight, baseFlashSight, baseLitSight, baseHearing, baseFeel;
    MonsterGenome genome;
    int feeds;   // catches this night (It feeds)

    // Catches so far this night, for tests and the HUD
    public int Feeds => feeds;
    float FedSpeed => 1f + feedSpeedBonus * feeds;

    // Host only: called by GenomeManager at game start. Only shows up when the genome is a Climber.
    public void ApplyGenome(MonsterGenome genome)
    {
        if (!baseSensesSaved)
        {
            baseSight = sightRange; baseFlashSight = flashlightSightRange;
            baseLitSight = litSightRange; baseHearing = hearingMultiplier;
            baseFeel = feelRange;
            baseSensesSaved = true;
        }
        this.genome = genome;
        feeds = 0;   // a new run: it starts hungry again
        ApplySenses();
        inPlay = genome.access == GenomeAccess.Climber;
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
        feelRange = baseFeel * fed;
    }

    // Host: it caught someone. Faster and sharper for the rest of the night.
    void Feed()
    {
        if (!baseSensesSaved || feeds >= maxFeeds) return;
        feeds++;
        ApplySenses();
        Debug.Log($"Climber fed ({feeds}/{maxFeeds}): speed x{FedSpeed:0.00}, hearing x{hearingMultiplier:0.00}, feel {feelRange:0.0} m");
    }

    void OnEnable() => NoiseSystem.OnNoise += HearNoise;
    void OnDisable() => NoiseSystem.OnNoise -= HearNoise;

    public override void OnNetworkSpawn()
    {
        if (!IsServer) { agent.enabled = false; return; }   // the host moves it; we just watch

        FindRoofs();
        agent.enabled = true;
        if (NavMesh.SamplePosition(transform.position, out var hit, 10f, Filter)) agent.Warp(hit.position);
        else Debug.LogError("ClimberAI: no Climber NavMesh near the climber. Run Tools > RunForYourLife > Climber Setup.");
        holding = false;
        currentRoof = null;
        feeds = 0;
        if (baseSensesSaved) ApplySenses();
        SetState(ClimberState.Patrol);
    }

    // Every building's roof, from the "Roof" boxes the Building Generator makes. A roof with a hatch is split into
    // pieces (Roof, Roof_N, Roof_W, Roof_E); they're merged back into one footprint per building. Houses with a
    // sloped roof have a "Ceiling" box instead: they count as buildings (windows, doors) but can't be climbed.
    void FindRoofs()
    {
        roofs.Clear();
        var pieces = new Dictionary<Transform, List<BoxCollider>>();
        foreach (var box in FindObjectsByType<BoxCollider>())
        {
            if (box.name != "Roof" && !box.name.StartsWith("Roof_") && box.name != "Ceiling") continue;
            var building = box.transform.parent != null ? box.transform.parent : box.transform;
            if (!pieces.TryGetValue(building, out var list)) pieces[building] = list = new List<BoxCollider>();
            list.Add(box);
        }

        foreach (var pair in pieces)
        {
            Transform space = pair.Key;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            float topY = float.NegativeInfinity;
            foreach (var box in pair.Value)
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = box.center + Vector3.Scale(box.size * 0.5f,
                        new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                    Vector3 world = box.transform.TransformPoint(corner);
                    Vector3 local = space.InverseTransformPoint(world);
                    min = Vector3.Min(min, local);
                    max = Vector3.Max(max, local);
                    topY = Mathf.Max(topY, world.y);
                }
            Vector3 s = space.lossyScale;
            roofs.Add(new Roof
            {
                center = space.TransformPoint((min + max) * 0.5f),
                right = space.right,
                fwd = space.forward,
                halfX = (max.x - min.x) * 0.5f * Mathf.Abs(s.x),
                halfZ = (max.z - min.z) * 0.5f * Mathf.Abs(s.z),
                topY = topY,
                climbable = !pair.Value.TrueForAll(b => b.name == "Ceiling"),
            });
        }

        foreach (var w in BreakableWindow.All)
        {
            var roof = BuildingAt(w.Center);
            if (roof != null) roof.windows.Add(w);
        }

        foreach (var hatch in RoofHatch.All)
        {
            var roof = BuildingAt(hatch.transform.position);
            if (roof != null) roof.hatch = hatch;
        }

        // Front doors, from the giant's doorway markers
        foreach (var rp in ReachPoint.All)
        {
            var roof = BuildingAt(rp.DoorPoint);
            if (roof == null) continue;
            Vector3 outward = -rp.Inward; outward.y = 0f;
            roof.hasDoor = true;
            roof.door = new Vector3(rp.DoorPoint.x, rp.transform.position.y, rp.DoorPoint.z);
            roof.doorOut = outward.normalized;
        }
    }

    bool shownActive = true;
    readonly List<Renderer> hiddenRenderers = new List<Renderer>();

    // Asleep during prep, and hidden all game when this run's genome isn't a Climber
    void UpdateAsleep(bool active)
    {
        if (active == shownActive) return;
        shownActive = active;
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
            if (active && !OnRoof) SetState(ClimberState.Patrol);
        }
    }

    void Update()
    {
        if (IsServer && IsSpawned && netInPlay.Value != inPlay) netInPlay.Value = inPlay;
        if (IsSpawned) RoofHatch.SetOpenAt(netHatchPos.Value, netHatchOpen.Value);
        if (IsServer && hatchCloseAt > 0f && Time.time >= hatchCloseAt) { hatchCloseAt = -1f; netHatchOpen.Value = false; }
        var flow = GameFlow.Instance;
        bool active = (flow == null || flow.MonsterActive) && netInPlay.Value;
        if (IsSpawned) UpdateAsleep(active);
        if (!IsServer || !IsSpawned || !active) return;

        // Still for a moment after a catch
        if (holding)
        {
            if (Time.time < holdUntil) return;
            holding = false;
            agent.isStopped = false;
            // It kills you where it caught you; there's no timer, a teammate can revive you any time
            if (heldVictim != null && heldVictim.IsSpawned) heldVictim.Down(heldVictim.transform.position);
            heldVictim = null;
        }

        TrackInside();

        var seen = Climbing ? null : FindVisiblePlayer();
        if (seen != null)
        {
            target = seen;
            lastSeenTime = Time.time;
            lastSeenPosition = seen.transform.position;
            if (state != ClimberState.Chase) GoAfter(lastSeenPosition, true);
        }

        // Prey left its building a moment ago and hasn't come back: now it follows
        if ((OnRoof || Indoors) && outsideSince >= 0f && Time.time - outsideSince >= leaveDelay &&
            (state == ClimberState.OnRoof || state == ClimberState.Perch || state == ClimberState.ToHatch ||
             state == ClimberState.Search || state == ClimberState.Investigate || state == ClimberState.Chase))
            GoAfter(pointOfInterest, Time.time - lastSeenTime < 0.5f);

        switch (state)
        {
            case ClimberState.Patrol:      UpdatePatrol(); break;
            case ClimberState.Investigate: if (Arrived()) SetState(ClimberState.Search); break;
            case ClimberState.Chase:       UpdateChase(); break;
            case ClimberState.Search:      UpdateSearch(); break;
            case ClimberState.ToWall:      if (Arrived()) StartClimb(ClimberState.ClimbUp); break;
            case ClimberState.ClimbUp:     UpdateClimbUp(); break;
            case ClimberState.OnRoof:      UpdateOnRoof(); break;
            case ClimberState.ToEdge:      if (Arrived()) StartClimb(leaping ? ClimberState.LeapDown : ClimberState.ClimbDown); break;
            case ClimberState.ClimbDown:   UpdateClimbDown(); break;
            case ClimberState.ToHatch:     UpdateToHatch(); break;
            case ClimberState.DropIn:      UpdateDropIn(); break;
            case ClimberState.ToUnderHatch: if (Arrived()) StartClimbOut(); break;
            case ClimberState.ClimbOut:    UpdateClimbOut(); break;
            case ClimberState.Perch:       UpdatePerch(); break;
            case ClimberState.LeapDown:    UpdateLeapDown(); break;
            case ClimberState.ToWindow:    UpdateToWindow(); break;
            case ClimberState.ThroughWindow: UpdateThroughWindow(); break;
        }

        bool running = canRun && state == ClimberState.Chase && target != null &&
                       Vector3.Distance(transform.position, target.transform.position) > runWhenFartherThan;
        if (agent.enabled && state == ClimberState.Chase) agent.speed = (grabStart >= 0f ? lungeSpeed : running ? runSpeed : chaseSpeed) * FedSpeed;
        if (netRunning.Value != running) netRunning.Value = running;
    }

    // ---------- States ----------

    void SetState(ClimberState newState)
    {
        state = newState;
        netState.Value = newState;
        stateStartTime = Time.time;
        pauseUntil = 0f;
        grabStart = -1f;
        if (!agent.enabled) return;
        agent.isStopped = false;

        switch (newState)
        {
            case ClimberState.Patrol:
                agent.speed = crawlSpeed * FedSpeed;
                GoToRandomPoint(patrolCenter != null ? patrolCenter.position : homePosition, wanderRadius);
                break;
            case ClimberState.Investigate:
            case ClimberState.Search:
                agent.speed = huntSpeed * FedSpeed;
                agent.SetDestination(pointOfInterest);
                break;
            case ClimberState.Chase:
                agent.speed = chaseSpeed * FedSpeed;
                nextRepath = 0f;
                break;
            case ClimberState.ToWall:
                agent.speed = huntSpeed * FedSpeed;
                agent.SetDestination(plan.foot);
                break;
            case ClimberState.OnRoof:
                agent.speed = huntSpeed * FedSpeed;
                nextRepath = 0f;
                break;
            case ClimberState.ToEdge:
                agent.speed = huntSpeed * FedSpeed;
                agent.SetDestination(plan.roofPoint);
                break;
            case ClimberState.ToHatch:
                agent.speed = huntSpeed * FedSpeed;
                if (RoofSpot(currentRoof, currentRoof.hatch.transform.position, out var rim)) agent.SetDestination(rim);
                break;
            case ClimberState.ToUnderHatch:
                agent.speed = huntSpeed * FedSpeed;
                agent.SetDestination(hatchFloor);
                break;
            case ClimberState.Perch:
                agent.speed = huntSpeed * FedSpeed;
                agent.SetDestination(plan.roofPoint);
                break;
            case ClimberState.ToWindow:
                agent.speed = huntSpeed * FedSpeed;
                agent.SetDestination(windowIn ? windowOutside : windowInside);
                break;
        }
    }

    // Decide how to get to 'point': crawl there along the street, or climb the building it's in.
    void GoAfter(Vector3 point, bool seenIt)
    {
        pointOfInterest = point;
        if (Climbing) return;   // finish the climb first; it heads for the new spot after
        Roof building = BuildingAt(point);
        if (building == null) building = Doorstep(point);   // just outside the door still counts as inside
        Roof mine = OnRoof ? currentRoof : indoorsRoof;
        if (mine != null && building == mine) outsideSince = -1f;   // came back: forget about following

        // Inside a building: hunt in here, or climb back out through the hatch if you got out
        if (Indoors && !IsReachable(point))
        {
            if (building == indoorsRoof) SetState(ClimberState.Investigate);   // same building: get as close as it can
            else if (state != ClimberState.ToUnderHatch && state != ClimberState.ToWindow && ReadyToLeave()) LeaveBuilding();
            return;
        }

        if (OnRoof)
        {
            if (building == currentRoof)
            {
                if (state != ClimberState.OnRoof && state != ClimberState.ToHatch && state != ClimberState.Perch)
                    SetState(ClimberState.OnRoof);
                return;
            }
            if (state != ClimberState.ToEdge && ReadyToLeave() && PlanClimb(currentRoof, point, false))
            {
                givingUp = false;
                leaping = seenIt;                 // can see you: jumps down instead of climbing
                SetState(ClimberState.ToEdge);
            }
            return;
        }

        if (IsReachable(point))
        {
            if (seenIt) { if (state != ClimberState.Chase) SetState(ClimberState.Chase); }
            else SetState(ClimberState.Investigate);
            return;
        }

        // Inside a building: get in by its roof hatch or a window, whichever is quicker. Already on the way? Keep going.
        if (building != null)
        {
            if (state == ClimberState.ToWall && building == climbTarget) return;
            if (state == ClimberState.ToWindow && windowIn && building == windowBuilding) return;
            if (EnterBuilding(building)) return;
        }
        SetState(ClimberState.Investigate);   // somewhere it can't get to: go as close as it can
    }

    Roof climbTarget;

    // Walked in through a door rather than dropping through the hatch? If that door gets shut behind it, it counts as
    // indoors (and leaves by the hatch, like after a drop-in). With the door open it just walks out.
    float nextInsideCheck;
    void TrackInside()
    {
        if (OnRoof || Climbing || state == ClimberState.ToUnderHatch || state == ClimberState.ToWindow || Time.time < nextInsideCheck) return;
        nextInsideCheck = Time.time + 0.5f;
        var inside = BuildingAt(transform.position);
        if (inside == null || transform.position.y > inside.topY - 1f) { indoorsRoof = null; return; }
        bool wayOut = inside.hasDoor && IsReachable(inside.door + inside.doorOut * 3f);
        indoorsRoof = wayOut || (inside.hatch == null && !inside.windows.Exists(w => w.Usable)) ? null : inside;
    }

    // ---------- Windows ----------

    // Where it stands outside and lands inside for this window (both on its NavMesh, each on its own side of the wall)
    bool WindowPoints(BreakableWindow w, out Vector3 outside, out Vector3 inside)
    {
        outside = inside = Vector3.zero;
        Vector3 o = w.Center + w.Outward * 0.9f, i = w.Center - w.Outward * 1.1f;
        o.y = i.y = w.SillY;
        if (!NavMesh.SamplePosition(o, out var a, 1.6f, Filter) || a.position.y > w.SillY - 0.3f) return false;
        if (!NavMesh.SamplePosition(i, out var b, 1.6f, Filter) || b.position.y > w.SillY - 0.3f) return false;
        if (Vector3.Dot(a.position - w.Center, w.Outward) < 0.3f || Vector3.Dot(b.position - w.Center, w.Outward) > -0.3f) return false;
        Vector3 fa = a.position - w.Center, fb = b.position - w.Center;
        fa.y = fb.y = 0f;
        if (fa.magnitude > 2f || fb.magnitude > 2f) return false;   // furniture in the way: it would land far off
        outside = a.position;
        inside = b.position;
        return true;
    }

    // Seconds to crawl to this point (infinity if it can't get there)
    float PathTime(Vector3 to)
    {
        if (!agent.enabled || !agent.isOnNavMesh) return float.PositiveInfinity;
        if (!NavMesh.SamplePosition(to, out var hit, 1f, Filter)) return float.PositiveInfinity;
        if (!agent.CalculatePath(hit.position, path) || path.status != NavMeshPathStatus.PathComplete) return float.PositiveInfinity;
        float length = 0f;
        var corners = path.corners;
        for (int i = 1; i < corners.Length; i++) length += Vector3.Distance(corners[i - 1], corners[i]);
        return length / huntSpeed;
    }

    float WindowTime(BreakableWindow w) =>
        (w.Current == BreakableWindow.State.Intact || w.Current == BreakableWindow.State.Holed ? windowSmashTime : 0.15f) + windowCrawlTime;

    // From outside: the quickest way in, a window or up the wall to the roof hatch. False = no way in.
    bool EnterBuilding(Roof b)
    {
        BreakableWindow bestWindow = null;
        float bestWindowTime = float.PositiveInfinity;
        Vector3 bestOut = Vector3.zero, bestIn = Vector3.zero;
        foreach (var w in b.windows)
        {
            if (!w.Usable || !WindowPoints(w, out var o, out var i)) continue;
            float t = PathTime(o) + WindowTime(w) + Vector3.Distance(i, pointOfInterest) / huntSpeed;
            if (t < bestWindowTime) { bestWindowTime = t; bestWindow = w; bestOut = o; bestIn = i; }
        }

        float climbTime = float.PositiveInfinity;
        if (b.climbable && PlanClimb(b, transform.position, true))
        {
            climbTime = PathTime(plan.foot) + (plan.wallTop.y - plan.foot.y) / climbSpeed + mantleTime;
            if (b.hatch != null)
                climbTime += Vector3.Distance(plan.roofPoint, b.hatch.transform.position) / huntSpeed + hatchPeekTime + 1f +
                             Vector3.Distance(b.hatch.transform.position, pointOfInterest) / huntSpeed;
            else if (bestWindow != null) climbTime = float.PositiveInfinity;   // no hatch: the roof doesn't get it inside
        }

        if (bestWindow != null && bestWindowTime <= climbTime)
        {
            useWindow = bestWindow;
            windowIn = true;
            windowBuilding = b;
            windowOutside = bestOut;
            windowInside = bestIn;
            SetState(ClimberState.ToWindow);
            return true;
        }
        if (climbTime < float.PositiveInfinity) { climbTarget = b; SetState(ClimberState.ToWall); return true; }
        return false;
    }

    // From inside (door shut): out by the quickest of the hatch or a window
    void LeaveBuilding()
    {
        Roof b = indoorsRoof;
        float hatchTime = float.PositiveInfinity;
        Vector3 floor = Vector3.zero;
        if (b.hatch != null && HatchFloor(b, out floor))
            hatchTime = PathTime(floor) + (b.topY - floor.y) / climbSpeed + mantleTime;

        BreakableWindow bestWindow = null;
        float bestWindowTime = float.PositiveInfinity;
        Vector3 bestOut = Vector3.zero, bestIn = Vector3.zero;
        foreach (var w in b.windows)
        {
            if (!w.Usable || !WindowPoints(w, out var o, out var i)) continue;
            float t = PathTime(i) + WindowTime(w) + Vector3.Distance(o, pointOfInterest) / huntSpeed;
            if (t < bestWindowTime) { bestWindowTime = t; bestWindow = w; bestOut = o; bestIn = i; }
        }

        if (bestWindow != null && bestWindowTime <= hatchTime)
        {
            useWindow = bestWindow;
            windowIn = false;
            windowBuilding = b;
            windowOutside = bestOut;
            windowInside = bestIn;
            SetState(ClimberState.ToWindow);
        }
        else if (hatchTime < float.PositiveInfinity)
        {
            hatchFloor = floor;
            SetState(ClimberState.ToUnderHatch);
        }
        else
        {
            indoorsRoof = null;   // no way out it can use: wander in here until a door opens
            SetState(ClimberState.Investigate);
        }
    }

    // Walk up to the window, punch the glass out, then climb through
    void UpdateToWindow()
    {
        // Boarded meanwhile, or it can't get there after all (60 s covers a walk across the whole city)
        if (useWindow == null || !useWindow.Usable || Time.time - stateStartTime > 60f ||
            (!agent.pathPending && agent.pathStatus == NavMeshPathStatus.PathInvalid))
        {
            SetState(ClimberState.Investigate);
            return;
        }
        if (pauseUntil == 0f)
        {
            if (!Arrived()) return;
            agent.isStopped = true;
            bool smash = useWindow.Current == BreakableWindow.State.Intact || useWindow.Current == BreakableWindow.State.Holed;
            if (smash)
            {
                if (WindowManager.Instance != null) WindowManager.Instance.Break(useWindow, !windowIn);
                GrabAnimRpc(windowSmashTime);   // the lunge pose doubles as the punch
            }
            pauseUntil = Time.time + (smash ? windowSmashTime : 0.15f);
        }
        FaceTowards(useWindow.Center);
        if (Time.time >= pauseUntil) StartClimb(ClimberState.ThroughWindow);
    }

    // Up onto the sill, through the opening and down the other side
    void UpdateThroughWindow()
    {
        Vector3 to = windowIn ? windowInside : windowOutside;
        Vector3 dir = to - phaseFrom;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(dir), 540f * Time.deltaTime);
        Vector3 sill = useWindow.Center;
        sill.y = useWindow.SillY;
        switch (climbPhase)
        {
            case 0:   // onto the sill
                if (MantleOverTime(phaseFrom, sill, useWindow.SillY + 0.35f, windowCrawlTime * 0.5f)) NextPhase();
                break;
            case 1:   // through and down
                if (MantleOverTime(phaseFrom, to, useWindow.SillY + 0.2f, windowCrawlTime * 0.5f)) NextPhase();
                break;
            case 2:
                LandOnNavMesh(to);
                outsideSince = -1f;
                indoorsRoof = windowIn ? windowBuilding : null;
                SetState(ClimberState.Search);
                bool sees = target != null && Time.time - lastSeenTime < loseSightTime;
                GoAfter(sees ? target.transform.position : pointOfInterest, sees);
                break;
        }
    }

    void UpdatePatrol()
    {
        if (OnRoof) { GiveUpAndClimbDown(); return; }
        if (Indoors) { LeaveBuilding(); return; }
        if (!Arrived()) return;
        if (pauseUntil == 0f) pauseUntil = Time.time + patrolPause;
        if (Time.time < pauseUntil) return;
        pauseUntil = 0f;
        GoToRandomPoint(patrolCenter != null ? patrolCenter.position : homePosition, wanderRadius);
    }

    void UpdateSearch()
    {
        if (Time.time - stateStartTime > searchDuration) { SetState(ClimberState.Patrol); return; }
        if (Arrived()) GoToRandomPoint(pointOfInterest, searchArea);
    }

    void UpdateChase()
    {
        if (target == null || !target.IsSpawned || target.IsGrabbed) { target = null; SetState(ClimberState.Search); return; }
        Vector3 playerPos = target.transform.position;

        // Mid-lunge: springing at you along the line it picked (no steering), then the grab lands
        if (grabStart >= 0f)
        {
            agent.velocity = lungeDir * lungeSpeed;
            if (Time.time - grabStart < grabWindup) return;
            grabStart = -1f;
            if (InLungePath(target)) { CatchPlayer(); return; }
            nextRepath = 0f;   // missed: straight back after you
        }
        else if (CanGrab(target, grabRange))
        {
            grabStart = Time.time;
            lungeDir = playerPos - transform.position;
            lungeDir.y = 0f;
            lungeDir = lungeDir.sqrMagnitude > 0.01f ? lungeDir.normalized : transform.forward;
            agent.isStopped = false;
            agent.SetDestination(transform.position + lungeDir * (grabRange + lungeSpeed * grabWindup + 1f));
            agent.velocity = lungeDir * lungeSpeed;
            GrabAnimRpc(grabWindup + 0.15f);   // lunge pose; fades out again if it misses
            return;
        }

        // Lost you? Go to where it last saw you (climbing the building if you went inside)
        if (Time.time - lastSeenTime > loseSightTime) { GoAfter(lastSeenPosition, false); return; }

        if (Time.time < nextRepath) return;
        nextRepath = Time.time + 0.2f;
        if (IsReachable(playerPos)) agent.SetDestination(playerPos);
        else GoAfter(playerPos, false);   // ran inside: climb
    }

    void UpdateOnRoof()
    {
        // Nothing new for a while? Climb down and patrol.
        if (Time.time - Mathf.Max(lastSeenTime, heardTime, stateStartTime) > roofGiveUpTime) { GiveUpAndClimbDown(); return; }

        // Prey near the door: perch on the edge above it (unless it just tried that here)
        bool perchBlocked = perchBlockedRoof == currentRoof && Time.time < perchBlockedUntil;
        if (!perchBlocked && PreyNearDoor(currentRoof) && PlanClimb(currentRoof, currentRoof.door, false))
        {
            // Right at the edge, so it can see over the parapet
            Vector3 edge = plan.wallTop - plan.outward * wallGap;
            if (RoofSpot(currentRoof, edge - plan.outward * perchInset, out var perchSpot)) plan.roofPoint = perchSpot;
            SetState(ClimberState.Perch);
            return;
        }

        // Someone's inside and there's a hatch: go in
        if (currentRoof.hatch != null && SomeoneInside(currentRoof) && HatchFloor(currentRoof, out hatchFloor))
        {
            SetState(ClimberState.ToHatch);
            return;
        }

        // No hatch, but a window it can smash: back down the wall to get in that way
        if (currentRoof.hatch == null && SomeoneInside(currentRoof) && currentRoof.windows.Exists(w => w.Usable) &&
            PlanClimb(currentRoof, pointOfInterest, false))
        {
            givingUp = false;
            leaping = false;
            SetState(ClimberState.ToEdge);
            return;
        }

        // No hatch: crawl to the spot right above where it thinks you are and wait
        if (Time.time < nextRepath) return;
        nextRepath = Time.time + 0.5f;
        if (RoofSpot(currentRoof, pointOfInterest, out var spot)) agent.SetDestination(spot);
    }

    void GiveUpAndClimbDown()
    {
        Vector3 away = homePosition;
        if (PlanClimb(currentRoof, away, false)) { givingUp = true; leaping = false; SetState(ClimberState.ToEdge); }
    }

    // ---------- Door camping ----------

    // Has its prey been out of its building long enough to bother following?
    bool ReadyToLeave()
    {
        if (outsideSince < 0f) outsideSince = Time.time;
        return Time.time - outsideSince >= leaveDelay;
    }

    // The few metres right outside a front door belong to that building
    Roof Doorstep(Vector3 p)
    {
        foreach (var r in roofs)
        {
            if (!r.hasDoor) continue;
            Vector3 d = p - r.door;
            float outward = Vector3.Dot(d, r.doorOut);
            float side = Vector3.Dot(d, Vector3.Cross(Vector3.up, r.doorOut));
            if (outward > -0.5f && outward < doorstepDepth && Mathf.Abs(side) < doorstepHalfWidth && p.y < r.topY) return r;
        }
        return null;
    }

    bool PreyNearDoor(Roof roof)
    {
        if (!roof.hasDoor) return false;
        Vector3 d = pointOfInterest - roof.door;
        d.y = 0f;
        return d.magnitude < doorNearDistance + doorstepDepth * 0.5f;
    }

    // On the roof edge above the door, looking down at the street. Pounces on anyone who steps out under it.
    void UpdatePerch()
    {
        if (!PreyNearDoor(currentRoof)) { SetState(ClimberState.OnRoof); return; }   // they went deeper inside

        if (Time.time - stateStartTime > perchMaxTime)
        {
            perchBlockedRoof = currentRoof;           // no waiting forever: go for the hatch instead
            perchBlockedUntil = Time.time + perchCooldown;
            SetState(ClimberState.OnRoof);
            return;
        }

        if (Arrived())
        {
            agent.isStopped = true;
            FaceTowards(transform.position + plan.outward);
        }

        // Someone right below it, outside: jump on them
        bool sees = target != null && target.IsSpawned && !target.IsGrabbed && Time.time - lastSeenTime < 0.5f;
        if (sees && BuildingAt(target.transform.position) == null)
        {
            Vector3 below = target.transform.position - plan.wallBase;
            below.y = 0f;
            if (below.magnitude < pounceRange)
            {
                leaping = true;
                StartClimb(ClimberState.LeapDown);
            }
        }
    }

    // Onto the edge, then off it in an arc, landing a couple of metres out (toward its prey if it can see them)
    void UpdateLeapDown()
    {
        transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(plan.outward), 540f * Time.deltaTime);
        switch (climbPhase)
        {
            case 0:   // up onto the edge
                if (MantleOverTime(phaseFrom, plan.wallTop, plan.wallTop.y + 0.3f, 0.3f))
                {
                    leapLanding = LeapLanding();
                    NextPhase();
                }
                break;
            case 1:   // fall
                float t = Time.time - phaseStart;
                float height = Mathf.Max(0.1f, phaseFrom.y - leapLanding.y);
                float k = Mathf.Clamp01(t / Mathf.Sqrt(2f * height / 9.81f));
                Vector3 flat = Vector3.Lerp(phaseFrom, leapLanding, k);
                float y = phaseFrom.y - 0.5f * 9.81f * t * t;
                if (k >= 1f || y <= leapLanding.y) { transform.position = leapLanding; NextPhase(); }
                else transform.position = new Vector3(flat.x, y, flat.z);
                break;
            case 2:   // landed
                if (Time.time - phaseStart < leapLandPause) break;
                currentRoof = null;
                leaping = false;
                outsideSince = -1f;
                LandOnNavMesh(leapLanding);
                SetState(ClimberState.Search);
                bool sees = target != null && Time.time - lastSeenTime < loseSightTime;
                GoAfter(sees ? target.transform.position : pointOfInterest, sees);
                break;
        }
    }

    Vector3 LeapLanding()
    {
        Vector3 aim = plan.wallBase + plan.outward * leapDistance;
        // Can see its prey just below? Land closer to them (but never more than twice the normal distance out)
        if (target != null && Time.time - lastSeenTime < 0.5f)
        {
            Vector3 toPrey = target.transform.position - plan.wallBase;
            toPrey.y = 0f;
            if (toPrey.magnitude < leapDistance * 2f && Vector3.Dot(toPrey, plan.outward) > 0.3f)
                aim = plan.wallBase + toPrey * 0.8f;
        }
        aim.y = plan.wallBase.y;
        if (NavMesh.SamplePosition(aim, out var hit, 2f, Filter) && hit.position.y < plan.wallTop.y - 2f) return hit.position;
        return plan.foot;
    }

    // ---------- Roof hatch ----------

    // Does it think its prey is in the room below this roof?
    bool SomeoneInside(Roof roof) => BuildingAt(pointOfInterest) == roof && pointOfInterest.y < roof.topY - 1f;

    // Where it lands on the floor below the hatch
    bool HatchFloor(Roof roof, out Vector3 floor)
    {
        floor = Vector3.zero;
        Vector3 h = roof.hatch.transform.position;
        if (!Physics.Raycast(new Vector3(h.x, roof.topY - 0.5f, h.z), Vector3.down, out var hit, roof.topY + 5f, ~0, QueryTriggerInteraction.Ignore))
            return false;
        if (!NavMesh.SamplePosition(hit.point, out var nav, 1.5f, Filter) || nav.position.y > roof.topY - 2f) return false;
        floor = nav.position;
        return true;
    }

    void SetHatch(bool open)
    {
        Roof roof = OnRoof ? currentRoof : indoorsRoof;
        if (roof == null || roof.hatch == null) return;
        netHatchPos.Value = roof.hatch.transform.position;
        netHatchOpen.Value = open;
        hatchCloseAt = -1f;
    }

    // Crawl to the hatch, open it, peer in, drop
    void UpdateToHatch()
    {
        if (!SomeoneInside(currentRoof)) { SetHatch(false); SetState(ClimberState.OnRoof); return; }
        if (pauseUntil == 0f)
        {
            if (!Arrived()) return;
            pauseUntil = Time.time + hatchPeekTime;   // there: lid opens, it looks in
            agent.isStopped = true;
            SetHatch(true);
        }
        FaceTowards(currentRoof.hatch.transform.position);
        if (Time.time >= pauseUntil) StartClimb(ClimberState.DropIn);
    }

    // Over the hole, fall through, land in the room
    void UpdateDropIn()
    {
        Vector3 hole = currentRoof.hatch.transform.position;
        switch (climbPhase)
        {
            case 0:   // shuffle over the opening
                if (MantleOverTime(phaseFrom, new Vector3(hole.x, currentRoof.topY, hole.z), currentRoof.topY + 0.3f, 0.35f)) NextPhase();
                break;
            case 1:   // fall
                float t = Time.time - phaseStart;
                Vector3 p = new Vector3(hole.x, phaseFrom.y - 0.5f * 9.81f * t * t, hole.z);
                if (p.y <= hatchFloor.y) { p = hatchFloor; NextPhase(); }
                transform.position = p;
                break;
            case 2:   // crouched where it landed
                if (Time.time - phaseStart < landPause) break;
                indoorsRoof = currentRoof;
                currentRoof = null;
                LandOnNavMesh(hatchFloor);
                hatchCloseAt = Time.time + hatchCloseDelay;
                SetState(ClimberState.Search);
                bool sees = target != null && Time.time - lastSeenTime < loseSightTime;
                GoAfter(sees ? target.transform.position : pointOfInterest, sees);
                break;
        }
    }

    void StartClimbOut()
    {
        SetHatch(true);
        StartClimb(ClimberState.ClimbOut);
    }

    // Up through the hatch from below and out onto the roof
    void UpdateClimbOut()
    {
        Roof roof = indoorsRoof;
        Vector3 hole = roof.hatch.transform.position;
        float climbTop = roof.topY - climbFinishBelow;
        switch (climbPhase)
        {
            case 0:   // under the opening
                if (MoveOverTime(phaseFrom, new Vector3(hole.x, phaseFrom.y, hole.z), 0.3f)) NextPhase();
                break;
            case 1:   // climb up
                Vector3 p = transform.position;
                p.y = Mathf.MoveTowards(p.y, climbTop, climbSpeed * Time.deltaTime);
                transform.position = p;
                if (p.y >= climbTop - 0.01f) NextPhase();
                break;
            case 2:   // out onto the roof beside the hatch
                if (!RoofSpot(roof, hole + roof.right * 1.2f, out var spot)) spot = new Vector3(hole.x, roof.topY, hole.z);
                if (MantleOverTime(phaseFrom, spot, roof.topY + 0.4f, mantleTime))
                {
                    currentRoof = roof;
                    indoorsRoof = null;
                    LandOnNavMesh(spot);
                    hatchCloseAt = Time.time + hatchCloseDelay;
                    SetState(ClimberState.OnRoof);
                    GoAfter(pointOfInterest, false);
                }
                break;
        }
    }

    // ---------- Climbing ----------

    void StartClimb(ClimberState climbState)
    {
        agent.ResetPath();
        agent.enabled = false;   // scripted from here until it's back on a NavMesh
        outsideSince = -1f;      // a fresh building: its prey has to leave this one before it follows again
        climbPhase = 0;
        phaseStart = Time.time;
        phaseFrom = transform.position;
        SetState(climbState);
    }

    // Up: press against the wall, climb, pull over the edge, land on the roof
    void UpdateClimbUp()
    {
        Quaternion faceWall = Quaternion.LookRotation(-plan.outward);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, faceWall, 360f * Time.deltaTime);
        float climbTop = plan.wallTop.y - climbFinishBelow;

        switch (climbPhase)
        {
            case 0:   // step up against the wall
                if (MoveOverTime(phaseFrom, plan.wallBase, 0.4f)) NextPhase();
                break;
            case 1:   // climb
                Vector3 p = transform.position;
                p.y = Mathf.MoveTowards(p.y, climbTop, climbSpeed * Time.deltaTime);
                transform.position = p;
                if (p.y >= climbTop - 0.01f) NextPhase();
                break;
            case 2:   // over the edge: up first, then in
                if (MantleOverTime(phaseFrom, plan.roofPoint, plan.wallTop.y + 0.3f, mantleTime))
                {
                    currentRoof = climbTarget;
                    LandOnNavMesh(plan.roofPoint);
                    SetState(ClimberState.OnRoof);
                    GoAfter(pointOfInterest, false);   // someone moved while it climbed? head there
                }
                break;
        }
    }

    // Down: lower itself over the edge, climb down facing the wall, step off at the bottom
    void UpdateClimbDown()
    {
        Quaternion faceWall = Quaternion.LookRotation(-plan.outward);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, faceWall, 360f * Time.deltaTime);
        Vector3 hang = new Vector3(plan.wallBase.x, plan.wallTop.y - climbFinishBelow, plan.wallBase.z);

        switch (climbPhase)
        {
            case 0:   // over the edge
                if (MantleOverTime(phaseFrom, hang, plan.wallTop.y + 0.3f, mantleTime)) NextPhase();
                break;
            case 1:   // climb down
                Vector3 p = transform.position;
                p.y = Mathf.MoveTowards(p.y, plan.wallBase.y, climbSpeed * Time.deltaTime);
                transform.position = p;
                if (p.y <= plan.wallBase.y + 0.01f) NextPhase();
                break;
            case 2:   // step back off the wall
                if (MoveOverTime(phaseFrom, plan.foot, 0.4f))
                {
                    currentRoof = null;
                    LandOnNavMesh(plan.foot);
                    SetState(givingUp ? ClimberState.Patrol : ClimberState.Search);   // off the wall before deciding
                    if (!givingUp) GoAfter(target != null && Time.time - lastSeenTime < loseSightTime ? target.transform.position : pointOfInterest,
                                 target != null && Time.time - lastSeenTime < loseSightTime);
                    givingUp = false;
                }
                break;
        }
    }

    void NextPhase()
    {
        climbPhase++;
        phaseStart = Time.time;
        phaseFrom = transform.position;
    }

    // Straight line from 'from' to 'to' over 'time' seconds. True when done.
    bool MoveOverTime(Vector3 from, Vector3 to, float time)
    {
        float t = Mathf.Clamp01((Time.time - phaseStart) / Mathf.Max(0.01f, time));
        transform.position = Vector3.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t));
        return t >= 1f;
    }

    // Over a ledge: height leads, sideways movement follows, arcing up to 'peakY' in the middle
    bool MantleOverTime(Vector3 from, Vector3 to, float peakY, float time)
    {
        float t = Mathf.Clamp01((Time.time - phaseStart) / Mathf.Max(0.01f, time));
        Vector3 flat = Vector3.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t));
        float y = Mathf.Lerp(from.y, to.y, t) + (peakY - Mathf.Max(from.y, to.y)) * Mathf.Sin(t * Mathf.PI);
        transform.position = new Vector3(flat.x, Mathf.Max(y, Mathf.Min(from.y, to.y)), flat.z);
        return t >= 1f;
    }

    void LandOnNavMesh(Vector3 point)
    {
        transform.position = point;
        agent.enabled = true;
        agent.Warp(point);
    }

    // Works out where to climb 'roof': the wall side nearest 'from' (the climber going up, the destination going
    // down). Tries all four sides, nearest first, until one has street NavMesh at the bottom and roof NavMesh on top.
    bool PlanClimb(Roof roof, Vector3 from, bool goingUp)
    {
        if (roof == null) return false;
        Vector3 d = from - roof.center;
        float lx = Vector3.Dot(d, roof.right), lz = Vector3.Dot(d, roof.fwd);

        var sides = new List<(float score, Vector3 outward, Vector3 edge)>();
        for (int i = 0; i < 4; i++)
        {
            bool xSide = i < 2;
            float sign = (i % 2 == 0) ? 1f : -1f;
            Vector3 outward = (xSide ? roof.right : roof.fwd) * sign;
            float along = xSide ? Mathf.Clamp(lz, -roof.halfZ + 1f, roof.halfZ - 1f) : Mathf.Clamp(lx, -roof.halfX + 1f, roof.halfX - 1f);
            Vector3 edge = roof.center + outward * (xSide ? roof.halfX : roof.halfZ) + (xSide ? roof.fwd : roof.right) * along;
            edge.y = roof.topY;
            Vector3 flatFrom = new Vector3(from.x, roof.topY, from.z);
            sides.Add((Vector3.Distance(flatFrom, edge), outward, edge));
        }
        sides.Sort((a, b) => a.score.CompareTo(b.score));

        foreach (var side in sides)
        {
            Vector3 footGuess = side.edge + side.outward * (wallGap + 0.8f);
            footGuess.y = roof.topY;
            // Street NavMesh straight below the edge (cast down from roof height, then snap)
            if (!Physics.Raycast(footGuess, Vector3.down, out var ground, roof.topY + 20f, ~0, QueryTriggerInteraction.Ignore)) continue;
            if (ground.point.y > roof.topY - 2f) continue;   // a neighbouring roof, not the street
            if (!NavMesh.SamplePosition(ground.point, out var footHit, 1.5f, Filter)) continue;
            if (goingUp && !IsReachable(footHit.position)) continue;
            if (!RoofSpot(roof, side.edge - side.outward * roofInset, out var roofPoint)) continue;

            // Top of the wall (a roof edge / parapet can stick up above the roof slab)
            float top = roof.topY;
            Vector3 probe = side.edge - side.outward * 0.15f + Vector3.up * 4f;
            if (Physics.Raycast(probe, Vector3.down, out var edgeHit, 6f, ~0, QueryTriggerInteraction.Ignore))
                top = Mathf.Max(top, edgeHit.point.y);

            Vector3 wallBase = side.edge + side.outward * wallGap;
            wallBase.y = footHit.position.y;
            plan = new ClimbPlan
            {
                foot = footHit.position,
                wallBase = wallBase,
                wallTop = new Vector3(wallBase.x, top, wallBase.z),
                roofPoint = roofPoint,
                outward = side.outward,
            };
            return true;
        }
        return false;
    }

    // The point on this roof's NavMesh nearest 'p' (never the floor inside the building)
    bool RoofSpot(Roof roof, Vector3 p, out Vector3 spot)
    {
        Vector3 d = p - roof.center;
        float lx = Mathf.Clamp(Vector3.Dot(d, roof.right), -roof.halfX + 0.8f, roof.halfX - 0.8f);
        float lz = Mathf.Clamp(Vector3.Dot(d, roof.fwd), -roof.halfZ + 0.8f, roof.halfZ - 0.8f);
        Vector3 q = roof.center + roof.right * lx + roof.fwd * lz;
        q.y = roof.topY;
        spot = q;
        if (!NavMesh.SamplePosition(q, out var hit, 2f, Filter) || hit.position.y < roof.topY - 0.5f) return false;
        spot = hit.position;
        return true;
    }

    // Which building is this point inside (or on top of)?
    Roof BuildingAt(Vector3 p)
    {
        foreach (var r in roofs)
        {
            Vector3 d = p - r.center;
            if (Mathf.Abs(Vector3.Dot(d, r.right)) <= r.halfX + 0.3f &&
                Mathf.Abs(Vector3.Dot(d, r.fwd)) <= r.halfZ + 0.3f &&
                p.y < r.topY + 3f)
                return r;
        }
        return null;
    }

    // ---------- Senses ----------

    void HearNoise(Vector3 position, float radius)
    {
        if (!IsServer || !IsSpawned || !shownActive || holding) return;
        if (state == ClimberState.Chase) return;
        if (Vector3.Distance(transform.position, position) > radius * hearingMultiplier) return;
        heardTime = Time.time;
        GoAfter(position, false);
    }

    /// <summary>Host: true while this monster is the one hunting tonight (its genome matches) and awake.</summary>
    public bool Hunting => IsServer && IsSpawned && inPlay && shownActive;

    // Host: a scout (or the Director's fallback hint) says a player was here: go after it like a heard noise
    public void Relay(Vector3 position)
    {
        if (!Hunting || holding || state == ClimberState.Chase) return;
        heardTime = Time.time;
        GoAfter(position, false);
    }

    FirstPersonController FindVisiblePlayer()
    {
        if (Time.time < ignorePlayersUntil) return null;
        // Perched: its head pokes out over the roof edge, looking down at the street
        Vector3 eye = state == ClimberState.Perch ? plan.wallTop + Vector3.up * 0.5f : transform.position + Vector3.up * eyeHeight;
        FirstPersonController best = null;
        float bestDist = float.MaxValue;

        foreach (var player in FirstPersonController.All)
        {
            if (!player.IsSpawned || player.IsGrabbed) continue;
            Vector3 toPlayer = Chest(player) - eye;
            float dist = toPlayer.magnitude;

            var light = player.GetComponentInChildren<Flashlight>();
            float range = (light != null && light.IsOn) ? flashlightSightRange : sightRange;
            if (StreetLamp.IsUnderLight(player.transform.position)) range = Mathf.Max(range, litSightRange);
            if (player.IsCrouching) range *= crouchVisibility;
            range = Mathf.Max(range, feelRange);   // blind or not, it notices you right next to it
            if (dist > range) continue;

            Vector3 flat = new Vector3(toPlayer.x, 0f, toPlayer.z);
            bool inCone = flat.sqrMagnitude < 0.01f || Vector3.Angle(transform.forward, flat) <= viewAngle / 2f;
            if (!inCone && flat.magnitude > feelRange) continue;
            if (!LineClear(eye, player)) continue;

            if (dist < bestDist) { best = player; bestDist = dist; }
        }
        return best;
    }

    // End of a lunge: still in its path (close ahead of it and near its line)?
    bool InLungePath(FirstPersonController player)
    {
        Vector3 local = player.transform.position - transform.position;
        float ahead = Vector3.Dot(local, lungeDir);
        float side = Mathf.Abs(Vector3.Dot(local, Vector3.Cross(Vector3.up, lungeDir)));
        if (ahead < -1f || ahead > grabEscapeRange || side > lungeWidth) return false;
        return LineClear(transform.position + Vector3.up * eyeHeight, player);
    }

    bool CanGrab(FirstPersonController player, float range)
    {
        Vector3 p = player.transform.position;
        if (Vector3.Distance(p, transform.position) > range) return false;
        return LineClear(transform.position + Vector3.up * eyeHeight, player);
    }

    static bool LineClear(Vector3 from, FirstPersonController player)
    {
        Vector3 to = Chest(player) - from;
        float dist = to.magnitude;
        if (dist < 0.01f) return true;
        // Fences are see-through
        if (Physics.Raycast(from, to / dist, out var hit, dist + 0.5f, FenceBuilder.AllButFences, QueryTriggerInteraction.Ignore))
            return hit.collider.GetComponentInParent<FirstPersonController>() == player;
        return true;
    }

    static Vector3 Chest(FirstPersonController p) =>
        p.transform.position + Vector3.up * (p.IsCrouching ? 0.7f : 1.4f);

    // ---------- Navigation helpers ----------

    NavMeshQueryFilter Filter => new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = agent.areaMask };

    bool IsReachable(Vector3 position, float snapDistance = 1.5f)
    {
        if (!agent.enabled || !agent.isOnNavMesh) return false;
        if (!NavMesh.SamplePosition(position, out var hit, snapDistance, Filter)) return false;
        return agent.CalculatePath(hit.position, path) && path.status == NavMeshPathStatus.PathComplete;
    }

    void GoToRandomPoint(Vector3 center, float radius)
    {
        for (int i = 0; i < 15; i++)
        {
            Vector3 random = center + Random.insideUnitSphere * radius;
            if (NavMesh.SamplePosition(random, out var hit, 4f, Filter) &&
                agent.CalculatePath(hit.position, path) && path.status == NavMeshPathStatus.PathComplete)
            {
                agent.SetDestination(hit.position);
                return;
            }
        }
    }

    bool Arrived() => agent.enabled && !agent.pathPending && agent.remainingDistance <= agent.stoppingDistance + 0.4f;

    void FaceTowards(Vector3 position)
    {
        Vector3 dir = position - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.01f) return;
        transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(dir), 360f * Time.deltaTime);
    }

    // ---------- Catching ----------

    void CatchPlayer()
    {
        Debug.Log($"Climber caught player {target.OwnerClientId}");
        target.Caught(NetworkObject);
        CaughtFxRpc(grabHoldTime);   // holds the pose while it has you, grab sound
        heldVictim = target;
        Feed();
        target = null;
        ignorePlayersUntil = Time.time + catchCooldown;
        SetState(ClimberState.Patrol);
        holding = true;
        holdUntil = Time.time + grabHoldTime;
        agent.velocity = Vector3.zero;
        agent.isStopped = true;
    }

    [Rpc(SendTo.Everyone)]
    void GrabAnimRpc(float holdSeconds)
    {
        var anim = GetComponentInChildren<ClimberAnimator>();
        if (anim != null) anim.PlayGrab(holdSeconds);
    }

    [Rpc(SendTo.Everyone)]
    void CaughtFxRpc(float holdSeconds)
    {
        var anim = GetComponentInChildren<ClimberAnimator>();
        if (anim != null) anim.PlayGrab(holdSeconds);
        var sounds = GetComponent<ClimberAudio>();
        if (sounds != null) sounds.PlayGrab();
    }

    // ---------- Debug drawing in the Scene view ----------

    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;
        Gizmos.color = Color.magenta;
        Gizmos.DrawWireSphere(pointOfInterest, 0.5f);
        if (state == ClimberState.ToWall || Climbing || state == ClimberState.ToEdge)
        {
            Gizmos.DrawLine(plan.foot, plan.wallBase);
            Gizmos.DrawLine(plan.wallBase, plan.wallTop);
            Gizmos.DrawLine(plan.wallTop, plan.roofPoint);
        }
    }
}
