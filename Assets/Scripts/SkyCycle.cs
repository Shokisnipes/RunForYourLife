using UnityEngine;
using UnityEngine.Rendering;

// Evening to night during the prep phase. At the start of prep it's a fall evening around 8 pm: the sun has just
// set behind the city, the sky glows orange low in the west, light is warm and dim. As the prep timer runs down it
// fades into night: stars, cold moonlight from the moon, thick fog. Clouds drift across the sky the whole time.
//
// Runs on every machine from the synced GameFlow clock (purely visual). Put it on the Directional Light; the sky
// itself is the RunForYourLife/Sky shader (Assets/Materials/Sky.mat).
//
// One directional light plays both parts: the low sun at dusk, then the moon. It fades out around mid-twilight
// while it swings over, so shadows never visibly sweep across the ground.
[RequireComponent(typeof(Light))]
public class SkyCycle : MonoBehaviour
{
    [System.Serializable]
    public struct Look
    {
        [Tooltip("Direct light (sun at dusk, moon at night).")]
        public Color lightColor;
        public float lightIntensity;
        [Tooltip("Flat ambient light: how bright the shadows are.")]
        public Color ambient;
        [Tooltip("Fog colour; the sky melts into it at the horizon.")]
        public Color fog;
        [Tooltip("Exponential-squared fog density. Night was 0.053 (about 30 m of view).")]
        public float fogDensity;
        public Color zenith;
        [Tooltip("Sunset glow strength around the sun.")]
        public float sunGlow;
        public float stars;
        public Color cloudLit;
        public Color cloudDark;
        [Tooltip("How much the sun lights cloud edges (orange undersides at dusk).")]
        public float cloudSunLight;
        [Tooltip("Moon disc brightness.")]
        public float moonBrightness;
    }

    [Tooltip("Sky material (RunForYourLife/Sky). A copy is used at runtime so the asset isn't changed.")]
    public Material skyMaterial;

    [Header("Looks")]
    public Look dusk = new Look
    {
        lightColor = new Color(1f, 0.6f, 0.36f), lightIntensity = 1.2f,
        ambient = new Color(0.42f, 0.39f, 0.44f),
        fog = new Color(0.52f, 0.44f, 0.44f), fogDensity = 0.012f,
        zenith = new Color(0.22f, 0.31f, 0.52f), sunGlow = 1.6f, stars = 0f,
        cloudLit = new Color(0.62f, 0.45f, 0.42f), cloudDark = new Color(0.27f, 0.25f, 0.31f), cloudSunLight = 1.6f,
        moonBrightness = 0.8f,
    };
    public Look night = new Look
    {
        lightColor = new Color(0.62f, 0.72f, 1f), lightIntensity = 0.3f,
        ambient = new Color(0.07f, 0.08f, 0.12f),
        fog = new Color(0.098f, 0.118f, 0.157f), fogDensity = 0.053f,
        zenith = new Color(0.015f, 0.022f, 0.045f), sunGlow = 0f, stars = 1f,
        cloudLit = new Color(0.13f, 0.14f, 0.17f), cloudDark = new Color(0.03f, 0.035f, 0.045f), cloudSunLight = 0f,
        moonBrightness = 2.5f,
    };

    [Header("Sun and moon (degrees)")]
    [Tooltip("Compass direction of the setting sun (0 = +Z, 90 = +X).")]
    public float sunAzimuth = 250f;
    [Tooltip("Sun height at the start of prep (just set or setting). It sinks further as night falls.")]
    public float sunElevationStart = 2f;
    public float sunElevationEnd = -18f;
    [Tooltip("Where the moon hangs. Roughly opposite the sun gives a nearly full moon.")]
    public float moonAzimuth = 95f;
    public float moonElevation = 32f;

    [Header("Timing")]
    [Tooltip("How dark it gets over the prep phase (x = prep progress 0..1, y = 0 dusk .. 1 night).")]
    public AnimationCurve darkening = AnimationCurve.EaseInOut(0f, 0f, 0.9f, 1f);
    [Tooltip("Dawn: how much of the end of the night the sky spends coming back to light (0.15 = the last 15%). " +
             "It plays the dusk fade backwards and reaches full dawn as the timer runs out (the win).")]
    [Range(0f, 0.5f)] public float dawnFraction = 0.15f;
    [Tooltip("How light it gets over the dawn part of the night (x = dawn progress 0..1, y = 0 night .. 1 dawn).")]
    public AnimationCurve brightening = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    [Tooltip("Compass direction of the rising sun at dawn (east-ish, across the sky from where it set).")]
    public float dawnAzimuth = 70f;
    [Tooltip("Darkness behind the main menu (0 = dusk, 1 = night).")]
    [Range(0f, 1f)] public float menuDarkness = 1f;
    [Tooltip("Testing (Play mode): above 0 forces this darkness, so you can scrub between dusk and night.")]
    [Range(0f, 1f)] public float previewDarkness = 0f;

    [Tooltip("Extra ambient light around mid-twilight, while the light swaps from sun to moon, so that moment " +
             "isn't darker than full night.")]
    [Range(0f, 1f)] public float twilightLift = 0.5f;

    [Header("Clouds")]
    [Range(0f, 1f)] public float cloudCover = 0.5f;
    [Range(0f, 0.2f)] public float cloudSpeed = 0.012f;

    // How dark it is right now, 0 = dusk .. 1 = night (AmbientAudio fades the night sounds in with it)
    public static float Darkness01 { get; private set; } = 1f;

    Light lightSource;
    Material sky;
    bool rising;   // dawn: the sun comes up on the dawn side instead of setting on the dusk side

    static readonly int Zenith = Shader.PropertyToID("_ZenithColor"), Horizon = Shader.PropertyToID("_HorizonColor"),
        Ground = Shader.PropertyToID("_GroundColor"), SunDir = Shader.PropertyToID("_SunDir"), SunGlow = Shader.PropertyToID("_SunGlow"),
        MoonDir = Shader.PropertyToID("_MoonDir"), MoonBright = Shader.PropertyToID("_MoonBrightness"), Stars = Shader.PropertyToID("_Stars"),
        CloudCover = Shader.PropertyToID("_CloudCover"), CloudSpeed = Shader.PropertyToID("_CloudSpeed"),
        CloudLit = Shader.PropertyToID("_CloudLit"), CloudDark = Shader.PropertyToID("_CloudDark"), CloudSun = Shader.PropertyToID("_CloudSunLight");

    void Awake()
    {
        lightSource = GetComponent<Light>();
        lightSource.useColorTemperature = false;   // colour comes from the looks above
        if (skyMaterial != null)
        {
            sky = new Material(skyMaterial);
            RenderSettings.skybox = sky;
        }
        RenderSettings.sun = lightSource;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
    }

    void OnDestroy()
    {
        if (sky != null) Destroy(sky);
    }

    void Update()
    {
        Darkness01 = Darkness();
        Apply(Darkness01);
    }

    // 0 = dusk (or dawn), 1 = night
    float Darkness()
    {
        rising = false;
        if (previewDarkness > 0f) return previewDarkness;
        var flow = GameFlow.Instance;
        if (flow == null || !flow.IsSpawned) return menuDarkness;
        switch (flow.Phase)
        {
            case GamePhase.Prep:
                float progress = flow.prepSeconds > 0f ? 1f - flow.SecondsLeft / flow.prepSeconds : 1f;
                return Mathf.Clamp01(darkening.Evaluate(Mathf.Clamp01(progress)));
            case GamePhase.Night:
                // The last stretch of the night is dusk in reverse: the sky lightens toward dawn
                float dawnSeconds = flow.nightSeconds * dawnFraction;
                if (dawnSeconds <= 0f || flow.SecondsLeft >= dawnSeconds) return 1f;
                rising = true;
                return 1f - Mathf.Clamp01(brightening.Evaluate(1f - flow.SecondsLeft / dawnSeconds));
            case GamePhase.Won:
                rising = true;   // survived: the sun is up
                return 0f;
            default:
                return 1f;       // everyone died: it stays night
        }
    }

    void Apply(float t)
    {
        Look a = dusk, b = night;
        float sunAz = rising ? dawnAzimuth : sunAzimuth;
        Vector3 sunDir = Direction(sunAz, Mathf.Lerp(sunElevationStart, sunElevationEnd, t));
        Vector3 moonDir = Direction(moonAzimuth, moonElevation);

        // The light: sun fading out over the first half, moon fading in over the second
        bool moonPhase = t >= 0.5f;
        float k = moonPhase ? Mathf.InverseLerp(0.5f, 1f, t) : 1f - Mathf.InverseLerp(0f, 0.5f, t);
        Look l = moonPhase ? b : a;
        lightSource.color = l.lightColor;
        lightSource.intensity = l.lightIntensity * Mathf.SmoothStep(0f, 1f, k);
        lightSource.enabled = lightSource.intensity > 0.002f;
        Vector3 from = moonPhase ? moonDir : Direction(sunAz, Mathf.Max(sunElevationStart, 6f));   // a set sun still lights from just above the rooftops
        transform.rotation = Quaternion.LookRotation(-from);

        Color fog = Color.Lerp(a.fog, b.fog, t);
        // URP lights the scene from the ambient probe, which isn't refreshed when ambientLight changes at runtime
        float swap = Mathf.Clamp01(1f - Mathf.Abs(t - 0.5f) * 2f);   // 1 at the swap, 0 at either end
        Color ambient = Color.Lerp(a.ambient, b.ambient, t) + a.ambient * (twilightLift * swap);
        RenderSettings.ambientLight = ambient;
        var probe = new SphericalHarmonicsL2();
        probe.AddAmbientLight(ambient.linear);
        RenderSettings.ambientProbe = probe;
        RenderSettings.fogColor = fog;
        RenderSettings.fogDensity = Mathf.Lerp(a.fogDensity, b.fogDensity, t);

        if (sky == null) return;
        sky.SetColor(Zenith, Color.Lerp(a.zenith, b.zenith, t));
        sky.SetColor(Horizon, fog);
        sky.SetColor(Ground, fog * 0.4f);
        sky.SetVector(SunDir, sunDir);
        sky.SetFloat(SunGlow, Mathf.Lerp(a.sunGlow, b.sunGlow, t));
        sky.SetVector(MoonDir, moonDir);
        sky.SetFloat(MoonBright, Mathf.Lerp(a.moonBrightness, b.moonBrightness, t));
        sky.SetFloat(Stars, Mathf.Lerp(a.stars, b.stars, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.4f, 1f, t))));   // stars come out late
        sky.SetFloat(CloudCover, cloudCover);
        sky.SetFloat(CloudSpeed, cloudSpeed);
        sky.SetColor(CloudLit, Color.Lerp(a.cloudLit, b.cloudLit, t));
        sky.SetColor(CloudDark, Color.Lerp(a.cloudDark, b.cloudDark, t));
        sky.SetFloat(CloudSun, Mathf.Lerp(a.cloudSunLight, b.cloudSunLight, t));
    }

    // Unit vector pointing at a spot in the sky
    static Vector3 Direction(float azimuth, float elevation)
    {
        float az = azimuth * Mathf.Deg2Rad, el = elevation * Mathf.Deg2Rad;
        return new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Cos(az) * Mathf.Cos(el)).normalized;
    }
}
