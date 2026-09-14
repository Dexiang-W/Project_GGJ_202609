using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 流水线生成器：放在起点，按“生成间隔”不断生成 prefab；
/// 生成出来的货物会以“移动速度”朝终点点位直线前进，碰到终点 Volume（PipelineEndZone）后消失。
/// 整条线的样子就是：起点 → 匀速流动的货物 → 终点入料口把它们吃掉。
///
/// 场景怎么摆（3 步）：
///   1. 起点：空物体挂上本组件（本物体自己就是生成点），把 prefab / End Point / 生成间隔 / 移动速度填好；
///   2. 终点：在终点位置放个空物体（拖到本组件的 End Point），给它加 BoxCollider（勾 Is Trigger）
///      + PipelineEndZone，尺寸就是入料口的范围；
///   3. 货物 prefab：挂上 PipelineItem（本组件生成时会自动把“终点 + 速度”注入给它）。
///      想让玩家被货物碰到就复活：再给 prefab 挂 PipelineHazard，并在本组件的「危险货物复活点」里拖一个
///      场景点位 —— prefab 资源不能引用场景物体，所以点位只能配在这里，生成时由本组件注入给每件货物。
///
/// 想调的两件事：
///   · 生成间隔 spawnIntervalSeconds（还能加随机抖动 intervalJitterSeconds，让节奏不那么机械）；
///   · 移动速度 moveSpeed —— 只改这一处，之后生成的所有货物都按它走。
///
/// 其它可选：最多同时存在几个（maxAliveCount）、开局是否立刻出一个（spawnImmediatelyOnStart）、
/// 要不要自动开始（autoStart）。想做“通电后才运转”的机关：取消 autoStart，需要时调用 StartPipeline()。
///
/// 备注：
///   · 暂停菜单（Time.timeScale = 0）会让生成和移动一起冻结，无需额外处理；
///   · 按 R 回溯（GGJLevelResetManager）只还原场景里原有的物体，已经在流水线上的货物不受影响、会继续走完；
///     想在回溯时清场就自己调用 DespawnAll()。
/// </summary>
[DisallowMultipleComponent]
public class PipelineSpawner : MonoBehaviour
{
    [Header("生成内容")]
    [Tooltip("要生成的 prefab（记得在 prefab 上挂 PipelineItem，否则生成出来不会动）")]
    [SerializeField] private GameObject prefab;

    [Tooltip("生成的货物挂在哪个父物体下（留空 = 直接放场景根目录）。只影响 Hierarchy 整洁，不影响运行。")]
    [SerializeField] private Transform itemParent;

    [Header("点位")]
    [Tooltip("生成点（留空 = 用本物体自己的位置和朝向）")]
    [SerializeField] private Transform spawnPoint;

    [Tooltip("终点点位：货物朝它走。建议把它放在终点 Volume 的内部，让货物是被 Volume 吃掉而不是自己到点消失。")]
    [SerializeField] private Transform endPoint;

    [Header("危险货物（可选）")]
    [Tooltip("生成的 prefab 上如果挂了 PipelineHazard（玩家被这件货物碰到就复活），在这里拖一个场景里的点位" +
             "（空物体 / SpawnPoint / 安全点都可以）当作复活落点。\n" +
             "prefab 资源不能引用场景物体，所以点位必须配在这里，生成时自动注入给每一件货物。\n" +
             "留空 = 货物上的 PipelineHazard 自己去找场景里名为 SpawnPoint 的物体。")]
    [SerializeField] private Transform hazardRespawnPoint;

    [Header("生成节奏")]
    [Tooltip("生成间隔（秒）：每过这么久生成一个")]
    [SerializeField] private float spawnIntervalSeconds = 1.5f;

    [Tooltip("间隔随机抖动（秒）：实际间隔 = 间隔 ± 这个值，让节奏不那么机械；0 = 固定间隔")]
    [SerializeField] private float intervalJitterSeconds = 0f;

    [Tooltip("开始运行时是否先立刻生成一个（不勾 = 等满一个间隔才出第一个）")]
    [SerializeField] private bool spawnImmediatelyOnStart = true;

    [Tooltip("场上最多同时存在几个货物（0 = 不限）。到上限的这一拍先不出，下个间隔再试。")]
    [SerializeField] private int maxAliveCount = 0;

    [Tooltip("开始后自动运行（不勾 = 用 StartPipeline() 手动开，适合“通电后才运转”的机关）")]
    [SerializeField] private bool autoStart = true;

    [Header("移动（生成时注入给货物）")]
    [Tooltip("货物的移动速度（米/秒）。想调快慢改这里就行。")]
    [SerializeField] private float moveSpeed = 3f;

    [Header("事件（可选：接音效 / 特效 / 计数）")]
    [Tooltip("每生成一个货物触发一次")]
    public PipelineItemEvent OnItemSpawned = new PipelineItemEvent();

    [Tooltip("每个货物消失时触发一次（被终点吃掉 / 到达终点 / 超时）")]
    public PipelineItemEvent OnItemFinished = new PipelineItemEvent();

    /// <summary>是否正在持续生成。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>场上还活着的货物数量（做“最多同时几个”“送满 N 个”这类逻辑时读）。</summary>
    public int AliveCount
    {
        get
        {
            PruneAlive();
            return alive.Count;
        }
    }

    private readonly List<PipelineItem> alive = new List<PipelineItem>();
    private float timer;
    private float nextInterval;

    // ---------------------------------------------------------------- 生命周期

    private void Start()
    {
        if (autoStart)
            StartPipeline();
    }

    private void Update()
    {
        if (!IsRunning)
            return;

        PruneAlive();

        // 暂停菜单把 Time.timeScale 设为 0，这里的 deltaTime 也就是 0，生成节奏会一起冻结
        float dt = Time.deltaTime;
        if (dt <= 0f)
            return;

        timer += dt;
        if (timer < nextInterval)
            return;

        timer = 0f;
        nextInterval = RollInterval();
        TrySpawnByInterval();
    }

    private void OnDestroy()
    {
        // 场景卸载 / 切关卡时不要在这里销毁货物或触发事件，交给 Unity 直接清掉即可
        alive.Clear();
    }

    // ---------------------------------------------------------------- 对外接口

    /// <summary>开始持续生成（已经在跑就不重复开）。做“通电后运转”的机关时调用。</summary>
    public void StartPipeline()
    {
        if (IsRunning)
            return;

        if (prefab == null)
        {
            Debug.LogWarning("[PipelineSpawner] 没有设置要生成的 prefab，无法开始。", this);
            return;
        }

        if (endPoint == null)
        {
            Debug.LogWarning("[PipelineSpawner] 没有设置 End Point（终点点位），货物不知道往哪走，无法开始。", this);
            return;
        }

        IsRunning = true;
        timer = 0f;
        nextInterval = RollInterval();

        if (spawnImmediatelyOnStart)
            Spawn();
    }

    /// <summary>停止生成（已经在场上的货物会继续走完）。</summary>
    public void StopPipeline()
    {
        IsRunning = false;
    }

    /// <summary>清场：把场上所有货物立刻销毁（照常触发 OnItemFinished，方便接音效 / 计数）。</summary>
    public void DespawnAll()
    {
        PruneAlive();

        for (int i = alive.Count - 1; i >= 0; i--)
        {
            if (alive[i] != null)
                alive[i].Despawn();
        }

        alive.Clear();
    }

    /// <summary>
    /// 生成一个货物（不看间隔，直接出）：做“手动投放”的机关或调试时用。
    /// 返回生成的货物；prefab / End Point 没配好时返回 null 并在 Console 里提示。
    /// </summary>
    public PipelineItem Spawn()
    {
        if (prefab == null)
        {
            Debug.LogWarning("[PipelineSpawner] 没有设置要生成的 prefab。", this);
            return null;
        }

        if (endPoint == null)
        {
            Debug.LogWarning("[PipelineSpawner] 没有设置 End Point（终点点位），货物不知道往哪走。", this);
            return null;
        }

        Transform from = spawnPoint != null ? spawnPoint : transform;
        GameObject go = Instantiate(prefab, from.position, from.rotation, itemParent);

        PipelineItem item = go.GetComponentInChildren<PipelineItem>();
        if (item == null)
        {
            Debug.LogWarning("[PipelineSpawner] 生成的 prefab 上没有 PipelineItem，它不会移动，已把它销毁。请给 prefab 挂上 PipelineItem。", go);
            Destroy(go);
            return null;
        }

        item.Despawned += HandleItemDespawned;
        item.Init(endPoint.position, moveSpeed);

        // 危险货物：把「复活点」注入给这件货物（prefab 挂了 PipelineHazard 才需要）
        if (hazardRespawnPoint != null)
        {
            PipelineHazard hazard = go.GetComponentInChildren<PipelineHazard>(true);
            if (hazard != null)
                hazard.SetRespawnPoint(hazardRespawnPoint);
        }

        alive.Add(item);

        OnItemSpawned.Invoke(item);
        return item;
    }

    // ---------------------------------------------------------------- 内部

    private void TrySpawnByInterval()
    {
        // 场上到上限就先跳过这一拍，下个间隔再试
        if (maxAliveCount > 0 && alive.Count >= maxAliveCount)
            return;

        Spawn();
    }

    private void HandleItemDespawned(PipelineItem item)
    {
        alive.Remove(item);
        OnItemFinished.Invoke(item);
    }

    /// <summary>把被别处销毁（没走 Despawn）的货物从列表里剔掉，避免计数和上限判断被卡住。</summary>
    private void PruneAlive()
    {
        for (int i = alive.Count - 1; i >= 0; i--)
        {
            if (alive[i] == null)
                alive.RemoveAt(i);
        }
    }

    private float RollInterval()
    {
        float interval = Mathf.Max(0.01f, spawnIntervalSeconds);

        if (intervalJitterSeconds > 0f)
            interval = Mathf.Max(0.01f, interval + Random.Range(-intervalJitterSeconds, intervalJitterSeconds));

        return interval;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Vector3 from = spawnPoint != null ? spawnPoint.position : transform.position;

        Gizmos.color = new Color(0.4f, 1f, 0.5f);
        Gizmos.DrawWireSphere(from, 0.4f);

        // 危险货物的复活点（配了才画）
        if (hazardRespawnPoint != null)
        {
            Gizmos.color = new Color(0.2f, 1f, 0.55f);
            Gizmos.DrawWireSphere(hazardRespawnPoint.position, 0.4f);
        }

        if (endPoint == null)
            return;

        Vector3 to = endPoint.position;

        Gizmos.color = new Color(1f, 0.55f, 0.2f);
        Gizmos.DrawWireSphere(to, 0.4f);

        // 路径 + 中点箭头，一眼看出货物流向
        Vector3 delta = to - from;
        Gizmos.color = new Color(1f, 0.9f, 0.3f);
        Gizmos.DrawLine(from, to);

        if (delta.sqrMagnitude < 1e-6f)
            return;

        Vector3 direction = delta.normalized;
        Vector3 side = Vector3.Cross(Vector3.up, direction);
        if (side.sqrMagnitude < 1e-6f)
            side = Vector3.right;
        else
            side = side.normalized;

        float size = Mathf.Clamp(delta.magnitude * 0.08f, 0.2f, 1f);
        Vector3 middle = from + delta * 0.5f;
        Gizmos.DrawLine(middle, middle - direction * size + side * size * 0.5f);
        Gizmos.DrawLine(middle, middle - direction * size - side * size * 0.5f);
    }
#endif
}
