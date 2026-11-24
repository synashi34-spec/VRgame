using UnityEngine;

public class FocusFog : MonoBehaviour
{
    [Header("霧の濃さ")]
    public float unfocusedDensity = 0.15f; // 誰にも注目していないとき
    public float focusedDensity = 0.0f;    // 注目しているとき

    [Header("見た目")]
    public Color fogColor = new Color(0.6f, 0.65f, 0.7f);
    public float smoothTime = 0.3f;

    private float current;
    private float velocity;

    void Start()
    {
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = fogColor;
        current = unfocusedDensity;
        RenderSettings.fogDensity = current;
    }

    void Update()
    {
        float target = Mathf.Lerp(unfocusedDensity, focusedDensity, SoundCut.MaxFocus);
        current = Mathf.SmoothDamp(current, target, ref velocity, smoothTime);
        RenderSettings.fogDensity = current;
    }
}