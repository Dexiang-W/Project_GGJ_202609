using System;
using System.Collections.Generic;
using StarterAssets;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// 书本 / 纸张收集系统（运行时自动创建、跨场景常驻，和 GameplayHUD 同一套路）：
///
///  · P_Book（书本）：挂 <see cref="GGJBookPickup"/>，靠近按 E 拾取 → 书本进入背包，
///    之后随时按 <b>B</b> 就能翻开/合上；
///  · P_Paper（纸张）：挂 <see cref="GGJPaperNote"/>，靠近按 E → 弹出“纸张阅读”界面
///    （背景 = 纸张纹理插槽，右上角 × 关闭），同时把这张纸收进书本；
///  · 收进来的纸张可以在书本里左右翻页反复浏览（‹ › 按钮 / ←→ / A D）；
///  · 纸张 / 书本界面打开时按 <b>ESC</b> 只退出阅读界面，不会弹出暂停菜单
///    （按 E / B 或点右上角 × 同样关闭）；
///
/// 使用方式（不用改代码）：
///  · 编辑器菜单 GGJ2026/书本系统 → 当前场景接入 P_Book / P_Paper（自动加交互），
///    它会自动给场景里这两个物体挂上 InteractableObject 与对应脚本；
///  · 每张纸的标题 / 正文 / 纸张贴图都在 P_Paper 物体的 GGJPaperNote 组件上填；
///  · 书本页面的默认纸张纹理、字体、界面配色都在本组件的 Inspector 上（运行时会自动
///    建一个常驻的 GGJ_BookSystem 对象；想预先配好，就把挂了本脚本的预制体放到
///    Resources/GGJ_BookSystem.prefab，和 GGJ_AudioManager 一个套路）。
///
/// 界面说明：项目里没有 EventSystem，所以按钮（× / ‹ / ›）与暂停菜单一样由本脚本
/// 直接对屏幕坐标做矩形判定，不依赖场景里有没有 UI 事件系统。
/// </summary>
[DisallowMultipleComponent]
public class GGJBookSystem : MonoBehaviour
{
    /// <summary>一张收集到的纸张。</summary>
    [Serializable]
    public class PaperEntry
    {
        [Tooltip("纸张唯一 ID（同一个 ID 只会被收录一次）；留空则自动用标题当 ID")]
        public string paperId;

        [Tooltip("书本里 / 阅读界面顶部显示的标题")]
        public string title = "无名的纸片";

        [Tooltip("纸张上的正文（支持换行）")]
        [TextArea(3, 12)]
        public string bodyText = "";

        [Tooltip("这张纸的纹理（阅读界面的背景）；留空则用书本系统的默认纸张纹理")]
        public Texture2D texture;
    }

    public static GGJBookSystem Instance { get; private set; }

    /// <summary>阅读界面是否正打开（书本 / 纸张都算）。其它系统可用它屏蔽自己的交互。</summary>
    public static bool IsUiOpen => Instance != null && Instance.uiOpen;

    /// <summary>
    /// 阅读界面是否要“吃掉”这一次 ESC：界面正开着时为真，界面刚被 ESC 关掉的那一帧也为真。
    /// 暂停菜单据此让出 ESC，避免按 ESC 关纸张/书本的同时又弹出“继续游戏 / 退出游戏”菜单。
    /// </summary>
    public static bool BlocksPauseToggle =>
        Instance != null && (Instance.uiOpen || Instance.closedFrame == Time.frameCount);

    // ---------------------------------------------------------------- 配置

    [Header("书本")]
    [Tooltip("书本标题（捡到 P_Book 时会用 GGJBookPickup 上填的标题覆盖）")]
    [SerializeField] private string bookTitle = "旅人笔记";
    [Tooltip("书本翻页时还没收集到纸张的提示文字")]
    [TextArea(1, 4)]
    [SerializeField] private string emptyBookText = "还没有收集到任何纸张。\n去旅途中寻找散落的纸张吧。";
    [Tooltip("单张纸阅读界面底部的小提示")]
    [SerializeField] private string noteHintText = "已收入书本 · 按 B 随时翻阅";
    [Tooltip("阅读界面右下角的关闭提示（告诉玩家按 ESC 退出，而不是弹暂停菜单）；留空就不显示")]
    [SerializeField] private string closeHintText = "ESC 关闭";

    [Header("纸张纹理（插槽）")]
    [Tooltip("书本页面的背景纹理（纸张纹理插槽）；留空则用下面的兜底底色")]
    [SerializeField] private Texture2D bookPaperTexture;
    [Tooltip("某张纸自己没配纹理时用的默认纸张纹理（也是便签界面的背景插槽）")]
    [SerializeField] private Texture2D defaultPaperTexture;
    [Tooltip("连默认纹理都没配时的兜底纸张底色")]
    [SerializeField] private Color paperFallbackColor = new Color(0.95f, 0.90f, 0.78f, 1f);

    [Header("界面")]
    [Tooltip("可选：想用项目自带的中文字体就拖一个 Font 进来；留空用 Unity 内置字体")]
    [SerializeField] private Font uiFont;
    [Tooltip("阅读界面画布层级：高于游戏 HUD(100)，低于暂停菜单(1500)")]
    [SerializeField] private int canvasSortingOrder = 800;
    [Tooltip("打开书本 / 纸张时冻结游戏时间（推荐开启，读字时角色和机关都停住）")]
    [SerializeField] private bool freezeTimeWhileOpen = true;
    [Tooltip("背景压暗程度（0~1）")]
    [SerializeField] private float dimAlpha = 0.55f;
    [Tooltip("正文文字颜色（纸张是浅色，所以文字用深色）")]
    [SerializeField] private Color textColor = new Color(0.16f, 0.12f, 0.08f, 1f);

    [Header("正文排版（居中）")]
    [Tooltip("正文水平 + 垂直居中，并根据文字实际行数自动收缩文本框高度，\n" +
             "让整段文字始终落在纸张正中间（文字少也不会顶在左上角）")]
    [SerializeField] private bool centerBodyText = true;
    [Tooltip("正文区域宽度（超出的部分自动换行）")]
    [SerializeField] private float bodyWidth = 1000f;
    [Tooltip("正文区域最大高度：文字再多也不超过这个高度，避免溢出纸张")]
    [SerializeField] private float bodyMaxHeight = 470f;
    [Tooltip("正文基础字号；文字超出纸张时会在 基础字号 ~ 最小字号 之间自动缩小")]
    [SerializeField] private int bodyFontSize = 32;
    [Tooltip("超长文案自动缩字的最小字号（再小看不清就不缩了）")]
    [SerializeField] private int bodyMinFontSize = 14;
    [Tooltip("正文相对纸张中心的上下偏移（负值 = 略微下移，给顶部标题让位）")]
    [SerializeField] private float bodyOffsetY = -10f;

    // ---------------------------------------------------------------- 运行时状态

    private readonly List<PaperEntry> collectedPapers = new List<PaperEntry>();

    /// <summary>是否已经捡到书本（没捡到书就不能翻书，但捡到的纸照样会记下来）。</summary>
    public bool HasBook { get; private set; }

    /// <summary>已收集的纸张（按收集顺序）。</summary>
    public IReadOnlyList<PaperEntry> CollectedPapers => collectedPapers;

    /// <summary>已收集纸张数量。</summary>
    public int PaperCount => collectedPapers.Count;

    // UI
    private GameObject uiRoot;
    private RawImage background;
    private Text titleText;
    private Text bodyText;
    private RectTransform bodyRT;
    private Text pageText;
    private Text closeHintLabel;
    private RectTransform prevButton;
    private RectTransform nextButton;
    private readonly List<UiButton> buttons = new List<UiButton>();

    private bool uiOpen;
    private bool bookMode;
    private int pageIndex;
    private PaperEntry noteEntry;
    private int openedFrame;
    private int closedFrame;
    private bool frozenTime;
    private CursorLockMode prevCursorLock;

#if ENABLE_INPUT_SYSTEM
    private PlayerInput cachedPlayerInput;
#endif
    private bool restorePlayerInput;

    private static readonly Color ButtonNormal = new Color(0.25f, 0.18f, 0.10f, 0.35f);
    private static readonly Color ButtonHover = new Color(0.45f, 0.32f, 0.16f, 0.85f);

    // ---------------------------------------------------------------- 生命周期

    /// <summary>
    /// 确保书本系统存在（首次拾取书本 / 收集纸张时自动创建并常驻，和 GameplayHUD 同一套路）。
    /// 想在 Inspector 里预先配好纹理 / 字体 / 配色，就把一个挂了本脚本的预制体放到
    /// Resources/GGJ_BookSystem.prefab；没有预制体时就用代码里的默认值现场创建。
    /// </summary>
    public static void EnsureCreated()
    {
        if (Instance != null)
            return;

        GameObject prefab = Resources.Load<GameObject>("GGJ_BookSystem");
        GameObject go = prefab != null ? Instantiate(prefab) : new GameObject("GGJ_BookSystem");
        go.name = "GGJ_BookSystem";
        DontDestroyOnLoad(go);

        if (go.GetComponent<GGJBookSystem>() == null)
            go.AddComponent<GGJBookSystem>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
            InteractableObject.SuppressInteractionInput = false;
        }

        if (frozenTime)
        {
            frozenTime = false;
            Time.timeScale = 1f;
        }
    }

    private void Update()
    {
        // 退出正式游玩（回标题等）时把界面收掉，避免残留
        if (!GameplayHUD.Exists && uiOpen)
        {
            CloseUi();
            return;
        }

        if (!uiOpen)
        {
            if (HasBook && WasBookTogglePressed() && CanOpenUi())
                OpenBook();
            return;
        }

        HandleOpenInput();
        UpdatePointer();
    }

    // ---------------------------------------------------------------- 对外接口

    /// <summary>捡到书本：之后按 B 就能翻开（bookTitle 为空表示沿用现有标题）。</summary>
    public void AcquireBook(string title = null)
    {
        HasBook = true;
        if (!string.IsNullOrEmpty(title))
            bookTitle = title;
    }

    /// <summary>
    /// 收录一张纸（同 ID 只收录一次）。
    /// 返回 true = 这是新的一张；false = 之前已经收过。
    /// </summary>
    public bool CollectPaper(PaperEntry entry)
    {
        if (entry == null)
            return false;

        if (string.IsNullOrEmpty(entry.paperId))
            entry.paperId = string.IsNullOrEmpty(entry.title) ? "Paper" : entry.title;

        for (int i = 0; i < collectedPapers.Count; i++)
        {
            PaperEntry existing = collectedPapers[i];
            if (existing != null && existing.paperId == entry.paperId)
                return false;
        }

        collectedPapers.Add(entry);
        return true;
    }

    /// <summary>是否已经收集过某张纸（按 ID 判断）。</summary>
    public bool HasPaper(string paperId)
    {
        if (string.IsNullOrEmpty(paperId))
            return false;

        for (int i = 0; i < collectedPapers.Count; i++)
        {
            PaperEntry e = collectedPapers[i];
            if (e != null && e.paperId == paperId)
                return true;
        }
        return false;
    }

    /// <summary>翻开书本（pageIndex 小于 0 表示停在上次看的那一页）。没捡到书则什么都不做。</summary>
    public void OpenBook(int pageIndex = -1)
    {
        if (!HasBook || !CanOpenUi())
            return;

        EnsureUi();
        bookMode = true;
        noteEntry = null;

        int maxIndex = Mathf.Max(0, collectedPapers.Count - 1);
        this.pageIndex = pageIndex >= 0 ? Mathf.Clamp(pageIndex, 0, maxIndex) : Mathf.Clamp(this.pageIndex, 0, maxIndex);

        RefreshContent();
        OpenUi();
    }

    /// <summary>直接打开某一张纸的阅读界面（不管它在不在书本里都能看）。</summary>
    public void ShowPaperNote(PaperEntry entry)
    {
        if (entry == null || !CanOpenUi())
            return;

        EnsureUi();
        bookMode = false;
        noteEntry = entry;
        RefreshContent();
        OpenUi();
    }

    /// <summary>按 B 的开关入口：没开就翻开，开了就合上。</summary>
    public void ToggleBook()
    {
        if (uiOpen)
            CloseUi();
        else
            OpenBook();
    }

    /// <summary>关闭阅读界面（× 按钮 / ESC / E / B 都走这里）。</summary>
    public void CloseUi()
    {
        if (!uiOpen)
            return;

        uiOpen = false;
        closedFrame = Time.frameCount;
        uiRoot.SetActive(false);
        InteractableObject.SuppressInteractionInput = false;

        if (frozenTime)
        {
            frozenTime = false;
            if (!PauseMenuManager.IsPaused)
                Time.timeScale = 1f;
        }

        RestorePlayerInput();
        ClearPlayerInputs();

        // 暂停菜单也会解锁鼠标：若此时界面是在暂停状态下被关掉的，就别把鼠标锁回去，
        // 否则暂停菜单的鼠标点击会失效（鼠标被锁在屏幕中心）。
        if (!PauseMenuManager.IsPaused)
        {
            Cursor.lockState = prevCursorLock;
            Cursor.visible = prevCursorLock != CursorLockMode.Locked;
        }
    }

    // ---------------------------------------------------------------- 界面开关

    private bool CanOpenUi()
    {
        if (!GameplayHUD.Exists)
            return false;
        if (PauseMenuManager.IsPaused)
            return false;
        if (LevelTransitionZone.AnyTransitionRunning)
            return false;
        if (GameTitleRestart.IsRestartInProgress)
            return false;
        return true;
    }

    private void OpenUi()
    {
        if (uiOpen)
        {
            uiRoot.SetActive(true);
            return;
        }

        uiOpen = true;
        openedFrame = Time.frameCount;
        uiRoot.SetActive(true);

        // 界面开着时屏蔽其它可交互物体的 E，避免按 E 关界面的同时触发旁边物体
        InteractableObject.SuppressInteractionInput = true;

        prevCursorLock = Cursor.lockState;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        ClearPlayerInputs();
        DisablePlayerInput();

        if (freezeTimeWhileOpen && !PauseMenuManager.IsPaused)
        {
            frozenTime = true;
            Time.timeScale = 0f;
        }
    }

    private void HandleOpenInput()
    {
        // 打开界面那一帧的按键（多半就是触发它的 E / B）不参与关闭判定
        if (Time.frameCount <= openedFrame)
            return;

        if (WasClosePressed())
        {
            CloseUi();
            return;
        }

        if (!bookMode || collectedPapers.Count <= 1)
            return;

        if (WasPrevPressed())
        {
            FlipPage(-1);
            return;
        }

        if (WasNextPressed())
            FlipPage(1);
    }

    private void FlipPage(int delta)
    {
        if (collectedPapers.Count == 0)
            return;

        pageIndex = (pageIndex + delta + collectedPapers.Count) % collectedPapers.Count;
        RefreshContent();
    }

    private void RefreshContent()
    {
        if (closeHintLabel != null)
        {
            closeHintLabel.text = closeHintText ?? string.Empty;
            closeHintLabel.gameObject.SetActive(!string.IsNullOrEmpty(closeHintText));
        }

        if (bookMode)
        {
            bool hasAny = collectedPapers.Count > 0;
            prevButton.gameObject.SetActive(hasAny && collectedPapers.Count > 1);
            nextButton.gameObject.SetActive(hasAny && collectedPapers.Count > 1);
            pageText.gameObject.SetActive(hasAny);

            if (!hasAny)
            {
                titleText.text = bookTitle;
                bodyText.text = emptyBookText;
                SetBackground(bookPaperTexture);
                UpdateBodyLayout();
                return;
            }

            PaperEntry entry = collectedPapers[Mathf.Clamp(pageIndex, 0, collectedPapers.Count - 1)];
            titleText.text = string.IsNullOrEmpty(entry.title) ? "无名的纸片" : entry.title;
            bodyText.text = entry.bodyText ?? string.Empty;
            pageText.text = $"{pageIndex + 1} / {collectedPapers.Count}";
            SetBackground(entry.texture != null ? entry.texture : bookPaperTexture);
        }
        else
        {
            prevButton.gameObject.SetActive(false);
            nextButton.gameObject.SetActive(false);
            // 还没捡到书本时就不提示“按 B 翻书”，免得玩家按了没反应
            pageText.gameObject.SetActive(HasBook && !string.IsNullOrEmpty(noteHintText));
            pageText.text = HasBook ? noteHintText : string.Empty;

            PaperEntry entry = noteEntry;
            titleText.text = entry == null || string.IsNullOrEmpty(entry.title) ? "无名的纸片" : entry.title;
            bodyText.text = entry?.bodyText ?? string.Empty;
            SetBackground(entry != null && entry.texture != null ? entry.texture : defaultPaperTexture);
        }

        UpdateBodyLayout();
    }

    /// <summary>
    /// 正文排版：先把文本框按文字实际需要的高度收缩，再靠“锚点在纸张中心”自动居中。
    /// 这样 1 行和 10 行的文字都会整块落在纸张正中间，不会顶在左上角。
    /// </summary>
    private void UpdateBodyLayout()
    {
        if (bodyText == null || bodyRT == null)
            return;

        // 每次排版都先恢复基础字号（短文案不会被上一张超长纸的小字号连累）
        bodyText.fontSize = bodyFontSize;

        if (!centerBodyText)
        {
            bodyText.alignment = TextAnchor.UpperLeft;
            bodyRT.sizeDelta = new Vector2(bodyWidth, bodyMaxHeight);
            return;
        }

        bodyText.alignment = TextAnchor.MiddleCenter;

        // 先用固定宽度量出这段文字真正需要多高（preferredHeight 会按当前宽度自动换行计算）
        bodyRT.sizeDelta = new Vector2(bodyWidth, bodyMaxHeight);
        bodyRT.ForceUpdateRectTransforms();
        float preferred = bodyText.preferredHeight;

        // 超长文案：按“需要高度 / 可用高度”的比例把字号缩到刚好放得下为止
        if (preferred > bodyMaxHeight && bodyFontSize > bodyMinFontSize)
        {
            float scale = bodyMaxHeight / preferred;
            int shrunk = Mathf.Clamp(Mathf.FloorToInt(bodyFontSize * scale), bodyMinFontSize, bodyFontSize);
            bodyText.fontSize = shrunk;
            preferred = bodyText.preferredHeight;
        }

        // 夹在 [一行, 最大高度] 之间：短文字收缩后居中，超长文字也不至于溢出纸张
        float minHeight = bodyText.fontSize * bodyText.lineSpacing;
        float height = Mathf.Clamp(preferred, minHeight, bodyMaxHeight);
        bodyRT.sizeDelta = new Vector2(bodyWidth, height);
    }

    private void SetBackground(Texture2D texture)
    {
        background.texture = texture;
        background.color = texture != null ? Color.white : paperFallbackColor;
    }

    // ---------------------------------------------------------------- 界面构建

    private void EnsureUi()
    {
        if (uiRoot != null)
            return;

        uiRoot = CreateRect("BookCanvas", transform).gameObject;
        Canvas canvas = uiRoot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = canvasSortingOrder;

        CanvasScaler scaler = uiRoot.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        uiRoot.AddComponent<GraphicRaycaster>();

        // —— 背景压暗（挡住游戏画面并吃掉点击） ——
        RectTransform dim = CreateRect("Dim", uiRoot.transform);
        Stretch(dim);
        Image dimImage = dim.gameObject.AddComponent<Image>();
        dimImage.color = new Color(0f, 0f, 0f, dimAlpha);
        dimImage.raycastTarget = true;

        // —— 纸张面板（背景就是纸张纹理插槽） ——
        RectTransform panel = CreateRect("PaperPanel", uiRoot.transform);
        CenterInPanel(panel);
        panel.sizeDelta = new Vector2(1180f, 720f);
        background = panel.gameObject.AddComponent<RawImage>();
        background.raycastTarget = true;
        Outline panelOutline = panel.gameObject.AddComponent<Outline>();
        panelOutline.effectColor = new Color(0.30f, 0.22f, 0.10f, 0.9f);
        panelOutline.effectDistance = new Vector2(3f, -3f);

        // 标题
        RectTransform titleRT = CreateRect("Title", panel);
        SetTopCenter(titleRT);
        titleRT.anchoredPosition = new Vector2(0f, -54f);
        titleRT.sizeDelta = new Vector2(1000f, 82f);
        titleText = titleRT.gameObject.AddComponent<Text>();
        titleText.font = GetFont();
        titleText.fontSize = 46;
        titleText.fontStyle = FontStyle.Bold;
        titleText.color = textColor;
        titleText.alignment = TextAnchor.MiddleCenter;
        titleText.horizontalOverflow = HorizontalWrapMode.Wrap;
        titleText.raycastTarget = false;

        // 正文：锚点在纸张中心，水平 / 垂直都居中，高度按文字自动收缩（见 UpdateBodyLayout）
        bodyRT = CreateRect("Body", panel);
        CenterInPanel(bodyRT);
        bodyRT.anchoredPosition = new Vector2(0f, bodyOffsetY);
        bodyRT.sizeDelta = new Vector2(bodyWidth, bodyMaxHeight);
        bodyText = bodyRT.gameObject.AddComponent<Text>();
        bodyText.font = GetFont();
        bodyText.fontSize = bodyFontSize;
        bodyText.color = textColor;
        bodyText.alignment = TextAnchor.MiddleCenter;
        bodyText.horizontalOverflow = HorizontalWrapMode.Wrap;
        bodyText.verticalOverflow = VerticalWrapMode.Truncate;
        bodyText.lineSpacing = 1.25f;
        bodyText.raycastTarget = false;

        // 页码 / 提示
        RectTransform pageRT = CreateRect("PageIndex", panel);
        CenterInPanel(pageRT);
        pageRT.anchoredPosition = new Vector2(0f, -292f);
        pageRT.sizeDelta = new Vector2(420f, 56f);
        pageText = pageRT.gameObject.AddComponent<Text>();
        pageText.font = GetFont();
        pageText.fontSize = 28;
        pageText.color = new Color(textColor.r, textColor.g, textColor.b, 0.75f);
        pageText.alignment = TextAnchor.MiddleCenter;
        pageText.raycastTarget = false;

        // 关闭提示（右下角：ESC 关闭）
        RectTransform closeHintRT = CreateRect("CloseHint", panel);
        closeHintRT.anchorMin = closeHintRT.anchorMax = closeHintRT.pivot = new Vector2(1f, 0f);
        closeHintRT.anchoredPosition = new Vector2(-44f, 26f);
        closeHintRT.sizeDelta = new Vector2(320f, 44f);
        closeHintLabel = closeHintRT.gameObject.AddComponent<Text>();
        closeHintLabel.font = GetFont();
        closeHintLabel.fontSize = 24;
        closeHintLabel.color = new Color(textColor.r, textColor.g, textColor.b, 0.55f);
        closeHintLabel.alignment = TextAnchor.MiddleRight;
        closeHintLabel.raycastTarget = false;

        // 翻页 / 关闭
        prevButton = CreateTextButton(panel, "PrevPage", "‹", new Vector2(-220f, -292f), new Vector2(84f, 84f), () => FlipPage(-1));
        nextButton = CreateTextButton(panel, "NextPage", "›", new Vector2(220f, -292f), new Vector2(84f, 84f), () => FlipPage(1));

        RectTransform closeRT = CreateRect("CloseButton", panel);
        closeRT.anchorMin = closeRT.anchorMax = closeRT.pivot = new Vector2(1f, 1f);
        closeRT.anchoredPosition = new Vector2(-36f, -32f);
        closeRT.sizeDelta = new Vector2(72f, 72f);
        Image closeImage = closeRT.gameObject.AddComponent<Image>();
        closeImage.color = ButtonNormal;
        closeImage.raycastTarget = true;
        RectTransform closeLabel = CreateRect("Label", closeRT);
        Stretch(closeLabel);
        Text closeText = closeLabel.gameObject.AddComponent<Text>();
        closeText.font = GetFont();
        closeText.fontSize = 46;
        closeText.fontStyle = FontStyle.Bold;
        closeText.color = Color.white;
        closeText.alignment = TextAnchor.MiddleCenter;
        closeText.text = "×";
        closeText.raycastTarget = false;
        buttons.Add(new UiButton { Rect = closeRT, Image = closeImage, Normal = ButtonNormal, Hover = ButtonHover, OnClick = CloseUi });

        uiRoot.SetActive(false);
    }

    private RectTransform CreateTextButton(RectTransform parent, string name, string label, Vector2 pos, Vector2 size, Action onClick)
    {
        RectTransform rt = CreateRect(name, parent);
        CenterInPanel(rt);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;

        Image image = rt.gameObject.AddComponent<Image>();
        image.color = ButtonNormal;
        image.raycastTarget = true;

        RectTransform labelRT = CreateRect("Label", rt);
        Stretch(labelRT);
        Text text = labelRT.gameObject.AddComponent<Text>();
        text.font = GetFont();
        text.fontSize = Mathf.RoundToInt(size.y * 0.66f);
        text.fontStyle = FontStyle.Bold;
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleCenter;
        text.text = label;
        text.raycastTarget = false;

        buttons.Add(new UiButton { Rect = rt, Image = image, Normal = ButtonNormal, Hover = ButtonHover, OnClick = onClick });
        return rt;
    }

    /// <summary>项目里没有 EventSystem，按钮命中与 hover 由这里手动判定（与暂停菜单同一套路）。</summary>
    private void UpdatePointer()
    {
        Vector2 pointer = GetPointerPosition();

        for (int i = 0; i < buttons.Count; i++)
        {
            UiButton button = buttons[i];
            if (button.Rect == null || button.Image == null)
                continue;

            bool active = button.Rect.gameObject.activeInHierarchy;
            bool over = active && RectTransformUtility.RectangleContainsScreenPoint(button.Rect, pointer, null);

            if (over == button.IsHovered)
                continue;

            button.IsHovered = over;
            button.Image.color = over ? button.Hover : button.Normal;
        }

        if (!WasLeftClickPressedThisFrame())
            return;

        for (int i = 0; i < buttons.Count; i++)
        {
            UiButton button = buttons[i];
            if (button.IsHovered && button.OnClick != null)
            {
                button.OnClick();
                return;
            }
        }
    }

    // ---------------------------------------------------------------- 玩家输入

    private void DisablePlayerInput()
    {
        ThirdPersonController player = ThirdPersonController.Instance;
        if (player == null)
            return;

#if ENABLE_INPUT_SYSTEM
        PlayerInput playerInput = player.GetComponent<PlayerInput>();
        cachedPlayerInput = playerInput;
        if (playerInput != null)
        {
            restorePlayerInput = playerInput.enabled;
            if (playerInput.enabled)
                playerInput.enabled = false;
        }
#endif
    }

    private void RestorePlayerInput()
    {
#if ENABLE_INPUT_SYSTEM
        if (cachedPlayerInput != null && restorePlayerInput)
            cachedPlayerInput.enabled = true;
        cachedPlayerInput = null;
#endif
        restorePlayerInput = false;
    }

    /// <summary>清空移动输入，避免界面打开瞬间角色还按着上一次的方向继续跑。</summary>
    private static void ClearPlayerInputs()
    {
        ThirdPersonController player = ThirdPersonController.Instance;
        if (player == null)
            return;

        StarterAssetsInputs inputs = player.GetComponent<StarterAssetsInputs>();
        if (inputs == null)
            return;

        inputs.MoveInput(Vector2.zero);
        inputs.LookInput(Vector2.zero);
        inputs.JumpInput(false);
        inputs.SprintInput(false);
    }

    // ---------------------------------------------------------------- 按键

    private static bool WasBookTogglePressed()
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        return keyboard != null && keyboard.bKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.B);
#endif
    }

    private static bool WasClosePressed()
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        return keyboard != null &&
               (keyboard.escapeKey.wasPressedThisFrame ||
                keyboard.eKey.wasPressedThisFrame ||
                keyboard.bKey.wasPressedThisFrame);
#else
        return Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.B);
#endif
    }

    private static bool WasPrevPressed()
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        return keyboard != null &&
               (keyboard.leftArrowKey.wasPressedThisFrame || keyboard.aKey.wasPressedThisFrame);
#else
        return Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A);
#endif
    }

    private static bool WasNextPressed()
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        return keyboard != null &&
               (keyboard.rightArrowKey.wasPressedThisFrame || keyboard.dKey.wasPressedThisFrame);
#else
        return Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D);
#endif
    }

    private static Vector2 GetPointerPosition()
    {
#if ENABLE_INPUT_SYSTEM
        Mouse mouse = Mouse.current;
        return mouse != null ? mouse.position.ReadValue() : Vector2.zero;
#else
        return Input.mousePosition;
#endif
    }

    private static bool WasLeftClickPressedThisFrame()
    {
#if ENABLE_INPUT_SYSTEM
        Mouse mouse = Mouse.current;
        return mouse != null && mouse.leftButton.wasPressedThisFrame;
#else
        return Input.GetMouseButtonDown(0);
#endif
    }

    // ---------------------------------------------------------------- UI 工具

    private Font GetFont()
    {
        if (uiFont != null)
            return uiFont;

        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        return font != null ? font : Resources.GetBuiltinResource<Font>("Arial.ttf");
    }

    private static RectTransform CreateRect(string name, Transform parent)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go.GetComponent<RectTransform>();
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private static void CenterInPanel(RectTransform rt)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
    }

    private static void SetTopCenter(RectTransform rt)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
    }

    private sealed class UiButton
    {
        public RectTransform Rect;
        public Image Image;
        public Color Normal;
        public Color Hover;
        public Action OnClick;
        public bool IsHovered;
    }
}
