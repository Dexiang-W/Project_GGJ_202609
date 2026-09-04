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

    private void Awake()
    {
        cam = GetComponent<Camera>();
        if (cam == null)
        {
            Debug.LogError("CameraFollowController: 需要 Camera 组件！");
            return;
        }

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
                targetRot = initialRotation;   // 恢复初始旋转
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
                break;
        }

        // 平滑位置
        float posSmooth = (currentMode == CameraMode.Normal || currentMode == CameraMode.ZoomedFollow)
                            ? followSmoothSpeed
                            : 1f / transitionSpeed;
        transform.position = Vector3.SmoothDamp(transform.position, targetPos, ref currentVelocity, posSmooth);

        // 平滑旋转（所有模式下都进行插值，目标是 targetRot）
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * transitionSpeed);

        // 平滑 FOV
        if (Mathf.Abs(cam.fieldOfView - targetFOV) > 0.01f)
        {
            cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, targetFOV, Time.deltaTime * transitionSpeed);
        }
    }

    // --- 公共方法 ---

    public void SetNormalMode()
    {
        currentMode = CameraMode.Normal;
        targetFOV = initialFOV;
        targetRotation = initialRotation;   // 确保旋转回到初始
    }

    public void SetZoomedFollow(Vector3 newOffset, float newFOV)
    {
        pendingOffset = newOffset;
        pendingFOV = newFOV;
        currentMode = CameraMode.ZoomedFollow;
        targetRotation = initialRotation;   // 拉远时也保持初始旋转
    }

    public void SetLockedPoint(Transform point, float fov)
    {
        pendingFixedPoint = point;
        pendingFOV = fov;
        currentMode = CameraMode.LockedPoint;
        if (point != null)
            targetRotation = point.rotation;  // 固定点旋转
        else
            targetRotation = initialRotation;
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
}
