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
/// “结局演出”播放器：交互后 渐黑 → 逐条字幕 → 稍候数秒 → 从下往上滚动报幕 →
/// 结尾图 淡入→停留→淡出（默认共 4 秒），播完全部结束（按 ⑥ 的设置回标题）。
///
/// 前面还可以挂一段“终章实机演出”（见下面的 ⓪）：
///   渐黑 → Final Scene 出现 → 左上角两段文字（玩家可自由活动）→ 渐黑 →
///   切到枯萎关卡做横移运镜（植物枯萎 / 水面下降 / 草消失）→ 渐黑 → 才播下面的字幕与报幕。
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
        [Tooltip("勾选 = 这一行显示在【屏幕正中间】（用来放最后的“谢谢游玩”之类）；\n" +
                 "不勾（默认）= 显示在左边（用下面 ③ 样式里的边距 / 宽度）。")]
        public bool centered;
    }

    [Header("⓪ 终章实机演出（在字幕 / 报幕之前播放）")]
    [Tooltip("勾选（默认）：交互后先播一段实机演出 —— 渐黑 → 本关的 Final Scene 出现 → " +
             "左上角两段文字（期间玩家可以自由活动）→ 渐黑 → 切到枯萎关卡做“从左往右”的横移运镜" +
             "（植物依次枯萎、水面下降、草逐渐消失）→ 渐黑，然后才开始下面的字幕与报幕。\n" +
             "取消勾选 = 维持原来的行为（交互后直接渐黑出字幕）。")]
    [SerializeField] private bool playFinaleBeforeSubtitles = true;

    [Tooltip("终章演出的导演（台词 / 运镜 / 枯萎参数都在它身上）。\n" +
             "留空 = 运行时自动创建一个常驻导演，用它的默认配置（枯萎关卡 Level2、Final Scene、" +
             "“实验体在弹指间疯狂生长”/“那么，代价呢...”），正常不需要手动摆。\n" +
             "想微调就在场景里放个空物体挂上 EndingFinaleDirector 并拖到这里（不拖也会自动找到它）。")]
    [SerializeField] private EndingFinaleDirector finaleDirector;

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
    [SerializeField] private float subtitleFontSize = 68f;
    [SerializeField] private Color subtitleColor = Color.white;
    [Tooltip("字幕离屏幕左边的距离（像素，按 1920x1080 参考分辨率）")]
    [SerializeField] private float subtitleLeftMargin = 120f;
    [Tooltip("字幕区域宽度（像素）")]
    [SerializeField] private float subtitleWidth = 1500f;
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
    [Tooltip("报幕滚到最后一行时，让这一行【停在屏幕正中】停留多少秒，再整块淡出。\n" +
             "演出顺序因此变成：报幕上滚 → 最后一行居中停留 → 文字消失 → 才接结尾图（不再和图片重叠）。")]
    [SerializeField] private float creditsFinalLineHoldSeconds = 1.6f;
    [Tooltip("最后一行居中停留完之后，报幕整体淡出所需时长（秒）")]
    [SerializeField] private float creditsFadeOutSeconds = 0.8f;
    [Tooltip("勾选（默认）= 最后一行停在屏幕正中；不勾 = 一直滚到所有文字都滚出顶边才结束")]
    [SerializeField] private bool holdLastCreditLineInCenter = true;
    [Tooltip("报幕文字彻底消失后、结尾图开始淡入前的停顿（秒）")]
    [SerializeField] private float afterCreditsDelaySeconds = 0.6f;

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

    [Header("⑦ 报幕后的结尾图")]
    [Tooltip("结尾图（报幕滚完后 淡入→停留→淡出，共 Ending Image Total Seconds 秒，播完全部结束）。\n" +
             "留空 = 运行时自动读 Resources/GGJ_EndingCard（Sprite / Texture 都行）。")]
    [SerializeField] private Sprite endingImage;
    [Tooltip("结尾图淡入 / 淡出时长（秒）")]
    [SerializeField] private float endingImageFadeSeconds = 1.5f;
    [Tooltip("结尾图从开始淡入到完全淡出的总时长（秒）：中间停留 = 总时长 − 淡入 − 淡出。默认 4 秒。")]
    [SerializeField] private float endingImageTotalSeconds = 4f;

    // ---------------------------------------------------------------- 运行时

    private bool isPlaying;

    private Canvas canvas;
    private RectTransform canvasRT;
    private Image blackImage;
    private Text subtitleText;
    private Outline subtitleOutline;
    private RectTransform creditsMaskRT;
    private RectTransform creditsWindow;
    /// <summary>报幕滚动窗口的淡入淡出控制（最后一行居中停留完之后整块淡掉）。</summary>
    private CanvasGroup creditsGroup;

    /// <summary>全屏结尾图（报幕滚完后 淡入→停留→淡出）。</summary>
    private Image endingImageObject;
    /// <summary>结尾图真的播过（播完 = 演出到头了，回标题前不再多等 Return Delay）。</summary>
    private bool endingImagePlayed;

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
    /// 只播“黑屏字幕 → 滚动报幕 → 结束”这一段，跳过 ⓪ 终章实机演出。
    /// “放弃实验”结局（GiveUpEndingDirector）用这条：调用时画面应已全黑。
    /// 结束后按本组件的 ⑤ 配置处理（默认停在黑屏 + 自动回标题）。
    /// </summary>
    /// <param name="texts">要依次显示的文案（可多条）。</param>
    /// <param name="holdSeconds">每条停留时长。</param>
    /// <param name="centered">true = 显示在屏幕正中间（结局定妆句一般用这个）。</param>
    public void PlayTextsAndCredits(IList<string> texts, float holdSeconds = 3f, bool centered = true)
    {
        if (isPlaying)
            return;

        playFinaleBeforeSubtitles = false;

        if (subtitleLines == null)
            subtitleLines = new List<SubtitleLine>();

        subtitleLines.Clear();

        if (texts != null)
        {
            foreach (string text in texts)
            {
                if (string.IsNullOrEmpty(text))
                    continue;

                subtitleLines.Add(new SubtitleLine
                {
                    text = text,
                    holdSeconds = Mathf.Max(0.2f, holdSeconds),
                    centered = centered,
                });
            }
        }

        isPlaying = true;
        StartCoroutine(SequenceRoutine());
    }

    /// <summary>
    /// 外部改写“滚动报幕名单”（例如“放弃实验”结局导演要用和 Level5 一样的名单）。
    /// 传空 / null 则保持原样。
    /// </summary>
    public void SetCreditsLines(IList<string> lines)
    {
        if (lines == null || lines.Count == 0)
            return;

        creditsLines = new string[lines.Count];
        for (int i = 0; i < lines.Count; i++)
            creditsLines[i] = lines[i] ?? string.Empty;
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

    /// <summary>
    /// 播“终章实机演出”（EndingFinaleDirector）。
    /// 这一步会切到枯萎关卡，所以本组件所在的物体必须先变成常驻的：
    /// LoadScene(Single) 会销毁场景里的一切，宿主一旦被销毁，
    /// 这条协程后面的字幕 / 报幕就永远播不出来了。
    /// </summary>
    private IEnumerator RunFinalePrelude()
    {
        EndingFinaleDirector director = ResolveFinaleDirector();
        if (director == null)
            yield break;

        DontDestroyOnLoad(gameObject.transform.root.gameObject);
        DontDestroyOnLoad(director.gameObject.transform.root.gameObject);

        yield return director.PlayFinale(this);
    }

    /// <summary>取终章导演：Inspector 指定 → 场景里查找 → 运行时创建（用脚本默认参数）。</summary>
    private EndingFinaleDirector ResolveFinaleDirector()
    {
        if (finaleDirector != null)
            return finaleDirector;

        finaleDirector = FindObjectOfType<EndingFinaleDirector>();
        if (finaleDirector != null)
            return finaleDirector;

        GameObject go = new GameObject("GGJ_EndingFinaleDirector");
        DontDestroyOnLoad(go);
        finaleDirector = go.AddComponent<EndingFinaleDirector>();

        Debug.Log("[EndingSequencePlayer] 已自动创建终章演出导演（EndingFinaleDirector），使用默认配置：" +
                  "枯萎关卡 Level2 / Final Scene / 两段左上角文字。想微调参数就在场景里放一个挂该组件的空物体。", go);

        return finaleDirector;
    }

    private IEnumerator SequenceRoutine()
    {
        // ⓪ 终章实机演出（含一次跨场景运镜，演完画面保持全黑）
        if (playFinaleBeforeSubtitles)
            yield return RunFinalePrelude();

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

                SetSubtitle(line.text.Replace("\\n", "\n"), line.centered);
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

        // 3.4 报幕文字已经彻底清掉了，这里再空一拍，让“文字消失”和“图片出现”在观感上分开
        if (afterCreditsDelaySeconds > 0f)
            yield return new WaitForSecondsRealtime(afterCreditsDelaySeconds);

        // 3.5 结尾图：淡入 → 停留 → 淡出（默认共 4 秒）→ 全部结束
        yield return ShowEndingImageRoutine();

        // 4. 结束
        // 先记下“是否要回标题”，再触发 OnFinished：OnFinished 里挂的命令
        // 若把本物体销毁/切场景，也不会影响下面判断要走的回标题流程。
        bool autoReturn = returnToTitleAfterFinish;
        OnFinished?.Invoke();

        if (autoReturn)
        {
            // 给 OnFinished 里挂的收尾提示 / 音效留一点黑屏演出时间，再整体清场回标题
            // （结尾图播完 = 演出到头了，不再多等，直接结束）
            if (!endingImagePlayed)
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
            ClearEndingImage();
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

        if (creditsGroup == null && creditsWindow != null)
            creditsGroup = creditsWindow.GetComponent<CanvasGroup>();
        if (creditsGroup != null)
            creditsGroup.alpha = 1f;

        Rect rect = creditsMaskRT.rect;
        float screenHeight = Mathf.Max(1f, rect.height);
        float lineHeight = Mathf.Max(20f, creditsFontSize * 1.5f);
        float spacing = lineHeight + Mathf.Max(0f, creditsLineSpacing);
        float startY = -lineHeight;                       // 首行从屏幕底边之下滚上来
        int count = lines.Length;

        // 整块名单堆在首行【下方】：窗口上移时第 0 行最先从底边露出来，最后一行最后才升上来
        // （以前写成 startY + i * spacing，名单是倒着排的：一开场中间就压着好几行，最后还滚不干净）
        float width = Mathf.Max(100f, rect.width - creditsSideMargin * 2f);
        for (int i = 0; i < count; i++)
        {
            RectTransform line = CreateCreditLine(lines[i], width, lineHeight);
            line.anchoredPosition = new Vector2(0f, startY - i * spacing);
            creditLines.Add(line);
        }

        float lastLineY = startY - (count - 1) * spacing;

        // 停在正中：让最后一行的“垂直中线”落在屏幕中线上（行 pivot 在底边，所以底边要再往下压半行）
        // 滚干净模式：再多滚一整屏，直到最后一行的底边也越过顶边
        float endTravel = holdLastCreditLineInCenter
            ? screenHeight * 0.5f - lineHeight * 0.5f - lastLineY
            : screenHeight - lastLineY + lineHeight;

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

        // 最后一行在正中停留 → 整块淡出 → 清干净
        // 这一步很关键：保证“文字先消失，再接结尾图”，不会同时叠在屏幕上
        if (creditsFinalLineHoldSeconds > 0f)
            yield return new WaitForSecondsRealtime(creditsFinalLineHoldSeconds);

        yield return FadeCreditsRoutine(1f, 0f, Mathf.Max(0.01f, creditsFadeOutSeconds));

        ClearCreditLines();
    }

    /// <summary>报幕滚动窗口整体淡入 / 淡出。</summary>
    private IEnumerator FadeCreditsRoutine(float from, float to, float duration)
    {
        if (creditsGroup == null)
        {
            if (creditsWindow != null)
                creditsGroup = creditsWindow.GetComponent<CanvasGroup>();
            if (creditsGroup == null)
                yield break;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            creditsGroup.alpha = Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration));
            yield return null;
        }

        creditsGroup.alpha = to;
    }

    // ---------------------------------------------------------------- 结尾图（报幕之后）

    /// <summary>
    /// 报幕滚完后的结尾图：淡入 → 停留 → 淡出（共 endingImageTotalSeconds 秒），整个结局到此为止。
    /// 没配图时整段跳过（维持老行为）。
    /// </summary>
    private IEnumerator ShowEndingImageRoutine()
    {
        Sprite sprite = ResolveEndingImage();
        if (sprite == null)
            yield break;

        EnsureEndingImageUi();
        endingImagePlayed = true;
        endingImageObject.sprite = sprite;

        float total = Mathf.Max(0.2f, endingImageTotalSeconds);
        float fade = Mathf.Clamp(endingImageFadeSeconds, 0.01f, total * 0.5f);
        float hold = Mathf.Max(0f, total - fade * 2f);

        yield return FadeEndingImageRoutine(0f, 1f, fade);

        if (hold > 0f)
            yield return new WaitForSecondsRealtime(hold);

        yield return FadeEndingImageRoutine(1f, 0f, fade);
    }

    /// <summary>结尾图：Inspector 拖的 → Resources/GGJ_EndingCard（Sprite）→ Texture2D 现转 Sprite。</summary>
    private Sprite ResolveEndingImage()
    {
        if (endingImage != null)
            return endingImage;

        Sprite sprite = Resources.Load<Sprite>("GGJ_EndingCard");
        if (sprite != null)
            return sprite;

        Texture2D texture = Resources.Load<Texture2D>("GGJ_EndingCard");
        if (texture != null)
            return Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                                 new Vector2(0.5f, 0.5f), 100f);

        return null;
    }

    /// <summary>结尾图的 UI：全屏一张图（报幕滚完后淡入淡出）。</summary>
    private void EnsureEndingImageUi()
    {
        if (endingImageObject != null)
            return;

        GameObject imageGO = CreateUiChild("EndingImage", canvasRT);
        RectTransform imageRT = imageGO.GetComponent<RectTransform>();
        StretchFull(imageRT);

        endingImageObject = imageGO.AddComponent<Image>();
        endingImageObject.color = new Color(1f, 1f, 1f, 0f);
        endingImageObject.preserveAspect = true;
        endingImageObject.raycastTarget = false;
    }

    private IEnumerator FadeEndingImageRoutine(float from, float to, float duration)
    {
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            SetEndingImageAlpha(Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration)));
            yield return null;
        }
        SetEndingImageAlpha(to);
    }

    private void SetEndingImageAlpha(float alpha)
    {
        if (endingImageObject == null)
            return;

        Color c = endingImageObject.color;
        c.a = Mathf.Clamp01(alpha);
        endingImageObject.color = c;
    }

    /// <summary>把结尾图收掉（只在“不回标题、不留在黑屏”的老行为里用）。</summary>
    private void ClearEndingImage()
    {
        if (endingImageObject != null)
        {
            endingImageObject.sprite = null;
            SetEndingImageAlpha(0f);
        }
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
        // 左边、垂直居中（不再压在屏幕上半部分正中）
        subtitleRT.anchorMin = new Vector2(0f, 0.4f);
        subtitleRT.anchorMax = new Vector2(0f, 0.6f);
        subtitleRT.pivot = new Vector2(0f, 0.5f);
        subtitleRT.anchoredPosition = new Vector2(Mathf.Max(0f, subtitleLeftMargin), 0f);
        subtitleRT.sizeDelta = new Vector2(Mathf.Max(200f, subtitleWidth), 0f);

        subtitleText = subtitleGO.AddComponent<Text>();
        subtitleText.font = GetDefaultFont();
        subtitleText.fontSize = (int)subtitleFontSize;
        subtitleText.fontStyle = FontStyle.Bold;
        subtitleText.color = subtitleColor;
        subtitleText.alignment = TextAnchor.MiddleLeft;
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

        creditsGroup = windowGO.AddComponent<CanvasGroup>();
        creditsGroup.alpha = 1f;
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

    private void SetSubtitle(string content, bool centered = false)
    {
        if (subtitleText == null)
            return;

        ApplySubtitleLayout(centered);

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

    /// <summary>
    /// 字幕摆放：centered = 屏幕正中间（居中对齐，整行宽度铺满屏幕）；
    /// 否则 = 左边、垂直居中（用 subtitleLeftMargin / subtitleWidth）。
    /// </summary>
    private void ApplySubtitleLayout(bool centered)
    {
        if (subtitleText == null)
            return;

        RectTransform rt = subtitleText.rectTransform;

        if (centered)
        {
            // 横向铺满、纵向占中间 20% → 文字正好落在屏幕正中央
            rt.anchorMin = new Vector2(0f, 0.4f);
            rt.anchorMax = new Vector2(1f, 0.6f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = Vector2.zero;
            subtitleText.alignment = TextAnchor.MiddleCenter;
        }
        else
        {
            rt.anchorMin = new Vector2(0f, 0.4f);
            rt.anchorMax = new Vector2(0f, 0.6f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(Mathf.Max(0f, subtitleLeftMargin), 0f);
            rt.sizeDelta = new Vector2(Mathf.Max(200f, subtitleWidth), 0f);
            subtitleText.alignment = TextAnchor.MiddleLeft;
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
