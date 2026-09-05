using System.Collections;
using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;

public class CameraTriggerVolume : MonoBehaviour
{
    public enum TriggerMode
    {
        Normal,          // 恢复普通跟随
        ZoomedFollow,    // 拉远跟随（仍跟随玩家）
        LockedPoint      // 锁定到固定点
    }

    [Header("目标相机")]
    [Tooltip("主相机（Bandeng_Test 场景）上的 CameraFollowController。\n" +
             "主相机跨场景保留（DontDestroyOnLoad），无法在 Inspector 里拖到 Level1 的物体上，**留空即可**：运行时会自动找到它。")]
    [SerializeField] private CameraFollowController cameraController;

    [Header("进入后的模式")]
    [SerializeField] private TriggerMode modeOnEnter = TriggerMode.ZoomedFollow;

    [Header("参数（仅当模式为 ZoomedFollow 时生效）")]
    [SerializeField] private Vector3 zoomOffset = new Vector3(0, 8, -20);
    [SerializeField] private float zoomFOV = 60f;

    [Header("参数（仅当模式为 LockedPoint 时生效）")]
    [SerializeField] private Transform fixedPoint;
    [SerializeField] private float fixedFOV = 60f;

    [Header("离开后行为")]
    [SerializeField] private bool restoreOnExit = false;

    [Header("玩家标签")]
    [SerializeField] private string playerTag = "Player";

    private bool warnedMissingCamera;

    private void OnEnable()
    {
        ResolveCameraController();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(playerTag))
            return;

        ResolveCameraController();
        if (cameraController == null)
        {
            if (!warnedMissingCamera)
            {
                warnedMissingCamera = true;
                Debug.LogWarning("[CameraTriggerVolume] 找不到主相机（CameraFollowController）。" +
                                 "请从 Bandeng_Test 场景开始完整游玩流程；若在 Level1 单独测试，需要先在场景里放一个带 CameraFollowController 的相机。", this);
            }
            return;
        }

        switch (modeOnEnter)
        {
            case TriggerMode.Normal:
                cameraController.SetNormalMode();
                break;

            case TriggerMode.ZoomedFollow:
                cameraController.SetZoomedFollow(zoomOffset, zoomFOV);
                break;

            case TriggerMode.LockedPoint:
                if (fixedPoint != null)
                    cameraController.SetLockedPoint(fixedPoint, fixedFOV);
                else
                    Debug.LogWarning("触发模式为 LockedPoint，但未指定 fixedPoint。");
                break;
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (!other.CompareTag(playerTag) || !restoreOnExit)
            return;

        ResolveCameraController();
        if (cameraController != null)
        {
            cameraController.SetNormalMode();
        }
    }

    /// <summary>
    /// 运行时自动解析主相机：手动拖了引用就用拖的；
    /// 没拖（Level1 无法直接拖到跨场景保留的主相机）则通过 CameraFollowController.Instance 获取。
    /// </summary>
    private void ResolveCameraController()
    {
        if (cameraController != null)
            return;

        if (CameraFollowController.Instance != null)
        {
            cameraController = CameraFollowController.Instance;
            return;
        }

        // 兜底：某些测试场景里相机没走 Instance 注册，再全局找一次
        cameraController = FindObjectOfType<CameraFollowController>();
    }
}
