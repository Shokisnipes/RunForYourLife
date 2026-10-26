using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Minimal horror-menu button: text only, slides right and turns red on hover.
public class MenuButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public Text label;
    public Color normalColor = new Color(0.82f, 0.82f, 0.8f);
    public Color hoverColor = new Color(0.8f, 0.12f, 0.1f);
    public float hoverShift = 14f;
    public AudioSource hoverSound;   // optional

    RectTransform labelRect;
    Vector2 basePos;
    bool hovered;

    // NetworkUI adds this component and only then assigns the label, so Awake is too early to set up
    void Start()
    {
        if (label != null)
        {
            labelRect = label.rectTransform;
            basePos = labelRect.anchoredPosition;
            label.color = normalColor;
        }
    }

    void OnDisable()
    {
        hovered = false;
        if (labelRect != null) labelRect.anchoredPosition = basePos;
        if (label != null) label.color = normalColor;
    }

    public void OnPointerEnter(PointerEventData e)
    {
        hovered = true;
        if (hoverSound != null) hoverSound.Play();
    }

    public void OnPointerExit(PointerEventData e) => hovered = false;

    void Update()
    {
        if (label == null || labelRect == null) return;
        float t = 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime);
        label.color = Color.Lerp(label.color, hovered ? hoverColor : normalColor, t);
        labelRect.anchoredPosition = Vector2.Lerp(labelRect.anchoredPosition, basePos + (hovered ? Vector2.right * hoverShift : Vector2.zero), t);
    }
}
