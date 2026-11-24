using UnityEngine;

public class FocusFog : MonoBehaviour
{
    [Header("霧の濃さ")]
    public float unfocusedDensity = 0.15f; // 誰にも近づいていないとき
    public float focusedDensity = 0.0f;    // 十分に近づいたとき

    [Header("見た目")]
    public Color fogColor = new Color(0.6f, 0.65f, 0.7f);
    public float smoothTime = 0.3f;

    [Header("空（背景）")]
    public bool matchBackgroundToFog = true;
    // 霧が晴れたときの空の色
    public Color clearSkyColor = new Color(0.5f, 0.7f, 0.95f);
    public Camera targetCamera;

    private float current;
    private float velocity;

    void Start()
    {
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = fogColor;
        current = unfocusedDensity;
        RenderSettings.fogDensity = current;

        if (targetCamera == null) targetCamera = Camera.main;
        if (matchBackgroundToFog && targetCamera != null)
        {
            targetCamera.clearFlags = CameraClearFlags.SolidColor;
            targetCamera.backgroundColor = fogColor;
        }
    }

    // 一番近い人物への「距離だけ」の focus（向きは関係しない）
    float DistanceOnlyFocus()
    {
        float best = 0f;
        for (int i = 0; i < SoundCut.All.Count; i++)
        {
            SoundCut s = SoundCut.All[i];
            if (s == null || s.player == null) continue;

            float d = Vector3.Distance(s.player.position, s.transform.position);
            float f = Mathf.InverseLerp(s.farDistance, s.nearDistance, d);
            if (f > best) best = f;
        }
        return best;
    }

    void Update()
    {
        float focus = DistanceOnlyFocus();
        float target = Mathf.Lerp(unfocusedDensity, focusedDensity, focus);
        current = Mathf.SmoothDamp(current, target, ref velocity, smoothTime);
        RenderSettings.fogDensity = current;

        // 霧が濃いほど背景を霧の色に、晴れるほど空の色にする
        if (matchBackgroundToFog && targetCamera != null)
        {
            float clear = Mathf.InverseLerp(unfocusedDensity, focusedDensity, current);
            targetCamera.backgroundColor = Color.Lerp(fogColor, clearSkyColor, clear);
        }
    }
}