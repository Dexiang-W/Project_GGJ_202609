using StarterAssets;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// 会“踩塌”的平台：玩家踩上去后先静止一段时间，然后才开始轻微晃动（幅度越来越大），最后收缩消失。
///
/// 时间轴： 踩上去 ──[静止 wobbleStartDelay 秒]──> 开始晃动 ──[越晃越厉害]──> 累计 standDuration 到点消失
///          ──[再等 respawnSeconds 秒]──> 长回来（respawnGrowDuration）──> 回到初始状态，可以再次踩塌
///
/// 【配置要点】
///   · 停留时长 = standDuration（秒）：踩上去开始计时，累计到这个时长就消失 —— 这是核心参数；
///   · wobbleStartDelay（秒）：踩上去之后先静止多久才开始晃 —— 要“踩上去几秒后才开始晃动”就调大这个值
///     （例如 1.5 ~ 2 秒）；设为 0 = 一踩上就晃；
///   · 晃动幅度 / 频率 / 抖动 / 回正时间都在「轻微晃动」一栏，默认幅度很小（1° ~ 3.5°），
///     越接近消失晃得越明显（幅度按“开始晃动之后”这段进度从 wobbleMinAngle 涨到 wobbleMaxAngle）；
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
/// 【玩家判定（为什么不写 OnCollision / OnTrigger）】
///   CharacterController 是脚本驱动的，和静态碰撞体之间不会产生 OnCollisionStay / Trigger 事件
///   （会表现为“时好时坏、甚至完全不触发”）。所以这里每帧主动判定：
///   把平台的碰撞体包围盒当成“站立区”，看玩家脚底是否落进去 ——
///   水平方向在平台占地范围内（略微放宽，站边上也算），竖直方向在平台“朝玩家重力反方向”的那一面
///   上方的一段高度带内（承受脚底略微沉入，往上放宽到 stayHeightAbovePlatform 米），
///   所以在平台起跳、跳到半空、落回表面都仍然算“在平台上”，停留计时不会被打断。
///   兼容 InvertGravity：翻转重力时玩家站在平台下表面同样算数。
///
/// 【用法】
///   1. 把本组件挂到平台物体上，保证它（或子物体）上有实体 Collider（推荐 BoxCollider，不要勾 Is Trigger）；
///   2. 调 standDuration（多久消失）和 wobbleStartDelay（踩上去多久开始晃）即可，不需要写任何代码。
/// </summary>
[DisallowMultipleComponent]
public class DisappearingPlatform : MonoBehaviour
{
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

    [Header("轻微晃动（越接近消失晃得越厉害）")]
    [Tooltip("要晃动的部位；留空 = 自动取自己或子物体上第一个带网格的物体（就是本物体自己也没关系）。\n" +
             "想只晃模型、不动碰撞体的话，把模型子物体拖到这里。")]
    [SerializeField] private Transform wobbleTarget;

    [Tooltip("刚踩上去时的晃动幅度（度）：默认很小，只是看起来有点不稳")]
    [SerializeField] private float wobbleMinAngle = 0.8f;

    [Tooltip("即将消失时的晃动幅度（度）：越大越像“快塌了”")]
    [SerializeField] private float wobbleMaxAngle = 3.5f;

    [Tooltip("晃动频率（每秒来回次数）")]
    [SerializeField] private float wobbleFrequency = 8f;

    [Tooltip("晃动时的位置抖动幅度（米）：0 = 只转不抖；轻微抖动会更有“松了”的感觉")]
    [SerializeField] private float wobbleShakeDistance = 0.02f;

    [Tooltip("玩家离开平台后，晃动在多长时间内回复原位（秒）")]
    [SerializeField] private float wobbleRecoverDuration = 0.2f;

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

    [Header("事件（可选，用来接音效 / 特效 / 动画）")]
    [Tooltip("玩家踩上平台、开始计时时触发一次（此时还没开始晃，离开后再站上来会再次触发）")]
    public UnityEvent OnStartStanding = new UnityEvent();
    [Tooltip("平台完全消失（物体被停用）时触发")]
    public UnityEvent OnDisappear = new UnityEvent();
    [Tooltip("平台重新出现时触发（自动恢复 / 按 R 回到安全点都会触发）")]
    public UnityEvent OnRespawn = new UnityEvent();

    // ---------------------------------------------------------------- 运行时状态

    /// <summary>平台状态：完好 / 正在消失 / 已消失 / 正在长回来。</summary>
    private enum State
    {
        Intact,
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

        visualRoot = ResolveVisualRoot();
        baseLocalPos = visualRoot.localPosition;
        baseLocalRot = visualRoot.localRotation;

        CacheColliders();
        WarnIfNoSolidCollider();
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

        if (IsPlayerStandingOnPlatform())
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
    /// 按“开始晃动之后的进度”施加晃动：幅度从 wobbleMinAngle 线性涨到 wobbleMaxAngle，越接近消失晃得越厉害；
    /// progress01 为 0（还没到晃动时刻）时完全回正、绝对静止。
    /// 位置抖动与旋转共用同一个相位，看起来是同一股力在晃。
    /// </summary>
    private void ApplyWobblePose(float progress01)
    {
        if (visualRoot == null)
            return;

        wobblePhase += Time.deltaTime * Mathf.Max(0f, wobbleFrequency) * Mathf.PI * 2f;

        float amplitude = Mathf.Lerp(wobbleMinAngle, wobbleMaxAngle, progress01) * wobbleWeight;

        float tiltX = Mathf.Sin(wobblePhase) * amplitude;
        float tiltZ = Mathf.Cos(wobblePhase * 0.87f) * amplitude;

        Vector3 shake = Vector3.zero;
        if (wobbleShakeDistance > 0f)
        {
            shake = new Vector3(
                Mathf.Sin(wobblePhase * 1.7f),
                0f,
                Mathf.Cos(wobblePhase * 1.31f)) * (wobbleShakeDistance * wobbleWeight * Mathf.Lerp(0.5f, 1f, progress01));
        }

        visualRoot.localRotation = baseLocalRot * Quaternion.Euler(tiltX, 0f, tiltZ);
        visualRoot.localPosition = baseLocalPos + shake;
    }

    // ---------------------------------------------------------------- 消失 / 恢复

    private void BeginVanishing()
    {
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
    }

    // ---------------------------------------------------------------- 玩家判定

    /// <summary>玩家此刻是否站在本平台上（每帧主动判定，不依赖碰撞 / 触发事件）。</summary>
    private bool IsPlayerStandingOnPlatform()
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

    private void CacheColliders()
    {
        ownColliders = GetComponentsInChildren<Collider>(true);
        if (ownColliders == null)
            ownColliders = new Collider[0];
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

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        // 站立区提示：平台碰撞体的包围盒（脚底落在这个盒子上表面附近即视为站上去）
        Gizmos.color = new Color(1f, 0.4f, 0.1f, 0.7f);

        Collider[] colliders = GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider col = colliders[i];
            if (col == null)
                continue;

            Bounds b = col.bounds;
            Gizmos.DrawWireCube(b.center, b.size);
        }
    }
#endif
}
