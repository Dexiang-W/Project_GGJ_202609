using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using StarterAssets;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// 流水线“危险货物”：玩家碰到这件正在移动的 prefab（货物）时，会被送回指定复活点。
///
/// 挂法：和 PipelineItem 挂在同一个 prefab 上（本组件会自动跟着 prefab 一起生成/移动）；
/// 复活点在 PipelineSpawner 的「危险货物复活点」里指定（prefab 引用不了场景物体，见下方小提示）。
///
/// 触发后的流程（与 RespawnZone 一致，都是项目里既有的复活流程）：
///   触碰 → （可选）画面淡入黑幕 → 显示提示文字（与 RespawnZone 同一套：位置 / 字体 / 淡入淡出都一样）
///        → 玩家传送到复活点、清空运动残留、耐力回满 → 相机回到跟拍机位并瞬间就位
///        → 黑幕淡出 → 文字再停留一会儿后淡隐 → 恢复玩家输入。
///
/// 提示文字怎么改（两种都行，本组件上填了以本组件为准）：
///   · 全局一处改：Assets/0_File_GGJ2026/Resources/GGJTextConfig.asset
///     →「复活提示（被流水线危险货物撞到）」，改一次所有货物通用，不用动 prefab；
///   · 单件改：在 prefab 的 PipelineHazard 上填「复活提示文字」。
///
/// 与 RespawnZone 的区别：RespawnZone 是“走进固定区域就复活”，本组件是“被移动中的货物撞到才复活”，
/// 判定范围跟着货物一起走。
///
/// 判定说明（不依赖物理触发设置，货物是 Trigger 还是实体碰撞都能正确命中，也不需要给玩家加 Rigidbody）：
///   · 默认按本 prefab 上 Collider 的尺寸算危险范围（用局部包围盒，随 prefab 的缩放 / 旋转一起动）；
///   · 想让危险范围比模型更大（刀光、火焰、锯片之类），把 hitRadius 填一个大于 0 的值，
///     这时就以 prefab 原点为中心用这个半径判定；
///   · prefab 上没有 Collider 也能用：这时用 fallbackHitRadius（默认 0.5 米）当危险半径。
///
/// 小提示：
///   · 不想让货物把玩家“顶开 / 卡住”（而是直接穿过去撞到就死），把 prefab 上 Collider 的 Is Trigger 勾上；
///   · 复活点是场景里的物体，而 prefab 资源不允许引用场景物体 —— 所以在 prefab 上是拖不进去的：
///     请在 PipelineSpawner 面板的「危险货物复活点」里指定，生成时会自动注入给每一件货物；
///     直接把这个 prefab 摆进场景手动用时，也能在本组件上拖点；两处都留空则自动找场景里名为 SpawnPoint 的物体；
///   · 暂停菜单、关卡转场期间不会触发；同一个货物有冷却时间，不会在复活过程中反复触发；
///   · 想让货物撞到玩家后消失，勾 destroyItemOnHit；不勾则流水线照常运转。
/// </summary>
[DisallowMultipleComponent]
public class PipelineHazard : MonoBehaviour
{
    [Header("触碰对象（默认标签 Player，与角色一致）")]
    [SerializeField] private string playerTag = "Player";

    [Header("判定范围")]
    [Tooltip("危险半径（米）：大于 0 = 以本 prefab 原点为中心用这个半径判定（想比模型大一圈就填它）。\n" +
             "留 0 = 自动：有 Collider 就按 Collider 尺寸算，没有就用 fallbackHitRadius。")]
    [SerializeField] private float hitRadius = 0f;

    [Tooltip("prefab 上没有 Collider 时使用的危险半径（米）")]
    [SerializeField] private float fallbackHitRadius = 0.5f;

    [Tooltip("额外宽容度（米）：命中判定再放宽一点，避免“看着撞上了却没反应”")]
    [SerializeField] private float contactPadding = 0.05f;

    [Header("复活点（玩家落点）")]
    [Tooltip("玩家被撞后复活到这里。\n" +
             "· 用 PipelineSpawner 生成时【这里留空即可】：点位请填在生成器面板的「危险货物复活点」，" +
             "生成时自动注入给每件货物（prefab 资源不能引用场景物体，所以在 prefab 上拖不进来）；\n" +
             "· 直接把这个 prefab 摆进场景手动用，才需要在这里拖一个点位。")]
    [SerializeField] private Transform respawnPoint;

    [Tooltip("兜底：上面没指定（也没被生成器注入）时，自动在场景里按名字找一个物体当复活点，" +
             "免去手动连线。")]
    [SerializeField] private bool autoFindSceneSpawnPoint = true;

    [Tooltip("兜底自动查找的物体名（默认 SpawnPoint，与本项目 GGJLevelResetManager / LevelTransitionZone 的约定一致）")]
    [SerializeField] private string spawnPointObjectName = "SpawnPoint";

    [Tooltip("落点微调（世界坐标偏移）：例如 +0.1 Y 让玩家别陷进地面")]
    [SerializeField] private Vector3 respawnOffset = Vector3.zero;

    [Tooltip("复活后把玩家朝向对齐到复活点的朝向（不勾 = 保持原朝向）")]
    [SerializeField] private bool useRespawnFacing = true;

    [Header("黑幕演出（可关）")]
    [Tooltip("关闭 = 撞到后瞬间传送，没有黑幕")]
    [SerializeField] private bool useFade = true;

    [Tooltip("触碰后画面变黑所需时长")]
    [SerializeField] private float fadeToBlackSeconds = 0.25f;

    [Tooltip("全黑停留时长（传送发生在全黑期间）")]
    [SerializeField] private float blackHoldSeconds = 0.15f;

    [Tooltip("黑幕淡出所需时长")]
    [SerializeField] private float fadeFromBlackSeconds = 0.35f;

    [Header("复活提示文字（与 RespawnZone 同一套显示）")]
    [Tooltip("复活时是否显示提示文字（关掉 = 只传送、不弹字）")]
    [SerializeField] private bool showMessage = true;

    [Tooltip("要显示的文字（屏幕左侧、垂直居中，位置 / 字体 / 淡入淡出与 RespawnZone 完全一致）。\n" +
             "支持 \\n 换行。\n" +
             "【留空 = 用全局配置里的文案】：Assets/0_File_GGJ2026/Resources/GGJTextConfig.asset\n" +
             "→「复活提示（被流水线危险货物撞到）」，一处就能改所有货物，不用逐个改 prefab。")]
    [SerializeField, TextArea(1, 3)] private string messageText = "";

    [Tooltip("淡出后文字再停留多久；0 = 淡出一完成就隐藏")]
    [SerializeField] private float messageStaySeconds = 1.2f;

    [Header("选项")]
    [Tooltip("撞到玩家后让这件货物消失（不勾 = 流水线继续运转）")]
    [SerializeField] private bool destroyItemOnHit = false;

    [Tooltip("同一个货物两次撞人的最短间隔（秒）：防止复活过程中被反复触发")]
    [SerializeField] private float cooldownSeconds = 1f;

    [Header("事件（可选：接音效 / 特效 / 计数）")]
    [Tooltip("撞到玩家时触发一次（在传送之前）")]
    public UnityEvent OnPlayerHit = new UnityEvent();

    [Tooltip("玩家被传送到复活点之后触发一次")]
    public UnityEvent OnPlayerRespawned = new UnityEvent();

    // 玩家缓存（换关卡 / 重建玩家后会自动重新获取）
    private ThirdPersonController player;
    private CharacterController playerController;
    private Collider[] playerColliders;

    // 本 prefab 的危险范围（在局部空间里算，随缩放 / 旋转一起动）
    private bool hasLocalBounds;
    private Bounds localBounds;

    private float nextHitTime;
    private bool respawning;

    // 兜底找到的场景出生点（生成器注入的 respawnPoint 优先）
    private Transform fallbackRespawnPoint;

    // ---------------------------------------------------------------- 生命周期

    private void Awake()
    {
        BuildDangerBounds();
    }

    private void Update()
    {
        if (respawning)
            return;

        // 已经有复活流程在跑（别的货物，或 RespawnZone 的死亡区），或刚复活完的免疫期内 → 不判定。
        // 否则会在别人的复活流程中间插进来第二套流程，输入 / 黑幕 / 传送互相打断。
        if (RespawnFlow.IsProtected)
            return;

        // 暂停 / 转场 / 重开流程中不判定
        if (PauseMenuManager.IsPaused || LevelTransitionZone.AnyTransitionRunning || GameTitleRestart.IsRestartInProgress)
            return;

        // 暂停时 Time.deltaTime 为 0，这里顺带把“时间冻结”的情况挡掉
        if (Time.deltaTime <= 0f)
            return;

        if (Time.time < nextHitTime)
            return;

        if (!ItemTouchesPlayer())
            return;

        Transform point = ResolveRespawnPoint();
        if (point == null)
        {
            Debug.LogWarning("[PipelineHazard] 撞到玩家了，但没有复活点可去，玩家不会被传送。\n" +
                             "请在本组件所属那条流水线的 PipelineSpawner 面板上填「危险货物复活点」" +
                             "（prefab 资源不能引用场景物体，所以点位要配在生成器上）；" +
                             "或者直接把这个 prefab 摆进场景、在本组件上拖一个 Respawn Point。", this);
            nextHitTime = Time.time + Mathf.Max(0.1f, cooldownSeconds);
            return;
        }

        BeginRespawn(point);
    }

    // ---------------------------------------------------------------- 判定

    private bool ItemTouchesPlayer()
    {
        ResolvePlayer();
        if (player == null)
            return false;
        if (!string.IsNullOrEmpty(playerTag) && !player.CompareTag(playerTag))
            return false;

        Vector3 bottom, top;
        float radius;
        GetPlayerCapsule(out bottom, out top, out radius);

        // 把玩家胶囊采样成几个点，逐点算“到货物表面的距离”，避免细长货物被漏判
        const int samples = 5;
        float reach = radius + Mathf.Max(0f, contactPadding);

        for (int i = 0; i < samples; i++)
        {
            float t = samples <= 1 ? 0.5f : i / (float)(samples - 1);
            Vector3 point = Vector3.Lerp(bottom, top, t);
            if (DistanceToItem(point) <= reach)
                return true;
        }

        return false;
    }

    /// <summary>点到货物表面的近似距离（点在范围内返回 0），世界单位。</summary>
    private float DistanceToItem(Vector3 worldPoint)
    {
        // ① 手填了危险半径 → 以 prefab 原点为中心当一个球
        if (hitRadius > 0f)
            return Vector3.Distance(worldPoint, transform.position) - hitRadius;

        // ② prefab 上有 Collider → 用局部包围盒（随缩放 / 旋转一起动）
        if (hasLocalBounds)
        {
            Vector3 local = transform.InverseTransformPoint(worldPoint);
            Vector3 d = local - localBounds.center;
            Vector3 extents = localBounds.extents;

            float dx = Mathf.Max(0f, Mathf.Abs(d.x) - extents.x);
            float dy = Mathf.Max(0f, Mathf.Abs(d.y) - extents.y);
            float dz = Mathf.Max(0f, Mathf.Abs(d.z) - extents.z);

            float scale = MaxAbsComponent(transform.lossyScale);
            return new Vector3(dx, dy, dz).magnitude * scale;
        }

        // ③ 什么都没配 → 用默认半径的球兜底
        return Vector3.Distance(worldPoint, transform.position) - Mathf.Max(0.05f, fallbackHitRadius);
    }

    private void GetPlayerCapsule(out Vector3 bottom, out Vector3 top, out float radius)
    {
        Transform t = player.transform;

        if (playerController != null)
        {
            Vector3 scale = t.lossyScale;
            float scaleXZ = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            float scaleY = Mathf.Abs(scale.y);

            Vector3 center = t.TransformPoint(playerController.center);
            radius = Mathf.Max(0.01f, playerController.radius * scaleXZ);
            float half = Mathf.Max(0f, playerController.height * 0.5f * scaleY - radius);

            bottom = center - t.up * half;
            top = center + t.up * half;
            return;
        }

        // 没有 CharacterController 时的兜底：拿玩家的一个 Collider 当球用
        Collider col = FirstUsablePlayerCollider();
        if (col != null)
        {
            Bounds bounds = col.bounds;
            bottom = bounds.center;
            top = bounds.center;
            radius = Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z);
            return;
        }

        bottom = t.position;
        top = t.position;
        radius = 0.5f;
    }

    private void ResolvePlayer()
    {
        if (player != null)
            return;

        player = ThirdPersonController.Instance;
        if (player == null)
            player = FindObjectOfType<ThirdPersonController>();

        playerController = player != null ? player.GetComponent<CharacterController>() : null;
        playerColliders = player != null ? player.GetComponentsInChildren<Collider>(true) : null;
    }

    private Collider FirstUsablePlayerCollider()
    {
        if (playerColliders == null)
            return null;

        for (int i = 0; i < playerColliders.Length; i++)
        {
            Collider col = playerColliders[i];
            if (col != null && col.enabled)
                return col;
        }

        return null;
    }

    // ---------------------------------------------------------------- 复活点

    /// <summary>
    /// 由 PipelineSpawner 生成时调用：把生成器面板上的复活点注入给这件货物。
    /// （prefab 资源不允许引用场景物体，所以点位只能在生成器上指定，运行时注入。）
    /// </summary>
    public void SetRespawnPoint(Transform point)
    {
        if (point == null)
            return;

        respawnPoint = point;
    }

    /// <summary>取当前生效的复活点：本组件面板 &gt; 生成器注入 &gt; 自动找场景里的 SpawnPoint。</summary>
    private Transform ResolveRespawnPoint()
    {
        if (respawnPoint != null)
            return respawnPoint;

        if (!autoFindSceneSpawnPoint)
            return null;

        if (fallbackRespawnPoint == null)
            fallbackRespawnPoint = FindSceneObjectByName(spawnPointObjectName);

        return fallbackRespawnPoint;
    }

    /// <summary>在激活场景里按名字找物体（含未激活的子物体），与 GGJLevelResetManager 的查找方式一致。</summary>
    private static Transform FindSceneObjectByName(string targetName)
    {
        if (string.IsNullOrEmpty(targetName))
            return null;

        GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            if (roots[i] == null)
                continue;

            Transform found = FindInChildren(roots[i].transform, targetName);
            if (found != null)
                return found;
        }

        return null;
    }

    private static Transform FindInChildren(Transform parent, string targetName)
    {
        if (parent.name == targetName)
            return parent;

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform found = FindInChildren(parent.GetChild(i), targetName);
            if (found != null)
                return found;
        }

        return null;
    }

    // ---------------------------------------------------------------- 复活流程

    /// <summary>
    /// 取本次复活要显示的文字：本组件填了就用本组件的，留空用全局配置（GGJTextConfig），
    /// 全局也留空则用内置默认文案（与 RespawnZone 相同）。showMessage 关掉时返回 null（不显示）。
    /// </summary>
    private string ResolveMessageText()
    {
        if (!showMessage)
            return null;

        return string.IsNullOrEmpty(messageText)
            ? GGJTextConfig.GetPipelineHazardMessage()
            : messageText;
    }

    private void BeginRespawn(Transform point)
    {
        // 抢不到复活总闸（别人正在复活 / 免疫期 / 暂停 / 转场 / 重开）就什么都不做：
        // 不关输入、不开黑幕，等这一波流程结束再说。
        if (!RespawnFlow.TryBegin())
            return;

        nextHitTime = Time.time + Mathf.Max(0.1f, cooldownSeconds);
        respawning = true;

        OnPlayerHit.Invoke();
        StartCoroutine(RespawnRoutine(player, point));
    }

    private IEnumerator RespawnRoutine(ThirdPersonController target, Transform point)
    {
        try
        {
            if (target == null || point == null)
                yield break;

            bool hadInput = SetPlayerInput(target, false);

            // 与 RespawnZone 一样：确保 HUD 存在，黑幕与提示文字才能显示
            GameplayHUD.EnsureCreated();
            GameplayHUD hud = GameplayHUD.Instance;

            // 1. 画面渐黑
            if (useFade && hud != null)
                yield return hud.FadeToBlackRoutine(Mathf.Max(0f, fadeToBlackSeconds));

            // 2. 显示提示文字（与 RespawnZone 走同一个 HUD、同一个位置；开了黑幕就是全黑时淡入）
            string tip = ResolveMessageText();
            if (hud != null && !string.IsNullOrEmpty(tip))
                hud.ShowMessage(tip);

            if (useFade && hud != null && blackHoldSeconds > 0f)
                yield return new WaitForSecondsRealtime(blackHoldSeconds);

            // 3. 传送回复活点（屏蔽相机触发区，避免落点恰好在某个触发区里被额外切镜）
            CameraTriggerVolume.SuppressCameraSwitching = true;

            // 一次性“瞬移 + 站直”：清掉被撞瞬间的速度 / 输入 / 动画残留，并保证角色竖直。
            // 朝向：只有复活点被特意摆过朝向时才对齐它，否则保持角色原本的左右朝向
            // （默认 0,0,0 的复活点会把角色转成面朝世界 +Z，横版里看着就像“横过来了”）。
            bool alignToPoint = useRespawnFacing && RespawnFlow.HasCustomFacing(point);
            float yaw = alignToPoint
                ? RespawnFlow.HorizontalYawOf(point, target.transform.eulerAngles.y)
                : target.transform.eulerAngles.y;

            target.RespawnAt(point.position + respawnOffset, yaw);

            // 让物理把触发区的进入/离开事件结算完，相机再吸附
            yield return new WaitForFixedUpdate();
            yield return null;

            CameraFollowController cam = CameraFollowController.Instance;
            if (cam != null)
            {
                cam.SetNormalMode();
                cam.SnapToCurrentTarget();
            }

            // 解除屏蔽：落点若在某个相机触发区内，立刻重新应用该区域的机位
            CameraTriggerVolume.SuppressCameraSwitching = false;
            CameraTriggerVolume.ReevaluateAll();

            // 4. 黑幕淡出
            if (useFade && hud != null)
                yield return hud.FadeFromBlackRoutine(Mathf.Max(0f, fadeFromBlackSeconds));

            // 5. 文字再停留一会儿后淡隐
            if (hud != null)
            {
                if (showMessage && messageStaySeconds > 0f)
                    yield return new WaitForSecondsRealtime(messageStaySeconds);
                hud.HideMessage();
            }

            OnPlayerRespawned.Invoke();

            if (destroyItemOnHit)
                ConsumeSelf();

            // 6. 恢复玩家输入（恢复前会先清掉输入残留）
            SetPlayerInput(target, hadInput);
        }
        finally
        {
            CameraTriggerVolume.SuppressCameraSwitching = false;
            respawning = false;
            RespawnFlow.End();
        }
    }

    /// <summary>让这件货物消失（勾了 destroyItemOnHit 时走这里）。</summary>
    private void ConsumeSelf()
    {
        PipelineItem item = GetComponentInChildren<PipelineItem>();
        if (item != null)
            item.Despawn();
        else
            Destroy(gameObject);
    }

    /// <summary>停用 / 恢复玩家的 PlayerInput（与 RespawnZone / GGJLevelResetManager 一致的做法）。</summary>
    private static bool SetPlayerInput(ThirdPersonController player, bool enabled)
    {
        if (player == null)
            return true;

        bool previous = true;
#if ENABLE_INPUT_SYSTEM
        PlayerInput playerInput = player.GetComponent<PlayerInput>();
        if (playerInput != null)
        {
            previous = playerInput.enabled;
            if (enabled && !previous)
            {
                // 恢复前把当前输入清零，避免角色带着旧输入“滑”走
                StarterAssetsInputs inputs = player.GetComponent<StarterAssetsInputs>();
                if (inputs != null)
                    inputs.MoveInput(Vector2.zero);
            }
            playerInput.enabled = enabled;
        }
#else
        previous = true;
#endif
        return previous;
    }

    // ---------------------------------------------------------------- 危险范围（局部包围盒）

    /// <summary>把本 prefab 上所有 Collider 折算成本物体局部空间的包围盒。</summary>
    private void BuildDangerBounds()
    {
        hasLocalBounds = false;
        localBounds = new Bounds(Vector3.zero, Vector3.zero);

        Collider[] colliders = GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider col = colliders[i];
            if (col == null || col is CharacterController)
                continue;

            Bounds box = ToLocalBounds(col);
            if (!hasLocalBounds)
            {
                localBounds = box;
                hasLocalBounds = true;
            }
            else
            {
                localBounds.Encapsulate(box);
            }
        }
    }

    private Bounds ToLocalBounds(Collider col)
    {
        Matrix4x4 toLocal = transform.worldToLocalMatrix * col.transform.localToWorldMatrix;

        Vector3 center = Vector3.zero;
        Vector3 size = Vector3.one * 0.5f;

        if (col is BoxCollider box)
        {
            center = box.center;
            size = box.size;
        }
        else if (col is SphereCollider sphere)
        {
            center = sphere.center;
            size = Vector3.one * (sphere.radius * 2f);
        }
        else if (col is CapsuleCollider capsule)
        {
            center = capsule.center;
            float diameter = capsule.radius * 2f;
            size = capsule.direction == 0
                ? new Vector3(capsule.height, diameter, diameter)
                : capsule.direction == 1
                    ? new Vector3(diameter, capsule.height, diameter)
                    : new Vector3(diameter, diameter, capsule.height);
        }
        else if (col is MeshCollider mesh && mesh.sharedMesh != null)
        {
            center = mesh.sharedMesh.bounds.center;
            size = mesh.sharedMesh.bounds.size;
        }

        Vector3 half = size * 0.5f;
        Vector3 min = Vector3.positiveInfinity;
        Vector3 max = Vector3.negativeInfinity;

        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = center + new Vector3(
                (i & 1) == 0 ? -half.x : half.x,
                (i & 2) == 0 ? -half.y : half.y,
                (i & 4) == 0 ? -half.z : half.z);

            Vector3 point = toLocal.MultiplyPoint3x4(corner);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        Bounds result = new Bounds();
        result.SetMinMax(min, max);
        return result;
    }

    private static float MaxAbsComponent(Vector3 v)
    {
        return Mathf.Max(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        // 编辑态也算一遍，方便直接在场景里看范围
        if (!Application.isPlaying)
            BuildDangerBounds();

        Gizmos.color = new Color(1f, 0.3f, 0.25f, 0.85f);

        if (hitRadius > 0f)
        {
            Gizmos.DrawWireSphere(transform.position, hitRadius);
        }
        else if (hasLocalBounds)
        {
            Matrix4x4 old = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(localBounds.center, localBounds.size);
            Gizmos.matrix = old;
        }
        else
        {
            Gizmos.DrawWireSphere(transform.position, Mathf.Max(0.05f, fallbackHitRadius));
        }

        Transform point = ResolveRespawnPoint();
        if (point != null)
        {
            Vector3 pos = point.position + respawnOffset;
            Gizmos.color = new Color(0.2f, 1f, 0.55f, 1f);
            Gizmos.DrawWireSphere(pos, 0.35f);
            Gizmos.DrawLine(transform.position, pos);
        }
    }
#endif
}
