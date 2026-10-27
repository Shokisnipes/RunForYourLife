using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Stress while you're being chased. Lives on the Player prefab; only does anything for your own player.
// A fear level (0-1) rises fast while a monster is chasing close to you and drains slowly once it's gone, and drives:
//  - tunnel vision: a dark vignette that throbs with the heartbeat you hear, and a slightly narrower view
//  - colour drain and colour fringing at the edges
//  - muffled hearing: a low-pass on your ears. The giant's footsteps, scream, the heartbeat and the chase music
//    skip it (bypassListenerEffects in MonsterAudio), so the threat stays sharp while the rest of the world dulls.
//  - panting: your character's heavy breathing (only you hear it), louder and faster with fear, calming slowly after
// The look is a priority-20 global Volume made at runtime; its weight is the fear level.
[RequireComponent(typeof(FirstPersonController))]
public class ChaseStress : MonoBehaviour
{
    [Header("Fear")]
    [Tooltip("A chasing monster starts to scare you this far away (m) and is at full fear when on top of you.")]
    public float giantFearDistance = 45f;
    [Tooltip("Same for the Climber (it's small, so it has to be closer).")]
    public float climberFearDistance = 20f;
    [Tooltip("Fear from just being chased at all, however far it still is (inside the distance above).")]
    [Range(0f, 1f)] public float chasedFloor = 0.35f;
    [Tooltip("How fast fear rises (per second).")]
    public float riseSpeed = 1.2f;
    [Tooltip("How fast it drains once the chase is over (per second). Slow, so it lingers.")]
    public float fallSpeed = 0.2f;

    [Header("Tunnel vision")]
    [Tooltip("Vignette at full fear (0-1).")]
    [Range(0f, 1f)] public float vignette = 0.3f;
    [Tooltip("Extra vignette on each heartbeat at full fear.")]
    [Range(0f, 0.5f)] public float vignettePulse = 0.08f;
    [Tooltip("The view narrows by this many degrees at full fear.")]
    public float fovSqueeze = 5f;

    [Header("Colour")]
    [Tooltip("Saturation at full fear (-100 = black and white).")]
    [Range(-100f, 0f)] public float saturation = -40f;
    [Tooltip("Colour fringing at the screen edges at full fear (0-1).")]
    [Range(0f, 1f)] public float chromaticAberration = 0.6f;

    [Header("Hearing")]
    [Tooltip("Low-pass cutoff (Hz) at full fear. 22000 = no muffling; ~1000 = through a wall.")]
    public float muffledCutoff = 1200f;

    [Header("Panting")]
    [Tooltip("Your character's heavy breathing loop (2D, only you hear it).")]
    public AudioClip pantingLoop;
    [Tooltip("Volume at full fear.")]
    [Range(0f, 1f)] public float pantingVolume = 0.6f;
    [Tooltip("Fear below this is silent breathing.")]
    [Range(0f, 1f)] public float pantingStart = 0.2f;
    [Tooltip("Playback speed at full fear (faster, higher breaths). At the start it's 0.9.")]
    public float pantingPitch = 1.15f;

    // Read-only, for tuning in Play mode
    [Header("Debug (read only)")]
    [SerializeField, Range(0f, 1f)] float fear;

    FirstPersonController player;
    Volume volume;
    Vignette vig;
    ChromaticAberration chroma;
    AudioLowPassFilter lowPass;
    AudioSource panting;

    void Awake() => player = GetComponent<FirstPersonController>();

    void OnDestroy()
    {
        if (volume != null)
        {
            Destroy(volume.sharedProfile);
            Destroy(volume.gameObject);
        }
        if (lowPass != null) Destroy(lowPass);
        if (panting != null) Destroy(panting);
    }

    void Update()
    {
        if (!player.IsSpawned || !player.IsOwner) return;
        if (volume == null) Build();

        // Caught: maximum terror. Dead: it all drains away behind the black screen.
        float target = player.IsDead ? 0f : player.IsGrabbed ? 1f : Threat();
        float speed = target > fear ? riseSpeed : (player.IsDead ? 2f : fallSpeed);
        fear = Mathf.MoveTowards(fear, target, speed * Time.deltaTime);

        float pulse = Heartbeat();
        float f = Mathf.SmoothStep(0f, 1f, fear);
        volume.weight = f;
        vig.intensity.value = vignette + vignettePulse * pulse;
        chroma.intensity.value = chromaticAberration * (0.8f + 0.2f * pulse);
        player.FovOffset = -fovSqueeze * f;
        lowPass.cutoffFrequency = Mathf.Lerp(22000f, muffledCutoff, f * f);   // muffling comes in late
        lowPass.enabled = f > 0.001f;

        if (panting != null)
        {
            float breath = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(pantingStart, 1f, fear));
            panting.volume = pantingVolume * breath;
            panting.pitch = Mathf.Lerp(0.9f, pantingPitch, breath);
            if (breath > 0.001f && !panting.isPlaying)
            {
                panting.time = Random.Range(0f, pantingLoop.length * 0.9f);   // not the same breath every chase
                panting.Play();
            }
            else if (breath <= 0.001f && panting.isPlaying) panting.Stop();
        }
    }

    // How scary things are right now: the closest chasing monster, by how near it is
    MonsterAI[] giants;
    ClimberAI[] climbers;
    float Threat()
    {
        // The monsters are scene objects that never go away, so look them up once
        if (giants == null) giants = FindObjectsByType<MonsterAI>();
        if (climbers == null) climbers = FindObjectsByType<ClimberAI>();

        float worst = 0f;
        Vector3 me = transform.position;
        foreach (var giant in giants)
        {
            if (giant == null || !giant.IsSpawned || giant.SyncedState != MonsterState.Chase) continue;
            worst = Mathf.Max(worst, ChaseFear(Vector3.Distance(me, giant.transform.position), giantFearDistance));
        }
        foreach (var climber in climbers)
        {
            if (climber == null || !climber.IsSpawned || climber.SyncedState != ClimberState.Chase) continue;
            worst = Mathf.Max(worst, ChaseFear(Vector3.Distance(me, climber.transform.position), climberFearDistance));
        }
        return worst;
    }

    float ChaseFear(float distance, float range)
    {
        if (distance > range) return 0f;
        return Mathf.Lerp(chasedFloor, 1f, 1f - distance / range);
    }

    // 0-1 on each beat of the heartbeat you hear (the giant's MonsterAudio plays it)
    MonsterAudio heartbeatSource;
    float Heartbeat()
    {
        if (heartbeatSource == null) heartbeatSource = FindAnyObjectByType<MonsterAudio>();
        return heartbeatSource != null ? heartbeatSource.HeartbeatPulse() : 0f;
    }

    void Build()
    {
        // Only the settings we drive are overridden, so the scene's own grading shows through underneath;
        // the Volume's weight blends from that toward these
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        vig = profile.Add<Vignette>();
        vig.intensity.overrideState = true;
        vig.smoothness.Override(0.6f);
        vig.color.Override(Color.black);
        var colour = profile.Add<ColorAdjustments>();
        colour.saturation.Override(saturation);
        chroma = profile.Add<ChromaticAberration>();
        chroma.intensity.overrideState = true;

        var go = new GameObject("Chase Stress Volume");
        volume = go.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 20f;   // above the scene and the retro look
        volume.weight = 0f;
        volume.sharedProfile = profile;

        // On our ears (the listener lives on the camera)
        var listener = player.cameraPivot.GetComponentInChildren<AudioListener>(true);
        lowPass = (listener != null ? listener.gameObject : gameObject).AddComponent<AudioLowPassFilter>();
        lowPass.cutoffFrequency = 22000f;
        lowPass.enabled = false;

        // Our own breath, right in our head; never muffled (that's the point: all you hear is yourself)
        if (pantingLoop != null)
        {
            panting = gameObject.AddComponent<AudioSource>();
            panting.clip = pantingLoop;
            panting.loop = true;
            panting.playOnAwake = false;
            panting.spatialBlend = 0f;
            panting.bypassListenerEffects = true;
            panting.volume = 0f;
        }
    }
}
