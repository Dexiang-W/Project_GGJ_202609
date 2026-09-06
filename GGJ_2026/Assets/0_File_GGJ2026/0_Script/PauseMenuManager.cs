using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using StarterAssets;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// 暂停菜单（按 ESC / 手柄 Start 呼出）：
///   · 正式游玩中：提供「继续游戏 / 返回主菜单 / 退出游戏」；
///   · 标题界面（GameFlowController 处于 Attract）：只显示「继续游戏 / 退出游戏」，方便退回桌面。
///
/// 与 GameplayHUD 一样是运行时自动创建、跨场景常驻（DontDestroyOnLoad）的单例。
/// 关卡切换 / 返回标题的清理过程中不会被误触发。
///
/// 设计要点：
///   · 用 Time.timeScale = 0 冻结游戏，并停用玩家输入 / 解锁鼠标（你可在菜单里点按钮）；
///   · 点击命中由本脚本直接对屏幕坐标做矩形判断（含 hover 高亮），不依赖场景里的 EventSystem，
///     因此无论关卡里有没有 UI 事件系统都能正常工作；
///   · 返回主菜单复用了 GameTitleRestart.ReturnToTitle，它会清掉所有常驻残留再全新加载标题场景；
///   · 暂停期间暂停音乐（AudioListener.pause），继续后恢复。
/// </summary>
[DisallowMultipleComponent]
public class PauseMenuManager : MonoBehaviour
{
    /// <summary>当前暂停菜单实例。</summary>
    public static PauseMenuManager Instance { get; private set; }

    /// <summary>当前是否处于暂停状态（供 GameplayHUD 等其它逻辑判断，例如暂停时不消耗能量）。</summary>
    public static bool IsPaused { get; private set; }

    /// <summary>标题界面「继续游戏」刚点完的一小段时间内禁止被当成“点击开始”（避免同一帧误开游戏）。</summary>
    public static float BlockTitleStartUntil { get; private set; }

    [Header("返回主菜单加载的标题场景名（须在 Build Settings 中，项目默认 Begin_Menu）")]
    [SerializeField] private string titleSceneName = "Begin_Menu";

    [Header("界面")]
    [Tooltip("菜单画布的排序层级：低于开场黑幕(2000)，高于游戏 HUD(100) 与结局报幕(1000)")]
    [SerializeField] private int canvasSortingOrder = 1500;

    [Tooltip("半透明遮罩按压透明度（0~1）")]
    [SerializeField] private float dimAlpha = 0.62f;

    // ---------------------------------------------------------------- 运行时状态

    /// <summary>暂停菜单在当前场景下的形态。</summary>
    private enum MenuContext
    {
        /// <summary>当前场景不允许呼出（例如空场景 / 正在加载）。</summary>
        None,

        /// <summary>标题界面形态：只显示「继续游戏 / 退出游戏」。</summary>
        TitleScreen,

        /// <summary>正式游玩形态：显示「继续游戏 / 返回主菜单 / 退出游戏」。</summary>
        InGame,
    }

    private GameObject menuRoot;
    private readonly List<MenuButtonEntry> buttons = new List<MenuButtonEntry>();

    private bool inputDisabledByPause;
    private CursorLockMode prevCursorLock;

    /// <summary>当前已按哪种形态构建的菜单（None 表示还没构建过）。</summary>
    private MenuContext builtContext;

    private sealed class MenuButtonEntry
    {
        public RectTransform Rect;
        public Image Image;
        public Color Normal;
        public Color Hover;
        public Action OnClick;
        public bool IsHovered;
    }

    // ---------------------------------------------------------------- 启动 / 常驻

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        EnsureCreated();
        // 之后每次场景加载（含“回标题重开”后）都确保存在常驻实例
        SceneManager.sceneLoaded += (scene, mode) =>
        {
            EnsureCreated();
            if (Instance != null)
                Instance.HandleSceneLoaded();
        };
    }

    public static void EnsureCreated()
    {
        if (Instance != null)
            return;

        GameObject go = new GameObject("GGJ_PauseMenu");
        DontDestroyOnLoad(go);
        go.AddComponent<PauseMenuManager>();
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

    /// <summary>场景发生切换时的兜底：万一菜单开着就切走了（异常/调试加载），立刻复位暂停态，避免 timeScale 卡 0。</summary>
    private void HandleSceneLoaded()
    {
        if (IsPaused)
            SetPaused(false, leaveCursorUnlocked: true);
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        if (IsPaused)
        {
            IsPaused = false;
            Time.timeScale = 1f;
            AudioListener.pause = false;
        }
    }

    private void Update()
    {
        if (!IsPaused)
        {
            // 打开菜单：正式游玩中允许手柄 Start；标题界面只接受 ESC（标题里手柄 Start 是“开始游戏”）
            bool allowGamepad = GetContext() == MenuContext.InGame;
            if (WasTogglePressedThisFrame(allowGamepad))
                TryPause();
            return;
        }

        // 已暂停：ESC 或手柄 Start 都能恢复
        if (WasTogglePressedThisFrame(true))
            ResumeGame();

        UpdateMenuPointer();
    }

    // ---------------------------------------------------------------- 暂停 / 恢复

    private void TryPause()
    {
        if (IsPaused)
            return;

        // 转场 / 回标题过程中不响应暂停
        if (LevelTransitionZone.AnyTransitionRunning || GameTitleRestart.IsRestartInProgress)
            return;

        MenuContext context = GetContext();
        if (context == MenuContext.None)
            return;

        // 场景形态变了（标题 ↔ 关卡）就按当前形态重建布局（按钮数量不同）
        if (context != builtContext)
            RebuildMenu(context);

        OpenMenu();
    }

    /// <summary>根据当前场景判定菜单形态：标题界面只给两颗按钮；空场景/加载中不给。</summary>
    private static MenuContext GetContext()
    {
        GameFlowController flow = FindObjectOfType<GameFlowController>();
        if (flow != null)
        {
            // 标题场景的 GameFlowController 处于 Attract（点击开始前）；若它已切到 Playing 则视为正式游玩
            return flow.CurrentState == GameFlowController.GameFlowState.Playing
                ? MenuContext.InGame
                : MenuContext.TitleScreen;
        }

        // 没有标题流程控制器时：
        if (ThirdPersonController.Instance != null)
            return MenuContext.InGame; // 单独直接 Play 某个关卡，有玩家就算游玩中

        // 既没流程也没玩家，但有开始界面 UI → 也算标题界面（部分场景没有 GameFlowController）
        return TitleScreenUI.Instance != null ? MenuContext.TitleScreen : MenuContext.None;
    }

    /// <summary>按指定形态重建整个菜单（不同形态按钮数量不同，布局尺寸也会跟着变）。</summary>
    private void RebuildMenu(MenuContext context)
    {
        if (menuRoot != null)
            Destroy(menuRoot);

        buttons.Clear();
        menuRoot = null;
        builtContext = MenuContext.None;

        BuildMenu(context);
        builtContext = context;
    }

    private void OpenMenu()
    {
        IsPaused = true;

        prevCursorLock = Cursor.lockState;

        // 清空玩家输入并停用 PlayerInput（否则暂停时鼠标/键盘仍会改镜头或触发动作）
        ClearPlayerInputs();
        DisablePlayerInput(true);

        Time.timeScale = 0f;
        AudioListener.pause = true;

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        // 标题界面呼出菜单时把“游戏名 / Click To Start”文字先藏起来，避免和面板互相遮挡
        if (builtContext == MenuContext.TitleScreen)
        {
            TitleScreenUI ui = TitleScreenUI.Instance;
            if (ui != null)
                ui.SetTextVisible(false);
        }

        SetMenuVisible(true);
        ResetButtonVisuals();
    }

    /// <summary>继续游戏（从暂停恢复）。</summary>
    public void ResumeGame()
    {
        SetPaused(false, leaveCursorUnlocked: false);
    }

    /// <summary>返回主菜单（先解除暂停，再走标准的“清常驻 → 重新加载标题”流程）。</summary>
    public void ReturnToMainMenu()
    {
        SetPaused(false, leaveCursorUnlocked: true);
        GameTitleRestart.ReturnToTitle(titleSceneName);
    }

    /// <summary>退出游戏。</summary>
    public void QuitGame()
    {
        SetPaused(false, leaveCursorUnlocked: true);

#if UNITY_EDITOR
        Debug.Log("[PauseMenuManager] 已请求退出游戏（编辑器内不生效）。", this);
#else
        Application.Quit();
#endif
    }

    private void SetPaused(bool paused, bool leaveCursorUnlocked)
    {
        if (!paused)
        {
            IsPaused = false;

            // 标题界面恢复时把藏掉的文字还回来；并短暂屏蔽“点击开始”（防止同一帧点“继续游戏”被当成开局）
            if (builtContext == MenuContext.TitleScreen)
            {
                BlockTitleStartUntil = Time.unscaledTime + 0.35f;
                TitleScreenUI ui = TitleScreenUI.Instance;
                if (ui != null)
                    ui.SetTextVisible(true);
            }

            SetMenuVisible(false);
            ResetButtonVisuals();

            Time.timeScale = 1f;
            AudioListener.pause = false;

            DisablePlayerInput(false);
            Cursor.lockState = leaveCursorUnlocked ? CursorLockMode.None : prevCursorLock;
            Cursor.visible = leaveCursorUnlocked || Cursor.lockState == CursorLockMode.None;
        }
    }

    private void ClearPlayerInputs()
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

    private void DisablePlayerInput(bool disable)
    {
        ThirdPersonController player = ThirdPersonController.Instance;
        if (player == null)
            return;

#if ENABLE_INPUT_SYSTEM
        PlayerInput playerInput = player.GetComponent<PlayerInput>();
        if (playerInput == null)
            return;

        if (disable)
        {
            inputDisabledByPause = playerInput.enabled;
            if (playerInput.enabled)
                playerInput.enabled = false;
        }
        else if (inputDisabledByPause)
        {
            playerInput.enabled = true;
            inputDisabledByPause = false;
        }
#endif
    }

    // ---------------------------------------------------------------- 菜单指针交互

    private void UpdateMenuPointer()
    {
        Vector2 pointer = GetPointerPosition();

        for (int i = 0; i < buttons.Count; i++)
        {
            MenuButtonEntry btn = buttons[i];
            bool over = btn.Rect != null &&
                        RectTransformUtility.RectangleContainsScreenPoint(btn.Rect, pointer, null);

            if (over != btn.IsHovered)
            {
                btn.IsHovered = over;
                btn.Image.color = over ? btn.Hover : btn.Normal;
            }
        }

        if (!WasLeftClickPressedThisFrame())
            return;

        for (int i = 0; i < buttons.Count; i++)
        {
            MenuButtonEntry btn = buttons[i];
            if (btn.IsHovered && btn.OnClick != null)
            {
                btn.OnClick();
                return; // 只处理一个按钮，避免同帧连锁
            }
        }
    }

    private void ResetButtonVisuals()
    {
        for (int i = 0; i < buttons.Count; i++)
        {
            MenuButtonEntry btn = buttons[i];
            btn.IsHovered = false;
            if (btn.Image != null)
                btn.Image.color = btn.Normal;
        }
    }

    // ---------------------------------------------------------------- 幻出/幻入

    private void BuildMenu(MenuContext ctx)
    {
        bool isTitle = ctx == MenuContext.TitleScreen;
        int buttonCount = isTitle ? 2 : 3;

        // —— 尺寸（参考分辨率 1920x1080 的“像素”，由 CanvasScaler 统一缩放）——
        const float panelW = 620f;     // 面板宽度
        const float topPad = 70f;      // 顶部留白
        const float titleH = 84f;      // 标题高
        const float gapTitle = 40f;    // 标题与按钮的间距
        const float buttonH = 96f;     // 按钮高
        const float buttonGap = 24f;   // 按钮间距
        const float gapToHint = 36f;   // 最后按钮与提示文字间距
        const float hintH = 42f;       // 提示文字高
        const float bottomPad = 40f;   // 底部留白

        // 面板高度按内容计算：标题、全部按钮、提示文字都被包在框内
        float panelH = topPad + titleH + gapTitle
                     + buttonCount * buttonH + (buttonCount - 1) * buttonGap
                     + gapToHint + hintH + bottomPad;
        float halfH = panelH * 0.5f;

        // 按钮区顶界（相对面板中心的 Y，向上为正）
        float buttonsTop = halfH - topPad - titleH - gapTitle;

        // —— 容器（挂 Canvas 的根，只有这一层会在暂停时被激活/隐藏）——
        GameObject canvasGO = new GameObject("MenuCanvas", typeof(RectTransform));
        canvasGO.transform.SetParent(transform, false);

        Canvas canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = canvasSortingOrder;

        CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        canvasGO.AddComponent<GraphicRaycaster>();

        Transform root = canvasGO.transform;

        // —— 全屏压暗遮罩（挡住下方游戏并拦截点击） ——
        RectTransform dim = CreateRect("Dim", root);
        Stretch(dim);
        Image dimImage = dim.gameObject.AddComponent<Image>();
        dimImage.color = new Color(0f, 0f, 0f, dimAlpha);
        dimImage.raycastTarget = true;

        // —— 中央面板 ——
        RectTransform panel = CreateRect("Panel", root);
        CenterInPanel(panel);
        panel.sizeDelta = new Vector2(panelW, panelH);
        Image panelImage = panel.gameObject.AddComponent<Image>();
        panelImage.color = new Color(0.05f, 0.06f, 0.10f, 0.97f);
        panelImage.raycastTarget = true;

        // 面板描边
        Outline panelOutline = panel.gameObject.AddComponent<Outline>();
        panelOutline.effectColor = new Color(1f, 1f, 1f, 0.12f);
        panelOutline.effectDistance = new Vector2(2f, -2f);

        // —— 标题（位于面板顶部留白之下） ——
        RectTransform title = CreateRect("Title", panel);
        CenterInPanel(title);
        title.anchoredPosition = new Vector2(0f, buttonsTop + gapTitle + titleH * 0.5f);
        title.sizeDelta = new Vector2(panelW - 60f, titleH);
        Text titleText = title.gameObject.AddComponent<Text>();
        titleText.font = GetDefaultFont();
        titleText.fontSize = 50;
        titleText.fontStyle = FontStyle.Bold;
        titleText.alignment = TextAnchor.MiddleCenter;
        titleText.color = new Color(0.92f, 0.96f, 1f, 1f);
        titleText.text = isTitle ? "菜单" : "游戏已暂停";
        titleText.raycastTarget = false;
        AddOutline(title.gameObject, new Color(0f, 0f, 0f, 0.8f), 3f);

        // —— 按钮（从标题下方逐颗往下排） ——
        const float btnW = panelW - 130f;
        List<ButtonSpec> specs = new List<ButtonSpec>();
        if (isTitle)
        {
            specs.Add(new ButtonSpec("继续游戏", new Color(0.14f, 0.38f, 0.26f, 1f), new Color(0.20f, 0.58f, 0.40f, 1f), ResumeGame));
            specs.Add(new ButtonSpec("退出游戏", new Color(0.52f, 0.16f, 0.16f, 1f), new Color(0.78f, 0.24f, 0.24f, 1f), QuitGame));
        }
        else
        {
            specs.Add(new ButtonSpec("继续游戏", new Color(0.16f, 0.30f, 0.52f, 1f), new Color(0.26f, 0.47f, 0.80f, 1f), ResumeGame));
            specs.Add(new ButtonSpec("返回主菜单", new Color(0.22f, 0.24f, 0.34f, 1f), new Color(0.34f, 0.38f, 0.54f, 1f), ReturnToMainMenu));
            specs.Add(new ButtonSpec("退出游戏", new Color(0.52f, 0.16f, 0.16f, 1f), new Color(0.78f, 0.24f, 0.24f, 1f), QuitGame));
        }

        for (int i = 0; i < specs.Count; i++)
        {
            float cy = buttonsTop - buttonH * 0.5f - i * (buttonH + buttonGap);
            CreateButton(panel, specs[i].Label, btnW, buttonH, new Vector2(0f, cy),
                specs[i].Normal, specs[i].Hover, specs[i].OnClick);
        }

        // —— 底部提示 ——
        RectTransform hint = CreateRect("Hint", panel);
        CenterInPanel(hint);
        hint.anchoredPosition = new Vector2(0f, -halfH + bottomPad + hintH * 0.5f);
        hint.sizeDelta = new Vector2(panelW - 60f, hintH);
        Text hintText = hint.gameObject.AddComponent<Text>();
        hintText.font = GetDefaultFont();
        hintText.fontSize = 26;
        hintText.alignment = TextAnchor.MiddleCenter;
        hintText.color = new Color(1f, 1f, 1f, 0.62f);
        hintText.text = "按 ESC 返回游戏";
        hintText.raycastTarget = false;

        menuRoot = canvasGO;
    }

    private void CreateButton(RectTransform panel, string label, float width, float height,
        Vector2 pos, Color normal, Color hover, Action onClick)
    {
        RectTransform rt = CreateRect("Button_" + label, panel);
        CenterInPanel(rt);
        rt.anchoredPosition = pos;
        rt.sizeDelta = new Vector2(width, height);

        Image image = rt.gameObject.AddComponent<Image>();
        image.color = normal;
        image.raycastTarget = true;

        // 按钮文字
        RectTransform labelRT = CreateRect("Label", rt);
        Stretch(labelRT);
        Text text = labelRT.gameObject.AddComponent<Text>();
        text.font = GetDefaultFont();
        text.fontSize = 40;
        text.fontStyle = FontStyle.Bold;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.text = label;
        text.raycastTarget = false;
        AddOutline(labelRT.gameObject, new Color(0f, 0f, 0f, 0.5f), 2f);

        buttons.Add(new MenuButtonEntry
        {
            Rect = rt,
            Image = image,
            Normal = normal,
            Hover = hover,
            OnClick = onClick
        });
    }

    private sealed class ButtonSpec
    {
        public readonly string Label;
        public readonly Color Normal;
        public readonly Color Hover;
        public readonly Action OnClick;

        public ButtonSpec(string label, Color normal, Color hover, Action onClick)
        {
            Label = label;
            Normal = normal;
            Hover = hover;
            OnClick = onClick;
        }
    }

    // ---------------------------------------------------------------- UI 工具

    private void SetMenuVisible(bool visible)
    {
        if (menuRoot != null)
            menuRoot.SetActive(visible);
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
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    /// <summary>把元素锚到父级中心（用于以父中心为原点做绝对定位）。</summary>
    private static void CenterInPanel(RectTransform rt)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
    }

    private static void AddOutline(GameObject target, Color color, float distance)
    {
        Outline outline = target.AddComponent<Outline>();
        outline.effectColor = color;
        outline.effectDistance = new Vector2(distance, -distance);
    }

    private static Font GetDefaultFont()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return font;
    }

    // ---------------------------------------------------------------- 输入读取

    private static bool WasTogglePressedThisFrame(bool gamepadEnabled)
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            return true;

        if (gamepadEnabled)
        {
            Gamepad gamepad = Gamepad.current;
            if (gamepad != null && gamepad.startButton.wasPressedThisFrame)
                return true;
        }

        return false;
#else
        return Input.GetKeyDown(KeyCode.Escape) ||
               (gamepadEnabled && Input.GetKeyDown(KeyCode.P));
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
}
