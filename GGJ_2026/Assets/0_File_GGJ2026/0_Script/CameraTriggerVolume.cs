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

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(playerTag) || cameraController == null)
            return;

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
        if (other.CompareTag(playerTag) && cameraController != null && restoreOnExit)
        {
            cameraController.SetNormalMode();
        }
    }
}
