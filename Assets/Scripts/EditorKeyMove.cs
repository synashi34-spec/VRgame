using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;

public class EditorKeyMove : MonoBehaviour
{
    [Header("パソコンのキーで動かす（テスト用）")]
    public Transform cameraTransform;   // Main Camera
    public float moveSpeed = 2f;        // 移動の速さ（m/秒）
    public float turnSpeed = 90f;       // 回転の速さ（度/秒）

    void Update()
    {
        // エディタ以外（実機のビルド）では何もしない
        if (!Application.isEditor) return;

        // ヘッドセットが接続されているときは何もしない
        if (XRSettings.isDeviceActive) return;

        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        float h = 0f;
        float v = 0f;
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed) v += 1f;
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed) v -= 1f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) h += 1f;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) h -= 1f;

        float turn = 0f;
        if (kb.eKey.isPressed) turn += 1f;
        if (kb.qKey.isPressed) turn -= 1f;

        transform.Rotate(0f, turn * turnSpeed * Time.deltaTime, 0f);

        Transform basis = cameraTransform != null ? cameraTransform : transform;
        Vector3 forward = basis.forward;
        forward.y = 0f;
        forward.Normalize();
        Vector3 right = basis.right;
        right.y = 0f;
        right.Normalize();

        transform.position += (forward * v + right * h) * moveSpeed * Time.deltaTime;
    }
}