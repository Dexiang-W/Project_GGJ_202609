using StarterAssets;
using UnityEngine;

/// <summary>
/// 能量弹跳板（触碰后把玩家弹起；力度随玩家当前能量格数分档）。
///
/// 用法：给空物体加 BoxCollider（Is Trigger 勾选，放在地面/平台上沿），再加本组件。
/// Inspector 中可分别调节：
///   · 0 格能量：小弹（或无弹）；
///   · 1 格能量：中档弹力；
///   · 2 格及以上：高档弹力。
/// 需要玩家带能量跑图时，建议把能量拾取物（EnergyPickup）放在附近必经之路。
/// </summary>
[DisallowMultipleComponent]
public class BouncePad : MonoBehaviour
{
    [Header("触碰对象")]
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

    private float lastLaunchTime = -100f;

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(playerTag))
            return;

        if (Time.time - lastLaunchTime < retriggerCooldown)
            return;

        ThirdPersonController player = ThirdPersonController.Instance;
        if (player == null)
            player = other.GetComponentInParent<ThirdPersonController>();

        if (player == null)
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
