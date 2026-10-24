using UnityEngine;
using UnityEngine.InputSystem;

// Put this on a Spot Light that is a child of CameraPivot.
// F toggles it. The beam lags slightly behind your view so it feels hand-held.
[RequireComponent(typeof(Light))]
public class Flashlight : MonoBehaviour
{
    [Header("References")]
    public Transform followTarget;   // drag CameraPivot here

    [Header("Feel")]
    public float followSpeed = 12f;  // lower = more lag/sway
    public bool startOn = false;

    [Header("Audio (optional)")]
    public AudioSource clickSound;

    Light spot;

    // Read by the monster later: a lit flashlight makes you easier to spot
    public bool IsOn => spot.enabled;

    void Awake()
    {
        spot = GetComponent<Light>();
        spot.enabled = startOn;
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.fKey.wasPressedThisFrame)
        {
            spot.enabled = !spot.enabled;
            if (clickSound != null) clickSound.Play();
        }
    }

    void LateUpdate()
    {
        if (followTarget == null) return;
        transform.rotation = Quaternion.Slerp(
            transform.rotation, followTarget.rotation, Time.deltaTime * followSpeed);
    }
}
