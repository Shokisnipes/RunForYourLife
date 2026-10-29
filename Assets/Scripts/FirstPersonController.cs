using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

// Networked first-person player. Lives on the Player prefab (spawned by the NetworkManager).
// Hierarchy:  Player (CharacterController + NetworkObject + OwnerNetworkTransform + this)
//               -> CameraPivot -> Main Camera, Flashlight
//               -> Model (character + PlayerAnimator, no collider) - what other players see
[RequireComponent(typeof(CharacterController))]
public class FirstPersonController : NetworkBehaviour
{
    [Header("References")]
    public Transform cameraPivot;
    public Renderer body;                // optional; hidden for your own player

    [Header("Movement")]
    public float walkSpeed = 3.5f;
    public float sprintSpeed = 6f;
    public float crouchSpeed = 1.75f;
    public float gravity = -20f;
    public float jumpHeight = 1f;

    [Header("Look")]
    public float mouseSensitivity = 0.1f;
    public float maxPitch = 85f;
    [Tooltip("Seconds the view takes to catch up with the mouse (0 = instant). Tiny values just take the edge off.")]
    public float lookSmoothing = 0.02f;

    [Header("Camera feel")]
    public float fieldOfView = 72f;
    public float sprintFovBoost = 6f;     // view widens a little when sprinting
    [Tooltip("Meters the head dips per step at walking speed.")]
    public float bobAmount = 0.02f;
    [Tooltip("Degrees the head rolls side to side per step.")]
    public float bobSway = 0.25f;
    [Tooltip("How much stronger the bob gets when sprinting (1 = same as walking).")]
    public float sprintBobBoost = 1.25f;
    public float bobStride = 0.75f;       // meters per step (matches the footstep sounds)

    [Header("Captured")]
    [Tooltip("Once the monster has hidden you, you die this many seconds later unless a teammate frees you. " +
             "Shown to everyone. Dead stays dead until the next run.")]
    public float hiddenSurviveSeconds = 300f;
    [Tooltip("Eye height (m) while you lie at the hiding spot.")]
    public float lyingEyeHeight = 0.3f;
    [Tooltip("Struggling (PlayerRescue: tapping the struggle key while hidden) runs your death timer at this speed " +
             "(0.5 = half as fast). Tapping faster doesn't beat it.")]
    [Range(0.1f, 1f)] public float struggleTimerSpeed = 0.5f;
    [Tooltip("Taps no further apart than this (s) count as struggling the whole time in between.")]
    public float struggleWindow = 0.6f;

    [Header("Death")]
    public float fadeOutTime = 0.4f;
    public float blackTime = 1.5f;
    public float fadeInTime = 1f;

    [Header("Crouch")]
    public float standHeight = 1.8f;
    public float crouchHeight = 1.0f;
    public float crouchLerpSpeed = 10f;

    // Every player in the scene registers here so the monster can find them
    public static readonly List<FirstPersonController> All = new List<FirstPersonController>();

    // Synced to everyone, written only by the player who owns this object
    readonly NetworkVariable<bool> netCrouching = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    readonly NetworkVariable<float> netPitch = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    readonly NetworkVariable<bool> netGrabbed = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    readonly NetworkVariable<bool> netDead = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // The monster holding us (NoCarrier when nobody is). Every computer pins the body to its own copy of that
    // monster's hand: our synced position arrives late and smoothed, so it wouldn't line up with the hand there.
    const ulong NoCarrier = ulong.MaxValue;
    readonly NetworkVariable<ulong> netCarrierId = new NetworkVariable<ulong>(
        NoCarrier, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // Hidden by the monster: the network time you die at (0 = not hidden). Written by the host, shown to everyone.
    readonly NetworkVariable<double> netHiddenUntil = new NetworkVariable<double>(0);
    bool dieSent;          // host: the death for this capture has been sent
    float lastStruggle = -100f;   // host: when this hidden player last struggled
    bool lyingHidden;      // owner: lying at the hiding spot, can only look around

    CharacterController controller;
    float pitch;
    float pitchTarget;     // where the mouse wants the head; pitch eases toward it
    float pendingYaw;      // mouse turn not applied to the body yet
    float verticalVelocity;
    bool isCrouching;
    readonly List<GameObject> hiddenSceneCameras = new List<GameObject>();
    bool grabbed;
    float fadeAlpha;
    Camera ownCamera;
    float bobPhase;
    float bobBlend;
    Coroutine falling;

    // The monster holding this player, looked up from netCarrierId
    ulong carrierId = NoCarrier;
    IMonsterGrabber carrier;
    // Someone else's player held by a monster (on our screen)
    bool shownCarrying;
    Vector3 shownCarryPos;

    // Read by other systems (footstep noise, monster)
    public float CurrentSpeed { get; private set; }
    // Set every frame by whatever slows you down (GeneratorJob: carrying a fuel can). 1 = normal speed.
    public float SpeedScale { get; set; } = 1f;
    public bool CanSprint { get; set; } = true;
    public bool IsGrabbed => netGrabbed.Value;   // synced, so the monster ignores whoever it's holding
    public bool IsCrouching => netCrouching.Value;
    public bool IsDead => netDead.Value;         // stays dead for the rest of the night (task 8 adds rescue)
    public float FovOffset { get; set; }         // added to the view's field of view (ChaseStress narrows it)
    public bool IsInMonsterHand => netCarrierId.Value != NoCarrier;
    public bool IsDropped => IsGrabbed && !IsInMonsterHand;   // the monster let go: the body lies on the floor
    public bool IsHidden => netHiddenUntil.Value > 0 && !IsDead;   // lying at a hiding spot, still alive
    public float HiddenSecondsLeft => IsHidden && IsSpawned ? Mathf.Max(0f, (float)(netHiddenUntil.Value - NetworkManager.ServerTime.Time)) : 0f;

    void Awake()
    {
        controller = GetComponent<CharacterController>();
        controller.height = standHeight;
        controller.center = new Vector3(0f, standHeight / 2f, 0f);
    }

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    public override void OnNetworkSpawn()
    {
        // Bodies never cast shadows, otherwise they block their own flashlight
        if (body != null) body.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        if (IsOwner)
        {
            MoveToSpawn();
            CityManager.Built += MoveToSpawn;   // joined before our copy of the city was built
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            if (body != null) body.enabled = false;   // don't see your own capsule

            ownCamera = cameraPivot.GetComponentInChildren<Camera>(true);
            if (ownCamera != null) ownCamera.fieldOfView = fieldOfView;

            // Turn off the lobby camera (any camera that isn't part of a player)
            foreach (var cam in Camera.allCameras)
            {
                if (cam.GetComponentInParent<FirstPersonController>() != null) continue;
                cam.gameObject.SetActive(false);
                hiddenSceneCameras.Add(cam.gameObject);
            }
        }
        else
        {
            // Someone else's player: we don't look through their camera or hear through their ears
            var cam = cameraPivot.GetComponentInChildren<Camera>(true);
            if (cam != null) cam.enabled = false;
            var listener = cameraPivot.GetComponentInChildren<AudioListener>(true);
            if (listener != null) listener.enabled = false;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner) return;
        CityManager.Built -= MoveToSpawn;
        foreach (var go in hiddenSceneCameras)
            if (go != null) go.SetActive(true);
        hiddenSceneCameras.Clear();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void Update()
    {
        if (!IsSpawned) return;

        // Host: a hidden player's time ran out
        if (IsServer && IsHidden && !dieSent && NetworkManager.ServerTime.Time >= netHiddenUntil.Value)
        {
            dieSent = true;
            Debug.Log($"Player {OwnerClientId} died at the hiding spot");
            DieRpc();
        }

        if (!IsOwner)
        {
            // Show other players' head angle and crouch
            cameraPivot.localRotation = Quaternion.Euler(netPitch.Value, 0f, 0f);
            UpdateCrouchHeight(netCrouching.Value);
            return;
        }

        var kb = Keyboard.current;
        var mouse = Mouse.current;

        // Lying at the hiding spot: you can only look around
        if (lyingHidden && !netDead.Value && mouse != null)
        {
            cameraPivot.localPosition = new Vector3(0f, lyingEyeHeight, 0f);
            Look(mouse);
            return;
        }

        if (grabbed || netDead.Value) return;   // no control while it's holding you, or once you're dead
        if (kb == null || mouse == null) return;

        Look(mouse);
        Crouch(kb);
        Move(kb);
        bool sprinting = !NetworkUI.MenuOpen && kb.leftShiftKey.isPressed && kb.wKey.isPressed && !isCrouching;
        UpdateCameraFeel(sprinting);

        // Esc opens the pause menu (NetworkUI). If the cursor got freed some other way
        // (e.g. alt-tab), clicking back into the game re-locks it.
        if (!NetworkUI.MenuOpen && mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    // Head bob, sway and a slight FOV kick when sprinting
    void UpdateCameraFeel(bool sprinting)
    {
        float dt = Time.deltaTime;
        bool moving = controller.isGrounded && CurrentSpeed > 0.3f;

        bobBlend = Mathf.MoveTowards(bobBlend, moving ? Mathf.Clamp(CurrentSpeed / walkSpeed, 0f, sprintBobBoost) : 0f, dt * 4f);
        if (moving) bobPhase += CurrentSpeed * dt * Mathf.PI / bobStride;   // half a cycle per step

        float damp = isCrouching ? 0.5f : 1f;
        float amount = bobAmount * bobBlend * damp;
        cameraPivot.localPosition += new Vector3(Mathf.Cos(bobPhase) * amount * 0.4f, -Mathf.Abs(Mathf.Sin(bobPhase)) * amount, 0f);

        float roll = Mathf.Cos(bobPhase) * bobSway * bobBlend * damp;
        cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, roll);

        if (ownCamera != null)
        {
            float target = fieldOfView + FovOffset + (sprinting && CurrentSpeed > walkSpeed ? sprintFovBoost : 0f);
            ownCamera.fieldOfView = Mathf.Lerp(ownCamera.fieldOfView, target, 1f - Mathf.Exp(-6f * dt));
        }
    }

    void Look(Mouse mouse)
    {
        if (NetworkUI.MenuOpen || Cursor.lockState != CursorLockMode.Locked) { pendingYaw = 0f; pitchTarget = pitch; return; }

        // The mouse sets a target; the view eases toward it over a few milliseconds (a little weight, no drag)
        Vector2 delta = mouse.delta.ReadValue() * mouseSensitivity;
        pendingYaw += delta.x;
        pitchTarget = Mathf.Clamp(pitchTarget - delta.y, -maxPitch, maxPitch);
        float k = lookSmoothing > 0f ? 1f - Mathf.Exp(-Time.deltaTime / lookSmoothing) : 1f;

        float yawStep = pendingYaw * k;
        pendingYaw -= yawStep;
        transform.Rotate(0f, yawStep, 0f);              // body turns left/right
        pitch = Mathf.Lerp(pitch, pitchTarget, k);
        cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f); // head looks up/down

        if (Mathf.Abs(netPitch.Value - pitch) > 0.5f) netPitch.Value = pitch;
    }

    void Move(Keyboard kb)
    {
        bool hasInput = !NetworkUI.MenuOpen;   // pause menu open: stand still (gravity still applies)
        Vector2 input = Vector2.zero;
        if (hasInput)
        {
            if (kb.wKey.isPressed) input.y += 1;
            if (kb.sKey.isPressed) input.y -= 1;
            if (kb.dKey.isPressed) input.x += 1;
            if (kb.aKey.isPressed) input.x -= 1;
        }

        Vector3 move = transform.right * input.x + transform.forward * input.y;
        if (move.sqrMagnitude > 1f) move.Normalize();

        float speed = walkSpeed;
        if (isCrouching) speed = crouchSpeed;
        else if (kb.leftShiftKey.isPressed && input.y > 0 && CanSprint) speed = sprintSpeed;
        speed *= SpeedScale;

        if (controller.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f; // keeps us snapped to the ground

        if (hasInput && controller.isGrounded && !isCrouching && kb.spaceKey.wasPressedThisFrame)
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);

        verticalVelocity += gravity * Time.deltaTime;

        controller.Move((move * speed + Vector3.up * verticalVelocity) * Time.deltaTime);

        Vector3 v = controller.velocity;
        CurrentSpeed = new Vector3(v.x, 0f, v.z).magnitude;
    }

    void Crouch(Keyboard kb)
    {
        bool wantCrouch = kb.leftCtrlKey.isPressed && !NetworkUI.MenuOpen;

        if (wantCrouch)
        {
            isCrouching = true;
        }
        else if (isCrouching)
        {
            // Only stand up if nothing is above our head
            Vector3 origin = transform.position + Vector3.up * (crouchHeight - 0.05f);
            float checkDist = standHeight - crouchHeight + 0.1f;
            bool blocked = Physics.Raycast(origin, Vector3.up, checkDist, ~0, QueryTriggerInteraction.Ignore);
            if (!blocked) isCrouching = false;
        }

        if (netCrouching.Value != isCrouching) netCrouching.Value = isCrouching;
        UpdateCrouchHeight(isCrouching);
    }

    void UpdateCrouchHeight(bool crouched)
    {
        float targetHeight = crouched ? crouchHeight : standHeight;
        controller.height = Mathf.Lerp(controller.height, targetHeight, Time.deltaTime * crouchLerpSpeed);
        controller.center = new Vector3(0f, controller.height / 2f, 0f);
        cameraPivot.localPosition = new Vector3(0f, controller.height - 0.1f, 0f);

        if (body != null)
        {
            // Squash the body capsule to match (a Unity capsule is 2 m tall at scale 1)
            body.transform.localScale = new Vector3(1f, controller.height / 2f, 1f);
            body.transform.localPosition = new Vector3(0f, controller.height / 2f, 0f);
        }
    }

    // ---------- Spawning / getting caught ----------

    void MoveToSpawn()
    {
        var spawn = GameObject.Find("PlayerSpawn");
        Vector3 basePos = spawn != null ? spawn.transform.position : Vector3.zero;
        Quaternion baseRot = spawn != null ? spawn.transform.rotation : Quaternion.identity;

        // Spread players out so they don't spawn inside each other
        Vector3 offset = baseRot * Vector3.right * ((int)(OwnerClientId % 4) * 1.5f);

        controller.enabled = false;              // CharacterController blocks teleports while enabled
        transform.SetPositionAndRotation(basePos + offset, baseRot);
        controller.enabled = true;
        verticalVelocity = 0f;
    }

    // Called by the monster on the host. You hang from its hand until it calls Hide.
    public void Caught(NetworkObject monster) => CaughtRpc(monster);

    [Rpc(SendTo.Owner)]
    void CaughtRpc(NetworkObjectReference monsterRef)
    {
        if (grabbed) return;
        IMonsterGrabber monster = null;   // the giant's MonsterAnimator or the climber's ClimberAnimator
        if (monsterRef.TryGet(out NetworkObject obj)) monster = obj.GetComponentInChildren<IMonsterGrabber>();
        letGoAt = null;
        StartCoroutine(GrabbedSequence(monster, monster != null ? obj.NetworkObjectId : NoCarrier));
    }

    // Called by the monster on the host when it lets go of you at a hiding spot. At night the death timer starts
    // (everyone sees it); outside the night (lobby, testing) you just respawn as before.
    public void Hide(Vector3 spot)
    {
        if (!IsServer) return;
        var flow = GameFlow.Instance;
        bool night = flow == null || !flow.IsSpawned || flow.Phase == GamePhase.Night;
        if (night)
        {
            netHiddenUntil.Value = NetworkManager.ServerTime.Time + hiddenSurviveSeconds;
            dieSent = false;
        }
        LetGoRpc(spot, night);
    }

    Vector3? letGoAt;      // owner: where the monster let go of us
    bool letGoHidden;

    [Rpc(SendTo.Owner)]
    void LetGoRpc(Vector3 spot, bool hidden)
    {
        letGoAt = spot;
        letGoHidden = hidden;
    }

    // Host: the hidden player struggled. The time since their last tap (up to struggleWindow) only counted at
    // struggleTimerSpeed, so keeping it up slows the whole countdown by that much.
    public void Struggle()
    {
        if (!IsServer || !IsHidden) return;
        float gap = Mathf.Min(Time.time - lastStruggle, struggleWindow);
        lastStruggle = Time.time;
        netHiddenUntil.Value += gap * (1f - struggleTimerSpeed);
    }

    // Host: a teammate freed this hidden player (PlayerRescue checked they were next to them)
    public void Free()
    {
        if (!IsServer || !IsHidden) return;
        netHiddenUntil.Value = 0;
        dieSent = false;
        FreedRpc();
    }

    // Owner: get up where you lay and carry on
    [Rpc(SendTo.Owner)]
    void FreedRpc()
    {
        if (!lyingHidden || netDead.Value) return;
        if (falling != null) { StopCoroutine(falling); falling = null; }
        lyingHidden = false;
        letGoAt = null;
        pitch = pitchTarget = 0f;
        pendingYaw = 0f;
        cameraPivot.localRotation = Quaternion.identity;
        netPitch.Value = 0f;
        verticalVelocity = 0f;
        controller.enabled = true;
        grabbed = false;
        netGrabbed.Value = false;   // every computer: the ragdoll stands back up (PlayerRagdoll)
        Debug.Log("You were freed!");
    }

    // Host: the hiding spot timer ran out
    [Rpc(SendTo.Owner)]
    void DieRpc() => StartCoroutine(DieSequence());

    IEnumerator DieSequence()
    {
        yield return Fade(fadeAlpha, 1f, fadeOutTime);
        yield return new WaitForSeconds(blackTime);
        netDead.Value = true;   // the screen stays black; the run ends when nobody is left
        lyingHidden = false;
    }

    IEnumerator GrabbedSequence(IMonsterGrabber monster, ulong monsterId)
    {
        Debug.Log("You were caught!");
        grabbed = true;
        netGrabbed.Value = true;
        netCarrierId.Value = monsterId;
        CurrentSpeed = 0f;
        isCrouching = false;
        if (netCrouching.Value) netCrouching.Value = false;
        controller.enabled = false;              // we move ourselves with the hand now

        // Dangling in its hand, forced to look at its face, until it lets go
        while (letGoAt == null)
        {
            if (monster != null)
            {
                transform.position = Vector3.Lerp(transform.position, CarryPoint(monster), 1f - Mathf.Exp(-14f * Time.deltaTime));

                Vector3 toFace = monster.HeadPosition - cameraPivot.position;
                Vector3 flat = new Vector3(toFace.x, 0f, toFace.z);
                if (flat.sqrMagnitude > 0.01f)
                    transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(flat), 1f - Mathf.Exp(-5f * Time.deltaTime));

                float targetPitch = -Mathf.Atan2(toFace.y, flat.magnitude) * Mathf.Rad2Deg;
                pitch = Mathf.Lerp(pitch, Mathf.Clamp(targetPitch, -maxPitch, maxPitch), 1f - Mathf.Exp(-5f * Time.deltaTime));
                cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
                if (Mathf.Abs(netPitch.Value - pitch) > 0.5f) netPitch.Value = pitch;
            }
            yield return null;
        }

        // It lets go: the body drops to the floor (the ragdoll lies there, see PlayerRagdoll)
        netCarrierId.Value = NoCarrier;
        Vector3 spot = letGoAt.Value;
        transform.position = new Vector3(spot.x, Mathf.Max(spot.y, transform.position.y), spot.z);
        falling = StartCoroutine(FallToFloor());

        // Hidden: lie there alive, looking around, until a teammate frees you or the timer runs out (DieRpc)
        if (letGoHidden)
        {
            lyingHidden = true;
            yield break;
        }

        // Not during a night: black out and respawn
        yield return Fade(0f, 1f, fadeOutTime);
        yield return new WaitForSeconds(blackTime);

        pitch = pitchTarget = 0f;
        pendingYaw = 0f;
        cameraPivot.localRotation = Quaternion.identity;
        netPitch.Value = 0f;
        if (falling != null) StopCoroutine(falling);
        MoveToSpawn();
        grabbed = false;
        netGrabbed.Value = false;

        yield return Fade(1f, 0f, fadeInTime);
    }

    // Where the body hangs below the monster's hand: dragged along the floor, never through it
    Vector3 CarryPoint(IMonsterGrabber monster)
    {
        Vector3 hand = monster.GripPosition;
        Vector3 hold = hand - Vector3.up * (standHeight * 0.6f);
        if (FloorBelow(hand + Vector3.up * 0.5f, 4f, out float floorY)) hold.y = Mathf.Max(hold.y, floorY);
        return hold;
    }

    // Nearest floor straight down: not a monster, a player or a ragdolled body
    static bool FloorBelow(Vector3 from, float range, out float floorY)
    {
        floorY = from.y;
        float nearest = float.MaxValue;
        foreach (var hit in Physics.RaycastAll(from, Vector3.down, range, ~0, QueryTriggerInteraction.Ignore))
        {
            if (hit.distance >= nearest || hit.collider.GetComponentInParent<NetworkObject>() != null ||
                PlayerRagdoll.IsBodyPart(hit.collider)) continue;
            nearest = hit.distance;
            floorY = hit.point.y;
        }
        return nearest < float.MaxValue;
    }

    // Dropped from the monster's hand: fall straight down onto whatever is below
    // (the ragdoll falls on its own; this keeps the player object, and so the synced position, with it)
    IEnumerator FallToFloor()
    {
        FloorBelow(transform.position + Vector3.up * 0.5f, 80f, out float floorY);
        float speed = 0f;
        while (transform.position.y > floorY)
        {
            speed -= gravity * Time.deltaTime;   // gravity is negative
            Vector3 p = transform.position;
            p.y = Mathf.Max(floorY, p.y - speed * Time.deltaTime);
            transform.position = p;
            yield return null;
        }
        falling = null;
    }

    // The monster holding this player, on this computer (null when nobody is)
    public IMonsterGrabber Carrier
    {
        get
        {
            ulong id = netCarrierId.Value;
            if (id != carrierId)
            {
                carrierId = id;
                carrier = null;
                if (id != NoCarrier && IsSpawned && NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(id, out var obj))
                    carrier = obj.GetComponentInChildren<IMonsterGrabber>();
            }
            return (carrier as Object) != null ? carrier : null;
        }
    }

    // Someone else held by a monster: pin them to the hand of our own copy of that monster
    // (runs after NetworkTransform has placed them from the network, so this wins)
    void LateUpdate()
    {
        if (!IsSpawned || IsOwner) return;

        var monster = Carrier;
        if (monster == null) { shownCarrying = false; return; }
        if (!shownCarrying) shownCarryPos = transform.position;   // ease in from where they were
        shownCarrying = true;

        shownCarryPos = Vector3.Lerp(shownCarryPos, CarryPoint(monster), 1f - Mathf.Exp(-14f * Time.deltaTime));
        transform.position = shownCarryPos;
    }

    IEnumerator Fade(float from, float to, float duration)
    {
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            fadeAlpha = Mathf.Lerp(from, to, t / duration);
            yield return null;
        }
        fadeAlpha = to;
    }

    // Black screen for fades (only drawn on your own screen)
    void OnGUI()
    {
        if (!IsOwner || fadeAlpha <= 0f) return;
        GUI.depth = -1000;
        GUI.color = new Color(0f, 0f, 0f, fadeAlpha);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }
}
