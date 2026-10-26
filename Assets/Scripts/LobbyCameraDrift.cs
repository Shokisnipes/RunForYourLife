using UnityEngine;

// Slow, uneasy drift for the menu background camera (added automatically by NetworkUI).
public class LobbyCameraDrift : MonoBehaviour
{
    public float swayDegrees = 1.5f;
    public float driftMeters = 0.4f;
    public float speed = 0.08f;

    Vector3 startPos;
    Quaternion startRot;

    void Start()
    {
        startPos = transform.position;
        startRot = transform.rotation;
    }

    void Update()
    {
        float t = Time.time * speed;
        float nx = Mathf.PerlinNoise(t, 0.3f) * 2f - 1f;
        float ny = Mathf.PerlinNoise(0.7f, t) * 2f - 1f;
        float nz = Mathf.PerlinNoise(t, t + 5f) * 2f - 1f;
        transform.position = startPos + new Vector3(nx, ny * 0.5f, nz) * driftMeters;
        transform.rotation = startRot * Quaternion.Euler(ny * swayDegrees, nx * swayDegrees, nz * swayDegrees * 0.3f);
    }
}
