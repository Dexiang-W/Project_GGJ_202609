using UnityEngine;

/// <summary>
/// P_Paper（散落的纸张）：
///  · 靠近 → InteractableObject 描边高亮提醒可交互，屏幕下方提示“按 E 查看”；
///  · 按 E → 弹出“纸张阅读”界面：背景是这张纸的纹理（<see cref="paperTexture"/> 插槽，
///    想换成自己的贴图直接拖进去就行），上方是标题与正文叙述，右上角 × 点击退出
///    （也可以按 ESC / E / B 退出）；
///  · 同时把这张纸收进书本（<see cref="GGJBookSystem"/>），之后按 B 翻开书本可以反复浏览；
///  · 收集后默认把场景里的纸张物体隐藏起来。
///
/// 用法：挂到场景里每一张 P_Paper 上（编辑器菜单 GGJ2026/书本系统 → 当前场景接入 P_Book / P_Paper
/// 可以一键挂好），标题 / 正文 / 纸张贴图都在 Inspector 上填。
/// </summary>
public class GGJPaperNote : GGJInteractableBase
{
    [Header("纸张内容")]
    [Tooltip("纸张唯一 ID（同 ID 只会被收录一次）；留空自动用标题，再没有就用物体名")]
    [SerializeField] private string paperId = "";
    [Tooltip("书本里 / 阅读界面顶部显示的标题")]
    [SerializeField] private string paperTitle = "无名的纸片";
    [Tooltip("纸张上的正文叙述（支持换行）")]
    [TextArea(10, 40)]
    [SerializeField] private string bodyText = "（在这里写这张纸上的文字）";
    [Tooltip("纸张纹理插槽：阅读界面的背景就换成这张贴图；留空用书本系统的默认纸张纹理")]
    [SerializeField] private Texture2D paperTexture;

    [Header("高亮")]
    [Tooltip("纸张通常只有一个面（开放网格），沿法线挤出的描边会被自己挡住看不见。" +
             "保持勾选 = 用“整体放大一圈”的描边方式，纸片也能正常发光。")]
    [SerializeField] private bool flatOutline = true;

    [Header("拾取后")]
    [Tooltip("收集后隐藏这张纸")]
    [SerializeField] private bool hideAfterCollect = true;
    [Tooltip("要隐藏的物体；留空 = 隐藏挂本脚本的物体本身")]
    [SerializeField] private GameObject objectToHide;
    [Tooltip("收集后立刻弹出这张纸的阅读界面")]
    [SerializeField] private bool showNoteOnCollect = true;

    private bool collected;

    protected override bool IsDone => collected;

    private void Reset()
    {
        promptText = "按 E 查看";
        paperId = name;
        paperTitle = name;
    }

    protected override void Awake()
    {
        base.Awake();

        // 纸张是开放薄片：让描边改用“整体放大一圈”的方式，否则靠近时看不到高亮
        if (interactable != null)
            interactable.SetFlatOutline(flatOutline);
    }

    protected virtual void OnEnable()
    {
        // 关卡重置 / 重新激活后这张纸可以再看一次（重复收集不会在书本里产生第二份）
        collected = false;
    }

    protected override void OnInteracted()
    {
        GGJBookSystem.EnsureCreated();
        GGJBookSystem book = GGJBookSystem.Instance;
        if (book == null)
            return;

        var entry = new GGJBookSystem.PaperEntry
        {
            paperId = string.IsNullOrEmpty(paperId) ? (string.IsNullOrEmpty(paperTitle) ? name : paperTitle) : paperId,
            title = string.IsNullOrEmpty(paperTitle) ? name : paperTitle,
            bodyText = bodyText,
            texture = paperTexture,
        };

        // 收录进书本：同一个 paperId 只会收一次（按 R 重置后物体重新出现也不会多一份）
        book.CollectPaper(entry);
        collected = true;

        // 交互音效由 InteractableObject 统一处理，这里不再叠一层，避免两张纸叠音
        if (hideAfterCollect)
            ConsumeAndHide(objectToHide, true);

        if (showNoteOnCollect)
            book.ShowPaperNote(entry);
    }
}
