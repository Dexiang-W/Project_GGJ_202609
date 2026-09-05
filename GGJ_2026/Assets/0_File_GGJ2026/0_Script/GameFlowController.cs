using System.Collections;
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

    [Header("标题阶段")]
    [Tooltip("1 = 持续向右跑动")]
    [SerializeField] private float attractRunAxis = 1f;
    [Tooltip("标题阶段是否加速跑动")]
    [SerializeField] private bool attractSprint = true;
    [Tooltip("进入标题画面时是否把相机瞬间吸到玩家身后的机位（避免开场镜头从远处猛甩过来）")]
    [SerializeField] private bool snapCameraOnAttractStart = true;

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

    // 标题循环用
    private Vector3 attractOrigin;
    private Vector3 attractCameraOffset;
    private float loopStartX;
    private float loopLengthX;

    // 生成弹出视觉缓存（先把模型压成 0 后仍需知道它的原大小）
    private Transform spawnVisualRoot;
    private Vector3 spawnVisualTargetScale;

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
            playerInputs.SprintInput(state == GameFlowState.Attract && attractSprint);
        }
    }

    /// <summary>手动触发「开始游戏」（UI 按钮 / 调试用）。</summary>
    [ContextMenu("开始游戏")]
    public void BeginGame()
    {
        if (state == GameFlowState.Attract && !sequenceStarted)
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

        PlayMusic(attractMusic);
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

    private IEnumerator StartGameSequence()
    {
        sequenceStarted = true;
        state = GameFlowState.Starting;

        if (titleUI != null)
            StartCoroutine(titleUI.FadeOutTextsRoutine(textFadeDuration));

        if (grayscaleController != null)
            StartCoroutine(grayscaleController.FadeToFullColorRoutine(colorFadeDuration));

        PlayMusic(gameplayMusic);

        yield return RampAutoRun(attractRunAxis, 0f, stopDuration);
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

            ResolveSpawnPointInLoadedScene();

            TeleportPlayerToSpawn();
            if (mainCamera != null && spawnPoint != null)
                mainCamera.transform.position = spawnPoint.position + spawnCameraOffset;

            DisableLevelDefaultCamera();

            if (playerInputs != null)
                playerInputs.MoveInput(Vector2.zero);

            // 让“从方块里弹出”作为生成的第一瞬间发生：
            // 先在黑幕内把角色视觉压成 0，再让黑幕淡出与弹出动画同时进行，
            // 避免出现“先完整站在出生点 → 再消失 → 才弹出”的割裂感。
            Transform player = playerController.transform;

            if (!skipSpawnSequence)
            {
                if (spawnAnimationClip != null)
                {
                    // 自定义生成动画 Clip：与黑幕淡出同时播放
                    if (titleUI != null && withCinematics)
                        yield return RunConcurrent(PlaySpawnAnimation(player),
                                                   titleUI.FadeFromBlackRoutine(fadeFromBlackDuration));
                    else
                        yield return PlaySpawnAnimation(player);
                    PlaySpawnEffects();
                }
                else
                {
                    PlaySpawnEffects();
                    PrepareScalePop(player);   // 缓存原大小并压成 0（画面此时仍全黑）

                    if (titleUI != null && withCinematics)
                        yield return FadeInWithScalePop(player);
                    else
                        yield return PopInSpawn(player);
                }

                if (walkOutDuration > 0f)
                {
                    driveInput = true;
                    autoRunAxis = walkOutSpeedAxis;
                    yield return new WaitForSeconds(walkOutDuration);
                }
            }
            else if (titleUI != null && withCinematics)
            {
                yield return titleUI.FadeFromBlackRoutine(fadeFromBlackDuration);
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

        if (mainCamera != null && spawnPoint != null)
            mainCamera.transform.position = spawnPoint.position + spawnCameraOffset;

        if (skipSpawnSequence || playerController == null)
        {
            if (titleUI != null)
                yield return titleUI.FadeFromBlackRoutine(fadeFromBlackDuration);
        }
        else
        {
            Transform player = playerController.transform;

            if (spawnAnimationClip != null)
            {
                if (titleUI != null)
                    yield return RunConcurrent(PlaySpawnAnimation(player),
                                               titleUI.FadeFromBlackRoutine(fadeFromBlackDuration));
                else
                    yield return PlaySpawnAnimation(player);
                PlaySpawnEffects();
            }
            else
            {
                PlaySpawnEffects();
                PrepareScalePop(player);

                if (titleUI != null)
                    yield return FadeInWithScalePop(player);
                else
                    yield return PopInSpawn(player);
            }
        }

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

            if (camera.gameObject.scene.IsValid() &&
                camera.gameObject.scene.name == gameplaySceneName)
            {
                camera.gameObject.SetActive(false);
            }
        }
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

    /// <summary>同时等待两个协程执行完毕（用于让“黑幕淡出”与“生成弹出”同步进行）。</summary>
    private IEnumerator RunConcurrent(IEnumerator first, IEnumerator second)
    {
        Coroutine c1 = first != null ? StartCoroutine(first) : null;
        Coroutine c2 = second != null ? StartCoroutine(second) : null;
        if (c1 != null) yield return c1;
        if (c2 != null) yield return c2;
    }

    /// <summary>黑幕淡出的同时播放“从方块里弹出”。</summary>
    private IEnumerator FadeInWithScalePop(Transform player)
    {
        if (titleUI == null)
        {
            yield return PopInSpawn(player);
            yield break;
        }

        yield return RunConcurrent(PopInSpawn(player),
                                   titleUI.FadeFromBlackRoutine(fadeFromBlackDuration));
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
