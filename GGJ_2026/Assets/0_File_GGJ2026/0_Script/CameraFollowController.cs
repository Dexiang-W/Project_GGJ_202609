using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CameraFollowController : MonoBehaviour
{
    public enum CameraMode
    {
        Normal,
        ZoomedFollow,
        LockedPoint
    }

    /// <summary>
    /// 运行时主相机入口。主相机在 Bandeng_Test 场景里被 DontDestroyOnLoad 保留，
    /// 其他场景（如 Level1）的脚本无法在 Inspector 里直接拖它，所以提供静态引用供运行时自动查找。
    /// </summary>
    public static CameraFollowController Instance { get; private set; }

    [Header("基础跟随设置")]
    [SerializeField] private Transform followTarget;
    [SerializeField] private Vector3 offset = new Vector3(0, 5, -10);
    [SerializeField] private float followSmoothSpeed = 0.125f;

    [Header("拉远跟随设置")]
    [SerializeField] private Vector3 zoomOffset = new Vector3(0, 8, -20);
    [SerializeField] private float zoomFOV = 60f;

    [Header("固定点模式设置")]
    [SerializeField] private Transform fixedPoint;
    [SerializeField] private float fixedFOV = 60f;

    [Header("过渡设置")]
    [Tooltip("相机在 跟拍 / 拉远 / 固定机位 之间切换机位（CameraTriggerVolume 触发）时，" +
             "镜头整体移动 + 旋转 + 视野一起缓慢过渡的时长（秒）。\n越大越缓慢舒缓（建议 0.8~3）；设为 0 则保持旧的即时切换手感。")]
    [SerializeField] private float modeSwitchBlendSeconds = 1.2f;
    [Tooltip("非过渡状态下旋转 / FOV 收敛的速度（值越大转得越快）")]
    [SerializeField] private float transitionSpeed = 2f;

    private Camera cam;
    private Vector3 currentVelocity = Vector3.zero;
    private float targetFOV;
    private float initialFOV;
    private Quaternion initialRotation;
    private Quaternion targetRotation;

    private CameraMode currentMode = CameraMode.Normal;

    // 用于拉远和固定点的参数
    private Vector3 pendingOffset;
    private float pendingFOV;
    private Transform pendingFixedPoint;

    // 机位切换“缓慢过渡”的混合状态（切换模式 / SetNormalModeWithBlend 触发）
    private float blendTimer;
    private float blendDuration;
    private float blendStartSmoothTime;
    private Quaternion blendStartRotation;
    private float blendStartFOV;

    private void Awake()
    {
        cam = GetComponent<Camera>();
        if (cam == null)
        {
            Debug.LogError("CameraFollowController: 需要 Camera 组件！");
            return;
        }

        if (Instance == null)
            Instance = this;

        initialFOV = cam.fieldOfView;
        targetFOV = initialFOV;
        initialRotation = transform.rotation;
        targetRotation = initialRotation;
        pendingOffset = offset;
    }

    private void LateUpdate()
    {
        Vector3 targetPos = transform.position;
        Quaternion targetRot = targetRotation;
        float modeDefaultSmooth = followSmoothSpeed;

        switch (currentMode)
        {
            case CameraMode.Normal:
                if (followTarget != null)
                    targetPos = followTarget.position + offset;
                else
                    targetPos = transform.position;
                targetFOV = initialFOV;
                targetRot = initialRotation;   // 恢复初始旋转
                break;

            case CameraMode.ZoomedFollow:
                if (followTarget != null)
                    targetPos = followTarget.position + pendingOffset;
                else
                    targetPos = transform.position;
                targetFOV = pendingFOV;
                targetRot = initialRotation;   // 拉远时也保持初始旋转
                break;

            case CameraMode.LockedPoint:
                if (pendingFixedPoint != null)
                {
                    targetPos = pendingFixedPoint.position;
                    targetRot = pendingFixedPoint.rotation;
                    targetFOV = pendingFOV;
                }
                else
                {
                    targetPos = transform.position;
                }
                modeDefaultSmooth = 1f / transitionSpeed;
                break;
        }

        bool blending = blendTimer > 0f;
        float progress = 1f;
        float eased = 1f;

        if (blending)
        {
            // 0 → 1 的过渡进度，结束前做平滑的 ease（先慢后快再慢），避免刹停生硬
            progress = 1f - Mathf.Clamp01(blendTimer / Mathf.Max(blendDuration, 0.0001f));
            eased = progress * progress * (3f - 2f * progress);
        }

        // —— 位置 ——
        // 过渡期：平滑时间从较大的起始值逐渐衰减到该模式默认的平滑时间，镜头会缓慢“滑”到目标机位
        // 非过渡期：直接用该模式的默认平滑时间
        float posSmooth = modeDefaultSmooth;
        if (blending)
        {
            posSmooth = Mathf.Lerp(blendStartSmoothTime, modeDefaultSmooth, eased);
        }

        transform.position = Vector3.SmoothDamp(transform.position, targetPos, ref currentVelocity, posSmooth);

        // —— 旋转 ——
        // 过渡期：从切换那一刻的姿态向目标姿态做一次匀速而缓和的 Slerp（可预测的总时长）
        // 非过渡期：指数式逼近（维持原有手感）
        if (blending)
        {
            transform.rotation = Quaternion.Slerp(blendStartRotation, targetRot, eased);
        }
        else
        {
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * transitionSpeed);
        }

        // —— FOV ——
        if (Mathf.Abs(cam.fieldOfView - targetFOV) > 0.01f)
        {
            if (blending)
            {
                cam.fieldOfView = Mathf.Lerp(blendStartFOV, targetFOV, eased);
            }
            else
            {
                cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, targetFOV, Time.deltaTime * transitionSpeed);
            }
        }

        if (blending)
            blendTimer = Mathf.Max(0f, blendTimer - Time.deltaTime);
    }

    // --- 公共方法 ---

    /// <summary>切回“跟拍玩家”，过渡时长用组件上的 modeSwitchBlendSeconds。</summary>
    public void SetNormalMode()
    {
        CameraMode previous = currentMode;
        currentMode = CameraMode.Normal;
        targetFOV = initialFOV;
        targetRotation = initialRotation;   // 确保旋转回到初始

        if (previous != CameraMode.Normal)
            BeginModeBlend(modeSwitchBlendSeconds);
    }

    /// <summary>
    /// 用指定的时长切回“跟拍玩家”（只影响这一次过渡）。
    /// 用于出生镜头点 → 跟拍这类需要单独控制时长的镜头语言；传入 0 则相当于普通的 SetNormalMode()。
    /// </summary>
    public void SetNormalModeWithBlend(float durationSeconds)
    {
        CameraMode previous = currentMode;
        currentMode = CameraMode.Normal;
        targetFOV = initialFOV;
        targetRotation = initialRotation;

        if (previous != CameraMode.Normal)
            BeginModeBlend(durationSeconds);
    }

    /// <summary>切换到“拉远跟随”（仍跟着玩家，但偏移更大），过渡时长用组件上的 modeSwitchBlendSeconds。</summary>
    public void SetZoomedFollow(Vector3 newOffset, float newFOV)
    {
        CameraMode previous = currentMode;
        bool offsetChanged = pendingOffset != newOffset;

        pendingOffset = newOffset;
        pendingFOV = newFOV;
        currentMode = CameraMode.ZoomedFollow;
        targetRotation = initialRotation;   // 拉远时也保持初始旋转

        if (previous != CameraMode.ZoomedFollow || offsetChanged)
            BeginModeBlend(modeSwitchBlendSeconds);
    }

    /// <summary>锁定到固定镜头点，过渡时长用组件上的 modeSwitchBlendSeconds。</summary>
    public void SetLockedPoint(Transform point, float fov)
    {
        CameraMode previous = currentMode;
        Transform previousPoint = pendingFixedPoint;

        pendingFixedPoint = point;
        pendingFOV = fov;
        currentMode = CameraMode.LockedPoint;
        if (point != null)
            targetRotation = point.rotation;  // 固定点旋转
        else
            targetRotation = initialRotation;

        if (previous != CameraMode.LockedPoint || previousPoint != point)
            BeginModeBlend(modeSwitchBlendSeconds);
    }

    public void SetFixedPoint(Transform point)
    {
        pendingFixedPoint = point;
    }

    public void SetZoomParameters(Vector3 offset, float fov)
    {
        pendingOffset = offset;
        pendingFOV = fov;
    }

    public CameraMode GetCurrentMode() => currentMode;
    public bool IsLocked => currentMode == CameraMode.LockedPoint;

    /// <summary>
    /// 开始一次机位过渡：记录起点姿态，让位置 / 旋转 / FOV 在 seconds 内一起缓动到新机位。
    /// seconds ≤ 0 表示不启用缓慢过渡（维持旧手感）。
    /// </summary>
    private void BeginModeBlend(float seconds)
    {
        seconds = Mathf.Max(0f, seconds);

        blendDuration = seconds;
        blendTimer = seconds;
        blendStartSmoothTime = Mathf.Max(followSmoothSpeed, seconds * 0.35f);
        blendStartRotation = transform.rotation;
        blendStartFOV = cam != null ? cam.fieldOfView : initialFOV;
    }

    /// <summary>
    /// 立刻把相机放到当前模式对应的目标位置，并清空平滑速度与进行中的过渡。
    /// 用于“循环传送 / 掉出世界拉回 / 出生镜头直接就位”这类瞬移场景，
    /// 避免 SmoothDamp 残留速度或未完的过渡把画面甩飞。
    /// </summary>
    public void SnapToCurrentTarget()
    {
        currentVelocity = Vector3.zero;
        blendTimer = 0f;   // 终止未完成的机位过渡

        Vector3 desired = transform.position;

        switch (currentMode)
        {
            case CameraMode.Normal:
                if (followTarget != null)
                    desired = followTarget.position + offset;
                break;
            case CameraMode.ZoomedFollow:
                if (followTarget != null)
                    desired = followTarget.position + pendingOffset;
                break;
            case CameraMode.LockedPoint:
                if (pendingFixedPoint != null)
                    desired = pendingFixedPoint.position;
                break;
        }

        transform.position = desired;
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }
}
