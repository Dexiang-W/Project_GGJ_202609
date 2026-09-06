using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 从“结局画面”干净地回到标题场景（Begin_Menu）重新开始一局完整流程。
///
/// 为什么需要它：正常游玩从标题一路切关卡时，玩家 / 主相机 / GameFlowController /
/// 标题 UI / GameplayHUD / AudioManager 都被 DontDestroyOnLoad 跨场景保留着。
/// 结局看完如果直接 LoadScene(标题)，标题场景自带的玩家 / 相机 / 流程控制器会
/// 和常驻的旧对象叠在一起，出现“两个玩家、两个流程控制器”等问题。
/// 因此这里先清掉 DontDestroyOnLoad 里的全部常驻残留，再全新加载标题场景，
/// 让标题场景里自带的玩家 / 相机 / GameFlowController / 标题 UI 像第一次运行一样重新开始。
///
/// 用法：结局报幕播完后调用 GameTitleRestart.ReturnToTitle("Begin_Menu") 即可
/// （EndingSequencePlayer 已内置该选项，见其 “⑥ 结束后回到标题” 分组）。
/// </summary>
public static class GameTitleRestart
{
    /// <summary>是否已在“回标题重开”的流程中（防止多处/重复触发）。</summary>
    public static bool IsRestartInProgress { get; private set; }

    /// <summary>
    /// 清掉跨场景常驻残留并回到指定标题场景，重新开始一局。
    /// </summary>
    /// <param name="titleSceneName">标题场景名（须已加入 Build Settings，本项目默认 Begin_Menu）</param>
    public static void ReturnToTitle(string titleSceneName)
    {
        if (IsRestartInProgress)
            return;

        if (string.IsNullOrEmpty(titleSceneName) ||
            !Application.CanStreamedLevelBeLoaded(titleSceneName))
        {
            Debug.LogError($"[GameTitleRestart] 目标标题场景 “{titleSceneName}” 不存在或不在 Build Settings 中，无法回标题重玩。\n" +
                           "请把它加入 Build Settings（可执行菜单 GGJ2026/关卡/把关卡加入 Build Settings）。");
            return;
        }

        IsRestartInProgress = true;

        // 用独立常驻物体执行：LoadScene(Single) 会销毁当前场景里的一切（包括调用方），
        // 协程挂在场景物体上会被打断，所以整个流程放到 DontDestroyOnLoad 的执行体里跑。
        GameObject runner = new GameObject("GGJ_ReturnToTitleRunner");
        Object.DontDestroyOnLoad(runner);
        runner.AddComponent<GameTitleRestartRunner>().Begin(titleSceneName);
    }

    /// <summary>由执行体结束时调用，解除重入保护。</summary>
    internal static void NotifyFinished()
    {
        IsRestartInProgress = false;
    }
}

/// <summary>
/// “回标题重开”的实际执行体（挂在临时 DontDestroyOnLoad 物体上，跑完自毁）。
/// 与 GameTitleRestart 同文件，仅供其内部使用。
/// </summary>
public class GameTitleRestartRunner : MonoBehaviour
{
    private string titleSceneName;
    private Image blackImage;
    private bool needCleanup = true;

    /// <summary>全黑遮罩淡出时长（让新的标题画面平滑出现）。</summary>
    private const float FadeOutSeconds = 0.6f;

    public void Begin(string sceneName)
    {
        titleSceneName = sceneName;

        // 1. 立即盖全屏黑，避免“清场 / 切场景”瞬间的画面闪白/闪灰
        CreateBlackOverlay();

        // 2. 复位可能遗留的全局转场锁 / 相机触发抑制位
        CameraTriggerVolume.SuppressCameraSwitching = false;
        LevelTransitionZone.ReleaseTransitionLock();

        // 3. 清掉跨场景常驻的旧状态（玩家 / 相机 / GameFlowController / 标题UI / HUD / 音频等）
        DestroyPersistentSceneObjects();

        // 4. 下一帧（Destroy 已生效、旧常驻对象 OnDestroy 已把各自单例置空）再加载标题场景
        StartCoroutine(Run());
    }

    private IEnumerator Run()
    {
        // 等一帧：让 Begin 里 Destroy 的常驻对象完成销毁、单例引用复位，
        // 避免新场景对象 Awake 时撞上“还指向上一个实例”的旧单例。
        yield return null;

        // 加载标题场景（同时卸载当前关卡场景）
        AsyncOperation load = SceneManager.LoadSceneAsync(titleSceneName, LoadSceneMode.Single);
        if (load == null)
        {
            SceneManager.LoadScene(titleSceneName, LoadSceneMode.Single);
        }
        else
        {
            while (!load.isDone)
                yield return null;
        }

        // 等新场景的 GameFlowController / TitleScreenUI 等 Awake / Start 跑起来
        yield return null;
        yield return null;

        // 淡出黑幕，露出全新的标题画面（Attract 灰屏循环跑）
        yield return FadeBlackOutRoutine(FadeOutSeconds);

        Finish();
    }

    // ---------------------------------------------------------------- 全屏黑幕

    private void CreateBlackOverlay()
    {
        GameObject canvasGO = new GameObject("BlackOverlayCanvas",
            typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGO.transform.SetParent(transform, false);

        Canvas canvas = canvasGO.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 2000; // 盖过 GameplayHUD(100) / EndingSequence(1000) 等一切 UI

        CanvasScaler scaler = canvasGO.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        GameObject blackGO = new GameObject("Black", typeof(RectTransform));
        blackGO.transform.SetParent(canvasGO.transform, false);

        RectTransform rt = blackGO.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        Image image = blackGO.AddComponent<Image>();
        image.color = Color.black;
        image.raycastTarget = true; // 全程黑幕期间挡住一切点击
        blackImage = image;
    }

    private IEnumerator FadeBlackOutRoutine(float duration)
    {
        if (blackImage == null)
            yield break;

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            SetBlackAlpha(Mathf.Lerp(1f, 0f, Mathf.Clamp01(elapsed / duration)));
            yield return null;
        }

        // 淡出结束：黑幕变成透明，不能再挡点击
        SetBlackAlpha(0f);
        if (blackImage != null)
            blackImage.raycastTarget = false;
    }

    private void SetBlackAlpha(float alpha)
    {
        if (blackImage == null)
            return;

        Color c = blackImage.color;
        c.a = Mathf.Clamp01(alpha);
        blackImage.color = c;
    }

    // ---------------------------------------------------------------- 清场

    /// <summary>销毁 DontDestroyOnLoad 场景里的全部常驻根物体（保留自身 runner）。</summary>
    private void DestroyPersistentSceneObjects()
    {
        // 本物体已被 DontDestroyOnLoad，其 gameObject.scene 即为常驻场景
        Scene persistentScene = gameObject.scene;
        GameObject[] roots = null;
        if (persistentScene.IsValid() && persistentScene.isLoaded)
            roots = persistentScene.GetRootGameObjects();
        else
            roots = FindDontDestroyOnLoadRootsFallback();

        if (roots == null)
            return;

        foreach (GameObject root in roots)
        {
            if (root == null || root == gameObject)
                continue;

            Debug.Log($"[GameTitleRestart] 清场常驻对象：{root.name}", root);
            Destroy(root);
        }
    }

    /// <summary>兜底：按名字遍历所有已加载物体，找出 DontDestroyOnLoad 场景的根物体。</summary>
    private static GameObject[] FindDontDestroyOnLoadRootsFallback()
    {
        GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
        List<GameObject> roots = new List<GameObject>();
        foreach (GameObject go in all)
        {
            if (go == null || go.scene.name != "DontDestroyOnLoad")
                continue;
            if (go.transform.parent != null)
                continue;
            roots.Add(go);
        }
        return roots.ToArray();
    }

    // ---------------------------------------------------------------- 收尾

    private void Finish()
    {
        if (!needCleanup)
            return;
        needCleanup = false;

        GameTitleRestart.NotifyFinished();
        Destroy(gameObject);
    }

    private void OnDestroy()
    {
        // 兜底：无论协程怎么退出都解除重入保护
        if (needCleanup)
        {
            needCleanup = false;
            GameTitleRestart.NotifyFinished();
        }
    }
}
