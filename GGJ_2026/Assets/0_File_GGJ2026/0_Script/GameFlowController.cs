using System.Collections;
using System.Collections.Generic;
using StarterAssets;
using UnityEngine;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// 开始界面 → 正式游戏的整段流程控制（双场景方案）：
///
/// Bandeng_Test（标题场景）：
///   Attract   ：画面灰屏，玩家持续向右跑动（到达终点自动循环回起点），
///                屏幕显示游戏名 / Click To Start。点击画面任意处 → 开始。
///   Starting  ：恢复彩色 + 文字淡出 → 角色缓慢停下 → 黑屏。
///
/// Level1（正式游戏场景）：
///   Spawning  ：加载 Level1 → 玩家在 SpawnPoint 生成（简易弹出/生成动画）→ 走出
///   Playing   ：交还玩家控制，正式开始游戏。
/// </summary>
[DisallowMultipleComponent]
public class GameFlowController : MonoBehaviour
{
    public enum GameFlowState
    {
        Attract,
        MovingToFocus,   // 点击后先跑到当前/下一张原画中心
        Starting,
        Spawning,
        Playing
    }

    [Header("引用（留空会自动在场景中查找）")]
    [SerializeField] private ThirdPersonController playerController;
    [SerializeField] private Camera mainCamera;
    [SerializeField] private GrayscaleController grayscaleController;
    [SerializeField] private TitleScreenUI titleUI;
    [Tooltip("当前生效的出生点（进 Level1 后自动按 spawnObjectName 重新查找）")]
    [SerializeField] private Transform spawnPoint;

    [Header("正式游戏场景（点击屏幕后切换过去）")]
    [Tooltip("黑屏后加载的正式游戏场景名（必须已加入 Build Settings）")]
    [SerializeField] private string gameplaySceneName = "Level1";
    [Tooltip("正式场景里玩家的出生点物体名（Level1 场景中命名为 SpawnPoint）")]
    [SerializeField] private string spawnObjectName = "SpawnPoint";

    [Header("待替换资产 —— 标题阶段循环播放的 3D 背景")]
    [Tooltip("循环 3D 场景的根节点：做好后拖进来即可；留空则直接循环播放当前场景里的世界")]
    [SerializeField] private GameObject attractSceneRoot;
    [Tooltip("循环场景自带角色动画时，可隐藏玩家本体（标题阶段看不到玩家）")]
    [SerializeField] private bool hidePlayerDuringAttract = false;

    [Header("标题循环跑动（点击前）")]
    [Tooltip("开启后玩家跑到终点自动回到起点，无限循环跑动")]
    [SerializeField] private bool attractLoop = true;
    [Tooltip("没有 Loop_Start / Loop_End 标记物体时的循环长度（米）")]
    [SerializeField] private float attractLoopLength = 40f;
    [Tooltip("(可选) 循环起点标记物体名；与 Loop_End 同时存在时用它精确控制循环区间")]
    [SerializeField] private string loopStartObjectName = "Loop_Start";
    [Tooltip("(可选) 循环终点标记物体名")]
    [SerializeField] private string loopEndObjectName = "Loop_End";
    [Tooltip("标题阶段自动停用场景中的相机触发区(CameraTriggerVolume)，防止它在循环边界处切换相机拉远视角、把循环中的角色甩出画面")]
    [SerializeField] private bool ignoreCameraTriggersDuringAttract = true;

    [Header("待替换资产 —— 生成动画 / 特效 / 音效")]
    [Tooltip("角色从方块里出现的动画（Animation Clip）。留空时使用简易的缩放弹出效果")]
    [SerializeField] private AnimationClip spawnAnimationClip;
    [Tooltip("生成时在生成点播放的特效预制体（可选）")]
    [SerializeField] private GameObject spawnEffectPrefab;
    [SerializeField] private AudioClip spawnSound;
    [SerializeField] private AudioSource audioSource;

    [Header("待替换资产 —— 音乐")]
    [SerializeField] private AudioSource musicSource;
    [SerializeField] private AudioClip attractMusic;
    [SerializeField] private AudioClip gameplayMusic;

    [Header("标题与正式关卡相机")]
    [Tooltip("标题阶段用 2D 无透视（正交），进入正式关卡后切回透视。这里指定正式关卡的主相机 FOV。")]
    [SerializeField] private float gameplayFieldOfView = 60f;

    [Header("标题阶段")]
    [Tooltip("1 = 持续向右跑动")]
    [SerializeField] private float attractRunAxis = 1f;
    [Tooltip("标题阶段是否加速跑动")]
    [SerializeField] private bool attractSprint = true;
    [Tooltip("进入标题画面时是否把相机瞬间吸到玩家身后的机位（避免开场镜头从远处猛甩过来）")]
    [SerializeField] private bool snapCameraOnAttractStart = true;

    [Header("标题跑动速度（独立于正式关卡，进关卡自动还原）")]
    [Tooltip("标题阶段角色的普通跑速（m/s）。默认 2 = 角色预制体原值，改这里只影响标题循环跑动")]
    [SerializeField] private float attractRunSpeed = 2.0f;
    [Tooltip("标题阶段角色的冲刺跑速（m/s）。勾选上面的「Attract Sprint」时用这个速度，默认 5.335 = 角色预制体原值")]
    [SerializeField] private float attractSprintSpeed = 5.335f;
    [Tooltip("整体速度倍率（1 = 保持上面两个数值）。想整体快速调快 / 调慢时用，例如 1.5 = 标题跑速快 50%")]
    [SerializeField] private float attractSpeedMultiplier = 1f;

    [Header("标题开场黑屏")]
    [Tooltip("标题场景一开始先盖一层全屏黑幕，等角色真正跑起来再淡入（用来藏住开场加载 / 卡顿）")]
    [SerializeField] private bool startWithBlackScreen = true;
    [Tooltip("开场黑幕淡入时长（秒）")]
    [SerializeField] private float attractFadeInDuration = 1.0f;
    [Tooltip("检测到角色开始跑动后，黑幕再多停留多久才淡入（秒）")]
    [SerializeField] private float attractIntroHoldSeconds = 0.4f;
    [Tooltip("角色从出生点移动多少米算「跑起来了」")]
    [SerializeField] private float attractIntroMoveThreshold = 0.6f;
    [Tooltip("开场黑幕最长等待时长（秒）；超时后无论角色是否跑起来都会淡入")]
    [SerializeField] private float attractIntroMaxWaitSeconds = 3f;

    [Header("标题点击后跑到原画中心")]
    [Tooltip("开启后，点击屏幕不会立刻开始游戏，而是先让玩家跑到当前/下一张原画切片的中心再进入开始序列。")]
    [SerializeField] private bool runToFocusPointOnStart = true;
    [Tooltip("原画切片上的跑停焦点，按从左到右顺序拖入。玩家会跑向自己前方最近的那个焦点。\n" +
             "想缩短跑动距离（不想只停在切片正中），用菜单 GGJ2026/标题/标题焦点工具（左中右）生成每片左/中/右多个焦点。")]
    [SerializeField] private Transform[] titleFocusPoints;
    [Tooltip("玩家到达中心点的 x 轴容差（米）")]
    [SerializeField] private float focusPointArriveDistance = 0.25f;
    [Tooltip("跑向中心点时的移动输入轴（1 = 向右）")]
    [SerializeField] private float runToFocusAxis = 1f;
    [Tooltip("接近中心点前多少米开始减速，避免冲过头")]
    [SerializeField] private float focusPointSlowDownDistance = 1.5f;

    [Header("过场时间（秒）")]
    [SerializeField] private float textFadeDuration = 0.6f;
    [SerializeField] private float colorFadeDuration = 1.0f;
    [SerializeField] private float stopDuration = 1.4f;
    [SerializeField] private float fadeToBlackDuration = 0.6f;
    [SerializeField] private float blackHoldDuration = 0.5f;

    [Header("生成与出场")]
    [SerializeField] private float fadeFromBlackDuration = 0.8f;
    [SerializeField] private float spawnPopDuration = 0.45f;
    [Tooltip("出场时向右行走的时间")]
    [SerializeField] private float walkOutDuration = 1.1f;
    [SerializeField] private float walkOutSpeedAxis = 1f;
    [Tooltip("生成瞬间相机相对生成点的机位（黑幕淡出期间会自动拉近到位）")]
    [SerializeField] private Vector3 spawnCameraOffset = new Vector3(0f, 2f, -4.5f);

    [Header("关卡出生镜头点")]
    [Tooltip("启用后：进正式关卡时若场景里摆有出生镜头点（挂 CameraPointMarker 且勾选 IsStartPoint 的空物体），" +
             "相机就先瞬移到该镜头点的位置/角度取景，定格一会后自动平滑切回跟拍玩家。\n" +
             "没摆镜头点时，回退用上面的 spawnCameraOffset 机位。")]
    [SerializeField] private bool useStartCameraPoint = true;
    [Tooltip("出生定格时长（秒）：玩家从黑幕淡出出现后，相机在出生镜头点上停留多久，再平滑切回跟拍玩家")]
    [SerializeField] private float startCameraHoldDuration = 0.6f;
    [Tooltip("定格结束后，相机从镜头点缓慢“追回/跟上”玩家的过渡时长（秒）。数值越大镜头回得越慢、越舒缓（建议 1~4 之间试）")]
    [SerializeField] private float resumeFollowBlendSeconds = 1.6f;

    [Header("生成落地（全程在黑幕里）")]
    [Tooltip("生成动画播完后，继续在黑幕里等待角色落到地面站稳的最长时长（秒）。生成点悬空时角色会自由落体，等它落地再亮屏，玩家就不会看到“从天上掉下来”")]
    [SerializeField] private float maxSpawnGroundWaitSeconds = 6f;
    [Tooltip("角色落地后黑幕再多停留一小会（秒）才淡出，避开落地瞬间的贴地修正 / 小弹跳")]
    [SerializeField] private float spawnGroundSettleSeconds = 0.25f;

    [Header("调试")]
    [SerializeField] private bool skipSpawnSequence = false;
    private StarterAssetsInputs playerInputs;
    private GameFlowState state = GameFlowState.Attract;
    private float autoRunAxis = 1f;
    private bool driveInput = true;
    private bool sequenceStarted;

    // 相机跟随组件（主相机上有 CameraFollowController 时，用它做瞬移/清速更平滑）
    private CameraFollowController cameraFollow;
    private CharacterController charController;
    private int attractWrapCount;
    private bool startCameraShotActive;

    // 标题循环用
    private Vector3 attractOrigin;
    private Vector3 attractCameraOffset;
    private float loopStartX;
    private float loopLengthX;

    // 生成弹出视觉缓存（先把模型压成 0 后仍需知道它的原大小）
    private Transform spawnVisualRoot;
    private Vector3 spawnVisualTargetScale;

    // 标题开场黑幕协程
    private Coroutine attractIntroRoutine;

    // 标题阶段临时改过的角色速度，进正式关卡时还原
    private float baseMoveSpeed;
    private float baseSprintSpeed;
    private bool baseSpeedsCaptured;

#if ENABLE_INPUT_SYSTEM
    private UnityEngine.InputSystem.PlayerInput playerInputComponent;
#endif

    public GameFlowState CurrentState => state;

    // 解析放到 Start 而不是 Awake，确保 TitleScreenUI / GrayscaleController 等
    // 组件的 Awake（注册 Instance、创建后处理 Volume）都已执行完毕
    private void Start()
    {
        ResolveReferences();
        EnterAttractState();
    }

    private void Update()
    {
        if (state == GameFlowState.Attract && !sequenceStarted)
        {
            if (WasStartPressed())
            {
                if (runToFocusPointOnStart)
                    StartCoroutine(MoveToFocusPointRoutine());
                else
                    StartCoroutine(StartGameSequence());
                return;
            }

            if (attractLoop)
                UpdateAttractLoop();
        }

        if (driveInput && playerInputs != null)
        {
            playerInputs.MoveInput(new Vector2(autoRunAxis, 0f));
            playerInputs.LookInput(Vector2.zero);
            playerInputs.JumpInput(false);
            bool canSprint = state == GameFlowState.Attract || state == GameFlowState.MovingToFocus;
            playerInputs.SprintInput(canSprint && attractSprint);
        }
    }

    /// <summary>手动触发「开始游戏」（UI 按钮 / 调试用）。</summary>
    [ContextMenu("开始游戏")]
    public void BeginGame()
    {
        if (state != GameFlowState.Attract || sequenceStarted)
            return;

        if (runToFocusPointOnStart)
            StartCoroutine(MoveToFocusPointRoutine());
        else
            StartCoroutine(StartGameSequence());
    }

    /// <summary>跳过标题/过场，直接加载正式场景生成玩家（调试用）。</summary>
    [ContextMenu("跳过标题，直接进入 Level1（调试用）")]
    public void SkipToGameplay()
    {
        if (sequenceStarted)
            return;

        sequenceStarted = true;
        StopAllCoroutines();
        StartCoroutine(SkipRoutine());
    }

    private void ResolveReferences()
    {
        if (playerController == null)
            playerController = FindObjectOfType<ThirdPersonController>();

        if (playerController != null)
        {
            playerInputs = playerController.GetComponent<StarterAssetsInputs>();
            charController = playerController.GetComponent<CharacterController>();
#if ENABLE_INPUT_SYSTEM
            playerInputComponent = playerController.GetComponent<UnityEngine.InputSystem.PlayerInput>();
#endif
        }
        else
        {
            Debug.LogWarning("[GameFlowController] 场景里没有找到 ThirdPersonController（玩家）。", this);
        }

        if (mainCamera == null)
            mainCamera = Camera.main;

        if (mainCamera != null)
            cameraFollow = mainCamera.GetComponent<CameraFollowController>();

        if (grayscaleController == null)
            grayscaleController = FindObjectOfType<GrayscaleController>();

        if (titleUI == null)
            titleUI = TitleScreenUI.Instance;

        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();
    }

    private void EnterAttractState()
    {
        state = GameFlowState.Attract;
        driveInput = true;
        autoRunAxis = attractRunAxis;

        // 标题阶段由本脚本接管角色输入，暂时关闭 PlayerInput 防止它覆盖自动跑动
        SetPlayerInputEnabled(false);

        if (attractSceneRoot != null)
            attractSceneRoot.SetActive(true);

        // 标题阶段停用残留/测试用的相机触发区，避免其在循环起点(触发区边界)反复切换相机拉远视角
        DisableCameraTriggersForAttract();

        if (hidePlayerDuringAttract && playerController != null)
            playerController.gameObject.SetActive(false);

        if (playerController != null)
        {
            attractOrigin = playerController.transform.position;
            attractCameraOffset = mainCamera != null
                ? mainCamera.transform.position - attractOrigin
                : Vector3.zero;

            SetupLoopBounds();

            // 开场直接把相机吸到玩家身后机位，避免从远处快速甩飞进场的观感
            if (snapCameraOnAttractStart && cameraFollow != null)
                cameraFollow.SnapToCurrentTarget();
        }

        if (grayscaleController != null)
            grayscaleController.SetGrayscale();

        // 标题 BGM：统一交给 AudioManager 按“关卡配乐表”播放 —— 表里加一行 Begin_Menu 即可配置
        // （musicClip 留空 = 标题静音）。只有在 AudioManager 没有可播的标题音乐时，
        // 才回退到本脚本自己的 attractMusic。
        if (!AudioManager.BeginTitleAudio())
            PlayMusic(attractMusic);

        // 标题阶段用独立速度（进正式关卡时会还原）
        ApplyAttractSpeeds();

        // 开场先盖黑幕：等角色真正跑起来再淡入，把开场加载 / 编译卡顿藏在黑幕里
        if (startWithBlackScreen && titleUI != null)
        {
            titleUI.SetBlackScreen(true);
            titleUI.SetTextVisible(false);
            attractIntroRoutine = StartCoroutine(AttractIntroRoutine());
        }
    }

    /// <summary>
    /// 开场黑幕：先全黑，等角色从出生点真正跑起来（移动超过阈值）后，
    /// 再把黑幕淡出、标题文字淡入。这样玩家看到的第一帧就已经是跑动中的画面。
    /// </summary>
    private IEnumerator AttractIntroRoutine()
    {
        Vector3 startPos = playerController != null ? playerController.transform.position : Vector3.zero;

        // 先过一帧，让首帧的加载尖峰过去
        yield return null;

        float deadline = Time.time + Mathf.Max(0.1f, attractIntroMaxWaitSeconds);
        float threshold = Mathf.Max(0.01f, attractIntroMoveThreshold);

        while (Time.time < deadline)
        {
            if (playerController == null)
                break;

            float moved = Mathf.Abs(playerController.transform.position.x - startPos.x);
            if (moved >= threshold)
                break;

            yield return null;
        }

        if (attractIntroHoldSeconds > 0f)
            yield return new WaitForSeconds(attractIntroHoldSeconds);

        if (titleUI != null)
        {
            StartCoroutine(titleUI.FadeFromBlackRoutine(attractFadeInDuration));
            StartCoroutine(titleUI.FadeInTextsRoutine(attractFadeInDuration));
        }

        attractIntroRoutine = null;
    }

    /// <summary>玩家在黑幕还没淡完就点了开始：立刻结束开场黑幕，避免和过场黑幕打架。</summary>
    private void StopAttractIntro()
    {
        if (attractIntroRoutine == null)
            return;

        StopCoroutine(attractIntroRoutine);
        attractIntroRoutine = null;

        if (titleUI != null)
        {
            titleUI.SetBlackScreen(false);
            titleUI.SetTextVisible(true);
        }
    }

    /// <summary>
    /// 标题阶段用独立的移动速度：直接写入「标题普通跑速 / 冲刺跑速（m/s）」× 倍率，
    /// 不依赖角色预制体自身的数值；进正式关卡时由 RestoreGameplaySpeeds() 还原回预制体原值。
    /// </summary>
    private void ApplyAttractSpeeds()
    {
        if (playerController == null)
            return;

        if (!baseSpeedsCaptured)
        {
            baseMoveSpeed = playerController.MoveSpeed;
            baseSprintSpeed = playerController.SprintSpeed;
            baseSpeedsCaptured = true;
        }

        float multiplier = Mathf.Max(0.01f, attractSpeedMultiplier);
        playerController.MoveSpeed = Mathf.Max(0.01f, attractRunSpeed) * multiplier;
        playerController.SprintSpeed = Mathf.Max(0.01f, attractSprintSpeed) * multiplier;
    }

    /// <summary>进入正式关卡前，把角色速度还原成原本的数值。</summary>
    private void RestoreGameplaySpeeds()
    {
        if (playerController == null || !baseSpeedsCaptured)
            return;

        playerController.MoveSpeed = baseMoveSpeed;
        playerController.SprintSpeed = baseSprintSpeed;
        baseSpeedsCaptured = false;
    }

    /// <summary>停用当前场景里所有相机触发区（CameraTriggerVolume）。标题循环需要相机恒定跟随，
    /// 若循环边界附近残留触发区，角色传送回起点时会反复触发“拉远视角”，导致角色瞬间出画。</summary>
    private void DisableCameraTriggersForAttract()
    {
        if (!ignoreCameraTriggersDuringAttract)
            return;

        var triggers = FindObjectsOfType<CameraTriggerVolume>();
        foreach (var trigger in triggers)
        {
            if (trigger == null || !trigger.enabled)
                continue;

            trigger.enabled = false;
            Debug.Log($"[GameFlowController] 标题阶段已停用相机触发区：{trigger.gameObject.name}", trigger);
        }
    }

    /// <summary>根据 Loop_Start/Loop_End 标记物体（可选）或出生点确定循环区间。</summary>
    private void SetupLoopBounds()
    {
        loopStartX = attractOrigin.x;
        loopLengthX = Mathf.Max(1f, attractLoopLength);

        GameObject startMarker = GameObject.Find(loopStartObjectName);
        GameObject endMarker = GameObject.Find(loopEndObjectName);

        if (startMarker != null && endMarker != null)
        {
            float a = startMarker.transform.position.x;
            float b = endMarker.transform.position.x;
            float length = Mathf.Abs(b - a);

            if (length > 0.5f)
            {
                loopStartX = Mathf.Min(a, b);
                loopLengthX = length;
            }
        }
    }

    /// <summary>标题阶段跑到终点后自动回到起点（近似无缝的循环跑动）。</summary>
    private void UpdateAttractLoop()
    {
        if (playerController == null)
            return;

        Transform player = playerController.transform;

        // 容错：标题阶段若掉出路面/世界（出生点高度以下较多），立刻拉回出生点并吸相机，
        // 避免相机长时间待在空区域造成“角色+场景全消失”。
        if (player.position.y < attractOrigin.y - 6f)
        {
            TeleportPlayerTo(player, attractOrigin);
            SnapCameraBehindPlayer();
            Debug.Log($"[GameFlow] 掉出世界拉回出生点：player 回 {attractOrigin}，相机 SnapToCurrentTarget。");
            return;
        }

        float traveled = player.position.x - loopStartX;

        // 兜底：正常到达终点即回卷，traveled 不会超过一个循环长。
        // 若异常情况跑出区间太多（例如某帧回卷没触发），直接拉回出生点，避免跑进无路面区域。
        if (traveled > loopLengthX + 5f)
        {
            TeleportPlayerTo(player, attractOrigin);
            SnapCameraBehindPlayer();
            Debug.Log($"[GameFlow] 超界兜底拉回出生点：traveled={traveled:F2} 超出一个循环长度，player→{attractOrigin}。");
            return;
        }

        if (traveled < loopLengthX)
            return;

        float shift = Mathf.Floor(traveled / loopLengthX) * loopLengthX;

        // 回卷：先安全位移角色（CharacterController 瞬移前先禁用，避免碰撞干扰），
        // 再把相机无条件吸回“角色身后的标准跟随机位”。
        // SnapToCurrentTarget 会同时清空 SmoothDamp 残留速度，保证循环前后机位严格一致，
        // 不依赖“相机整体平移”的几何连续性，也不会因相机滞后把角色甩出画面。
        TeleportPlayerBy(player, new Vector3(-shift, 0f, 0f));
        SnapCameraBehindPlayer();

        attractWrapCount++;
        Debug.Log($"[GameFlow] 循环回卷 #{attractWrapCount}：traveled={traveled:F2} shift={shift:F2}，" +
                  $"player→{player.position:F2} cam→{(mainCamera != null ? mainCamera.transform.position.ToString("F2") : "null")}，" +
                  $"cameraMode={(cameraFollow != null ? cameraFollow.GetCurrentMode() : CameraFollowController.CameraMode.Normal)}");
    }

    /// <summary>把角色沿某向量安全移动（移动前暂时禁用 CharacterController，避免瞬移穿透/抖动）。</summary>
    private void TeleportPlayerBy(Transform player, Vector3 delta)
    {
        if (charController != null) charController.enabled = false;
        player.position += delta;
        if (charController != null) charController.enabled = true;
    }

    /// <summary>把角色放到指定位置（移动前暂时禁用 CharacterController）。</summary>
    private void TeleportPlayerTo(Transform player, Vector3 position)
    {
        if (charController != null) charController.enabled = false;
        player.position = position;
        if (charController != null) charController.enabled = true;
    }

    /// <summary>把相机瞬间放到玩家身后的跟随机位（优先用 CameraFollowController，避免平滑甩飞）。</summary>
    private void SnapCameraBehindPlayer()
    {
        if (cameraFollow != null)
        {
            cameraFollow.SnapToCurrentTarget();
            return;
        }

        if (mainCamera == null || playerController == null)
            return;

        // 无跟随组件时，用记录过的偏移量手动对齐
        mainCamera.transform.position = playerController.transform.position + attractCameraOffset;
    }

    /// <summary>
    /// 点击后让玩家先跑到当前/下一张原画切片的中心，再开始游戏。
    /// 这样无论玩家在标题循环的哪个位置点击，都会先“跑一定距离”站到合适构图位置。
    /// </summary>
    private IEnumerator MoveToFocusPointRoutine()
    {
        sequenceStarted = true;
        state = GameFlowState.MovingToFocus;
        StopAttractIntro();

        // Inspector 没拖焦点时，也支持按场景里 TitleFocusPoint_1/2/... 的名字自动查找
        if (titleFocusPoints == null || titleFocusPoints.Length == 0)
            titleFocusPoints = FindTitleFocusPointsByName();

        Transform target = PickNextFocusPoint();
        if (target == null)
        {
            Debug.LogWarning("[GameFlowController] 未配置标题原画中心点（Title Focus Points），直接进入开始序列。", this);
            StartCoroutine(StartGameSequence());
            yield break;
        }

        driveInput = true;

        float timeout = 10f;
        float elapsed = 0f;
        while (elapsed < timeout)
        {
            if (playerController == null)
                break;

            float px = playerController.transform.position.x;
            float tx = target.position.x;
            float remaining = tx - px;

            if (Mathf.Abs(remaining) <= focusPointArriveDistance)
                break;

            // 接近目标前减速，防止冲过头
            float direction = Mathf.Sign(remaining);
            if (Mathf.Abs(remaining) < focusPointSlowDownDistance)
                autoRunAxis = direction * Mathf.Lerp(0.2f, 1f, Mathf.Abs(remaining) / focusPointSlowDownDistance);
            else
                autoRunAxis = direction * runToFocusAxis;

            elapsed += Time.deltaTime;
            yield return null;
        }

        // 到达目标点后停稳一小会，再进入开始序列
        autoRunAxis = 0f;
        if (playerInputs != null)
            playerInputs.MoveInput(Vector2.zero);

        yield return new WaitForSeconds(0.15f);

        StartCoroutine(StartGameSequence());
    }

    /// <summary>选择玩家前方（x 更大）最近的焦点中心；若已超过所有焦点，则选最后一个。</summary>
    private Transform PickNextFocusPoint()
    {
        if (titleFocusPoints == null || titleFocusPoints.Length == 0)
            return null;

        float px = playerController != null ? playerController.transform.position.x : float.MinValue;
        Transform chosen = null;
        foreach (var t in titleFocusPoints)
        {
            if (t == null)
                continue;
            if (t.position.x >= px - focusPointArriveDistance)
                return t;
            chosen = t;
        }
        return chosen;
    }

    /// <summary>按名字 TitleFocusPoint_1/2/... 在场景里查找焦点（作为 Inspector 没拖引用的兜底）。</summary>
    private Transform[] FindTitleFocusPointsByName()
    {
        var list = new List<Transform>();
        // 每张原画可能生成多个焦点（左/中/右），上限给宽松一些
        for (int i = 1; i <= 128; i++)
        {
            GameObject go = GameObject.Find($"TitleFocusPoint_{i}");
            if (go == null)
                break;
            list.Add(go.transform);
        }
        return list.ToArray();
    }

    private IEnumerator StartGameSequence()
    {
        sequenceStarted = true;
        state = GameFlowState.Starting;
        StopAttractIntro();

        // 角色已在标题阶段站定 → 还原正式关卡的移动速度
        RestoreGameplaySpeeds();

        if (titleUI != null)
            StartCoroutine(titleUI.FadeOutTextsRoutine(textFadeDuration));

        if (grayscaleController != null)
            StartCoroutine(grayscaleController.FadeToFullColorRoutine(colorFadeDuration));

        // 音乐统一由 AudioManager 按“关卡配乐表”接管（标题阶段已在播标题 BGM），
        // 这里不再用旧音乐源重复播放，避免双份音乐叠加；仅在 AudioManager 缺失时回退。
        if (AudioManager.Instance == null)
            PlayMusic(gameplayMusic);

        // 若刚从“跑到原画中心”状态过来，autoRunAxis 已经降到 0；否则从 attractRunAxis 开始减速。
        float runAxisAtStart = autoRunAxis;
        yield return RampAutoRun(runAxisAtStart, 0f, stopDuration);
        autoRunAxis = 0f;

        if (titleUI != null)
            yield return titleUI.FadeToBlackRoutine(fadeToBlackDuration);

        yield return new WaitForSeconds(blackHoldDuration);

        yield return RunTransitionToLevel(true);
    }

    /// <summary>
    /// 黑屏阶段把玩家/相机/UI/流程对象标记为跨场景保留，加载正式场景并完成生成。
    /// </summary>
    private IEnumerator RunTransitionToLevel(bool withCinematics)
    {
        state = GameFlowState.Spawning;

        // 不再驱动自动跑动
        driveInput = false;
        autoRunAxis = 0f;
        if (playerInputs != null)
        {
            playerInputs.MoveInput(Vector2.zero);
            playerInputs.SprintInput(false);
        }

        // 一进入正式关卡（画面还处于黑幕）就创建 HUD，但先隐藏正式游玩 UI，
        // 避免黑幕还没淡出就先看到左上角能量三格 / 耐力条；等关卡真正开始再显示。
        GameplayHUD.EnsureCreated();
        GameplayHUD.SetGameplayUiVisibleIfExists(false);

        if (attractSceneRoot != null)
            attractSceneRoot.SetActive(false);

        if (playerController != null)
        {
            // 跨场景保留玩家、相机、UI 黑幕与本流程控制器（含音乐 AudioSource）
            MakePersistent(gameObject);
            MakePersistent(playerController.gameObject);
            if (mainCamera != null) MakePersistent(mainCamera.gameObject);
            if (titleUI != null) MakePersistent(titleUI.gameObject);

            if (!Application.CanStreamedLevelBeLoaded(gameplaySceneName))
            {
                Debug.LogError($"[GameFlowController] 场景 {gameplaySceneName} 不在 Build Settings 中，" +
                               "无法切换场景，将退回在当前场景原地生成。", this);
                yield return FallbackSpawnInCurrentScene();
                yield break;
            }

            AsyncOperation load = SceneManager.LoadSceneAsync(gameplaySceneName, LoadSceneMode.Single);
            while (load != null && !load.isDone)
                yield return null;

            yield return null; // 等一帧让场景物体激活完成

            // 标题阶段是正交 2D，进入正式关卡后恢复 3D 透视视角
            SwitchToGameplayCameraProjection();

            ResolveSpawnPointInLoadedScene();

            TeleportPlayerToSpawn();
            SetupLevelStartCamera();

            DisableLevelDefaultCamera();

            if (playerInputs != null)
                playerInputs.MoveInput(Vector2.zero);

            // 生成流程全部在黑幕内完成：
            //  1) 播完“从天上落下 / 弹出”的生成动画；
            //  2) 继续等角色真正落到地面站稳（生成点若悬空，此时发生的是物理自由落体）；
            //  3) 到这一步角色已站定、音乐也已在正常播放，才淡出黑幕亮屏。
            // 这样玩家永远不会看到“角色从天上掉下来”或“音乐还在淡入”的画面。
            Transform player = playerController.transform;

            if (!skipSpawnSequence)
            {
                if (spawnAnimationClip != null)
                {
                    yield return PlaySpawnAnimation(player);
                    PlaySpawnEffects();
                }
                else
                {
                    PlaySpawnEffects();
                    PrepareScalePop(player);   // 缓存原大小并压成 0（画面此时仍全黑）
                    yield return PopInSpawn(player);
                }
            }

            // 关键：无论有没有播生成动画，都在黑幕里等角色落到地面站稳，
            // 避免淡出后看到角色从天上掉下来的过程。
            yield return WaitUntilPlayerSettled();

            // 人物生成完毕、音乐已正常播放 → 这时才让黑幕淡出
            if (titleUI != null && withCinematics)
                yield return titleUI.FadeFromBlackRoutine(fadeFromBlackDuration);

            // 玩家已出现在画面中：让出生镜头点“定格”一小会，再平滑切回跟拍玩家
            BeginStartCameraFollowReturn();

            if (!skipSpawnSequence && walkOutDuration > 0f)
            {
                driveInput = true;
                autoRunAxis = walkOutSpeedAxis;
                yield return new WaitForSeconds(walkOutDuration);
            }
        }
        else
        {
            Debug.LogWarning("[GameFlowController] 没有玩家，无法完成转场生成。", this);
        }

        FinishSpawn();
        HideTitleUI();
    }

    private IEnumerator FallbackSpawnInCurrentScene()
    {
        ResolveSpawnPointByName();
        TeleportPlayerToSpawn();
        SetupLevelStartCamera();

        if (skipSpawnSequence || playerController == null)
        {
            yield return WaitUntilPlayerSettled();

            if (titleUI != null)
                yield return titleUI.FadeFromBlackRoutine(fadeFromBlackDuration);
        }
        else
        {
            Transform player = playerController.transform;

            // 与正式转场一致：先在黑幕内播完生成动画，音乐就绪后才淡出亮屏
            if (spawnAnimationClip != null)
            {
                yield return PlaySpawnAnimation(player);
                PlaySpawnEffects();
            }
            else
            {
                PlaySpawnEffects();
                PrepareScalePop(player);
                yield return PopInSpawn(player);
            }

            yield return WaitUntilPlayerSettled();

            if (titleUI != null)
                yield return titleUI.FadeFromBlackRoutine(fadeFromBlackDuration);
        }

        // 玩家已出现：让出生镜头点定格一会，再平滑切回跟拍玩家
        BeginStartCameraFollowReturn();

        FinishSpawn();
        HideTitleUI();
    }

    private void ResolveSpawnPointByName()
    {
        if (spawnPoint != null)
            return;

        GameObject found = GameObject.Find(spawnObjectName);
        if (found == null && spawnObjectName != "Spawn")
            found = GameObject.Find("Spawn");

        if (found != null)
            spawnPoint = found.transform;
    }

    private void ResolveSpawnPointInLoadedScene()
    {
        spawnPoint = null; // 场景已切换，重新在正式场景里找
        ResolveSpawnPointByName();
    }

    private void TeleportPlayerToSpawn()
    {
        if (playerController == null)
            return;

        Transform player = playerController.transform;

        if (spawnPoint == null)
        {
            Debug.LogWarning($"[GameFlowController] 没有找到生成点（名为 {spawnObjectName} 的物体），角色将留在原地。", this);
            return;
        }

        CharacterController characterController = player.GetComponent<CharacterController>();
        if (characterController != null)
            characterController.enabled = false;

        player.position = spawnPoint.position;
        player.rotation = Quaternion.Euler(0f, playerController.RightFacingAngle, 0f);

        if (characterController != null)
            characterController.enabled = true;
    }

    /// <summary>
    /// 关卡出生镜头处理（在玩家传送回出生点后、黑幕淡出前调用）：
    /// 若场景摆有出生镜头点（CameraPointMarker，优先勾选了 IsStartPoint 的那个），
    /// 黑幕内把相机瞬移到该点的位置/角度并锁定取景；稍后由 BeginStartCameraFollowReturn 定格一会再切回跟拍。
    /// 没有摆镜头点时，维持旧的 spawnPoint + spawnCameraOffset 机位作为回退。
    /// </summary>
    private void SetupLevelStartCamera()
    {
        if (mainCamera == null || playerController == null)
            return;

        bool applyFallbackOffset = true;

        if (useStartCameraPoint && cameraFollow != null)
        {
            CameraPointMarker start = ResolveStartCameraPoint();
            if (start != null)
            {
                Transform point = start.transform;

                // 锁定到镜头点并立即就位（含位置/角度/FOV），黑幕期间玩家看不到切换过程
                cameraFollow.SetLockedPoint(point, start.CameraFOV);
                cameraFollow.SnapToCurrentTarget();
                mainCamera.transform.rotation = point.rotation;
                mainCamera.fieldOfView = start.CameraFOV;

                startCameraShotActive = true;
                applyFallbackOffset = false;

                Debug.Log($"[GameFlowController] 使用出生镜头点 {point.name}：位置 {point.position}，" +
                          $"朝向 {point.eulerAngles}，FOV {start.CameraFOV}，" +
                          $"定格 {startCameraHoldDuration}s 后切回跟拍玩家。", start);
            }
        }

        if (applyFallbackOffset && spawnPoint != null)
            mainCamera.transform.position = spawnPoint.position + spawnCameraOffset;
    }

    /// <summary>
    /// 玩家从黑幕中正式出现后调用：让出生镜头点再“定格”一小段（构成开场取景），
    /// 然后自动平滑切回 CameraFollowController 的“跟拍玩家”机位。
    /// </summary>
    private void BeginStartCameraFollowReturn()
    {
        if (!startCameraShotActive)
            return;

        startCameraShotActive = false;
        StartCoroutine(HoldStartCameraThenFollowRoutine());
    }

    private IEnumerator HoldStartCameraThenFollowRoutine()
    {
        if (startCameraHoldDuration > 0f)
            yield return new WaitForSeconds(startCameraHoldDuration);

        if (cameraFollow != null && cameraFollow.IsLocked)
        {
            // 在 resumeFollowBlendSeconds 内缓慢追回跟拍机位（而不是“嗖”地一下瞬切）
            cameraFollow.SetNormalModeWithBlend(resumeFollowBlendSeconds);
            Debug.Log($"[GameFlowController] 出生镜头定格结束，相机在 {resumeFollowBlendSeconds}s 内缓慢切回跟拍玩家。", this);
        }
    }

    /// <summary>查找出生镜头点：优先场景里勾选了 IsStartPoint 的 CameraPointMarker；全都没勾时取第一个，避免“忘了勾选就用不上”。</summary>
    private CameraPointMarker ResolveStartCameraPoint()
    {
        CameraPointMarker[] markers = FindObjectsOfType<CameraPointMarker>();
        if (markers == null || markers.Length == 0)
            return null;

        foreach (CameraPointMarker marker in markers)
        {
            if (marker != null && marker.IsStartPoint)
                return marker;
        }

        return markers[0];
    }

    /// <summary>禁用正式场景自带的默认相机（避免两个相机/两个 AudioListener）。</summary>
    private void DisableLevelDefaultCamera()
    {
        if (mainCamera == null)
            return;

        Camera[] cameras = FindObjectsOfType<Camera>();

        foreach (Camera camera in cameras)
        {
            if (camera == null || camera == mainCamera)
                continue;

            if (camera.transform.root == mainCamera.transform.root)
                continue;

            // 渲染到 RenderTexture 的特效相机（如踩水波纹顶视 RT 相机）保留，不影响画面相机。
            if (camera.targetTexture != null)
                continue;

            if (camera.gameObject.scene.IsValid() &&
                camera.gameObject.scene.name == gameplaySceneName)
            {
                camera.gameObject.SetActive(false);
            }
        }
    }

    /// <summary>
    /// 标题阶段的主相机是正交（2D 无透视），进入正式关卡后需要切回透视。
    /// 在黑幕期间完成切换，玩家正式看到画面时已经是 3D 视角。
    /// </summary>
    private void SwitchToGameplayCameraProjection()
    {
        if (mainCamera == null)
            return;

        Camera cam = mainCamera.GetComponent<Camera>();
        if (cam != null)
        {
            cam.orthographic = false;
            cam.fieldOfView = gameplayFieldOfView;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 1000f;
        }

        // 通知 CameraFollowController 刷新基准 FOV 并统一关卡默认参数。
        // 若标题场景忘挂该组件，在这里自动补上，保证进关卡后相机逻辑可用。
        CameraFollowController follow = mainCamera.GetComponent<CameraFollowController>();
        if (follow == null)
        {
            follow = mainCamera.gameObject.AddComponent<CameraFollowController>();
            Debug.Log("[GameFlowController] 主相机缺少 CameraFollowController，已自动补加。", mainCamera);
        }
        follow.OnSwitchedToPerspective(gameplayFieldOfView);
    }

    private void FinishSpawn()
    {
        autoRunAxis = 0f;
        driveInput = false;

        if (playerInputs != null)
        {
            playerInputs.MoveInput(Vector2.zero);
            playerInputs.SprintInput(false);
        }

        // 交还角色控制
        SetPlayerInputEnabled(true);

        // 正式游玩开始时创建左上角能量 HUD（三格绿点 / Q 消耗 / 黑幕文字层），标题阶段不会出现
        GameplayHUD.EnsureCreated();

        // 关卡已就绪（黑幕已淡出 / 角色已出现）→ 这时才显示能量格与耐力条
        GameplayHUD.SetGameplayUiVisibleIfExists(true);

        state = GameFlowState.Playing;
        sequenceStarted = false;
    }

    /// <summary>隐藏并停用标题 UI（转场完成后不再需要）。</summary>
    private void HideTitleUI()
    {
        if (titleUI == null)
            return;

        titleUI.SetTextVisible(false);

        GameObject root = titleUI.transform.root.gameObject;
        if (root != null)
            root.SetActive(false);
    }

    private void MakePersistent(GameObject target)
    {
        if (target == null)
            return;

        DontDestroyOnLoad(target.transform.root.gameObject);
    }

    private void SetPlayerInputEnabled(bool enabled)
    {
#if ENABLE_INPUT_SYSTEM
        if (playerInputComponent != null)
            playerInputComponent.enabled = enabled;
#endif
    }

    private Transform ResolveVisualRoot(Transform player)
    {
        Transform visualRoot = playerController != null ? playerController.CharacterModelRoot : null;

        if (visualRoot == null)
        {
            Animator animator = player.GetComponentInChildren<Animator>();
            if (animator != null)
                visualRoot = animator.transform;
        }

        if (visualRoot == null)
            visualRoot = player;

        return visualRoot;
    }

    /// <summary>生成前调用：把模型压成 0（黑幕中还看不见），并缓存它原本的大小供弹出时恢复。</summary>
    private void PrepareScalePop(Transform player)
    {
        spawnVisualRoot = ResolveVisualRoot(player);

        Vector3 target = spawnVisualRoot.localScale;
        if (target == Vector3.zero)
            target = Vector3.one;
        spawnVisualTargetScale = target;

        spawnVisualRoot.localScale = Vector3.zero;
    }

    private IEnumerator PopInSpawn(Transform player)
    {
        Transform visualRoot = spawnVisualRoot;
        Vector3 targetScale = spawnVisualTargetScale;

        if (visualRoot == null
            || targetScale == Vector3.zero
            || visualRoot != ResolveVisualRoot(player))
        {
            visualRoot = ResolveVisualRoot(player);
            targetScale = visualRoot.localScale;
            if (targetScale == Vector3.zero)
                targetScale = Vector3.one;
        }

        visualRoot.localScale = Vector3.zero;

        float elapsed = 0f;
        while (elapsed < spawnPopDuration)
        {
            elapsed += Time.deltaTime;
            float t = spawnPopDuration > 0f ? elapsed / spawnPopDuration : 1f;
            visualRoot.localScale = Vector3.Lerp(Vector3.zero, targetScale, t);
            yield return null;
        }

        visualRoot.localScale = targetScale;
        spawnVisualRoot = null;
    }

    private IEnumerator PlaySpawnAnimation(Transform player)
    {
        Animation animation = player.GetComponentInChildren<Animation>();

        if (animation == null)
            animation = player.gameObject.AddComponent<Animation>();

        const string clipName = "Spawn";

        if (animation.GetClip(clipName) == null)
            animation.AddClip(spawnAnimationClip, clipName);

        animation.wrapMode = WrapMode.Once;
        animation.Play(clipName);

        yield return new WaitForSeconds(spawnAnimationClip.length);
    }

    /// <summary>
    /// 黑幕里“等角色落地站稳”：
    /// 生成动画播完后角色可能仍悬在空中（生成点高于地面，或动画结束姿势停在半空），
    /// 其后的自由落体与落地修正如果发生在黑幕淡出之后，玩家就会看到角色“从天上掉下来”。
    /// 这里在画面仍全黑时等它真正踩到地面、竖直速度归零，再多留一小会才结束；
    /// 返回之后调用方才会淡出黑幕。
    /// </summary>
    private IEnumerator WaitUntilPlayerSettled()
    {
        if (playerController == null)
            yield break;

        CharacterController characterController = playerController.GetComponent<CharacterController>();
        float deadline = Time.time + Mathf.Max(0.1f, maxSpawnGroundWaitSeconds);

        // 生成动画刚结束时接地状态还没刷新，先放两帧
        yield return null;
        yield return null;

        // 角色还在自由落体时不接地；一直等到它真正踩到地面
        while (!IsPlayerGrounded() && Time.time < deadline)
            yield return null;

        // 落地后再确认竖直速度已经归零，避开“刚触地 / 小弹跳”的瞬间
        int settledFrames = 0;
        while (settledFrames < 2 && Time.time < deadline)
        {
            float verticalSpeed = characterController != null ? characterController.velocity.y : 0f;
            settledFrames = verticalSpeed > -0.5f ? settledFrames + 1 : 0;
            yield return null;
        }

        // 落地后再黑屏停留一小会
        if (spawnGroundSettleSeconds > 0f)
            yield return new WaitForSeconds(spawnGroundSettleSeconds);
    }

    /// <summary>
    /// 角色是否已踩在地面上：优先用 CharacterController 内建检测（不依赖角色的 GroundLayers 配置），
    /// 没有 CharacterController 时退回角色的球形接地检测结果。
    /// </summary>
    private bool IsPlayerGrounded()
    {
        if (playerController == null)
            return true;

        CharacterController characterController = playerController.GetComponent<CharacterController>();
        if (characterController != null)
            return characterController.isGrounded;

        return playerController.Grounded;
    }

    private void PlaySpawnEffects()
    {
        Vector3 position = spawnPoint != null
            ? spawnPoint.position
            : (playerController != null ? playerController.transform.position : transform.position);

        if (spawnEffectPrefab != null)
        {
            GameObject effect = Instantiate(spawnEffectPrefab, position, Quaternion.identity);
            Destroy(effect, 10f);
        }

        if (spawnSound != null)
        {
            if (audioSource != null)
                audioSource.PlayOneShot(spawnSound);
            else
                AudioSource.PlayClipAtPoint(spawnSound, position);
        }
    }

    /// <summary>停止本控制器管理的旧音乐/音效源（正式关卡 BGM 转交 AudioManager 后调用，避免双份音乐叠加）。</summary>
    public void StopBackgroundAudio()
    {
        if (musicSource != null)
        {
            musicSource.Stop();
            musicSource.clip = null;
        }

        if (audioSource != null)
            audioSource.Stop();
    }

    private void PlayMusic(AudioClip clip)
    {
        if (musicSource == null || clip == null)
            return;

        if (musicSource.clip == clip && musicSource.isPlaying)
            return;

        musicSource.Stop();
        musicSource.clip = clip;
        musicSource.loop = true;
        musicSource.Play();
    }

    private IEnumerator RampAutoRun(float from, float to, float duration)
    {
        if (duration <= 0f)
        {
            autoRunAxis = to;
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            autoRunAxis = Mathf.Lerp(from, to, elapsed / duration);
            yield return null;
        }

        autoRunAxis = to;
    }

    private IEnumerator SkipRoutine()
    {
        state = GameFlowState.Starting;

        if (grayscaleController != null)
            grayscaleController.SetFullColor();

        if (titleUI != null)
        {
            titleUI.SetTextVisible(false);
            titleUI.SetBlackScreen(true);
        }

        yield return RunTransitionToLevel(false);
    }

    /// <summary>
    /// 点击 / 触屏点按画面任意处开始（键盘空格/回车、手柄也支持）。
    /// </summary>
    private static bool WasStartPressed()
    {
        // 暂停菜单打开期间（或刚点完「继续游戏」的瞬间）不把点击/按键当成开局
        if (PauseMenuManager.IsPaused || Time.unscaledTime < PauseMenuManager.BlockTitleStartUntil)
            return false;

#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null &&
            (keyboard.spaceKey.wasPressedThisFrame ||
             keyboard.enterKey.wasPressedThisFrame))
            return true;

        Mouse mouse = Mouse.current;
        if (mouse != null &&
            (mouse.leftButton.wasPressedThisFrame ||
             mouse.rightButton.wasPressedThisFrame ||
             mouse.middleButton.wasPressedThisFrame))
            return true;

        if (Touchscreen.current != null &&
            Touchscreen.current.primaryTouch.press.wasPressedThisFrame)
            return true;

        Gamepad gamepad = Gamepad.current;
        if (gamepad != null &&
            (gamepad.buttonSouth.wasPressedThisFrame ||
             gamepad.startButton.wasPressedThisFrame))
            return true;

        return false;
#else
        return Input.GetMouseButtonDown(0) ||
               Input.GetMouseButtonDown(1) ||
               Input.GetMouseButtonDown(2) ||
               Input.GetKeyDown(KeyCode.Space) ||
               Input.GetKeyDown(KeyCode.Return);
#endif
    }

    private void OnDrawGizmosSelected()
    {
        Transform target = spawnPoint;

        if (target == null && !string.IsNullOrEmpty(spawnObjectName))
        {
            GameObject found = GameObject.Find(spawnObjectName);
            if (found != null)
                target = found.transform;
        }

        if (target == null)
            return;

        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(target.position, 0.35f);
        Gizmos.DrawLine(target.position, target.position + Vector3.right * 1.5f);
    }

}
