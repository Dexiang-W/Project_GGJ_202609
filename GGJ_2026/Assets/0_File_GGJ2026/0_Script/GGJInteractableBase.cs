using UnityEngine;

/// <summary>
/// 可交互拾取物基类：
///  · 自动复用 / 挂载 InteractableObject（负责“靠近描边高亮 + 按 E 触发”）；
///  · 物体被 InteractableObject 高亮时，通过 GameplayHUD 在屏幕下方显示“按 E …”提示；
///  · 子类只需要实现 <see cref="OnInteracted"/>，拿完调用 <see cref="ConsumeAndHide"/> 收起来。
/// </summary>
[DisallowMultipleComponent]
public abstract class GGJInteractableBase : MonoBehaviour
{
    [Header("交互提示")]
    [Tooltip("玩家靠近（物体被描边高亮）时，屏幕下方显示的提示文字")]
    [SerializeField] protected string promptText = "按 E 交互";

    [Header("高亮 / 交互组件")]
    [Tooltip("负责靠近描边高亮与按 E 触发的组件；留空会自动挂一个")]
    [SerializeField] protected InteractableObject interactable;

    protected InteractableObject Interactable => interactable;

    /// <summary>是否已经完成（完成后不再显示提示、也不再响应 E）。</summary>
    protected virtual bool IsDone => false;

    protected virtual void Awake()
    {
        if (interactable == null)
            interactable = GetComponent<InteractableObject>();
        if (interactable == null)
            interactable = gameObject.AddComponent<InteractableObject>();
        if (interactable != null)
            interactable.OnInteract.AddListener(HandleInteract);
    }

    protected virtual void Update()
    {
        if (!GameplayHUD.Exists)
            return;

        GameplayHUD hud = GameplayHUD.Instance;
        bool show = interactable != null && interactable.IsHighlighted && !IsDone && !GGJBookSystem.IsUiOpen;

        if (show)
            hud.ShowInteractPrompt(this, promptText);
        else
            hud.HideInteractPrompt(this);
    }

    protected virtual void OnDisable()
    {
        if (GameplayHUD.Exists)
            GameplayHUD.Instance.HideInteractPrompt(this);
    }

    private void HandleInteract()
    {
        if (IsDone)
            return;

        OnInteracted();
    }

    /// <summary>按 E 之后真正要做的事。</summary>
    protected abstract void OnInteracted();

    /// <summary>
    /// 收进背包式的处理：关掉高亮交互（consumed），并可选隐藏物体（默认隐藏挂脚本的物体本身）。
    /// </summary>
    protected void ConsumeAndHide(GameObject objectToHide, bool hide)
    {
        if (interactable != null)
            interactable.SetConsumed(true);

        if (GameplayHUD.Exists)
            GameplayHUD.Instance.HideInteractPrompt(this);

        if (!hide)
            return;

        GameObject target = objectToHide != null ? objectToHide : gameObject;
        if (target != null)
            target.SetActive(false);
    }

    protected static void PlayPickupSound()
    {
        if (AudioManager.Instance != null)
            AudioManager.Instance.PlayPickup();
    }
}
