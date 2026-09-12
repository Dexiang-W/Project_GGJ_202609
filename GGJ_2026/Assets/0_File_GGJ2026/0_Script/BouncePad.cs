using System.Collections;
using StarterAssets;
using UnityEngine;

/// <summary>
/// 能量弹跳板 / 能量蘑菇（玩家进入弹跳区域后把其竖直弹起；力度按“本蘑菇自身能量档位”分档）。
///
/// 能量档位（默认 0 ~ 2，每个蘑菇各自独立）：
///   · E：玩家靠近按 E → 消耗玩家 1 格能量，本蘑菇 +1 档（最多 maxEnergyLevel）；
///   · Q：本蘑菇 -1 档 → 1 格能量还回玩家；
///   · 档位越高：蘑菇上半部分（scaleTarget）越大、弹力越强；
///   · 只有被分配过能量的蘑菇才会变化，其它蘑菇保持原样（“这个 2 档、那个 0 档”）。
///
/// 弹力对应关系（可在 Inspector 调）：
///   0 档 = zeroEnergyLaunch；1 档 = oneEnergyLaunch；2 档及以上 = twoPlusEnergyLaunch。
///
/// 用法：给物体加 BoxCollider（Is Trigger 勾选，放在地面/平台上沿），再加本组件。
///
/// 触发机制说明（重要）：
/// 早期版本只靠 OnTriggerEnter/Stay —— 而 CharacterController 只有在自己发生物理位移时
/// 才会向静态 Trigger 发送回调；如果玩家传送/生成后“已经站在板内”而没发生一次“从外到内”
/// 的穿越，就永远收不到事件 → 表现为时好时坏（正式流程里是“完全没弹起”）。
/// 现改为每个物理帧用 Physics.OverlapBox 主动探测弹跳区域内的角色控制器，
/// 不再依赖 Trigger 事件是否送达，保证“踩上去就弹”。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
public class BouncePad : MonoBehaviour, IEnergyChargeable
{
    [Header("触碰对象")]
    [Tooltip("玩家的 Tag；用于过滤目标。留空表示只按角色控制器识别")]
    [SerializeField] private string playerTag = "Player";

    [Header("三档弹力（按本蘑菇自身能量档位，竖直向上速度，单位 m/s，可自己调）")]
    [Tooltip("0 档能量时的弹力；0 = 不弹")]
    [SerializeField] private float zeroEnergyLaunch = 3f;
    [Tooltip("1 档能量时的弹力（中档）")]
    [SerializeField] private float oneEnergyLaunch = 7f;
    [Tooltip("2 档及以上能量时的弹力（高档）")]
    [SerializeField] private float twoPlusEnergyLaunch = 12f;

    [Header("防重复触发")]
    [Tooltip("弹过一次后，多少秒内不再重复触发（防止玩家悬空停在板内时反复被弹）")]
    [SerializeField] private float retriggerCooldown = 0.8f;

    [Header("踩踏动画（玩家踩上去时播放）")]
    [Tooltip("蘑菇动画剪辑：从 Action_mogu.fbx 里直接把 AnimationClip 拖进来即可，不需要 Animator Controller / 状态机")]
    [SerializeField] private AnimationClip squashClip;

    [Tooltip("动画作用的目标（模型骨骼根节点，一般就是 Action_mogu.fbx 实例本身）。\n留空自动依次取：缩放目标 → 父级 → 本物体")]
    [SerializeField] private Transform squashTarget;

    [Tooltip("各能量档位的动画速度倍率，索引 = 档位：\n0 档 = 2 倍速（跳得矮、动作要快，才衔接得上）\n1 档 = 1.5 倍速\n2 档 = 1 倍速（原速）\n想自己调就改这三个数；数组比档位数短时，多出的档位沿用最后一个值")]
    [SerializeField] private float[] levelAnimationSpeeds = new float[] { 2f, 1.5f, 1f };

    [Tooltip("勾选后忽略上面的速度数组：自动让动画总时长 = 玩家被弹起后的滞空时间（2v/g），保证落地前刚好播完")]
    [SerializeField] private bool matchPlayerAirTime = false;

    [Tooltip("动画播完后回到第 0 帧（恢复蘑菇原样）；不勾选则停在最后一帧")]
    [SerializeField] private bool returnToFirstFrameOnFinish = true;

    [Tooltip("改用 Animator 状态机播放（蘑菇身上已有 Animator 时用）；不勾选则用上面的动画剪辑直接采样播放")]
    [SerializeField] private bool useAnimatorState = false;
    [Tooltip("要播放的 Animator 状态名（需与 Animator 里的状态同名）")]
    [SerializeField] private string animatorStateName = "";

    [Header("踩踏形变（静态模型也能动，不需要骨骼/动画文件）")]
    [Tooltip("勾选后踩上去时对 squashTarget 做“压扁→弹回”形变。\n静态 .obj 模型也能用，不依赖 AnimationClip / Animator / 骨骼")]
    [SerializeField] private bool enableSquashDeform = true;
    [Tooltip("各能量档位的压扁深度（0~1，0.45 = 高度压到 55%）：索引 = 档位，和速度一样分开调")]
    [SerializeField] private float[] levelSquashDepths = new float[] { 0.45f, 0.35f, 0.25f };
    [Tooltip("一次“压下 → 回弹”的总时长（秒）；实际时长 = 本值 ÷ 档位速度倍率（0 档最快）")]
    [SerializeField] private float squashDuration = 0.4f;
    [Tooltip("压扁时横向变粗的系数（1.25 = 横向撑到 125%，做出保体积的“软糖”感；1 = 横向不变）")]
    [SerializeField] private float squashWiden = 1.25f;

    [Header("玩家充能（E 逐档 +1 / Q 逐档 -1）")]
    [Tooltip("勾选后玩家靠近可按 E 给它充能、按 Q 回收（每一档 = 玩家 1 格能量）")]
    [SerializeField] private bool enablePlayerCharging = true;
    [Tooltip("最高能量档位（默认 2，即 0/1/2 三档）")]
    [SerializeField] private int maxEnergyLevel = 2;
    [Tooltip("初始能量档位（默认 0 = 最小）")]
    [SerializeField] private int startEnergyLevel = 0;

    [Header("随能量档位变大的部位（蘑菇上半部分）")]
    [Tooltip("要随能量放大的 Transform；留空自动使用本物体的父级（通常是蘑菇模型根节点）")]
    [SerializeField] private Transform scaleTarget;
    [Tooltip("1 档时该部位的缩放倍率（相对初始大小）")]
    [SerializeField] private float level1ScaleMultiplier = 1.35f;
    [Tooltip("2 档时该部位的缩放倍率")]
    [SerializeField] private float level2ScaleMultiplier = 1.75f;
    [Tooltip("档位切换时的缩放过渡时间（0 = 立即）")]
    [SerializeField] private float scaleLerpDuration = 0.25f;

    private BoxCollider zoneCollider;
    private float lastLaunchTime = -100f;

    // ---- 踩踏动画运行时状态 ----
    private Animator cachedAnimator;
    private bool animPlaying;
    private float animTime;
    private float animSpeed = 1f;

    // ---- 诊断提示（只提示一次，避免刷屏）----
    private bool warnedNoAnimator;
    private bool warnedNoSkeleton;

    // ---- 踩踏形变运行时状态 ----
    private bool deforming;
    private float deformTime;
    private float deformDuration = 0.4f;
    private float deformDepth;
    private Vector3 deformBaseScale = Vector3.one;
    private Transform deformTarget;
    private Vector3 squashBaseScale = Vector3.one;

    private int energyLevel;
    private Vector3 baseScale = Vector3.one;
    private Coroutine scaleRoutine;

    /// <summary>一次形变里“压下”占的时间比例，剩下的都是回弹段。</summary>
    private const float SquashCompressPortion = 0.35f;

    // 复用缓冲区，避免每帧分配 GC
    private static readonly Collider[] OverlapBuffer = new Collider[16];

    /// <summary>当前能量档位（0 ~ MaxEnergyLevel）。</summary>
    public int EnergyLevel => energyLevel;

    /// <summary>最高能量档位。</summary>
    public int MaxEnergyLevel => maxEnergyLevel;

    private void Awake()
    {
        zoneCollider = GetComponent<BoxCollider>();
        if (zoneCollider == null)
        {
            enabled = false;
            Debug.LogError($"[BouncePad] {name} 缺少 BoxCollider，弹跳板已停用。", this);
            return;
        }

        // 记录初始大小（缩放基准），并按初始档位刷新外观
        if (scaleTarget == null)
            scaleTarget = transform.parent != null ? transform.parent : transform;
        baseScale = scaleTarget.localScale;
        squashBaseScale = ResolveSquashTarget().localScale;

        energyLevel = Mathf.Clamp(startEnergyLevel, 0, Mathf.Max(0, maxEnergyLevel));
        ApplyEnergyVisual(true);

        // 玩家充能交互（E 加档 / Q 回收）交给 InteractableObject 统一处理
        if (enablePlayerCharging)
            EnsureEnergyInteraction();
    }

    private void FixedUpdate()
    {
        if (zoneCollider == null)
            return;

        if (Time.time - lastLaunchTime < retriggerCooldown)
            return;

        // 以弹跳板 Trigger 的世界包围盒做主动探测。
        // QueryTriggerInteraction.Ignore：只探测实体（玩家 CharacterController），
        // 顺便排除板子自身的 Trigger 及其它纯触发区，避免误触发/自触发。
        Transform pad = transform;
        Vector3 worldSize = Vector3.Scale(zoneCollider.size, pad.lossyScale);
        Vector3 halfExtents = worldSize * 0.5f;
        Vector3 center = pad.TransformPoint(zoneCollider.center);

        int count = Physics.OverlapBoxNonAlloc(
            center, halfExtents, OverlapBuffer,
            pad.rotation, Physics.AllLayers, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < count; i++)
        {
            Collider hit = OverlapBuffer[i];
            if (hit == null)
                continue;

            // 玩家靠 CharacterController 移动；地面上其它实体（平台/装饰）不参与判定
            if (!(hit is CharacterController))
                continue;

            TryLaunchPlayer(hit.gameObject);
        }
    }

    private void TryLaunchPlayer(GameObject contact)
    {
        ThirdPersonController player = contact.GetComponentInParent<ThirdPersonController>();
        if (player == null)
            return;

        // Tag 用于过滤；但当前被控制的玩家（Instance）即使 Tag 没对上也要放行，
        // 避免 Tag 配错导致板子永远不响应
        if (!string.IsNullOrEmpty(playerTag)
            && !contact.CompareTag(playerTag)
            && (ThirdPersonController.Instance == null || ThirdPersonController.Instance != player))
            return;

        float strength = EnergyLaunchStrength();
        if (strength <= 0f)
            return;

        player.LaunchUp(strength);

        // 踩踏动画：蘑菇被踩的动画，速度按本蘑菇自己的档位取（0 档最快、2 档原速）
        PlaySquashAnimation(strength, player);

        // 踩踏形变：不依赖骨骼，静态模型也会被“踩扁再弹回”，速度同样按档位分开
        PlaySquashDeform(strength, player);

        // 弹跳蘑菇音效（统一走 AudioManager，素材已接入 GGJ_AudioManager，无需每块板单独拖）
        if (AudioManager.Instance != null)
            AudioManager.Instance.PlayBounce();

        lastLaunchTime = Time.time;
    }

    /// <summary>按本蘑菇自己的能量档位取弹力（0/1/2 三档）。</summary>
    private float EnergyLaunchStrength()
    {
        if (energyLevel >= 2)
            return twoPlusEnergyLaunch;
        if (energyLevel == 1)
            return oneEnergyLaunch;
        return zeroEnergyLaunch;
    }

    // ------------------------------------------------------------ 多档能量（IEnergyChargeable）

    /// <summary>设置能量档位并刷新外观（蘑菇变大/变小）。每个实例各自独立。</summary>
    public void SetEnergyLevel(int level)
    {
        energyLevel = Mathf.Clamp(level, 0, Mathf.Max(0, maxEnergyLevel));
        ApplyEnergyVisual(false);
    }

    /// <summary>复用统一的交互组件（InteractableObject）处理靠近高亮与 E/Q。</summary>
    private void EnsureEnergyInteraction()
    {
        InteractableObject interactable = GetComponent<InteractableObject>();
        if (interactable == null)
            interactable = gameObject.AddComponent<InteractableObject>();

        // 让交互组件用“多档能量”规则处理 E 充能 / Q 回收
        interactable.SetupMultiLevelEnergy(this);
    }

    /// <summary>按当前档位刷新 scaleTarget 的大小：档位越高越大（上半部分变大）。</summary>
    private void ApplyEnergyVisual(bool instant)
    {
        if (scaleTarget == null)
            return;

        Vector3 targetScale = baseScale * EnergyScaleMultiplier();

        if (scaleRoutine != null)
        {
            StopCoroutine(scaleRoutine);
            scaleRoutine = null;
        }

        if (instant || scaleLerpDuration <= 0f || !gameObject.activeInHierarchy)
        {
            scaleTarget.localScale = targetScale;
            return;
        }

        scaleRoutine = StartCoroutine(LerpScaleRoutine(targetScale, scaleLerpDuration));
    }

    private IEnumerator LerpScaleRoutine(Vector3 targetScale, float duration)
    {
        Vector3 from = scaleTarget.localScale;
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / duration);
            scaleTarget.localScale = Vector3.Lerp(from, targetScale, k);
            yield return null;
        }

        scaleTarget.localScale = targetScale;
        scaleRoutine = null;
    }

    // ------------------------------------------------------------ 踩踏动画

    /// <summary>
    /// 玩家踩上去时播放蘑菇动画。速度按本蘑菇当前档位取（0 档最快、2 档原速），
    /// 可用 matchPlayerAirTime 让动画总时长自动等于玩家滞空时间。
    /// </summary>
    private void PlaySquashAnimation(float launchStrength, ThirdPersonController player)
    {
        float speed = ResolveAnimationSpeed(launchStrength, player);

        // ① Animator 状态机模式（蘑菇身上已有 Animator 时）
        if (useAnimatorState)
        {
            Animator animator = ResolveAnimator();
            if (animator == null)
            {
                WarnOnce(ref warnedNoAnimator,
                    $"[BouncePad] {name}：勾选了“用 Animator 播放”，但蘑菇上找不到 Animator 组件。" +
                    "请给带动画的模型实例（如 Action_mogu.fbx）加 Animator 并指定状态名。");
                return;
            }

            if (string.IsNullOrEmpty(animatorStateName))
            {
                WarnOnce(ref warnedNoAnimator,
                    $"[BouncePad] {name}：勾选了“用 Animator 播放”，但 Animator State Name 为空。");
                return;
            }

            animator.speed = speed;
            animator.Play(animatorStateName, 0, 0f);
            return;
        }

        // ② 动画剪辑直接采样模式（不需要 Animator Controller）
        if (squashClip == null)
            return;

        Transform target = ResolveSquashTarget();
        if (target == null)
            return;

        // 目标里没有蒙皮网格 → 是静态模型（.obj 之类），骨骼动画驱动不了它。
        // 这是“拖了 clip 却没反应”最常见的原因，这里明确提示一次。
        if (!warnedNoSkeleton && target.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
        {
            WarnOnce(ref warnedNoSkeleton,
                $"[BouncePad] {name}：动画目标「{target.name}」及子物体里没有蒙皮网格(SkinnedMeshRenderer)，" +
                "说明它是静态模型，Action_mogu 这类骨骼动画不会有效果。" +
                "要么换成带动画的模型（Action_mogu.fbx），要么用本组件自带的“踩踏形变”做动效。");
        }

        animSpeed = speed;
        animTime = 0f;
        animPlaying = true;
        squashClip.SampleAnimation(target.gameObject, 0f);
    }

    private void Update()
    {
        UpdateSquashAnimation();
        UpdateSquashDeform();
    }

    private void UpdateSquashAnimation()
    {
        if (!animPlaying)
            return;

        Transform target = ResolveSquashTarget();
        if (squashClip == null || target == null)
        {
            animPlaying = false;
            return;
        }

        float length = Mathf.Max(0.0001f, squashClip.length);
        animTime += Time.deltaTime * animSpeed;

        if (animTime >= length)
        {
            // 播放结束：默认拨回第 0 帧（蘑菇恢复原样），也可选择停在末帧
            animPlaying = false;
            squashClip.SampleAnimation(target.gameObject, returnToFirstFrameOnFinish ? 0f : length);
            return;
        }

        squashClip.SampleAnimation(target.gameObject, animTime);
    }

    // ------------------------------------------------------------ 踩踏形变（不依赖骨骼 / 动画文件）

    /// <summary>
    /// 踩踏形变：直接把目标的 Transform 压扁再弹回，静态模型（.obj）也能动。
    /// 时长同样按档位速度倍率换算（0 档最快），保证矮蘑菇也能在玩家落地前恢复原样。
    /// </summary>
    private void PlaySquashDeform(float launchStrength, ThirdPersonController player)
    {
        if (!enableSquashDeform)
            return;

        Transform target = ResolveSquashTarget();
        if (target == null)
            return;

        deformTarget = target;
        deformBaseScale = ResolveDeformBaseScale(target);
        deformDepth = ResolveSquashDepth();
        deformDuration = Mathf.Max(0.05f, squashDuration / ResolveAnimationSpeed(launchStrength, player));
        deformTime = 0f;
        deforming = true;
    }

    private void UpdateSquashDeform()
    {
        if (!deforming)
            return;

        if (deformTarget == null)
        {
            deforming = false;
            return;
        }

        deformTime += Time.deltaTime;
        float k = Mathf.Clamp01(deformTime / deformDuration);

        // amount > 0 = 压扁，< 0 = 被拉长（回弹过冲）
        float amount;
        if (k < SquashCompressPortion)
        {
            // 压下段：先快后慢
            float c = k / SquashCompressPortion;
            amount = deformDepth * Mathf.Sin(c * Mathf.PI * 0.5f);
        }
        else
        {
            // 回弹段：压扁 → 恢复 → 轻微过冲拉长 → 归位
            float c = (k - SquashCompressPortion) / (1f - SquashCompressPortion);
            amount = deformDepth * Mathf.Cos(c * Mathf.PI * 1.5f) * (1f - c);
        }

        Vector3 scale = deformBaseScale;
        scale.y *= 1f - amount;
        float widen = 1f + amount * (squashWiden - 1f);
        scale.x *= widen;
        scale.z *= widen;
        deformTarget.localScale = scale;

        if (k >= 1f)
        {
            deformTarget.localScale = deformBaseScale;
            deforming = false;
        }
    }

    /// <summary>
    /// 形变基准缩放：目标是“随档位放大”的部位时，把档位倍率一起算进去（两者叠加而不是互相覆盖）；
    /// 否则用 Awake 记录的初始缩放，避免把上一次形变的残留值当基准。
    /// </summary>
    private Vector3 ResolveDeformBaseScale(Transform target)
    {
        if (scaleTarget != null && target == scaleTarget)
            return baseScale * EnergyScaleMultiplier();
        if (target == squashTarget)
            return squashBaseScale;
        return target.localScale;
    }

    private float EnergyScaleMultiplier()
    {
        if (energyLevel >= 2)
            return level2ScaleMultiplier;
        if (energyLevel == 1)
            return level1ScaleMultiplier;
        return 1f;
    }

    private float ResolveSquashDepth()
    {
        if (levelSquashDepths == null || levelSquashDepths.Length == 0)
            return 0.3f;

        int index = Mathf.Clamp(energyLevel, 0, levelSquashDepths.Length - 1);
        return Mathf.Clamp(levelSquashDepths[index], 0f, 0.95f);
    }

    private void OnDisable()
    {
        animPlaying = false;
        deforming = false;
    }

    /// <summary>取动画速度倍率：优先按档位数组，matchPlayerAirTime 开启时按滞空时间自动换算。</summary>
    private float ResolveAnimationSpeed(float launchStrength, ThirdPersonController player)
    {
        // 自动对齐：弹起后滞空时间 = 2v/g（上升 + 下落），让动画刚好在玩家落地前播完
        if (matchPlayerAirTime && squashClip != null && player != null)
        {
            float gravity = Mathf.Abs(player.Gravity);
            if (gravity > 0.01f)
            {
                float airTime = 2f * Mathf.Max(0f, launchStrength) / gravity;
                if (airTime > 0.01f)
                    return Mathf.Max(0.01f, squashClip.length / airTime);
            }
        }

        if (levelAnimationSpeeds == null || levelAnimationSpeeds.Length == 0)
            return 1f;

        int index = Mathf.Clamp(energyLevel, 0, levelAnimationSpeeds.Length - 1);
        return Mathf.Max(0.01f, levelAnimationSpeeds[index]);
    }

    /// <summary>动画作用目标：优先 squashTarget，其次档位缩放目标，最后父级/自身。</summary>
    private Transform ResolveSquashTarget()
    {
        if (squashTarget != null)
            return squashTarget;
        if (scaleTarget != null)
            return scaleTarget;
        return transform.parent != null ? transform.parent : transform;
    }

    private Animator ResolveAnimator()
    {
        if (cachedAnimator != null)
            return cachedAnimator;

        // Animator 一般挂在“带动画的模型实例”上，而它不一定是本组件的子物体
        Transform target = ResolveSquashTarget();
        if (target != null)
            cachedAnimator = target.GetComponentInChildren<Animator>(true);
        if (cachedAnimator == null)
            cachedAnimator = GetComponentInChildren<Animator>(true);
        if (cachedAnimator == null && transform.parent != null)
            cachedAnimator = transform.parent.GetComponentInChildren<Animator>(true);

        return cachedAnimator;
    }

    private void WarnOnce(ref bool flag, string message)
    {
        if (flag)
            return;
        flag = true;
        Debug.LogWarning(message, this);
    }
}
