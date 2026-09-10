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

    private int energyLevel;
    private Vector3 baseScale = Vector3.one;
    private Coroutine scaleRoutine;

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

        float multiplier = energyLevel >= 2 ? level2ScaleMultiplier
                         : energyLevel == 1 ? level1ScaleMultiplier
                         : 1f;
        Vector3 targetScale = baseScale * multiplier;

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
}
