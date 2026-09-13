using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 全局文本配置（GGJTextConfig）的准备工作：
///   保证 Assets/0_File_GGJ2026/Resources/GGJTextConfig.asset 存在，这样你随时双击它就能改文字。
///
/// 生效方式：
///   · 脚本编译完成后自动检查一次，资源不存在才自动创建（已存在则绝不覆盖你改过的内容）；
///   · 也可以随时手动：菜单 GGJ2026 → 文本提示配置 → 创建或定位文本配置。
/// </summary>
public static class GGJTextConfigSetup
{
    /// <summary>配置资源所在目录（Resources 下的资源可在运行时按名加载）。</summary>
    private const string ResourcesFolder = "Assets/0_File_GGJ2026/Resources";

    /// <summary>配置资源完整路径。</summary>
    public const string AssetPath = ResourcesFolder + "/" + GGJTextConfig.ResourcesName + ".asset";

    // ------------------------------------------------------------------ 自动执行

    [InitializeOnLoadMethod]
    private static void AutoCreateWhenScriptsCompile()
    {
        // 等编辑器资源数据库就绪再写资源，避免在加载阶段操作 AssetDatabase
        EditorApplication.delayCall += () => EnsureAsset(false);
    }

    // ------------------------------------------------------------------ 手动菜单

    [MenuItem("GGJ2026/文本提示配置/创建或定位文本配置（GGJTextConfig.asset）", false, 100)]
    private static void CreateOrLocate()
    {
        GGJTextConfig config = EnsureAsset(true);
        if (config == null)
            return;

        Selection.activeObject = config;
        EditorGUIUtility.PingObject(config);
    }

    // ------------------------------------------------------------------ 核心逻辑

    /// <summary>确保配置资源存在；返回该资源（创建失败时返回已存在的或 null）。</summary>
    private static GGJTextConfig EnsureAsset(bool logWhenExists)
    {
        GGJTextConfig existing = AssetDatabase.LoadAssetAtPath<GGJTextConfig>(AssetPath);
        if (existing != null)
        {
            if (logWhenExists)
                Debug.Log($"[GGJTextConfig] 文本配置已存在：{AssetPath}\n双击该资源即可编辑提示文字。", existing);
            return existing;
        }

        try
        {
            if (!AssetDatabase.IsValidFolder(ResourcesFolder))
            {
                Directory.CreateDirectory(ResourcesFolder);
                AssetDatabase.Refresh();
            }

            GGJTextConfig config = ScriptableObject.CreateInstance<GGJTextConfig>();
            AssetDatabase.CreateAsset(config, AssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[GGJTextConfig] 已创建全局文本配置：{AssetPath}\n" +
                      "双击该资源即可编辑“能量不足”等提示文字，改完直接生效（不用改代码、不用改场景）。", config);
            return config;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[GGJTextConfig] 自动创建文本配置失败：{ex.Message}\n" +
                             $"可稍后手动执行菜单：GGJ2026 → 文本提示配置 → 创建或定位文本配置。");
            return null;
        }
    }
}
