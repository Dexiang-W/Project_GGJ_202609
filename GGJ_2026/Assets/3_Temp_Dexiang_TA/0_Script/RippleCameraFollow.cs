using UnityEngine;

[ExecuteAlways]
public class RippleCameraFollow : MonoBehaviour
{
    [Header("相机设置")]
    public Camera rippleCamera;

    [Header("跟随目标 (例如玩家 Player)")]
    public Transform target;

    [Header("相机固定高度 (Y 轴)")]
    public float cameraHeight = 20f;

    [Header("跟随平滑度 (0 表示瞬间跟随，不平滑)")]
    [Range(0f, 1f)]
    public float smoothness = 0.1f;

    [Header("像素格对齐 (网格吸附，防止移动时纹理抖动/锯齿)")]
    public bool enableSnapToPixel = true;

    [Header("渲染纹理与 Shader 传递")]
    public RenderTexture rippleRT;

    private static readonly int RippleTexID = Shader.PropertyToID("_RippleTex");
    private static readonly int RippleCamPosID = Shader.PropertyToID("_RippleCamPos");
    private static readonly int RippleCamSizeID = Shader.PropertyToID("_RippleCamSize");

    private void OnEnable()
    {
        EnsureCameraReference();
        UpdateShaderGlobals();
    }

    private void LateUpdate()
    {
        EnsureCameraReference();
        if (rippleCamera == null || target == null) return;

        Transform camTransform = rippleCamera.transform;

        // 1. 计算目标位置
        Vector3 targetPosition = new Vector3(target.position.x, cameraHeight, target.position.z);

        // 2. 位置平滑插值
        Vector3 newPosition;
        if (smoothness > 0f && Application.isPlaying)
        {
            newPosition = Vector3.Lerp(camTransform.position, targetPosition, 1f - smoothness);
        }
        else
        {
            newPosition = targetPosition;
        }

        // 3. 像素/单位对齐（优先取 RT 的实际高度，防止 pixelHeight 为 0 导致除以零错误）
        if (enableSnapToPixel && rippleCamera.orthographic)
        {
            float texHeight = (rippleRT != null && rippleRT.height > 0) ? rippleRT.height : rippleCamera.pixelHeight;
            if (texHeight > 0)
            {
                float texelSize = (rippleCamera.orthographicSize * 2f) / texHeight;
                newPosition.x = Mathf.Round(newPosition.x / texelSize) * texelSize;
                newPosition.z = Mathf.Round(newPosition.z / texelSize) * texelSize;
            }
        }

        // 4. 应用位置与旋转
        camTransform.position = newPosition;
        camTransform.rotation = Quaternion.Euler(90f, 0f, 0f);

        // 5. 实时更新 Shader 全局参数
        UpdateShaderGlobals();
    }

    private void EnsureCameraReference()
    {
        if (rippleCamera == null)
        {
            rippleCamera = GetComponent<Camera>();
        }
    }

    private void UpdateShaderGlobals()
    {
        if (rippleCamera == null) return;

        if (rippleRT != null)
        {
            Shader.SetGlobalTexture(RippleTexID, rippleRT);
        }

        Shader.SetGlobalVector(RippleCamPosID, rippleCamera.transform.position);
        Shader.SetGlobalFloat(RippleCamSizeID, rippleCamera.orthographicSize);
    }
}