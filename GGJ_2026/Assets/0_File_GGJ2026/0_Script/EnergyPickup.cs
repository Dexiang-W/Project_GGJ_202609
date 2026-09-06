using UnityEngine;

/// <summary>
/// 能量拾取物（触碰后能量 +1 格，浅绿圆点变亮绿）。
/// 用法：给空物体加 BoxCollider（Is Trigger 勾选，大小即拾取范围），再加本组件即可；
/// 也可以直接挂到场景里某个“能量水晶 / 能量柱”样式的物体上（该物本身需是 Trigger 或另配一个 Trigger 子物体）。
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

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(playerTag))
            return;

        GameplayHUD.EnsureCreated();
        GameplayHUD.Instance.AddEnergy(energyAmount);

        // 拾取音效：SFX_Energy_Pickup（GameplayHUD.EnsureCreated 会同时确保音频系统就绪）
        if (AudioManager.Instance != null)
            AudioManager.Instance.PlayPickup();

        if (oneShot)
        {
            // 吃掉：整个物体（连同子物体视觉）隐藏，不会再触发（需要“消失动画”可自行扩展）
            gameObject.SetActive(false);
        }
    }
}
