using StarterAssets;
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

    [Header("自由移动区（可选）")]
    [Tooltip("勾选后：玩家进入该区域期间解锁 WASD（可在 XZ 平面全向自由移动）；" +
             "走出区域自动恢复默认的强制横向移动（只左右）。\n与上方相机模式互不影响，可以不填相机设置单独用作自由移动区。")]
    [SerializeField] private bool unlockWASDWhileInside = false;

    [Header("玩家标签")]
    [SerializeField] private string playerTag = "Player";

    private bool warnedMissingCamera;

    /// <summary>
    /// 重生/传送流程期间置 true，临时屏蔽“因传送被放入某相机触发区”而触发的相机切换
    /// （拉远 / 固定机位等）。玩家的自由移动解锁（unlockWASDWhileInside）不受影响。
    /// </summary>
    public static bool SuppressCameraSwitching { get; set; }

    [Header("主动检测（重生 / 传送后依然能正确切镜）")]
    [Tooltip("每隔多少秒主动检测一次“玩家此刻是否真的在区域内”（0 = 关闭，退回纯 Trigger 事件）。\n" +
             "开启后：玩家被重生 / 传送“放”进区域时（不会有 OnTriggerEnter 事件），相机同样会切到本区域设置。")]
    [SerializeField] private float presenceCheckInterval = 0.15f;

    private Collider zoneCollider;
    private ThirdPersonController cachedPlayer;
    private float presenceTimer;

    /// <summary>玩家当前是否在区域内（主动检测结果，不依赖 Trigger 事件是否送达）。</summary>
    private bool cameraInside;

    /// <summary>本区域的相机设置是否已经应用（避免每帧重复调用）。</summary>
    private bool cameraApplied;

    /// <summary>屏蔽期间没能应用的标记：解除屏蔽后补应用，且补应用时“立刻就位”而不是缓缓滑过去。</summary>
    private bool pendingSnapApply;

    private void Awake()
    {
        zoneCollider = GetComponent<Collider>();
    }

    private void OnEnable()
    {
        ResolveCameraController();
    }

    private void Update()
    {
        if (presenceCheckInterval <= 0f)
            return;

        // 屏蔽刚解除（重生黑幕结束 / 转场完成）→ 当帧立刻补应用，不等下一次巡检
        if (pendingSnapApply && !SuppressCameraSwitching)
        {
            presenceTimer = presenceCheckInterval;
            RefreshCameraPresence();
            return;
        }

        // 玩家在区域内，但机位已被重生 / 按 R 回溯等流程强制切走 → 当帧就补回来，不等巡检
        if (!SuppressCameraSwitching && cameraInside && NeedReapplyCameraMode())
        {
            presenceTimer = presenceCheckInterval;
            RefreshCameraPresence();
            return;
        }

        presenceTimer -= Time.deltaTime;
        if (presenceTimer > 0f)
            return;
        presenceTimer = presenceCheckInterval;

        RefreshCameraPresence();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(playerTag))
            return;

        // 自由移动解锁（进入 +1）
        if (unlockWASDWhileInside)
            ResolveTriggeredPlayer(other)?.EnableFreeMovement();

        // 相机：主动检测关闭时维持旧的“进区域立刻切镜”行为；
        // 开启时统一交给状态检测（这里只是立刻评估一次，保持响应速度不变）。
        if (presenceCheckInterval <= 0f)
        {
            ApplyCameraModeImmediately();
            return;
        }

        cameraInside = true;
        RefreshCameraPresence();
    }

    private void OnTriggerExit(Collider other)
    {
        if (!other.CompareTag(playerTag))
            return;

        // 自由移动恢复（离开 -1，无论相机是否要恢复都执行）
        if (unlockWASDWhileInside)
            ResolveTriggeredPlayer(other)?.DisableFreeMovement();

        if (presenceCheckInterval <= 0f)
        {
            if (restoreOnExit)
                SetNormalCamera();
            return;
        }

        cameraInside = false;
        RefreshCameraPresence();
    }

    // ------------------------------------------------------------ 相机：状态驱动

    /// <summary>
    /// 按“玩家此刻是否真的在区域内”来决定相机，而不是依赖 OnTriggerEnter / OnTriggerExit 事件。
    /// 关键作用：
    ///   · 重生 / 转场是把玩家“放”进区域的 —— 既不会产生 Enter 事件，期间还被
    ///     SuppressCameraSwitching 屏蔽，只靠事件会让相机一直停在普通跟拍；
    ///   · 重生 / 按 R 回溯会强制把相机切回普通跟拍（哪怕玩家一直站在本区域内、本区域的
    ///     机位此前已经应用过），所以“在区域内”时还要确认机位没被切回普通跟拍，被切走就补应用。
    /// </summary>
    private void RefreshCameraPresence()
    {
        bool inside = IsPlayerInsideVolume();

        if (inside && !cameraInside)
        {
            // ① 刚进入区域：机位需要重新应用
            cameraInside = true;
            cameraApplied = false;
        }
        else if (!inside)
        {
            // ② 玩家在区域外：统一在这里收尾 —— 不管是“离开事件”先把 cameraInside 置了 false，
            //    还是巡检先发现人已出去，只要本区域此前应用过机位，就恢复普通跟拍并清标记。
            //    （旧写法把“清 cameraApplied”放在判断之前，巡检先发现的那次就漏掉恢复，
            //      表现为“走出某个固定机位区域后镜头一直锁着”。）
            cameraInside = false;
            pendingSnapApply = false;

            if (cameraApplied)
            {
                cameraApplied = false;
                if (restoreOnExit)
                    SetNormalCamera();
            }
            return;
        }

        // ③ 在区域内、机位已就位、且没被重生 / R 回溯等外部流程切走 → 无事可做
        if (!NeedReapplyCameraMode())
        {
            pendingSnapApply = false;
            return;
        }

        // ④ 重生 / 转场流程中：先记下，等屏蔽解除后再补应用
        if (SuppressCameraSwitching)
        {
            pendingSnapApply = true;
            return;
        }

        bool snap = pendingSnapApply;
        pendingSnapApply = false;
        if (ApplyCameraMode(snap))
            cameraApplied = true;
    }

    /// <summary>
    /// 本区域的机位是否需要（补）应用：
    ///   · 从没应用过 —— 例如被重生 / 转场“放”进区域时不会有 Enter 事件；
    ///   · 已经应用过，但相机被重生 / 按 R 回溯这类流程强制切回了普通跟拍
    ///     （它们直接调 SetNormalMode，并不知道玩家正站在某个触发区里）。
    /// 只在“相机当前是普通跟拍、而本区域要的不是普通跟拍”时才算被抢走：
    /// 相机若正被出生镜头 / 其它镜头逻辑占用（固定点 / 拉远），不去抢回来，避免两套镜头逻辑打架。
    /// </summary>
    private bool NeedReapplyCameraMode()
    {
        if (!cameraApplied)
            return true;

        if (modeOnEnter == TriggerMode.Normal)
            return false;

        ResolveCameraController();
        if (cameraController == null)
            return true;

        return cameraController.GetCurrentMode() == CameraFollowController.CameraMode.Normal;
    }

    /// <summary>
    /// 重生 / 转场 / 按 R 回溯这类“把玩家放进去”的流程，在解除 SuppressCameraSwitching 之后调用：
    /// 立刻让所有触发区重新判定一次，保证落点所在区域当帧就把机位补上（并且直接就位，不慢慢滑过去），
    /// 不用等下一次巡检（也兼顾把巡检关掉的区域）。
    /// </summary>
    public static void ReevaluateAll()
    {
        CameraTriggerVolume[] volumes = FindObjectsOfType<CameraTriggerVolume>(true);
        for (int i = 0; i < volumes.Length; i++)
        {
            CameraTriggerVolume volume = volumes[i];
            if (volume == null || !volume.isActiveAndEnabled)
                continue;

            volume.pendingSnapApply = true;
            volume.presenceTimer = volume.presenceCheckInterval;
            volume.RefreshCameraPresence();
        }
    }

    /// <summary>主动检测玩家是否在区域内（用碰撞体包围盒判定）。</summary>
    private bool IsPlayerInsideVolume()
    {
        if (zoneCollider == null || !zoneCollider.enabled)
            return false;

        Transform player = ResolvePlayerTransform();
        if (player == null)
            return false;

        // 取角色躯干位置：CharacterController 的原点在脚底，贴地时可能落在盒子边缘之外一点点
        Vector3 point = player.position + Vector3.up * 0.5f;
        return zoneCollider.bounds.Contains(point);
    }

    /// <summary>运行时解析玩家：优先静态入口，其次全局查找（找到后缓存）。</summary>
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

    /// <summary>
    /// 把本区域的相机设置应用到主相机。
    /// snap = true 时直接到位（重生 / 转场后的补应用用）：避免画面恢复后镜头还在从上一个机位缓缓滑动。
    /// </summary>
    private bool ApplyCameraMode(bool snap)
    {
        ResolveCameraController();
        if (cameraController == null)
        {
            WarnMissingCamera();
            return false;
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
                if (fixedPoint == null)
                {
                    Debug.LogWarning("触发模式为 LockedPoint，但未指定 fixedPoint。", this);
                    return false;
                }
                cameraController.SetLockedPoint(fixedPoint, fixedFOV);
                break;
        }

        if (snap)
            cameraController.SnapToCurrentTarget();

        return true;
    }

    /// <summary>旧的纯事件路径：进区域且不在屏蔽中时立即切镜。</summary>
    private void ApplyCameraModeImmediately()
    {
        if (SuppressCameraSwitching)
            return;

        ApplyCameraMode(false);
    }

    private void SetNormalCamera()
    {
        ResolveCameraController();
        if (cameraController != null)
            cameraController.SetNormalMode();
    }

    private void WarnMissingCamera()
    {
        if (warnedMissingCamera)
            return;

        warnedMissingCamera = true;
        Debug.LogWarning("[CameraTriggerVolume] 找不到主相机（CameraFollowController）。" +
                         "请从 Bandeng_Test 场景开始完整游玩流程；若在 Level1 单独测试，需要先在场景里放一个带 CameraFollowController 的相机。", this);
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

    /// <summary>
    /// 运行时解析触发区的玩家：优先用触发碰撞体上的 ThirdPersonController；
    /// 再回退到 ThirdPersonController.Instance（正式流程里玩家同样从 Bandeng_Test 跨场景保留）。
    /// </summary>
    private ThirdPersonController ResolveTriggeredPlayer(Collider other)
    {
        ThirdPersonController player = other != null
            ? other.GetComponentInParent<ThirdPersonController>()
            : null;

        if (player == null)
            player = ThirdPersonController.Instance;

        if (player == null)
            player = FindObjectOfType<ThirdPersonController>();

        return player;
    }
}
