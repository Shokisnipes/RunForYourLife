using UnityEngine;

// Put on the Player next to FirstPersonController.
// Each footstep emits a noise: sprinting is loud, walking is medium, crouching is nearly silent.
[RequireComponent(typeof(FirstPersonController))]
public class FootstepNoise : MonoBehaviour
{
    [Header("Noise radius per footstep (meters)")]
    public float crouchRadius = 2f;
    public float walkRadius = 8f;
    public float sprintRadius = 18f;
    public float landRadius = 10f;

    [Header("Steps")]
    public float strideLength = 0.75f;   // meters travelled per footstep

    [Header("Audio (optional)")]
    public AudioSource audioSource;
    public AudioClip[] footstepClips;

    FirstPersonController player;
    CharacterController controller;
    float distanceSinceStep;
    bool wasGrounded = true;

    // For the Scene view gizmo
    Vector3 lastNoisePos;
    float lastNoiseRadius;
    float lastNoiseTime = -10f;

    void Awake()
    {
        player = GetComponent<FirstPersonController>();
        controller = GetComponent<CharacterController>();
    }

    void Update()
    {
        bool grounded = controller.isGrounded;

        // Landing from a jump/fall is loud
        if (grounded && !wasGrounded)
            MakeNoise(landRadius);
        wasGrounded = grounded;

        float speed = player.CurrentSpeed;
        if (!grounded || speed < 0.1f) return;

        distanceSinceStep += speed * Time.deltaTime;
        if (distanceSinceStep < strideLength) return;
        distanceSinceStep = 0f;

        float radius;
        if (player.IsCrouching) radius = crouchRadius;
        else if (speed > (player.walkSpeed + player.sprintSpeed) / 2f) radius = sprintRadius;
        else radius = walkRadius;

        MakeNoise(radius);
    }

    void MakeNoise(float radius)
    {
        NoiseSystem.Emit(transform.position, radius);

        lastNoisePos = transform.position;
        lastNoiseRadius = radius;
        lastNoiseTime = Time.time;

        if (audioSource != null && footstepClips != null && footstepClips.Length > 0)
        {
            audioSource.pitch = Random.Range(0.9f, 1.1f);
            float volume = Mathf.Clamp01(radius / sprintRadius) * 0.8f + 0.2f;
            audioSource.PlayOneShot(footstepClips[Random.Range(0, footstepClips.Length)], volume);
        }
    }

    // Yellow circle in the Scene view shows how far each footstep carries
    void OnDrawGizmos()
    {
        if (Time.time - lastNoiseTime > 0.4f) return;
        Gizmos.color = new Color(1f, 0.9f, 0.2f, 0.6f);
        Gizmos.DrawWireSphere(lastNoisePos, lastNoiseRadius);
    }
}
