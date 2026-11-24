using System;
using System.Collections.Generic;
using UnityEngine;

public class SoundCut : MonoBehaviour
{
    [System.Serializable]
    public class AudioRegion
    {
        public float start;
        public float end;
    }

    // 全人物の一覧（FocusFog と EnvironmentDucker が使う）
    public static readonly List<SoundCut> All = new List<SoundCut>();

    // 全人物の中で一番高い focus
    public static float MaxFocus
    {
        get
        {
            float m = 0f;
            for (int i = 0; i < All.Count; i++)
            {
                if (All[i] != null && All[i].Focus > m) m = All[i].Focus;
            }
            return m;
        }
    }

    public float Focus { get { return smoothFocus; } }

    [Header("参照")]
    public AudioSource audioSource;
    public AudioClip originalClip;
    public Transform player;

    [Header("初期位置で再生する区間（手動・秒）")]
    public AudioRegion[] alwaysAudibleRegions;
    [Range(0.1f, 1f)] public float audibleScale = 0.5f;

    [Header("区間の自動生成（手動の代わり）")]
    public bool useAutoRegions = false;
    public int autoSeed = 1;
    public Vector2 autoAudibleSec = new Vector2(0.05f, 0.15f);
    public Vector2 autoGapSec = new Vector2(0.1f, 0.35f);

    [Header("距離の設定")]
    public float farDistance = 10f;
    public float nearDistance = 1f;

    [Header("向きの設定")]
    public bool useFacing = true;
    public float facingFullAngle = 15f;
    public float facingNoneAngle = 60f;
    // 向いたときに、聞こえる区間が増える量（小さいほど内容は分からない）
    [Range(0f, 0.4f)] public float facingReveal = 0.15f;
    // 向いたときに、音が大きくなる量（気付くための合図）
    [Range(0f, 0.6f)] public float facingVolumeBoost = 0.3f;

    [Header("聞こえ方の設定")]
    [Range(0.5f, 1f)] public float fullFocusAt = 0.95f;
    public float focusSmoothTime = 0.3f;
    [Range(0f, 1f)] public float unfocusedVolume = 0.5f;
    [Range(0.5f, 3f)] public float voiceGain = 1f;
    public float fadeMs = 6f;
    public float loopEdgeFadeMs = 2f;

    [Header("デバッグ（読み取り用）")]
    public float debugDistance;
    public float debugFocus;
    public float debugAudibleRatio;

    // メインスレッド側
    private AudioClip dummyClip;
    private float smoothFocus;
    private float focusVelocity;
    private float smoothFacing;
    private float facingVelocity;

    // オーディオスレッドと共有
    private volatile float currentFocus;
    private volatile float currentVolume = 1f;
    private volatile bool ready;

    // オーディオスレッド専用
    private float[] pcm;
    private float[] threshold;
    private int pcmFrames;
    private int position;
    private float gain;
    private float rampStep;
    private int edgeSamples;

    void OnEnable()
    {
        if (!All.Contains(this)) All.Add(this);
    }

    void OnDisable()
    {
        All.Remove(this);
    }

    void Start()
    {
        if (audioSource == null) audioSource = GetComponent<AudioSource>();
        if (player == null && Camera.main != null) player = Camera.main.transform;

        if (audioSource == null || originalClip == null || player == null)
        {
            Debug.LogError("SoundCutの参照が不足しています: " + name);
            return;
        }

        int outRate = AudioSettings.outputSampleRate;

        if (!BuildData(outRate)) return;

        rampStep = 1f / Mathf.Max(1f, fadeMs * 0.001f * outRate);
        edgeSamples = Mathf.RoundToInt(loopEdgeFadeMs * 0.001f * outRate);

        // OnAudioFilterRead を動かすためのダミークリップ
        dummyClip = AudioClip.Create("SoundCutDummy", 1024, 1, outRate, false);
        float[] ones = new float[1024];
        for (int i = 0; i < ones.Length; i++) ones[i] = 1f;
        dummyClip.SetData(ones, 0);

        audioSource.Stop();
        audioSource.clip = dummyClip;
        audioSource.loop = true;
        audioSource.mute = false;
        ready = true;
        audioSource.Play();
    }

    void Update()
    {
        if (player == null) return;

        float distance = Vector3.Distance(player.position, transform.position);
        float distanceFocus = Mathf.InverseLerp(farDistance, nearDistance, distance);

        // 向き：1 = 正面、0 = そっぽ
        float facing = 0f;
        if (useFacing)
        {
            Vector3 to = transform.position - player.position;
            float angle = Vector3.Angle(player.forward, to);
            facing = 1f - Mathf.InverseLerp(facingFullAngle, facingNoneAngle, angle);
        }

        // 内容が聞こえる度合い：主役は距離。
        // 向きは遠いときにだけ少し足す（近づくほど効果は小さくなる）
        float targetFocus = distanceFocus + facingReveal * (1f - distanceFocus) * facing;

        smoothFocus = Mathf.SmoothDamp(
            smoothFocus, targetFocus, ref focusVelocity, focusSmoothTime);
        smoothFacing = Mathf.SmoothDamp(
            smoothFacing, facing, ref facingVelocity, focusSmoothTime);

        currentFocus = smoothFocus;
        // 音量：focus で上がり、向くとさらに少し上がる（気付くための合図）
        currentVolume = Mathf.Clamp01(
            Mathf.Lerp(unfocusedVolume, 1f, smoothFocus) + facingVolumeBoost * smoothFacing);

        debugDistance = distance;
        debugFocus = smoothFocus;
    }

    bool BuildData(int outRate)
    {
        int srcCh = originalClip.channels;
        int srcFrames = originalClip.samples;
        int srcRate = originalClip.frequency;

        float[] src = new float[srcFrames * srcCh];
        if (!originalClip.GetData(src, 0))
        {
            Debug.LogError("音声データを読み込めませんでした。AudioClipのLoad TypeをDecompress On Loadにしてください: " + name);
            return false;
        }

        // モノラル化
        float[] mono = new float[srcFrames];
        for (int i = 0; i < srcFrames; i++)
        {
            float sum = 0f;
            for (int c = 0; c < srcCh; c++) sum += src[i * srcCh + c];
            mono[i] = sum / srcCh;
        }

        // 出力サンプルレートに合わせる（線形補間）
        pcmFrames = Mathf.Max(1, Mathf.RoundToInt(srcFrames * (float)outRate / srcRate));
        float[] data = new float[pcmFrames];
        for (int i = 0; i < pcmFrames; i++)
        {
            float p = i * (float)srcRate / outRate;
            int i0 = Mathf.Min((int)p, srcFrames - 1);
            int i1 = Mathf.Min(i0 + 1, srcFrames - 1);
            data[i] = Mathf.Lerp(mono[i0], mono[i1], p - i0);
        }

        // 最初から聞こえる区間
        bool[] audible = new bool[pcmFrames];
        if (useAutoRegions)
        {
            System.Random rng = new System.Random(autoSeed);
            float t = 0f;
            float total = pcmFrames / (float)outRate;
            while (t < total)
            {
                float aud = Mathf.Lerp(autoAudibleSec.x, autoAudibleSec.y, (float)rng.NextDouble());
                float gap = Mathf.Lerp(autoGapSec.x, autoGapSec.y, (float)rng.NextDouble());
                int a = Mathf.Clamp(Mathf.RoundToInt(t * outRate), 0, pcmFrames);
                int b = Mathf.Clamp(Mathf.RoundToInt((t + aud) * outRate), 0, pcmFrames);
                for (int i = a; i < b; i++) audible[i] = true;
                t += aud + gap;
            }
        }
        else if (alwaysAudibleRegions != null)
        {
            foreach (AudioRegion r in alwaysAudibleRegions)
            {
                float center = (r.start + r.end) * 0.5f;
                float half = (r.end - r.start) * 0.5f * audibleScale;

                int a = Mathf.Clamp(Mathf.RoundToInt((center - half) * outRate), 0, pcmFrames);
                int b = Mathf.Clamp(Mathf.RoundToInt((center + half) * outRate), 0, pcmFrames);
                for (int i = a; i < b; i++) audible[i] = true;
            }
        }

        // ミュート区間ごとに「聞こえ始める focus」を計算
        // 両端が先に聞こえ、中央が最後に聞こえる
        float[] thr = new float[pcmFrames];
        int idx = 0;
        while (idx < pcmFrames)
        {
            if (audible[idx]) { thr[idx] = 0f; idx++; continue; }

            int a = idx;
            while (idx < pcmFrames && !audible[idx]) idx++;
            int b = idx; // 区間は [a, b)
            int len = b - a;
            bool openA = a > 0;
            bool openB = b < pcmFrames;

            for (int i = a; i < b; i++)
            {
                if (!openA && !openB) { thr[i] = 1f; continue; }
                float dA = openA ? (i - a) : float.MaxValue;
                float dB = openB ? (b - 1 - i) : float.MaxValue;
                float d = Mathf.Min(dA, dB);
                float norm = (openA && openB) ? len * 0.5f : len;
                thr[i] = Mathf.Clamp01((d + 0.5f) / Mathf.Max(1f, norm));
            }
        }

        // デバッグ：最初から聞こえる割合
        int audibleCount = 0;
        for (int i = 0; i < pcmFrames; i++)
            if (thr[i] == 0f) audibleCount++;
        debugAudibleRatio = (float)audibleCount / pcmFrames;
        Debug.Log("SoundCut(" + name + "): 最初から聞こえる割合=" +
            (debugAudibleRatio * 100f).ToString("F0") + "%, 長さ=" +
            (pcmFrames / (float)outRate).ToString("F2") + "秒");

        pcm = data;
        threshold = thr;
        return true;
    }

    // 音声処理スレッドから呼ばれる（new や Unity オブジェクトの参照は禁止）
    void OnAudioFilterRead(float[] data, int channels)
    {
        if (!ready)
        {
            Array.Clear(data, 0, data.Length);
            return;
        }

        float focus = currentFocus;
        float volume = currentVolume;
        bool full = focus >= fullFocusAt;
        int frames = data.Length / channels;

        for (int i = 0; i < frames; i++)
        {
            float target = (full || threshold[position] <= focus) ? 1f : 0f;

            // 音量を少しずつ変える（クリック防止）
            if (gain < target) { gain += rampStep; if (gain > target) gain = target; }
            else if (gain > target) { gain -= rampStep; if (gain < target) gain = target; }

            float g = gain * gain * (3f - 2f * gain);

            // ループの継ぎ目だけ極短いフェード
            float edge = 1f;
            if (edgeSamples > 0)
            {
                if (position < edgeSamples)
                    edge = (float)position / edgeSamples;
                else if (position >= pcmFrames - edgeSamples)
                    edge = (float)(pcmFrames - position) / edgeSamples;
            }

            float s = pcm[position] * g * edge * volume * voiceGain;
            if (s > 1f) s = 1f; else if (s < -1f) s = -1f;

            int baseIndex = i * channels;
            for (int c = 0; c < channels; c++) data[baseIndex + c] = s;

            position++;
            if (position >= pcmFrames) position = 0;
        }
    }

    void OnDestroy()
    {
        ready = false;
        if (dummyClip != null) Destroy(dummyClip);
    }
}