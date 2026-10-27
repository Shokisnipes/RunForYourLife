using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

// Networked first-person player. Lives on the Player prefab (spawned by the NetworkManager).
// Hierarchy:  Player (CharacterController + NetworkObject + OwnerNetworkTransform + this)
//               -> CameraPivot -> Main Camera, Flashlight
//               -> Body (capsule mesh, no collider) - what other players see
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

    [Header("Camera feel")]
    public float fieldOfView = 72f;
    public float sprintFovBoost = 6f;     // view widens a little when sprinting
    public float bobAmount = 0.045f;      // meters the head dips per step
    public float bobSway = 0.7f;          // degrees the head rolls side to side
    public float bobStride = 0.75f;       // meters per step (matches the footstep sounds)

    [Header("Death")]
    public float carriedTime = 3f;       // dangling in its hand before you die
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

    CharacterController controller;
    float pitch;
    float verticalVelocity;
    bool isCrouching;
    readonly List<GameObject> hiddenSceneCameras = new List<GameObject>();
    bool grabbed;
    float fadeAlpha;
    Camera ownCamera;
    float bobPhase;
    float bobBlend;

    // Read by other systems (footstep noise, monster)
    public float CurrentSpeed { get; private set; }
    public bool IsGrabbed => netGrabbed.Value;   // synced, so the monster ignores whoever it's holding
    public bool IsCrouching => netCrouching.Value;
    public bool IsDead => netDead.Value;         // stays dead for the rest of the night (task 8 adds rescue)

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
        foreach (var go in hiddenSceneCameras)
            if (go != null) go.SetActive(true);
        hiddenSceneCameras.Clear();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void Update()
    {
        if (!IsSpawned) return;

        if (!IsOwner)
        {
            // Show other players' head angle and crouch
            cameraPivot.localRotation = Quaternion.Euler(netPitch.Value, 0f, 0f);
            UpdateCrouchHeight(netCrouching.Value);
            return;
        }

        if (grabbed || netDead.Value) return;   // no control while it's holding you, or once you're dead

        var kb = Keyboard.current;
        var mouse = Mouse.current;
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

        bobBlend = Mathf.MoveTowards(bobBlend, moving ? Mathf.Clamp(CurrentSpeed / walkSpeed, 0f, 1.6f) : 0f, dt * 4f);
        if (moving) bobPhase += CurrentSpeed * dt * Mathf.PI / bobStride;   // half a cycle per step

        float damp = isCrouching ? 0.5f : 1f;
        float amount = bobAmount * bobBlend * damp;
        cameraPivot.localPosition += new Vector3(Mathf.Cos(bobPhase) * amount * 0.6f, -Mathf.Abs(Mathf.Sin(bobPhase)) * amount, 0f);

        float roll = Mathf.Cos(bobPhase) * bobSway * bobBlend * damp;
        cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, roll);

        if (ownCamera != null)
        {
            float target = fieldOfView + (sprinting && CurrentSpeed > walkSpeed ? sprintFovBoost : 0f);
            ownCamera.fieldOfView = Mathf.Lerp(ownCamera.fieldOfView, target, 1f - Mathf.Exp(-6f * dt));
        }
    }

    void Look(Mouse mouse)
    {
        if (NetworkUI.MenuOpen || Cursor.lockState != CursorLockMode.Locked) return;

        Vector2 delta = mouse.delta.ReadValue() * mouseSensitivity;
        transform.Rotate(0f, delta.x, 0f);              // body turns left/right
        pitch = Mathf.Clamp(pitch - delta.y, -maxPitch, maxPitch);
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
        else if (kb.leftShiftKey.isPressed && input.y > 0) speed = sprintSpeed;

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

    // Called by the monster on the host
    public void Caught(NetworkObject monster, float extraTime = 0f) => CaughtRpc(monster, extraTime);

    [Rpc(SendTo.Owner)]
    void CaughtRpc(NetworkObjectReference monsterRef, float extraTime)
    {
        if (grabbed) return;
        IMonsterGrabber monster = null;   // the giant's MonsterAnimator or the climber's ClimberAnimator
        if (monsterRef.TryGet(out NetworkObject obj)) monster = obj.GetComponentInChildren<IMonsterGrabber>();
        StartCoroutine(GrabbedSequence(monster, carriedTime + extraTime));
    }

    IEnumerator GrabbedSequence(IMonsterGrabber monster, float holdTime)
    {
        Debug.Log("You were caught!");
        grabbed = true;
        netGrabbed.Value = true;
        CurrentSpeed = 0f;
        isCrouching = false;
        if (netCrouching.Value) netCrouching.Value = false;
        controller.enabled = false;              // we move ourselves with the hand now

        // Dangling in its hand, forced to look at its face
        float start = Time.time;
        while (Time.time - start < holdTime)
        {
            if (monster != null)
            {
                Vector3 hand = monster.HandPosition;
                Vector3 hold = hand - Vector3.up * (standHeight * 0.6f);
                // Dragged along the floor, never through it
                if (Physics.Raycast(hand + Vector3.up * 0.5f, Vector3.down, out var floor, 4f, ~0, QueryTriggerInteraction.Ignore))
                    hold.y = Mathf.Max(hold.y, floor.point.y);
                transform.position = Vector3.Lerp(transform.position, hold, 1f - Mathf.Exp(-14f * Time.deltaTime));

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

        // Death
        yield return Fade(0f, 1f, fadeOutTime);
        yield return new WaitForSeconds(blackTime);

        // During the night you stay dead (screen stays black); the run ends when everyone is
        var flow = GameFlow.Instance;
        if (flow != null && flow.IsSpawned && flow.Phase == GamePhase.Night)
        {
            netDead.Value = true;
            yield break;
        }

        pitch = 0f;
        cameraPivot.localRotation = Quaternion.identity;
        netPitch.Value = 0f;
        MoveToSpawn();
        grabbed = false;
        netGrabbed.Value = false;

        yield return Fade(1f, 0f, fadeInTime);
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
