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

    [Header("Audio")]
    public AudioClip[] footstepClips;
    public AudioClip landClip;           // optional
    [Range(0f, 1f)] public float volume = 0.7f;
    public float hearDistance = 25f;     // how far other players hear your steps

    FirstPersonController player;
    CharacterController controller;
    AudioSource source;
    float distanceSinceStep;
    bool wasGrounded = true;
    Vector3 lastPosition;
    float smoothedSpeed;

    // For the Scene view gizmo
    Vector3 lastNoisePos;
    float lastNoiseRadius;
    float lastNoiseTime = -10f;

    void Awake()
    {
        player = GetComponent<FirstPersonController>();
        controller = GetComponent<CharacterController>();

        source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 1f;                       // fully 3D
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = 1f;
        source.maxDistance = hearDistance;
        source.dopplerLevel = 0f;
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

        if (!grounded || speed < 0.3f) return;

        distanceSinceStep += speed * Time.deltaTime;
        if (distanceSinceStep < strideLength) return;
        distanceSinceStep = 0f;

        float radius;
        if (player.IsCrouching) radius = crouchRadius;
        else if (speed > (player.walkSpeed + player.sprintSpeed) / 2f) radius = sprintRadius;
        else radius = walkRadius;

        Step(radius, false);
    }

    void Step(float radius, bool landing)
    {
        // Sound: louder steps for louder movement
        AudioClip clip = landing && landClip != null ? landClip : RandomClip(footstepClips);
        if (clip != null)
        {
            source.pitch = Random.Range(0.9f, 1.1f);
            float loudness = Mathf.Clamp01(radius / sprintRadius) * 0.75f + 0.25f;
            source.PlayOneShot(clip, volume * loudness);
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
