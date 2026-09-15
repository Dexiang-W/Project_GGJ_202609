using System.Collections;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// 能量拾取物（“能量球”，触碰后能量 +1 格，浅绿圆点变亮绿）。
/// 用法：给空物体加 BoxCollider / SphereCollider（Is Trigger 勾选，大小即拾取范围），再加本组件即可；
/// 也可以直接挂到场景里某个“能量水晶 / 能量柱”样式的物体上（该物本身需是 Trigger 或另配一个 Trigger 子物体）；
/// 没有碰撞体也能用：勾上“无碰撞体时用距离判定”，靠近 pickupRadius 范围内就会自动吸收。
///
/// 【P_EnergyBall（Blender 程序化动画）】
///   · 把模型自带的 AnimationClip 拖到 Idle Clip（或用菜单
///     “GGJ2026/能量球 → 把选中物体配成能量球（自动填动画）” 自动填），Play 时会循环播放，球就“活”起来；
///   · 没填 Clip 时，用下面的“兜底浮动/自转”让它也能动（不会抢 Clip 的表现）；
///   · 被吸收时会飞向玩家身体（胸口）并缩小到 0 后消失（Fly To Player On Pickup）。
///
/// 【重新生成（可选，默认关闭）】
///   · 想做成“可再生资源”：勾上 respawnAfterPickup，并把 respawnSeconds 填成想要的间隔（秒）——
///     被吸收后过这么多秒，球会原地重新出现、可以再次吸收；
///   · 默认不勾：吃掉就永久消失（一次性资源）；
///   · 重新出现时用 OnRespawn 事件可以接音效 / 特效 / “长回来”的动画。
///   注意：重新生成的位置就是原来那个位置，如果玩家一直站在拾取范围里不动，
///   球一出现就会立刻被再次吸收（看起来是一闪一闪）——这是设计如此，把球放在玩家不会久留的位置即可。
///
/// 与 R 键回溯（GGJLevelResetManager）的关系：
///   · 被玩家吸收（oneShot 拾取）后本组件会记下 IsCollected = true；
///   · 没有开启“重新生成”的球：按 R 还原关卡时【已经被吸收的球不会重新生成】（吸收算永久进度），
///     还没被吸收的球保持原样；玩家的能量也不会被回溯回滚，
///     所以回溯后要靠玩家自己去找剩下的能量球来补能量。
///   · 开启了“重新生成”的球：什么时候在、什么时候不在完全由它自己的计时决定（见 CanRespawn），
///     按 R 不干预它 —— 否则“快照恰好拍在它消失的那几秒里”会把已经长回来的球又按回消失状态，再也回不来。
///   · 想让所有球都回到初始状态：重新加载关卡即可（切关卡 / 重开流程会重建场景对象）。
/// </summary>
[DisallowMultipleComponent]
public class EnergyPickup : MonoBehaviour
{
    [Header("触碰对象")]
    [SerializeField] private string playerTag = "Player";

    [Header("拾取设置")]
    [Tooltip("一次拾取增加的能量格数（默认 1）")]
    [SerializeField] private int energyAmount = 1;
    [Tooltip("拾取一次后是否失效（勾选后碰到一次就被吃掉，隐藏并停用）")]
    [SerializeField] private bool oneShot = true;

    [Header("重新生成（可选，默认关闭）")]
    [Tooltip("被吸收后是否重新生成。\n" +
             "取消勾选（默认）：吃掉就永久消失，按 R 回溯也不会回来（一次性资源）。\n" +
             "勾选：过 respawnSeconds 秒原地重新出现，可以再次被吸收（可再生资源）。\n" +
             "只在勾选了上面“拾取一次后失效（oneShot）”时才有意义。")]
    [SerializeField] private bool respawnAfterPickup = false;

    [Tooltip("重新生成的间隔（秒）：从被吸收那一刻开始算，过这么多秒球再次出现。\n" +
             "只在勾选了“被吸收后重新生成”时生效；填 0 或负数 = 不自动生成。")]
    [SerializeField] private float respawnSeconds = 8f;

    [Header("动画（P_EnergyBall）")]
    [Tooltip("模型自带的循环动画 Clip（Blender 里做的程序化动画导进来的那个）。\n" +
             "留空且勾选了“兜底浮动/自转”：会用上下浮动 + 自转让它动起来。")]
    [SerializeField] private AnimationClip idleClip;
    [Tooltip("Start / 重新出现时自动播放上面的 Clip（循环）")]
    [SerializeField] private bool playIdleClipOnStart = true;
    [Tooltip("模型上带的 Animator 没有控制器时，临时关掉它，改用 Animation 组件播 Clip（否则 Animator 会抢走动画权）")]
    [SerializeField] private bool disableAnimatorWhenUsingLegacy = true;
    [Tooltip("没有 Clip 时的兜底：上下浮动 + 自转（保证 Play 起来时球不会是死的）")]
    [SerializeField] private bool fallbackIdleMotion = true;
    [SerializeField] private float idleFloatAmplitude = 0.08f;
    [SerializeField] private float idleFloatSpeed = 1.6f;
    [SerializeField] private float idleSpinSpeed = 35f;
    [Tooltip("每个球起落错开一点，看起来不是整齐划一地跳")]
    [SerializeField] private bool randomizeIdlePhase = true;

    [Header("吸收表现（飞向玩家）")]
    [Tooltip("勾选：被吸收时飞向玩家身体并缩小到 0 再消失；取消：和以前一样瞬间消失")]
    [SerializeField] private bool flyToPlayerOnPickup = true;
    [Tooltip("飞向玩家 + 缩小到 0 的总时长（秒）")]
    [SerializeField] private float absorbDuration = 0.45f;
    [Tooltip("玩家身上的目标点：留空 = 玩家物体位置 + 身上偏移（胸口）")]
    [SerializeField] private Transform absorbTargetOverride;
    [Tooltip("目标点在玩家身上的高度偏移（米），默认约在胸口")]
    [SerializeField] private float absorbHeightOffset = 1f;
    [Tooltip("飞过去时额外自转的速度（度/秒），让它看起来被“吸”进去")]
    [SerializeField] private float absorbSpinSpeed = 720f;
    [Tooltip("勾选：飞到玩家身上那一刻才加能量 / 播音效；\n" +
             "不勾选（默认）：一碰到就加能量，飞的过程只是表现。")]
    [SerializeField] private bool addEnergyOnArrive = false;

    [Header("无碰撞体时（距离判定）")]
    [Tooltip("物体身上没有 Trigger 碰撞体时，用“玩家走近到 pickupRadius 内”来代替碰撞触发")]
    [SerializeField] private bool useDistanceFallback = true;
    [SerializeField] private float pickupRadius = 1.2f;

    [Header("事件（可选，用来接音效 / 特效 / 动画）")]
    [Tooltip("能量球重新生成、再次可以吸收时触发一次")]
    public UnityEvent OnRespawn = new UnityEvent();

    /// <summary>是否已经被玩家吸收（oneShot 拾取后为 true，重新生成后回到 false）。</summary>
    private bool collected;

    private const string IdleClipName = "EnergyBall_Idle";

    private Transform player;
    private Animation legacyAnimation;
    private Animator cachedAnimator;
    private bool hasTriggerCollider;
    private bool absorbing;
    private bool idlePaused;

    private Vector3 initialLocalPos;
    private Vector3 initialLocalScale;
    private bool initialPoseCaptured;
    private Vector3 idleBaseWorldPos;
    private float idleTime;
    private float idlePhase;

    /// <summary>已经被玩家吸收（且不会自己重新生成）：按 R 回溯时这颗球保持消失。</summary>
    public bool IsCollected => collected;

    /// <summary>
    /// 这颗球被吸收后会不会自己重新生成（勾了“重新生成”且间隔有效）。
    /// 这类球的出现 / 消失完全由自己的计时决定，按 R 回溯时不要去干预它（见 GGJLevelResetManager）。
    /// </summary>
    public bool CanRespawn => respawnAfterPickup && respawnSeconds > 0f;

    // ————————————————————————————————————— 生命周期

    private void Awake()
    {
        CaptureInitialPose();
    }

    /// <summary>记下初始位置 / 大小（重新生成时用来复位）。</summary>
    private void CaptureInitialPose()
    {
        if (initialPoseCaptured)
            return;

        initialLocalPos = transform.localPosition;
        initialLocalScale = transform.localScale;
        initialPoseCaptured = true;
    }

    private void Start()
    {
        // 视觉基准点（兜底浮动用世界坐标上下飘）
        idleBaseWorldPos = transform.position;
        idlePhase = randomizeIdlePhase ? Random.Range(0f, Mathf.PI * 2f) : 0f;

        hasTriggerCollider = FindTriggerCollider() != null;

        if (playIdleClipOnStart)
            PlayIdleClip();

        if (hasTriggerCollider)
            return;

        if (useDistanceFallback)
        {
            // 没有 Trigger 碰撞体 → 用距离判定兜底，玩家走近就会被吸收
            player = FindPlayer();
        }
        else
        {
            Debug.LogWarning($"[EnergyPickup] {name} 身上没有 Trigger 碰撞体，也没有开“距离判定”，这颗能量球永远不会被吸收。", this);
        }
    }

    private void Update()
    {
        if (collected || absorbing)
            return;

        if (idleClip == null && fallbackIdleMotion && !idlePaused)
            UpdateFallbackIdle();

        if (useDistanceFallback && !hasTriggerCollider)
            CheckDistancePickup();
    }

    private void OnEnable()
    {
        CaptureInitialPose();

        // 首次激活（关卡开始）时 collected 还是 false，不需要做什么；
        // 只有“被吸收之后又被激活”（自动重新生成 / 层级被重新激活）才复位成“可以再次吸收”。
        if (collected)
        {
            collected = false;
            RestoreVisual();
            if (playIdleClipOnStart)
                PlayIdleClip();
            OnRespawn?.Invoke();
            return;
        }

        RestoreVisual();
        if (playIdleClipOnStart)
            PlayIdleClip();
    }

    // ————————————————————————————————————— 拾取

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(playerTag))
            return;

        if (collected || absorbing)
            return;

        Pickup(other.transform);
    }

    /// <summary>没有 Trigger 碰撞体时的兜底：玩家走近就吸收。</summary>
    private void CheckDistancePickup()
    {
        if (player == null)
            player = FindPlayer();

        if (player == null)
            return;

        float radius = Mathf.Max(0.01f, pickupRadius);
        if ((transform.position - player.position).sqrMagnitude > radius * radius)
            return;

        Pickup(player);
    }

    private void Pickup(Transform playerRoot)
    {
        // 允许反复拾取（oneShot 关闭）：只加能量，球不消失
        if (!oneShot)
        {
            GrantEnergy();
            return;
        }

        collected = true;

        if (flyToPlayerOnPickup && absorbDuration > 0f)
        {
            StartCoroutine(FlyToPlayerRoutine(playerRoot));
            return;
        }

        // 老行为：瞬间吃掉
        GrantEnergy();
        gameObject.SetActive(false);

        if (CanRespawn)
            GGJRespawnTimer.Schedule(gameObject, respawnSeconds);
    }

    /// <summary>飞向玩家身体 → 缩小到 0 → 消失（收回能量）。</summary>
    private IEnumerator FlyToPlayerRoutine(Transform playerRoot)
    {
        absorbing = true;

        // 飞行途中不再吃新的碰撞 / 距离判定
        SetCollidersEnabled(false);

        // 兜底浮动 / 模型动画都不再插手（位置、大小由吸收动画接管）
        idlePaused = true;
        PauseIdleAnimation();

        if (!addEnergyOnArrive)
            GrantEnergy();

        Vector3 startPos = transform.position;
        Vector3 startScale = transform.localScale;
        float duration = Mathf.Max(0.01f, absorbDuration);
        float t = 0f;

        while (t < duration)
        {
            t += Time.deltaTime;
            float p = Mathf.Clamp01(t / duration);

            // 目标点每帧刷新：玩家还在跑也能吸进去
            Vector3 target = GetAbsorbTargetPoint(playerRoot);
            transform.position = Vector3.Lerp(startPos, target, p * p * (3f - 2f * p));   // smoothstep：先慢后快

            // 越靠近越小，最后缩成 0
            float shrink = 1f - p;
            transform.localScale = startScale * (shrink * shrink);

            if (absorbSpinSpeed != 0f)
                transform.Rotate(transform.up, absorbSpinSpeed * Time.deltaTime, Space.World);

            yield return null;
        }

        transform.position = GetAbsorbTargetPoint(playerRoot);
        transform.localScale = Vector3.zero;

        if (addEnergyOnArrive)
            GrantEnergy();

        absorbing = false;
        gameObject.SetActive(false);

        if (CanRespawn)
            GGJRespawnTimer.Schedule(gameObject, respawnSeconds);
    }

    /// <summary>玩家身上的吸收目标点（胸口）：可以手动指定骨骼，默认是玩家位置 + 高度偏移。</summary>
    private Vector3 GetAbsorbTargetPoint(Transform playerRoot)
    {
        if (absorbTargetOverride != null)
            return absorbTargetOverride.position;

        if (playerRoot == null)
            return transform.position;

        return playerRoot.position + Vector3.up * absorbHeightOffset;
    }

    private void GrantEnergy()
    {
        GameplayHUD.EnsureCreated();
        GameplayHUD.Instance.AddEnergy(energyAmount);

        // 拾取音效：SFX_Energy_Pickup（GameplayHUD.EnsureCreated 会同时确保音频系统就绪）
        if (AudioManager.Instance != null)
            AudioManager.Instance.PlayPickup();
    }

    // ————————————————————————————————————— 动画 / 视觉

    /// <summary>播放模型自带的循环动画（Blender 程序化动画导进来的 Clip）。</summary>
    private void PlayIdleClip()
    {
        if (idleClip == null)
            return;

        cachedAnimator = GetComponentInChildren<Animator>(true);

        // 已经有 AnimatorController 在管动画了 → 不用我们插手（它自己会播默认状态）
        if (cachedAnimator != null && cachedAnimator.runtimeAnimatorController != null)
        {
            cachedAnimator.enabled = true;
            return;
        }

        // 空 Animator 会把动画权抢走，导致 Animation 组件播了没效果
        if (cachedAnimator != null && disableAnimatorWhenUsingLegacy)
            cachedAnimator.enabled = false;

        if (legacyAnimation == null)
        {
            legacyAnimation = GetComponent<Animation>();
            if (legacyAnimation == null)
                legacyAnimation = gameObject.AddComponent<Animation>();
        }

        if (legacyAnimation.GetClip(IdleClipName) == null)
            legacyAnimation.AddClip(idleClip, IdleClipName);

        legacyAnimation.wrapMode = WrapMode.Loop;
        legacyAnimation.cullingType = AnimationCullingType.AlwaysAnimate;   // 出画也继续动，走近时不会突然“接上”
        legacyAnimation.Play(IdleClipName);
    }

    /// <summary>吸收动画期间停掉模型动画，避免它和“飞向玩家 / 缩小”抢位置与缩放。</summary>
    private void PauseIdleAnimation()
    {
        if (legacyAnimation != null)
            legacyAnimation.Stop();

        if (cachedAnimator == null)
            cachedAnimator = GetComponentInChildren<Animator>(true);

        if (cachedAnimator != null)
            cachedAnimator.enabled = false;
    }

    /// <summary>没有 Clip 时的兜底：上下浮动 + 自转。</summary>
    private void UpdateFallbackIdle()
    {
        if (idleFloatAmplitude != 0f)
        {
            idleTime += Time.deltaTime * idleFloatSpeed;
            Vector3 p = idleBaseWorldPos;
            p.y += Mathf.Sin(idleTime + idlePhase) * idleFloatAmplitude;
            transform.position = p;
        }

        if (idleSpinSpeed != 0f)
            transform.Rotate(transform.up, idleSpinSpeed * Time.deltaTime, Space.World);
    }

    /// <summary>回到初始位置 / 大小（重新生成 或 重新激活时调用）。</summary>
    private void RestoreVisual()
    {
        transform.localPosition = initialLocalPos;
        transform.localScale = initialLocalScale;
        idleBaseWorldPos = transform.position;
        idleTime = 0f;
        idlePaused = false;
        absorbing = false;
        SetCollidersEnabled(true);
    }

    private Collider FindTriggerCollider()
    {
        Collider[] colliders = GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            if (colliders[i] != null && colliders[i].isTrigger)
                return colliders[i];
        }
        return null;
    }

    private void SetCollidersEnabled(bool enabled)
    {
        Collider[] colliders = GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            if (colliders[i] != null)
                colliders[i].enabled = enabled;
        }
    }

    private Transform FindPlayer()
    {
        GameObject go = GameObject.FindGameObjectWithTag(playerTag);
        return go != null ? go.transform : null;
    }
}
