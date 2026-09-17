using UnityEngine;

/// <summary>
/// 仅在打包版本中生效的“构建一致性保险”，让导出的画面和编辑器里跑的一致：
/// 1) 强制使用 High Fidelity 质量档——只有这一档绑定了 URP-HighFidelity，
///    里面挂着全部自定义 Renderer Feature（描边/材质覆盖/SSAO）和完整后处理；
///    导出后若回落到 Balanced / Performant，这些效果会整体消失。
/// 2) 强制 16:9 画面比例，避免不同显示器宽高比导致相机取景变化。
///
/// 编辑器内不生效，方便继续用任意 Game 视图比例调试。
/// </summary>
internal static class GGJBuildConsistencyGuard
{
    private const string TargetQualityName = "High Fidelity";

    // 与 Game 视图比例保持一致（16:9）。想让不同比例的显示器都满屏，
    // 把下面的 FullScreenMode.FullScreenWindow 改成 FullScreenMode.ExclusiveFullScreen。
    private const int TargetWidth = 1920;
    private const int TargetHeight = 1080;

#if !UNITY_EDITOR
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
    private static void Apply()
    {
        int level = System.Array.IndexOf(QualitySettings.names, TargetQualityName);
        if (level < 0)
        {
            // 找不到就用最高档兜底
            level = QualitySettings.names.Length - 1;
        }

        if (QualitySettings.GetQualityLevel() != level)
        {
            // applyExpensiveChanges = true：确保 Renderer Feature / 后处理资源立刻重建
            QualitySettings.SetQualityLevel(level, true);
        }

        if (Screen.width != TargetWidth || Screen.height != TargetHeight)
        {
            Screen.SetResolution(TargetWidth, TargetHeight, FullScreenMode.FullScreenWindow, 0);
        }
    }
#endif
}
