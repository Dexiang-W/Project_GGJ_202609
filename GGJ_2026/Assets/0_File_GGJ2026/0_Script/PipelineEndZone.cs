using UnityEngine;

/// <summary>
/// 流水线的终点 Volume：挂在终点处的触发区上，被流水线送过来的货物（PipelineItem）碰到它就会被“吃掉”（消失）。
///
/// 挂法：空物体 + Collider（勾选 Is Trigger，大小调成“入料口”的范围），再加本组件即可。
/// 建议把 PipelineSpawner 上的 End Point 放在这个 Volume 内部 —— 这样货物一定是先撞到 Volume 被吃掉，
/// 而不是先到达点位自己消失；也顺手避开了“高速穿过很薄的 Volume 漏检”的问题。
///
/// 可以做的：
///   · OnItemEntered 里接音效 / 特效 / 加能量 / 计数，做“送满 N 个才开门”的机关可以直接读 PassedCount；
///   · despawnDelaySeconds 填一点点，货物会先在入料口停住、过一会儿再消失，更像被机器吃进去。
///
/// 备注：
///   · 只有带 PipelineItem 的物体才被处理，角色或别的碰撞体碰到不会有反应（可选 requireTag 再按标签过滤）；
///   · 暂停菜单（Time.timeScale = 0）期间延迟计时也会停住，和流水线一起冻结。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))]
public class PipelineEndZone : MonoBehaviour
{
    [Header("判定")]
    [Tooltip("只吃掉带这个标签的货物（留空 = 不按标签过滤，只要带 PipelineItem 组件就算）")]
    [SerializeField] private string requireTag = "";

    [Header("消失方式")]
    [Tooltip("货物进入后多久消失（秒）：0 = 立刻消失；填一点点（例如 0.3）会先停在入料口，过一会儿再消失。")]
    [SerializeField] private float despawnDelaySeconds = 0f;

    [Header("事件（可选：接音效 / 特效 / 加能量 / 开门）")]
    [Tooltip("货物刚进入终点时触发一次（在它消失之前）")]
    public PipelineItemEvent OnItemEntered = new PipelineItemEvent();

    /// <summary>已经吃掉的货物数量（“送满 N 个才开门”这类机关可以直接读它）。</summary>
    public int PassedCount { get; private set; }

    private void Awake()
    {
        Collider col = GetComponent<Collider>();
        if (col != null && !col.isTrigger)
            Debug.LogWarning("[PipelineEndZone] 这个 Collider 没有勾 Is Trigger：货物撞上来只会被挡住，不会被吃掉。", this);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!string.IsNullOrEmpty(requireTag) && !other.CompareTag(requireTag))
            return;

        PipelineItem item = other.GetComponentInParent<PipelineItem>();
        if (item == null || !item.TryConsume())
            return;

        PassedCount++;
        OnItemEntered.Invoke(item);

        if (despawnDelaySeconds > 0f)
            item.DespawnAfter(despawnDelaySeconds);
        else
            item.Despawn();
    }

    /// <summary>把通过计数清零（例如按 R 回溯、重开机关之后）。</summary>
    public void ResetCount()
    {
        PassedCount = 0;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Collider col = GetComponent<Collider>();
        if (col == null)
            return;

        Gizmos.color = new Color(1f, 0.55f, 0.2f, 0.4f);

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
        else if (col is CapsuleCollider capsule)
        {
            Gizmos.DrawWireSphere(col.transform.TransformPoint(capsule.center), capsule.radius);
        }
    }
#endif
}
