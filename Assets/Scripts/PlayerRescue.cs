using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

// Captured players: the trail to them and getting them out. Lives on the Player prefab, runs on every computer.
// - Blood trail: while a monster carries this player off, every computer drips blood on the ground below them, and
//   leaves a pool where they're put down. Follow it from the catch to the hiding spot (their voice is the other clue).
// - Struggling (your own player, while hidden): tap Space to slow your death timer. Every tap jolts your body on
//   everyone's screen and makes noise the monster can hear.
// - Freeing (your own player): stand next to a hidden teammate and hold E. It takes a few seconds and makes noise
//   the monster can hear. The host checks you're really there, then they get up and their death timer stops.
public class PlayerRescue : NetworkBehaviour
{
    [Header("Freeing a hidden teammate")]
    [Tooltip("How close (m) you have to be to a hidden teammate to free them.")]
    public float freeRange = 2.5f;
    [Tooltip("Seconds of holding E to free them.")]
    public float freeHoldTime = 4f;
    public Key freeKey = Key.E;
    [Tooltip("Freeing someone is noisy: a noise this big (m)...")]
    public float freeNoiseRadius = 12f;
    [Tooltip("...every this many seconds while you hold E.")]
    public float freeNoiseInterval = 1f;

    [Header("Struggling (while hidden)")]
    public Key struggleKey = Key.Space;
    [Tooltip("Struggling makes a noise this big (m)...")]
    public float struggleNoiseRadius = 10f;
    [Tooltip("...at most this often (s).")]
    public float struggleNoiseInterval = 1f;
    [Tooltip("How hard each tap jolts the limp body (speed in m/s given to the arms and legs).")]
    public float wiggleStrength = 1.6f;
    [Tooltip("Small camera jolt you feel on each tap.")]
    public float struggleShake = 0.25f;

    [Header("Blood trail (while carried off)")]
    [Tooltip("A drip every this many meters the body travels.")]
    public float dripSpacing = 2.5f;
    [Tooltip("Size range (m) of a drip.")]
    public Vector2 dripSize = new Vector2(0.25f, 0.55f);
    [Tooltip("Size (m) of the pool where the body is put down.")]
    public float poolSize = 1.4f;
    [Tooltip("Most drips one capture leaves (keeps a long carry cheap).")]
    public int maxDrips = 80;

    FirstPersonController player;
    PlayerRagdoll ragdoll;
    float nextStruggleNoise, lastWiggle;   // host

    // Freeing (owner)
    FirstPersonController freeing;
    float freeProgress, nextFreeNoise;
    GUIStyle hintStyle;

    // Trail (every computer)
    bool wasCarried, wasHidden;
    Vector3 lastDrip;
    int drips;
    Transform trailRoot;

    void Awake()
    {
        player = GetComponent<FirstPersonController>();
        ragdoll = GetComponentInChildren<PlayerRagdoll>(true);
    }

    void Update()
    {
        if (!IsSpawned || player == null) return;
        UpdateTrail();
        if (!IsOwner) return;
        UpdateFreeing();
        UpdateStruggle();
    }

    // ---------- Struggling ----------

    void UpdateStruggle()
    {
        if (!player.IsHidden || NetworkUI.MenuOpen) return;
        var kb = Keyboard.current;
        if (kb == null || !kb[struggleKey].wasPressedThisFrame) return;
        CameraShake.ShakeFrom(transform.position, struggleShake, 1f);
        StruggleRpc();
    }

    // Host: slows the timer (FirstPersonController.Struggle), makes noise now and then, jolts the body for everyone
    [Rpc(SendTo.Server)]
    void StruggleRpc()
    {
        if (!player.IsHidden) return;
        player.Struggle();
        if (Time.time >= nextStruggleNoise)
        {
            nextStruggleNoise = Time.time + struggleNoiseInterval;
            NoiseSystem.Emit(transform.position, struggleNoiseRadius);
        }
        if (Time.time - lastWiggle >= 0.12f)   // a mashing player doesn't flood the network
        {
            lastWiggle = Time.time;
            WiggleRpc();
        }
    }

    [Rpc(SendTo.Everyone)]
    void WiggleRpc()
    {
        if (ragdoll != null) ragdoll.Wiggle(wiggleStrength);
    }

    // ---------- Freeing ----------

    void UpdateFreeing()
    {
        var target = player.IsDead || player.IsGrabbed || NetworkUI.MenuOpen ? null : NearestHidden();
        if (target != freeing) { freeing = target; freeProgress = 0f; }
        if (freeing == null) return;

        var kb = Keyboard.current;
        if (kb == null || !kb[freeKey].isPressed) { freeProgress = 0f; return; }

        if (freeProgress == 0f) nextFreeNoise = 0f;
        freeProgress += Time.deltaTime / Mathf.Max(0.1f, freeHoldTime);
        if (Time.time >= nextFreeNoise)
        {
            nextFreeNoise = Time.time + freeNoiseInterval;
            NoiseRpc(transform.position, freeNoiseRadius);
        }
        if (freeProgress >= 1f)
        {
            FreeRpc(freeing.NetworkObject);
            freeProgress = 0f;
            freeing = null;
        }
    }

    FirstPersonController NearestHidden()
    {
        FirstPersonController best = null;
        float bestD = freeRange;
        foreach (var p in FirstPersonController.All)
        {
            if (p == player || !p.IsSpawned || !p.IsHidden) continue;
            float d = FlatDistance(p.transform.position, transform.position);
            if (d <= bestD) { bestD = d; best = p; }
        }
        return best;
    }

    static float FlatDistance(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

    [Rpc(SendTo.Server)]
    void NoiseRpc(Vector3 position, float radius) => NoiseSystem.Emit(position, radius);

    // Host: check the rescuer really is next to them and free, then free them
    [Rpc(SendTo.Server)]
    void FreeRpc(NetworkObjectReference targetRef)
    {
        if (!targetRef.TryGet(out var obj)) return;
        var target = obj.GetComponent<FirstPersonController>();
        if (target == null || !target.IsHidden || player.IsDead || player.IsGrabbed) return;
        if (FlatDistance(target.transform.position, transform.position) > freeRange + 1.5f) return;   // allow for lag
        Debug.Log($"Player {OwnerClientId} freed player {target.OwnerClientId}");
        target.Free();
    }

    void OnGUI()
    {
        if (!IsSpawned || !IsOwner || NetworkUI.MenuOpen) return;
        if (hintStyle == null) hintStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        hintStyle.fontSize = Mathf.RoundToInt(Screen.height * 0.028f);

        if (player.IsHidden)
        {
            GUI.Label(new Rect(0, Screen.height * 0.8f, Screen.width, Screen.height * 0.04f),
                      $"TAP {struggleKey.ToString().ToUpper()} TO STRUGGLE  (slows your timer, but it hears you)", hintStyle);
            return;
        }
        if (freeing == null) return;

        float w = Screen.width * 0.22f, h = Screen.height * 0.012f;
        float x = (Screen.width - w) / 2f, y = Screen.height * 0.62f;
        GUI.Label(new Rect(0, y - Screen.height * 0.05f, Screen.width, Screen.height * 0.04f),
                  $"HOLD {freeKey.ToString().ToUpper()} TO FREE PLAYER {freeing.OwnerClientId + 1}", hintStyle);
        if (freeProgress <= 0f) return;
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);
        GUI.color = new Color(0.85f, 0.85f, 0.8f, 0.9f);
        GUI.DrawTexture(new Rect(x, y, w * Mathf.Clamp01(freeProgress), h), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    // ---------- Blood trail ----------

    void UpdateTrail()
    {
        bool carried = player.IsInMonsterHand;
        Vector3 body = transform.position;

        if (carried && !wasCarried) { drips = 0; lastDrip = body; Drip(body, dripSize.y * 1.4f); }   // where it grabbed them
        if (carried && drips < maxDrips && FlatDistance(body, lastDrip) >= dripSpacing)
        {
            lastDrip = body;
            Drip(body, Random.Range(dripSize.x, dripSize.y));
        }
        wasCarried = carried;

        bool hidden = player.IsHidden;
        if (hidden && !wasHidden) Drip(body, poolSize);   // the pool where they lie
        wasHidden = hidden;
    }

    // A blood decal flat on the first real ground below (not bodies, monsters or players)
    void Drip(Vector3 from, float size)
    {
        var pal = CityManager.Instance != null && CityManager.Instance.settings != null ? CityManager.Instance.settings.palette : null;
        if (pal == null || pal.decalMaterial == null) return;
        if (!Ground(from + Vector3.up * 0.5f, out RaycastHit hit)) return;

        if (trailRoot == null)
        {
            var parent = CityManager.Current != null && CityManager.Current.root != null ? CityManager.Current.root.transform : null;
            trailRoot = new GameObject("BloodTrail").transform;   // goes with the city when the next run builds a new one
            trailRoot.SetParent(parent, false);
        }

        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        q.name = "Decal_BloodDrip";
        Destroy(q.GetComponent<Collider>());
        q.transform.SetParent(trailRoot, false);
        Vector3 n = hit.normal;
        q.transform.SetPositionAndRotation(hit.point + n * 0.02f,
            Quaternion.LookRotation(-n) * Quaternion.Euler(0f, 0f, Random.Range(0f, 360f)));
        q.transform.localScale = new Vector3(size, size * Random.Range(0.7f, 1.2f), 1f);
        var r = q.GetComponent<MeshRenderer>();
        r.sharedMaterial = BuildingDressing.DecalMaterial(pal, ProcTextures.DecalTexture(ProcTextures.Decal.Blood, 0));
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        drips++;
    }

    static readonly RaycastHit[] hits = new RaycastHit[16];

    static bool Ground(Vector3 from, out RaycastHit ground)
    {
        ground = default;
        float nearest = float.MaxValue;
        int n = Physics.RaycastNonAlloc(from, Vector3.down, hits, 40f, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var h = hits[i];
            if (h.distance >= nearest || PlayerRagdoll.IsBodyPart(h.collider) ||
                h.collider.GetComponentInParent<NetworkObject>() != null) continue;
            nearest = h.distance;
            ground = h;
        }
        return nearest < float.MaxValue;
    }
}
