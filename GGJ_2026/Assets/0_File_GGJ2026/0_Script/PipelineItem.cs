using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// 带“货物”参数的 UnityEvent：Inspector 里绑事件时可以把碰到的那件货物传进去。
/// （UnityEvent&lt;T&gt; 想被 Inspector 序列化，需要一个这样具体的子类。）
/// </summary>
[Serializable]
public class PipelineItemEvent : UnityEvent<PipelineItem> { }

/// <summary>
/// 流水线上的“货物”：挂在要被传送的 prefab 上。
///
/// 行为：
///   · 由 PipelineSpawner 生成时，生成器会调用 Init 把“终点 + 速度”注入进来，之后它匀速直线朝终点走；
///   · 走到终点、或被终点 Volume（PipelineEndZone）碰到，就消失；
///   · 超过 maxLifetimeSeconds 还没走完也会消失（漏接保护），避免漏接的货物永远留在场景里。
///
/// 挂法：挂到 prefab 的根物体上，并且 prefab 上要有 Collider（终点 Volume 靠它检测到货物）。
/// 没有 Rigidbody 时本组件会自动补一个 Kinematic 的 —— Unity 的触发事件要求碰撞双方至少一方有 Rigidbody，
/// 少了它终点 Volume 收不到消息（详见 SetUpPhysics）。
///
/// 小提示：
///   · 速度、终点一般不用在这里填：用 PipelineSpawner 生成时以生成器面板上的值为准（一处调整、效果统一）；
///     这里面板上的值是“不接生成器、单独摆一个在场景里”时用的默认值。
///   · 暂停菜单（Time.timeScale = 0）会让它一起停住，不需要额外处理。
/// </summary>
[DisallowMultipleComponent]
public class PipelineItem : MonoBehaviour
{
    [Header("移动（单独摆放时用；用生成器生成时以生成器上的值为准）")]
    [Tooltip("移动速度（米/秒）。想调快慢请改 PipelineSpawner 面板上的“移动速度”。")]
    [SerializeField] private float moveSpeed = 3f;

    [Tooltip("是否让货物朝向移动方向（箭矢 / 子弹 / 鱼这类勾上；货物箱保持 prefab 原朝向就不勾）")]
    [SerializeField] private bool faceMoveDirection = false;

    [Tooltip("转向速度（度/秒），只在勾选“朝向移动方向”时生效。0 = 瞬间转到位。")]
    [SerializeField] private float turnSpeedDegrees = 720f;

    [Tooltip("（可选）不想用生成器、只想让场景里这一个货物自己走一趟时：把终点拖进来，运行后它会自己出发。\n" +
             "被 PipelineSpawner 生成的情况下这个字段会被生成器的 End Point 覆盖，随便填没影响。")]
    [SerializeField] private Transform standaloneTarget;

    [Header("到达判定（终点 Volume 没接住时的兜底）")]
    [Tooltip("距离终点多少米以内算“到达”")]
    [SerializeField] private float arriveRadius = 0.3f;

    [Tooltip("到达终点后是否自己消失。终点 Volume 会吃掉货物，这里是“没摆 Volume / 漏接”时的保险。")]
    [SerializeField] private bool destroyOnArrival = true;

    [Tooltip("最长存活时间（秒）：0 = 不限。填正数时超时也会消失，防止漏接的货物永远留在场上。")]
    [SerializeField] private float maxLifetimeSeconds = 0f;

    [Header("物理")]
    [Tooltip("自动补 / 改成 Kinematic Rigidbody（推荐勾选）：\n" +
             "· 触发事件要求碰撞双方至少一方有 Rigidbody，没有的话终点 Volume 检测不到货物；\n" +
             "· 本组件用 transform 匀速推进，动态刚体会和物理打架（抖动 / 被重力拽下去）；\n" +
             "· 顺带打开 Speculative Continuous，减少高速穿过体积较薄的 Volume 时漏检。\n" +
             "取消勾选 = 不动 prefab 上原有的 Rigidbody（那就要自己保证别和物理打架）。")]
    [SerializeField] private bool makeRigidbodyKinematic = true;

    [Header("事件（可选：接音效 / 特效 / 计数）")]
    [Tooltip("到达终点（进入 arriveRadius）时触发一次")]
    public UnityEvent OnReachedEnd = new UnityEvent();

    /// <summary>货物消失（到达 / 超时 / 被 Volume 吃掉 / 手动）时回调，PipelineSpawner 用它维护“场上还剩几个”。</summary>
    public event Action<PipelineItem> Despawned;

    /// <summary>当前要去的终点（世界坐标），只用于调试 / 画 Gizmo。</summary>
    public Vector3 TargetPosition { get; private set; }

    private float currentSpeed;
    private Vector3 moveDirection = Vector3.forward;

    private bool launched;     // 已经拿到终点、正在移动
    private bool arrived;      // 已到达终点（destroyOnArrival = false 时留在原地）
    private bool consumed;     // 已被终点 Volume 吃掉
    private bool despawning;   // 已经在消失流程里
    private float aliveTime;

    // ---------------------------------------------------------------- 生命周期

    private void Awake()
    {
        currentSpeed = Mathf.Max(0f, moveSpeed);
        SetUpPhysics();

        // 单独摆在场景里用的情况：自己朝 standaloneTarget 出发
        if (standaloneTarget != null)
            Init(standaloneTarget.position, moveSpeed);
    }

    /// <summary>
    /// 由 PipelineSpawner 生成时调用：注入终点与速度，开始移动。
    /// 也用于把一个场景里已存在的货物重新“发射”出去。
    /// </summary>
    public void Init(Vector3 targetPosition, float speed)
    {
        TargetPosition = targetPosition;
        currentSpeed = Mathf.Max(0f, speed);
        launched = true;
        arrived = false;
        aliveTime = 0f;

        Vector3 delta = targetPosition - transform.position;
        if (delta.sqrMagnitude > 1e-6f)
            moveDirection = delta.normalized;

        // 出生就摆好朝向，避免“先原地转头再往前走”的突兀感
        if (faceMoveDirection)
            transform.rotation = Quaternion.LookRotation(moveDirection, Vector3.up);
    }

    private void Update()
    {
        if (!launched || arrived || consumed || despawning)
            return;

        // 暂停菜单把 Time.timeScale 设为 0，这里的 deltaTime 也就是 0，流水线会跟游戏一起冻结
        float dt = Time.deltaTime;
        if (dt <= 0f)
            return;

        if (maxLifetimeSeconds > 0f)
        {
            aliveTime += dt;
            if (aliveTime >= maxLifetimeSeconds)
            {
                Despawn();
                return;
            }
        }

        Vector3 position = transform.position;
        Vector3 delta = TargetPosition - position;
        float distance = delta.magnitude;
        if (distance > 1e-4f)
            moveDirection = delta / distance;

        float step = currentSpeed * dt;
        if (step >= distance || distance <= Mathf.Max(0f, arriveRadius))
        {
            // 到达终点：贴到点位上（点位一般就放在终点 Volume 里面，顺手会被 Volume 吃掉）
            if (destroyOnArrival)
                transform.position = TargetPosition;

            arrived = true;
            OnReachedEnd.Invoke();

            if (destroyOnArrival)
                Despawn();

            return;
        }

        transform.position = position + moveDirection * step;
        TurnTowardsMoveDirection(dt);
    }

    // ---------------------------------------------------------------- 对外接口

    /// <summary>
    /// 被终点 Volume“吃掉”：标记为已消耗并停下（同一个货物有多个 Collider、被重复触发时只算一次）。
    /// </summary>
    /// <returns>true = 这次才算吃掉（调用方继续走事件 / 计数）；false = 之前已经处理过了。</returns>
    public bool TryConsume()
    {
        if (consumed || despawning)
            return false;

        consumed = true;
        launched = false;   // 停下，别继续往 Volume 外面飘走
        return true;
    }

    /// <summary>让货物消失（终点 Volume、生成器清场、手动都走这里）。会先通知生成器，再销毁物体。</summary>
    public void Despawn()
    {
        if (despawning)
            return;

        despawning = true;

        Action<PipelineItem> handler = Despawned;
        Despawned = null;
        if (handler != null)
            handler(this);

        Destroy(gameObject);
    }

    /// <summary>
    /// 过 seconds 秒后消失（0 或负数 = 立刻）。终点 Volume 想让它“进料口停一下再被吃掉”时用。
    /// 用 deltaTime 计时，暂停期间不会偷偷走完。
    /// </summary>
    public void DespawnAfter(float seconds)
    {
        if (seconds <= 0f || !isActiveAndEnabled)
        {
            Despawn();
            return;
        }

        StartCoroutine(DespawnRoutine(seconds));
    }

    // ---------------------------------------------------------------- 内部

    private IEnumerator DespawnRoutine(float seconds)
    {
        float elapsed = 0f;
        while (elapsed < seconds)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        Despawn();
    }

    private void TurnTowardsMoveDirection(float dt)
    {
        if (!faceMoveDirection || moveDirection.sqrMagnitude < 1e-6f)
            return;

        Quaternion look = Quaternion.LookRotation(moveDirection, Vector3.up);
        transform.rotation = turnSpeedDegrees > 0f
            ? Quaternion.RotateTowards(transform.rotation, look, turnSpeedDegrees * dt)
            : look;
    }

    /// <summary>
    /// 保证这种货物是“有 Rigidbody 的 Kinematic 物体”：
    /// 触发事件要求碰撞双方至少一方有 Rigidbody，而 transform 推进又不能让刚体是动态的。
    /// </summary>
    private void SetUpPhysics()
    {
        Rigidbody body = GetComponent<Rigidbody>();
        if (body == null)
        {
            body = gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
        }
        else if (makeRigidbodyKinematic && !body.isKinematic)
        {
            body.isKinematic = true;
            body.useGravity = false;
        }

        // Kinematic 刚体只支持 Speculative Continuous；速度较快、Volume 较薄时能明显减少漏检
        if (body.isKinematic && body.collisionDetectionMode == CollisionDetectionMode.Discrete)
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

        if (GetComponentInChildren<Collider>() == null)
            Debug.LogWarning("[PipelineItem] 这个 prefab 上没有 Collider，终点 Volume 检测不到它（会一路飞过终点）。请给 prefab 加一个 Collider。", this);
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!launched)
            return;

        Gizmos.color = new Color(1f, 0.75f, 0.2f);
        Gizmos.DrawWireSphere(TargetPosition, Mathf.Max(arriveRadius, 0.05f));
        Gizmos.DrawLine(transform.position, TargetPosition);
    }
#endif
}
