using UnityEngine;

/// <summary>
/// 关卡“安全点（检查点）触发区域”。美术/关卡设计把它摆到想让玩家“存档”的位置，
/// 身上需要有 Trigger 碰撞盒（可在 Inspector 右键本组件 → “添加 Trigger 碰撞盒”一键补上）。
///
/// 作用：玩家走进区域 → 自动拍一张“本关快照”（当前植物的成长状态 / 能量拾取物 / 机关动画
/// + 玩家当时所在位置 + 当前能量），之后玩家在任意关卡按 R 即可在 1 秒内瞬移回这个安全点，
/// 并把本关世界还原成记录那一刻的样子。
/// </summary>
[DisallowMultipleComponent]
public class GGJSafePoint : MonoBehaviour
{
    [Header("触发")]
    [Tooltip("玩家身上的 Tag；用于过滤由哪个物体触发记录")]
    [SerializeField] private string playerTag = "Player";
    [Tooltip("只记录一次：玩家首次进入后不再刷新该安全点（适合单向的关卡）")]
    [SerializeField] private bool recordOnce = false;

    [Header("调试")]
    [Tooltip("仅用于日志/Inspector 区分，不影响玩法")]
    [SerializeField] private string pointName = "安全点";

    private bool used;

    private void Start()
    {
        // 提醒没有配触发碰撞盒的误摆（不自动改碰撞盒，避免破坏美术已有的造型碰撞）
        Collider col = GetComponent<Collider>();
        if (col == null || !col.isTrigger)
        {
            Debug.LogWarning(
                "[GGJSafePoint] 请在安全点物体上配置一个 Trigger 碰撞盒（Box/Sphere/Capsule 均可），" +
                "否则玩家走过不会触发记录。可在 Inspector 右键本组件 → “添加 Trigger 碰撞盒(Box)” 一键补上。",
                this);
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (used)
            return;
        if (other == null)
            return;
        if (!string.IsNullOrEmpty(playerTag) && !other.CompareTag(playerTag))
            return;

        if (GGJLevelResetManager.Instance != null &&
            GGJLevelResetManager.Instance.TryRecordCheckpoint(this))
        {
            if (recordOnce)
                used = true;

            Debug.Log($"[GGJSafePoint] 已记录安全点「{pointName}」@ {transform.position}", this);
        }
    }

    /// <summary>回到安全点时的落地位置（记录的是玩家进入区域的这一刻）。</summary>
    public Vector3 GetRespawnPosition()
    {
        return transform.position;
    }

#if UNITY_EDITOR
    [ContextMenu("添加 Trigger 碰撞盒(Box)")]
    private void AutoAddTriggerBox()
    {
        BoxCollider box = GetComponent<BoxCollider>();
        if (box == null)
            box = gameObject.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(3f, 3f, 3f);

        UnityEditor.EditorUtility.SetDirty(gameObject);
        Debug.Log("[GGJSafePoint] 已添加 Box Trigger 碰撞盒（可自行调整大小/位置）。", this);
    }

    private void OnDrawGizmos()
    {
        DrawGizmo(false);
    }

    private void OnDrawGizmosSelected()
    {
        DrawGizmo(true);
    }

    private void DrawGizmo(bool selected)
    {
        Collider col = GetComponent<Collider>();
        if (col == null)
            return;

        Color color = selected
            ? new Color(0.2f, 1f, 0.55f, 1f)
            : new Color(0.2f, 1f, 0.55f, 0.4f);
        Gizmos.color = color;

        if (col is BoxCollider box)
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(box.center, box.size);
            if (selected)
            {
                color.a = 0.12f;
                Gizmos.color = color;
                Gizmos.DrawCube(box.center, box.size);
            }
            Gizmos.matrix = Matrix4x4.identity;
        }
        else if (col is SphereCollider sphere)
        {
            Gizmos.DrawWireSphere(transform.TransformPoint(sphere.center), sphere.radius);
        }
        else if (col is CapsuleCollider capsule)
        {
            Vector3 center = transform.TransformPoint(capsule.center);
            float half = Mathf.Max(0.05f, capsule.height * 0.5f);
            Vector3 dir = Vector3.up;
            if (capsule.direction == 1) dir = Vector3.forward;
            else if (capsule.direction == 2) dir = Vector3.right;
            dir = transform.TransformDirection(dir).normalized;

            Vector3 a = center - dir * half;
            Vector3 b = center + dir * half;
            Gizmos.DrawWireSphere(a, capsule.radius);
            Gizmos.DrawWireSphere(b, capsule.radius);
            Gizmos.DrawLine(a + Vector3.right * capsule.radius, b + Vector3.right * capsule.radius);
            Gizmos.DrawLine(a - Vector3.right * capsule.radius, b - Vector3.right * capsule.radius);
            Gizmos.DrawLine(a + Vector3.up * capsule.radius, b + Vector3.up * capsule.radius);
            Gizmos.DrawLine(a - Vector3.up * capsule.radius, b - Vector3.up * capsule.radius);
            Gizmos.DrawLine(a + Vector3.forward * capsule.radius, b + Vector3.forward * capsule.radius);
            Gizmos.DrawLine(a - Vector3.forward * capsule.radius, b - Vector3.forward * capsule.radius);
        }

        // 顶部小旗子样式的指示点
        Gizmos.color = new Color(0.2f, 1f, 0.55f, selected ? 1f : 0.7f);
        Gizmos.DrawSphere(transform.position + Vector3.up * 0.35f, 0.18f);
    }
#endif
}
