using System.Collections;
using System.Collections.Generic;
using StarterAssets;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// 终章实机演出（在 EndingSequencePlayer 的“字幕 + 报幕”之前播放的那段戏）：
///
///   ① 玩家与 P_Machine 交互 → 画面渐黑 →（全黑中）让本关的 Final Scene 出现、
///      玩家就位 → 画面渐亮 → 交还操作，玩家可以自由活动；
///   ② 左上角依次淡入两段文字（“实验体在弹指间疯狂生长” → “那么，代价呢...”），
///      每段自行淡出；期间玩家始终可以自由活动；
///   ③ 第二段文字淡出后 → 再次渐黑 →（全黑中）切换到枯萎关卡（默认 Level2）、
///      把相机锁定到一条“从左往右”的运镜轨道上慢慢横移；
///      屏幕左边中上方随亮屏渐渐浮现一行字（植物疯长的代价），运镜后段再渐渐消失；
///   ④ 随着镜头推进：植物依次枯萎消失、水面缓缓下降、地形上的草逐渐消失；
///   ⑤ 镜头走完后画面渐黑并保持全黑 —— 后面交给 EndingSequencePlayer 继续
///      播字幕与滚动报幕（最后按它自己的配置回标题）。
///
/// 用法：正常不需要手动摆放。EndingSequencePlayer 勾选“播放终章实机演出”后，
/// 运行时会自动创建一个常驻的导演物体（用本脚本的默认值：Level2 / Final Scene /
/// 上面那两句台词）。想微调参数（台词、运镜时长、枯萎速度等），
/// 就在场景里放一个空物体挂上本组件，EndingSequencePlayer 会自动用它。
///
/// 注意：本演出会切换场景，所以导演物体必须是 DontDestroyOnLoad 的
/// （由 EndingSequencePlayer 创建时保证），否则协程会被场景卸载打断。
/// </summary>
[DisallowMultipleComponent]
public class EndingFinaleDirector : MonoBehaviour
{
    /// <summary>终章演出是否正在播放（LevelResetManager 用它屏蔽“按 R 重置”）。</summary>
    public static bool IsRunning { get; private set; }

    // ---------------------------------------------------------------- 配置

    [Header("① Final Scene（在本关里出现）")]
    [Tooltip("要“出现”的那个物体的根节点（Level5 里叫 Final Scene）。\n" +
             "留空 = 运行时按下面的名字在场景里找。")]
    [SerializeField] private GameObject finalSceneRoot;
    [Tooltip("finalSceneRoot 留空时按这个名字查找")]
    [SerializeField] private string finalSceneObjectName = "Final Scene";
    [Tooltip("（可选）黑屏期间把玩家放到这里，保证亮屏时正好看得到 Final Scene。\n" +
             "留空 = 尝试用 Final Scene 下面名为 PlayerPoint / ViewPoint / SpawnPoint 的子物体；" +
             "再没有就按下面的“自动站位”把玩家摆到 Final Scene 左前方。")]
    [SerializeField] private Transform playerViewPoint;
    [Tooltip("没有观看点时，自动把玩家摆到 Final Scene 左前方（保证亮屏就看得到它）")]
    [SerializeField] private bool autoPlacePlayerInFrontOfFinalScene = true;
    [Tooltip("自动站位：站在 Final Scene 左端外多少米")]
    [SerializeField] private float viewDistance = 8f;
    [Tooltip("自动站位：站在 Final Scene 顶部之上多少米（随后自由落到台面上）")]
    [SerializeField] private float spawnHeightAboveFinalScene = 1.2f;
    [Tooltip("玩家本来就站在 Final Scene 附近时就不挪他（免得把人从看戏的位置拽走）")]
    [SerializeField] private bool autoPlaceOnlyWhenFar = true;
    [Tooltip("“附近”的判定距离（米）：玩家离 Final Scene 中心小于这个值就不自动挪")]
    [SerializeField] private float autoPlaceDistanceThreshold = 15f;
    [Tooltip("自动站位后最多等玩家落地多久（秒）；超时就退回原位")]
    [SerializeField] private float groundWaitSeconds = 2f;

    [Header("② 左上角文字（玩家可自由活动）")]
    [SerializeField, TextArea(1, 3)] private string firstLine = "实验体在弹指间疯狂生长";
    [Tooltip("第一段文字淡入后停留多久再淡出（秒）")]
    [SerializeField] private float firstLineHoldSeconds = 3.5f;
    [SerializeField, TextArea(1, 3)] private string secondLine = "那么，代价呢...";
    [Tooltip("第二段文字淡入后停留多久再淡出（秒）")]
    [SerializeField] private float secondLineHoldSeconds = 4f;
    [Tooltip("两段文字之间的间隔（秒）")]
    [SerializeField] private float lineGapSeconds = 0.6f;
    [Tooltip("文字淡入 / 淡出时长（秒）")]
    [SerializeField] private float lineFadeSeconds = 0.8f;
    [SerializeField] private int lineFontSize = 56;
    [SerializeField] private Color lineColor = Color.white;
    [Tooltip("文字位置（像素，相对 1920x1080 参考分辨率）：\n" +
             "x = 离左边多远，y = 相对垂直中心的上下偏移（0 = 正好在左边中间）")]
    [SerializeField] private Vector2 lineAnchoredPosition = new Vector2(80f, 0f);
    [Tooltip("文字区域宽 / 高（像素）")]
    [SerializeField] private float lineWidth = 1400f;
    [SerializeField] private float lineHeight = 200f;

    [Header("②·2 枯萎关卡的那行字（切到 Level2 后显示在屏幕左边中上方）")]
    [Tooltip("枯萎运镜时屏幕左边中上方渐渐浮现的一行字（「隐瞒真相，继续试验」结局的代价）。\n" +
             "留空 = 不显示。")]
    [SerializeField, TextArea(2, 4)] private string witheredLine =
        "实验室里的植物疯狂生长，各界对结果赞不绝口。\n" +
        "可自那以后，城市不再落雨，荒漠一寸寸蔓延——世上再没有一株自然生长的植物。";
    [Tooltip("亮屏后等多久这行字开始浮现（秒）")]
    [SerializeField] private float witheredLineDelaySeconds = 1.5f;
    [Tooltip("这行字淡入 / 淡出时长（秒）")]
    [SerializeField] private float witheredLineFadeSeconds = 1.5f;
    [Tooltip("完全显示后停留多久（秒）。总时长（淡入+停留+淡出）要留出富余，\n" +
             "保证收尾渐黑开始前字就已经淡完了。")]
    [SerializeField] private float witheredLineHoldSeconds = 11f;
    [SerializeField] private int witheredLineFontSize = 44;
    [SerializeField] private Color witheredLineColor = Color.white;
    [Tooltip("这行字离屏幕左边的距离（像素，1920x1080 参考分辨率）")]
    [SerializeField] private float witheredLineLeftMargin = 110f;
    [Tooltip("这行字的高度（0 = 屏幕底边，1 = 顶边；0.66 ≈ 左边中上方）")]
    [SerializeField, Range(0f, 1f)] private float witheredLineHeightRatio = 0.66f;
    [Tooltip("这行字区域宽度（像素），文字超出会自动换行")]
    [SerializeField] private float witheredLineWidth = 1300f;

    [Header("③ 枯萎关卡与运镜")]
    [Tooltip("要切换过去的“枯萎关卡”场景名（须已加入 Build Settings）")]
    [SerializeField] private string witheredSceneName = "Level2";
    [Tooltip("镜头从左端走到右端的总时长（秒）")]
    [SerializeField] private float travelSeconds = 20f;
    [Tooltip("勾选（默认）= 按场景内容自动算一条从左到右的镜头轨道（用下面的高度 / 纵深 / 留白）；\n" +
             "取消勾选 = 用下面手填的起止坐标。")]
    [SerializeField] private bool autoCameraPath = true;
    [SerializeField] private Vector3 cameraPathStart = Vector3.zero;
    [SerializeField] private Vector3 cameraPathEnd = Vector3.zero;
    [Tooltip("镜头朝向（欧拉角，度）。勾选下面的“自动对准内容”时会被覆盖，只作兜底。")]
    [SerializeField] private Vector3 cameraAngles = new Vector3(10f, 0f, 0f);
    [Tooltip("运镜时的视野（FOV）。调小 = 画面更长焦、更“贴”着场景；调大 = 更广角。")]
    [SerializeField] private float cameraFOV = 48f;
    [Tooltip("自动轨道：在“自动缩进”之后再往外多留的空白（米）。\n" +
             "正常用不到 —— 想让起止点再往外一点就填正数，想再往里一点就填负数。")]
    [SerializeField] private float cameraPathMargin = 0f;
    [Tooltip("勾选（默认）= 起止点按“半个画面宽”自动往里缩：\n" +
             "起点从场景左边界【往右】挪，终点从场景右边界【往左】挪，\n" +
             "这样镜头任何时刻都对着场景，画面里不会出现边界外的空白。")]
    [SerializeField] private bool autoInsetByViewWidth = true;
    [Tooltip("自动缩进的比例：1 = 画面边缘正好贴着场景边界（画面完全被场景填满）；\n" +
             "0.8~0.9 = 只往里缩一点（边缘会露出少许场景边界外，但更保险）")]
    [SerializeField] private float viewWidthInsetRatio = 1f;
    [Tooltip("自动缩进的安全网：无论怎么缩，镜头左右行程至少留这么长（米），免得镜头被钉在中点不动")]
    [SerializeField] private float minTravelDistance = 10f;
    [Tooltip("起点在自动缩进的基础上，再往右挪多少（米）")]
    [SerializeField] private float cameraPathStartInset = 0f;
    [Tooltip("终点在自动缩进的基础上，再往左挪多少（米）")]
    [SerializeField] private float cameraPathEndInset = 0f;
    [Tooltip("自动轨道：镜头高度 = 内容【中心】之上多少米。\n" +
             "（不是顶端之上 —— 站得比内容顶还高十几米，镜头就只剩俯视天空，拍不到场景了）")]
    [SerializeField] private float cameraPathHeight = 4f;
    [Tooltip("自动轨道：镜头离内容的纵深下限（米，正值 = 站在 -Z 一侧看向场景）。\n" +
             "实际纵深 = max(这个值, 按 FOV 算出来刚好把内容高度装进画面的距离)，保证一定拍得到。")]
    [SerializeField] private float cameraPathMinDepth = 13f;
    [Tooltip("自动轨道：按 FOV 装下内容高度时，上下各多留的余量（米）")]
    [SerializeField] private float cameraFitPadding = 1.5f;
    [Tooltip("在算出来的纵深上再乘一个系数：<1 = 镜头再往场景里推近一些（画面更满、细节更多）；\n" +
             ">1 = 再往后拉远一点。调太小画面上下会被裁掉，自己看着调。")]
    [SerializeField] private float cameraDepthScale = 0.85f;
    [Tooltip("勾选（默认）= 镜头自动俯视内容中心（自己算俯角）；\n" +
             "取消勾选 = 用上面手填的“镜头朝向”。")]
    [SerializeField] private bool autoAimAtContent = true;
    [Tooltip("切到枯萎关卡、镜头就位后，先全黑停留这么久再淡出（秒）")]
    [SerializeField] private float blackHoldBeforeTravel = 0.4f;
    [Tooltip("勾选（默认）= 切到枯萎关卡时关掉“淋雨”全屏特效（Level2 的 RainControl）。\n" +
             "运镜拍的是枯萎后的世界，不该再有雨。")]
    [SerializeField] private bool disableRainOnWitheredScene = true;
    [Tooltip("要关掉的淋雨特效物体名（RainControl）。\n" +
             "留空 = 关掉场景里所有 FullScreenPassFader（全屏特效控制器）。")]
    [SerializeField] private string rainObjectName = "RainControl";
    [Tooltip("切到枯萎关卡后、亮屏之前，先在【黑屏里】把镜头摆好并空转这么多帧：\n" +
             "新场景第一次被渲染时的贴图上传 / 着色器编译，就全被藏在黑屏里了，进关卡不会再卡一下。\n" +
             "还卡就往上加（比如 8）。")]
    [SerializeField] private int warmupFramesBeforeFadeIn = 4;
    [Tooltip("勾选 = 预热期间额外调一次 Shader.WarmupAllShaders()（把所有 Shader 变体编译也塞进黑屏）。\n" +
             "一般不需要，编译特别卡的项目再打开。")]
    [SerializeField] private bool warmupAllShaders = false;

    [Header("④ 万物枯萎")]
    [Tooltip("植物父节点的名字（默认 Plants）")]
    [SerializeField] private string plantsRootName = "Plants";
    [Tooltip("水面物体的名字（默认 Water）")]
    [SerializeField] private string waterObjectName = "Water";
    [Tooltip("镜头走完全程时水面下降多少米")]
    [SerializeField] private float waterDropDistance = 5f;
    [Tooltip("镜头离水面还有多远（米）时，水面【开始】下沉（和植物的枯萎一个套路）。\n" +
             "给小一点 = 镜头都快怼到跟前了才开始沉（保证镜头一定会录到“还有水”和水位下降的趋势）。")]
    [SerializeField] private float waterDropAheadDistance = 10f;
    [Tooltip("水面从开始下沉到完全沉下去的时长（秒）。\n" +
             "现在给得比较长（16 秒）：镜头走到跟前时水位基本还是满的，之后能一路看着它慢慢退下去。\n" +
             "想让它真的在画面里“沉到底”就调小（8 秒左右）。")]
    [SerializeField] private float waterDropSeconds = 16f;
    [Tooltip("单株植物枯萎（缩没）的时长（秒）。\n" +
             "太短会变成“镜头一扫，植物啪一下没了”；给到 3 秒左右，画面里能看到“正在枯萎”的过程。")]
    [SerializeField] private float plantVanishSeconds = 3f;
    [Tooltip("镜头推进到植物前方多少米时，这株植物【开始】枯萎。\n" +
             "给得比 0 大，植物就是在镜头【前方可见范围内】慢慢枯萎的（能录到趋势），\n" +
             "而不是等镜头怼到脸上才瞬间消失。")]
    [SerializeField] private float plantVanishAheadDistance = 16f;
    [Tooltip("枯萎时同时往下沉一点（更像“塌下去”）")]
    [SerializeField] private bool plantsSinkWhileVanishing = true;
    [SerializeField] private float plantSinkDistance = 0.6f;
    [Tooltip("地形上草的可见距离：从多少米开始（镜头刚出发时）")]
    [SerializeField] private float grassDetailStartDistance = 90f;
    [Tooltip("地形上草的可见距离：最后降到多少米（0 = 草全部消失）")]
    [SerializeField] private float grassDetailEndDistance = 0f;
    [Tooltip("草在镜头走到全程的百分之多少时就【已经消失完】（0.55 = 走一半多点草就没了）。\n" +
             "比 1 小 = 草退场得比水面 / 植物更快。")]
    [SerializeField] private float grassFadeFinishProgress = 0.55f;
    [Tooltip("草的可见距离每变化这么多米才真正写一次地形。\n" +
             "写一次就会重建一遍草的可见范围，每帧都写会一卡一卡的；给个 4 米左右就够顺了。")]
    [SerializeField] private float grassDetailStep = 4f;
    [Tooltip("勾选（默认）= 黑幕开始淡出的【同时】镜头就起步：亮屏时画面已经在动，\n" +
             "不会先给你一拍一动不动的定镜。取消勾选 = 等亮屏结束后镜头才开始走。")]
    [SerializeField] private bool startTravelWithFadeIn = true;
    [Tooltip("镜头【还没走完】就开始渐黑：提前这么多秒起步（秒）。\n" +
             "0 = 等镜头走完（再停 endHoldSeconds）才渐黑。")]
    [SerializeField] private float fadeOutLeadSeconds = 1.6f;
    [Tooltip("fadeOutLeadSeconds 为 0 时：镜头走完后、渐黑前再停留一会（秒）")]
    [SerializeField] private float endHoldSeconds = 0.6f;

    [Header("⑤ 黑屏节奏（秒）")]
    [SerializeField] private float fadeToBlackSeconds = 1.0f;
    [SerializeField] private float fadeFromBlackSeconds = 1.2f;

    // ---------------------------------------------------------------- 运行时

    private class PlantState
    {
        public Transform transform;
        public float triggerX;
        public Vector3 baseScale = Vector3.one;
        public Vector3 basePosition;
        public float timer;
        public bool started;
        public bool done;
    }

    private class WaterState
    {
        public Transform transform;
        public float baseY;
        public float triggerX;
        public float timer;
        public bool started;
    }

    private class TerrainState
    {
        public Terrain terrain;
        public float baseDetailDistance;
    }

    private Canvas canvas;
    private Text lineText;
    private Outline lineOutline;

    /// <summary>枯萎关卡那行字（屏幕左边中上方）+ 它的播放协程。</summary>
    private Text witheredText;
    private Outline witheredOutline;
    private Coroutine witheredLineRoutine;

    private ThirdPersonController player;
    private bool hadInputEnabled = true;
    private bool inputWasChanged;

    private readonly List<PlantState> plants = new List<PlantState>();
    private readonly List<WaterState> waters = new List<WaterState>();
    private readonly List<TerrainState> terrains = new List<TerrainState>();

    /// <summary>上一次真正写进地形的草可见距离（用来节流：变化不明显就不写）。</summary>
    private float appliedGrassDistance = float.MinValue;

    // 运镜轨道（黑屏期间就准备好，亮屏后只做插值）
    private Transform cameraDolly;
    private CameraFollowController lockedFollow;
    private Vector3 cameraPathStartPoint;
    private Vector3 cameraPathEndPoint;

    // ---------------------------------------------------------------- 入口

    /// <summary>
    /// 播放整段终章演出。由 EndingSequencePlayer 在字幕 / 报幕之前调用。
    /// owner 传调用方（通常是 P_Machine 上的 EndingSequencePlayer）：切场景前会把它的根节点
    /// 挪到看不见的地方，免得它在枯萎关卡里露脸。
    /// </summary>
    public IEnumerator PlayFinale(EndingSequencePlayer owner)
    {
        if (IsRunning)
            yield break;

        IsRunning = true;

        // 演出期间屏蔽“按 E 交互”，避免玩家乱按触发别的可交互物体打断演出
        bool previousSuppress = InteractableObject.SuppressInteractionInput;
        InteractableObject.SuppressInteractionInput = true;

        try
        {
            GameplayHUD.EnsureCreated();
            GameplayHUD hud = GameplayHUD.Instance;

            ResolvePlayer();

            // —— 第一段：渐黑 → Final Scene 出现 → 渐亮 → 玩家可以自由活动 ——
            DisablePlayerInput();

            if (hud != null)
                yield return hud.FadeToBlackRoutine(fadeToBlackSeconds);

            // （全黑中）让 Final Scene 出现 + 玩家就位；自动站位会等到落稳再继续
            yield return RevealFinalSceneRoutine();

            SnapCameraToPlayer();

            yield return null;
            yield return null;

            if (hud != null)
                yield return hud.FadeFromBlackRoutine(fadeFromBlackSeconds);

            // 交还操作：接下来两段文字播完之前，玩家都可以自由活动
            EnablePlayerInput();

            // —— 第二段：左上角两段文字 ——
            EnsureCanvas();

            yield return ShowLineRoutine(firstLine, firstLineHoldSeconds);
            if (lineGapSeconds > 0f)
                yield return new WaitForSeconds(lineGapSeconds);
            yield return ShowLineRoutine(secondLine, secondLineHoldSeconds);

            HideLine();

            // —— 第三段：渐黑 → 切到枯萎关卡 → 横移运镜 + 万物枯萎 → 渐黑 ——
            DisablePlayerInput();

            if (hud != null)
                yield return hud.FadeToBlackRoutine(fadeToBlackSeconds);

            yield return PlayWitheredTravel(owner);

            // 结束时画面保持全黑，交给 EndingSequencePlayer 接字幕与报幕
        }
        finally
        {
            InteractableObject.SuppressInteractionInput = previousSuppress;
            IsRunning = false;
        }
    }

    // ---------------------------------------------------------------- 第一段

    private IEnumerator RevealFinalSceneRoutine()
    {
        if (finalSceneRoot == null && !string.IsNullOrEmpty(finalSceneObjectName))
            finalSceneRoot = FindObjectEvenIfInactive(finalSceneObjectName);

        if (finalSceneRoot == null)
        {
            Debug.LogWarning($"[EndingFinaleDirector] 场景里没有找到 Final Scene（名字“{finalSceneObjectName}”），跳过“出现”这一步。", this);
            yield break;
        }

        if (!finalSceneRoot.activeSelf)
            finalSceneRoot.SetActive(true);

        if (player == null)
            yield break;

        // 玩家站位：① 自己拖的观看点 → ② Final Scene 下名为 PlayerPoint / ViewPoint / SpawnPoint 的子物体
        //           → ③ 自动站到 Final Scene 左前方（保证亮屏就看得到它）
        Transform viewPoint = playerViewPoint;
        if (viewPoint == null)
            viewPoint = FindChildByName(finalSceneRoot.transform, "PlayerPoint", "ViewPoint", "SpawnPoint");

        Vector3 target;
        float facingY;

        if (viewPoint != null)
        {
            target = viewPoint.position;
            facingY = viewPoint.eulerAngles.y;
        }
        else if (autoPlacePlayerInFrontOfFinalScene &&
                 TryGetContentBounds(finalSceneRoot.transform, out Bounds bounds))
        {
            // 玩家本来就站在 Final Scene 附近时不要挪他：亮屏直接就能看见，别把人从看戏的位置拽走
            float distanceToFinalScene = Mathf.Abs(player.transform.position.x - bounds.center.x);
            if (autoPlaceOnlyWhenFar && distanceToFinalScene < autoPlaceDistanceThreshold)
                yield break;

            target = new Vector3(bounds.min.x - viewDistance,
                                 bounds.max.y + spawnHeightAboveFinalScene,
                                 bounds.center.z);
            // 面朝右（关卡的前进方向）看向 Final Scene
            facingY = player.RightFacingAngle;
        }
        else
        {
            yield break;
        }

        Vector3 origin = player.transform.position;
        TeleportPlayer(target, facingY);

        // 落点可能是半空：黑幕里等它落稳；真悬空就退回原位，别把玩家丢出关卡
        CharacterController cc = player.GetComponent<CharacterController>();
        float deadline = Time.unscaledTime + Mathf.Max(0.1f, groundWaitSeconds);
        bool grounded = false;

        yield return null;
        yield return null;

        while (Time.unscaledTime < deadline)
        {
            grounded = cc != null ? cc.isGrounded : player.Grounded;
            if (grounded)
                break;

            yield return null;
        }

        if (!grounded)
        {
            Debug.LogWarning("[EndingFinaleDirector] 自动站位后玩家没落到地面（Final Scene 悬空？），已退回交互时的位置。\n" +
                             "可以在 Final Scene 下面放一个名为 PlayerPoint 的空物体指定站位，或把本组件的 Player View Point 拖上去。", this);
            TeleportPlayer(origin, player.RightFacingAngle);
            yield return null;
        }
    }

    private void TeleportPlayer(Vector3 position, float facingY)
    {
        if (player == null)
            return;

        CharacterController cc = player.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;

        player.transform.position = position;
        player.transform.rotation = Quaternion.Euler(0f, facingY, 0f);

        if (cc != null) cc.enabled = true;
    }

    /// <summary>取一个物体（含子物体）的世界包围盒：优先 Renderer，没有就退回 Collider。</summary>
    private static bool TryGetContentBounds(Transform root, out Bounds bounds)
    {
        bounds = new Bounds();
        if (root == null)
            return false;

        bool found = false;

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(false);
        foreach (Renderer r in renderers)
        {
            if (r == null)
                continue;

            if (!found)
            {
                bounds = r.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(r.bounds);
            }
        }

        if (found)
            return true;

        Collider[] colliders = root.GetComponentsInChildren<Collider>(false);
        foreach (Collider c in colliders)
        {
            if (c == null)
                continue;

            if (!found)
            {
                bounds = c.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(c.bounds);
            }
        }

        return found;
    }

    private void SnapCameraToPlayer()
    {
        CameraFollowController follow = CameraFollowController.Instance;
        if (follow == null)
        {
            Camera cam = Camera.main;
            if (cam != null)
                follow = cam.GetComponent<CameraFollowController>();
        }

        if (follow != null)
            follow.SnapToCurrentTarget();
    }

    // ---------------------------------------------------------------- 文字

    private IEnumerator ShowLineRoutine(string content, float holdSeconds)
    {
        if (lineText == null || string.IsNullOrEmpty(content))
            yield break;

        lineText.text = content;
        lineText.enabled = true;
        SetLineAlpha(0f);

        yield return FadeLineRoutine(0f, 1f, lineFadeSeconds);

        if (holdSeconds > 0f)
            yield return new WaitForSeconds(holdSeconds);

        yield return FadeLineRoutine(1f, 0f, lineFadeSeconds);

        lineText.text = string.Empty;
        lineText.enabled = false;
        SetLineAlpha(0f);
    }

    private void HideLine()
    {
        if (lineText == null)
            return;

        lineText.text = string.Empty;
        lineText.enabled = false;
        SetLineAlpha(0f);
    }

    private IEnumerator FadeLineRoutine(float from, float to, float duration)
    {
        if (duration <= 0f)
        {
            SetLineAlpha(to);
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            SetLineAlpha(Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration)));
            yield return null;
        }
        SetLineAlpha(to);
    }

    private void SetLineAlpha(float alpha)
    {
        if (lineText == null)
            return;

        float a = Mathf.Clamp01(alpha);
        Color c = lineText.color;
        c.a = a;
        lineText.color = c;

        if (lineOutline != null)
        {
            Color oc = lineOutline.effectColor;
            oc.a = a * 0.85f;
            lineOutline.effectColor = oc;
        }
    }

    // ---------------------------------------------------------------- 枯萎关卡的那行字

    /// <summary>
    /// 屏幕左边中上方那行字：等画面亮起来 → 渐渐浮现 → 停一会儿 → 渐渐消失。
    /// 时间轴对齐运镜：总时长 = 延迟 + 淡入 + 停留 + 淡出，要短于运镜全程。
    /// </summary>
    private IEnumerator ShowWitheredLineRoutine()
    {
        if (witheredText == null)
            yield break;

        witheredText.text = witheredLine.Replace("\\n", "\n");
        witheredText.enabled = true;
        SetWitheredLineAlpha(0f);

        if (witheredLineDelaySeconds > 0f)
            yield return new WaitForSeconds(witheredLineDelaySeconds);

        yield return FadeWitheredLineRoutine(0f, 1f, witheredLineFadeSeconds);

        if (witheredLineHoldSeconds > 0f)
            yield return new WaitForSeconds(witheredLineHoldSeconds);

        yield return FadeWitheredLineRoutine(1f, 0f, witheredLineFadeSeconds);

        HideWitheredLine();
    }

    /// <summary>把那行字立刻收掉（运镜提前收尾 / 渐黑前用）。</summary>
    private void HideWitheredLine()
    {
        if (witheredText == null)
            return;

        witheredText.text = string.Empty;
        witheredText.enabled = false;
        SetWitheredLineAlpha(0f);
    }

    private IEnumerator FadeWitheredLineRoutine(float from, float to, float duration)
    {
        if (duration <= 0f)
        {
            SetWitheredLineAlpha(to);
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            SetWitheredLineAlpha(Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration)));
            yield return null;
        }
        SetWitheredLineAlpha(to);
    }

    private void SetWitheredLineAlpha(float alpha)
    {
        if (witheredText == null)
            return;

        float a = Mathf.Clamp01(alpha);
        Color c = witheredText.color;
        c.a = a;
        witheredText.color = c;

        // 描边跟着一起淡，否则字淡完了还会剩一圈黑影
        if (witheredOutline != null)
        {
            Color oc = witheredOutline.effectColor;
            oc.a = a * 0.85f;
            witheredOutline.effectColor = oc;
        }
    }

    // ---------------------------------------------------------------- 第三段：枯萎关卡运镜

    private IEnumerator PlayWitheredTravel(EndingSequencePlayer owner)
    {
        ResolvePlayer();

        Camera mainCamera = Camera.main;

        // 演出期间玩家不参与画面：关掉碰撞与渲染，挪到镜头绝对拍不到的地方
        HidePlayerForCinematic();

        // 主人（通常是 P_Machine）也要挪走：它被设为常驻了，否则会留在枯萎关卡里
        if (owner != null)
            MoveOutOfSight(owner.transform.root);

        // 运镜画面里不需要左上角能量格 / 耐力条
        GameplayHUD.SetGameplayUiVisibleIfExists(false);

        if (string.IsNullOrEmpty(witheredSceneName) || !Application.CanStreamedLevelBeLoaded(witheredSceneName))
        {
            Debug.LogError($"[EndingFinaleDirector] 枯萎关卡 “{witheredSceneName}” 不在 Build Settings 中，跳过运镜直接进字幕。\n" +
                           "请把它加入 Build Settings（可执行菜单 GGJ2026/关卡/把关卡加入 Build Settings）。", this);
            yield break;
        }

        // 跨场景保留：玩家 / 主相机 / 本导演（导演由 EndingSequencePlayer 创建时已常驻，这里再兜一次底）
        if (player != null)
            DontDestroyOnLoad(player.transform.root.gameObject);
        if (mainCamera != null)
            DontDestroyOnLoad(mainCamera.transform.root.gameObject);
        DontDestroyOnLoad(gameObject.transform.root.gameObject);

        // 加载期间屏蔽相机触发区 / 提示区，避免新场景的触发区把镜头或文字抢走
        CameraTriggerVolume.SuppressCameraSwitching = true;
        MessageTriggerZone.SuppressMessages = true;

        AsyncOperation load = SceneManager.LoadSceneAsync(witheredSceneName, LoadSceneMode.Single);
        while (load != null && !load.isDone)
            yield return null;

        // 等新场景物体激活完成
        yield return null;
        yield return null;

        // 清掉目标场景自带的多余玩家 / 相机，画面里只留下常驻主相机拍的运镜
        // （每步之间让出一帧，把一次性开销摊开，别挤在同一帧里）
        DisableExtraPlayers();
        yield return null;

        DisableExtraCameras(mainCamera);
        yield return null;

        // 关掉“淋雨”全屏特效（此时画面还是全黑，怎么关都看不见跳变）
        DisableRainEffect();

        // 【消卡顿的关键】趁画面还全黑：收集目标 + 建好轨道 + 把相机摆到起点，
        // 然后空转几帧 —— 新场景第一次被渲染时的贴图上传 / 着色器编译全发生在黑屏里，
        // 亮屏之后就直接是顺滑的运镜，不会再“进关卡卡一下”。
        PrepareFinaleCamera(mainCamera);
        yield return WarmupFramesRoutine();

        GameplayHUD hud = GameplayHUD.Instance;

        if (blackHoldBeforeTravel > 0f)
            yield return new WaitForSeconds(blackHoldBeforeTravel);

        // 亮出枯萎关卡（此时镜头已就位）
        // 默认让“亮屏”和“运镜”同时起步：画面还没全亮，镜头已经在动，
        // 不会先晾一拍定镜（想改回“亮完再走”就把 startTravelWithFadeIn 取消勾选）。
        Coroutine travelRoutine = startTravelWithFadeIn
            ? StartCoroutine(TravelCameraRoutine(mainCamera))
            : null;

        // 枯萎关卡的那行字：屏幕左边中上方随亮屏渐渐浮现，运镜走到后段再渐渐消失
        witheredLineRoutine = string.IsNullOrEmpty(witheredLine)
            ? null
            : StartCoroutine(ShowWitheredLineRoutine());

        if (hud != null)
            yield return hud.FadeFromBlackRoutine(fadeFromBlackSeconds);

        if (travelRoutine != null)
        {
            // 镜头还在走 —— 快到终点时就开始渐黑（见 fadeOutLeadSeconds）
            float lead = Mathf.Max(0f, fadeOutLeadSeconds);
            float elapsedTravel = Mathf.Max(0f, fadeFromBlackSeconds);
            float waitBeforeFade = Mathf.Max(0f, Mathf.Max(0.1f, travelSeconds) - elapsedTravel - lead);

            if (waitBeforeFade > 0f)
                yield return new WaitForSeconds(waitBeforeFade);
        }
        else
        {
            yield return TravelCameraRoutine(mainCamera);

            if (endHoldSeconds > 0f)
                yield return new WaitForSeconds(endHoldSeconds);
        }

        // 收尾渐黑前把那行字收干净（正常情况它早就淡完了，这里是保险：
        // 这块画布在黑幕之上，不收掉的话字会浮在渐黑的画面上）
        if (witheredLineRoutine != null)
        {
            StopCoroutine(witheredLineRoutine);
            witheredLineRoutine = null;
        }
        HideWitheredLine();

        // 收尾渐黑：镜头还没停稳就已经在变黑了（之后交给 EndingSequencePlayer 的字幕 / 报幕）
        if (hud != null)
            yield return hud.FadeToBlackRoutine(fadeToBlackSeconds);

        // 等运镜真正走完再收工（渐黑比镜头早起步，这里把剩下的尾巴补掉）
        if (travelRoutine != null)
            yield return travelRoutine;
    }

    private IEnumerator TravelCameraRoutine(Camera mainCamera)
    {
        // 正常情况下黑屏期间就已经准备好了（PrepareFinaleCamera），这里只是兜底
        if (cameraDolly == null)
            PrepareFinaleCamera(mainCamera);

        Transform dolly = cameraDolly;
        if (dolly == null)
            yield break;

        Vector3 start = cameraPathStartPoint;
        Vector3 end = cameraPathEndPoint;

        float duration = Mathf.Max(0.1f, travelSeconds);
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);

            dolly.position = Vector3.Lerp(start, end, t);

            UpdatePlants(dolly.position.x, Time.deltaTime);
            UpdateWater(dolly.position.x, Time.deltaTime);
            UpdateGrass(t);

            yield return null;
        }

        dolly.position = end;

        // 收尾：镜头走完了还没枯萎的，直接抹平
        FinishWither();

        if (lockedFollow != null)
            lockedFollow.SetLockedPoint(dolly, cameraFOV);
    }

    /// <summary>
    /// 在【黑屏期间】把运镜准备好：收集枯萎目标 → 算出轨道 → 建轨道物体 → 锁定相机并 Snap。
    /// 这样新场景的第一次渲染发生在黑屏里，进关卡时不会再卡一下。
    /// </summary>
    private void PrepareFinaleCamera(Camera mainCamera)
    {
        CollectWitherTargets();

        CameraFollowController follow = mainCamera != null
            ? mainCamera.GetComponent<CameraFollowController>()
            : null;
        if (follow == null)
            follow = CameraFollowController.Instance;

        Vector3 start;
        Vector3 end;
        Vector3 aim;
        ResolveCameraPath(out start, out end, out aim);

        // 镜头轨道：一个跟着本导演走的空物体，相机锁定它 → 移动它就等于运镜
        GameObject dollyGO = new GameObject("GGJ_FinaleCameraDolly");
        Transform dolly = dollyGO.transform;
        dolly.SetParent(transform, false);
        dolly.position = start;

        // 朝向：默认自动俯视内容中心（自己算俯角，机位再高也不会拍到天上去）；
        // 关掉自动对准才用 Inspector 里手填的欧拉角。
        Vector3 lookDirection = autoAimAtContent ? aim - (start + end) * 0.5f : Vector3.zero;
        dolly.rotation = autoAimAtContent && lookDirection.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(lookDirection.normalized, Vector3.up)
            : Quaternion.Euler(cameraAngles);

        if (follow != null)
        {
            follow.SetLockedPoint(dolly, cameraFOV);
            follow.SnapToCurrentTarget();
        }

        if (mainCamera != null)
            mainCamera.fieldOfView = cameraFOV;

        cameraDolly = dolly;
        lockedFollow = follow;
        cameraPathStartPoint = start;
        cameraPathEndPoint = end;
    }

    /// <summary>黑屏里空转几帧，让新场景真正被渲染一遍（把编译 / 上传的开销吃掉）。</summary>
    private IEnumerator WarmupFramesRoutine()
    {
        if (warmupAllShaders)
        {
            Shader.WarmupAllShaders();
            yield return null;
        }

        for (int i = 0; i < Mathf.Max(0, warmupFramesBeforeFadeIn); i++)
            yield return new WaitForEndOfFrame();
    }

    /// <summary>收集枯萎目标：植物 / 水面 / 带草的地形。</summary>
    private void CollectWitherTargets()
    {
        plants.Clear();
        waters.Clear();
        terrains.Clear();
        appliedGrassDistance = float.MinValue;

        // —— 植物 ——
        List<Transform> units = new List<Transform>();
        GameObject plantsRoot = string.IsNullOrEmpty(plantsRootName) ? null : GameObject.Find(plantsRootName);

        if (plantsRoot != null)
        {
            for (int i = 0; i < plantsRoot.transform.childCount; i++)
                units.Add(plantsRoot.transform.GetChild(i));

            // 只有一层（或根本没有子节点）时，退一步取所有带 Renderer 的后代，避免“整片一起消失”
            if (units.Count <= 1)
            {
                units.Clear();
                Renderer[] renderers = plantsRoot.GetComponentsInChildren<Renderer>(true);
                foreach (Renderer r in renderers)
                {
                    if (r != null && !units.Contains(r.transform))
                        units.Add(r.transform);
                }
            }
        }

        foreach (Transform unit in units)
        {
            if (unit == null || !unit.gameObject.activeInHierarchy)
                continue;

            plants.Add(new PlantState
            {
                transform = unit,
                triggerX = unit.position.x,
                baseScale = unit.localScale,
                basePosition = unit.position
            });
        }

        // 按 x 从小到大：镜头从左往右推进时依次枯萎
        plants.Sort((a, b) => a.triggerX.CompareTo(b.triggerX));

        // —— 水面（可能有多个同名水面，遍历全场景而不是只取第一个）——
        if (!string.IsNullOrEmpty(waterObjectName))
        {
            Transform[] all = FindObjectsOfType<Transform>(false);
            foreach (Transform t in all)
            {
                if (t != null && t.name == waterObjectName)
                {
                    waters.Add(new WaterState
                    {
                        transform = t,
                        baseY = t.position.y,
                        triggerX = t.position.x
                    });
                }
            }
        }

        // —— 地形上的草 ——
        Terrain[] terrainArray = FindObjectsOfType<Terrain>(false);
        foreach (Terrain terrain in terrainArray)
        {
            if (terrain == null)
                continue;

            terrains.Add(new TerrainState
            {
                terrain = terrain,
                baseDetailDistance = terrain.detailObjectDistance
            });
        }

        Debug.Log($"[EndingFinaleDirector] 枯萎目标：植物 {plants.Count} 株 / 水面 {waters.Count} 个 / 地形 {terrains.Count} 块。", this);
    }

    /// <summary>
    /// 确定镜头轨道：优先手填坐标，否则按场景内容自动算一条从左到右的横线。
    /// 同时给出“要看向的目标点”，供镜头自动俯视内容（免得机位偏高拍不到东西）。
    /// </summary>
    private void ResolveCameraPath(out Vector3 start, out Vector3 end, out Vector3 aim)
    {
        bool manual = !autoCameraPath &&
                      (cameraPathStart != Vector3.zero || cameraPathEnd != Vector3.zero);

        if (manual)
        {
            start = cameraPathStart;
            end = cameraPathEnd;
            aim = (start + end) * 0.5f + Vector3.down * 10f;
            return;
        }

        Bounds content = new Bounds();
        bool hasContent = false;

        foreach (PlantState plant in plants)
        {
            if (plant.transform == null)
                continue;

            if (!hasContent)
            {
                content = new Bounds(plant.transform.position, Vector3.zero);
                hasContent = true;
            }
            else
            {
                content.Encapsulate(plant.transform.position);
            }
        }

        if (!hasContent)
        {
            foreach (TerrainState terrainState in terrains)
            {
                if (terrainState.terrain == null)
                    continue;

                Bounds tb = terrainState.terrain.terrainData.bounds;
                Vector3 center = terrainState.terrain.transform.position + tb.center;
                Bounds world = new Bounds(center, terrainState.terrain.terrainData.size);

                if (!hasContent)
                {
                    content = world;
                    hasContent = true;
                }
                else
                {
                    content.Encapsulate(world);
                }
            }
        }

        if (!hasContent)
        {
            // 什么都没找到：给一条默认的横线，至少镜头会动
            content = new Bounds(Vector3.zero, new Vector3(60f, 10f, 10f));
        }

        // 高度：内容【中心】之上一点点（不是顶端之上十几米 —— 那样镜头只剩俯视天空，拍不到场景）
        float y = content.center.y + cameraPathHeight;

        // 纵深：至少 cameraPathMinDepth，同时保证内容高度能被 FOV 装进画面
        float halfFov = Mathf.Max(5f, cameraFOV) * 0.5f * Mathf.Deg2Rad;
        float fitDistance = (content.size.y * 0.5f + Mathf.Max(0f, cameraFitPadding)) / Mathf.Tan(halfFov);
        float depth = Mathf.Max(Mathf.Max(1f, cameraPathMinDepth), fitDistance) * Mathf.Max(0.05f, cameraDepthScale);

        float z = content.center.z - depth;

        // 起止点：默认按“半个画面宽”往里缩 ——
        // 起点从场景左边界往右挪、终点从场景右边界往左挪，镜头从头到尾都对着场景，
        // 不会像以前那样先把镜头摆在场景外面（画面里一半是空白）再一路走到另一头外面。
        float aspect = 16f / 9f;
        Camera cam = Camera.main;
        if (cam != null && cam.aspect > 0.01f)
            aspect = cam.aspect;

        float halfViewWidth = depth * Mathf.Tan(halfFov) * aspect;
        float inset = autoInsetByViewWidth ? halfViewWidth * Mathf.Max(0f, viewWidthInsetRatio) : 0f;

        // 安全网：场景本身没多宽时，缩进别把镜头“钉死”在中点 —— 至少留这么长的行程
        float maxInset = Mathf.Max(0f, (content.size.x - Mathf.Max(0f, minTravelDistance)) * 0.5f);
        inset = Mathf.Min(inset, maxInset);

        float from = content.min.x + inset + cameraPathStartInset - cameraPathMargin;
        float to = content.max.x - inset - cameraPathEndInset + cameraPathMargin;

        // 场景比一屏还窄（缩进过头了）：退到停在中点，别让起点跑到终点右边去
        if (from > to)
        {
            float middle = (from + to) * 0.5f;
            from = middle;
            to = middle;
        }

        start = new Vector3(from, y, z);
        end = new Vector3(to, y, z);
        aim = new Vector3(content.center.x, content.center.y, content.center.z);

        Debug.Log($"[EndingFinaleDirector] 运镜轨道：x {from:F1} → {to:F1}（场景 x {content.min.x:F1} ~ {content.max.x:F1}，" +
                  $"缩进 {inset:F1}），机位高 {y:F1}，纵深 {depth:F1}，看向 {aim}。", this);
    }

    private void UpdatePlants(float cameraX, float deltaTime)
    {
        float duration = Mathf.Max(0.01f, plantVanishSeconds);
        float trigger = cameraX + plantVanishAheadDistance;

        for (int i = 0; i < plants.Count; i++)
        {
            PlantState plant = plants[i];
            if (plant.done || plant.transform == null)
                continue;

            if (!plant.started)
            {
                if (trigger < plant.triggerX)
                    continue;
                plant.started = true;
            }

            plant.timer += deltaTime;
            float k = Mathf.Clamp01(plant.timer / duration);

            // 平滑起落（SmoothStep）：开头慢慢蔫、末尾慢慢没，比线性“匀速缩小”更像枯萎
            float shrink = Mathf.SmoothStep(0f, 1f, k);
            plant.transform.localScale = plant.baseScale * (1f - shrink);

            if (plantsSinkWhileVanishing)
                plant.transform.position = plant.basePosition + Vector3.down * (plantSinkDistance * k);

            if (k >= 1f)
            {
                plant.done = true;
                plant.transform.localScale = Vector3.zero;
                plant.transform.gameObject.SetActive(false);
            }
        }
    }

    /// <summary>
    /// 水面下沉：和植物一样按“镜头推进到它前面多远”触发，
    /// 免得镜头还在半路上、水就已经沉到底了（以前是按整体进度插值，出发瞬间就开始沉）。
    /// </summary>
    private void UpdateWater(float cameraX, float deltaTime)
    {
        float duration = Mathf.Max(0.01f, waterDropSeconds);
        float trigger = cameraX + waterDropAheadDistance;

        foreach (WaterState water in waters)
        {
            if (water.transform == null)
                continue;

            if (!water.started)
            {
                if (trigger < water.triggerX)
                    continue;
                water.started = true;
            }

            water.timer += deltaTime;
            float k = Mathf.Clamp01(water.timer / duration);

            // 平滑起落：先慢慢降、末尾慢慢停，比匀速下沉更像“水位退下去”
            float drop = waterDropDistance * Mathf.SmoothStep(0f, 1f, k);

            Vector3 p = water.transform.position;
            p.y = water.baseY - drop;
            water.transform.position = p;
        }
    }

    private void UpdateGrass(float progress)
    {
        // 草比水面 / 植物退场得早：在 grassFadeFinishProgress 处就已经消失完
        float k = Mathf.Clamp01(progress / Mathf.Max(0.01f, grassFadeFinishProgress));
        float distance = Mathf.Lerp(grassDetailStartDistance, grassDetailEndDistance, k);

        // 每写一次 detailObjectDistance，地形就要重建一遍草的可见范围（不便宜）：
        // 只有变化明显（或已经该收尾）时才真的写，免得每帧都卡一下。
        bool needFinalWrite = k >= 1f;
        if (!needFinalWrite &&
            appliedGrassDistance != float.MinValue &&
            Mathf.Abs(distance - appliedGrassDistance) < Mathf.Max(0.5f, grassDetailStep))
            return;

        appliedGrassDistance = distance;

        foreach (TerrainState terrainState in terrains)
        {
            if (terrainState.terrain == null)
                continue;

            terrainState.terrain.detailObjectDistance = distance;
        }
    }

    private void FinishWither()
    {
        foreach (PlantState plant in plants)
        {
            if (plant.done || plant.transform == null)
                continue;

            plant.done = true;
            plant.transform.localScale = Vector3.zero;
            plant.transform.gameObject.SetActive(false);
        }

        // 只把“镜头还没走到、压根没开始下沉”的水面直接压到底（反正画面里看不见）；
        // 已经在慢慢沉的不动它 —— 硬拉到底会在镜头前“啪”地跳一下。
        foreach (WaterState water in waters)
        {
            if (water.transform == null || water.started)
                continue;

            Vector3 p = water.transform.position;
            p.y = water.baseY - waterDropDistance;
            water.transform.position = p;
        }

        foreach (TerrainState terrainState in terrains)
        {
            if (terrainState.terrain != null)
                terrainState.terrain.detailObjectDistance = grassDetailEndDistance;
        }
    }

    // ---------------------------------------------------------------- 玩家 / 相机 / 场景

    private void HidePlayerForCinematic()
    {
        if (player == null)
            return;

        // 关掉碰撞：演出期间玩家不该再被物理推动 / 触发任何区域（重生区之类）
        CharacterController cc = player.GetComponent<CharacterController>();
        if (cc != null)
            cc.enabled = false;

        // 连角色脚本一起停：不然它的 Update 还会继续驱动上面这个已停用的 CharacterController
        if (player != null)
            player.enabled = false;

        // 挪到镜头绝对拍不到的地方（后面会切场景，坐标会被带过去）
        player.transform.position = new Vector3(0f, -5000f, 0f);

        // 顺手关掉渲染，双保险
        Renderer[] renderers = player.GetComponentsInChildren<Renderer>(true);
        foreach (Renderer r in renderers)
        {
            if (r != null)
                r.enabled = false;
        }
    }

    private static void MoveOutOfSight(Transform target)
    {
        if (target == null)
            return;

        // 结局演出结束后会整体回标题，这里只需要在演出期间别让它入镜
        target.position = new Vector3(0f, -8000f, 0f);
    }

    /// <summary>停用目标场景自带的多余玩家，避免运镜画面里冒出第二个角色。</summary>
    private void DisableExtraPlayers()
    {
        if (player == null)
            return;

        ThirdPersonController[] controllers = FindObjectsOfType<ThirdPersonController>(true);
        foreach (ThirdPersonController other in controllers)
        {
            if (other == null || other == player || other.transform.root == player.transform.root)
                continue;

            other.gameObject.SetActive(false);
            Debug.Log($"[EndingFinaleDirector] 已停用枯萎关卡里多余的玩家：{other.gameObject.name}。", other);
        }
    }

    /// <summary>
    /// 关掉枯萎关卡里的“淋雨”全屏特效（Level2 的 RainControl / FullScreenPassFader）。
    /// 只在黑屏期间调用：直接停播放 + 关 Renderer Feature，不会看到雨突然消失。
    /// </summary>
    private void DisableRainEffect()
    {
        if (!disableRainOnWitheredScene)
            return;

        FullScreenPassFader[] faders = FindObjectsOfType<FullScreenPassFader>(true);
        int count = 0;

        foreach (FullScreenPassFader fader in faders)
        {
            if (fader == null)
                continue;

            if (!string.IsNullOrEmpty(rainObjectName) && fader.gameObject.name != rainObjectName)
                continue;

            // 停掉播放 / 音频 / 透明度归零，并还原 Renderer Data（关 Feature、还原材质）
            fader.CleanupNow();
            // 顺手把物体关掉：免得 Zone 触发器 / OnEnable 又把雨拉起来
            fader.gameObject.SetActive(false);
            count++;
        }

        if (count > 0)
            Debug.Log($"[EndingFinaleDirector] 已关闭 {count} 个淋雨特效（枯萎关卡不该有雨）。", this);
    }

    /// <summary>禁用目标场景里自带的相机，避免和常驻主相机打架（渲染到 RT 的特效相机保留）。</summary>
    private static void DisableExtraCameras(Camera mainCamera)
    {
        Camera[] cameras = FindObjectsOfType<Camera>(true);

        foreach (Camera camera in cameras)
        {
            if (camera == null || camera == mainCamera)
                continue;

            if (mainCamera != null && camera.transform.root == mainCamera.transform.root)
                continue;

            if (camera.targetTexture != null)
                continue;

            camera.gameObject.SetActive(false);
        }
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

    private void EnablePlayerInput()
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

    // ---------------------------------------------------------------- UI / 工具

    private void EnsureCanvas()
    {
        if (canvas != null)
            return;

        GameObject root = new GameObject("GGJ_FinaleTextUI");
        // 挂在导演物体下（导演是常驻的）→ 跨场景切到枯萎关卡后文字仍然跟着走
        root.transform.SetParent(transform, false);

        canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 500;   // 盖过 GameplayHUD(100) 的黑幕，低于结局字幕(1000)

        CanvasScaler scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        root.AddComponent<GraphicRaycaster>();

        GameObject textGO = new GameObject("FinaleLine", typeof(RectTransform));
        textGO.transform.SetParent(root.transform, false);

        RectTransform rt = textGO.GetComponent<RectTransform>();
        // 左边、垂直居中（原来贴着左上角，太高了）
        rt.anchorMin = new Vector2(0f, 0.5f);
        rt.anchorMax = new Vector2(0f, 0.5f);
        rt.pivot = new Vector2(0f, 0.5f);
        rt.anchoredPosition = lineAnchoredPosition;
        rt.sizeDelta = new Vector2(lineWidth, lineHeight);

        Text text = textGO.AddComponent<Text>();
        text.font = GetDefaultFont();
        text.fontSize = lineFontSize;
        text.fontStyle = FontStyle.Bold;
        text.color = lineColor;
        text.alignment = TextAnchor.MiddleLeft;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        text.enabled = false;

        Outline outline = textGO.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
        outline.effectDistance = new Vector2(2f, -2f);

        lineText = text;
        lineOutline = outline;
        SetLineAlpha(0f);

        // —— 枯萎关卡的那行字（屏幕左边中上方，和上面那句台词是两个位置）——
        GameObject witheredGO = new GameObject("WitheredLine", typeof(RectTransform));
        witheredGO.transform.SetParent(root.transform, false);

        RectTransform wrt = witheredGO.GetComponent<RectTransform>();
        float height = Mathf.Clamp01(witheredLineHeightRatio);
        wrt.anchorMin = new Vector2(0f, height);
        wrt.anchorMax = new Vector2(0f, height);
        wrt.pivot = new Vector2(0f, 0.5f);
        wrt.anchoredPosition = new Vector2(Mathf.Max(0f, witheredLineLeftMargin), 0f);
        wrt.sizeDelta = new Vector2(Mathf.Max(200f, witheredLineWidth), 0f);

        Text wText = witheredGO.AddComponent<Text>();
        wText.font = GetDefaultFont();
        wText.fontSize = witheredLineFontSize;
        wText.fontStyle = FontStyle.Bold;
        wText.color = witheredLineColor;
        wText.alignment = TextAnchor.MiddleLeft;
        wText.horizontalOverflow = HorizontalWrapMode.Wrap;
        wText.verticalOverflow = VerticalWrapMode.Overflow;
        wText.raycastTarget = false;
        wText.enabled = false;

        Outline wOutline = witheredGO.AddComponent<Outline>();
        wOutline.effectColor = new Color(0f, 0f, 0f, 0.85f);
        wOutline.effectDistance = new Vector2(2f, -2f);

        witheredText = wText;
        witheredOutline = wOutline;
        SetWitheredLineAlpha(0f);
    }

    /// <summary>
    /// 按名字找物体 —— 连“当前是关着的”也能找到。
    /// GameObject.Find 只搜激活物体，而 Level5 的 Final Scene 一开始就是关着的
    /// （要等玩家做完选择、黑屏之后才出现），所以必须走这条遍历根目录的路子。
    /// </summary>
    private static GameObject FindObjectEvenIfInactive(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        Scene scene = SceneManager.GetActiveScene();
        if (scene.IsValid() && scene.isLoaded)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root == null)
                    continue;

                if (root.name == name)
                    return root;

                Transform found = FindDeepChild(root.transform, name);
                if (found != null)
                    return found.gameObject;
            }
        }

        return GameObject.Find(name);
    }

    private static Transform FindChildByName(Transform root, params string[] names)
    {
        if (root == null || names == null)
            return null;

        foreach (string name in names)
        {
            if (string.IsNullOrEmpty(name))
                continue;

            Transform found = FindDeepChild(root, name);
            if (found != null)
                return found;
        }

        return null;
    }

    private static Transform FindDeepChild(Transform root, string name)
    {
        foreach (Transform child in root)
        {
            if (child == null)
                continue;

            if (child.name == name)
                return child;

            Transform deep = FindDeepChild(child, name);
            if (deep != null)
                return deep;
        }

        return null;
    }

    private static Font GetDefaultFont()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return font;
    }
}
