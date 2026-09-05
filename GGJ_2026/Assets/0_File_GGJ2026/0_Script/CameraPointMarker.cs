using UnityEngine;

/// <summary>
/// 关卡内“相机镜头点”标记。
/// 挂在场景里的空物体上即可：Position / Rotation（空物体蓝轴 = 镜头看向的方向）就是镜头取景的位置和角度。
///
/// 用法：
///   · 玩家在 Level1 出生时：GameFlowController 会查找场景里勾选了 IsStartPoint 的镜头点，
///     黑幕淡出前把相机瞬移到这个点的位置/角度取景，定格 startCameraHoldDuration 后自动平滑切回“跟拍玩家”。
///   · 场景里摆了多个镜头点时，只有出生用的那一个需要勾选 IsStartPoint；
///     其它镜头点留着，以后可配合 CameraTriggerVolume 的固定点模式使用。
/// </summary>
public class CameraPointMarker : MonoBehaviour
{
    [Header("出生镜头点")]
    [Tooltip("勾选 = 玩家在本关卡生成时，相机先瞬移到这个镜头点取景。\n场景里若有多个镜头点，只有出生用的这个需要勾选。")]
    [SerializeField] private bool isStartPoint = true;

    [Header("镜头参数")]
    [Tooltip("镜头停留在此镜头点时的视野（Field of View）。切回跟拍后会自动恢复相机原本的 FOV。")]
    [SerializeField] private float cameraFOV = 60f;

    public bool IsStartPoint => isStartPoint;
    public float CameraFOV => cameraFOV;

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        Transform t = transform;
        Vector3 pos = t.position;
        Vector3 fwd = t.forward;

        // 出生镜头点用绿色突出显示，普通镜头点用蓝色
        Gizmos.color = isStartPoint
            ? new Color(0.2f, 0.95f, 0.5f, 0.95f)
            : new Color(0.3f, 0.7f, 1f, 0.95f);

        // 锚点小球
        Gizmos.DrawWireSphere(pos, 0.18f);

        // 朝向线（空物体蓝轴 = 镜头看向的方向）
        Gizmos.DrawLine(pos, pos + fwd * 0.8f);

        // 小锥形：表示“镜头从该点朝这个方向取景”
        Vector3 tip = pos + fwd * 1.15f;
        Vector3 back = pos + fwd * 0.12f;
        float half = 0.22f;
        Vector3 right = t.right * half;
        Vector3 up = t.up * half;

        Vector3[] quad =
        {
            back + right + up,
            back - right + up,
            back - right - up,
            back + right - up
        };

        for (int i = 0; i < 4; i++)
        {
            Gizmos.DrawLine(quad[i], tip);
            Gizmos.DrawLine(quad[i], quad[(i + 1) % 4]);
        }
    }
#endif
}
