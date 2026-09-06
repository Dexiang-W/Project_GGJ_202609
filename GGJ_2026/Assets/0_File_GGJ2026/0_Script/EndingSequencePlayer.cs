using System.Collections;
using System.Collections.Generic;
using StarterAssets;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// “结局演出”播放器：交互后 渐黑 → 逐条字幕 → 稍候数秒 → 从下往上滚动报幕。
///
/// 用法（以可交互物体触发为例）：
///   1. 给物体加 InteractableObject（负责靠近高亮 + 按 E）；
///   2. 同一个物体（或任意空物体）再加本组件；
///   3. 在 InteractableObject 的 “On Interact” 事件里挂本组件的 Play()；
///     或者在任何脚本 / 机关事件里直接调用 xxx.Play()。
///   4. 所有字幕、报幕文案、字号、时长都在本组件 Inspector 里编辑（都可留空跳过对应阶段）。
///
/// 结束行为（看结局需要）：
///   · returnToTitleAfterFinish 勾选（默认）：报幕滚完 → OnFinished 触发 → 稍停后自动
///     清掉跨场景常驻的玩家/相机/流程对象，回到标题场景（Begin_Menu）重新开始完整流程；
///   · stayOnBlackAfterFinish 勾选（且不回标题）：报幕滚完停在黑屏（典型结局画面），
///     可把 OnFinished 接到“显示结束面板”等命令；
///   · 都不勾：黑幕淡出、交还操作，继续回到游戏。
/// </summary>
[DisallowMultipleComponent]
public class EndingSequencePlayer : MonoBehaviour
{
    /// <summary>一行字幕（黑屏时逐条播放）。</summary>
    [System.Serializable]
    public class SubtitleLine
    {
        [TextArea(1, 4)] public string text = "";
        [Min(0.2f)] public float holdSeconds = 2.4f;
    }

    [Header("① 字幕（黑屏后依次显示；留空则跳过）")]
    [SerializeField] private List<SubtitleLine> subtitleLines = new List<SubtitleLine>
    {
        new SubtitleLine { text = "第一句字幕……（Inspector 里改）", holdSeconds = 2.5f },
        new SubtitleLine { text = "第二句字幕……", holdSeconds = 2.5f },
    };

    [Header("② 滚动报幕（字幕结束后从下往上滚；留空则跳过）")]
    [Tooltip("每一格 = 一行报幕；想加空行就填一条空字符串。")]
    [SerializeField] private string[] creditsLines =
    {
        "—— 鸣 谢 ——",
        "",
        "制作人：你的名字",
        "程序：你的名字",
        "美术：你的名字",
        "音乐：你的名字",
        "",
        "感谢每一位测试玩家！",
    };

    [Header("③ 样式")]
    [SerializeField] private float subtitleFontSize = 54f;
    [SerializeField] private Color subtitleColor = Color.white;
    [SerializeField] private float creditsFontSize = 46f;
    [SerializeField] private Color creditsColor = new Color(1f, 0.96f, 0.85f, 1f);

    [Header("④ 节奏（秒）")]
    [Tooltip("字幕淡入/淡出时长")]
    [SerializeField] private float subtitleFadeSeconds = 0.5f;
    [Tooltip("所有字幕播完后、开始滚动报幕前的停顿")]
    [SerializeField] private float afterSubtitlesDelaySeconds = 1.2f;
    [Tooltip("画面渐黑所需时长")]
    [SerializeField] private float fadeToBlackSeconds = 0.9f;
    [Tooltip("报幕上滚速度（画布像素/秒；画布参考高 1080）")]
    [SerializeField] private float creditsScrollSpeed = 130f;
    [Tooltip("报幕两行之间的额外间距（像素）")]
    [SerializeField] private float creditsLineSpacing = 24f;
    [Tooltip("报幕左右留白（像素）")]
    [SerializeField] private float creditsSideMargin = 260f;

    [Header("⑤ 结束行为")]
    [Tooltip("勾选 = 报幕滚完停在黑屏（典型结局）；不勾 = 黑幕淡出、恢复操作继续游戏")]
    [SerializeField] private bool stayOnBlackAfterFinish = true;
    [Tooltip("报幕滚完时触发（可挂“显示结束面板 / 收尾音效”等命令）")]
    public UnityEvent OnFinished = new UnityEvent();

    [Header("⑥ 结束后回到标题（重新游玩）")]
    [Tooltip("勾选（默认）= OnFinished 触发后，自动清理跨场景常驻的玩家/相机/流程对象，\n" +
             "回到标题场景（Begin_Menu）重新开始完整流程（灰屏循环跑 → 点击开始 → Level1）。\n" +
             "结局看完直接 LoadScene 会重复出两个玩家/两个流程控制器，必须先清场，故走 GameTitleRestart。")]
    [SerializeField] private bool returnToTitleAfterFinish = true;
    [Tooltip("要回去的标题场景名（须已加入 Build Settings，本项目默认 Begin_Menu）")]
    [SerializeField] private string titleSceneName = "Begin_Menu";
    [Tooltip("OnFinished 触发后、真正切回标题前的黑屏停留秒数（给“最后一句提示 / 收尾音效”留时间）")]
    [SerializeField] private float returnDelaySeconds = 1.2f;

    // ---------------------------------------------------------------- 运行时

    private bool isPlaying;

    private Canvas canvas;
    private RectTransform canvasRT;
    private Image blackImage;
    private Text subtitleText;
    private Outline subtitleOutline;
    private RectTransform creditsMaskRT;
    private RectTransform creditsWindow;

    private readonly List<RectTransform> creditLines = new List<RectTransform>();

    private ThirdPersonController player;
    private bool hadInputEnabled = true;
    private bool inputWasChanged;

    public bool IsPlaying => isPlaying;

    /// <summary>开始结局演出（InteractableObject 的 On Interact 事件挂这里，或外部脚本直接调用）。</summary>
    [ContextMenu("播放结局演出（预览用）")]
    public void Play()
    {
        if (isPlaying)
            return;

        isPlaying = true;
        StartCoroutine(SequenceRoutine());
    }

    /// <summary>
    /// 清掉跨场景常驻对象并回到标题场景（Begin_Menu），重新开始一局完整游玩。
    /// 可挂到 OnFinished 事件，或由“结束按钮 / 其它脚本”直接调用。
    /// </summary>
    [ContextMenu("回到标题场景，重新游玩")]
    public void ReturnToTitleAndRestart()
    {
        GameTitleRestart.ReturnToTitle(titleSceneName);
    }

    // ---------------------------------------------------------------- 主流程

    private IEnumerator SequenceRoutine()
    {
        ResolvePlayer();
        DisablePlayerInput();

        EnsureCanvas();
        gameObject.SetActive(true);
        if (canvas != null) canvas.gameObject.SetActive(true);

        SetBlackAlpha(0f);
        SetSubtitle(null);

        // 1. 渐黑
        yield return FadeBlackToRoutine(1f, fadeToBlackSeconds);

        // 2. 字幕逐条播放
        if (subtitleLines != null)
        {
            foreach (SubtitleLine line in subtitleLines)
            {
                if (line == null || string.IsNullOrEmpty(line.text))
                {
                    if (subtitleText != null) subtitleText.enabled = false;
                    continue;
                }

                SetSubtitle(line.text.Replace("\\n", "\n"));
                yield return FadeSubtitleAlphaRoutine(0f, 1f, subtitleFadeSeconds);
                yield return new WaitForSecondsRealtime(Mathf.Max(0.2f, line.holdSeconds));
                yield return FadeSubtitleAlphaRoutine(1f, 0f, subtitleFadeSeconds);
            }
        }

        SetSubtitle(null);

        // 3. 停顿几秒后开始滚动报幕
        yield return new WaitForSecondsRealtime(Mathf.Max(0f, afterSubtitlesDelaySeconds));

        if (creditsLines != null && creditsLines.Length > 0)
            yield return ScrollCreditsRoutine(creditsLines);

        // 4. 结束
        // 先记下“是否要回标题”，再触发 OnFinished：OnFinished 里挂的命令
        // 若把本物体销毁/切场景，也不会影响下面判断要走的回标题流程。
        bool autoReturn = returnToTitleAfterFinish;
        OnFinished?.Invoke();

        if (autoReturn)
        {
            // 给 OnFinished 里挂的收尾提示 / 音效留一点黑屏演出时间，再整体清场回标题
            yield return new WaitForSecondsRealtime(Mathf.Max(0f, returnDelaySeconds));

            isPlaying = false;
            // 清掉跨场景常驻对象并回标题（见 GameTitleRestart.cs 注释）；当前场景随后会被卸载
            GameTitleRestart.ReturnToTitle(titleSceneName);
            yield break;
        }

        if (!stayOnBlackAfterFinish)
        {
            yield return FadeBlackToRoutine(0f, fadeToBlackSeconds); // 黑幕淡出
            SetSubtitle(null);
            ClearCreditLines();
            if (canvas != null) canvas.gameObject.SetActive(false);
            RestorePlayerInput();
        }
        // stayOnBlackAfterFinish == true：黑屏停留，操作保持锁定（可把 OnFinished 接到“显示结束面板”等）

        isPlaying = false;
    }

    private IEnumerator ScrollCreditsRoutine(string[] lines)
    {
        EnsureCanvas();
        ClearCreditLines();

        Rect rect = creditsMaskRT.rect;
        float screenHeight = Mathf.Max(1f, rect.height);
        float lineHeight = Mathf.Max(20f, creditsFontSize * 1.5f);
        float spacing = lineHeight + Mathf.Max(0f, creditsLineSpacing);
        float startY = -lineHeight;                       // 首行从屏幕底边之下滚上来
        int count = lines.Length;

        float width = Mathf.Max(100f, rect.width - creditsSideMargin * 2f);
        for (int i = 0; i < count; i++)
        {
            RectTransform line = CreateCreditLine(lines[i], width, lineHeight);
            line.anchoredPosition = new Vector2(0f, startY + i * spacing);
            creditLines.Add(line);
        }

        // 需要把最后一行完全滚出顶边才结束
        float endTravel = screenHeight + lineHeight - (startY + (count - 1) * spacing);
        float traveled = 0f;

        while (traveled < endTravel)
        {
            traveled += Mathf.Max(0f, creditsScrollSpeed) * Time.unscaledDeltaTime;
            if (creditsWindow != null)
                creditsWindow.anchoredPosition = new Vector2(0f, traveled);
            yield return null;
        }

        if (creditsWindow != null)
            creditsWindow.anchoredPosition = new Vector2(0f, endTravel);
    }

    // ---------------------------------------------------------------- UI 构建

    private void EnsureCanvas()
    {
        if (canvas != null)
            return;

        GameObject root = new GameObject("GGJ_EndingSequence_UI");
        root.transform.SetParent(null, false);

        canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000; // 盖过 GameplayHUD(100) 等所有游戏内 UI

        CanvasScaler scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        root.AddComponent<GraphicRaycaster>();
        canvasRT = canvas.GetComponent<RectTransform>();

        // 黑幕
        GameObject blackGO = CreateUiChild("BlackScreen", canvasRT);
        RectTransform blackRT = blackGO.GetComponent<RectTransform>();
        StretchFull(blackRT);
        blackImage = blackGO.AddComponent<Image>();
        blackImage.color = new Color(0f, 0f, 0f, 0f);
        blackImage.raycastTarget = true;
        blackImage.enabled = true;

        // 字幕
        GameObject subtitleGO = CreateUiChild("Subtitle", canvasRT);
        RectTransform subtitleRT = subtitleGO.GetComponent<RectTransform>();
        subtitleRT.anchorMin = new Vector2(0f, 0.35f);
        subtitleRT.anchorMax = new Vector2(1f, 0.62f);
        subtitleRT.offsetMin = Vector2.zero;
        subtitleRT.offsetMax = Vector2.zero;
        subtitleRT.pivot = new Vector2(0.5f, 0.5f);

        subtitleText = subtitleGO.AddComponent<Text>();
        subtitleText.font = GetDefaultFont();
        subtitleText.fontSize = (int)subtitleFontSize;
        subtitleText.fontStyle = FontStyle.Bold;
        subtitleText.color = subtitleColor;
        subtitleText.alignment = TextAnchor.MiddleCenter;
        subtitleText.horizontalOverflow = HorizontalWrapMode.Wrap;
        subtitleText.verticalOverflow = VerticalWrapMode.Truncate;
        subtitleText.raycastTarget = false;
        subtitleOutline = subtitleGO.AddComponent<Outline>();
        subtitleOutline.effectColor = new Color(0f, 0f, 0f, 0.9f);
        subtitleOutline.effectDistance = new Vector2(2.5f, -2.5f);

        // 报幕裁切区（全屏，把上下越界内容裁掉）
        GameObject maskGO = CreateUiChild("CreditsScrollMask", canvasRT);
        creditsMaskRT = maskGO.GetComponent<RectTransform>();
        StretchFull(creditsMaskRT);
        maskGO.AddComponent<RectMask2D>();

        // 可整体上移的滚动窗口（铺满裁切区，仅作坐标父节点）
        GameObject windowGO = CreateUiChild("CreditsScrollWindow", creditsMaskRT);
        creditsWindow = windowGO.GetComponent<RectTransform>();
        StretchFull(creditsWindow);
    }

    private RectTransform CreateCreditLine(string content, float width, float height)
    {
        GameObject go = CreateUiChild("CreditLine", creditsWindow);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0f);
        rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.sizeDelta = new Vector2(width, height);

        Text text = go.AddComponent<Text>();
        text.font = GetDefaultFont();
        text.fontSize = (int)creditsFontSize;
        text.color = creditsColor;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        text.text = content;

        Outline outline = go.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
        outline.effectDistance = new Vector2(2f, -2f);

        return rt;
    }

    private void ClearCreditLines()
    {
        foreach (RectTransform line in creditLines)
        {
            if (line != null)
                Destroy(line.gameObject);
        }
        creditLines.Clear();

        if (creditsWindow != null)
            creditsWindow.anchoredPosition = Vector2.zero;
    }

    private void SetSubtitle(string content)
    {
        if (subtitleText == null)
            return;

        bool visible = !string.IsNullOrEmpty(content);
        subtitleText.text = visible ? content : string.Empty;
        subtitleText.enabled = visible;
        Color c = subtitleText.color;
        c.a = visible ? 1f : 0f;
        subtitleText.color = c;

        if (subtitleOutline != null)
        {
            Color oc = subtitleOutline.effectColor;
            oc.a = visible ? 0.9f : 0f;
            subtitleOutline.effectColor = oc;
        }
    }

    private IEnumerator FadeSubtitleAlphaRoutine(float from, float to, float duration)
    {
        if (subtitleText == null)
            yield break;

        if (duration <= 0f)
        {
            SetSubtitleAlpha(to);
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            SetSubtitleAlpha(Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration)));
            yield return null;
        }
        SetSubtitleAlpha(to);
    }

    private void SetSubtitleAlpha(float alpha)
    {
        if (subtitleText == null)
            return;

        float clamped = Mathf.Clamp01(alpha);
        Color c = subtitleText.color;
        c.a = clamped;
        subtitleText.color = c;

        if (subtitleOutline != null)
        {
            Color oc = subtitleOutline.effectColor;
            oc.a = clamped * 0.9f;
            subtitleOutline.effectColor = oc;
        }
    }

    /// <summary>把黑幕 alpha 平滑过渡到目标值（不依赖 Time.timeScale，用 unscaled 时间）。</summary>
    private IEnumerator FadeBlackToRoutine(float target, float duration)
    {
        float start = blackImage != null ? blackImage.color.a : (target >= 0.5f ? 0f : 1f);
        float clampedDuration = Mathf.Max(0.01f, duration);

        float elapsed = 0f;
        while (elapsed < clampedDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            SetBlackAlpha(Mathf.Lerp(start, target, Mathf.Clamp01(elapsed / clampedDuration)));
            yield return null;
        }
        SetBlackAlpha(target);
    }

    private void SetBlackAlpha(float alpha)
    {
        if (blackImage == null)
            return;

        Color c = blackImage.color;
        c.a = Mathf.Clamp01(alpha);
        blackImage.color = c;
    }

    // ---------------------------------------------------------------- 玩家

    private void ResolvePlayer()
    {
        if (player == null)
            player = ThirdPersonController.Instance;
        if (player == null)
            player = FindObjectOfType<ThirdPersonController>();
    }

    private void DisablePlayerInput()
    {
        if (player == null)
            return;

        var inputs = player.GetComponent<StarterAssetsInputs>();
        if (inputs != null)
        {
            inputs.MoveInput(Vector2.zero);
            inputs.SprintInput(false);
            inputs.JumpInput(false);
        }

#if ENABLE_INPUT_SYSTEM
        var playerInput = player.GetComponent<PlayerInput>();
        if (playerInput != null)
        {
            hadInputEnabled = playerInput.enabled;
            if (hadInputEnabled)
            {
                playerInput.enabled = false;
                inputWasChanged = true;
            }
        }
#endif
    }

    private void RestorePlayerInput()
    {
        if (player == null)
            return;

#if ENABLE_INPUT_SYSTEM
        var playerInput = player.GetComponent<PlayerInput>();
        if (playerInput != null && inputWasChanged)
            playerInput.enabled = hadInputEnabled;
#endif
        inputWasChanged = false;
    }

    // ---------------------------------------------------------------- 工具

    private static GameObject CreateUiChild(string name, Transform parent)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    private static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private static Font GetDefaultFont()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return font;
    }
}
