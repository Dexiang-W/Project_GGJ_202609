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

    [Header("性能优化（运行时生效）")]
    [Tooltip("涟漪相机每 N 帧才真正渲染一次（1=每帧；2=隔帧，RT 开销约减半）。涟漪本身很淡、扩散很慢，隔帧几乎无感")]
    [Range(1, 6)]
    public int renderEveryNFrames = 2;

    private static readonly int RippleTexID = Shader.PropertyToID("_RippleTex");
    private static readonly int RippleCamPosID = Shader.PropertyToID("_RippleCamPos");
    private static readonly int RippleCamSizeID = Shader.PropertyToID("_RippleCamSize");

    private Transform fallbackTarget;

    private void OnEnable()
    {
        EnsureCameraReference();
        EnsureTargetFallback();
        UpdateShaderGlobals();
    }

    private void LateUpdate()
    {
        EnsureCameraReference();
        EnsureTargetFallback();
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

        // 6. 渲染频率控制：隔 N 帧才真正渲一次，省 RT 清屏/粒子渲染开销
        if (Application.isPlaying)
        {
            bool wantRender = renderEveryNFrames <= 1 || (Time.frameCount % renderEveryNFrames) == 0;
            if (rippleCamera.enabled != wantRender)
                rippleCamera.enabled = wantRender;
        }
        else if (!rippleCamera.enabled)
        {
            rippleCamera.enabled = true; // 编辑模式随时可看
        }
    }

    /// <summary>
    /// 没手动拖 target 时，自动跟随场景里带 Player 标签的对象；
    /// 原目标被隐藏(SetActive false)/销毁后会自动让位，改跟当前激活的角色。
    /// </summary>
    private void EnsureTargetFallback()
    {
        if (target != null && !target.gameObject.activeInHierarchy)
        {
            target = null;          // 跟随的角色被隐藏 → 让位重找
            fallbackTarget = null;
        }

        if (target != null) return;

        if (fallbackTarget == null || !fallbackTarget.gameObject.activeInHierarchy)
        {
            GameObject player = GameObject.FindGameObjectWithTag("Player");
            fallbackTarget = player != null ? player.transform : null;
        }

        if (fallbackTarget != null)
            target = fallbackTarget;
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
