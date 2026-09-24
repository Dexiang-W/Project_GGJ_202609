using UnityEngine;

/// <summary>
/// 仅在打包版本中生效的“构建一致性保险”：确保运行时使用 High Fidelity 质量档。
///
/// 只有这一档绑定了 URP-HighFidelity——项目的高亮（描边 / 材质覆盖 Renderer Feature）
/// 和后处理都挂在这一套管线资源上。导出后若回落到 Balanced / Performant，
/// 高亮和后处理会整体消失，所以这里兜一下底。
///
/// 除此之外不做任何干预：分辨率、全屏模式、画面比例、渲染缩放全部使用项目默认设置。
/// 编辑器内不生效，方便继续调试。
/// </summary>
internal static class GGJBuildConsistencyGuard
{
    private const string TargetQualityName = "High Fidelity";

#if !UNITY_EDITOR
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
    private static void Apply()
    {
        int level = System.Array.IndexOf(QualitySettings.names, TargetQualityName);
        if (level < 0 || QualitySettings.GetQualityLevel() == level)
            return;

        // applyExpensiveChanges = true：确保 Renderer Feature / 后处理资源立刻重建
        QualitySettings.SetQualityLevel(level, true);
    }
#endif
}
