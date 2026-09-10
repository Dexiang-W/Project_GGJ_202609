using System.Collections;
using UnityEngine;

/// <summary>
/// “文字提示触发区”（Volume）：角色（默认标签 Player）进入触发区后，
/// 屏幕中部偏左淡入显示一段本区域配置好的文字，用于引导 / 旁白 / 教学提示。
///
/// 与 RespawnZone、LevelTransitionZone 的区别：本组件【不黑屏、不传送、不切场景】，
/// 只是单纯弹一段文字，所以可以到处摆放（例：“前方小心”“站到能量球旁按 E 给植物充能”）。
/// 文字的淡入淡出与样式完全复用常驻的 GameplayHUD，和现有区域提示保持一致。
///
/// 挂法：给空物体加 Collider（勾选 Is Trigger，把大小调成区域大小），再加本组件，
/// 在 Inspector 里填“触发文字 / 显示时长”即可，无需写代码。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))]
public class MessageTriggerZone : MonoBehaviour
{
    [Header("触碰对象（默认标签 Player，与角色一致）")]
    [Tooltip("触发该区域的角色标签")]
    [SerializeField] private string playerTag = "Player";

    [Header("触发文字")]
    [Tooltip("角色进入触发区后显示的提示文字。支持 \\n 换行。留空则什么都不做。")]
    [SerializeField, TextArea(1, 4)] private string triggerMessage = "……";

    [Header("显示时序（秒）")]
    [Tooltip("进入后延迟多久才显示（0 = 立刻显示）")]
    [SerializeField] private float showDelaySeconds = 0f;
    [Tooltip("显示后自动淡隐前停留的时长；设为 0 或负数表示一直显示，直到角色离开触发区。")]
    [SerializeField] private float staySeconds = 4f;

    [Header("选项")]
    [Tooltip("角色离开触发区时立刻隐藏文字（想让它停留满 staySeconds 再消失可取消勾选）")]
    [SerializeField] private bool hideOnExit = true;
    [Tooltip("勾选后整个区域只触发一次（之后进出都不再显示）")]
    [SerializeField] private bool oneShot = false;
    [Tooltip("进入时若已有别的触发区正在显示文字，是否允许覆盖。同一时间只保留一条最靠近的提示，一般保持勾选。")]
    [SerializeField] private bool overrideOtherMessage = true;

    /// <summary>当前“拥有”屏幕提示文字的触发区，避免 A 区离场时把 B 区刚显示的文字关掉。</summary>
    private static MessageTriggerZone activeOwner;

    private Coroutine routine;
    private int insideCount;
    private bool used;

    // ---------------------------------------------------------------- 触发

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(playerTag))
            return;

        // 角色身上可能有多个 Collider，只把“真正第一次进入”算作一次触发
        insideCount++;
        if (insideCount > 1)
            return;

        Begin();
    }

    private void OnTriggerExit(Collider other)
    {
        if (!other.CompareTag(playerTag))
            return;

        insideCount = Mathf.Max(0, insideCount - 1);
        if (insideCount > 0)
            return;

        if (hideOnExit)
            StopAndHide();
    }

    private void OnDisable()
    {
        // 区域被停用 / 场景卸载时，别把文字留在屏幕上
        StopAndHide();
        insideCount = 0;
    }

    private void Begin()
    {
        if (oneShot && used)
            return;

        if (string.IsNullOrEmpty(triggerMessage))
            return;

        if (!overrideOtherMessage && activeOwner != null && activeOwner != this)
            return;

        used = true;

        GameplayHUD.EnsureCreated();
        if (!GameplayHUD.Exists)
            return;

        StopRoutine();
        routine = StartCoroutine(ShowRoutine());
    }

    /// <summary>调试 / 机关唤用：不经过碰撞，直接手动弹出本区域的文字。</summary>
    public void TriggerMessage()
    {
        Begin();
    }

    // ---------------------------------------------------------------- 内部

    private IEnumerator ShowRoutine()
    {
        activeOwner = this;

        if (showDelaySeconds > 0f)
            yield return WaitRealtimeRespectingPause(showDelaySeconds);

        if (GameplayHUD.Exists)
            GameplayHUD.Instance.ShowMessage(triggerMessage);

        if (staySeconds > 0f)
            yield return WaitRealtimeRespectingPause(staySeconds);

        // 停留结束：即使角色还站在区域内，也照常淡隐
        HideIfOwner();
        routine = null;
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
    private static IEnumerator WaitRealtimeRespectingPause(float seconds)
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

        Gizmos.color = new Color(0.4f, 0.85f, 1f, 0.4f);

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
