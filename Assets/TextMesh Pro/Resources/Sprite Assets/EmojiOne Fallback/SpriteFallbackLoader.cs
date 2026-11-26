using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.XR;
#if UNITY_EDITOR && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Unity.VRTemplate
{
    // BasicScene 再生中、まれに霧の向こうからイラストがこちらを見つめる演出。
    // ・霧の濃さに合わせて「うっすら見える」距離に、プレイヤーの視界の外で現れる
    // ・近づかれると下がり、離れられると付いてきて、同じ距離を保つ
    // ・霧が晴れてはっきり見えそうになったら、霧を一時的に濃くして、隠れてから消える
    // ・一度視界に入ったら、次に視界から外れた時点で消える（二度見するといない）
    // シーンやプレハブには何も置かず、実行時に自動で起動する。FocusFog などの既存スクリプトは変更しない。
    // このフォルダ（EmojiOne Fallback）ごと消せば完全に削除できる。
    //
    // テスト用の隠し操作（待ち時間を飛ばして、次に出現できるタイミングでプレイヤーの真右に出現させる）
    // ・VR：左右のスティックを両方押し込んだまま 3 秒（発動するとコントローラーが短く振動する）
    // ・エディタ：Ctrl + Shift + F12
    public class SpriteFallbackLoader : MonoBehaviour
    {
        const string k_TargetScene = "BasicScene";
        const string k_MaterialPath = "Sprite Assets/EmojiOne Fallback/EmojiOne Fallback";

        // 出現間隔（分）。1時間に1回あるかないか
        static readonly Vector2 s_FirstDelayMin = new Vector2(15f, 90f);
        static readonly Vector2 s_IntervalMin = new Vector2(45f, 100f);

        // 霧越しの見え方（0 = 霧で完全に隠れる, 1 = 霧の影響なし）
        // 出現時はこの範囲の「うっすら」になる距離を選ぶ
        static readonly Vector2 s_SpawnVisibility = new Vector2(0.15f, 0.3f);
        // 出現距離の下限・上限（m）。上限でもうっすらにならないほど霧が薄いときは出現しない
        static readonly Vector2 s_DistanceLimit = new Vector2(6f, 14f);
        // これより見えてしまったら（霧が晴れた・近づかれた）霧を濃くして隠す
        const float k_RevealLimit = 0.45f;
        // ここまで霧に隠れたら消す
        const float k_HiddenVisibility = 0.02f;
        // 霧を濃くするのにかける秒数、元に戻すのにかける秒数
        const float k_ThickenTime = 2.5f;
        const float k_ReleaseTime = 5f;
        // 距離を保つために下がる最大速度（m/s）。テレポートなどで追いつかれたら霧で隠れる
        const float k_RetreatSpeed = 6f;
        // 離れられたときに付いてくる最大速度（m/s）
        const float k_FollowSpeed = 6f;

        // 出現位置の、正面からの角度（視界の外）
        static readonly Vector2 s_AngleFromFront = new Vector2(75f, 150f);

        // 大きさ（等身大の胸像）と、顔がイラストのどの高さにあるか（下端=0, 上端=1）
        const float k_Width = 0.75f;
        const float k_FaceHeightRatio = 0.667f;

        // 視界判定の余白（ビューポート比）。少し広めに取って、画面の端でも出入りが見えないようにする
        const float k_ViewMargin = 0.15f;
        // この秒数以上視界に入ったら「見られた」とみなす
        const float k_SeenTime = 0.25f;
        // 見られないまま、この秒数がたったら（視界の外で）引っ込む
        const float k_MaxWaitUnseen = 90f;
        // 出現できる場所・霧の濃さになるまで待つ最大秒数
        const float k_PlaceTimeout = 60f;
        // テスト操作：両スティック押し込みを続ける秒数
        const float k_TestHoldTime = 3f;

        static SpriteFallbackLoader s_Instance;

        Transform m_Figure;
        float m_Height;
        bool m_Forced;
        float m_TestHold;
        readonly Vector3[] m_Corners = new Vector3[4];

        // 霧の上書き（0 なら上書きしない）。FocusFog が Update で書いた値より濃いときだけ LateUpdate で上書きする
        float m_FogOverride;
        bool m_Thickening;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != k_TargetScene || s_Instance != null)
                return;

            var material = Resources.Load<Material>(k_MaterialPath);
            if (material == null)
                return;

            var host = new GameObject(string.Empty) { hideFlags = HideFlags.HideInHierarchy };
            SceneManager.MoveGameObjectToScene(host, scene);
            s_Instance = host.AddComponent<SpriteFallbackLoader>();
            s_Instance.Setup(material);
        }

        void Setup(Material material)
        {
            var tex = material.GetTexture("_BaseMap");
            float aspect = tex != null ? (float)tex.height / tex.width : 1.25f;
            m_Height = k_Width * aspect;

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(quad.GetComponent<Collider>());
            quad.name = string.Empty;
            quad.hideFlags = HideFlags.HideInHierarchy;
            quad.transform.SetParent(transform, false);
            quad.transform.localScale = new Vector3(k_Width, m_Height, 1f);

            m_Figure = quad.transform;
            var renderer = quad.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            quad.SetActive(false);
            StartCoroutine(Run());
        }

        void OnDestroy()
        {
            if (s_Instance == this)
                s_Instance = null;
        }

        void Update()
        {
            if (m_Forced)
                return;

            if (TestKeyPressed())
            {
                Force();
                return;
            }

            if (BothSticksPressed())
            {
                m_TestHold += Time.deltaTime;
                if (m_TestHold >= k_TestHoldTime)
                    Force();
            }
            else
            {
                m_TestHold = 0f;
            }
        }

        void LateUpdate()
        {
            // 霧を濃くしていないときは、上書きをゆっくり 0 に戻す
            if (!m_Thickening && m_FogOverride > 0f)
            {
                float rate = Mathf.Max(m_FogOverride, 0.05f) / k_ReleaseTime;
                m_FogOverride = Mathf.MoveTowards(m_FogOverride, 0f, rate * Time.deltaTime);
            }

            if (m_FogOverride > RenderSettings.fogDensity)
                RenderSettings.fogDensity = m_FogOverride;
        }

        void Force()
        {
            m_Forced = true;
            m_TestHold = 0f;
            Pulse(XRNode.LeftHand);
            Pulse(XRNode.RightHand);
            TestLog("テスト出現を受け付けました");
        }

        // テスト出現のときだけ、エディタの Console に状態を出す（ビルドには含まれない）
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        void TestLog(string message)
        {
            if (m_Forced)
                Debug.Log("[EmojiOne Fallback] " + message);
        }

        static bool BothSticksPressed()
        {
            return StickPressed(XRNode.LeftHand) && StickPressed(XRNode.RightHand);
        }

        static bool StickPressed(XRNode node)
        {
            var device = InputDevices.GetDeviceAtXRNode(node);
            return device.isValid &&
                   device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxisClick, out bool pressed) &&
                   pressed;
        }

        static void Pulse(XRNode node)
        {
            var device = InputDevices.GetDeviceAtXRNode(node);
            if (device.isValid)
                device.SendHapticImpulse(0, 0.4f, 0.15f);
        }

        static bool TestKeyPressed()
        {
#if UNITY_EDITOR && ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.ctrlKey.isPressed && kb.shiftKey.isPressed && kb.f12Key.wasPressedThisFrame;
#else
            return false;
#endif
        }

        // ---- 霧の計算 ----

        // 今の霧（FocusFog の値と上書きの濃い方）
        float EffectiveDensity()
        {
            return Mathf.Max(RenderSettings.fogDensity, m_FogOverride);
        }

        // 距離 d の物が、霧越しにどれだけ見えるか（1 = くっきり, 0 = 完全に隠れる）
        static float Visibility(float density, float d)
        {
            if (!RenderSettings.fog)
                return 1f;

            switch (RenderSettings.fogMode)
            {
                case FogMode.Linear:
                    float start = RenderSettings.fogStartDistance;
                    float end = RenderSettings.fogEndDistance;
                    return end > start ? Mathf.Clamp01((end - d) / (end - start)) : 1f;
                case FogMode.Exponential:
                    return Mathf.Exp(-density * d);
                default:
                    float x = density * d;
                    return Mathf.Exp(-x * x);
            }
        }

        // 距離 d で見え方が v になる距離・霧の濃さ（指数系の霧のみ。線形の霧では -1）
        static float DistanceForVisibility(float density, float v)
        {
            if (density <= 1e-5f)
                return float.PositiveInfinity;
            if (RenderSettings.fogMode == FogMode.Exponential)
                return -Mathf.Log(v) / density;
            if (RenderSettings.fogMode == FogMode.ExponentialSquared)
                return Mathf.Sqrt(-Mathf.Log(v)) / density;
            return -1f;
        }

        static float DensityForVisibility(float d, float v)
        {
            d = Mathf.Max(d, 0.5f);
            if (RenderSettings.fogMode == FogMode.Exponential)
                return -Mathf.Log(v) / d;
            return Mathf.Sqrt(-Mathf.Log(v)) / d;
        }

        // ---- 本体 ----

        // 指定秒数待つ。テスト操作が入ったら途中で切り上げる
        IEnumerator Wait(float seconds)
        {
            for (float t = 0f; t < seconds && !m_Forced; t += Time.deltaTime)
                yield return null;
        }

        IEnumerator Run()
        {
            yield return Wait(Random.Range(s_FirstDelayMin.x, s_FirstDelayMin.y) * 60f);

            while (true)
            {
                var cam = Camera.main;
                if (cam != null && RenderSettings.fog && RenderSettings.fogMode != FogMode.Linear)
                    yield return Watch(cam);
                else
                    TestLog($"出現できません: カメラ={(cam != null ? cam.name : "なし")}, 霧={RenderSettings.fog}, 霧の種類={RenderSettings.fogMode}");

                m_Forced = false;
                yield return Wait(Random.Range(s_IntervalMin.x, s_IntervalMin.y) * 60f);
            }
        }

        IEnumerator Watch(Camera cam)
        {
            // 1. 霧で「うっすら」になる距離が取れて、その場所が視界の外になるまで待つ
            Vector3 dir = Vector3.forward;
            float keepDistance = 0f;
            bool placed = false;
            for (float t = 0f; t < k_PlaceTimeout && cam != null; t += Time.deltaTime)
            {
                float targetVis = Random.Range(s_SpawnVisibility.x, s_SpawnVisibility.y);
                float d = DistanceForVisibility(EffectiveDensity(), targetVis);
                if (d >= s_DistanceLimit.x && d <= s_DistanceLimit.y)
                {
                    // テスト出現のときはプレイヤーの真右に固定する
                    dir = m_Forced ? RightOf(cam.transform) : PickDirection(cam.transform);
                    keepDistance = d;
                    PlaceAt(Ground(cam.transform.position) + dir * keepDistance, cam.transform);
                    if (!IsInView(cam))
                    {
                        placed = true;
                        break;
                    }
                }
                yield return null;
            }

            if (!placed)
            {
                TestLog($"出現場所が見つかりませんでした: 霧の濃さ={EffectiveDensity():F3}（霧が薄すぎるか、視界の外に置けません）");
                yield break;
            }

            Vector3 anchor = Ground(cam.transform.position) + dir * keepDistance;
            m_Figure.gameObject.SetActive(true);
            TestLog($"出現しました: 位置={m_Figure.position}, 距離={keepDistance:F1}m, 霧の濃さ={EffectiveDensity():F3}" +
                    "（Scene ビューで確認できます）");

            // 2. 見られるまで待つ。見られたら、次に視界から外れた時点で消える
            //    近づかれたら下がり、はっきり見えそうになったら霧を濃くして隠れてから消える
            float seen = 0f;
            float waited = 0f;
            while (cam != null)
            {
                Vector3 player = Ground(cam.transform.position);

                // 距離を保つ（近づかれたら下がり、離れられたら付いてくる）
                Vector3 offset = anchor - player;
                float dist = offset.magnitude;
                if (dist > 1e-3f)
                {
                    Vector3 wanted = player + offset / dist * keepDistance;
                    float speed = dist < keepDistance ? k_RetreatSpeed : k_FollowSpeed;
                    anchor = Vector3.MoveTowards(anchor, wanted, speed * Time.deltaTime);
                }

                PlaceAt(anchor, cam.transform);
                dist = Vector3.Distance(Ground(m_Figure.position), player);

                // はっきり見えそうなら、霧を濃くして隠す
                if (m_Thickening || Visibility(EffectiveDensity(), dist) > k_RevealLimit)
                {
                    if (!m_Thickening)
                    {
                        m_Thickening = true;
                        m_FogOverride = Mathf.Max(m_FogOverride, RenderSettings.fogDensity);
                    }

                    float target = DensityForVisibility(dist, k_HiddenVisibility * 0.5f);
                    float rate = Mathf.Max(target - RenderSettings.fogDensity, 0.05f) / k_ThickenTime;
                    m_FogOverride = Mathf.MoveTowards(m_FogOverride, target, rate * Time.deltaTime);

                    if (Visibility(EffectiveDensity(), dist) <= k_HiddenVisibility)
                        break;

                    yield return null;
                    continue;
                }

                bool inView = IsInView(cam);
                waited += Time.deltaTime;

                if (inView)
                {
                    seen += Time.deltaTime;
                }
                else if (seen >= k_SeenTime || waited >= k_MaxWaitUnseen)
                {
                    break;
                }

                yield return null;
            }

            m_Figure.gameObject.SetActive(false);
            TestLog(m_Thickening ? "霧で隠して消えました" :
                    seen >= k_SeenTime ? "見られたあと視界から外れたので消えました" :
                    "見られないまま時間切れで消えました");

            // 霧で隠した場合は、少し濃いまま待ってから戻し始める
            if (m_Thickening)
            {
                for (float t = 0f; t < 1.5f; t += Time.deltaTime)
                    yield return null;
                m_Thickening = false;
            }
        }

        static Vector3 Ground(Vector3 p)
        {
            return new Vector3(p.x, 0f, p.z);
        }

        static Vector3 RightOf(Transform viewer)
        {
            Vector3 right = Vector3.ProjectOnPlane(viewer.right, Vector3.up);
            return right.sqrMagnitude < 1e-4f ? Vector3.right : right.normalized;
        }

        static Vector3 PickDirection(Transform viewer)
        {
            Vector3 forward = Vector3.ProjectOnPlane(viewer.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f)
                forward = Vector3.forward;
            forward.Normalize();

            float side = Random.value < 0.5f ? -1f : 1f;
            float angle = Random.Range(s_AngleFromFront.x, s_AngleFromFront.y) * side;
            return Quaternion.AngleAxis(angle, Vector3.up) * forward;
        }

        // 顔の高さをプレイヤーの目の高さに合わせ、常にこちらを向く
        void PlaceAt(Vector3 anchor, Transform viewer)
        {
            float centerY = viewer.position.y - (k_FaceHeightRatio - 0.5f) * m_Height;
            m_Figure.position = new Vector3(anchor.x, centerY, anchor.z);

            Vector3 away = m_Figure.position - viewer.position;
            away.y = 0f;
            if (away.sqrMagnitude > 1e-4f)
                m_Figure.rotation = Quaternion.LookRotation(away, Vector3.up);
        }

        // イラストの四隅のどれかが（余白込みで）カメラの視界に入っているか
        bool IsInView(Camera cam)
        {
            Vector3 c = m_Figure.position;
            Vector3 r = m_Figure.right * (k_Width * 0.5f);
            Vector3 u = m_Figure.up * (m_Height * 0.5f);
            m_Corners[0] = c - r - u;
            m_Corners[1] = c + r - u;
            m_Corners[2] = c - r + u;
            m_Corners[3] = c + r + u;

            for (int i = 0; i < m_Corners.Length; i++)
            {
                Vector3 vp = cam.WorldToViewportPoint(m_Corners[i]);
                if (vp.z > 0f &&
                    vp.x > -k_ViewMargin && vp.x < 1f + k_ViewMargin &&
                    vp.y > -k_ViewMargin && vp.y < 1f + k_ViewMargin)
                    return true;
            }
            return false;
        }
    }
}
