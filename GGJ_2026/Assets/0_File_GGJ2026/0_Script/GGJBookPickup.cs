using System.Collections;
using UnityEngine;

/// <summary>
/// P_Book（书本）拾取：
///  · 靠近 → InteractableObject 描边高亮，屏幕下方提示“按 E 拾取”；
///  · 按 E → 书本进入背包（之后随时按 <b>B</b> 翻开/合上，可反复浏览收集到的纸张），
///    并依次弹出系统提示：“注意旅途中遇到的纸张，多收集纸张可以获得更多信息”；
///  · 拾取后默认把场景里的书本物体隐藏起来。
///
/// 用法：挂到场景里的 P_Book 上（编辑器菜单 GGJ2026/书本系统 → 当前场景接入 P_Book / P_Paper
/// 可以一键挂好），提示文字 / 停留时长都在 Inspector 上改。
/// </summary>
public class GGJBookPickup : GGJInteractableBase
{
    [Header("书本")]
    [Tooltip("书本标题（翻开书本时显示在页面顶部）")]
    [SerializeField] private string bookTitle = "旅人笔记";

    [Header("拾取后的系统提示")]
    [TextArea(1, 4)]
    [SerializeField] private string pickupMessage = "注意旅途中遇到的纸张，多收集纸张可以获得更多信息";
    [Tooltip("第一条提示停留的秒数")]
    [SerializeField] private float pickupMessageSeconds = 4f;
    [TextArea(1, 4)]
    [Tooltip("第二条小提示：告诉玩家之后怎么翻书；留空就不显示")]
    [SerializeField] private string hintMessage = "按 B 随时翻开书本";
    [SerializeField] private float hintMessageSeconds = 3.5f;

    [Header("拾取后")]
    [Tooltip("拾取后隐藏书本物体")]
    [SerializeField] private bool hideOnPickup = true;
    [Tooltip("要隐藏的物体；留空 = 隐藏挂本脚本的物体本身")]
    [SerializeField] private GameObject objectToHide;

    private bool picked;

    protected override bool IsDone => picked;

    private void Reset()
    {
        promptText = "按 E 拾取";
    }

    protected override void OnInteracted()
    {
        picked = true;
        PlayPickupSound();

        GGJBookSystem.EnsureCreated();
        GGJBookSystem book = GGJBookSystem.Instance;
        if (book != null)
            book.AcquireBook(bookTitle);

        // 注意：本物体马上会被隐藏，协程要挂到 HUD 上跑，否则会被一起停掉
        if (GameplayHUD.Exists)
            GameplayHUD.Instance.StartCoroutine(ShowPickupMessages(GameplayHUD.Instance));

        ConsumeAndHide(objectToHide, hideOnPickup);
    }

    private IEnumerator ShowPickupMessages(GameplayHUD hud)
    {
        if (hud == null)
            yield break;

        hud.ShowMessage(pickupMessage);
        yield return new WaitForSecondsRealtime(Mathf.Max(0f, pickupMessageSeconds));

        if (hud == null)
            yield break;

        if (!string.IsNullOrEmpty(hintMessage))
        {
            hud.ShowMessage(hintMessage);
            yield return new WaitForSecondsRealtime(Mathf.Max(0f, hintMessageSeconds));
        }

        if (hud != null)
            hud.HideMessage();
    }
}
