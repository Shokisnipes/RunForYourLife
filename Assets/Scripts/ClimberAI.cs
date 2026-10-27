using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

public enum ClimberState { Patrol, Investigate, Chase, Search, ToWall, ClimbUp, OnRoof, ToEdge, ClimbDown }

// The Climber genome's monster: a lanky crawler that hunts on the streets and, when you hide inside, climbs the
// building's wall to the roof. Host runs all of this; everyone else sees it through NetworkTransform and the
// synced state (ClimberAnimator). Its NavMesh agent type ("Climber") is too tall for doorways, so it can't walk in.
[RequireComponent(typeof(NavMeshAgent))]
public class ClimberAI : NetworkBehaviour
{
    [Header("Current state (read only)")]
    public ClimberState state = ClimberState.Patrol;

    readonly NetworkVariable<ClimberState> netState = new NetworkVariable<ClimberState>(ClimberState.Patrol);
    readonly NetworkVariable<bool> netRunning = new NetworkVariable<bool>();
    readonly NetworkVariable<bool> netInPlay = new NetworkVariable<bool>(false);
    public ClimberState SyncedState => netState.Value;
    public bool SyncedRunning => netRunning.Value;

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
    [Tooltip("Short pause before the grab lands. Get this far away in time and it misses.")]
    public float grabWindup = 0.35f;
    public float grabEscapeRange = 2.6f;
    [Tooltip("Stays still this long after a catch.")]
    public float grabHoldTime = 2.5f;
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

    [Header("Search")]
    public float searchDuration = 12f;
    public float searchArea = 12f;

    NavMeshAgent agent;
    NavMeshPath path;
    FirstPersonController target;
    Vector3 pointOfInterest, lastSeenPosition, homePosition;
    float lastSeenTime = -999f, heardTime = -999f, stateStartTime, pauseUntil, ignorePlayersUntil, nextRepath;
    float grabStart = -1f, holdUntil;
    bool holding, inPlay;

    // Climbing
    class Roof { public Vector3 center, right, fwd; public float halfX, halfZ, topY; }
    struct ClimbPlan { public Vector3 foot, wallBase, wallTop, roofPoint, outward; }
    readonly List<Roof> roofs = new List<Roof>();
    Roof currentRoof;   // the roof it's on (null on the street)
    ClimbPlan plan;
    int climbPhase;
    float phaseStart;
    Vector3 phaseFrom;
    bool givingUp;      // climbing down because it lost interest, not to chase someone

    bool OnRoof => currentRoof != null;
    bool Climbing => state == ClimberState.ClimbUp || state == ClimberState.ClimbDown;

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        path = new NavMeshPath();
        homePosition = transform.position;
    }

    // Base senses are remembered the first time, so applying a new genome each run never stacks multipliers.
    bool baseSensesSaved;
    float baseSight, baseFlashSight, baseLitSight, baseHearing;

    // Host only: called by GenomeManager at game start. Only shows up when the genome is a Climber.
    public void ApplyGenome(MonsterGenome genome)
    {
        if (!baseSensesSaved)
        {
            baseSight = sightRange; baseFlashSight = flashlightSightRange;
            baseLitSight = litSightRange; baseHearing = hearingMultiplier;
            baseSensesSaved = true;
        }
        sightRange = baseSight * genome.sightRangeMultiplier;
        flashlightSightRange = baseFlashSight * genome.sightRangeMultiplier;
        litSightRange = baseLitSight * genome.sightRangeMultiplier;
        hearingMultiplier = baseHearing * genome.hearingMultiplier;
        inPlay = genome.access == GenomeAccess.Climber;
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
        SetState(ClimberState.Patrol);
    }

    // Every building's roof, from the "Roof" boxes the Building Generator makes
    void FindRoofs()
    {
        roofs.Clear();
        foreach (var box in FindObjectsByType<BoxCollider>())
        {
            if (box.name != "Roof") continue;
            var t = box.transform;
            Vector3 s = t.lossyScale;
            roofs.Add(new Roof
            {
                center = t.TransformPoint(box.center),
                right = t.right,
                fwd = t.forward,
                halfX = box.size.x * 0.5f * Mathf.Abs(s.x),
                halfZ = box.size.z * 0.5f * Mathf.Abs(s.z),
                topY = t.TransformPoint(box.center).y + box.size.y * 0.5f * Mathf.Abs(s.y),
            });
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
        }

        var seen = Climbing ? null : FindVisiblePlayer();
        if (seen != null)
        {
            target = seen;
            lastSeenTime = Time.time;
            lastSeenPosition = seen.transform.position;
            if (state != ClimberState.Chase) GoAfter(lastSeenPosition, true);
        }

        switch (state)
        {
            case ClimberState.Patrol:      UpdatePatrol(); break;
            case ClimberState.Investigate: if (Arrived()) SetState(ClimberState.Search); break;
            case ClimberState.Chase:       UpdateChase(); break;
            case ClimberState.Search:      UpdateSearch(); break;
            case ClimberState.ToWall:      if (Arrived()) StartClimb(ClimberState.ClimbUp); break;
            case ClimberState.ClimbUp:     UpdateClimbUp(); break;
            case ClimberState.OnRoof:      UpdateOnRoof(); break;
            case ClimberState.ToEdge:      if (Arrived()) StartClimb(ClimberState.ClimbDown); break;
            case ClimberState.ClimbDown:   UpdateClimbDown(); break;
        }

        bool running = canRun && state == ClimberState.Chase && target != null &&
                       Vector3.Distance(transform.position, target.transform.position) > runWhenFartherThan;
        if (agent.enabled && state == ClimberState.Chase) agent.speed = running ? runSpeed : chaseSpeed;
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
                agent.speed = crawlSpeed;
                GoToRandomPoint(patrolCenter != null ? patrolCenter.position : homePosition, wanderRadius);
                break;
            case ClimberState.Investigate:
            case ClimberState.Search:
                agent.speed = huntSpeed;
                agent.SetDestination(pointOfInterest);
                break;
            case ClimberState.Chase:
                agent.speed = chaseSpeed;
                nextRepath = 0f;
                break;
            case ClimberState.ToWall:
                agent.speed = huntSpeed;
                agent.SetDestination(plan.foot);
                break;
            case ClimberState.OnRoof:
                agent.speed = huntSpeed;
                nextRepath = 0f;
                break;
            case ClimberState.ToEdge:
                agent.speed = huntSpeed;
                agent.SetDestination(plan.roofPoint);
                break;
        }
    }

    // Decide how to get to 'point': crawl there along the street, or climb the building it's in.
    void GoAfter(Vector3 point, bool seenIt)
    {
        pointOfInterest = point;
        if (Climbing) return;   // finish the climb first; it heads for the new spot after
        Roof building = BuildingAt(point);

        if (OnRoof)
        {
            if (building == currentRoof) { if (state != ClimberState.OnRoof) SetState(ClimberState.OnRoof); return; }
            if (state != ClimberState.ToEdge && PlanClimb(currentRoof, point, false))
            {
                givingUp = false;
                SetState(ClimberState.ToEdge);   // climb down toward it
            }
            return;
        }

        if (IsReachable(point))
        {
            if (seenIt) { if (state != ClimberState.Chase) SetState(ClimberState.Chase); }
            else SetState(ClimberState.Investigate);
            return;
        }

        // Inside a building: climb it. Already on the way to that wall? Keep going.
        if (building != null)
        {
            if (state == ClimberState.ToWall && building == climbTarget) return;
            if (PlanClimb(building, transform.position, true)) { climbTarget = building; SetState(ClimberState.ToWall); return; }
        }
        SetState(ClimberState.Investigate);   // somewhere it can't get to: go as close as it can
    }

    Roof climbTarget;

    void UpdatePatrol()
    {
        if (OnRoof) { GiveUpAndClimbDown(); return; }
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

        // Mid-lunge: stopped, facing you
        if (grabStart >= 0f)
        {
            FaceTowards(playerPos);
            if (Time.time - grabStart < grabWindup) return;
            grabStart = -1f;
            if (CanGrab(target, grabEscapeRange)) { CatchPlayer(); return; }
            agent.isStopped = false;   // missed
        }
        else if (CanGrab(target, grabRange))
        {
            grabStart = Time.time;
            agent.velocity = Vector3.zero;
            agent.isStopped = true;
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

        // Crawl to the spot right above where it thinks you are (task 4b: the hatch drop goes here)
        if (Time.time < nextRepath) return;
        nextRepath = Time.time + 0.5f;
        if (RoofSpot(currentRoof, pointOfInterest, out var spot)) agent.SetDestination(spot);
    }

    void GiveUpAndClimbDown()
    {
        Vector3 away = homePosition;
        if (PlanClimb(currentRoof, away, false)) { givingUp = true; SetState(ClimberState.ToEdge); }
    }

    // ---------- Climbing ----------

    void StartClimb(ClimberState climbState)
    {
        agent.ResetPath();
        agent.enabled = false;   // scripted from here until it's back on a NavMesh
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

    FirstPersonController FindVisiblePlayer()
    {
        if (Time.time < ignorePlayersUntil) return null;
        Vector3 eye = transform.position + Vector3.up * eyeHeight;
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
            if (dist > range) continue;

            Vector3 flat = new Vector3(toPlayer.x, 0f, toPlayer.z);
            bool inCone = flat.sqrMagnitude < 0.01f || Vector3.Angle(transform.forward, flat) <= viewAngle / 2f;
            if (!inCone && flat.magnitude > feelRange) continue;
            if (!LineClear(eye, player)) continue;

            if (dist < bestDist) { best = player; bestDist = dist; }
        }
        return best;
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
        if (Physics.Raycast(from, to / dist, out var hit, dist + 0.5f, ~0, QueryTriggerInteraction.Ignore))
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
        GrabAnimRpc(grabHoldTime);   // holds the pose while it has you
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
