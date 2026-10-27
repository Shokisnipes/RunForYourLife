using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Switchable "retro" look (chunky low-res pixels, banded colours, extra glow and grain), or the normal look.
// Each player picks for themselves in the main or pause menu; the choice is remembered on their PC.
//  1. Pixels: the 3D view renders at about pixelHeight lines and is scaled up without smoothing (URP render scale
//     + nearest-neighbour upscaling). The menus and HUD stay sharp. It's also cheaper to render.
//  2. Colour steps + dithering: the RunForYourLife/RetroPost full-screen pass (on the URP renderer).
//  3. Glow and grain: a second Volume (the retro profile) faded in on top of the normal grading.
// Tools > RunForYourLife > Retro Look Setup creates all of this. Switching off puts the URP settings back exactly.
public class RetroLook : MonoBehaviour
{
    [Header("Pixels")]
    [Tooltip("How many pixel rows the 3D view is drawn with (screen height in 'big pixels'). Lower = chunkier.")]
    [Range(120, 720)] public int pixelHeight = 300;

    [Header("Colour")]
    [Tooltip("Colour steps per channel. Lower = stronger banding.")]
    [Range(4, 64)] public float colourSteps = 22f;
    [Tooltip("0 = hard colour bands, 1 = bands broken up with a fine dither pattern.")]
    [Range(0f, 1f)] public float dither = 0.7f;

    [Header("Glow and grain")]
    [Tooltip("Global Volume with the retro profile (more bloom, grain, vignette). Weight is set by this script.")]
    public Volume retroVolume;

    [Tooltip("Testing: start with the retro look on (otherwise each player's saved choice is used).")]
    public bool forceOnAtStart;

    public static RetroLook Instance { get; private set; }
    public static bool On { get; private set; }

    const string PrefKey = "RunForYourLife.RetroLook";
    static readonly int AmountId = Shader.PropertyToID("_RetroAmount");
    static readonly int StepsId = Shader.PropertyToID("_RetroSteps");
    static readonly int DitherId = Shader.PropertyToID("_RetroDither");

    UniversalRenderPipelineAsset urp;
    float normalScale;
    UpscalingFilterSelection normalFilter;
    int appliedHeight;

    void Awake()
    {
        Instance = this;
        urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (urp != null)
        {
            normalScale = urp.renderScale;
            normalFilter = urp.upscalingFilter;
        }
        On = forceOnAtStart || PlayerPrefs.GetInt(PrefKey, 0) == 1;
        Apply();
    }

    public void SetOn(bool on)
    {
        On = on;
        PlayerPrefs.SetInt(PrefKey, on ? 1 : 0);
        PlayerPrefs.Save();
        Apply();
    }

    public void Toggle() => SetOn(!On);

    void Update()
    {
        // Keep the pixel size the same if the window is resized, and pick up Inspector tweaks while playing
        if (On && Screen.height != appliedHeight) Apply();
        Shader.SetGlobalFloat(StepsId, colourSteps);
        Shader.SetGlobalFloat(DitherId, dither);
    }

    void Apply()
    {
        appliedHeight = Screen.height;
        if (urp != null)
        {
            if (On)
            {
                urp.renderScale = Mathf.Clamp(pixelHeight / (float)Mathf.Max(1, Screen.height), 0.1f, 1f);
                urp.upscalingFilter = UpscalingFilterSelection.Point;
            }
            else Restore();
        }
        Shader.SetGlobalFloat(AmountId, On ? 1f : 0f);
        Shader.SetGlobalFloat(StepsId, colourSteps);
        Shader.SetGlobalFloat(DitherId, dither);
        if (retroVolume != null) retroVolume.weight = On ? 1f : 0f;
    }

    // The URP asset is a project file: always put it back, so leaving Play mode never keeps the retro settings
    void Restore()
    {
        if (urp == null) return;
        urp.renderScale = normalScale;
        urp.upscalingFilter = normalFilter;
    }

    void OnDisable()
    {
        Restore();
        Shader.SetGlobalFloat(AmountId, 0f);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
