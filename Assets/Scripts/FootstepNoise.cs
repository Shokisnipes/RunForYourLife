using Unity.Netcode;
using UnityEngine;

// Put on the Player next to FirstPersonController.
// Every player's footsteps play in 3D on every computer.
// The owner also sends each step to the host as a noise the monster can hear:
// sprinting is loud, walking is medium, crouching is nearly silent.
[RequireComponent(typeof(FirstPersonController))]
public class FootstepNoise : NetworkBehaviour
{
    [Header("Noise radius per footstep (meters) - what the monster hears")]
    public float crouchRadius = 2f;
    public float walkRadius = 8f;
    public float sprintRadius = 18f;
    public float landRadius = 10f;

    [Header("Steps")]
    public float strideLength = 0.75f;   // meters travelled per footstep
    [Tooltip("Stand still this long and your next movement plays a footstep right away.")]
    public float stopTime = 0.15f;

    [Header("Audio")]
    [Tooltip("Used for both feet if the left/right lists below are empty.")]
    public AudioClip[] footstepClips;
    public AudioClip[] leftFootClips;    // optional: alternates left, right, left, right...
    public AudioClip[] rightFootClips;
    [Tooltip("How far each foot sits from the center, in meters. Gives a subtle left/right pan.")]
    public float footSpacing = 0.12f;
    public AudioClip landClip;           // optional
    [Range(0f, 1f)] public float volume = 0.7f;
    public float hearDistance = 25f;     // how far other players hear your steps

    FirstPersonController player;
    CharacterController controller;
    AudioSource leftSource, rightSource;
    bool nextIsLeft = true;
    float distanceSinceStep;
    bool wasGrounded = true;
    Vector3 lastPosition;
    float smoothedSpeed;
    float stillTimer = 999f;             // start "stopped" so the very first move makes a step

    // For the Scene view gizmo
    Vector3 lastNoisePos;
    float lastNoiseRadius;
    float lastNoiseTime = -10f;

    void Awake()
    {
        player = GetComponent<FirstPersonController>();
        controller = GetComponent<CharacterController>();

        leftSource = MakeFootSource("Foot_L", -footSpacing);
        rightSource = MakeFootSource("Foot_R", footSpacing);
    }

    AudioSource MakeFootSource(string name, float x)
    {
        var foot = new GameObject(name);
        foot.transform.SetParent(transform, false);
        foot.transform.localPosition = new Vector3(x, 0.05f, 0f);

        var s = foot.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.spatialBlend = 1f;                       // fully 3D
        s.rolloffMode = AudioRolloffMode.Linear;
        s.minDistance = 1f;
        s.maxDistance = hearDistance;
        s.dopplerLevel = 0f;
        return s;
    }

    public override void OnNetworkSpawn()
    {
        lastPosition = transform.position;
    }

    void Update()
    {
        if (!IsSpawned || Time.deltaTime <= 0f) return;

        float speed;
        bool grounded;

        if (IsOwner)
        {
            speed = player.CurrentSpeed;
            grounded = controller.isGrounded;

            // Landing from a jump/fall is loud
            if (grounded && !wasGrounded) Step(landRadius, true);
            wasGrounded = grounded;
        }
        else
        {
            // Other players: work out their speed from how far they moved
            Vector3 delta = transform.position - lastPosition;
            delta.y = 0f;
            smoothedSpeed = Mathf.Lerp(smoothedSpeed, delta.magnitude / Time.deltaTime, Time.deltaTime * 10f);
            speed = smoothedSpeed;
            grounded = true;
        }
        lastPosition = transform.position;

        if (!grounded) return;

        if (speed < 0.3f)
        {
            stillTimer += Time.deltaTime;
            return;
        }

        // Just started moving after standing still: first footfall happens immediately
        if (stillTimer >= stopTime)
        {
            stillTimer = 0f;
            distanceSinceStep = 0f;
            Step(RadiusFor(speed), false);
            return;
        }
        stillTimer = 0f;

        distanceSinceStep += speed * Time.deltaTime;
        if (distanceSinceStep < strideLength) return;
        distanceSinceStep = 0f;

        Step(RadiusFor(speed), false);
    }

    float RadiusFor(float speed)
    {
        if (player.IsCrouching) return crouchRadius;
        if (speed > (player.walkSpeed + player.sprintSpeed) / 2f) return sprintRadius;
        return walkRadius;
    }

    void Step(float radius, bool landing)
    {
        // Alternate feet: left, right, left, right...
        bool left = nextIsLeft;
        if (!landing) nextIsLeft = !nextIsLeft;
        AudioSource src = left ? leftSource : rightSource;

        AudioClip clip;
        if (landing && landClip != null) clip = landClip;
        else
        {
            var footClips = left ? leftFootClips : rightFootClips;
            clip = RandomClip(footClips) ?? RandomClip(footstepClips);
        }

        // Sound: louder steps for louder movement
        if (clip != null)
        {
            src.pitch = Random.Range(0.92f, 1.08f);
            float loudness = Mathf.Clamp01(radius / sprintRadius) * 0.75f + 0.25f;
            src.PlayOneShot(clip, volume * loudness);
        }

        // Noise for the monster (only the owner reports, so it isn't counted twice)
        if (IsOwner) NoiseRpc(transform.position, radius);

        lastNoisePos = transform.position;
        lastNoiseRadius = radius;
        lastNoiseTime = Time.time;
    }

    [Rpc(SendTo.Server)]
    void NoiseRpc(Vector3 position, float radius)
    {
        NoiseSystem.Emit(position, radius);
    }

    static AudioClip RandomClip(AudioClip[] clips)
    {
        if (clips == null || clips.Length == 0) return null;
        return clips[Random.Range(0, clips.Length)];
    }

    // Yellow circle in the Scene view shows how far each footstep carries
    void OnDrawGizmos()
    {
        if (Time.time - lastNoiseTime > 0.4f) return;
        Gizmos.color = new Color(1f, 0.9f, 0.2f, 0.6f);
        Gizmos.DrawWireSphere(lastNoisePos, lastNoiseRadius);
    }
}
