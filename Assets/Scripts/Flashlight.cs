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

    [Header("Beam")]
    [Tooltip("Gives the beam a realistic hotspot, ring and uneven edge. Ignored if the Light already has a Cookie.")]
    public bool proceduralCookie = true;

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
        if (proceduralCookie && spot.cookie == null) spot.cookie = MakeCookie(256);
    }

    // A real flashlight beam: bright hotspot, a faint ring, uneven blotches, soft falloff
    static Texture2D MakeCookie(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "FlashlightCookie",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };
        var px = new Color32[size * size];
        float half = (size - 1) / 2f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x - half) / half, dy = (y - half) / half;
                float r = Mathf.Sqrt(dx * dx + dy * dy);

                float hotspot = Mathf.Exp(-r * r * 14f) * 0.55f;
                float body = (1f - Mathf.SmoothStep(0.45f, 0.95f, r)) * 0.5f;
                float ring = Mathf.Exp(-Mathf.Pow((r - 0.62f) / 0.05f, 2f)) * 0.12f;
                float blotch = (Mathf.PerlinNoise(x * 0.04f, y * 0.04f) - 0.5f) * 0.12f;

                float v = Mathf.Clamp01(hotspot + body + ring + blotch * body * 2f);
                if (r >= 0.98f) v = 0f;
                byte b = (byte)(v * 255f);
                px[y * size + x] = new Color32(b, b, b, b);
            }
        }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return tex;
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
