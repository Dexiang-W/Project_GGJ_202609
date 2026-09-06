using UnityEngine;
// 如果你使用的是 StarterAssets 命名空间，可以取消下面这行的注释
// using StarterAssets;

public class RippleParticleController : MonoBehaviour
{
    [Header("引用设置")]
    [Tooltip("拖入玩家身上的 ThirdPersonController 或 FirstPersonController")]
    public MonoBehaviour playerController;

    [Tooltip("拖入你的涟漪/脚印 Particle System")]
    public ParticleSystem rippleParticle;

    // 反射/通用获取 Grounded 字段
    private System.Reflection.FieldInfo groundedField;

    private void Start()
    {
        if (playerController != null)
        {
            // 获取 ThirdPersonController / FirstPersonController 里的 Grounded 字段
            groundedField = playerController.GetType().GetField("Grounded");
        }
    }

    private void Update()
    {
        if (playerController == null || rippleParticle == null) return;

        // 获取当前是否着地
        bool isGrounded = false;
        if (groundedField != null)
        {
            isGrounded = (bool)groundedField.GetValue(playerController);
        }

        // 获取粒子的发射模块 (Emission Module)
        var emission = rippleParticle.emission;

        // 着地时开启发射，空中（跳跃/下落）时关闭发射
        if (isGrounded)
        {
            if (!emission.enabled) emission.enabled = true;
        }
        else
        {
            if (emission.enabled) emission.enabled = false;
        }
    }
}