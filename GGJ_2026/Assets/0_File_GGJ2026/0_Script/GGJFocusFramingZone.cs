using StarterAssets;
using UnityEngine;

/// <summary>
/// 取景跟随区（“全景框”）：玩家走进这个盒子后，相机**保持跟拍角色**（不是固定机位），
/// 只是“跟拍距离”随角色离花（focusTarget）多远而连续变化：
///
///   · 角色越靠近花 → 相机拉得越远，视野越大（全景）；
///   · 角色远离花 → 相机拉回，收到接近普通跟拍的距离（视野收小）；
///   · 角色走进花的 ±deadZoneRadius（默认 2 米）内 → 距离锁在最大值，死区里不再变化；
///   · 出框自动平滑恢复普通跟拍。
///
/// 全程平滑：取景系数自己 SmoothDamp 一层，相机自身还有位置 / FOV 缓动，进出区域还有一次机位过渡。
///
/// 用法：空物体挂本脚本 + 勾了 Is Trigger 的 Box Collider（这个盒子就是要调的“框”）。
/// 在 Scene 视图拖 Collider 的 Size / Center 即可。
/// Gizmo：青色线框 = 框，黄球 = 花（参考点），黄圈 = 死区，青圈 = 有效范围，橙线 = 最大跟拍距离。
/// </summary>
public class GGJFocusFramingZone : MonoBehaviour
{
    [Header("相机（留空 = 运行时自动找主相机）")]
    [Tooltip("主相机上的 CameraFollowController。主相机跨场景保留，Level 里拖不到，留空即可。")]
    [SerializeField] private CameraFollowController cameraController;

    [Header("参照目标（花）")]
    [Tooltip("测距的参照点：相机距离按“角色离它多近”来算（P_LightFlower）。留空则按下面的名字找。")]
    [SerializeField] private Transform focusTarget;
    [Tooltip("Inspector 引用为空时，运行时按这个名字在场景里查找目标")]
    [SerializeField] private string focusTargetName = "P_LightFlower";
    [Tooltip("参照点相对目标的偏移，一般不用动")]
    [SerializeField] private Vector3 focusOffset = Vector3.zero;

    [Header("跟拍距离：远离花（近） → 贴近花（远 / 全景）")]
    [Tooltip("角色远离花时的跟拍距离。建议 ≈ 普通跟拍距离（默认 8），进出区域才不会跳变")]
    [SerializeField] private float followDistanceMin = 8f;
    [Tooltip("角色贴近花时的最大跟拍距离 = 全景。嫌最大距离太远，就把这个调小（推荐 20~30）")]
    [SerializeField] private float followDistanceMax = 26f;
    [Tooltip("两端的视野角。默认一样大 = 只靠拉远 / 拉近改变取景；想用变焦加强效果就把右边调大")]
    [SerializeField] private float fovAtMinDistance = 60f;
    [SerializeField] private float fovAtMaxDistance = 60f;

    [Header("距离 → 跟拍距离 的映射")]
    [Tooltip("死区半径（米）：角色在花的这个半径内，跟拍距离锁在最大值不变")]
    [SerializeField] private float deadZoneRadius = 2f;
    [Tooltip("勾选 = 衰减距离自动取“框内离花最远的水平距离 − 死区”，走到框边缘正好收回最小跟拍距离")]
    [SerializeField] private bool autoFalloffFromBox = true;
    [Tooltip("不勾上面时手动指定：从死区边缘往外再走多远收回最小跟拍距离")]
    [SerializeField] private float falloffDistance = 8f;
    [Tooltip("按水平（XZ 平面）距离算，跳跃 / 高低差不会引起镜头变化")]
    [SerializeField] private bool useHorizontalDistance = true;

    [Header("机位方向（相机待在角色的哪一侧）")]
    [Tooltip("勾选 = 用相机自己的朝向反推，相机永远在角色正后方，角色始终在画面中心（推荐）")]
    [SerializeField] private bool useCameraForward = true;
    [Tooltip("不勾上面时手动指定：水平角 / 俯仰角（度）。俯仰角建议和相机 Follow Rotation Offset 的 X 一致（默认 20）")]
    [SerializeField] private float orbitYaw = 0f;
    [SerializeField] private float orbitPitch = 20f;

    [Header("平滑")]
    [Tooltip("跟拍距离跟随角色位置变化的平滑时间（秒），0 = 立刻跟随")]
    [SerializeField] private float framingSmoothTime = 0.5f;

    [Header("触发")]
    [SerializeField] private string playerTag = "Player";
    [Tooltip("玩家走出框后恢复普通跟拍")]
    [SerializeField] private bool restoreOnExit = true;
    [Tooltip("每隔多少秒主动检测一次玩家是否真的在框内（0 = 只用 Trigger 事件）")]
    [SerializeField] private float presenceCheckInterval = 0.15f;

    private Collider zoneCollider;
    private ThirdPersonController cachedPlayer;

    private bool inside;
    private bool applied;
    private float presenceTimer;

    /// <summary>当前取景系数：0 = 最小跟拍距离（远离花），1 = 最大跟拍距离（贴到花 / 死区内）。</summary>
    private float framingT;
    private float framingVelocity;

    private bool warnedMissingCamera;

    // ------------------------------------------------------------------ 生命周期

    private void Awake()
    {
        zoneCollider = GetComponent<Collider>();

        if (zoneCollider != null)
            zoneCollider.isTrigger = true;
    }

    private void OnEnable()
    {
        ResolveCameraController();
        ResolveFocusTarget();
    }

    private void OnDisable()
    {
        // 组件被关掉 / 场景卸载时，若相机正用着本区域的设置，交还给普通跟拍
        if (applied)
        {
            applied = false;
            inside = false;
            RestoreCamera();
        }
    }

    private void Update()
    {
        if (presenceCheckInterval > 0f)
        {
            presenceTimer -= Time.deltaTime;
            if (presenceTimer <= 0f)
            {
                presenceTimer = presenceCheckInterval;
                RefreshPresence();
            }
        }

        if (!inside)
            return;

        // 重生 / 转场屏蔽期间不抢镜头，等流程结束后的下一次巡检再补上
        if (CameraTriggerVolume.SuppressCameraSwitching)
        {
            applied = false;
            return;
        }

        // 刚进框 / 跟拍被重生、按 R 回溯这类流程切回普通跟拍 → 先过渡一次，之后每帧平滑跟随
        if (NeedReapply())
        {
            applied = true;
            UpdateFraming(0f, true);
        }

        UpdateFraming(Time.deltaTime);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(playerTag))
            return;

        ResolveTriggeredPlayer(other);

        if (presenceCheckInterval <= 0f)
        {
            if (CameraTriggerVolume.SuppressCameraSwitching)
                return;

            // 从边缘进入：跟拍距离直接从“角色当前位置应有的值”开始，不会先弹到最大再收回来
            inside = true;
            applied = false;
            framingT = ComputeTargetT();
            framingVelocity = 0f;
            UpdateFraming(0f, true);
            return;
        }

        RefreshPresence();
    }

    private void OnTriggerExit(Collider other)
    {
        if (!other.CompareTag(playerTag))
            return;

        if (presenceCheckInterval <= 0f)
        {
            inside = false;
            if (applied)
            {
                applied = false;
                if (restoreOnExit)
                    RestoreCamera();
            }
            return;
        }

        RefreshPresence();
    }

    // ------------------------------------------------------------------ 进 / 出框判定

    /// <summary>按“玩家此刻是否真的在框内”驱动相机（和 CameraTriggerVolume 同样的思路，重生 / 传送也能正确生效）。</summary>
    private void RefreshPresence()
    {
        bool isInside = IsPlayerInside();

        if (isInside && !inside)
        {
            inside = true;
            applied = false;
            framingT = ComputeTargetT();
            framingVelocity = 0f;
        }
        else if (!isInside)
        {
            inside = false;

            if (applied)
            {
                applied = false;
                if (restoreOnExit)
                    RestoreCamera();
            }
            return;
        }

        if (!inside)
            return;

        if (CameraTriggerVolume.SuppressCameraSwitching)
        {
            applied = false;
            return;
        }

        // 相机被外部流程抢回普通跟拍时补一次（过渡由相机自己 blend，平滑切过去）
        if (NeedReapply())
        {
            applied = true;
            UpdateFraming(0f, true);
        }
    }

    private bool IsPlayerInside()
    {
        if (zoneCollider == null || !zoneCollider.enabled)
            return false;

        Transform player = ResolvePlayerTransform();
        if (player == null)
            return false;

        // 取角色躯干位置：CharacterController 原点在脚底，贴地时可能落在盒子边缘之外一点点
        Vector3 point = player.position + Vector3.up * 0.5f;
        return zoneCollider.bounds.Contains(point);
    }

    // ------------------------------------------------------------------ 取景

    /// <summary>算目标取景系数：0 = 最小跟拍距离（远离花），1 = 最大跟拍距离（贴到花 / 死区内）。</summary>
    private float ComputeTargetT()
    {
        Transform player = ResolvePlayerTransform();
        if (player == null)
            return 0f;

        Vector3 toPlayer = player.position - GetReferencePoint();
        float distance = useHorizontalDistance
            ? new Vector2(toPlayer.x, toPlayer.z).magnitude
            : toPlayer.magnitude;

        float falloff = GetFalloffDistance();
        if (falloff <= 0.0001f)
            return distance <= deadZoneRadius ? 1f : 0f;

        // 死区内恒为 1（距离锁在最大），之后随距离线性衰减到 0
        return 1f - Mathf.Clamp01((distance - deadZoneRadius) / falloff);
    }

    /// <summary>死区边缘往外再走多远，收回到最小跟拍距离。</summary>
    private float GetFalloffDistance()
    {
        Collider zone = zoneCollider != null ? zoneCollider : GetComponent<Collider>();

        if (!autoFalloffFromBox || zone == null)
            return Mathf.Max(0f, falloffDistance);

        // 框内离参照点最远的水平距离（走到框边时刚好收到最小跟拍距离）
        Bounds bounds = zone.bounds;
        Vector3 reference = GetReferencePoint();
        float dx = Mathf.Max(Mathf.Abs(bounds.max.x - reference.x), Mathf.Abs(reference.x - bounds.min.x));
        float dz = Mathf.Max(Mathf.Abs(bounds.max.z - reference.z), Mathf.Abs(reference.z - bounds.min.z));

        return Mathf.Max(new Vector2(dx, dz).magnitude - deadZoneRadius, 0.001f);
    }

    /// <summary>每帧算好跟拍距离与 FOV，交给相机的“拉远跟随”（仍然跟着角色）。</summary>
    private void UpdateFraming(float deltaTime, bool immediate = false)
    {
        ResolveCameraController();
        if (cameraController == null)
        {
            WarnMissingCamera();
            return;
        }

        Transform target = ResolveFocusTarget();
        if (target == null)
        {
            Debug.LogWarning("[GGJFocusFramingZone] 没有参照目标：请在 Inspector 指定 focusTarget，或填对 focusTargetName。", this);
            return;
        }

        float targetT = ComputeTargetT();

        if (immediate || framingSmoothTime <= 0f)
        {
            framingT = targetT;
            framingVelocity = 0f;
        }
        else
        {
            framingT = Mathf.SmoothDamp(framingT, targetT, ref framingVelocity, framingSmoothTime);
        }

        float distance = Mathf.Lerp(followDistanceMin, followDistanceMax, framingT);
        float fov = Mathf.Lerp(fovAtMinDistance, fovAtMaxDistance, framingT);
        Vector3 offset = GetOffsetDirection() * distance;

        // 刚进框 / 被别的流程切走后：走 SetZoomedFollow，做一次完整的机位过渡；
        // 之后每帧只更新偏移和 FOV（不重启过渡），位置 / FOV 靠相机自己的缓动平滑收敛
        if (immediate || NeedReapply())
            cameraController.SetZoomedFollow(offset, fov);
        else
            cameraController.SetZoomParameters(offset, fov);
    }

    /// <summary>相机相对角色的偏移方向（单位向量）。</summary>
    private Vector3 GetOffsetDirection()
    {
        if (useCameraForward && cameraController != null)
        {
            // 相机自己的朝向反推：相机永远在角色正后方，角色始终在画面中心
            Vector3 back = -cameraController.transform.forward;
            if (back.sqrMagnitude > 0.0001f)
                return back.normalized;
        }

        return (Quaternion.Euler(orbitPitch, orbitYaw, 0f) * Vector3.back).normalized;
    }

    private void RestoreCamera()
    {
        ResolveCameraController();
        if (cameraController != null)
            cameraController.SetNormalMode();
    }

    /// <summary>
    /// 是否需要（重新）应用：从没应用过，或相机被重生 / 按 R 回溯这类流程切回了普通跟拍。
    /// 只在“相机当前是普通跟拍”时补，其它镜头（出生镜头 / 别的固定机位）不去抢，避免两套镜头逻辑打架。
    /// </summary>
    private bool NeedReapply()
    {
        if (!applied)
            return true;

        if (cameraController == null)
            return true;

        return cameraController.GetCurrentMode() == CameraFollowController.CameraMode.Normal;
    }

    // ------------------------------------------------------------------ 运行时解析

    /// <summary>测距参照点（花）的世界坐标。</summary>
    private Vector3 GetReferencePoint()
    {
        Transform target = ResolveFocusTarget();

        return (target != null ? target.position : transform.position) + focusOffset;
    }

    private Transform ResolveFocusTarget()
    {
        if (focusTarget != null)
            return focusTarget;

        if (!string.IsNullOrEmpty(focusTargetName))
        {
            GameObject found = GameObject.Find(focusTargetName);
            if (found != null)
                focusTarget = found.transform;
        }

        return focusTarget;
    }

    private Transform ResolvePlayerTransform()
    {
        if (cachedPlayer == null)
        {
            cachedPlayer = ThirdPersonController.Instance != null
                ? ThirdPersonController.Instance
                : FindObjectOfType<ThirdPersonController>();
        }

        return cachedPlayer != null ? cachedPlayer.transform : null;
    }

    private ThirdPersonController ResolveTriggeredPlayer(Collider other)
    {
        ThirdPersonController player = other != null
            ? other.GetComponentInParent<ThirdPersonController>()
            : null;

        if (player == null)
            player = ThirdPersonController.Instance;

        if (player == null)
            player = FindObjectOfType<ThirdPersonController>();

        if (player != null)
            cachedPlayer = player;

        return player;
    }

    private void ResolveCameraController()
    {
        if (cameraController != null)
            return;

        if (CameraFollowController.Instance != null)
        {
            cameraController = CameraFollowController.Instance;
            return;
        }

        cameraController = FindObjectOfType<CameraFollowController>();
    }

    private void WarnMissingCamera()
    {
        if (warnedMissingCamera)
            return;

        warnedMissingCamera = true;
        Debug.LogWarning("[GGJFocusFramingZone] 找不到主相机（CameraFollowController）。" +
                         "请从 Bandeng_Test 场景开始完整游玩流程。", this);
    }

    // ------------------------------------------------------------------ Gizmo

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        BoxCollider box = GetComponent<BoxCollider>();
        if (box == null)
            return;

        // 全景框
        Gizmos.color = new Color(0.2f, 1f, 0.85f, 0.85f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(box.center, box.size);
        Gizmos.matrix = Matrix4x4.identity;

        Transform target = ResolveFocusTargetForGizmo();
        if (target == null)
            return;

        Vector3 reference = target.position + focusOffset;

        // 参照点（花）
        Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.95f);
        Gizmos.DrawSphere(reference, 0.22f);

        // 死区：这个圈内跟拍距离锁在最大
        Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.7f);
        DrawWireCircleXZ(reference, deadZoneRadius);

        // 有效范围：走到这个圈 → 收回到最小跟拍距离
        Gizmos.color = new Color(0.2f, 1f, 0.85f, 0.55f);
        DrawWireCircleXZ(reference, deadZoneRadius + GetFalloffDistance());

        // 跟拍距离示意：从角色出发，最小 / 最大机位
        Transform player = ResolvePlayerForGizmo();
        if (player == null)
            return;

        Vector3 direction = (Quaternion.Euler(orbitPitch, orbitYaw, 0f) * Vector3.back).normalized;
        Vector3 maxPos = player.position + direction * followDistanceMax;
        Vector3 minPos = player.position + direction * followDistanceMin;

        Gizmos.color = new Color(1f, 0.6f, 0.2f, 0.9f);
        Gizmos.DrawLine(player.position, maxPos);
        Gizmos.DrawWireSphere(maxPos, 0.35f);

        Gizmos.color = new Color(1f, 1f, 1f, 0.35f);
        Gizmos.DrawLine(player.position, minPos);
        Gizmos.DrawWireSphere(minPos, 0.25f);
    }

    private Transform ResolveFocusTargetForGizmo()
    {
        if (focusTarget != null)
            return focusTarget;

        if (string.IsNullOrEmpty(focusTargetName))
            return null;

        GameObject found = GameObject.Find(focusTargetName);
        return found != null ? found.transform : null;
    }

    private Transform ResolvePlayerForGizmo()
    {
        if (Application.isPlaying)
            return ResolvePlayerTransform();

        try
        {
            GameObject player = GameObject.FindWithTag(playerTag);
            return player != null ? player.transform : null;
        }
        catch
        {
            // 场景里没有这个 tag（例如单独编辑 Level 场景）：跳过机位示意
            return null;
        }
    }

    private static void DrawWireCircleXZ(Vector3 center, float radius)
    {
        if (radius <= 0f)
            return;

        int segments = 48;
        Vector3 prev = center + new Vector3(radius, 0f, 0f);

        for (int i = 1; i <= segments; i++)
        {
            float angle = (float)i / segments * Mathf.PI * 2f;
            Vector3 next = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }
#endif
}
