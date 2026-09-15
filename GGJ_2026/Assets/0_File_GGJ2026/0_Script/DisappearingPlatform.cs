using System.Collections.Generic;
using StarterAssets;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// 会“踩塌”的平台：玩家踩上去后先静止一段时间，然后才开始轻微晃动（幅度越来越大），最后收缩消失。
///
/// 时间轴： 踩上去 ──[静止 wobbleStartDelay 秒]──> 开始晃动 ──[越晃越厉害]──> 累计 standDuration 到点消失
///          ──[再等 respawnSeconds 秒]──> 长回来（respawnGrowDuration）──> 回到初始状态，可以再次踩塌
///          （配了 breakPiecesPrefab 的话，“到点消失”这一步会换成“到点炸成碎块坠落”，见下）
///
/// 【配置要点】
///   · 停留时长 = standDuration（秒）：踩上去开始计时，累计到这个时长就消失 —— 这是核心参数；
///   · wobbleStartDelay（秒）：踩上去之后先静止多久才开始晃 —— 要“踩上去几秒后才开始晃动”就调大这个值
///     （例如 1.5 ~ 2 秒）；设为 0 = 一踩上就晃；
///   · 晃动 = 沿关卡“左右”方向的轻微横移（1.5 ~ 5 厘米），越接近消失晃得越明显
///     （幅度按“开始晃动之后”这段进度从 wobbleMinDistance 涨到 wobbleMaxDistance）；
///     只平移、不倾斜：转一度看着不多，但板子两端会随之上下翘，站在上面的人跟着上下起伏，
///     观感会剧烈得多 —— 这里只要让玩家看懂“这块板快掉了”就够了；
///   · 计时只看“人还在不在平台上”，在平台上怎么活动都不会重置：站立区的竖直方向是一段
///     “高度带”（从平台表面往上 stayHeightAbovePlatform 米），起跳、落下、原地弹跳都仍然算在平台上。
///   · 「玩家离开后计时清零」默认勾选：真的走开（水平离开平台范围）就重新计时；
///     leaveGraceSeconds 是宽限时间（默认 0.3 秒），这期间又踩回来就不清零；
///     取消勾选则变成“离开期间暂停计时、回来接着累加”。
///
/// 【消失与恢复（和关卡重置系统的关系，重要）】
///   · 消失 = 本物体 SetActive(false)。GGJLevelResetManager 的世界快照记录的是每个节点的 activeSelf，
///     所以按 R 回到安全点时，平台会随快照一起还原（快照拍下时它还完好，就会被重新激活），不需要额外接线。
///     注意：如果拍快照之前它就已经消失了，按 R 不会凭空恢复它（与能量植物等世界状态的处理一致），
///     想要“必定能回来”就给 respawnSeconds 设一个大于 0 的值。
///   · 重新生成：respawnSeconds（默认 3 秒）—— 消失后等这么多秒，平台会重新出现并复位（计时归零，可以再次踩塌）；
///     填 0 = 不自动生成（板子一直是消失状态，只能靠按 R 或重进关卡恢复）；
///     重新出现时用 respawnGrowDuration 做一段“长回来”的动画，0 = 立刻出现。
///     自动恢复的计时由 GGJRespawnTimer 托管 —— 本物体被停用后自身的
///     Update / 协程都会停，计时必须交给一个没被停用的小助手（它随场景销毁，不跨场景保留）。
///
/// 【破碎效果（可选，配了就有）】
///   · 把碎裂模型（stone_pieces.fbx —— 里面每个子物体就是一块碎块）拖到 breakPiecesPrefab 即可；
///     留空 = 保持原来的“缩小消失”。
///   · 到点不再收缩，而是直接藏掉完好外观、关掉碰撞体（踩在上面的人立刻掉下去）并摆出碎块，
///     什么时候“崩成一块块”由 breakFallDelay 决定：
///       · breakFallDelay = 0（默认）：摆好碎块的同一帧就当场崩开 —— 一踩塌就是满地碎块在往下掉，
///         中间不会有“整块先往下坠一下”的动作；
///       · breakFallDelay > 0：先让碎块【当作一个整体】往下坠 breakFallDelay 秒，
///         到点才给每一块加刚体、继承下坠速度 + 向外炸开 + 翻滚。
///         想要 Houdini 里那种「石头边掉边裂」的味道就填 0.3 ~ 0.5。
///   · 碎块的速度 / 自旋参数和 Houdini 里 add_spread 的参数一一对应，想调效果两边对着调就行。
///   · 碎块到 pieceLifetime 秒后自动销毁；平台照常按 respawnSeconds 重新长回来，可以反复踩。
///   · OnBreak 事件可以接破碎音效 / 尘土特效 / 震屏（在“空中崩开”那一瞬触发）。
///
/// 【玩家判定（为什么不写 OnCollision / OnTrigger）】
///   CharacterController 是脚本驱动的，和静态碰撞体之间不会产生 OnCollisionStay / Trigger 事件
///   （会表现为“时好时坏、甚至完全不触发”）。所以这里每帧主动去量玩家和平台的位置关系，
///   有两种判定方式（detectionMode）：
///     · 踩在平台上（默认）：把平台实体碰撞体的包围盒当成“站立区”，看玩家脚底是否落进去 ——
///       水平方向在平台占地范围内（略微放宽，站边上也算），竖直方向在平台“朝玩家重力反方向”的那一面
///       上方的一段高度带内（承受脚底略微沉入，往上放宽到 stayHeightAbovePlatform 米），
///       所以在平台起跳、跳到半空、落回表面都仍然算“在平台上”，停留计时不会被打断。
///     · 进入 Trigger 体积：看玩家是否和平台上的 Trigger（勾了 Is Trigger 的 Collider）重叠 ——
///       人从旁边经过、还没踩上去就开始计时 / 晃动（想让“路过就晃”，把那个 Trigger 拉大即可）。
///   兼容 InvertGravity：翻转重力时玩家站在平台下表面同样算数。
///
/// 【用法】
///   1. 把本组件挂到平台物体上，保证它（或子物体）上有实体 Collider（推荐 BoxCollider，不要勾 Is Trigger）；
///   2. 调 standDuration（多久消失）和 wobbleStartDelay（踩上去多久开始晃）即可，不需要写任何代码；
///   3. 想让“人一靠近 / 路过就开始晃”，把「触发方式」改成「进入 Trigger 体积」，
///      并保证平台上有一个勾了 Is Trigger 的 Collider（那个盒子就是触发范围，拉大即可）。
/// </summary>
[DisallowMultipleComponent]
public class DisappearingPlatform : MonoBehaviour
{
    /// <summary>用什么方式判断“玩家来了”。</summary>
    public enum DetectionMode
    {
        /// <summary>看玩家脚底是否踩在平台表面上（默认，原行为）。</summary>
        [InspectorName("踩在平台上（默认）")] StandOnTop,

        /// <summary>看玩家是否进入了平台上的 Trigger 体积 —— 人经过 / 靠近就算。</summary>
        [InspectorName("进入 Trigger 体积")] TriggerVolume,
    }

    // ---------------------------------------------------------------- Inspector 配置

    [Header("停留时间（核心配置）")]
    [Tooltip("玩家站在平台上累计停留多少秒后，平台开始消失（从踩上去算起，包含前面静止不晃的那段）。")]
    [SerializeField] private float standDuration = 3f;

    [Tooltip("踩上去之后先静止多少秒才开始晃动。\n" +
             "0 = 一踩上就开始晃；想要“踩上去几秒后才开始晃动”，就调大到 1.5 ~ 2 秒左右。\n" +
             "会被 standDuration 截断（不会出现“等到消失都没开始晃”）。")]
    [SerializeField] private float wobbleStartDelay = 1.5f;

    [Tooltip("勾选（默认）：玩家离开平台后计时清零，重新踩上来要重新计满时间。\n" +
             "取消勾选：离开期间只是暂停计时，回来接着累加。")]
    [SerializeField] private bool resetTimerWhenPlayerLeaves = true;

    [Tooltip("玩家离开平台后的宽限时间（秒）：在这段时间内重新踩回平台，计时不清零。\n" +
             "用来避免“脚步蹭出平台又立刻踩回来 / 平台晃动时脚底瞬间离地”把计时打断（默认 0.3 秒）。")]
    [SerializeField] private float leaveGraceSeconds = 0.3f;

    [Tooltip("玩家在平台上方多高的范围内，仍然算“还在这块平台上”（默认 2 米，足够容纳一次普通起跳）。\n" +
             "在这段高度内起跳 / 落下 / 原地弹跳都不会打断计时，照样累加；\n" +
             "高过这个范围就不算了 —— 例如站在更上面的另一块平台上时，就不会再给下面这块计时。")]
    [SerializeField] private float stayHeightAbovePlatform = 2f;

    [Header("触发方式（怎么算“玩家来了”）")]
    [Tooltip("判定“玩家来了”的方式：\n" +
             "· 踩在平台上（默认）：看玩家脚底是否落在平台表面上 —— 原来的行为；\n" +
             "· 进入 Trigger 体积：看玩家是否进入了平台上的 Trigger（勾了 Is Trigger 的 Collider）。\n" +
             "  人从旁边经过、还没踩上去就会开始计时 / 晃动 —— 想让“路过就晃”，\n" +
             "  把那个 Trigger 拉大一点就行（Scene 里直接拖青色线框）。")]
    [SerializeField] private DetectionMode detectionMode = DetectionMode.StandOnTop;

    [Tooltip("「进入 Trigger 体积」模式下，用来判定的那个触发器。\n" +
             "留空 = 自动用本平台（含子物体）上所有的 Trigger。\n" +
             "想用一个单独的、更大的触发区（比如摆在平台前面一点，人还在空中/旁边就算），把那个 Trigger 拖进来。")]
    [SerializeField] private Collider triggerVolume;

    [Header("轻微晃动（越接近消失晃得越厉害）")]
    [Tooltip("要晃动的部位；留空 = 自动取自己或子物体上第一个带网格的物体（就是本物体自己也没关系）。\n" +
             "想只晃模型、不动碰撞体的话，把模型子物体拖到这里。")]
    [SerializeField] private Transform wobbleTarget;

    [Tooltip("刚踩上去时左右横移的幅度（米）：默认只有一两厘米，就是“有点松了”的意思")]
    [SerializeField] private float wobbleMinDistance = 0.015f;

    [Tooltip("即将消失时左右横移的幅度（米）：越大越像“快塌了”，不建议超过 0.1（10 厘米）")]
    [SerializeField] private float wobbleMaxDistance = 0.05f;

    [Tooltip("横移频率（每秒来回次数）：只左右平移、不做倾斜，太快会显得很躁")]
    [SerializeField] private float wobbleFrequency = 3.5f;

    [Tooltip("玩家离开平台后，晃动在多长时间内回复原位（秒）")]
    [SerializeField] private float wobbleRecoverDuration = 0.2f;

    [Header("外观替换（可选：把 ProBuilder 的方块换成自己的模型）")]
    [Tooltip("把 stone_pieces.fbx 直接拖进来，就会用它替换平台原本的外观（那块 ProBuilder 石头）：\n" +
             "• 自动缩放到和原外观一样大、并居中 —— 两块模型严丝合缝，不需要手动对位置；\n" +
             "• 晃动自动作用在它身上（不用再填 Wobble Target）；\n" +
             "• 原外观的 Renderer 会被永久隐藏，但碰撞体保留 —— 玩家照样站在原来的位置上；\n" +
             "• 破碎时也是从它的位置整块下坠、在空中崩开。\n" +
             "留空 = 不做任何替换，保持原来的方块外观。\n" +
             "（自己往平台下拖的那个模型请删掉，否则会和自动生成的重复）")]
    [SerializeField] private GameObject intactVisualPrefab;

    [Header("消失 / 重新生成")]
    [Tooltip("从开始消失到完全看不见的时长（秒）；0 = 立刻消失")]
    [SerializeField] private float disappearDuration = 0.25f;

    [Tooltip("消失动画结束时缩到初始大小的百分之几（0.02 = 缩到 2%，看起来就是没了）")]
    [SerializeField] private float disappearEndScale = 0.02f;

    [Tooltip("消失后多少秒重新生成（默认 3 秒）。\n" +
             "填 0 = 不自动生成，板子会一直是消失状态（只能靠“按 R 回到安全点”或重新进关恢复）。")]
    [SerializeField] private float respawnSeconds = 3f;

    [Tooltip("重新生成时“长回来”的时长（秒）：从缩没的尺寸长回原大小，看起来更自然；0 = 立刻出现")]
    [SerializeField] private float respawnGrowDuration = 0.25f;

    [Header("破碎效果（把碎裂模型拖进来就会替代“缩小消失”）")]
    [Tooltip("碎裂模型：把 stone_pieces.fbx 直接拖到这里就行（里面每个子物体当作一块碎块）。\n" +
             "留空 = 不使用破碎，走原来的“缩小消失”。\n" +
             "碎块模型的支点和平台外观一致，所以摆上去是严丝合缝的，看不出接缝。")]
    [SerializeField] private GameObject breakPiecesPrefab;

    [Tooltip("什么时候“崩成一块块”（秒）：\n" +
             "0（默认）= 摆好碎块的同一帧就当场崩开 —— 不会有“整块先往下坠一下才碎”的那段；\n" +
             "填 0.3 ~ 0.5 = 碎块先当作一个整体往下掉这么久，到点才在空中崩开，\n" +
             "就是 Houdini 里那种「石头边掉边裂」的味道。")]
    [SerializeField] private float breakFallDelay = 0f;

    [Tooltip("碎块向外炸开的速度（米/秒）：对应 Houdini 里 add_spread 的「向外炸开速度」。\n" +
             "1 ~ 1.5 像“裂开坠落”，3 以上就像“爆炸”了。")]
    [SerializeField] private float breakSpreadSpeed = 1.5f;

    [Tooltip("整体向下的速度（米/秒）：对应 Houdini 里的「向下附加速度」。\n" +
             "下坠阶段是“整块往下砸”的初速度，崩开之后变成每块额外向下的速度。\n" +
             "想更像“塌下去”就往 2 ~ 3 调；想更像“炸开”就调到 0.5 以下。")]
    [SerializeField] private float breakDownSpeed = 2.5f;

    [Tooltip("碎块初始自旋速度：对应 Houdini 里的「碎块自转」，越大翻得越乱。")]
    [SerializeField] private float breakSpinSpeed = 1.5f;

    [Tooltip("炸开方向的随机扰动量：对应 Houdini 里的「方向随机扰动」。\n" +
             "0 = 规整的放射状，0.3 左右更自然。")]
    [SerializeField, Range(0f, 1f)] private float breakChaos = 0.3f;

    [Tooltip("每一块碎块的质量（千克）：越大越“重”，被撞开时越不容易被带飞。")]
    [SerializeField] private float pieceMass = 6f;

    [Tooltip("碎块的空气阻力：0.05 左右手感像石头，0 = 纯自由落体。")]
    [SerializeField] private float pieceDrag = 0.05f;

    [Tooltip("碎块用凸包 MeshCollider（更贴形状，但要求碎块模型打开 Read/Write Enabled）；\n" +
             "不勾（默认）= 用包围盒 BoxCollider，便宜且没有任何导入要求，砸人 / 落地堆叠都够用。")]
    [SerializeField] private bool useConvexMeshCollider = false;

    [Tooltip("碎块生成后多少秒自动销毁（从炸开那一刻算起）。")]
    [SerializeField] private float pieceLifetime = 6f;

    [Header("事件（可选，用来接音效 / 特效 / 动画）")]
    [Tooltip("玩家踩上平台、开始计时时触发一次（此时还没开始晃，离开后再站上来会再次触发）")]
    public UnityEvent OnStartStanding = new UnityEvent();
    [Tooltip("平台完全消失（物体被停用）时触发")]
    public UnityEvent OnDisappear = new UnityEvent();
    [Tooltip("平台重新出现时触发（自动恢复 / 按 R 回到安全点都会触发）")]
    public UnityEvent OnRespawn = new UnityEvent();
    [Tooltip("平台炸成碎块的瞬间触发一次（接破碎音效 / 尘土特效 / 震屏）")]
    public UnityEvent OnBreak = new UnityEvent();

    // ---------------------------------------------------------------- 运行时状态

    /// <summary>平台状态：完好 / 正在破碎（整块下坠中）/ 正在消失 / 已消失 / 正在长回来。</summary>
    private enum State
    {
        Intact,
        Breaking,
        Vanishing,
        Gone,
        Respawning
    }

    private State state = State.Intact;

    private float standTimer;          // 已累计的停留时长
    private float awayTimer;           // 离开平台后的时间（配合 leaveGraceSeconds）
    private float wobblePhase;         // 晃动相位（连续累加，保证来回自然）
    private float wobbleWeight;        // 0 = 完全回正，1 = 按当前进度晃动
    private bool standingNotified;     // OnStartStanding 是否已触发

    private float vanishTime;
    private Vector3 vanishStartScale;
    private Quaternion vanishStartRot;
    private Vector3 vanishStartPos;

    private float respawnGrowTime;     // “长回来”动画已播放的时间

    private Transform visualRoot;      // 实际被晃动的部位
    private Vector3 baseLocalPos;
    private Quaternion baseLocalRot;
    private Vector3 baseScale;
    private Vector3 wobbleAxisLocal = Vector3.right; // 晃动轴向（父物体空间下、对应世界“左右”的方向）

    private Renderer[] visualRenderers = new Renderer[0];   // 平台自身的外观（破碎时整组藏掉）
    private Renderer[] replacedRenderers = new Renderer[0]; // 被 intactVisualPrefab 顶掉的原始外观（永久隐藏，不参与显隐切换）
    private bool warnedUnreadableMesh;                      // 只提示一次“网格没开读写”

    private GameObject breakDebris;                         // 当前这批碎块的根物体
    private Renderer[] breakRenderers;                      // 碎块外观（算包围盒中心 / 逐块处理都靠它）
    private Vector3 breakOrigin;                            // 整块石头的中心 = 向外炸开的原点
    private Vector3 breakFallVelocity;                      // “整块下坠”阶段累积的下坠速度
    private float breakTimer;                               // 进入破碎后经过的时间
    private bool breakShattered;                            // 是否已经在空中崩开

    private Collider[] ownColliders = new Collider[0];
    private ThirdPersonController cachedPlayer;

    /// <summary>踩上去后晃动幅度起来的过渡时间（秒）。</summary>
    private const float WobbleRampSeconds = 0.15f;

    /// <summary>站立区：平台占地范围外再放宽多少米（站在边缘上也算站在上面）。</summary>
    private const float FootprintMargin = 0.15f;

    /// <summary>脚底最多允许沉到平台表面以下多少米（包容 CharacterController 的 skin / 台阶误差）。</summary>
    private const float StandDepthTolerance = 0.2f;

    /// <summary>站立高度带的保底高度（米）：即使 stayHeightAbovePlatform 配得很小，也至少容忍这点起伏。</summary>
    private const float StandHeightTolerance = 0.4f;

    /// <summary>当前是否还“完好”（供外部系统查询）。</summary>
    public bool IsIntact => state == State.Intact;

    /// <summary>当前已累计的停留时长（秒），可用于做进度提示。</summary>
    public float StandTimer => standTimer;

    /// <summary>配置的停留时长（秒）。</summary>
    public float StandDuration => standDuration;

    /// <summary>配置的“开始晃动前延迟”（秒）。</summary>
    public float WobbleStartDelay => wobbleStartDelay;

    /// <summary>是否已经进入晃动阶段（踩上去超过 wobbleStartDelay 秒）。</summary>
    public bool IsWobbling => state == State.Intact && standTimer >= EffectiveWobbleStartDelay;

    /// <summary>
    /// 实际生效的“开始晃动”时刻：用 standDuration 截断，
    /// 避免配出“延迟比停留时长还长、等到消失都没开始晃”的情况。
    /// </summary>
    private float EffectiveWobbleStartDelay =>
        Mathf.Clamp(wobbleStartDelay, 0f, Mathf.Max(0f, standDuration));

    // ---------------------------------------------------------------- 生命周期

    private void Awake()
    {
        baseScale = transform.localScale;

        CacheColliders();

        visualRoot = ResolveVisualRoot();

        // 配了 intactVisualPrefab 就用它顶掉原本的外观；之后 visualRoot 指向替换后的模型
        SwapInIntactVisual();

        baseLocalPos = visualRoot.localPosition;
        baseLocalRot = visualRoot.localRotation;
        CacheWobbleAxis();

        CacheVisualRenderers();
        WarnIfNoSolidCollider();
        WarnIfTriggerMissing();
    }

    private void OnEnable()
    {
        // 首次激活 / 自动恢复 / 按 R 回到安全点重新激活 —— 都在这里回到初始状态。
        bool wasGone = state == State.Vanishing || state == State.Gone;

        CacheColliders();          // 停用期间层级可能被改过，重新缓存
        ResetToInitialState();

        if (!wasGone)
            return;

        // 重新生成：先以“缩没”的尺寸出现，再长回原大小（看起来是长出来的，而不是啪一下蹦出来）
        if (respawnGrowDuration > 0f)
        {
            state = State.Respawning;
            respawnGrowTime = 0f;
            transform.localScale = baseScale * Mathf.Max(0.001f, disappearEndScale);
        }

        OnRespawn?.Invoke();
    }

    private void Update()
    {
        switch (state)
        {
            case State.Intact:
                UpdateStanding();
                break;

            case State.Breaking:
                UpdateBreaking();
                break;

            case State.Vanishing:
                UpdateVanishing();
                break;

            case State.Respawning:
                UpdateRespawning();
                break;
        }
    }

    // ---------------------------------------------------------------- 停留计时 + 晃动

    private void UpdateStanding()
    {
        float wobbleStart = EffectiveWobbleStartDelay;

        if (IsPlayerDetected())
        {
            if (!standingNotified)
            {
                standingNotified = true;
                OnStartStanding?.Invoke();
            }

            awayTimer = 0f;
            standTimer += Time.deltaTime;

            // 踩上去后的前 wobbleStartDelay 秒保持静止（晃动权重收回到 0），到点才开始晃
            bool shouldWobble = standTimer >= wobbleStart;
            wobbleWeight = Mathf.MoveTowards(wobbleWeight, shouldWobble ? 1f : 0f,
                Time.deltaTime / (shouldWobble
                    ? WobbleRampSeconds
                    : Mathf.Max(0.0001f, wobbleRecoverDuration)));
        }
        else
        {
            awayTimer += Time.deltaTime;
            standingNotified = false;

            // 玩家走开：晃动慢慢回正；超过了宽限时间且配置了“离开清零”就把计时清掉
            wobbleWeight = Mathf.MoveTowards(wobbleWeight, 0f,
                Time.deltaTime / Mathf.Max(0.0001f, wobbleRecoverDuration));

            if (resetTimerWhenPlayerLeaves && awayTimer >= Mathf.Max(0f, leaveGraceSeconds))
                standTimer = 0f;
        }

        // 晃动进度：从“开始晃动”那一刻算 0，到 standDuration 时算 1（静止阶段恒为 0）
        float wobbleProgress = Mathf.InverseLerp(
            wobbleStart,
            Mathf.Max(wobbleStart + 0.0001f, standDuration),
            standTimer);

        ApplyWobblePose(wobbleProgress);

        // 停够了 → 开始消失
        if (standTimer >= standDuration)
            BeginVanishing();
    }

    /// <summary>
    /// 按“开始晃动之后的进度”施加晃动：只会左右来回平移，
    /// 幅度从 wobbleMinDistance 线性涨到 wobbleMaxDistance，越接近消失晃得越明显；
    /// progress01 为 0（还没到晃动时刻）/ wobbleWeight 归零时完全回正、绝对静止。
    ///
    /// 刻意不倾斜：转一度纸面上只是“一点点”，但板的两端会随之上下翘，
    /// 站在上面的人跟着上下起伏，观感比纯平移剧烈得多 —— 这里只需要“提示快掉了”。
    /// </summary>
    private void ApplyWobblePose(float progress01)
    {
        if (visualRoot == null)
            return;

        wobblePhase += Time.deltaTime * Mathf.Max(0f, wobbleFrequency) * Mathf.PI * 2f;

        // 完全回正：把之前可能残留的位移 / 旋转也一次性拉回去
        if (wobbleWeight <= 0f)
        {
            visualRoot.localPosition = baseLocalPos;
            visualRoot.localRotation = baseLocalRot;
            return;
        }

        float amplitude = Mathf.Lerp(wobbleMinDistance, wobbleMaxDistance, progress01) * wobbleWeight;
        Vector3 shake = wobbleAxisLocal * (Mathf.Sin(wobblePhase) * amplitude);

        visualRoot.localPosition = baseLocalPos + shake;
    }

    /// <summary>
    /// 算晃动轴向：在世界坐标里“左右”那条轴，换到 visualRoot 的父物体空间。
    /// 平台本身没有旋转时就是 (1,0,0)；带旋转的平台上也仍然横着晃，不会变成上下抖。
    /// </summary>
    private void CacheWobbleAxis()
    {
        wobbleAxisLocal = Vector3.right;

        if (visualRoot == null || visualRoot.parent == null)
            return;

        Vector3 axis = visualRoot.parent.InverseTransformDirection(Vector3.right);
        if (axis.sqrMagnitude > 1e-6f)
            wobbleAxisLocal = axis.normalized;
    }

    // ---------------------------------------------------------------- 消失 / 恢复

    private void BeginVanishing()
    {
        // 配了碎块模型就走破碎：整块先下坠 → 在空中崩开 → 碎块坠落（不再走“缩小消失”那套）
        if (BeginBreaking())
            return;

        state = State.Vanishing;
        vanishTime = 0f;

        // 从当前（带晃动的）姿态开始：一边收缩一边回正，看起来是“晃着塌下去”
        vanishStartScale = transform.localScale;
        if (visualRoot != null)
        {
            vanishStartRot = visualRoot.localRotation;
            vanishStartPos = visualRoot.localPosition;
        }
    }

    private void UpdateVanishing()
    {
        float duration = Mathf.Max(0f, disappearDuration);
        vanishTime += Time.deltaTime;

        float k = duration <= 0f ? 1f : Mathf.Clamp01(vanishTime / duration);

        float endScale = Mathf.Max(0.001f, disappearEndScale);
        transform.localScale = Vector3.Lerp(vanishStartScale, vanishStartScale * endScale, k);

        if (visualRoot != null)
        {
            visualRoot.localRotation = Quaternion.Slerp(vanishStartRot, baseLocalRot, k);
            visualRoot.localPosition = Vector3.Lerp(vanishStartPos, baseLocalPos, k);
        }

        if (k >= 1f)
            CompleteVanishing();
    }

    private void CompleteVanishing()
    {
        state = State.Gone;
        OnDisappear?.Invoke();

        // SetActive(false)：既彻底从画面与物理里消失，也让 GGJLevelResetManager 的
        // 世界快照把它当作“被玩家改动的状态”一起还原（按 R 回到安全点时平台会重新出现）。
        gameObject.SetActive(false);

        // 安排“过一会儿自己重新生成”（默认 3 秒；设为 0 就不自动生成）
        if (respawnSeconds > 0f)
            GGJRespawnTimer.Schedule(gameObject, respawnSeconds);
    }

    // ---------------------------------------------------------------- 破碎效果

    /// <summary>
    /// 开始破碎：藏掉完好外观、关掉碰撞体（踩在上面的人立刻掉下去），在原位置摆出碎块。
    ///
    /// 摆好之后的走向看 breakFallDelay：
    ///   · 0（默认）—— 同一帧就崩开：立刻给每一块加刚体、向外炸开，不再有“整块往下坠”的动作；
    ///   · &gt; 0 —— 先进入「整块下坠」阶段（这段时间碎块还是一个整体、没有刚体，由 UpdateBreaking
    ///     整块往下送），到点才在空中崩开，并把这段时间攒下的下坠速度继承给每一块。
    ///
    /// 返回 false 表示没有配碎块模型，调用方继续走原来的“缩小消失”。
    /// </summary>
    private bool BeginBreaking()
    {
        if (breakPiecesPrefab == null)
            return false;

        Transform reference = visualRoot != null ? visualRoot : transform;

        // 上一批碎块还没散完（例如 respawnSeconds 配得很短、马上又被踩塌）就先清掉，避免叠出两批
        if (breakDebris != null)
            Destroy(breakDebris);

        // 1) 藏掉完好外观 + 关掉碰撞体：视觉上立刻变成碎块，物理上玩家立刻失去落脚点
        SetVisualVisible(false);
        SetCollidersEnabled(false);

        // 2) 在完全相同的位置 / 旋转 / 缩放下摆出碎块
        //    （碎块模型支点和平台外观一致，所以对得上，看不出接缝）
        GameObject debris = Instantiate(breakPiecesPrefab, reference.position, reference.rotation);
        debris.name = name + " (碎块)";
        SetWorldScale(debris.transform, reference.lossyScale);
        breakDebris = debris;

        Physics.SyncTransforms();      // 保证下面读到的是刚摆好的位置
        breakRenderers = debris.GetComponentsInChildren<Renderer>(true);
        breakOrigin = ComputeBreakCenter(breakRenderers);   // 向外炸开的原点 = 整块石头的中心

        // 3) 「整块下坠」：先给一个整体向下的初速度，之后靠重力继续加速（这一段不拆开、不加刚体）
        breakFallVelocity = Physics.gravity.normalized * Mathf.Max(0f, breakDownSpeed);
        breakTimer = 0f;
        breakShattered = false;
        state = State.Breaking;

        // 4) 到点自己收拾干净：Destroy(带延迟) 不受本物体被停用影响，平台重新生成时也不会残留
        Destroy(debris, Mathf.Max(0.5f, pieceLifetime));

        // 5) breakFallDelay = 0：不放「整块下坠」那一段，摆好碎块的这一帧就直接崩开
        //    （不会先往下坠一下才碎；碎块本身照常受重力，崩开后立刻开始下落）
        if (breakFallDelay <= 0f)
        {
            ShatterDebris(breakFallVelocity);
            CompleteVanishing();
        }

        return true;
    }

    /// <summary>
    /// 破碎阶段（只在 breakFallDelay &gt; 0 时才会走到这里）：
    /// 先整块往下掉 breakFallDelay 秒（自己按重力加速），到点在空中崩开，
    /// 平台随即进入“消失 → 重新生成”。
    /// </summary>
    private void UpdateBreaking()
    {
        if (breakShattered)
            return;

        breakTimer += Time.deltaTime;

        // ---- 第一段：整块完整地往下掉 ----
        breakFallVelocity += Physics.gravity * Time.deltaTime;        // 和刚体用同一套重力
        if (breakDebris != null)
            breakDebris.transform.position += breakFallVelocity * Time.deltaTime;

        // ---- 到点：在半空中崩开，下坠速度交给每一块碎块接手 ----
        if (breakTimer >= Mathf.Max(0f, breakFallDelay))
        {
            ShatterDebris(breakFallVelocity);
            CompleteVanishing();
        }
    }

    /// <summary>空中崩开：每一块补上刚体 + 碰撞体，继承下坠速度，并向外炸开 + 随机翻滚。</summary>
    private void ShatterDebris(Vector3 inheritedVelocity)
    {
        breakShattered = true;

        if (breakRenderers != null)
        {
            for (int i = 0; i < breakRenderers.Length; i++)
                PrepareBreakPiece(breakRenderers[i], breakOrigin, inheritedVelocity);
        }

        // 通知外部接音效 / 尘土 / 震屏（“在空中炸开”的那一瞬）
        OnBreak?.Invoke();
    }

    /// <summary>所有碎块渲染包围盒的合并中心（也就是整块石头的中心）。</summary>
    private static Vector3 ComputeBreakCenter(Renderer[] pieceRenderers)
    {
        if (pieceRenderers == null || pieceRenderers.Length == 0)
            return Vector3.zero;

        Bounds bounds = pieceRenderers[0].bounds;
        for (int i = 1; i < pieceRenderers.Length; i++)
        {
            if (pieceRenderers[i] != null)
                bounds.Encapsulate(pieceRenderers[i].bounds);
        }

        return bounds.center;
    }

    /// <summary>把一块碎块变成刚体：继承整体下坠速度 + 向外炸开 + 随机自旋，并按配置补上碰撞体。</summary>
    private void PrepareBreakPiece(Renderer pieceRenderer, Vector3 stoneCenter, Vector3 inheritedVelocity)
    {
        if (pieceRenderer == null)
            return;

        GameObject piece = pieceRenderer.gameObject;
        if (piece.GetComponent<Rigidbody>() != null)
            return;                                     // 已经处理过就别重复加

        MeshFilter filter = piece.GetComponent<MeshFilter>();
        if (filter == null || filter.sharedMesh == null)
            return;                                     // 空节点（没有网格）跳过

        // 向外方向：水平面上从整块石头的中心指向这一块，再加一点随机扰动
        //（和 Houdini 里 add_spread 的做法一致）
        Vector3 dir = pieceRenderer.bounds.center - stoneCenter;
        dir.y = 0f;
        dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Random.insideUnitSphere;

        Vector3 jitter = Random.insideUnitSphere * breakChaos;
        jitter.y *= 0.35f;                              // 竖直方向少给点，保持“向外炸开”的观感
        dir = (dir + jitter).normalized;

        Rigidbody body = piece.AddComponent<Rigidbody>();
        body.mass = Mathf.Max(0.01f, pieceMass);
        body.drag = Mathf.Max(0f, pieceDrag);
        body.angularDrag = 0.05f;
        body.velocity = inheritedVelocity + dir * breakSpreadSpeed;
        body.angularVelocity = Random.insideUnitSphere * breakSpinSpeed;

        // 碰撞体：凸包更贴形状（要求碎块模型开 Read/Write Enabled），否则用包围盒
        if (useConvexMeshCollider && filter.sharedMesh.isReadable)
        {
            MeshCollider convex = piece.AddComponent<MeshCollider>();
            convex.convex = true;
        }
        else
        {
            if (useConvexMeshCollider && !warnedUnreadableMesh)
            {
                warnedUnreadableMesh = true;
                Debug.LogWarning($"[DisappearingPlatform] {breakPiecesPrefab.name} 的网格没有开 Read/Write Enabled，" +
                                 "碎块退回用 BoxCollider。想要贴形状：选中该模型 → Model 页 → 勾上 Read/Write Enabled。", this);
            }

            // MeshFilter 和碰撞体在同一个物体上，直接拿网格的本地包围盒就是对的
            BoxCollider box = piece.AddComponent<BoxCollider>();
            box.center = filter.sharedMesh.bounds.center;
            box.size = filter.sharedMesh.bounds.size;
        }
    }

    /// <summary>重新生成中：从“缩没”的尺寸长回原大小，长完就回到完好状态（可以再次踩塌）。</summary>
    private void UpdateRespawning()
    {
        float duration = Mathf.Max(0f, respawnGrowDuration);
        respawnGrowTime += Time.deltaTime;

        float k = duration <= 0f ? 1f : Mathf.Clamp01(respawnGrowTime / duration);

        Vector3 growFrom = baseScale * Mathf.Max(0.001f, disappearEndScale);
        transform.localScale = Vector3.Lerp(growFrom, baseScale, k);

        if (k >= 1f)
        {
            transform.localScale = baseScale;
            state = State.Intact;
        }
    }

    /// <summary>把平台恢复成初始状态（初始大小 / 无晃动 / 计时清零）。外部系统也可以主动调用。</summary>
    public void ResetToInitialState()
    {
        state = State.Intact;
        standTimer = 0f;
        awayTimer = 0f;
        wobblePhase = 0f;
        wobbleWeight = 0f;
        standingNotified = false;
        vanishTime = 0f;
        respawnGrowTime = 0f;

        transform.localScale = baseScale;

        if (visualRoot != null)
        {
            visualRoot.localPosition = baseLocalPos;
            visualRoot.localRotation = baseLocalRot;
        }

        // 破碎残留：还没崩开的碎块先让它崩开（免得僵在半空），其余的交给它自己的 Destroy 计时
        if (breakDebris != null && !breakShattered)
            ShatterDebris(breakFallVelocity);

        breakDebris = null;
        breakRenderers = null;
        breakShattered = false;
        breakFallVelocity = Vector3.zero;
        breakTimer = 0f;

        CacheVisualRenderers();
        SetVisualVisible(true);
        SetCollidersEnabled(true);
    }

    // ---------------------------------------------------------------- 玩家判定

    /// <summary>玩家此刻是否“来了”（每帧主动判定，不依赖碰撞 / 触发事件）：具体用哪种方式看 detectionMode。</summary>
    private bool IsPlayerDetected()
    {
        return detectionMode == DetectionMode.TriggerVolume
            ? IsPlayerInsideTriggerVolume()
            : IsPlayerStandingOnSurface();
    }

    /// <summary>玩家此刻是否站在本平台上（看脚底和平台表面的位置关系）。</summary>
    private bool IsPlayerStandingOnSurface()
    {
        ThirdPersonController player = ResolvePlayer();
        if (player == null)
            return false;

        // 兼容 InvertGravity：翻转重力时玩家的“上”是世界下方，站的是平台的下表面
        Vector3 up = player.InvertGravity ? Vector3.down : Vector3.up;
        Vector3 feet = player.transform.position;

        // 竖直方向是一段“高度带”：从平台表面往上直到 stayHeightAbovePlatform。
        // 所以站在表面、起跳、跳到半空、落回表面 —— 全程都算在平台上，计时不会被打断。
        float maxAbove = Mathf.Max(StandHeightTolerance, stayHeightAbovePlatform);

        for (int i = 0; i < ownColliders.Length; i++)
        {
            Collider col = ownColliders[i];
            if (col == null || col.isTrigger)
                continue;

            if (IsInsideStandZone(col.bounds, feet, up, maxAbove))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 玩家（胶囊）是否和平台上的 Trigger 体积重叠 —— 经过 / 靠近就算，不需要真的踩上去。
    /// 指定了 triggerVolume 就只认那一个，否则用本平台（含子物体）上所有的 Trigger。
    /// </summary>
    private bool IsPlayerInsideTriggerVolume()
    {
        ThirdPersonController player = ResolvePlayer();
        if (player == null)
            return false;

        if (triggerVolume != null)
            return triggerVolume.enabled && OverlapsTrigger(triggerVolume, player);

        for (int i = 0; i < ownColliders.Length; i++)
        {
            Collider col = ownColliders[i];
            if (col == null || !col.isTrigger || !col.enabled)
                continue;

            if (OverlapsTrigger(col, player))
                return true;
        }

        return false;
    }

    /// <summary>玩家身体和这个 Trigger 是否重叠（水平方向略微放宽，竖直方向容忍一点点贴边）。</summary>
    private static bool OverlapsTrigger(Collider trigger, ThirdPersonController player)
    {
        if (trigger == null || player == null)
            return false;

        Bounds volume = trigger.bounds;
        Bounds body = GetPlayerBounds(player);

        return body.min.x <= volume.max.x + FootprintMargin
            && body.max.x >= volume.min.x - FootprintMargin
            && body.min.z <= volume.max.z + FootprintMargin
            && body.max.z >= volume.min.z - FootprintMargin
            && body.min.y <= volume.max.y + StandDepthTolerance
            && body.max.y >= volume.min.y - StandDepthTolerance;
    }

    /// <summary>玩家身体的包围盒：优先用 CharacterController 的胶囊范围，取不到就按一个普通人体大小的盒子估算。</summary>
    private static Bounds GetPlayerBounds(ThirdPersonController player)
    {
        CharacterController controller = player.GetComponent<CharacterController>();
        if (controller != null && controller.enabled)
            return controller.bounds;

        return new Bounds(player.transform.position + Vector3.up * 0.9f, new Vector3(0.6f, 1.8f, 0.6f));
    }

    /// <summary>
    /// 脚底是否落在“站立区”里：水平方向要在平台包围盒的占地范围内（略微放宽，站边上也算），
    /// 竖直方向要在平台“朝上的那一面”附近的一段高度带内（往下容忍脚底沉入，往上容忍整个跳跃高度）。
    /// </summary>
    private static bool IsInsideStandZone(Bounds bounds, Vector3 feet, Vector3 up, float maxAbove)
    {
        if (feet.x < bounds.min.x - FootprintMargin || feet.x > bounds.max.x + FootprintMargin)
            return false;
        if (feet.z < bounds.min.z - FootprintMargin || feet.z > bounds.max.z + FootprintMargin)
            return false;

        // 脚底在平台表面外侧（沿玩家“上”方向）的距离：刚好站在表面上约为 0
        float above = up.y >= 0f ? feet.y - bounds.max.y : bounds.min.y - feet.y;
        return above >= -StandDepthTolerance && above <= maxAbove;
    }

    /// <summary>取玩家：优先静态入口，其次全局查找（找到后缓存）。</summary>
    private ThirdPersonController ResolvePlayer()
    {
        if (cachedPlayer != null)
            return cachedPlayer;

        cachedPlayer = ThirdPersonController.Instance != null
            ? ThirdPersonController.Instance
            : FindObjectOfType<ThirdPersonController>();

        return cachedPlayer;
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>晃动目标：指定了就用指定的；否则取自己或子物体上第一个带网格的物体（只晃视觉）。</summary>
    private Transform ResolveVisualRoot()
    {
        if (wobbleTarget != null)
            return wobbleTarget;

        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        if (renderers != null && renderers.Length > 0 && renderers[0] != null)
            return renderers[0].transform;

        return transform;
    }

    // ---------------------------------------------------------------- 外观替换

    /// <summary>
    /// 用 intactVisualPrefab 顶掉平台原本的外观（ProBuilder 那块）：
    /// 按原外观的包围盒缩放 + 居中，让两块模型严丝合缝；
    /// 原外观的 Renderer 永久隐藏（MeshCollider / BoxCollider 都保留，玩家照样站得住），
    /// 之后晃动 / 破碎 / 复位全部以新外观为准。
    /// </summary>
    private void SwapInIntactVisual()
    {
        if (intactVisualPrefab == null)
            return;

        Bounds target = default;

        // 对齐基准：优先用原外观（那块 ProBuilder 石头）自己的包围盒；
        // 取不到（例如压根没有原始外观）就退回平台实体碰撞体的范围。
        bool hasTarget = visualRoot != null && visualRoot != transform
            ? TryGetRendererThenColliderBounds(visualRoot, out target)
            : TryGetSolidColliderBounds(out target);

        if (!hasTarget && !TryGetRendererThenColliderBounds(transform, out target))
        {
            Debug.LogWarning($"[DisappearingPlatform] {name} 找不到可用来对齐的原始外观，" +
                             "已跳过「外观替换」。", this);
            return;
        }

        Renderer[] originalRenderers = visualRoot != null
            ? visualRoot.GetComponentsInChildren<Renderer>(true)
            : new Renderer[0];

        GameObject visual = Instantiate(intactVisualPrefab, transform);
        visual.name = $"{intactVisualPrefab.name} (外观)";

        // 先摆正、清零，量出它自己当前的包围盒
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.identity;
        Vector3 prefabScale = visual.transform.localScale;
        Physics.SyncTransforms();

        if (TryGetRendererThenColliderBounds(visual.transform, out Bounds current))
        {
            // 按三轴分别缩放到原外观的尺寸 —— 原模型和石头是配套的，缩放系数本来就接近 1，不会变形；
            // 万一平台尺寸不同（比如三块板子宽度 6 / 8 / 9.5），这样也保证严丝合缝。
            Vector3 factor = new Vector3(
                ScaleFactor(target.size.x, current.size.x),
                ScaleFactor(target.size.y, current.size.y),
                ScaleFactor(target.size.z, current.size.z));

            visual.transform.localScale = Vector3.Scale(prefabScale, factor);
            Physics.SyncTransforms();
        }

        // 居中：让替换后的外观和原外观的中心重合
        if (TryGetRendererThenColliderBounds(visual.transform, out current))
        {
            visual.transform.position += target.center - current.center;
            Physics.SyncTransforms();
        }

        // 原始外观永久隐藏
        List<Renderer> hidden = new List<Renderer>(originalRenderers.Length);
        for (int i = 0; i < originalRenderers.Length; i++)
        {
            Renderer r = originalRenderers[i];
            if (r == null)
                continue;

            r.enabled = false;
            hidden.Add(r);
        }

        replacedRenderers = hidden.ToArray();

        // 新外观跟随原外观的 Layer（方便相机 / 光照保持一致）
        if (originalRenderers.Length > 0 && originalRenderers[0] != null)
            SetLayerRecursively(visual, originalRenderers[0].gameObject.layer);

        visualRoot = visual.transform;
    }

    /// <summary>取物体（含子物体）的参考包围盒：优先用 Renderer，没有 Renderer 就退回 Collider。</summary>
    private static bool TryGetRendererThenColliderBounds(Transform root, out Bounds bounds)
    {
        bounds = default;
        if (root == null)
            return false;

        bool has = false;

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null)
                continue;

            if (!has)
            {
                bounds = r.bounds;
                has = true;
            }
            else
            {
                bounds.Encapsulate(r.bounds);
            }
        }

        if (has)
            return true;

        Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider c = colliders[i];
            if (c == null)
                continue;

            if (!has)
            {
                bounds = c.bounds;
                has = true;
            }
            else
            {
                bounds.Encapsulate(c.bounds);
            }
        }

        return has;
    }

    /// <summary>平台所有实体碰撞体（不含 Trigger）的合并包围盒 —— 也就是玩家真正能站的那块范围。</summary>
    private bool TryGetSolidColliderBounds(out Bounds bounds)
    {
        bounds = default;
        bool has = false;

        for (int i = 0; i < ownColliders.Length; i++)
        {
            Collider c = ownColliders[i];
            if (c == null || c.isTrigger)
                continue;

            if (!has)
            {
                bounds = c.bounds;
                has = true;
            }
            else
            {
                bounds.Encapsulate(c.bounds);
            }
        }

        return has;
    }

    /// <summary>安全地求缩放系数（目标尺寸 / 当前尺寸）：尺寸接近 0 时返回 1，避免除零或爆炸。</summary>
    private static float ScaleFactor(float target, float current)
    {
        if (current < 1e-4f || target < 1e-4f)
            return 1f;

        return Mathf.Clamp(target / current, 1e-3f, 1e3f);
    }

    private static void SetLayerRecursively(GameObject go, int layer)
    {
        if (go == null)
            return;

        go.layer = layer;

        Transform t = go.transform;
        for (int i = 0; i < t.childCount; i++)
            SetLayerRecursively(t.GetChild(i).gameObject, layer);
    }

    private void CacheColliders()
    {
        ownColliders = GetComponentsInChildren<Collider>(true);
        if (ownColliders == null)
            ownColliders = new Collider[0];
    }

    /// <summary>缓存平台自身的外观（用来在破碎时整组隐藏 / 重新生成时恢复显示）。</summary>
    private void CacheVisualRenderers()
    {
        // 取的是【整个平台】下面的所有渲染器，而不是 visualRoot（晃动目标通常只是“那层石头”），
        // 否则挂在平台下的装饰（蘑菇、替换上去的石头模型…）在破碎后会孤零零留在半空中。
        Renderer[] all = GetComponentsInChildren<Renderer>(true);
        if (all == null || all.Length == 0)
        {
            visualRenderers = new Renderer[0];
            return;
        }

        // 被「外观替换」顶掉的原始外观是永久隐藏的，不参与“显示 / 隐藏”切换
        if (replacedRenderers == null || replacedRenderers.Length == 0)
        {
            visualRenderers = all;
            return;
        }

        List<Renderer> kept = new List<Renderer>(all.Length);
        for (int i = 0; i < all.Length; i++)
        {
            Renderer r = all[i];
            if (r == null)
                continue;

            bool replaced = false;
            for (int j = 0; j < replacedRenderers.Length; j++)
            {
                if (replacedRenderers[j] == r)
                {
                    replaced = true;
                    break;
                }
            }

            if (!replaced)
                kept.Add(r);
        }

        visualRenderers = kept.ToArray();
    }

    /// <summary>显示 / 隐藏平台外观（用 Renderer.enabled，不动层级，恢复起来最省事）。</summary>
    private void SetVisualVisible(bool visible)
    {
        if (visualRenderers == null || visualRenderers.Length == 0)
            CacheVisualRenderers();

        for (int i = 0; i < visualRenderers.Length; i++)
        {
            if (visualRenderers[i] != null)
                visualRenderers[i].enabled = visible;
        }
    }

    /// <summary>整体开关平台（含子物体）的碰撞体：破碎瞬间关掉，玩家才会立刻掉下去。</summary>
    private void SetCollidersEnabled(bool enabled)
    {
        for (int i = 0; i < ownColliders.Length; i++)
        {
            if (ownColliders[i] != null)
                ownColliders[i].enabled = enabled;
        }
    }

    /// <summary>设置物体的世界缩放（用于把碎块摆成和平台外观一样大）。</summary>
    private static void SetWorldScale(Transform target, Vector3 worldScale)
    {
        Transform parent = target.parent;
        if (parent == null)
        {
            target.localScale = worldScale;
            return;
        }

        Vector3 p = parent.lossyScale;
        target.localScale = new Vector3(
            Mathf.Approximately(p.x, 0f) ? worldScale.x : worldScale.x / p.x,
            Mathf.Approximately(p.y, 0f) ? worldScale.y : worldScale.y / p.y,
            Mathf.Approximately(p.z, 0f) ? worldScale.z : worldScale.z / p.z);
    }

    private void WarnIfNoSolidCollider()
    {
        if (ownColliders.Length == 0)
        {
            Debug.LogWarning($"[DisappearingPlatform] {name} 上（含子物体）没有 Collider，玩家无法站在上面，机制不会触发。", this);
            return;
        }

        for (int i = 0; i < ownColliders.Length; i++)
        {
            if (ownColliders[i] != null && !ownColliders[i].isTrigger)
                return;
        }

        Debug.LogWarning($"[DisappearingPlatform] {name} 上的 Collider 都是 Trigger，玩家会直接穿过去；" +
                         "请取消勾选平台碰撞体的 Is Trigger。", this);
    }

    private void WarnIfTriggerMissing()
    {
        if (detectionMode != DetectionMode.TriggerVolume || triggerVolume != null)
            return;

        for (int i = 0; i < ownColliders.Length; i++)
        {
            if (ownColliders[i] != null && ownColliders[i].isTrigger)
                return;
        }

        Debug.LogWarning($"[DisappearingPlatform] {name} 的「触发方式」选的是「进入 Trigger 体积」，" +
                         "但平台上（含子物体）没有任何勾了 Is Trigger 的 Collider，机制不会触发。", this);
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Collider[] colliders = GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider col = colliders[i];
            if (col == null)
                continue;

            Bounds b = col.bounds;

            if (col.isTrigger)
            {
                // 触发区（「进入 Trigger 体积」模式下看这个，青色）：人进这个盒子就开始晃
                Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.7f);
                Gizmos.DrawWireCube(b.center, b.size);
                continue;
            }

            // 站立区（「踩在平台上」模式下看这个，橙色）：脚底落在这个盒子上表面附近即视为站上去
            Gizmos.color = new Color(1f, 0.4f, 0.1f, 0.7f);
            Gizmos.DrawWireCube(b.center, b.size);
        }

        // 单独指定的触发区也画出来，方便确认范围
        if (triggerVolume != null)
        {
            Bounds b = triggerVolume.bounds;
            Gizmos.color = new Color(0.2f, 0.9f, 1f, 1f);
            Gizmos.DrawWireCube(b.center, b.size);
        }
    }
#endif
}
