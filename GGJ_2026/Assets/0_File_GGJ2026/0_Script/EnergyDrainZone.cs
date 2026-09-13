using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// “能量清零”触发区域（Volume）：玩家经过时，自身能量立即清零（左上角能量格全部变回浅绿）。
///
/// 挂法：给空物体加 Collider（推荐 BoxCollider，勾选 Is Trigger，大小调成区域大小），再加本组件；
/// 区域摆在哪、多大，全在场景里调，不需要写代码。
///
/// 行为细节：
///   · 只在“玩家进入区域的那一刻”清零一次；站在区域里不会持续扣（本来也没得扣）；
///   · 默认每次经过都清零 —— 离开后再捡了能量、又走回来，照样会被清空；
///     勾选 oneShot 则整个区域只清一次（第一次真的清空之后彻底失效）；
///   · 玩家进来时本来就是 0 能量的话什么都不做（也不会触发 OnEnergyDrained，避免“空清一次”还响音效）；
///   · 真正清掉能量时触发 OnEnergyDrained，可用来接音效 / 特效 / 屏幕提示
///     （提示文字可以直接用 GameplayHUD.Instance.ShowMessage("……")）。
///
/// 与 R 键回溯（GGJLevelResetManager）的关系：
///   能量本来就不随回溯回滚（安全点快照里不记录能量），所以被清零后按 R 回到安全点，能量依旧是 0，
///   需要重新去捡能量球补回来 —— 与 InteractableObject 消耗能量的处理一致。
/// </summary>
[DisallowMultipleComponent]
public class EnergyDrainZone : MonoBehaviour
{
    [Header("触碰对象（默认标签 Player，与角色一致）")]
    [SerializeField] private string playerTag = "Player";

    [Header("清零设置")]
    [Tooltip("勾选：同一个区域只清零一次（第一次把能量清空之后本区域失效）。\n" +
             "不勾（默认）：每次进入都清零 —— 离开后捡了能量再走回来，照样会被清空。")]
    [SerializeField] private bool oneShot = false;

    [Header("事件（可选，用来接音效 / 特效 / 提示）")]
    [Tooltip("真正把能量清掉的那一刻触发一次（玩家进来时本来就是 0 能量则不触发）")]
    public UnityEvent OnEnergyDrained = new UnityEvent();

    /// <summary>oneShot 模式下是否已经真正清空过一次。</summary>
    private bool drained;

    private void Awake()
    {
        Collider col = GetComponent<Collider>();
        if (col == null)
        {
            Debug.LogWarning($"[EnergyDrainZone] {name} 上没有 Collider，玩家不会触发这个区域；" +
                             "请加一个 BoxCollider 并勾选 Is Trigger。", this);
        }
        else if (!col.isTrigger)
        {
            Debug.LogWarning($"[EnergyDrainZone] {name} 上的 Collider 没有勾选 Is Trigger，" +
                             "玩家会撞在上面而不是穿过去；请勾选 Is Trigger。", this);
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(playerTag))
            return;

        if (oneShot && drained)
            return;

        GameplayHUD.EnsureCreated();
        GameplayHUD hud = GameplayHUD.Instance;
        if (hud == null)
            return;

        // 本来就没能量：什么都不做（也不触发事件，免得“空手路过”还响一声）
        if (hud.CurrentEnergy <= 0)
            return;

        hud.SetEnergy(0);

        // oneShot 在“真的清空过一次”之后才失效，这样“空手路过”不会把这次机会用掉
        drained = true;
        OnEnergyDrained?.Invoke();
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Collider col = GetComponent<Collider>();
        if (col == null)
            return;

        // 紫色线框提示清零区域范围
        Gizmos.color = new Color(0.45f, 0.1f, 0.9f, 0.6f);

        if (col is BoxCollider box)
        {
            Matrix4x4 old = Gizmos.matrix;
            Gizmos.matrix = col.transform.localToWorldMatrix;
            Gizmos.DrawWireCube(box.center, box.size);
            Gizmos.matrix = old;
        }
        else if (col is SphereCollider sphere)
        {
            Gizmos.DrawWireSphere(col.transform.TransformPoint(sphere.center), sphere.radius);
        }
    }
#endif
}
