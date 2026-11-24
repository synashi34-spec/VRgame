using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class EnvironmentDucker : MonoBehaviour
{
    [Header("誰かに完全に注目したときの環境音の音量（元の音量に対する割合）")]
    [Range(0f, 1f)] public float duckedRatio = 0.4f;

    private AudioSource source;
    private float baseVolume;

    void Start()
    {
        source = GetComponent<AudioSource>();
        baseVolume = source.volume;
    }

    void Update()
    {
        float focus = SoundCut.MaxFocus;
        source.volume = baseVolume * Mathf.Lerp(1f, duckedRatio, focus);
    }
}