using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 开始界面 UI：上半部分游戏名（白色粗体），下半部分点击开始提示，
/// 外加一层全屏黑幕用于过场淡入淡出。
/// 挂在 UI_TitleScreen 预制体的 Canvas 根节点上。
/// </summary>
[DisallowMultipleComponent]
public class TitleScreenUI : MonoBehaviour
{
    [Header("文本")]
    [SerializeField] private Text titleText;
    [SerializeField] private Text pressAnyKeyText;

    [Header("文本淡入淡出")]
    [Tooltip("文本所在的父节点，需要有 CanvasGroup 组件")]
    [SerializeField] private CanvasGroup textGroup;

    [Header("黑幕")]
    [Tooltip("全屏纯黑 Image，用于过场黑屏")]
    [SerializeField] private Image fadeImage;

    [Header("默认文本")]
    [SerializeField] private string gameTitle = "GAME TITLE";
    [SerializeField] private string pressAnyKeyMessage = "Click To Start";

    /// <summary>当前实例（GameFlowController 会自动查找）。</summary>
    public static TitleScreenUI Instance { get; private set; }

    private void Awake()
    {
        Instance = this;

        if (titleText != null)
            titleText.text = gameTitle;

        if (pressAnyKeyText != null)
            pressAnyKeyText.text = pressAnyKeyMessage;

        SetFadeImageAlpha(0f);

        if (textGroup != null)
        {
            textGroup.alpha = 1f;
            textGroup.blocksRaycasts = false;
        }
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    #region 对外接口

    /// <summary>显示 / 隐藏整组文本（用于调试或复用）。</summary>
    public void SetTextVisible(bool visible)
    {
        if (textGroup != null)
            textGroup.alpha = visible ? 1f : 0f;
    }

    /// <summary>文本淡出并隐藏。</summary>
    public IEnumerator FadeOutTextsRoutine(float duration)
    {
        yield return FadeTextGroup(1f, 0f, duration);
    }

    /// <summary>文本重新淡入。</summary>
    public IEnumerator FadeInTextsRoutine(float duration)
    {
        yield return FadeTextGroup(0f, 1f, duration);
    }

    /// <summary>淡入黑幕（画面变全黑）。</summary>
    public IEnumerator FadeToBlackRoutine(float duration)
    {
        yield return FadeBlack(0f, 1f, duration);
    }

    /// <summary>从黑幕淡出（画面恢复可见）。</summary>
    public IEnumerator FadeFromBlackRoutine(float duration)
    {
        yield return FadeBlack(1f, 0f, duration);
    }

    /// <summary>直接把黑幕设为不透明 / 透明。</summary>
    public void SetBlackScreen(bool black)
    {
        SetFadeImageAlpha(black ? 1f : 0f);
    }

    /// <summary>运行时替换标题文字（例如多语言）。</summary>
    public void SetGameTitle(string title)
    {
        if (titleText != null)
            titleText.text = title;
    }

    #endregion

    private IEnumerator FadeTextGroup(float from, float to, float duration)
    {
        if (textGroup == null)
            yield break;

        textGroup.alpha = from;

        if (duration <= 0f)
        {
            textGroup.alpha = to;
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            textGroup.alpha = Mathf.Lerp(from, to, elapsed / duration);
            yield return null;
        }

        textGroup.alpha = to;
    }

    private IEnumerator FadeBlack(float from, float to, float duration)
    {
        if (fadeImage == null)
            yield break;

        SetFadeImageAlpha(from);

        if (duration <= 0f)
        {
            SetFadeImageAlpha(to);
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            SetFadeImageAlpha(Mathf.Lerp(from, to, elapsed / duration));
            yield return null;
        }

        SetFadeImageAlpha(to);
    }

    private void SetFadeImageAlpha(float alpha)
    {
        if (fadeImage == null)
            return;

        Color color = fadeImage.color;
        color.a = alpha;
        fadeImage.color = color;
        fadeImage.enabled = alpha > 0.001f;
    }
}
