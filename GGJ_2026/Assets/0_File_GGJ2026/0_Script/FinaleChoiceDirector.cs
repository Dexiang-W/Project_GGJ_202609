using System.Collections;
using System.Collections.Generic;
using StarterAssets;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// 结局前的「抉择」演出（Level5 · P_Machine）：
///
///   ① 玩家走进 AutoCollider → 接管操作，角色以正常速度沿 S 形弧线自动走向
///      AutoPoint（场景里有就先走它）→ 再到 P_Machine（此时玩家不能操作）；
///   ② 走到机器前站定 → 画面逐渐变灰，屏幕中间逐渐淡入
///      “你下定决心了吗...”，下方一行小字
///      “谨慎选择，或许你可以审视你的来时路（B键翻阅）”；
///   ③ 屏幕下方出现两个白字选项：左「隐瞒真相，继续试验」/ 右「揭露真相，停止实验」；
///      A / D（或 ← / →）切换，选中的那一项文字周围出现一个轻微倒角的小框；
///      E（或 空格 / 回车）确认；
///      Esc = 暂时取消这一次抉择（收起 UI、画面恢复彩色、交还操作），
///      两个选项正下方有一行小字提示；玩家再走回触发区会重新开演；
///   ④ 选「隐瞒真相，继续试验」→ 播结局音乐 MUS_Ending_Overgrowth，交给场景里的
///      EndingSequencePlayer 走原来的结局流程（渐黑 → Final Scene → 两段文字 →
///      切 Level2 枯萎运镜 → 字幕 → 报幕）；
///      选「揭露真相，停止实验」→ 播结局音乐 MUS_Ending_Natural，交给
///      GiveUpEndingDirector（Level3+4 实机行走 → 字幕 → 报幕）。
///
/// 用法：正常不用手动摆。脚本会在 Level5 加载时自动创建一个（和 GameplayHUD 同一套路）；
/// 想改文案 / 走位速度 / 灰屏时长，就在 Level5 里放个空物体挂上本组件，
/// 自动创建的那份会自动让位（检测到场景里已有实例就什么都不做）。
/// </summary>
[DisallowMultipleComponent]
public class FinaleChoiceDirector : MonoBehaviour
{
    /// <summary>抉择演出是否正在进行（GGJLevelResetManager 用它屏蔽“按 R 重置”）。</summary>
    public static bool IsRunning { get; private set; }

    /// <summary>
    /// 抉择演出进行中（含走向机器 / 提问 / 选选项）。暂停菜单用它把 Esc 让给抉择 ——
    /// 否则玩家按 Esc 取消的同时会顺手弹出暂停菜单。
    /// </summary>
    public static bool IsChoosing => IsRunning;

    private static FinaleChoiceDirector instance;

    /// <summary>当前场景里（或自动创建的）抉择导演。</summary>
    public static FinaleChoiceDirector Instance => instance;

    /// <summary>自动创建时认的关卡名（只有这个关卡才会自动挂上本组件）。</summary>
    private const string AutoCreateSceneName = "Level5";

    // ---------------------------------------------------------------- 配置

    [Header("① 触发区 / 目标（留空按名字自动找）")]
    [Tooltip("“进入后开始自动走”的触发盒（Level5 里的 AutoCollider）。\n" +
             "留空 = 运行时按下面的名字找；本组件也可以直接挂在那个物体上，靠 OnTriggerEnter 触发。")]
    [SerializeField] private Collider autoCollider;
    [SerializeField] private string autoColliderName = "AutoCollider";

    [Tooltip("要自动走过去的机器（Level5 里的 P_Machine）")]
    [SerializeField] private Transform machineTarget;
    [SerializeField] private string machineObjectName = "P_Machine";

    [Tooltip("（可选）中途先走到的点（Level5 里的 AutoPoint）。\n" +
             "角色会先走到这里，再继续走向 P_Machine；两段各走一条弧线、鼓的方向相反，\n" +
             "连起来是自然的 S 形路线。\n" +
             "留空 = 按下面的名字在场景里找；找不到就直接走向 P_Machine。")]
    [SerializeField] private Transform autoPoint;
    [SerializeField] private string autoPointName = "AutoPoint";
    [Tooltip("走到中途点后停顿多久（秒）。0（默认）= 不停顿，一路顺畅走过去，动作更自然")]
    [SerializeField] private float pauseAtWaypointSeconds = 0f;

    [Tooltip("选「隐瞒真相，继续试验」后交给它播放的结局流程。留空 = 运行时在场景里找 EndingSequencePlayer。")]
    [SerializeField] private EndingSequencePlayer endingPlayer;

    [Tooltip("选「揭露真相，停止实验」后交给它播放的结局流程（渐黑 → Level3+4 实机行走 → 字幕 → 报幕）。\n" +
             "留空 = 运行时在场景里找；还是没有就按脚本里的默认参数自动建一个。")]
    [SerializeField] private GiveUpEndingDirector giveUpDirector;

    [Tooltip("勾选（默认）：一开局就停用 P_Machine 上的 InteractableObject ——\n" +
             "结局只能由触发区启动，玩家提前按 E 不会跳过这段抉择。")]
    [SerializeField] private bool disableMachineInteractable = true;

    [Header("② 自动走向机器")]
    [Tooltip("自动行走的速度（m/s）。\n" +
             "0（默认）= 用角色当前的正常行走速度（MoveSpeed），平时怎么走就怎么走；\n" +
             "想更慢一点就填具体数值（比如 1.2）。")]
    [SerializeField] private float walkSpeed = 0f;
    [Tooltip("走一条偏右的弧线：相对“起点→终点”的直线，往行进方向的右侧鼓出去多少米。\n" +
             "0 = 直线走到机器前。")]
    [SerializeField] private float arcBulge = 3.5f;
    [Tooltip("勾选（默认）= 弧线往【行进方向的右边】鼓；取消勾选 = 往左边鼓。\n" +
             "（觉得鼓反了直接取消勾选，或者把上面的数值填成负数也行）")]
    [SerializeField] private bool arcBulgeToRight = true;
    [Tooltip("走在弧线前面多远的“引导点”（米）：角色一直朝这个点走，走出来的才是弧线而不是折线")]
    [SerializeField] private float arcLookAheadDistance = 2.5f;
    [Tooltip("勾选（默认）= 自动行走期间临时开启“自由移动”（WASD 全向），弧线才走得出来；走完还原")]
    [SerializeField] private bool forceFreeMovementWhileWalking = true;
    [Tooltip("停在机器前方多少米（从靠近的那一侧算）")]
    [SerializeField] private float stopDistance = 2.2f;
    [Tooltip("距离目标多少米内开始减速（免得冲过头）")]
    [SerializeField] private float slowDownDistance = 3.5f;
    [Tooltip("离终点还有多少米就算“走到了”（水平距离，左右前后都算）")]
    [SerializeField] private float arriveDistance = 0.25f;
    [Tooltip("最长走多久（秒）：超时就直接进入提问，不会卡死")]
    [SerializeField] private float walkTimeoutSeconds = 25f;
    [Tooltip("走到之后站定多久再变灰（秒）")]
    [SerializeField] private float settleSeconds = 0.5f;

    [Header("③ 灰屏 + 提问")]
    [Tooltip("画面逐渐变灰的时长（秒）")]
    [SerializeField] private float grayFadeSeconds = 1.6f;
    [Tooltip("后处理不可用时（相机没开 Post Processing）的兜底灰幕透明度")]
    [SerializeField] private float fallbackGrayAlpha = 0.55f;
    [SerializeField, TextArea(1, 3)] private string questionText = "你下定决心了吗...";
    [SerializeField, TextArea(1, 3)] private string hintText = "谨慎选择，或许你可以审视你的来时路（B键翻阅）";
    [Tooltip("提问文字淡入时长（秒）")]
    [SerializeField] private float questionFadeSeconds = 1.2f;
    [SerializeField] private int questionFontSize = 58;
    [SerializeField] private int hintFontSize = 30;
    [Tooltip("提问文字相对屏幕中心的上下偏移（像素）")]
    [SerializeField] private float questionOffsetY = 60f;
    [Tooltip("小字在提问文字下方多少像素")]
    [SerializeField] private float hintOffsetY = 78f;

    [Header("④ 两个选项")]
    [SerializeField] private string leftOptionText = "隐瞒真相，继续试验";
    [SerializeField] private string rightOptionText = "揭露真相，停止实验";
    [SerializeField] private int optionFontSize = 46;
    [Tooltip("选项组离屏幕底边的距离（像素）")]
    [SerializeField] private float optionsOffsetY = 170f;
    [Tooltip("左右两个选项的间距（像素）")]
    [SerializeField] private float optionsSpacing = 640f;
    [SerializeField] private Vector2 optionBoxSize = new Vector2(560f, 110f);
    [Tooltip("选中项：外框颜色")]
    [SerializeField] private Color selectedFrameColor = new Color(1f, 1f, 1f, 0.92f);
    [Tooltip("未选中项：外框颜色（很淡，主要靠白字本身可读）")]
    [SerializeField] private Color normalFrameColor = new Color(1f, 1f, 1f, 0.22f);
    [Tooltip("选中项：框内的淡淡填充")]
    [SerializeField] private Color selectedFillColor = new Color(1f, 1f, 1f, 0.12f);
    [SerializeField] private float selectedTextAlpha = 1f;
    [SerializeField] private float normalTextAlpha = 0.68f;
    [Tooltip("选项淡入时长（秒）")]
    [SerializeField] private float optionFadeSeconds = 0.6f;
    [Tooltip("两个选项正下方的一行小字（提示可以取消）。留空 = 不显示")]
    [SerializeField, TextArea(1, 2)] private string escHintText = "Esc键暂时取消选择";
    [SerializeField] private int escHintFontSize = 26;
    [Tooltip("小字在两个选项下方多少像素")]
    [SerializeField] private float escHintOffsetY = 70f;
    [SerializeField] private Color escHintColor = new Color(1f, 1f, 1f, 0.62f);
    [Tooltip("勾选（默认）= 抉择期间按 Esc 可以暂时取消：收起提问 / 选项、画面恢复彩色、交还操作；\n" +
             "玩家再走回触发区会重新开演。取消勾选 = Esc 无效。")]
    [SerializeField] private bool allowCancelWithEscape = true;

    [Header("⑤ 收尾")]
    [Tooltip("选完之后，提问 + 选项淡出的时长（秒）")]
    [SerializeField] private float choiceFadeOutSeconds = 0.5f;
    [Tooltip("选「隐瞒真相，继续试验」后等多久再把画面恢复成彩色（秒）。\n" +
             "此时画面已经在渐黑了，颜色切换看不见，不会闪。")]
    [SerializeField] private float colorRestoreDelaySeconds = 1.2f;
    [Tooltip("选「揭露真相，停止实验」后，画面从灰屏恢复彩色的时长（秒）")]
    [SerializeField] private float colorRestoreFadeSeconds = 1.0f;

    [Header("⑥ 事件")]
    [Tooltip("玩家选择「隐瞒真相，继续试验」时触发（默认行为是播放 EndingSequencePlayer）")]
    public UnityEvent onRestartChosen = new UnityEvent();
    [Tooltip("玩家选择「揭露真相，停止实验」时触发（默认行为是播放 GiveUpEndingDirector）")]
    public UnityEvent onGiveUpChosen = new UnityEvent();

    // ---------------------------------------------------------------- 运行时

    private ThirdPersonController player;
    private bool hadInputEnabled = true;
    private bool inputWasChanged;
    private bool started;
    private float nextResolveTime;

    private Canvas canvas;
    private CanvasGroup questionGroup;
    private CanvasGroup optionGroup;
    private Image fallbackGray;

    private sealed class OptionView
    {
        public Image fill;
        public Image frame;
        public Text text;
    }

    private OptionView leftOption;
    private OptionView rightOption;
    private int selectedIndex;
    private bool useFallbackGray;
    /// <summary>本次抉择是否被玩家按 Esc 取消（取消后走回触发区可以重新开演）。</summary>
    private bool choiceCancelled;

    private static Sprite roundedFillSprite;
    private static Sprite roundedBorderSprite;

    // ---------------------------------------------------------------- 生命周期

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Debug.LogWarning("[FinaleChoiceDirector] 场景里已有一个抉择导演，多余的这个会被销毁。", this);
            Destroy(gameObject);
            return;
        }

        instance = this;
    }

    private void Start()
    {
        ResolveReferences();

        if (disableMachineInteractable && machineTarget != null)
        {
            InteractableObject machineInteractable = machineTarget.GetComponentInChildren<InteractableObject>(true);
            if (machineInteractable != null && machineInteractable.enabled)
            {
                machineInteractable.enabled = false;
                Debug.Log("[FinaleChoiceDirector] 已停用 P_Machine 的交互（结局改由 AutoCollider 触发）。", machineInteractable);
            }
        }
    }

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;

        IsRunning = false;
    }

    private void Update()
    {
        if (started || IsRunning)
            return;

        // 触发盒 / 玩家可能比本组件晚出现，隔一会重试一次
        if (autoCollider == null && Time.unscaledTime >= nextResolveTime)
        {
            nextResolveTime = Time.unscaledTime + 0.5f;
            ResolveReferences();
        }

        if (autoCollider == null)
            return;

        if (player == null)
            ResolvePlayer();
        if (player == null)
            return;

        // 玩家站进触发盒 → 开演（不靠 OnTriggerEnter 也能工作，省得手动给 AutoCollider 挂脚本）
        if (IsPlayerInsideTrigger())
            StartCoroutine(RunChoiceSequence());
    }

    /// <summary>
    /// 玩家是否在触发区内：只判水平范围（XZ），不看高度。
    /// 这类横版关卡的触发盒常挂在半空 / 贴地，按包围盒判 Y 会因为几厘米误差永远判不中。
    /// </summary>
    private bool IsPlayerInsideTrigger()
    {
        if (autoCollider == null || player == null)
            return false;

        Bounds bounds = autoCollider.bounds;
        Vector3 position = player.transform.position;

        return position.x >= bounds.min.x && position.x <= bounds.max.x &&
               position.z >= bounds.min.z && position.z <= bounds.max.z;
    }

    /// <summary>本组件就挂在触发盒上时走这条路（OnTriggerEnter）。</summary>
    private void OnTriggerEnter(Collider other)
    {
        if (started || IsRunning)
            return;

        if (other == null || other.GetComponentInParent<ThirdPersonController>() == null)
            return;

        StartCoroutine(RunChoiceSequence());
    }

    /// <summary>调试用：不等触发区，直接开演。</summary>
    [ContextMenu("直接开始抉择演出（预览）")]
    public void BeginForTest()
    {
        if (!started && !IsRunning)
            StartCoroutine(RunChoiceSequence());
    }

    // ---------------------------------------------------------------- 自动创建

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        TryAutoCreate();
        SceneManager.sceneLoaded += (scene, mode) => TryAutoCreate();
    }

    private static void TryAutoCreate()
    {
        if (instance != null)
            return;

        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || !scene.isLoaded || scene.name != AutoCreateSceneName)
            return;

        if (FindObjectOfType<ThirdPersonController>() == null)
            return;

        GameObject go = new GameObject("GGJ_FinaleChoiceDirector");
        instance = go.AddComponent<FinaleChoiceDirector>();

        Debug.Log("[FinaleChoiceDirector] 已在 " + AutoCreateSceneName + " 自动创建：玩家走进 AutoCollider 后" +
                  "自动走向 P_Machine 并弹出抉择。想改文案 / 参数，就在场景里放个空物体挂上本组件。", go);
    }

    // ---------------------------------------------------------------- 主流程

    private IEnumerator RunChoiceSequence()
    {
        if (started || IsRunning)
            yield break;

        started = true;
        IsRunning = true;

        ResolveReferences();
        ResolvePlayer();

        if (player == null)
        {
            Debug.LogWarning("[FinaleChoiceDirector] 场景里没有玩家（ThirdPersonController），无法播放抉择演出。", this);
            IsRunning = false;
            yield break;
        }

        // 演出期间屏蔽“按 E 交互”，免得玩家乱按碰到别的可交互物体
        bool previousSuppress = InteractableObject.SuppressInteractionInput;
        InteractableObject.SuppressInteractionInput = true;
        bool handOffToEnding = false;

        try
        {
            EnsureUi();

            DisablePlayerInput();
            GameplayHUD.SetGameplayUiVisibleIfExists(false);

            // ① 缓慢自动走向机器
            yield return WalkToMachineRoutine();

            // ② 画面逐渐变灰（后处理不可用时退回一层灰色蒙版）
            GrayscaleController gray = ResolveGrayscale();

            Coroutine questionFade = StartCoroutine(FadeGroupRoutine(questionGroup, 0f, 1f, questionFadeSeconds));

            if (gray != null && gray.IsAvailable)
                yield return gray.FadeToGrayscaleRoutine(grayFadeSeconds);
            if (useFallbackGray)
                yield return FadeFallbackGrayRoutine(0f, fallbackGrayAlpha, grayFadeSeconds);

            yield return questionFade;

            // ③ 两个选项
            selectedIndex = 0;
            RefreshSelection();
            yield return FadeGroupRoutine(optionGroup, 0f, 1f, optionFadeSeconds);

            // ④ 等玩家选（A / D 切换，E 确认；Esc 暂时取消；看书界面开着时不响应）
            yield return WaitForChoiceRoutine();

            yield return FadeGroupRoutine(optionGroup, 1f, 0f, choiceFadeOutSeconds);
            yield return FadeGroupRoutine(questionGroup, 1f, 0f, choiceFadeOutSeconds);

            // —— Esc：暂时取消这一次抉择 ——
            // 提问 / 选项收起、画面恢复彩色、把操作还给玩家；
            // started 复位 → 玩家再走回触发区（AutoCollider）会重新开演。
            if (choiceCancelled)
            {
                SetFallbackGrayAlpha(0f);
                if (gray != null)
                    yield return gray.FadeToFullColorRoutine(colorRestoreFadeSeconds);

                GameplayHUD.SetGameplayUiVisibleIfExists(true);
                RestorePlayerInput();
                started = false;
                Debug.Log("[FinaleChoiceDirector] 玩家按 Esc 暂时取消了抉择，走回触发区会重新开始。", this);
                yield break;
            }

            if (selectedIndex == 0)
            {
                // —— 隐瞒真相，继续试验：交给原来的结局流程 ——
                onRestartChosen?.Invoke();

                // 结局音乐（MUS_Ending_Overgrowth）：锁定主音乐通道，之后切场景不会被关卡配乐顶掉
                AudioManager.PlayEndingOvergrowthMusic();

                if (endingPlayer != null)
                {
                    endingPlayer.Play();
                    // 结局流程自己会接管“屏蔽交互”，这里别在 finally 里把它解开
                    handOffToEnding = true;
                }
                else
                {
                    Debug.LogWarning("[FinaleChoiceDirector] 场景里没有 EndingSequencePlayer，无法播放结局流程。", this);
                }

                // 此时画面已经在渐黑了，等黑幕盖住再把颜色还回去（玩家看不到跳变）
                yield return new WaitForSecondsRealtime(Mathf.Max(0f, colorRestoreDelaySeconds));
                if (gray != null)
                    gray.SetFullColor();
                SetFallbackGrayAlpha(0f);
            }
            else
            {
                // —— 揭露真相，停止实验：交给“放弃结局”导演 ——
                // 它自己会：渐黑 → Level3+4 实机行走 → 字幕 → 报幕。
                // 画面第一次全黑时它会回调这里，那时再把灰屏的颜色还回去（玩家看不到跳变）。
                Debug.Log("[FinaleChoiceDirector] 玩家选择了「揭露真相，停止实验」 —— 转交 GiveUpEndingDirector。", this);
                onGiveUpChosen?.Invoke();

                // 结局音乐（MUS_Ending_Natural）：锁定主音乐通道，之后切场景不会被关卡配乐顶掉
                AudioManager.PlayEndingNaturalMusic();

                GiveUpEndingDirector director = ResolveGiveUpDirector();
                if (director != null)
                {
                    director.Play(() =>
                    {
                        SetFallbackGrayAlpha(0f);
                        if (gray != null)
                            gray.SetFullColor();
                    });

                    // 后面（黑幕 / 交互解锁 / 报幕）都归它管
                    handOffToEnding = true;
                }
                else
                {
                    // 兜底：没有导演就维持原来的行为（恢复画面 + 交还操作）
                    SetFallbackGrayAlpha(0f);
                    if (gray != null)
                        yield return gray.FadeToFullColorRoutine(colorRestoreFadeSeconds);

                    GameplayHUD.SetGameplayUiVisibleIfExists(true);
                    RestorePlayerInput();
                }
            }
        }
        finally
        {
            // 交给结局流程之后，屏蔽交互由那边负责，别在这里提前解开
            if (!handOffToEnding)
                InteractableObject.SuppressInteractionInput = previousSuppress;

            IsRunning = false;
        }
    }

    /// <summary>
    /// 接管操作，让角色以正常速度走过去：先走中途点 AutoPoint（场景里有才走），再走 P_Machine。
    /// 每一段都是一条弧线、相邻两段鼓的方向相反 → 整条路线是自然的 S 形，不会僵直。
    /// </summary>
    private IEnumerator WalkToMachineRoutine()
    {
        StarterAssetsInputs inputs = player.GetComponent<StarterAssetsInputs>();
        if (inputs == null)
        {
            Debug.LogWarning("[FinaleChoiceDirector] 玩家身上没有 StarterAssetsInputs，无法自动行走。", player);
            yield break;
        }

        List<Vector3> waypoints = BuildWalkWaypoints();

        float previousMoveSpeed = player.MoveSpeed;
        if (walkSpeed > 0f)
            player.MoveSpeed = walkSpeed;

        // 横版关卡默认只让走 X（弧线走不出来），这里临时开一下全向移动，走完还原
        bool forcedFreeMove = false;
        if (forceFreeMovementWhileWalking && !player.FreeMovementEnabled)
        {
            player.EnableFreeMovement();
            forcedFreeMove = true;
        }

        for (int i = 0; i < waypoints.Count; i++)
        {
            bool last = i == waypoints.Count - 1;

            // 相邻两段朝相反方向鼓 → 连起来是自然的 S 形，而不是两段同向的“硬折线”
            bool bulgeRight = (i % 2 == 0) ? arcBulgeToRight : !arcBulgeToRight;

            // 只有最后一段（走到机器前）才减速停下；中途点保持速度走过去，动作更连贯
            yield return WalkSegmentRoutine(inputs, waypoints[i], bulgeRight, last);

            if (!last && pauseAtWaypointSeconds > 0f)
                yield return new WaitForSeconds(pauseAtWaypointSeconds);
        }

        inputs.MoveInput(Vector2.zero);

        if (settleSeconds > 0f)
            yield return new WaitForSeconds(settleSeconds);

        if (forcedFreeMove)
            player.DisableFreeMovement();

        player.MoveSpeed = previousMoveSpeed;
    }

    /// <summary>路线：AutoPoint（场景里有就先走它）→ 机器前的站位。</summary>
    private List<Vector3> BuildWalkWaypoints()
    {
        List<Vector3> waypoints = new List<Vector3>();

        Transform auto = ResolveAutoPoint();
        if (auto != null)
            waypoints.Add(auto.position);

        waypoints.Add(ResolveWalkTarget());
        return waypoints;
    }

    /// <summary>取中途点：Inspector 指定 → 按名字在场景里找 → 没有就返回 null（直接走向机器）。</summary>
    private Transform ResolveAutoPoint()
    {
        if (autoPoint != null)
            return autoPoint;

        if (string.IsNullOrEmpty(autoPointName))
            return null;

        GameObject go = GameObject.Find(autoPointName);
        if (go == null)
        {
            Debug.LogWarning($"[FinaleChoiceDirector] 场景里没有名为 “{autoPointName}” 的中途点，" +
                             $"直接走向 {machineObjectName}。", this);
            return null;
        }

        autoPoint = go.transform;
        return autoPoint;
    }

    /// <summary>
    /// 沿一条弧线走到 target：二次贝塞尔，控制点从“起点→终点”直线的中点往行进方向的一侧鼓出去。
    /// 角色一直朝弧线前方的引导点走，所以走出来的是弧线而不是折线。
    /// </summary>
    private IEnumerator WalkSegmentRoutine(StarterAssetsInputs inputs, Vector3 target, bool bulgeRight, bool slowDownAtEnd)
    {
        Vector3 pathStart = player.transform.position;
        pathStart.y = 0f;

        Vector3 pathEnd = target;
        pathEnd.y = 0f;

        Vector3 forward = pathEnd - pathStart;
        float chord = forward.magnitude;
        if (chord < 0.05f)
            yield break;

        // 行进方向的右侧 = 前进方向 × 上方向（横版关卡里通常是 +Z，也就是往画面里侧偏）
        Vector3 right = Vector3.Cross(forward / chord, Vector3.up).normalized;

        // 短的那一段别鼓得太夸张（比如“中途点 → 机器正前方”只有几米时），按弦长收着点
        float bulge = Mathf.Min(Mathf.Abs(arcBulge), chord * 0.35f);
        Vector3 control = (pathStart + pathEnd) * 0.5f + right * (bulgeRight ? 1f : -1f) * bulge;

        float pathLength = EstimateBezierLength(pathStart, control, pathEnd);
        float timeout = Mathf.Max(1f, walkTimeoutSeconds);
        // 引导点也按路径长度收一收：路很短时还看 2.5 米开外，就变成直着冲过去了
        float lookAhead = Mathf.Min(Mathf.Max(0.1f, arcLookAheadDistance), pathLength * 0.6f);
        float elapsed = 0f;
        float t = 0f;
        bool arrived = false;

        while (elapsed < timeout && !arrived)
        {
            // 沿弧线匀速推进“引导点”，角色一直朝它走 → 走出来的是弧线而不是直线
            t = Mathf.Clamp01(t + (Mathf.Max(0.05f, player.MoveSpeed) * Time.deltaTime) / pathLength);

            float steerT = Mathf.Clamp01(t + lookAhead / pathLength);
            Vector3 point = Bezier(pathStart, control, pathEnd, steerT);

            Vector3 toPoint = point - player.transform.position;
            toPoint.y = 0f;

            Vector2 axis = Vector2.zero;
            float distance = toPoint.magnitude;
            if (distance > 0.05f)
            {
                // 快到终点时收着点走，免得冲过头；中途点不收速，一路顺畅地走过去
                float scale = slowDownAtEnd ? Mathf.Clamp01(distance / Mathf.Max(0.01f, slowDownDistance)) : 1f;
                Vector3 direction = toPoint / distance;
                axis = new Vector2(Mathf.Clamp(direction.x * scale, -1f, 1f),
                                   Mathf.Clamp(direction.z * scale, -1f, 1f));
            }

            inputs.MoveInput(axis);
            inputs.LookInput(Vector2.zero);
            inputs.JumpInput(false);
            inputs.SprintInput(false);

            elapsed += Time.deltaTime;

            // “走到了”按三维平面距离算（终点可能在机器的正前方，只差 Z 不差 X）；
            // 曲线走完还没贴上去时（被地形挡了一下之类）稍微放宽，免得一直磨蹭到超时
            Vector3 toEnd = pathEnd - player.transform.position;
            toEnd.y = 0f;
            float arrive = Mathf.Max(0.05f, arriveDistance);
            arrived = toEnd.magnitude <= arrive ||
                      (t >= 1f && toEnd.magnitude <= arrive * 4f);

            yield return null;
        }
    }

    /// <summary>二次贝塞尔取点。</summary>
    private static Vector3 Bezier(Vector3 p0, Vector3 p1, Vector3 p2, float t)
    {
        float u = 1f - Mathf.Clamp01(t);
        return u * u * p0 + 2f * u * Mathf.Clamp01(t) * p1 + Mathf.Clamp01(t) * Mathf.Clamp01(t) * p2;
    }

    /// <summary>采样估算贝塞尔曲线的长度（用来把“米/秒”换算成曲线进度）。</summary>
    private static float EstimateBezierLength(Vector3 p0, Vector3 p1, Vector3 p2)
    {
        float length = 0f;
        Vector3 previous = p0;

        for (int i = 1; i <= 16; i++)
        {
            Vector3 current = Bezier(p0, p1, p2, i / 16f);
            length += Vector3.Distance(previous, current);
            previous = current;
        }

        return Mathf.Max(1f, length);
    }

    /// <summary>
    /// 站位目标：P_Machine 的【正前方】 stopDistance 米处 —— 用机器自己的朝向（forward）在水平面上推出去，
    /// 所以不管机器朝哪个方向摆，最后都停在它面前，而不是它的左边 / 右边。
    /// 机器没给朝向（forward 是零向量）时才退回“停在靠玩家的那一侧”。
    /// </summary>
    private Vector3 ResolveWalkTarget()
    {
        if (machineTarget == null)
            return player.transform.position;

        Vector3 target = machineTarget.position;

        Vector3 forward = machineTarget.forward;
        forward.y = 0f;

        if (forward.sqrMagnitude > 0.0001f)
        {
            target += forward.normalized * Mathf.Max(0f, stopDistance);
            return target;
        }

        // 兜底：机器没有明确朝向 → 停在靠玩家那一侧的侧面
        float side = player.transform.position.x <= target.x ? -1f : 1f;
        target.x += side * Mathf.Max(0f, stopDistance);
        return target;
    }

    /// <summary>等玩家用 A / D 选好并按确认键。</summary>
    private IEnumerator WaitForChoiceRoutine()
    {
        choiceCancelled = false;

        while (true)
        {
            // 书本 / 纸条界面开着时把 A/D/E 让给它翻页，别顺手把选项给选了
            if (GGJBookSystem.IsUiOpen)
            {
                yield return null;
                continue;
            }

            if (allowCancelWithEscape && WasEscapePressed())
            {
                choiceCancelled = true;
                yield break;
            }

            if (WasLeftPressed())
            {
                selectedIndex = 0;
                RefreshSelection();
            }

            if (WasRightPressed())
            {
                selectedIndex = 1;
                RefreshSelection();
            }

            if (WasConfirmPressed())
                yield break;

            yield return null;
        }
    }

    private void RefreshSelection()
    {
        ApplySelection(leftOption, selectedIndex == 0);
        ApplySelection(rightOption, selectedIndex == 1);
    }

    private void ApplySelection(OptionView view, bool selected)
    {
        if (view == null)
            return;

        if (view.fill != null)
        {
            Color fill = selectedFillColor;
            fill.a = selected ? selectedFillColor.a : 0f;
            view.fill.color = fill;
        }

        if (view.frame != null)
            view.frame.color = selected ? selectedFrameColor : normalFrameColor;

        if (view.text != null)
        {
            Color color = view.text.color;
            color.a = selected ? selectedTextAlpha : normalTextAlpha;
            view.text.color = color;
        }
    }

    // ---------------------------------------------------------------- 灰屏

    private GrayscaleController ResolveGrayscale()
    {
        GrayscaleController gray = FindObjectOfType<GrayscaleController>();
        if (gray == null)
        {
            GameObject go = new GameObject("GGJ_FinaleGrayscale");
            DontDestroyOnLoad(go);
            gray = go.AddComponent<GrayscaleController>();
        }

        // 组件的 Awake 会直接把画面设成全灰，这里先拉回彩色，免得创建瞬间画面“啪”地一下变灰
        gray.SetFullColor();

        // 后处理要真的开着，饱和度才有效果；没开就顺手打开，实在没有就退回灰色蒙版
        useFallbackGray = !gray.IsAvailable || !EnsurePostProcessingEnabled();
        return gray;
    }

    /// <summary>确保主相机开着后处理（灰屏靠它）。返回 true 表示确实可用。</summary>
    private static bool EnsurePostProcessingEnabled()
    {
        Camera mainCamera = Camera.main;
        if (mainCamera == null)
            return false;

        UnityEngine.Rendering.Universal.UniversalAdditionalCameraData cameraData =
            mainCamera.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();

        if (cameraData == null)
            return false;

        if (!cameraData.renderPostProcessing)
        {
            cameraData.renderPostProcessing = true;
            Debug.Log("[FinaleChoiceDirector] 主相机原先没开后处理，已临时打开（灰屏需要）。", mainCamera);
        }

        return true;
    }

    private IEnumerator FadeFallbackGrayRoutine(float from, float to, float duration)
    {
        if (duration <= 0f)
        {
            SetFallbackGrayAlpha(to);
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            SetFallbackGrayAlpha(Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration)));
            yield return null;
        }

        SetFallbackGrayAlpha(to);
    }

    private void SetFallbackGrayAlpha(float alpha)
    {
        if (fallbackGray == null)
            return;

        float a = Mathf.Clamp01(alpha);
        Color color = fallbackGray.color;
        color.a = a;
        fallbackGray.color = color;
        fallbackGray.enabled = a > 0.001f;
    }

    // ---------------------------------------------------------------- UI 构建

    private void EnsureUi()
    {
        if (canvas != null)
            return;

        GameObject root = new GameObject("GGJ_FinaleChoiceUI");
        root.transform.SetParent(transform, false);

        canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 700;   // 盖过游戏 HUD(100)，低于书本(800) / 暂停菜单(1500) / 结局字幕(1000)

        CanvasScaler scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        root.AddComponent<GraphicRaycaster>();

        // 兜底灰幕（后处理不可用时才用）
        fallbackGray = CreateUiObject("GrayFallback", root.transform).AddComponent<Image>();
        StretchFull(fallbackGray.rectTransform);
        fallbackGray.color = new Color(0.5f, 0.5f, 0.5f, 0f);
        fallbackGray.raycastTarget = false;
        fallbackGray.enabled = false;

        // —— 提问 + 小字 ——
        GameObject questionGO = CreateUiObject("QuestionGroup", root.transform);
        StretchFull(questionGO.GetComponent<RectTransform>());
        questionGroup = questionGO.AddComponent<CanvasGroup>();
        questionGroup.alpha = 0f;
        questionGroup.interactable = false;
        questionGroup.blocksRaycasts = false;

        Text question = CreateText("Question", questionGO.transform, 1600f, 200f,
                                   questionFontSize, FontStyle.Bold, TextAnchor.MiddleCenter);
        question.rectTransform.anchoredPosition = new Vector2(0f, questionOffsetY);
        question.text = questionText;
        question.color = Color.white;

        Text hint = CreateText("Hint", questionGO.transform, 1600f, 120f,
                               hintFontSize, FontStyle.Normal, TextAnchor.MiddleCenter);
        hint.rectTransform.anchoredPosition = new Vector2(0f, questionOffsetY - hintOffsetY);
        hint.text = hintText;
        hint.color = new Color(1f, 1f, 1f, 0.82f);

        // —— 两个选项 ——
        GameObject optionsGO = CreateUiObject("OptionGroup", root.transform);
        StretchFull(optionsGO.GetComponent<RectTransform>());
        optionGroup = optionsGO.AddComponent<CanvasGroup>();
        optionGroup.alpha = 0f;
        optionGroup.interactable = false;
        optionGroup.blocksRaycasts = false;

        GameObject rowGO = CreateUiObject("Options", optionsGO.transform);
        RectTransform rowRT = rowGO.GetComponent<RectTransform>();
        rowRT.anchorMin = new Vector2(0.5f, 0f);
        rowRT.anchorMax = new Vector2(0.5f, 0f);
        rowRT.pivot = new Vector2(0.5f, 0f);
        rowRT.anchoredPosition = new Vector2(0f, optionsOffsetY);
        rowRT.sizeDelta = new Vector2(optionsSpacing + optionBoxSize.x, optionBoxSize.y);

        leftOption = BuildOption(rowGO.transform, -optionsSpacing * 0.5f, leftOptionText);
        rightOption = BuildOption(rowGO.transform, optionsSpacing * 0.5f, rightOptionText);

        // —— 两个选项正中间、下方的一行小字（Esc 提示）——
        if (!string.IsNullOrEmpty(escHintText))
        {
            Text escHint = CreateText("EscHint", optionsGO.transform, 900f, 60f,
                                      escHintFontSize, FontStyle.Normal, TextAnchor.MiddleCenter);
            RectTransform escRT = escHint.rectTransform;
            escRT.anchorMin = new Vector2(0.5f, 0f);
            escRT.anchorMax = new Vector2(0.5f, 0f);
            escRT.pivot = new Vector2(0.5f, 0f);
            // 和选项同一套锚点（离屏幕底边 optionsOffsetY），再往下挪 escHintOffsetY
            escRT.anchoredPosition = new Vector2(0f, Mathf.Max(0f, optionsOffsetY - escHintOffsetY));
            escHint.text = escHintText;
            escHint.color = escHintColor;
        }
    }

    private OptionView BuildOption(Transform parent, float x, string content)
    {
        GameObject boxGO = CreateUiObject("Option", parent);
        RectTransform boxRT = boxGO.GetComponent<RectTransform>();
        boxRT.anchorMin = new Vector2(0.5f, 0.5f);
        boxRT.anchorMax = new Vector2(0.5f, 0.5f);
        boxRT.pivot = new Vector2(0.5f, 0.5f);
        boxRT.anchoredPosition = new Vector2(x, 0f);
        boxRT.sizeDelta = optionBoxSize;

        // ① 选中时的一层淡填充（轻微倒角）
        Image fill = boxGO.AddComponent<Image>();
        fill.sprite = GetRoundedFillSprite();
        fill.type = Image.Type.Sliced;
        fill.color = new Color(1f, 1f, 1f, 0f);
        fill.raycastTarget = false;

        // ② 边框（同一个倒角矩形，只画边）
        Image frame = CreateUiObject("Frame", boxGO.transform).AddComponent<Image>();
        StretchFull(frame.rectTransform);
        frame.sprite = GetRoundedBorderSprite();
        frame.type = Image.Type.Sliced;
        frame.color = normalFrameColor;
        frame.raycastTarget = false;

        // ③ 白字
        Text text = CreateText("Text", boxGO.transform, optionBoxSize.x - 20f, optionBoxSize.y,
                               optionFontSize, FontStyle.Bold, TextAnchor.MiddleCenter);
        text.rectTransform.anchoredPosition = Vector2.zero;
        text.text = content;

        return new OptionView { fill = fill, frame = frame, text = text };
    }

    private static Text CreateText(string name, Transform parent, float width, float height,
                                   int fontSize, FontStyle style, TextAnchor anchor)
    {
        GameObject go = CreateUiObject(name, parent);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(width, height);

        Text text = go.AddComponent<Text>();
        text.font = GetDefaultFont();
        text.fontSize = fontSize;
        text.fontStyle = style;
        text.alignment = anchor;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;

        Outline outline = go.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
        outline.effectDistance = new Vector2(2f, -2f);

        return text;
    }

    private IEnumerator FadeGroupRoutine(CanvasGroup group, float from, float to, float duration)
    {
        if (group == null)
            yield break;

        if (duration <= 0f)
        {
            group.alpha = Mathf.Clamp01(to);
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            group.alpha = Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration));
            yield return null;
        }

        group.alpha = Mathf.Clamp01(to);
    }

    // ---------------------------------------------------------------- 倒角框贴图

    private static Sprite GetRoundedFillSprite()
    {
        if (roundedFillSprite == null)
            roundedFillSprite = CreateRoundedRectSprite(64, 14f, 0f, true);
        return roundedFillSprite;
    }

    private static Sprite GetRoundedBorderSprite()
    {
        if (roundedBorderSprite == null)
            roundedBorderSprite = CreateRoundedRectSprite(64, 14f, 3f, false);
        return roundedBorderSprite;
    }

    /// <summary>代码生成一个“轻微倒角”的矩形贴图：filled=true 实心，否则只留一圈边。</summary>
    private static Sprite CreateRoundedRectSprite(int size, float radius, float border, bool filled)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.name = filled ? "GGJ_ChoiceBoxFill" : "GGJ_ChoiceBoxBorder";

        float half = size * 0.5f;
        float outerHalf = half - 1f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - half;
                float dy = y + 0.5f - half;

                float alpha = Mathf.Clamp01(0.5f - RoundedRectDistance(dx, dy, outerHalf, radius));

                if (!filled)
                {
                    float innerAlpha = Mathf.Clamp01(0.5f -
                        RoundedRectDistance(dx, dy, outerHalf - border, Mathf.Max(0f, radius - border)));
                    alpha = Mathf.Clamp01(alpha - innerAlpha);
                }

                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }

        tex.Apply();

        float slice = radius + 2f;
        return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f),
                             100f, 0u, SpriteMeshType.FullRect, new Vector4(slice, slice, slice, slice));
    }

    /// <summary>到圆角矩形边界的有向距离（负数 = 在内部）。</summary>
    private static float RoundedRectDistance(float dx, float dy, float halfSize, float radius)
    {
        float qx = Mathf.Abs(dx) - (halfSize - radius);
        float qy = Mathf.Abs(dy) - (halfSize - radius);
        float outsideX = Mathf.Max(qx, 0f);
        float outsideY = Mathf.Max(qy, 0f);
        float outside = Mathf.Sqrt(outsideX * outsideX + outsideY * outsideY);
        return outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
    }

    // ---------------------------------------------------------------- 玩家 / 输入

    private void ResolveReferences()
    {
        ResolvePlayer();

        if (autoCollider == null && !string.IsNullOrEmpty(autoColliderName))
        {
            GameObject found = FindObjectEvenIfInactive(autoColliderName);
            if (found != null)
                autoCollider = found.GetComponent<Collider>();
        }

        if (machineTarget == null && !string.IsNullOrEmpty(machineObjectName))
        {
            GameObject found = GameObject.Find(machineObjectName);
            if (found != null)
                machineTarget = found.transform;
        }

        if (endingPlayer == null)
            endingPlayer = FindObjectOfType<EndingSequencePlayer>();
    }

    /// <summary>“揭露真相，停止实验”结局的导演：场景里有就用，没有就按默认参数现建一个常驻的。</summary>
    private GiveUpEndingDirector ResolveGiveUpDirector()
    {
        if (giveUpDirector == null)
            giveUpDirector = FindObjectOfType<GiveUpEndingDirector>();

        if (giveUpDirector == null)
        {
            GameObject go = new GameObject("GGJ_GiveUpEndingDirector");
            DontDestroyOnLoad(go);
            giveUpDirector = go.AddComponent<GiveUpEndingDirector>();
        }

        return giveUpDirector;
    }

    private void ResolvePlayer()
    {
        if (player == null)
            player = ThirdPersonController.Instance;
        if (player == null)
            player = FindObjectOfType<ThirdPersonController>();
    }

    private void DisablePlayerInput()
    {
        if (player == null)
            return;

        StarterAssetsInputs inputs = player.GetComponent<StarterAssetsInputs>();
        if (inputs != null)
        {
            inputs.MoveInput(Vector2.zero);
            inputs.SprintInput(false);
            inputs.JumpInput(false);
        }

#if ENABLE_INPUT_SYSTEM
        PlayerInput playerInput = player.GetComponent<PlayerInput>();
        if (playerInput != null)
        {
            hadInputEnabled = playerInput.enabled;
            if (hadInputEnabled)
            {
                playerInput.enabled = false;
                inputWasChanged = true;
            }
        }
#endif
    }

    private void RestorePlayerInput()
    {
        if (player == null)
            return;

#if ENABLE_INPUT_SYSTEM
        PlayerInput playerInput = player.GetComponent<PlayerInput>();
        if (playerInput != null && inputWasChanged)
            playerInput.enabled = hadInputEnabled;
#endif
        inputWasChanged = false;
    }

    // ---------------------------------------------------------------- 按键

    private static bool WasLeftPressed()
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        return keyboard != null &&
               (keyboard.aKey.wasPressedThisFrame || keyboard.leftArrowKey.wasPressedThisFrame);
#else
        return Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.LeftArrow);
#endif
    }

    private static bool WasRightPressed()
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        return keyboard != null &&
               (keyboard.dKey.wasPressedThisFrame || keyboard.rightArrowKey.wasPressedThisFrame);
#else
        return Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.RightArrow);
#endif
    }

    /// <summary>取消键：Esc。</summary>
    private static bool WasEscapePressed()
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        return keyboard != null && keyboard.escapeKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.Escape);
#endif
    }

    /// <summary>确认键：E（交互键）为主，空格 / 回车兜底。</summary>
    private static bool WasConfirmPressed()
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        return keyboard != null &&
               (keyboard.eKey.wasPressedThisFrame ||
                keyboard.spaceKey.wasPressedThisFrame ||
                keyboard.enterKey.wasPressedThisFrame);
#else
        return Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return);
#endif
    }

    // ---------------------------------------------------------------- 工具

    private static GameObject CreateUiObject(string name, Transform parent)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    private static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    /// <summary>按名字找物体（连关着的也能找到；AutoCollider 一般不会关，这里顺手兼容）。</summary>
    private static GameObject FindObjectEvenIfInactive(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || !scene.isLoaded)
            return GameObject.Find(name);

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root != null && root.name == name)
                return root;
        }

        return GameObject.Find(name);
    }

    private static Font GetDefaultFont()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return font;
    }
}
