using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

// Put this on a Spot Light that is a child of CameraPivot (inside the Player prefab).
// F toggles it. On/off is synced so everyone sees your beam.
[RequireComponent(typeof(Light))]
public class Flashlight : NetworkBehaviour
{
    [Header("References")]
    public Transform followTarget;   // drag CameraPivot here

    [Header("Feel")]
    public float followSpeed = 12f;  // lower = more lag/sway
    public bool startOn = false;

    [Header("Audio (optional)")]
    public AudioSource clickSound;

    readonly NetworkVariable<bool> isOn = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    Light spot;

    // Read by the monster: a lit flashlight makes you easier to spot
    public bool IsOn => isOn.Value;

    void Awake()
    {
        spot = GetComponent<Light>();
        spot.enabled = false;
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner) isOn.Value = startOn;
        spot.enabled = isOn.Value;
        isOn.OnValueChanged += OnToggled;
    }

    public override void OnNetworkDespawn()
    {
        isOn.OnValueChanged -= OnToggled;
    }

    void OnToggled(bool wasOn, bool nowOn)
    {
        spot.enabled = nowOn;
        if (clickSound != null) clickSound.Play();
    }

    void Update()
    {
        if (!IsOwner) return;
        var kb = Keyboard.current;
        if (kb != null && kb.fKey.wasPressedThisFrame)
            isOn.Value = !isOn.Value;
    }

    void LateUpdate()
    {
        if (followTarget == null) return;
        transform.rotation = Quaternion.Slerp(
            transform.rotation, followTarget.rotation, Time.deltaTime * followSpeed);
    }
}
