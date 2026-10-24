using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Put this on the Player object (pivot at the player's feet).
// Hierarchy:  Player (CharacterController + this) -> CameraPivot -> Main Camera
[RequireComponent(typeof(CharacterController))]
public class FirstPersonController : MonoBehaviour
{
    [Header("References")]
    public Transform cameraPivot;

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

    // Every player in the scene registers here so the monster can find them (co-op ready)
    public static readonly List<FirstPersonController> All = new List<FirstPersonController>();

    CharacterController controller;
    float pitch;
    float verticalVelocity;
    bool isCrouching;

    // Read by other systems later (footstep noise, monster hearing)
    public float CurrentSpeed { get; private set; }
    public bool IsCrouching => isCrouching;

    void Awake()
    {
        controller = GetComponent<CharacterController>();
        controller.height = standHeight;
        controller.center = new Vector3(0f, standHeight / 2f, 0f);
    }

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
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

        float targetHeight = isCrouching ? crouchHeight : standHeight;
        controller.height = Mathf.Lerp(controller.height, targetHeight, Time.deltaTime * crouchLerpSpeed);
        controller.center = new Vector3(0f, controller.height / 2f, 0f);
        cameraPivot.localPosition = new Vector3(0f, controller.height - 0.1f, 0f);
    }
}
