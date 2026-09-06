using StarterAssets;
using UnityEngine;

/// <summary>
/// 能量弹跳板（玩家进入弹跳区域后把其竖直弹起；力度随玩家当前能量格数分档）。
///
/// 用法：给物体加 BoxCollider（Is Trigger 勾选，放在地面/平台上沿），再加本组件。
/// Inspector 中可分别调节：
///   · 0 格能量：小弹（或无弹）；
///   · 1 格能量：中档弹力；
///   · 2 格及以上：高档弹力。
/// 需要玩家带能量跑图时，建议把能量拾取物（EnergyPickup）放在附近必经之路。
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
public class BouncePad : MonoBehaviour
{
    [Header("触碰对象")]
    [Tooltip("玩家的 Tag；用于过滤目标。留空表示只按角色控制器识别")]
    [SerializeField] private string playerTag = "Player";

    [Header("三档弹力（竖直向上速度，单位 m/s，可自己调）")]
    [Tooltip("玩家 0 格能量时的弹力；0 = 不弹")]
    [SerializeField] private float zeroEnergyLaunch = 3f;
    [Tooltip("玩家 1 格能量时的弹力（中档）")]
    [SerializeField] private float oneEnergyLaunch = 7f;
    [Tooltip("玩家 2 格及以上能量时的弹力（高档）")]
    [SerializeField] private float twoPlusEnergyLaunch = 12f;

    [Header("防重复触发")]
    [Tooltip("弹过一次后，多少秒内不再重复触发（防止玩家悬空停在板内时反复被弹）")]
    [SerializeField] private float retriggerCooldown = 0.8f;

    private BoxCollider zoneCollider;
    private float lastLaunchTime = -100f;

    // 复用缓冲区，避免每帧分配 GC
    private static readonly Collider[] OverlapBuffer = new Collider[16];

    private void Awake()
    {
        zoneCollider = GetComponent<BoxCollider>();
        if (zoneCollider == null)
        {
            enabled = false;
            Debug.LogError($"[BouncePad] {name} 缺少 BoxCollider，弹跳板已停用。", this);
            return;
        }
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

        GameplayHUD.EnsureCreated();
        int energy = GameplayHUD.Instance.CurrentEnergy;

        float strength;
        if (energy >= 2)
            strength = twoPlusEnergyLaunch;
        else if (energy == 1)
            strength = oneEnergyLaunch;
        else
            strength = zeroEnergyLaunch;

        if (strength <= 0f)
            return;

        player.LaunchUp(strength);
        lastLaunchTime = Time.time;
    }
}
