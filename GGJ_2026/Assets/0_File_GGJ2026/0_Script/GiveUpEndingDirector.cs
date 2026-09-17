using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using StarterAssets;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// “揭露真相，停止实验”结局导演：玩家在 Level5 的抉择里选了「揭露真相，停止实验」之后播的整段实机演出。
///
/// 流程（每一段都在黑幕里换场景，玩家看不到跳变）：
///   切到 Level3+4，玩家在 1Point 出现、自动往右走，镜头跟着他（边亮边走）
///         —— 屏幕左边中上方渐渐浮出“希望我这个选择是正确的...”，停一会儿再渐渐消失
///   走完渐黑 → 全黑 → 显示“因为你的选择，实验室辜负了外界的期待……” → 滚动报幕 → 回到标题。
///
/// 走路和黑幕是【重叠】的：画面还在变亮时人已经在走，快到时间时一边走一边变黑，全黑了才停。
/// 每段开始时画面本来就是全黑的，所以【不会再渐黑一次】——
/// GameplayHUD 的渐黑会先把黑幕 alpha 归 0，在已经全黑时跑它就会“闪一下亮画面再变黑”。
/// 体积：三段都是原始大小（不再随场景越变越小），也不再化成能量球。
/// 演出期间所有关卡机制都会哑火（含死亡区 / 传送区这类触发器），玩家只管往前走。
///
/// 用法：
///   · 正常不需要手动摆：FinaleChoiceDirector 会在需要时自动创建一个常驻导演（用本脚本的默认参数）；
///   · 想调参（走多远 / 体积 / 速度 / 镜头 / 文案）就在场景里放个空物体挂上本组件，
///     拖到 FinaleChoiceDirector 的 “Give Up Director” 上（不拖也会自动找到场景里这一个）。
/// </summary>
[DisallowMultipleComponent]
public class GiveUpEndingDirector : MonoBehaviour
{
    /// <summary>演出是否正在进行（GGJLevelResetManager 用来屏蔽“按 R 重置”）。</summary>
    public static bool IsRunning { get; private set; }

    /// <summary>一段“换场景 + 自动行走”（行走序列里的其中一段）。</summary>
    [System.Serializable]
    public class WalkStage
    {
        [Header("场景与出生点")]
        [Tooltip("要切到的场景名（须已加入 Build Settings）")]
        public string sceneName = "Level3+4";

        [Tooltip("出生点物体名：该场景里的空对象（本项目统一叫 1Point）")]
        public string startPointName = "1Point";
        [Tooltip("找不到上面那个名字时的兜底名字（可以填多个，从前往后找）")]
        public string[] startPointFallbacks = { "StartPoint", "SpawnPoint" };

        [Header("走到哪儿")]
        [Tooltip("终点物体名：填了就一路走到这个点（如 Level1 的 FinalPoint）；\n" +
                 "留空 = 按下面的「走多少秒」走一段就收。")]
        public string endPointName = "";
        [Tooltip("离终点多近算走到（米）")]
        public float arriveDistance = 1.2f;
        [Tooltip("行走方向：1 = 向右，-1 = 向左")]
        public float walkDirection = 1f;
        [Tooltip("这一段走多少秒。\n走路和渐亮 / 渐黑是【重叠】的：画面还在变亮时人已经在走，" +
                 "快到时间时一边走一边逐渐变黑，所以不再“停下来再黑屏”。\n" +
                 "一段的总时长 ≈ 渐亮 + 本项 + 渐黑。")]
        public float walkSeconds = 5.5f;
        [Tooltip("硬上限：走满这么久一定收尾（被地形卡住 / 终点太远的兜底）")]
        public float maxWalkSeconds = 12f;

        [Header("玩家")]
        [Tooltip("这一段玩家的体积（相对原始大小）：1 = 原样，0.6 = 缩到六成。\n" +
                 "三段都留 1 = 玩家不再随场景越变越小（只有最后一段化成能量球时才会缩）。")]
        public float playerScale = 1f;
        [Tooltip("这一段玩家的移动速度（米/秒）；<= 0 = 不改（沿用当前速度）")]
        public float moveSpeed = 2.6f;

        [Header("镜头（跟着玩家）")]
        [Tooltip("相机相对玩家的偏移：离玩家近一点就把数值调小")]
        public Vector3 cameraOffset = new Vector3(0f, 3.2f, -6.5f);
        [Tooltip("这一段相机的 FOV（越小越“长焦”）")]
        public float cameraFOV = 42f;

        [Header("台词（屏幕左边中上方）")]
        [Tooltip("这一段显示在【屏幕左边中上方】的一行字：渐渐浮现 → 停一会儿 → 渐渐消失。\n" +
                 "留空 = 这一段不显示台词。淡入 / 停留 / 淡出的时长在导演的「③ 台词」里统一设置。")]
        public string message = "";

        [Header("收尾（最后一段）")]
        [Tooltip("勾选 = 这一段边走边从人形化成能量球")]
        public bool morphToEnergyBall = false;
        [Tooltip("走到这个进度才开始变形（0~1）")]
        public float morphStartProgress = 0.1f;
        [Tooltip("人形最后缩到多小（0 = 走完这段时人已经看不见了，只剩能量球）")]
        public float morphEndScale = 0f;
        [Tooltip("逐渐停住：到点前用这么多秒把速度平滑降到 0（人自然减速站住，不是被硬定住）。\n" +
                 "0 = 不停（一直走到黑幕盖上，前两段用的就是这个）。")]
        public float slowDownSeconds = 0f;
    }

    // ---------------------------------------------------------------- 配置

    [Header("① 行走段（按顺序播放）")]
    [Tooltip("每段：黑幕里换场景 → 摆人摆镜头 → 边亮边走 → 边走边黑。\n" +
             "时长 = 渐亮 + Walk Seconds + 渐黑（走路和黑幕是重叠的）。")]
    [SerializeField] private List<WalkStage> walkStages = new List<WalkStage>
    {
        new WalkStage
        {
            sceneName = "Level3+4",
            startPointName = "1Point",
            startPointFallbacks = new[] { "StartPoint", "SpawnPoint" },
            endPointName = "",
            walkDirection = 1f,
            walkSeconds = 5.5f,
            maxWalkSeconds = 9f,
            playerScale = 1f,
            moveSpeed = 2.8f,
            cameraOffset = new Vector3(0f, 3.2f, -6.5f),
            cameraFOV = 42f,
            message = "希望我这个选择是正确的...",
        },
    };

    [Header("② 黑幕节奏（秒）")]
    [Tooltip("本作所有关卡都在 Z=0 的平面上演出。勾选后：摆人时把出生点 / 终点的 Z 一律拉回 0，\n" +
             "免得在场景里拖点时手抖偏了一点 Z，人就跑到布景前面 / 后面去了。")]
    [SerializeField] private bool snapPointZToZero = true;
    [Tooltip("渐黑：走路还没停就开始变黑，所以这一段人一直在走")]
    [SerializeField] private float fadeToBlackSeconds = 1.0f;
    [Tooltip("渐亮：人跟着画面一起亮出来（边亮边走）")]
    [SerializeField] private float fadeFromBlackSeconds = 0.8f;
    [Tooltip("全黑停留时长（换场景 / 摆人 / 摆镜头都在这段时间里做完）")]
    [SerializeField] private float blackHoldSeconds = 0.5f;

    [Header("②·2 走路动画（人变小之后的步频）")]
    [Tooltip("勾选 = 按体积补偿走路动画的播放速度。\n" +
             "人缩小后步幅也该变短：同样的世界速度下腿要倒得更快，否则会像在冰上滑、动作显慢。\n" +
             "（只改动画播放速度，移动速度仍然是每一段里的 Move Speed）")]
    [SerializeField] private bool matchAnimationToScale = true;
    [Tooltip("步频补偿的上限（体积缩得太小时不至于快成陀螺）")]
    [SerializeField] private float maxAnimationSpeedFactor = 4f;

    [Header("③ 台词（屏幕左边中上方：淡入 → 停留 → 淡出）")]
    [Tooltip("画面亮起后再等这么久，台词才开始浮现（等镜头里看得清人了再出字）")]
    [SerializeField] private float messageDelaySeconds = 0.8f;
    [Tooltip("台词淡入时长（渐渐显示）")]
    [SerializeField] private float messageFadeInSeconds = 1.0f;
    [Tooltip("台词完全显示后停留多久")]
    [SerializeField] private float messageHoldSeconds = 2.2f;
    [Tooltip("台词淡出时长（渐渐消失）")]
    [SerializeField] private float messageFadeOutSeconds = 1.2f;
    [SerializeField] private float messageFontSize = 44f;
    [SerializeField] private Color messageColor = new Color(1f, 1f, 1f, 1f);
    [Tooltip("台词离屏幕左边的距离（像素，按 1920x1080 参考分辨率）")]
    [SerializeField] private float messageLeftMargin = 110f;
    [Tooltip("台词的高度（0 = 屏幕底边，1 = 屏幕顶边；0.66 ≈ 左边中上方）")]
    [SerializeField] [Range(0f, 1f)] private float messageHeightRatio = 0.66f;
    [Tooltip("台词区域宽度（像素）")]
    [SerializeField] private float messageWidth = 1200f;

    [Header("④ 能量球（最后一段变形用）")]
    [Tooltip("能量球预制体 —— 把 P_EnergyBall_Still 拖进来。\n" +
             "留空也没关系：会依次尝试 ① Resources 里同名预制体 ② 编辑器里按名字自动找（见 energyBallPrefabNames）" +
             "③ 场景里现成的能量球 ④ 临时捏一个发光小球。")]
    [SerializeField] private GameObject energyBallPrefab;
    [Tooltip("能量球最后有多大（世界米，按球的直径算）。\n" +
             "0 = 直接用预制体自己的原始大小（推荐：关卡里那颗球多大，结局里就多大）。\n" +
             "预制体原始大小离谱（不足 5cm 或超过 4m）时才会按这个值折算。")]
    [SerializeField] private float energyBallDiameter = 0f;
    [Tooltip("能量球浮在玩家脚底之上多高（人化成球时高度不变，球不会跟着人一起沉到地上）")]
    [SerializeField] private float energyBallHeight = 0.9f;
    [Tooltip("能量球再往【镜头方向】挪多少米。\n" +
             "不挪的话球会正好长在人物身体里（胸口那块儿），被人形挡住看不见。")]
    [SerializeField] private float energyBallTowardCamera = 0.6f;
    [Tooltip("临时小球（找不到现成预制体时）用的颜色")]
    [SerializeField] private Color energyBallColor = new Color(0.35f, 0.95f, 1f, 1f);

    [Header("⑤ 收尾字幕 / 报幕")]
    [Tooltip("结局播放器（字幕 + 报幕 + 回标题）。\n" +
             "留空 = 自动使用场景里那一个；场景里也没有就现建一个。")]
    [SerializeField] private EndingSequencePlayer endingPlayer;
    [Tooltip("黑屏后依次显示的文案")]
    [SerializeField, TextArea(1, 3)] private List<string> endingTexts = new List<string>
    {
        "因为你的选择，实验室辜负了外界的期待。",
        "舆论的压力之下，你被销毁了。",
        "至于实验是否仍在某处继续……无人知晓。",
    };
    [Tooltip("滚动报幕名单（和 Level5 里 EndingSequencePlayer 的 Credits Lines 一致）。\n" +
             "留空 = 沿用 EndingSequencePlayer 自己的名单。")]
    [SerializeField] private string[] creditsLines =
    {
        "—— 鸣 谢 ——",
        "王德祥 TA",
        "板凳 关卡策划+程序",
        "陶骏腾 音频",
        "Ruki 主美",
        "包包 3d美术",
        "小青 3d美术",
        "殷鸿嘉 3d美术",
        "感谢每一位测试玩家！",
    };
    [SerializeField] private float endingTextHoldSeconds = 3.2f;
    [Tooltip("画面全黑后、字幕出现前的停顿（秒）")]
    [SerializeField] private float beforeTextsDelaySeconds = 0.6f;

    [Header("⑥ 场景机制清理（切场景后只留“自动走 + 跟拍”）")]
    [Tooltip("勾选 = 每进一个场景就把这个场景里所有脚本全部停用：\n" +
             "传送触发器、能量球拾取、可交互物、移动平台、敌人、旁白、相机切换区、刷怪点……\n" +
             "一律不生效，画面里只剩“玩家自动走、镜头跟着他”。\n" +
             "渲染 / 碰撞 / 地形 / 动画这些 Unity 自带组件不受影响，所以场景看起来完全正常，只是没玩法了。")]
    [SerializeField] private bool disableSceneMechanics = true;
    [Tooltip("勾选 = 把本场景里所有「触发器」碰撞体（Is Trigger）也一起关掉。\n" +
             "一定要关：Unity 的 OnTriggerEnter 在脚本被停用之后【仍然会调用】，\n" +
             "所以只停脚本挡不住死亡区 / 关卡传送区 / 能量球拾取 —— 人一走进去照样被传走、照样弹死亡文字。\n" +
             "只关触发器：地面 / 墙这些实体碰撞保留，人还是踩在地上、撞得到墙。")]
    [SerializeField] private bool disableTriggerColliders = true;
    [Tooltip("例外：这些组件不停用（直接把场景里的组件拖进来）")]
    [SerializeField] private MonoBehaviour[] keepEnabledBehaviours;
    [Tooltip("例外：这些类型不停用（填类型名，支持 * 通配，例如 UniversalAdditional*）")]
    [SerializeField] private string[] keepEnabledTypeNames =
    {
        "Volume",
        "VolumeTrigger",
        "UniversalAdditionalCameraData",
        "UniversalAdditionalLightData",
        "HDAdditionalCameraData",
        "HDAdditionalLightData",
    };

    // ---------------------------------------------------------------- 运行时

    private ThirdPersonController player;
    private Camera mainCamera;
    private CameraFollowController camFollow;
    private CharacterController charController;

    /// <summary>能量球“挂载点”：跟着玩家走（漂浮 / 自转的球挂在它下面）。</summary>
    private GameObject energyBall;
    /// <summary>真正看得见的那一颗球。</summary>
    private GameObject energyBallView;
    private float energyBallBaseHeight;
    private float energyBallSizeFactor = 1f;

    private readonly List<Renderer> playerRenderers = new List<Renderer>();

    private bool hadInputEnabled = true;

    /// <summary>台词用的 UI（屏幕左边中上方那行字，平时全透明）。</summary>
    private Canvas messageCanvas;
    private Text messageText;
    private Outline messageOutline;
    private Coroutine messageCoroutine;

    /// <summary>走路协程正在跑（黑幕亮起 / 落下的时候都还在走）。</summary>
    private bool walking;
    private Coroutine walkCoroutine;
    /// <summary>当前这一段的终点（用来判断“走到了没”），空 = 这一段没有终点，纯按时间走。</summary>
    private Transform currentEndPoint;

    /// <summary>能量球预制体的候选名字（依次尝试）。</summary>
    private static readonly string[] EnergyBallPrefabNames =
    {
        "P_EnergyBall_Still",
        "P_EnergyBall",
        "EnergyQiu",
    };

    /// <summary>
    /// 开始“放弃实验”结局。
    /// </summary>
    /// <param name="onFirstBlackReached">
    /// 画面第一次完全变黑时回调（抉择导演用它把灰屏颜色还原，玩家看不到跳变）。
    /// </param>
    public void Play(Action onFirstBlackReached = null)
    {
        if (IsRunning)
            return;

        StartCoroutine(Run(onFirstBlackReached));
    }

    // ---------------------------------------------------------------- 主流程

    private IEnumerator Run(Action onFirstBlackReached)
    {
        IsRunning = true;
        DontDestroyOnLoad(transform.root.gameObject);

        GameplayHUD.EnsureCreated();
        GameplayHUD hud = GameplayHUD.Instance;

        ResolvePlayer();
        if (player == null)
        {
            Debug.LogWarning("[GiveUpEndingDirector] 场景里没有玩家（ThirdPersonController），无法播放“放弃实验”结局。", this);
            IsRunning = false;
            yield break;
        }

        // 开演前先把这台相机锁定下来（之后换场景一律沿用，不再抓新场景自带的固定机位）
        ResolveCamera();

        // 演出期间：屏蔽“按 E 交互” / 相机触发区 / 旁白 / HUD，玩家自己的输入也收走
        bool previousSuppress = InteractableObject.SuppressInteractionInput;
        InteractableObject.SuppressInteractionInput = true;
        CameraTriggerVolume.SuppressCameraSwitching = true;
        MessageTriggerZone.SuppressMessages = true;
        // 演出全程禁止一切复活流程：死亡区 / 危险货物再也不能把正在走的人传走、也不能弹死亡文字
        RespawnFlow.Suppressed = true;
        GameplayHUD.SetGameplayUiVisibleIfExists(false);
        DisablePlayerInput();
        MakePersistent(player.transform.root);

        try
        {
            // 开场渐黑（“重启实验”那边也是这么开的）
            if (hud != null)
                yield return hud.FadeToBlackRoutine(fadeToBlackSeconds);

            onFirstBlackReached?.Invoke();

            // 当前场景（做抉择的那一场）的机制也一起停掉
            DisableSceneMechanics();

            if (blackHoldSeconds > 0f)
                yield return new WaitForSecondsRealtime(blackHoldSeconds);

            // 注意：每段结束时画面已经是全黑的了，所以下一段【绝对不能再渐黑一次】——
            // GameplayHUD 的渐黑是“先把黑幕 alpha 归 0 再淡到 1”，在已经全黑的时候跑它，
            // 就会先闪一下亮画面再变黑（这就是之前“屏幕闪一下”的来源）。
            for (int i = 0; i < walkStages.Count; i++)
            {
                WalkStage stage = walkStages[i];
                if (stage == null || string.IsNullOrEmpty(stage.sceneName))
                    continue;

                yield return RunStage(stage, hud);
            }

            yield return ShowEndingTexts(hud);
        }
        finally
        {
            CameraTriggerVolume.SuppressCameraSwitching = false;
            MessageTriggerZone.SuppressMessages = false;
            RespawnFlow.Suppressed = false;
            InteractableObject.SuppressInteractionInput = previousSuppress;
            IsRunning = false;
        }
    }

    /// <summary>一段：换场景（全黑里做）→ 摆人摆镜头 → 边亮边走 → 边走边黑 → 全黑才停。</summary>
    private IEnumerator RunStage(WalkStage stage, GameplayHUD hud)
    {
        // 进这一段时画面本来就该是全黑的（开演渐黑过 / 上一段结尾渐黑过）。
        // 这里只“钉死”成全黑，不再跑一次渐黑：渐黑会先把黑幕 alpha 归 0，反而会闪一下亮画面。
        if (hud != null)
            hud.SetBlackScreen(true);

        // —— 换场景（黑幕里完成）——
        if (!string.IsNullOrEmpty(stage.sceneName) &&
            SceneManager.GetActiveScene().name != stage.sceneName)
        {
            if (Application.CanStreamedLevelBeLoaded(stage.sceneName))
            {
                AsyncOperation load = SceneManager.LoadSceneAsync(stage.sceneName, LoadSceneMode.Single);
                while (load != null && !load.isDone)
                    yield return null;

                // 等新场景激活
                yield return null;
                yield return null;
            }
            else
            {
                Debug.LogWarning($"[GiveUpEndingDirector] 场景 “{stage.sceneName}” 不在 Build Settings 里，" +
                                 "跳过这一段。", this);
                yield break;
            }
        }

        // 场景换了：玩家 / 相机是常驻的，但引用要重新确认一遍
        ResolvePlayer();
        ResolveCamera();

        // 目标场景自带的玩家 / 相机清掉，免得画面里出现两个人
        DisableExtraPlayers();
        DisableExtraCameras();

        // 这个场景里所有玩法脚本全部停用：只留“人自动走 + 镜头跟着”
        DisableSceneMechanics();

        if (blackHoldSeconds > 0f)
            yield return new WaitForSecondsRealtime(blackHoldSeconds);

        // —— 摆人 ——
        Transform start = FindPoint(stage.startPointName, stage.startPointFallbacks);
        if (start == null)
        {
            Debug.LogWarning($"[GiveUpEndingDirector] 场景 “{stage.sceneName}” 里没找到出生点 " +
                             $"“{stage.startPointName}”，玩家将停在原位。", this);
        }
        else
        {
            Debug.Log($"[GiveUpEndingDirector] 场景 “{stage.sceneName}”：玩家摆到 “{start.name}” " +
                      $"原点 {start.position}" + (snapPointZToZero ? "（Z 已拉回 0）" : "") + "。", this);
        }

        // 这一段的终点：有终点就走到终点为止（最后一段的 FinalPoint），没有就纯按时间走
        currentEndPoint = string.IsNullOrEmpty(stage.endPointName)
            ? null
            : FindPoint(stage.endPointName, null);

        TeleportPlayer(start, stage.walkDirection);

        // 体积（三段都是 1：不再随场景缩小）+ 速度（逐段稍微放慢）
        ApplyPlayerScale(Mathf.Max(0.01f, stage.playerScale));
        ApplyMoveSpeed(stage);

        // —— 摆镜头：跟着玩家（三段用同一套偏移 / FOV）——
        SetupCamera(stage);

        if (stage.morphToEnergyBall)
            EnsureEnergyBall(stage);

        // 保险：摆完人到开走之间还隔着几帧（场景脚本的延迟调用 / 物理沉降可能把人挪走），
        // 趁画面还全黑再摆一次，确保一定是从 1Point 出发。
        if (start != null)
            TeleportPlayer(start, stage.walkDirection);

        // —— 边亮边走：画面开始变亮的同时人就迈开腿（不再“等黑幕散完才开始走”）——
        walking = true;
        walkCoroutine = StartCoroutine(WalkRoutine(stage));

        // 台词：画面亮起来的同时，屏幕左边中上方渐渐浮出一行字，停一会儿再渐渐消失
        messageCoroutine = string.IsNullOrEmpty(stage.message)
            ? null
            : StartCoroutine(ShowStageMessage(stage));

        if (hud != null)
            yield return hud.FadeFromBlackRoutine(fadeFromBlackSeconds);

        // —— 走满这一段的时长（有终点的话，到了终点就提前收）——
        yield return WaitForWalkTime(stage);

        // —— 边走边黑：人还在走，画面已经开始变黑（不再“停下来再黑屏”）——
        if (hud != null)
            yield return hud.FadeToBlackRoutine(fadeToBlackSeconds);

        // 全黑了才停
        StopWalking();

        // 台词收干净（这一段要是提前收尾，字可能还没淡完）
        HideStageMessage();
    }

    /// <summary>
    /// 驱动玩家沿一个方向走。走路和黑幕完全并行：
    /// 画面还在变亮时人已经在走，快到时间时一边走一边变黑，直到 StopWalking()（全黑）才停。
    /// 变形进度 = 已走时长 / 本段时长，所以“边走边变成能量球”一定会在这一段里完成。
    /// </summary>
    private IEnumerator WalkRoutine(WalkStage stage)
    {
        if (player == null)
            yield break;

        StarterAssetsInputs inputs = player.GetComponent<StarterAssetsInputs>();
        float direction = stage.walkDirection >= 0f ? 1f : -1f;
        float startX = player.transform.position.x;
        float total = Mathf.Max(0.1f, stage.walkSeconds);
        float hardLimit = Mathf.Max(total, stage.maxWalkSeconds);
        float elapsed = 0f;

        while (walking && elapsed < hardLimit)
        {
            if (player == null)
                break;

            // “逐渐停住”：停下前把输入幅度和移动速度一起平滑降到 0（最后一段用）
            float factor = ComputeSlowDownFactor(stage, elapsed, total);

            if (inputs != null)
                inputs.MoveInput(new Vector2(direction * factor, 0f));

            if (stage.moveSpeed > 0f)
                player.MoveSpeed = stage.moveSpeed * factor;

            elapsed += Time.deltaTime;

            // 变形进度直接按“这一段走了多久”算 —— 边走边变，走完这段就变成球了
            if (stage.morphToEnergyBall)
                UpdateMorph(stage, Mathf.Clamp01(elapsed / total));

            // 这一段有终点、而且已经走到了：人到位，交给外面接着渐黑
            if (HasArrived(stage))
                break;

            // 已经完全停住了：不用再空转（最后一段的收尾）
            if (factor <= 0.001f)
                break;

            yield return null;
        }

        if (inputs != null)
            inputs.MoveInput(Vector2.zero);

        // 万一中途 break（到终点 / 被卡住）：变形也要收完整
        if (stage.morphToEnergyBall)
            UpdateMorph(stage, 1f);

        // 走完打印一下：位移明显小于“速度 × 时长”就说明半路被地形 / 机制挡住了
        float travelled = Mathf.Abs(player.transform.position.x - startX);
        Debug.Log($"[GiveUpEndingDirector] 场景 “{stage.sceneName}” 这一段走了 {elapsed:0.##} 秒、" +
                  $"往{(direction >= 0f ? "右" : "左")}位移 {travelled:0.##} 米。", this);
    }

    /// <summary>等这一段走满时长（有终点的话到了终点就提前收，接着就渐黑）。</summary>
    private IEnumerator WaitForWalkTime(WalkStage stage)
    {
        float limit = Mathf.Max(0.1f, stage.walkSeconds);
        float elapsed = 0f;

        while (walking && elapsed < limit)
        {
            elapsed += Time.deltaTime;

            if (HasArrived(stage))
                break;

            yield return null;
        }
    }

    /// <summary>
    /// “逐渐停住”的速度系数：1 = 正常走，0 = 完全停下。
    /// 到点前 Slow Down Seconds 之内线性降下来；“还剩多少秒”取
    /// ① 这一段的剩余时间 ② 离终点还有几秒（有终点时）里更小的那个，
    /// 所以不论走满时间还是走到终点，人都是自然减速站住的，不会被硬生生定住。
    /// </summary>
    private float ComputeSlowDownFactor(WalkStage stage, float elapsed, float total)
    {
        float slow = Mathf.Max(0f, stage.slowDownSeconds);
        if (slow <= 0.0001f)
            return 1f;

        float remaining = Mathf.Max(0f, total - elapsed);

        if (currentEndPoint != null && player != null)
        {
            float speed = Mathf.Max(0.01f, stage.moveSpeed > 0f ? stage.moveSpeed : player.MoveSpeed);
            float distance = Mathf.Max(0f, HorizontalDistance(player.transform.position, currentEndPoint.position) -
                                          Mathf.Max(0.05f, stage.arriveDistance));
            remaining = Mathf.Min(remaining, distance / speed);
        }

        return Mathf.Clamp01(remaining / slow);
    }

    private bool HasArrived(WalkStage stage)
    {
        if (player == null || currentEndPoint == null)
            return false;

        return HorizontalDistance(player.transform.position, currentEndPoint.position) <=
               Mathf.Max(0.05f, stage.arriveDistance);
    }

    /// <summary>画面全黑后收尾：停掉走路协程、松开输入。</summary>
    private void StopWalking()
    {
        walking = false;

        if (walkCoroutine != null)
        {
            StopCoroutine(walkCoroutine);
            walkCoroutine = null;
        }

        if (player == null)
            return;

        StarterAssetsInputs inputs = player.GetComponent<StarterAssetsInputs>();
        if (inputs != null)
        {
            inputs.MoveInput(Vector2.zero);
            inputs.SprintInput(false);
        }
    }

    /// <summary>
    /// 本段的移动速度 + 走路动画步频。
    /// 人的体积缩小后步幅也该变短：同样世界速度下腿要倒得更快，
    /// 否则会出现“人照常往前挪、动作却慢半拍像在滑冰”。
    /// </summary>
    private void ApplyMoveSpeed(WalkStage stage)
    {
        if (player == null)
            return;

        if (stage.moveSpeed > 0f)
            player.MoveSpeed = stage.moveSpeed;

        if (!matchAnimationToScale)
            return;

        Animator animator = player.GetComponent<Animator>();
        if (animator == null)
            return;

        float scale = Mathf.Max(0.05f, Mathf.Abs(player.transform.localScale.x));
        animator.speed = Mathf.Clamp(1f / scale, 0.25f, Mathf.Max(0.25f, maxAnimationSpeedFactor));
    }

    // ---------------------------------------------------------------- 变形

    private void UpdateMorph(WalkStage stage, float progress)
    {
        if (player == null)
            return;

        float span = Mathf.Clamp01(1f - Mathf.Clamp01(stage.morphStartProgress));
        float k = span <= 0.0001f ? 1f : Mathf.Clamp01((progress - stage.morphStartProgress) / span);

        // ① 人形越走越小（留一点底：缩放为 0 会让角色控制器退化，人会掉出地面）
        float from = Mathf.Max(0.01f, stage.playerScale);
        float to = Mathf.Max(0f, stage.morphEndScale);
        ApplyPlayerScale(Mathf.Max(0.02f, Mathf.Lerp(from, to, k)));

        // ② 能量球越长越大：高度固定（人是在缩小，球不该跟着沉到地上），
        //    并且往镜头方向让开一点，免得长在人物身体里面被挡住看不见
        if (energyBall == null || energyBallView == null)
            return;

        energyBall.transform.position = player.transform.position +
                                        Vector3.up * energyBallBaseHeight +
                                        TowardCameraXZ() * energyBallTowardCamera;

        // 只改“球”这一层 —— 挂载点的 scale 保持 1，漂浮 / 自转交给 EnergyBallBobber
        energyBallView.transform.localScale = Vector3.one * (energyBallSizeFactor * k);

        // ③ 人形缩到快看不见了 → 关掉人形渲染，画面上从此只剩能量球
        SetPlayerRenderersVisible(k < 0.98f);
    }

    /// <summary>
    /// 建（或抄）一颗能量球。
    /// 结构：一个空“挂载点”跟着玩家走，真正的球挂在它下面 ——
    /// 这样 EnergyBallBobber 的漂浮 / 自转（写 localPosition）和“跟着玩家”（写挂载点 position）互不打架。
    /// 抄来的球可能带着拾取逻辑 / 碰撞体，这里只保留外观和漂浮。
    /// </summary>
    private void EnsureEnergyBall(WalkStage stage)
    {
        if (energyBall != null)
            return;

        energyBallBaseHeight = Mathf.Max(0f, energyBallHeight);

        // 优先用拖进来的；没拖就按名字自动找（P_EnergyBall_Still → P_EnergyBall → EnergyQiu），
        // 再不行就在场景里找现成的能量球，最后才临时捏一个发光小球。
        GameObject source = energyBallPrefab;
        if (source == null)
            source = FindEnergyBallPrefab();
        if (source == null)
            source = FindSceneEnergyBall();

        // ① 挂载点（跟着玩家）
        energyBall = new GameObject("GGJ_EndingEnergyBall");
        DontDestroyOnLoad(energyBall);

        // ② 真正看得见的球
        if (source != null)
        {
            energyBallView = Instantiate(source, energyBall.transform);
            energyBallView.name = "EnergyBallView";

            foreach (Collider col in energyBallView.GetComponentsInChildren<Collider>(true))
            {
                if (col != null)
                    Destroy(col);
            }

            foreach (MonoBehaviour behaviour in energyBallView.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null || behaviour is EnergyBallBobber)
                    continue;
                Destroy(behaviour);
            }
        }
        else
        {
            // 兜底：捏一个会发光的小球
            energyBallView = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            energyBallView.name = "EnergyBallView";
            energyBallView.transform.SetParent(energyBall.transform, false);

            Collider own = energyBallView.GetComponent<Collider>();
            if (own != null)
                Destroy(own);

            Renderer renderer = energyBallView.GetComponent<Renderer>();
            if (renderer != null)
            {
                // URP 项目里 Standard 会渲染成洋红 / 黑球，优先用 URP/Lit
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                Material material = shader != null ? new Material(shader) : null;

                if (material != null)
                {
                    material.color = energyBallColor;
                    material.SetColor("_BaseColor", energyBallColor);
                    material.SetColor("_EmissionColor", energyBallColor * 1.6f);
                    material.EnableKeyword("_EMISSION");
                    renderer.material = material;
                }
            }

            energyBallView.AddComponent<EnergyBallBobber>();
        }

        // ③ 保险：抄来的球（FBX 模型 / 预制体变体）里可能有默认关着的子物体或渲染器 —— 一律打开，
        //    否则“球建好了却什么也看不见”。
        foreach (Transform child in energyBallView.GetComponentsInChildren<Transform>(true))
        {
            if (child != null && !child.gameObject.activeSelf)
                child.gameObject.SetActive(true);
        }

        foreach (Renderer renderer in energyBallView.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer != null)
                renderer.enabled = true;
        }

        energyBallView.transform.localPosition = Vector3.zero;
        energyBallView.transform.localRotation = Quaternion.identity;

        // ④ 尺寸：先把球“原始多大”量出来
        energyBallSizeFactor = 1f;
        float natural = 0f;
        Bounds bounds = new Bounds(Vector3.zero, Vector3.zero);
        bool hasBounds = false;

        foreach (Renderer renderer in energyBallView.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null)
                continue;

            if (!hasBounds)
            {
                bounds = new Bounds(renderer.transform.position, Vector3.zero);
                hasBounds = true;
            }

            bounds.Encapsulate(renderer.bounds);
        }

        if (hasBounds)
            natural = Mathf.Max(Mathf.Max(bounds.size.x, bounds.size.y), bounds.size.z);

        if (natural > 0.0001f)
        {
            // 原始大小看着就正常（关卡里那颗球本来就是这么大）→ 直接用，不折腾；
            // 只有原始大小离谱（不足 5cm 或超过 4m），或者手动填了直径，才按配置折算。
            bool naturalLooksFine = energyBallDiameter <= 0f && natural >= 0.05f && natural <= 4f;
            float target = energyBallDiameter > 0f ? energyBallDiameter : 0.55f;
            energyBallSizeFactor = naturalLooksFine ? 1f : Mathf.Max(0.001f, target) / natural;
        }

        // ④ 重新以“挂载点”为基准漂浮
        EnergyBallBobber bobber = energyBallView.GetComponent<EnergyBallBobber>();
        if (bobber != null)
            bobber.CacheBase();

        energyBall.transform.position = player != null
            ? player.transform.position + Vector3.up * energyBallBaseHeight +
              TowardCameraXZ() * energyBallTowardCamera
            : Vector3.zero;

        // 保险：万一抄来的球 / 挂载点是关着的，这里统一打开
        energyBall.SetActive(true);
        energyBallView.SetActive(true);

        // 先缩到 0 藏起来，走起来再慢慢“长”出来
        energyBallView.transform.localScale = Vector3.zero;

        Debug.Log($"[GiveUpEndingDirector] 能量球来源：“{(source != null ? source.name : "临时发光小球")}”，" +
                  $"原始直径 {natural:0.###}m × 系数 {energyBallSizeFactor:0.###} = 最终直径 " +
                  $"{natural * energyBallSizeFactor:0.###}m，挂在玩家脚底之上 {energyBallBaseHeight:0.##}m。", this);
    }

    /// <summary>
    /// 按名字找能量球预制体（P_EnergyBall_Still → P_EnergyBall → EnergyQiu）。
    /// 打包后只认 Resources 下同名资源；在编辑器里会用 AssetDatabase 全项目搜一遍，省得手动拖引用。
    /// </summary>
    private static GameObject FindEnergyBallPrefab()
    {
        foreach (string name in EnergyBallPrefabNames)
        {
            GameObject prefab = Resources.Load<GameObject>(name);
            if (prefab != null)
                return prefab;
        }

#if UNITY_EDITOR
        foreach (string name in EnergyBallPrefabNames)
        {
            string suffix = "/" + name + ".prefab";

            foreach (string guid in UnityEditor.AssetDatabase.FindAssets(name + " t:Prefab"))
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) ||
                    !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                GameObject prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null)
                    return prefab;
            }
        }
#endif

        return null;
    }

    /// <summary>场景里现成的能量球（P_EnergyBall / P_EnergyBall_Still 之类）。</summary>
    private static GameObject FindSceneEnergyBall()
    {
        foreach (Renderer renderer in FindObjectsOfType<Renderer>())
        {
            if (renderer == null)
                continue;

            string name = renderer.gameObject.name;
            if (name.IndexOf("EnergyBall", StringComparison.OrdinalIgnoreCase) >= 0)
                return renderer.gameObject;
        }

        return null;
    }

    // ---------------------------------------------------------------- 收尾

    private IEnumerator ShowEndingTexts(GameplayHUD hud)
    {
        // 最后一段走完时画面已经是全黑的了：这里只“钉死”成全黑，
        // 不再跑渐黑（渐黑会先把黑幕 alpha 归 0，等于闪一下亮画面）。
        if (hud != null)
            hud.SetBlackScreen(true);

        if (beforeTextsDelaySeconds > 0f)
            yield return new WaitForSecondsRealtime(beforeTextsDelaySeconds);

        EndingSequencePlayer ending = ResolveEndingPlayer();
        if (ending == null)
        {
            Debug.LogWarning("[GiveUpEndingDirector] 没有 EndingSequencePlayer，无法播放结局字幕 / 报幕。", this);
            yield break;
        }

        DontDestroyOnLoad(ending.transform.root.gameObject);

        // 报幕名单统一用这里配的那一份（默认和 Level5 里 EndingSequencePlayer 的一致）
        if (creditsLines != null && creditsLines.Length > 0)
            ending.SetCreditsLines(creditsLines);

        // 字幕 + 报幕（跳过它自带的“终章实机演出”），播完按它自己的设置回标题
        ending.PlayTextsAndCredits(endingTexts, endingTextHoldSeconds, centered: true);
    }

    /// <summary>
    /// 结局播放器（字幕 + 报幕 + 回标题）。
    /// 场景里没有就现建一个：它自己会搭 UI（黑幕 / 字幕 / 报幕滚动条）和默认报幕名单，
    /// 所以不用事先在场景里摆一个，也不会再报“场景里没有 EndingSequencePlayer”。
    /// 想改文案 / 报幕名单，就在场景里摆一个挂该组件的物体，或者直接拖到导演的 Ending Player 上。
    /// </summary>
    private EndingSequencePlayer ResolveEndingPlayer()
    {
        if (endingPlayer != null)
            return endingPlayer;

        endingPlayer = FindObjectOfType<EndingSequencePlayer>();

        if (endingPlayer == null)
        {
            GameObject go = new GameObject("EndingSequencePlayer (Runtime)");
            DontDestroyOnLoad(go);
            endingPlayer = go.AddComponent<EndingSequencePlayer>();

            Debug.Log("[GiveUpEndingDirector] 场景里没有 EndingSequencePlayer，已自动建一个" +
                      "（用它自带的默认报幕名单 / 回标题设置）。", go);
        }

        return endingPlayer;
    }

    // ---------------------------------------------------------------- 玩家 / 相机

    private void ResolvePlayer()
    {
        if (player == null)
            player = ThirdPersonController.Instance;
        if (player == null)
            player = FindObjectOfType<ThirdPersonController>();

        if (player != null)
            charController = player.GetComponent<CharacterController>();
    }

    private void ResolveCamera()
    {
        // 关键：整段演出必须用“同一台”相机。换场景后 Camera.main 会变成新场景自带的固定机位相机，
        // 所以一旦锁定了就不再换，新场景那台由 DisableExtraCameras 关掉。
        if (mainCamera != null)
        {
            if (!mainCamera.gameObject.activeSelf)
                mainCamera.gameObject.SetActive(true);
            if (!mainCamera.enabled)
                mainCamera.enabled = true;
        }
        else
        {
            mainCamera = Camera.main;
            if (mainCamera == null && player != null)
                mainCamera = player.GetComponentInChildren<Camera>(true);
        }

        if (mainCamera == null)
            return;

        if (player == null || mainCamera.transform.root != player.transform.root)
            MakePersistent(mainCamera.transform.root);

        // 相机上必须有跟拍脚本；没有就现挂一个（不能拿别的相机身上的来用）
        camFollow = mainCamera.GetComponent<CameraFollowController>();
        if (camFollow == null)
            camFollow = mainCamera.gameObject.AddComponent<CameraFollowController>();

        // 跟拍目标永远绑回当前这个玩家（换场景后旧的引用可能已经失效 / 指向被停用的场景玩家）
        camFollow.SetFollowTarget(player != null ? player.transform : null);
    }

    private void TeleportPlayer(Transform point, float direction)
    {
        if (player == null)
            return;

        // 角色每帧把朝向写成 Euler(0, yaw, roll)，这里摆一次同样的朝向：往右走时面朝右
        float characterRoll = player.InvertGravity ? 180f : 0f;
        float yaw = direction >= 0f ? player.RightFacingAngle : player.LeftFacingAngle;

        if (charController != null)
            charController.enabled = false;

        if (point != null)
        {
            Vector3 target = point.position;
            if (snapPointZToZero)
                target.z = 0f;

            player.transform.position = target;
        }

        player.transform.rotation = Quaternion.Euler(0f, yaw, characterRoll);

        if (charController != null)
            charController.enabled = true;

        StarterAssetsInputs inputs = player.GetComponent<StarterAssetsInputs>();
        if (inputs != null)
        {
            inputs.MoveInput(Vector2.zero);
            inputs.SprintInput(false);
            inputs.JumpInput(false);
        }
    }

    private void ApplyPlayerScale(float scale)
    {
        if (player == null)
            return;

        player.transform.localScale = Vector3.one * Mathf.Max(0f, scale);
    }

    private void SetupCamera(WalkStage stage)
    {
        ResolveCamera();
        if (mainCamera == null)
            return;

        if (camFollow == null)
            camFollow = mainCamera.gameObject.AddComponent<CameraFollowController>();

        camFollow.SetFollowTarget(player != null ? player.transform : null);

        // ZoomedFollow = 仍然跟着玩家，只是换成这一段的偏移 / FOV
        camFollow.SetZoomedFollow(stage.cameraOffset, stage.cameraFOV);

        // 直接就位：换场景后不该看到镜头从上一场的位置滑过来
        camFollow.SnapToCurrentTarget();
    }

    private void SetPlayerRenderersVisible(bool visible)
    {
        if (player == null)
            return;

        if (playerRenderers.Count == 0)
            playerRenderers.AddRange(player.GetComponentsInChildren<Renderer>(true));

        foreach (Renderer renderer in playerRenderers)
        {
            if (renderer != null)
                renderer.enabled = visible;
        }
    }

    // ---------------------------------------------------------------- 台词

    /// <summary>
    /// 屏幕左边中上方那行台词：等画面亮起来 → 渐渐浮出 → 停一会儿 → 渐渐消失。
    /// 和黑幕各走各的：字在画面里，黑幕盖上来自然把它压掉。
    /// </summary>
    private IEnumerator ShowStageMessage(WalkStage stage)
    {
        if (stage == null || string.IsNullOrEmpty(stage.message))
            yield break;

        EnsureMessageUi();
        if (messageText == null)
            yield break;

        messageText.text = stage.message.Replace("\\n", "\n");
        SetMessageAlpha(0f);

        if (messageDelaySeconds > 0f)
            yield return new WaitForSecondsRealtime(messageDelaySeconds);

        yield return FadeMessageRoutine(0f, 1f, Mathf.Max(0f, messageFadeInSeconds));
        yield return new WaitForSecondsRealtime(Mathf.Max(0f, messageHoldSeconds));
        yield return FadeMessageRoutine(1f, 0f, Mathf.Max(0f, messageFadeOutSeconds));
    }

    /// <summary>把台词立刻收掉（这一段提前收尾、或者演出结束时用）。</summary>
    private void HideStageMessage()
    {
        if (messageCoroutine != null)
        {
            StopCoroutine(messageCoroutine);
            messageCoroutine = null;
        }

        if (messageText != null)
            messageText.text = string.Empty;

        SetMessageAlpha(0f);
    }

    private IEnumerator FadeMessageRoutine(float from, float to, float duration)
    {
        if (messageText == null)
            yield break;

        if (duration <= 0f)
        {
            SetMessageAlpha(to);
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            SetMessageAlpha(Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration)));
            yield return null;
        }

        SetMessageAlpha(to);
    }

    private void SetMessageAlpha(float alpha)
    {
        if (messageText == null)
            return;

        float a = Mathf.Clamp01(alpha);
        Color c = messageText.color;
        c.a = a;
        messageText.color = c;

        // 描边要跟着一起淡，否则字淡完了还会剩一圈黑影
        if (messageOutline != null)
        {
            Color oc = messageOutline.effectColor;
            oc.a = a * 0.85f;
            messageOutline.effectColor = oc;
        }
    }

    /// <summary>台词用的 UI：一块覆盖层画布 + 左边中上方一行字（平时全透明）。</summary>
    private void EnsureMessageUi()
    {
        if (messageCanvas != null)
            return;

        GameObject root = new GameObject("GGJ_GiveUpEnding_Line");
        DontDestroyOnLoad(root);

        messageCanvas = root.AddComponent<Canvas>();
        messageCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // 排序要【低于】GameplayHUD(100)：这样渐黑的时候黑幕能把它盖住，
        // 字会跟着画面一起暗下去，而不是浮在黑幕上面。
        messageCanvas.sortingOrder = 50;

        CanvasScaler scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        GameObject textGO = new GameObject("Line", typeof(RectTransform));
        textGO.transform.SetParent(messageCanvas.transform, false);

        RectTransform rt = textGO.GetComponent<RectTransform>();
        float height = Mathf.Clamp01(messageHeightRatio);
        rt.anchorMin = new Vector2(0f, height);
        rt.anchorMax = new Vector2(0f, height);
        rt.pivot = new Vector2(0f, 0.5f);
        rt.anchoredPosition = new Vector2(Mathf.Max(0f, messageLeftMargin), 0f);
        rt.sizeDelta = new Vector2(Mathf.Max(200f, messageWidth), Mathf.Max(40f, messageFontSize * 2.4f));

        messageText = textGO.AddComponent<Text>();
        messageText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ??
                           Resources.GetBuiltinResource<Font>("Arial.ttf");
        messageText.fontSize = Mathf.Max(8, (int)messageFontSize);
        messageText.fontStyle = FontStyle.Bold;
        messageText.color = messageColor;
        messageText.alignment = TextAnchor.MiddleLeft;
        messageText.horizontalOverflow = HorizontalWrapMode.Wrap;
        messageText.verticalOverflow = VerticalWrapMode.Overflow;
        messageText.raycastTarget = false;
        messageText.text = string.Empty;

        messageOutline = textGO.AddComponent<Outline>();
        messageOutline.effectColor = new Color(0f, 0f, 0f, 0.85f);
        messageOutline.effectDistance = new Vector2(2f, -2f);

        SetMessageAlpha(0f);
    }

    // ---------------------------------------------------------------- 输入

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
                playerInput.enabled = false;
        }
#endif
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>玩家 → 相机 的水平方向（单位向量）：能量球往这个方向让开，就不会长在人物身体里被挡住。</summary>
    private Vector3 TowardCameraXZ()
    {
        if (mainCamera != null && player != null)
        {
            Vector3 toCamera = mainCamera.transform.position - player.transform.position;
            toCamera.y = 0f;

            if (toCamera.sqrMagnitude > 1e-4f)
                return toCamera.normalized;
        }

        return Vector3.back;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>按名字找点：先找出生点，再依次找兜底名字（支持嵌套子物体）。</summary>
    private static Transform FindPoint(string name, string[] fallbacks)
    {
        Transform found = FindByName(name);
        if (found != null)
            return found;

        if (fallbacks != null)
        {
            foreach (string fallback in fallbacks)
            {
                found = FindByName(fallback);
                if (found != null)
                    return found;
            }
        }

        return null;
    }

    private static Transform FindByName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || !scene.isLoaded)
            return null;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root == null)
                continue;

            if (root.name == name)
                return root.transform;

            Transform found = FindDeep(root.transform, name);
            if (found != null)
                return found;
        }

        GameObject global = GameObject.Find(name);
        return global != null ? global.transform : null;
    }

    private static Transform FindDeep(Transform parent, string name)
    {
        foreach (Transform child in parent)
        {
            if (child == null)
                continue;

            if (child.name == name)
                return child;

            Transform found = FindDeep(child, name);
            if (found != null)
                return found;
        }

        return null;
    }

    /// <summary>停用“不是当前操作玩家”的其他玩家（目标场景自带的）。</summary>
    private void DisableExtraPlayers()
    {
        if (player == null)
            return;

        foreach (ThirdPersonController other in FindObjectsOfType<ThirdPersonController>(true))
        {
            if (other == null || other == player || other.transform.root == player.transform.root)
                continue;

            other.gameObject.SetActive(false);
        }
    }

    /// <summary>停用不属于“主玩家 / 主相机”的屏幕相机（保留渲染到 RT 的特效相机）。</summary>
    private void DisableExtraCameras()
    {
        if (mainCamera == null)
            return;

        foreach (Camera cam in FindObjectsOfType<Camera>(true))
        {
            if (cam == null || cam == mainCamera || cam.targetTexture != null)
                continue;
            if (cam.transform.root == mainCamera.transform.root)
                continue;
            if (player != null && cam.transform.root == player.transform.root)
                continue;

            cam.gameObject.SetActive(false);
        }
    }

    /// <summary>
    /// 把当前场景里“不属于玩家 / 主相机 / 本导演 / HUD”的所有脚本（MonoBehaviour）全部停用。
    /// 只停用脚本，不动 GameObject、渲染器、碰撞体、刚体、动画机，
    /// 所以地形和布景照常显示、人照样踩在地上，但玩法机制全部哑火。
    /// </summary>
    private void DisableSceneMechanics()
    {
        if (!disableSceneMechanics && !disableTriggerColliders)
            return;

        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || !scene.isLoaded)
            return;

        if (mainCamera == null)
            mainCamera = Camera.main;

        // 这些根下面的东西一律保留：玩家、主相机、导演自己、HUD（后面还要用它渐黑渐亮）
        HashSet<Transform> keepRoots = new HashSet<Transform> { transform.root };
        if (player != null)
            keepRoots.Add(player.transform.root);
        if (mainCamera != null)
            keepRoots.Add(mainCamera.transform.root);
        if (GameplayHUD.Instance != null)
            keepRoots.Add(GameplayHUD.Instance.transform.root);

        HashSet<MonoBehaviour> keepComponents = new HashSet<MonoBehaviour>();
        if (keepEnabledBehaviours != null)
        {
            foreach (MonoBehaviour behaviour in keepEnabledBehaviours)
            {
                if (behaviour != null)
                    keepComponents.Add(behaviour);
            }
        }

        List<Regex> keepPatterns = new List<Regex>();
        if (keepEnabledTypeNames != null)
        {
            foreach (string entry in keepEnabledTypeNames)
            {
                if (string.IsNullOrWhiteSpace(entry))
                    continue;

                keepPatterns.Add(new Regex("^" + Regex.Escape(entry.Trim()).Replace("\\*", ".*") + "$",
                    RegexOptions.IgnoreCase));
            }
        }

        int disabledCount = 0;
        int disabledTriggers = 0;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root == null)
                continue;

            if (disableSceneMechanics)
            {
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null || !behaviour.enabled)
                        continue;

                    if (keepComponents.Contains(behaviour))
                        continue;

                    Transform behaviourRoot = behaviour.transform.root;
                    if (behaviourRoot != null && keepRoots.Contains(behaviourRoot))
                        continue;

                    if (ShouldKeepType(behaviour, keepPatterns))
                        continue;

                    behaviour.enabled = false;
                    disabledCount++;
                }
            }

            // 光停脚本不够：OnTriggerEnter 在脚本被停用后仍会被调用，
            // 死亡区 / 关卡传送区 / 能量球拾取这些“触发器”必须把碰撞体本身关掉才真的哑火。
            if (!disableTriggerColliders)
                continue;

            foreach (Collider col in root.GetComponentsInChildren<Collider>(true))
            {
                if (col == null || !col.isTrigger || !col.enabled)
                    continue;

                Transform colRoot = col.transform.root;
                if (colRoot != null && keepRoots.Contains(colRoot))
                    continue;

                col.enabled = false;
                disabledTriggers++;
            }
        }

        Debug.Log($"[GiveUpEndingDirector] 场景 “{scene.name}” 里已停用 {disabledCount} 个脚本、" +
                  $"{disabledTriggers} 个触发器碰撞体，现在只剩“玩家自动走 + 镜头跟随”。", this);
    }

    private static bool ShouldKeepType(MonoBehaviour behaviour, List<Regex> keepPatterns)
    {
        // 结局这条线上的几个导演 / HUD 是常驻的，可能刚好挂在这个场景中，不能停
        if (behaviour is GiveUpEndingDirector ||
            behaviour is FinaleChoiceDirector ||
            behaviour is EndingSequencePlayer ||
            behaviour is EndingFinaleDirector ||
            behaviour is GameplayHUD ||
            behaviour is CameraFollowController)
            return true;

        if (keepPatterns == null || keepPatterns.Count == 0)
            return false;

        Type type = behaviour.GetType();
        string name = type.Name;
        string fullName = type.FullName;

        foreach (Regex pattern in keepPatterns)
        {
            if (pattern.IsMatch(name) || (fullName != null && pattern.IsMatch(fullName)))
                return true;
        }

        return false;
    }

    private static void MakePersistent(Transform target)
    {
        if (target != null)
            DontDestroyOnLoad(target.root.gameObject);
    }

    private void OnDestroy()
    {
        if (energyBall != null)
            Destroy(energyBall);

        if (messageCanvas != null)
            Destroy(messageCanvas.gameObject);

        IsRunning = false;
    }
}
