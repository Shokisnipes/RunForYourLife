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

    CharacterController controller;
    float pitch;
    float verticalVelocity;
    bool isCrouching;
    readonly List<GameObject> hiddenSceneCameras = new List<GameObject>();

    // Read by other systems (footstep noise, monster)
    public float CurrentSpeed { get; private set; }
    public bool IsCrouching => netCrouching.Value;

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

        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (kb == null || mouse == null) return;

        Look(mouse);
        Crouch(kb);
        Move(kb);

        // Free the cursor with Esc, click back into the Game view to re-lock
        if (kb.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        if (mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    void Look(Mouse mouse)
    {
        if (Cursor.lockState != CursorLockMode.Locked) return;

        Vector2 delta = mouse.delta.ReadValue() * mouseSensitivity;
        transform.Rotate(0f, delta.x, 0f);              // body turns left/right
        pitch = Mathf.Clamp(pitch - delta.y, -maxPitch, maxPitch);
        cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f); // head looks up/down

        if (Mathf.Abs(netPitch.Value - pitch) > 0.5f) netPitch.Value = pitch;
    }

    void Move(Keyboard kb)
    {
        Vector2 input = Vector2.zero;
        if (kb.wKey.isPressed) input.y += 1;
        if (kb.sKey.isPressed) input.y -= 1;
        if (kb.dKey.isPressed) input.x += 1;
        if (kb.aKey.isPressed) input.x -= 1;

        Vector3 move = transform.right * input.x + transform.forward * input.y;
        if (move.sqrMagnitude > 1f) move.Normalize();

        float speed = walkSpeed;
        if (isCrouching) speed = crouchSpeed;
        else if (kb.leftShiftKey.isPressed && input.y > 0) speed = sprintSpeed;

        if (controller.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f; // keeps us snapped to the ground

        if (controller.isGrounded && !isCrouching && kb.spaceKey.wasPressedThisFrame)
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);

        verticalVelocity += gravity * Time.deltaTime;

        controller.Move((move * speed + Vector3.up * verticalVelocity) * Time.deltaTime);

        Vector3 v = controller.velocity;
        CurrentSpeed = new Vector3(v.x, 0f, v.z).magnitude;
    }

    void Crouch(Keyboard kb)
    {
        bool wantCrouch = kb.leftCtrlKey.isPressed;

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
    public void Caught() => CaughtRpc();

    [Rpc(SendTo.Owner)]
    void CaughtRpc()
    {
        Debug.Log("You were caught!");
        MoveToSpawn();
    }
}
