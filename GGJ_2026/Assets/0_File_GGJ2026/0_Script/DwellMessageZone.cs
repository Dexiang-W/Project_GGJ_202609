using System.Collections;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// “停留触发区”（Dwell Volume）：角色（默认标签 Player）进入触发区后，
/// 必须【在区域内累计待够 requiredStaySeconds 秒】才会弹出一段文字提示。
///
/// 与 <see cref="MessageTriggerZone"/> 的区别：
///   · MessageTriggerZone：进入即开始倒计时，就算马上离开也会照样弹文字；
///   · DwellMessageZone：人走了就不算数（默认清零进度），适合“玩家停下来观察/思考”的时刻，
///     例如站在断裂的墙体前 5 秒才冒出一句旁白。
///
/// 文字的淡入淡出与样式完全复用常驻的 GameplayHUD，和现有提示保持一致。
///
/// 挂法：给空物体加 Collider（勾选 Is Trigger，大小调成区域大小），再加本组件，
/// 在 Inspector 里填“触发文字 / 需要停留的秒数”即可，无需写代码。
/// 勾选 resetProgressOnExit = 必须连续待够；取消勾选 = 可以分几次进出累计。
///
/// 【纯检测、零阻拦】本组件只“看”角色进没进来，不做任何碰撞响应，也不会冻结/接管角色操作：
///   · 运行时会强制把身上的碰撞体设成 Is Trigger（forceTriggerCollider），绝不会变成挡路的实体墙；
///   · 文字只是 HUD 上的一行淡入淡出，不暂停时间、不停输入、不拦跳跃；
///   · 配合 oneShot：文字播完即把碰撞体关掉，同一段文字只播一次，之后进出都不再有任何检测。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))]
public class DwellMessageZone : MonoBehaviour
{
    [Header("触碰对象（默认标签 Player，与角色一致）")]
    [Tooltip("触发该区域的角色标签")]
    [SerializeField] private string playerTag = "Player";

    [Header("触发文字")]
    [Tooltip("停留时间达标后显示的提示文字。支持 \\n 换行。留空则只触发下面的事件，不显示文字。")]
    [SerializeField, TextArea(1, 4)] private string triggerMessage = "……";

    [Header("停留判定（秒）")]
    [Tooltip("需要在区域内累计停留多久才算达标（例：5 = 待满 5 秒才弹字）")]
    [SerializeField] private float requiredStaySeconds = 5f;
    [Tooltip("离开区域时是否把停留进度清零。勾选 = 必须连续待够；取消 = 可以分几次累计")]
    [SerializeField] private bool resetProgressOnExit = true;
    [Tooltip("进入区域后延迟多久才开始累计停留时间（0 = 立刻开始）")]
    [SerializeField] private float startDelaySeconds = 0f;

    [Header("显示时序（秒）")]
    [Tooltip("文字显示后自动淡隐前停留的时长；设为 0 或负数表示一直显示，直到角色离开触发区。")]
    [SerializeField] private float staySeconds = 4f;

    [Header("选项")]
    [Tooltip("角色离开触发区时立刻隐藏文字（想让它停留满 staySeconds 再消失可取消勾选）")]
    [SerializeField] private bool hideOnExit = true;
    [Tooltip("勾选后整个区域只触发一次（之后进出都不再显示）")]
    [SerializeField] private bool oneShot = false;
    [Tooltip("达标时若已有别的提示区正在显示文字，是否允许覆盖。一般保持勾选。")]
    [SerializeField] private bool overrideOtherMessage = true;

    [Header("纯检测（本区域绝不挡路）")]
    [Tooltip("运行时强制把本物体上的碰撞体设成 Is Trigger：\n" +
             "本区域只做“有没有人进来”的检测，绝不会变成挡住角色跳跃/通行的实体墙。\n" +
             "（即使有人在 Inspector 里把 Is Trigger 勾掉了，也会被自动改回来）")]
    [SerializeField] private bool forceTriggerCollider = true;
    [Tooltip("文字播完后把碰撞体直接关掉：配合“只触发一次”，同一段文字绝不可能播第二遍，" +
             "也不会在后面留下任何还在检测的残留体积。")]
    [SerializeField] private bool disableColliderAfterFire = true;

    [Header("达标时触发（可选）")]
    [Tooltip("停留达标时触发，可用来播音效 / 点亮机关 / 触发其它逻辑，不需要就留空")]
    public UnityEvent onDwellCompleted;

    /// <summary>当前“拥有”屏幕提示文字的停留区，避免 A 区离场时把 B 区刚显示的文字关掉。</summary>
    private static DwellMessageZone activeOwner;

    /// <summary>是否有停留区正在显示文字（供其它系统避让）。</summary>
    public static bool IsMessageActive => activeOwner != null;

    private Coroutine routine;
    private int insideCount;
    private bool used;
    private Collider[] zoneColliders;

    /// <summary>当前累计的停留秒数（只读，方便调试 / 别的脚本查询）。</summary>
    public float ElapsedStay { get; private set; }

    // ---------------------------------------------------------------- 生命周期

    private void Awake()
    {
        CacheColliders();
        ApplyTriggerSafety();
    }

    // ---------------------------------------------------------------- 触发

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(playerTag))
            return;

        // 转场中（玩家还在上一关的位置）不判定，避免“刚进关卡就弹出后面的旁白”
        if (MessageTriggerZone.SuppressMessages)
            return;

        // 角色身上可能有多个 Collider，只把“真正第一次进入”算作一次触发
        insideCount++;
        if (insideCount > 1)
            return;

        if (oneShot && used)
            return;

        StopRoutine();
        routine = StartCoroutine(DwellRoutine());
    }

    private void OnTriggerExit(Collider other)
    {
        if (!other.CompareTag(playerTag))
            return;

        if (MessageTriggerZone.SuppressMessages)
            return;

        insideCount = Mathf.Max(0, insideCount - 1);
        if (insideCount > 0)
            return;

        if (resetProgressOnExit)
            ResetProgress();

        if (hideOnExit)
            StopAndHide();
    }

    private void OnDisable()
    {
        // 区域被停用 / 场景卸载时，别把文字留在屏幕上
        StopAndHide();
        insideCount = 0;
        ElapsedStay = 0f;
    }

    // ---------------------------------------------------------------- 对外接口

    /// <summary>把停留进度清零（下一次进入要重新待满）。</summary>
    public void ResetProgress()
    {
        StopRoutine();
        ElapsedStay = 0f;
    }

    /// <summary>调试 / 机关唤用：不经过停留判定，直接手动弹出本区域的文字。</summary>
    public void TriggerMessage()
    {
        StopRoutine();
        StartCoroutine(ShowOnlyRoutine());
    }

    // ---------------------------------------------------------------- 内部

    private IEnumerator DwellRoutine()
    {
        if (startDelaySeconds > 0f)
            yield return WaitRespectingPause(startDelaySeconds);

        // —— 累计停留时间：人不在区域里就不计时（可选保留已累计的进度）——
        while (ElapsedStay < requiredStaySeconds)
        {
            if (PauseMenuManager.IsPaused)
            {
                yield return null;
                continue;
            }

            if (insideCount <= 0)
            {
                if (resetProgressOnExit)
                {
                    // 中途离开：这一次不算数，等下次进来重新开始
                    ElapsedStay = 0f;
                    routine = null;
                    yield break;
                }

                // 不清零：人在外面就暂停计时，进度留着
                yield return null;
                continue;
            }

            ElapsedStay += Time.unscaledDeltaTime;
            yield return null;
        }

        // —— 达标：先触发事件，再显示文字 ——
        used = true;
        if (onDwellCompleted != null)
            onDwellCompleted.Invoke();

        if (!string.IsNullOrEmpty(triggerMessage))
        {
            GameplayHUD.EnsureCreated();
            if (GameplayHUD.Exists && CanTakeOverScreen())
            {
                activeOwner = this;
                GameplayHUD.Instance.ShowMessage(triggerMessage);

                if (staySeconds > 0f)
                    yield return WaitRespectingPause(staySeconds);

                // 停留结束：即使角色还站在区域内，也照常淡隐
                HideIfOwner();
            }
        }

        // 只触发一次：文字播完就把碰撞体关掉，之后彻底不再检测
        if (oneShot && disableColliderAfterFire)
            DisableZoneColliders();

        routine = null;
    }

    /// <summary>缓存本物体上的碰撞体。</summary>
    private void CacheColliders()
    {
        zoneColliders = GetComponents<Collider>();
    }

    /// <summary>
    /// 强制把碰撞体设成 Is Trigger：本区域只是“检测器”，
    /// 不产生任何碰撞响应，不会挡住角色的跳跃 / 通行。
    /// </summary>
    private void ApplyTriggerSafety()
    {
        if (!forceTriggerCollider)
            return;

        if (zoneColliders == null)
            CacheColliders();

        if (zoneColliders == null)
            return;

        foreach (Collider col in zoneColliders)
        {
            if (col != null)
                col.isTrigger = true;
        }
    }

    /// <summary>关掉碰撞体：一次性区域播完即彻底退场，之后进出都不再有任何检测。</summary>
    private void DisableZoneColliders()
    {
        if (zoneColliders == null)
            CacheColliders();

        if (zoneColliders == null)
            return;

        foreach (Collider col in zoneColliders)
        {
            if (col != null)
                col.enabled = false;
        }
    }

#if UNITY_EDITOR
    /// <summary>在 Inspector 里改参数时也顺手纠正：Is Trigger 被勾掉会自动改回来。</summary>
    private void OnValidate()
    {
        if (!forceTriggerCollider)
            return;

        foreach (Collider col in GetComponents<Collider>())
        {
            if (col != null)
                col.isTrigger = true;
        }
    }
#endif

    /// <summary>本区是否可以占用屏幕提示（不允许覆盖时，已有提示就不抢）。</summary>
    private bool CanTakeOverScreen()
    {
        if (overrideOtherMessage)
            return true;

        bool othersShowing = MessageTriggerZone.IsMessageActive || (IsMessageActive && activeOwner != this);
        return !othersShowing;
    }

    /// <summary>直接显示文字并按 staySeconds 停留（staySeconds ≤ 0 时一直显示到离开）。</summary>
    private IEnumerator ShowOnlyRoutine()
    {
        if (string.IsNullOrEmpty(triggerMessage))
            yield break;

        GameplayHUD.EnsureCreated();
        if (!GameplayHUD.Exists)
            yield break;

        if (!CanTakeOverScreen())
            yield break;

        activeOwner = this;
        GameplayHUD.Instance.ShowMessage(triggerMessage);

        if (staySeconds > 0f)
            yield return WaitRespectingPause(staySeconds);

        HideIfOwner();
    }

    private void StopAndHide()
    {
        StopRoutine();
        HideIfOwner();
    }

    private void StopRoutine()
    {
        if (routine == null)
            return;

        StopCoroutine(routine);
        routine = null;
    }

    private void HideIfOwner()
    {
        if (activeOwner != this)
            return;

        if (GameplayHUD.Exists)
            GameplayHUD.Instance.HideMessage();

        activeOwner = null;
    }

    /// <summary>按真实时间等待，暂停菜单期间不计时（与 HUD 的淡入淡出用同一套时间基准）。</summary>
    private static IEnumerator WaitRespectingPause(float seconds)
    {
        float elapsed = 0f;
        while (elapsed < seconds)
        {
            if (PauseMenuManager.IsPaused)
            {
                yield return null;
                continue;
            }

            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Collider col = GetComponent<Collider>();
        if (col == null)
            return;

        Gizmos.color = new Color(1f, 0.75f, 0.25f, 0.55f);

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
