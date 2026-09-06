using System;
using System.Collections;
using System.Collections.Generic;
using StarterAssets;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 正式游玩期间（Level1）的通用游戏 UI（运行时自动创建，不依赖场景里预先摆 Canvas）：
///   1. 左上角能量格：三格绿色圆点（空=浅绿，满=亮绿），Q 键消耗一格 / EnergyPickup 触碰增加一格；
///   2. 全屏黑幕：供“触碰 Volume → 渐黑 → 传送回重生点”这类流程淡入淡出；
///   3. 黑幕期间在屏幕中部偏左显示的提示文字（每个区域可单独配置文字）。
///
/// 该对象只会在正式关卡（Level1）里出现：正常标题流程由 GameFlowController 创建；
/// 直接单独 Play Level1 测试时也有兜底自动创建（见 BootstrapDirectLevelHud），因此标题阶段（Bandeng_Test）不会显示，
/// 而正式游玩一开始左上角就能看到三格空能量（浅绿圆点），无需先碰到能量拾取物。
/// 其它组件请统一通过 Instance 访问，例如：
///     GameplayHUD.Instance.AddEnergy(1);
///     yield return GameplayHUD.Instance.FadeToBlackRoutine(0.8f);
/// </summary>
[DisallowMultipleComponent]
public class GameplayHUD : MonoBehaviour
{
    public const int MaxEnergy = 3;

    private static GameplayHUD _instance;

    /// <summary>当前能量值（0~3）。</summary>
    public int CurrentEnergy { get; private set; }

    /// <summary>能量变化（新增/消耗）时触发，参数为变化后的能量值。供其他系统订阅做提示。</summary>
    public event Action<int> EnergyChanged;

    private Image blackImage;
    private Text messageText;
    private Outline messageOutline;
    private Coroutine messageFadeRoutine;

    [Header("黑幕提示文字淡入淡出")]
    [Tooltip("提示文字淡入时长（秒），0 表示直接出现")]
    [SerializeField] private float messageFadeInSeconds = 0.45f;
    [Tooltip("提示文字淡隐时长（秒），0 表示直接消失")]
    [SerializeField] private float messageFadeOutSeconds = 0.45f;

    private readonly List<Image> energyDots = new List<Image>();

    // 三格圆点的颜色：空槽 = 半透明“枯灰绿”（明显暗，像没通电的插座）；
    // 满格 = 高饱和亮绿（发光的能量点）。两者拉开明暗与饱和度，一眼就能区分。
    private static readonly Color EmptyDotColor = new Color(0.33f, 0.42f, 0.36f, 0.75f);
    private static readonly Color FullDotColor = new Color(0.15f, 1f, 0.25f, 1f);

    public static GameplayHUD Instance
    {
        get
        {
            if (_instance == null)
                EnsureCreated();
            return _instance;
        }
    }

    /// <summary>确保 HUD 存在（Level1 场景一加载，各触发区/拾取物就会调用它）。</summary>
    public static void EnsureCreated()
    {
        if (_instance != null)
            return;

        GameObject go = new GameObject("GGJ_GameplayHUD");
        DontDestroyOnLoad(go);
        go.AddComponent<GameplayHUD>();

        // HUD 只在“正式游玩”出现 → 这里顺带启动音频：BGM 全程 + Level1 环境音
        AudioManager.EnsureCreated();
        if (AudioManager.Instance != null)
            AudioManager.Instance.BeginLevelAudio();
    }

    /// <summary>
    /// 兜底：直接单独打开某个关卡（如 Play Level1 测试，场景里没有 GameFlowController 的标题流程）时，
    /// 保证正式游玩一开始左上角就有能量三格（空能量）显示，而不是等碰到能量拾取物才出现。
    /// 正常从标题流程进入时 GameFlowController 已存在，由它在 FinishSpawn 里创建，这里会自动跳过，不会重复。
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void BootstrapDirectLevelHud()
    {
        TryCreateForDirectLevelPlay();

        // 之后每次场景加载都检查一次（覆盖“切到正式关卡”等动态加载路径）
        SceneManager.sceneLoaded += (scene, mode) => TryCreateForDirectLevelPlay();
    }

    private static bool TryCreateForDirectLevelPlay()
    {
        if (_instance != null)
            return true;

        // 存在游戏流程控制器（标题场景 / 由标题流程带进关卡）→ 由 GameFlowController 负责创建
        if (FindObjectOfType<GameFlowController>() != null)
            return false;

        // 独立测试关卡：场景里有玩家（ThirdPersonController）才代表处于正式游玩环境
        if (FindObjectOfType<ThirdPersonController>() == null)
            return false;

        EnsureCreated();
        return true;
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        BuildUI();
        RefreshEnergyDots();
    }

    private void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
    }

    private void Update()
    {
        // 暂停菜单弹出期间不消耗能量
        if (PauseMenuManager.IsPaused) return;

        // Q 键消耗一格能量（无能量时忽略）；成功消耗播放 SFX_Energy_Return
        if (WasQPressedThisFrame() && CurrentEnergy > 0)
        {
            SpendEnergy(1);
            if (AudioManager.Instance != null)
                AudioManager.Instance.PlayEnergyReturn();
        }
    }

    // ---------------------------------------------------------------- 对外接口

    /// <summary>增加能量（上限 MaxEnergy）。</summary>
    public void AddEnergy(int amount)
    {
        if (amount <= 0)
            return;

        int before = CurrentEnergy;
        CurrentEnergy = Mathf.Clamp(CurrentEnergy + amount, 0, MaxEnergy);
        if (CurrentEnergy != before)
        {
            RefreshEnergyDots();
            EnergyChanged?.Invoke(CurrentEnergy);
        }
    }

    /// <summary>消耗能量；不足时返回 false 且不扣除。</summary>
    public bool SpendEnergy(int amount)
    {
        if (amount <= 0 || CurrentEnergy < amount)
            return false;

        CurrentEnergy -= amount;
        RefreshEnergyDots();
        EnergyChanged?.Invoke(CurrentEnergy);
        return true;
    }

    /// <summary>直接设置能量值（用于关卡开始重置为 0 等）。</summary>
    public void SetEnergy(int value)
    {
        int clamped = Mathf.Clamp(value, 0, MaxEnergy);
        if (clamped != CurrentEnergy)
        {
            CurrentEnergy = clamped;
            RefreshEnergyDots();
            EnergyChanged?.Invoke(CurrentEnergy);
        }
    }

    /// <summary>在屏幕中部偏左淡入显示一段文字（黑幕期间通常用它）。</summary>
    public void ShowMessage(string text)
    {
        if (messageText == null)
            return;

        string content = text ?? string.Empty;
        if (string.IsNullOrEmpty(content))
        {
            HideMessage();
            return;
        }

        StopMessageFade();

        messageText.text = content;
        messageText.enabled = true;
        SetMessageAlpha(0f);
        messageFadeRoutine = StartCoroutine(FadeMessageAlphaRoutine(0f, 1f, messageFadeInSeconds));
    }

    /// <summary>把提示文字淡隐掉（再次淡入显示前会立刻停止上一次淡入/淡出）。</summary>
    public void HideMessage()
    {
        if (messageText == null)
            return;

        StopMessageFade();

        if (!messageText.enabled || messageText.color.a <= 0.001f)
        {
            messageText.text = string.Empty;
            messageText.enabled = false;
            SetMessageAlpha(0f);
            return;
        }

        messageFadeRoutine = StartCoroutine(HideMessageRoutine());
    }

    private IEnumerator HideMessageRoutine()
    {
        float from = messageText.color.a;
        yield return FadeMessageAlphaRoutine(from, 0f, messageFadeOutSeconds);

        messageText.text = string.Empty;
        messageText.enabled = false;
        SetMessageAlpha(0f);
        messageFadeRoutine = null;
    }

    private IEnumerator FadeMessageAlphaRoutine(float from, float to, float duration)
    {
        if (duration <= 0f)
        {
            SetMessageAlpha(to);
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            SetMessageAlpha(Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration)));
            yield return null;
        }
        SetMessageAlpha(to);
    }

    private void StopMessageFade()
    {
        if (messageFadeRoutine == null)
            return;

        StopCoroutine(messageFadeRoutine);
        messageFadeRoutine = null;
    }

    private void SetMessageAlpha(float alpha)
    {
        if (messageText == null)
            return;

        float clamped = Mathf.Clamp01(alpha);
        Color c = messageText.color;
        c.a = clamped;
        messageText.color = c;

        // 文字自带的黑色描边也要一起淡，否则文字淡出后还会剩一圈“黑字影”
        if (messageOutline != null)
        {
            Color oc = messageOutline.effectColor;
            oc.a = clamped * 0.85f;
            messageOutline.effectColor = oc;
        }
    }

    /// <summary>淡入黑幕（画面逐渐变黑）。使用不受暂停影响的时间。</summary>
    public IEnumerator FadeToBlackRoutine(float duration)
    {
        yield return FadeBlack(0f, 1f, duration);
    }

    /// <summary>从黑幕淡出（画面恢复）。</summary>
    public IEnumerator FadeFromBlackRoutine(float duration)
    {
        yield return FadeBlack(1f, 0f, duration);
    }

    /// <summary>直接把黑幕设为不透明 / 透明。</summary>
    public void SetBlackScreen(bool black)
    {
        if (blackImage == null)
            return;

        Color c = blackImage.color;
        c.a = black ? 1f : 0f;
        blackImage.color = c;
        blackImage.enabled = black;
    }

    // ---------------------------------------------------------------- 内部

    private void BuildUI()
    {
        // Canvas
        Canvas canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        gameObject.AddComponent<GraphicRaycaster>();

        // —— 黑幕（全屏，最底层，其它 UI 盖在黑幕上层时不受影响） ——
        GameObject blackGO = CreateUiChild("BlackScreen", gameObject.transform);
        RectTransform blackRT = blackGO.GetComponent<RectTransform>();
        blackRT.anchorMin = Vector2.zero;
        blackRT.anchorMax = Vector2.one;
        blackRT.offsetMin = Vector2.zero;
        blackRT.offsetMax = Vector2.zero;

        blackImage = blackGO.AddComponent<Image>();
        blackImage.color = new Color(0f, 0f, 0f, 0f);
        blackImage.raycastTarget = false;
        blackImage.enabled = false;

        // —— 黑幕提示文字（垂直居中、从左侧稍靠中间开始显示，盖在黑幕之上） ——
        GameObject msgGO = CreateUiChild("MessageText", gameObject.transform);
        RectTransform msgRT = msgGO.GetComponent<RectTransform>();
        msgRT.anchorMin = new Vector2(0f, 0.5f);
        msgRT.anchorMax = new Vector2(0f, 0.5f);
        msgRT.pivot = new Vector2(0f, 0.5f);
        msgRT.anchoredPosition = new Vector2(90f, 0f);
        msgRT.sizeDelta = new Vector2(1100f, 260f);

        Text msgText = msgGO.AddComponent<Text>();
        msgText.font = GetDefaultFont();
        msgText.fontSize = 42;
        msgText.fontStyle = FontStyle.Bold;
        msgText.color = Color.white;
        msgText.alignment = TextAnchor.MiddleLeft;
        msgText.horizontalOverflow = HorizontalWrapMode.Wrap;
        msgText.verticalOverflow = VerticalWrapMode.Truncate;
        msgText.raycastTarget = false;
        Outline outline = msgGO.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
        outline.effectDistance = new Vector2(2f, -2f);
        messageText = msgText;
        messageOutline = outline;
        HideMessage();

        // —— 左上角能量三格 ——
        GameObject energyGO = CreateUiChild("EnergyPanel", gameObject.transform);
        RectTransform energyRT = energyGO.GetComponent<RectTransform>();
        energyRT.anchorMin = new Vector2(0f, 1f);
        energyRT.anchorMax = new Vector2(0f, 1f);
        energyRT.pivot = new Vector2(0f, 1f);
        energyRT.anchoredPosition = new Vector2(20f, -20f);
        energyRT.sizeDelta = new Vector2(200f, 40f);

        Sprite circle = CreateCircleSprite();

        for (int i = 0; i < MaxEnergy; i++)
        {
            GameObject dotGO = new GameObject($"EnergyDot_{i + 1}", typeof(RectTransform));
            dotGO.transform.SetParent(energyRT, false);

            RectTransform dotRT = dotGO.GetComponent<RectTransform>();
            dotRT.anchorMin = new Vector2(0f, 0.5f);
            dotRT.anchorMax = new Vector2(0f, 0.5f);
            dotRT.pivot = new Vector2(0f, 0.5f);
            dotRT.anchoredPosition = new Vector2(i * 42f + 16f, 0f);
            dotRT.sizeDelta = new Vector2(28f, 28f);

            Image dot = dotGO.AddComponent<Image>();
            dot.sprite = circle;
            dot.color = EmptyDotColor;
            dot.raycastTarget = false;
            energyDots.Add(dot);
        }
    }

    private static GameObject CreateUiChild(string name, Transform parent)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    private static Font GetDefaultFont()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return font;
    }

    /// <summary>代码生成一个白色圆点 Sprite（不用美术资源）。</summary>
    private static Sprite CreateCircleSprite()
    {
        const int size = 64;
        const int radius = 30; // 半径-1留抗锯齿边缘
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.name = "GGJ_EnergyCircle";

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - (size - 1) * 0.5f;
                float dy = y - (size - 1) * 0.5f;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);

                float alpha = Mathf.Clamp01(radius + 0.5f - dist);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }

        tex.Apply();
        return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    private void RefreshEnergyDots()
    {
        for (int i = 0; i < energyDots.Count; i++)
        {
            if (energyDots[i] == null)
                continue;

            energyDots[i].color = i < CurrentEnergy ? FullDotColor : EmptyDotColor;
        }
    }

    private IEnumerator FadeBlack(float from, float to, float duration)
    {
        if (blackImage == null)
            yield break;

        blackImage.enabled = true;
        SetBlackAlpha(from);

        if (duration <= 0f)
        {
            SetBlackAlpha(to);
            if (to <= 0.001f)
                blackImage.enabled = false;
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            SetBlackAlpha(Mathf.Lerp(from, to, elapsed / duration));
            yield return null;
        }

        SetBlackAlpha(to);
        if (to <= 0.001f)
            blackImage.enabled = false;
    }

    private void SetBlackAlpha(float alpha)
    {
        Color c = blackImage.color;
        c.a = alpha;
        blackImage.color = c;
    }

    private static bool WasQPressedThisFrame()
    {
#if ENABLE_INPUT_SYSTEM
        var keyboard = UnityEngine.InputSystem.Keyboard.current;
        return keyboard != null && keyboard.qKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.Q);
#endif
    }
}
