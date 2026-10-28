using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

public enum ScoutState : byte { Wander, Investigate, Spotted, Screech, Flee, Frozen, Dead, Window, ClimbUp, ClimbDown }

// A scout: a tiny crawler that shares the monster's genome and is its eyes. Brain runs on the host only
// (spawned by ScoutDirector); every client animates and plays sounds from the synced state.
//   Wander / Investigate -> sees a player at night -> Spotted (scuttles off) -> Screech (wind-up, audible)
//   -> if it finishes, the monster learns where that player WAS (ScoutDirector.Relay) -> Flee.
//   Hold a flashlight beam on it -> Frozen (dazzled; a screech in progress is cut off) -> walk up and stomp it.
//   Players getting close (or a Sight scout seeing a flashlight) -> Flee.
// Moves on the Climber's NavMesh (streets, and in through open doors). When a closed door is in the way it goes
// through a holed or broken window instead (Window: a scripted hop up to the hole, through, and down the other side).
// It climbs walls too (ClimbUp / ClimbDown, scripted like the window hop): up onto a flat roof to escape players
// (they can't follow) or now and then to look around from up high, and back down the side nearest where it's going.
[RequireComponent(typeof(NavMeshAgent))]
public class ScoutAI : NetworkBehaviour
{
    public static readonly System.Collections.Generic.List<ScoutAI> All = new System.Collections.Generic.List<ScoutAI>();

    readonly NetworkVariable<ScoutState> netState = new NetworkVariable<ScoutState>(ScoutState.Wander);
    // Its genome's sense, so every client gives it the right eyes/ears (set by the host before it spawns)
    readonly NetworkVariable<bool> netSight = new NetworkVariable<bool>(true);
    public ScoutState State => netState.Value;
    public bool IsDead => netState.Value == ScoutState.Dead;

    [Header("Speed (m/s)")]
    public float wanderSpeed = 1.2f;
    public float huntSpeed = 2.4f;
    public float fleeSpeed = 4.5f;

    [Header("Senses")]
    [Tooltip("How far it sees a player (before the genome multiplier).")]
    public float sightRange = 16f;
    [Tooltip("...a player with their flashlight on.")]
    public float flashlightSightRange = 26f;
    [Tooltip("Full cone width it sees in (degrees). Low to the ground, it sees wide.")]
    public float viewAngle = 160f;
    public float eyeHeight = 0.3f;
    [Tooltip("Crouching shrinks how far it sees you.")]
    public float crouchVisibility = 0.6f;
    [Tooltip("A player this close is noticed even unseen (and it bolts).")]
    public float feelRange = 2.5f;
    [Tooltip("Hears a noise when within its radius times this (Hearing genome; a Sight scout uses hearingSightGenome).")]
    public float hearingHearingGenome = 1.5f;
    public float hearingSightGenome = 0.6f;
    [Tooltip("Sight range multiplier for a Sight genome (and for a Hearing genome).")]
    public float sightSightGenome = 1.3f;
    public float sightHearingGenome = 0.7f;

    [Header("Relay")]
    [Tooltip("After spotting someone it scuttles off this long before it screeches.")]
    public float spottedRunTime = 1.2f;
    [Tooltip("The screech wind-up: kill or dazzle it before this ends and nothing gets passed on.")]
    public float screechTime = 2.8f;
    [Tooltip("After a relay it won't relay again for this long.")]
    public float relayCooldown = 25f;

    [Header("Flee")]
    [Tooltip("A seen player closer than this makes it run (outside a relay).")]
    public float fleeTriggerDistance = 6f;
    [Tooltip("A Sight scout runs from a flashlight-carrying player this close.")]
    public float flashlightFleeDistance = 14f;
    public float fleeDistance = 18f;
    public float fleeTime = 5f;

    [Header("Flashlight + stomp")]
    [Tooltip("How long a beam has to stay on it before it freezes.")]
    public float freezeLightTime = 0.3f;
    [Tooltip("How long it stays frozen: Sight scouts are dazzled longer.")]
    public float freezeTimeSight = 4f;
    public float freezeTimeHearing = 2.5f;
    [Tooltip("After breaking free it can't be frozen again for this long.")]
    public float freezeImmunity = 3f;
    [Tooltip("The beam freezes it out to this distance...")]
    public float beamRange = 18f;
    [Tooltip("...within this many degrees of where the player looks.")]
    public float beamAngle = 20f;
    [Tooltip("Players stomp it from this close (flat distance).")]
    public float stompRange = 1.6f;
    [Tooltip("A stomp is a noise this big.")]
    public float stompNoise = 8f;
    [Tooltip("The body stays this long, then goes.")]
    public float corpseTime = 120f;

    [Header("Windows")]
    [Tooltip("Looks for a holed/broken window this close to where it's going when the way there is shut.")]
    public float windowSearchRadius = 30f;
    [Tooltip("Seconds to scramble up, squeeze through the hole and drop down the other side.")]
    public float windowHopTime = 1.4f;

    [Header("Climbing")]
    [Tooltip("How fast it scuttles up and down walls (m/s).")]
    public float climbSpeed = 2.5f;
    [Tooltip("Looks for a climbable wall this close.")]
    public float climbSearchDistance = 5f;
    [Tooltip("Won't climb anything taller than this (m).")]
    public float maxClimbHeight = 16f;
    [Tooltip("Chance it escapes up a wall when it flees (if there's one close).")]
    [Range(0f, 1f)] public float fleeClimbChance = 0.5f;
    [Tooltip("Chance, each time it picks a new spot to wander to, that it climbs up for a look around instead.")]
    [Range(0f, 1f)] public float lookoutChance = 0.15f;
    [Tooltip("How long it stays up on a roof before heading back down (s, random in this range).")]
    public Vector2 roofTime = new Vector2(10f, 25f);
    [Tooltip("Up on a roof it wanders only this far.")]
    public float roofWanderRadius = 5f;

    [Header("Wander")]
    public float wanderRadius = 20f;
    public Vector2 wanderPause = new Vector2(1f, 4f);

    NavMeshAgent agent;
    ScoutAnimator anim;
    ScoutAudio sfx;
    Vector3 lastPos, smoothVel;

    // Host state
    ScoutState state;
    float stateStart, pauseUntil = -1f, litTime, frozenUntil, immuneUntil, relayReadyAt, diedAt;
    float hearing = 1f, sightMul = 1f, freezeTime = 3f;
    bool sightGenome;
    Vector3 areaCenter, spottedPos;
    FirstPersonController spottedPlayer;
    // Scripted move (window hop or wall climb): walk to linkPts[0], then follow the points, then carry on to linkDest
    bool linkPending, linkHasDest;
    ScoutState linkKind, linkResume;
    Vector3[] linkPts;
    float[] linkTimes;
    Vector3 linkFace, linkDest;
    float linkStart, roofUntil;
    Vector3 streetCenter;   // where it was before going up on a roof

    /// <summary>Host: true when the Director may send it somewhere (it's just roaming).</summary>
    public bool Idle => IsServer && state == ScoutState.Wander;

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        agent.enabled = false;   // the host turns it on when spawned
        anim = GetComponentInChildren<ScoutAnimator>();
        sfx = GetComponent<ScoutAudio>();
    }

    void OnEnable() { All.Add(this); NoiseSystem.OnNoise += HearNoise; }
    void OnDisable() { All.Remove(this); NoiseSystem.OnNoise -= HearNoise; }

    // Host: called by the Director right after spawning
    public void Setup(MonsterGenome genome)
    {
        sightGenome = genome == null || genome.sense == GenomeSense.Sight;
        hearing = sightGenome ? hearingSightGenome : hearingHearingGenome;
        sightMul = sightGenome ? sightSightGenome : sightHearingGenome;
        if (genome != null && genome.blind) sightMul = 1f;   // a blind monster's scouts are its eyes: they see fine
        freezeTime = sightGenome ? freezeTimeSight : freezeTimeHearing;
        netSight.Value = sightGenome;
    }

    public override void OnNetworkSpawn()
    {
        lastPos = transform.position;
        netState.OnValueChanged += OnStateChanged;
        if (anim != null) anim.ShowSense(netSight.Value);
        if (!IsServer) return;
        agent.enabled = true;
        areaCenter = transform.position;
        SetState(ScoutState.Wander);
    }

    public override void OnNetworkDespawn() => netState.OnValueChanged -= OnStateChanged;

    // ---------- Every client: animation and sound ----------

    void OnStateChanged(ScoutState old, ScoutState now)
    {
        if (sfx == null) return;
        if (now == ScoutState.Screech) sfx.Screech();
        else if (old == ScoutState.Screech) sfx.StopScreech();
        if (now == ScoutState.Dead) { sfx.Die(); if (anim != null) anim.Die(); }
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt > 0f && anim != null)
        {
            Vector3 v = (transform.position - lastPos) / dt;
            if (v.sqrMagnitude > 20f * 20f) v = Vector3.zero;   // teleported
            smoothVel = Vector3.Lerp(smoothVel, v, 1f - Mathf.Exp(-10f * dt));
            anim.velocity = netState.Value == ScoutState.Frozen ? Vector3.zero : smoothVel;
            anim.screeching = netState.Value == ScoutState.Screech;
            var st = netState.Value;   // scrambling through a window or up a wall, or backing down one
            anim.climb = st == ScoutState.Window || st == ScoutState.ClimbUp ? 1 : st == ScoutState.ClimbDown ? -1 : 0;
            if (IsDead && !anim.Dead) anim.Die();
        }
        lastPos = transform.position;

        if (IsServer && IsSpawned) Think();
    }

    // ---------- Host brain ----------

    void SetState(ScoutState s)
    {
        state = s;
        stateStart = Time.time;
        pauseUntil = -1f;
        if (!IsLink(s)) linkPending = false;   // a new plan drops any window/climb route
        netState.Value = s;
        if (!agent.enabled || !agent.isOnNavMesh) return;
        agent.isStopped = s == ScoutState.Frozen || s == ScoutState.Screech || s == ScoutState.Dead;
        agent.speed = s == ScoutState.Flee || s == ScoutState.Spotted ? fleeSpeed
                    : s == ScoutState.Investigate ? huntSpeed : wanderSpeed;
    }

    static bool IsLink(ScoutState s) => s == ScoutState.Window || s == ScoutState.ClimbUp || s == ScoutState.ClimbDown;

    // Up on a roof (the streets and ground floors are at about 0)
    bool OnRoof => transform.position.y > 1.8f;

    bool Night => GameFlow.Instance != null && GameFlow.Instance.IsSpawned && GameFlow.Instance.Phase == GamePhase.Night;

    void Think()
    {
        if (state == ScoutState.Dead)
        {
            if (Time.time - diedAt > corpseTime) NetworkObject.Despawn(true);
            return;
        }
        if (IsLink(state)) { UpdateLink(); return; }
        if (!agent.isOnNavMesh) return;

        // Reached the window or wall it was heading for: through it / up it / down it
        if (linkPending && !agent.pathPending && Vector3.Distance(transform.position, linkPts[0]) < 0.7f)
        {
            linkPending = false;
            linkResume = state;
            SetState(linkKind);
            agent.enabled = false;   // scripted from here; NetworkTransform shows it to everyone
            linkStart = Time.time;
            return;
        }

        // A beam held on it dazzles it (and cuts a screech off: nothing gets relayed)
        litTime = InBeam() ? litTime + Time.deltaTime : 0f;
        if (state != ScoutState.Frozen && litTime >= freezeLightTime && Time.time >= immuneUntil)
        {
            SetState(ScoutState.Frozen);
            frozenUntil = Time.time + freezeTime;
            return;
        }

        var seen = FindVisiblePlayer(out float seenDist);
        bool roaming = state == ScoutState.Wander || state == ScoutState.Investigate;
        if (seen != null && roaming)
        {
            if (Night && Time.time >= relayReadyAt)
            {
                // Got one: scuttle off out of reach, then call it in
                spottedPlayer = seen;
                spottedPos = seen.transform.position;
                SetState(ScoutState.Spotted);
                RunFrom(seen.transform.position);
                return;
            }
            var light = seen.GetComponentInChildren<Flashlight>();
            float flee = sightGenome && light != null && light.IsOn ? flashlightFleeDistance : fleeTriggerDistance;
            if (seenDist < flee && !OnRoof) { Flee(seen.transform.position); return; }
        }
        var near = NearestPlayer(out float nearDist);
        if (near != null && !OnRoof && nearDist < feelRange && state != ScoutState.Frozen && state != ScoutState.Screech)
        {
            if (state != ScoutState.Flee && state != ScoutState.Spotted) { Flee(near.transform.position); return; }
        }

        switch (state)
        {
            case ScoutState.Wander: UpdateWander(); break;
            case ScoutState.Investigate:
                if (Arrived()) { areaCenter = agent.destination; SetState(ScoutState.Wander); }
                break;
            case ScoutState.Spotted:
                if (Time.time - stateStart >= spottedRunTime || Arrived()) SetState(ScoutState.Screech);
                break;
            case ScoutState.Screech:
                if (spottedPlayer != null) FaceTowards(spottedPlayer.transform.position);
                if (Time.time - stateStart >= screechTime)
                {
                    if (ScoutDirector.Instance != null) ScoutDirector.Instance.Relay(spottedPos, this);
                    relayReadyAt = Time.time + relayCooldown;
                    Flee(spottedPlayer != null ? spottedPlayer.transform.position : spottedPos);
                }
                break;
            case ScoutState.Flee:
                if (Arrived() || Time.time - stateStart > fleeTime) { areaCenter = transform.position; SetState(ScoutState.Wander); }
                break;
            case ScoutState.Frozen:
                if (Time.time >= frozenUntil)
                {
                    immuneUntil = Time.time + freezeImmunity;
                    var p = NearestPlayer(out _);
                    if (p != null) Flee(p.transform.position); else SetState(ScoutState.Wander);
                }
                break;
        }
    }

    void UpdateWander()
    {
        if (!Arrived()) return;
        if (pauseUntil < 0f) { pauseUntil = Time.time + Random.Range(wanderPause.x, wanderPause.y); return; }
        if (Time.time < pauseUntil) return;
        pauseUntil = -1f;
        if (OnRoof)
        {
            // Had its look around: back down to the streets (GoTo finds the way down)
            if (Time.time >= roofUntil) { Vector2 d = Random.insideUnitCircle * wanderRadius * 0.5f; GoTo(streetCenter + new Vector3(d.x, 0f, d.y)); return; }
            Vector2 rr = Random.insideUnitCircle * roofWanderRadius;
            GoTo(areaCenter + new Vector3(rr.x, 0f, rr.y));
            return;
        }
        if (Random.value < lookoutChance && ClimbUp(Vector3.zero)) return;   // up for a look around
        Vector2 r = Random.insideUnitCircle * wanderRadius;
        GoTo(areaCenter + new Vector3(r.x, 0f, r.y));
    }

    /// <summary>Host: the Director sends it to look around somewhere (near where players are).</summary>
    public void SendToward(Vector3 point)
    {
        if (state != ScoutState.Wander && state != ScoutState.Investigate) return;
        SetState(ScoutState.Investigate);
        if (!GoTo(point)) SetState(ScoutState.Wander);
    }

    void HearNoise(Vector3 position, float radius)
    {
        if (!IsServer || !IsSpawned || (state != ScoutState.Wander && state != ScoutState.Investigate)) return;
        if (Vector3.Distance(transform.position, position) > radius * hearing) return;
        SendToward(position);
    }

    void Flee(Vector3 from)
    {
        SetState(ScoutState.Flee);
        // Up a wall where nobody can follow, if there's one close (away from the threat if it can)
        Vector3 away = transform.position - from;
        away.y = 0f;
        if (!OnRoof && Random.value < fleeClimbChance && ClimbUp(away.normalized)) return;
        RunFrom(from);
    }

    // Head for a NavMesh point about fleeDistance away from the threat. Tries a few directions and skips any whose
    // path starts off toward the threat (a dead end that loops back past the player is no escape).
    void RunFrom(Vector3 from)
    {
        Vector3 away = transform.position - from;
        away.y = 0f;
        if (away.sqrMagnitude < 0.01f) away = transform.forward;
        away.Normalize();
        var filter = new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = NavMesh.AllAreas };
        var path = new NavMeshPath();
        foreach (float angle in new[] { 0f, 35f, -35f, 70f, -70f, 110f, -110f, 150f, -150f })
        {
            Vector3 dir = Quaternion.Euler(0f, angle, 0f) * away;
            if (!NavMesh.SamplePosition(transform.position + dir * fleeDistance, out var hit, 6f, filter)) continue;
            if (!NavMesh.CalculatePath(transform.position, hit.position, filter, path) || path.status != NavMeshPathStatus.PathComplete) continue;
            if (path.corners.Length < 2) continue;
            Vector3 first = path.corners[1] - transform.position;
            first.y = 0f;
            Vector3 toThreat = from - transform.position;
            toThreat.y = 0f;
            // The first leg must not head at the threat, and the path never comes closer to it than we are now
            if (first.sqrMagnitude > 0.01f && Vector3.Angle(first, toThreat) < 60f) continue;
            if (ClosestApproach(path.corners, from) < Mathf.Min(toThreat.magnitude, 4f) * 0.8f) continue;
            agent.SetPath(path);
            return;
        }
        GoTo(transform.position + away * fleeDistance);   // cornered: best effort
    }

    static float ClosestApproach(Vector3[] corners, Vector3 p)
    {
        float best = float.MaxValue;
        for (int i = 1; i < corners.Length; i++)
        {
            Vector3 a = corners[i - 1], b = corners[i];
            Vector3 ab = b - a;
            float t = ab.sqrMagnitude > 0.0001f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            Vector3 c = a + ab * t - p;
            c.y = 0f;
            best = Mathf.Min(best, c.magnitude);
        }
        return best;
    }

    // Walk there; if the way is shut (a closed door), go round through a holed/broken window instead.
    // Up on a roof, the way is down a wall on the side nearest the goal.
    bool GoTo(Vector3 point)
    {
        var filter = Filter;
        if (!NavMesh.SamplePosition(point, out var hit, 6f, filter)) return false;
        linkPending = false;
        var path = new NavMeshPath();
        if (NavMesh.CalculatePath(transform.position, hit.position, filter, path) && path.status == NavMeshPathStatus.PathComplete)
            return agent.SetPath(path);
        if (OnRoof ? ClimbDownRoute(hit.position) : WindowRoute(hit.position)) return true;
        return agent.SetDestination(hit.position);   // as close as it can get
    }

    // Plans a scripted move: walk to pts[0], then pass through the points (times = seconds for each leg).
    // face = which way it faces on the way (zero = the way it's going).
    bool PlanLink(ScoutState kind, Vector3[] pts, float[] times, Vector3 face, Vector3 dest, bool hasDest)
    {
        if (!agent.SetDestination(pts[0])) return false;
        linkPending = true;
        linkKind = kind;
        linkPts = pts;
        linkTimes = times;
        linkFace = face;
        linkDest = dest;
        linkHasDest = hasDest;
        return true;
    }

    // Masks out fences and doors: neither is a wall to climb
    static int ClimbMask => Physics.DefaultRaycastLayers & FenceBuilder.AllButFences & Door.AllButDoors;

    // Up the nearest climbable wall onto a flat roof (prefer = a direction it would rather go; zero = any).
    // The wall has to go straight up to the roof edge (no awning or balcony in the way) and the roof must be walkable.
    bool ClimbUp(Vector3 prefer)
    {
        var filter = Filter;
        var path = new NavMeshPath();
        Vector3 me = transform.position;
        float best = float.MaxValue;
        Vector3[] bestPts = null;
        Vector3 bestFace = default;
        float bestHeight = 0f;
        for (int i = 0; i < 16; i++)
        {
            Vector3 dir = Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward;
            if (!Physics.Raycast(me + Vector3.up * 0.3f, dir, out var wall, climbSearchDistance, ClimbMask, QueryTriggerInteraction.Ignore)) continue;
            Vector3 n = wall.normal;
            if (Mathf.Abs(n.y) > 0.2f) continue;
            n.y = 0f;
            n.Normalize();
            // The roof above this wall
            if (!Physics.Raycast(wall.point - n * 0.7f + Vector3.up * 40f, Vector3.down, out var roof, 45f, ClimbMask, QueryTriggerInteraction.Ignore)) continue;
            float height = roof.point.y - me.y;
            if (roof.normal.y < 0.9f || height < 2f || height > maxClimbHeight) continue;
            // Nothing sticking out of the wall on the way up
            Vector3 face = wall.point + n * 0.2f;
            if (Physics.Raycast(new Vector3(face.x, me.y + 0.4f, face.z), Vector3.up, height + 0.2f, ClimbMask, QueryTriggerInteraction.Ignore)) continue;
            if (!NavMesh.SamplePosition(new Vector3(wall.point.x, roof.point.y, wall.point.z) - n * 0.8f, out var top, 1.2f, filter) ||
                Mathf.Abs(top.position.y - roof.point.y) > 0.4f) continue;
            if (!NavMesh.SamplePosition(new Vector3(face.x, me.y, face.z) + n * 0.3f, out var foot, 1f, filter)) continue;
            float walk = PathLength(me, foot.position, filter, path);
            if (walk < 0f) continue;
            float score = walk + (prefer != Vector3.zero ? Vector3.Angle(prefer, -n) / 30f : 0f);
            if (score >= best) continue;
            best = score;
            bestHeight = height;
            bestFace = -n;
            bestPts = new[]
            {
                foot.position,
                new Vector3(face.x, foot.position.y, face.z),
                new Vector3(face.x, roof.point.y - 0.15f, face.z),
                top.position,
            };
        }
        if (bestPts == null) return false;
        var times = new[] { 0.25f, bestHeight / climbSpeed, 0.4f };
        if (!PlanLink(ScoutState.ClimbUp, bestPts, times, bestFace, Vector3.zero, false)) return false;
        streetCenter = transform.position;
        roofUntil = Time.time + Random.Range(roofTime.x, roofTime.y);
        return true;
    }

    // On a roof: walk to the roof edge that's best for getting to goal, and climb down there
    bool ClimbDownRoute(Vector3 goal)
    {
        var filter = Filter;
        var path = new NavMeshPath();
        Vector3 me = transform.position;
        Vector3 toGoal = goal - me;
        toGoal.y = 0f;
        toGoal = toGoal.sqrMagnitude > 0.01f ? toGoal.normalized : transform.forward;
        float best = float.MaxValue;
        Vector3[] bestPts = null;
        Vector3 bestFace = default;
        float bestHeight = 0f;
        foreach (float k in new[] { 1.5f, 3f, 6f, 10f, 15f })
        foreach (float a in new[] { 0f, 40f, -40f, 80f, -80f })
        {
            Vector3 probe = me + Quaternion.Euler(0f, a, 0f) * toGoal * k;
            if (!NavMesh.SamplePosition(probe, out var onRoof, 1.5f, filter) || Mathf.Abs(onRoof.position.y - me.y) > 0.5f) continue;
            if (!NavMesh.FindClosestEdge(onRoof.position, out var edge, filter)) continue;
            Vector3 outward = -edge.normal;
            outward.y = 0f;
            if (outward.sqrMagnitude < 0.01f) continue;
            outward.Normalize();
            // A real drop beyond the edge, with walkable ground at the bottom. The NavMesh stops short of the roof's
            // real edge (by the agent radius), so step outward until the roof falls away.
            Vector3 over = default;
            RaycastHit ground = default;
            float height = 0f;
            for (float step = 0.4f; step <= 2.0f; step += 0.3f)
            {
                over = edge.position + outward * step;
                if (!Physics.Raycast(over + Vector3.up * 0.3f, Vector3.down, out ground, maxClimbHeight + 1f, ClimbMask, QueryTriggerInteraction.Ignore)) { height = 0f; break; }
                height = edge.position.y - ground.point.y;
                if (height >= 1.5f) break;
            }
            if (height < 1.5f) continue;
            if (!NavMesh.SamplePosition(ground.point + outward * 0.3f, out var foot, 1f, filter) || Mathf.Abs(foot.position.y - ground.point.y) > 0.4f) continue;
            float walk = PathLength(me, edge.position, filter, path);
            if (walk < 0f) continue;
            float score = walk + Vector3.Distance(foot.position, goal);
            if (score >= best) continue;
            // The wall it climbs down (or just straight down past the edge if there's an overhang)
            Vector3 face = over;
            if (Physics.Raycast(over + Vector3.down * Mathf.Min(1.5f, height * 0.5f), -outward, out var wall, 1.5f, ClimbMask, QueryTriggerInteraction.Ignore))
                face = wall.point + outward * 0.2f;
            best = score;
            bestHeight = height;
            bestFace = -outward;   // backs down facing the wall, like down a ladder
            bestPts = new[]
            {
                edge.position,
                new Vector3(face.x, edge.position.y + 0.1f, face.z),
                new Vector3(face.x, foot.position.y + 0.2f, face.z),
                foot.position,
            };
        }
        if (bestPts == null) return false;
        var times = new[] { 0.4f, bestHeight / climbSpeed, 0.25f };
        return PlanLink(ScoutState.ClimbDown, bestPts, times, bestFace, goal, true);
    }

    NavMeshQueryFilter Filter => new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = NavMesh.AllAreas };

    // The cheapest usable window near the goal that joins where we are to where we're going (in or out)
    bool WindowRoute(Vector3 goal)
    {
        var filter = Filter;
        var near = new System.Collections.Generic.List<BreakableWindow>();
        foreach (var w in BreakableWindow.All)
            if (w != null && w.ScoutUsable && Vector3.Distance(w.Center, goal) < windowSearchRadius) near.Add(w);
        near.Sort((a, b) => Vector3.Distance(a.Center, goal).CompareTo(Vector3.Distance(b.Center, goal)));

        var path = new NavMeshPath();
        float best = float.MaxValue;
        BreakableWindow bestW = null;
        Vector3 bestFrom = default, bestTo = default;
        for (int i = 0; i < near.Count && i < 6; i++)
        {
            var w = near[i];
            if (!WindowSides(w, out var outside, out var inside)) continue;
            for (int dir = 0; dir < 2; dir++)
            {
                Vector3 a = dir == 0 ? outside : inside, b = dir == 0 ? inside : outside;
                float l1 = PathLength(transform.position, a, filter, path);
                if (l1 < 0f) continue;
                float l2 = PathLength(b, goal, filter, path);
                if (l2 < 0f || l1 + l2 >= best) continue;
                best = l1 + l2; bestW = w; bestFrom = a; bestTo = b;
            }
        }
        if (bestW == null) return false;
        var gap = bestW.ScoutGap;
        float hop = Mathf.Max(0.1f, windowHopTime);
        return PlanLink(ScoutState.Window, new[] { bestFrom, gap, bestTo }, new[] { hop * 0.5f, hop * 0.5f }, Vector3.zero, goal, true);
    }

    // Floor points just outside and just inside the window, each on its own side of the wall
    bool WindowSides(BreakableWindow w, out Vector3 outside, out Vector3 inside)
    {
        outside = inside = Vector3.zero;
        var filter = Filter;
        Vector3 gap = w.ScoutGap;
        Vector3 o = gap + w.Outward * 0.7f, i = gap - w.Outward * 0.8f;
        o.y = i.y = w.SillY;
        if (!NavMesh.SamplePosition(o, out var a, 1.5f, filter) || a.position.y > w.SillY - 0.2f) return false;
        if (!NavMesh.SamplePosition(i, out var b, 1.5f, filter) || b.position.y > w.SillY - 0.2f) return false;
        if (Vector3.Dot(a.position - w.Center, w.Outward) < 0.2f || Vector3.Dot(b.position - w.Center, w.Outward) > -0.2f) return false;
        outside = a.position;
        inside = b.position;
        return true;
    }

    static float PathLength(Vector3 from, Vector3 to, NavMeshQueryFilter filter, NavMeshPath path)
    {
        if (!NavMesh.CalculatePath(from, to, filter, path) || path.status != NavMeshPathStatus.PathComplete) return -1f;
        float len = 0f;
        for (int i = 1; i < path.corners.Length; i++) len += Vector3.Distance(path.corners[i - 1], path.corners[i]);
        return len;
    }

    // The scripted move: along the points leg by leg (eased), facing the wall on a climb. Then back on the NavMesh
    // and on to where it was going (or, after climbing up for a look, wandering the roof).
    void UpdateLink()
    {
        float e = Time.time - linkStart;
        int leg = 0;
        while (leg < linkTimes.Length - 1 && e > linkTimes[leg]) { e -= linkTimes[leg]; leg++; }
        float t = Mathf.Clamp01(e / Mathf.Max(0.05f, linkTimes[leg]));
        transform.position = Vector3.Lerp(linkPts[leg], linkPts[leg + 1], Mathf.SmoothStep(0f, 1f, t));
        Vector3 look = linkFace != Vector3.zero ? linkFace : linkPts[linkPts.Length - 1] - linkPts[0];
        look.y = 0f;
        if (look.sqrMagnitude > 0.01f) transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(look), 600f * Time.deltaTime);
        if (leg < linkTimes.Length - 1 || t < 1f) return;

        Vector3 end = linkPts[linkPts.Length - 1];
        agent.enabled = true;
        agent.Warp(end);
        var resume = IsLink(linkResume) || linkResume == ScoutState.Flee ? ScoutState.Wander : linkResume;
        SetState(resume);
        if (resume == ScoutState.Wander) areaCenter = end;
        if (linkHasDest) GoTo(linkDest);
    }

    bool Arrived() => !agent.pathPending && agent.remainingDistance <= Mathf.Max(0.4f, agent.stoppingDistance);

    void FaceTowards(Vector3 p)
    {
        Vector3 d = p - transform.position;
        d.y = 0f;
        if (d.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(d), 240f * Time.deltaTime);
    }

    FirstPersonController FindVisiblePlayer(out float bestDist)
    {
        Vector3 eye = transform.position + Vector3.up * eyeHeight;
        FirstPersonController best = null;
        bestDist = float.MaxValue;
        foreach (var p in FirstPersonController.All)
        {
            if (!p.IsSpawned || p.IsDead || p.IsGrabbed) continue;
            Vector3 chest = p.transform.position + Vector3.up * (p.IsCrouching ? 0.8f : 1.2f);
            Vector3 to = chest - eye;
            float dist = to.magnitude;
            var light = p.GetComponentInChildren<Flashlight>();
            float range = (light != null && light.IsOn ? flashlightSightRange : sightRange) * sightMul;
            if (p.IsCrouching) range *= crouchVisibility;
            if (dist > range || dist >= bestDist) continue;
            Vector3 flat = new Vector3(to.x, 0f, to.z);
            if (Vector3.Angle(transform.forward, flat) > viewAngle * 0.5f) continue;
            if (Physics.Raycast(eye, to / dist, out var hit, dist, FenceBuilder.AllButFences, QueryTriggerInteraction.Ignore) &&
                hit.collider.GetComponentInParent<FirstPersonController>() != p) continue;
            best = p;
            bestDist = dist;
        }
        return best;
    }

    FirstPersonController NearestPlayer(out float bestDist)
    {
        FirstPersonController best = null;
        bestDist = float.MaxValue;
        foreach (var p in FirstPersonController.All)
        {
            if (!p.IsSpawned || p.IsDead || p.IsGrabbed) continue;
            float d = Vector3.Distance(p.transform.position, transform.position);
            if (d < bestDist) { bestDist = d; best = p; }
        }
        return best;
    }

    // Is any player's flashlight beam on it right now (in range, near the middle of the beam, nothing in between)?
    bool InBeam()
    {
        Vector3 body = transform.position + Vector3.up * 0.2f;
        foreach (var p in FirstPersonController.All)
        {
            if (!p.IsSpawned || p.IsDead || p.IsGrabbed || p.cameraPivot == null) continue;
            var light = p.GetComponentInChildren<Flashlight>();
            if (light == null || !light.IsOn) continue;
            Vector3 from = p.cameraPivot.position;
            Vector3 to = body - from;
            float dist = to.magnitude;
            if (dist > beamRange || Vector3.Angle(p.cameraPivot.forward, to) > beamAngle) continue;
            if (Physics.Raycast(from, to / dist, out var hit, dist - 0.3f, FenceBuilder.AllButFences, QueryTriggerInteraction.Ignore) &&
                hit.collider.GetComponentInParent<FirstPersonController>() != p) continue;
            return true;
        }
        return false;
    }

    // ---------- Stomp ----------

    /// <summary>A player close enough to stomp it (flat distance, about the same height).</summary>
    public bool InStompRange(Vector3 playerPos)
    {
        Vector3 d = transform.position - playerPos;
        return Mathf.Abs(d.y) < 1.5f && new Vector2(d.x, d.z).magnitude <= stompRange;
    }

    [Rpc(SendTo.Server)]
    public void StompRpc(RpcParams rpc = default)
    {
        if (IsDead) return;
        FirstPersonController stomper = null;
        foreach (var p in FirstPersonController.All)
            if (p.OwnerClientId == rpc.Receive.SenderClientId && p.IsSpawned) stomper = p;
        // A bit of slack for lag; the client already checked the real range
        if (stomper == null || stomper.IsDead || Vector3.Distance(stomper.transform.position, transform.position) > stompRange + 1.2f) return;
        NoiseSystem.Emit(transform.position, stompNoise);
        Debug.Log($"Scout: stomped by player {rpc.Receive.SenderClientId} ({(state == ScoutState.Screech ? "mid-screech, relay stopped" : state.ToString())})");
        diedAt = Time.time;
        SetState(ScoutState.Dead);
        agent.enabled = false;
    }
}
