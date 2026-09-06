using System;
using System.IO;
using UnityEditor;
using UnityEngine;

#pragma warning disable 0618 // Get/SetIconsForTargetGroup 等旧 API 在 2022.3 中仍可用（仅有弃用提示），此处用于桌面平台图标

/// <summary>
/// 游戏图标设置工具：
///   把 3_Textures/GGJ2026_GameIcon.jpg 按官方要求的各尺寸自动缩放，写入
///   Player Settings → Player → Icon（Standalone / 桌面平台）。
///
/// 生效方式（无需手动点菜单）：
///   · 工程脚本编译完成后自动执行一次（已设置过图标则自动跳过，不会反复覆盖）；
///   · 素材导入完成也会补一次兜底；
///   · 也可以随时手动：菜单 GGJ2026 → 图标 → 重新应用游戏图标。
///
/// 处理策略：从原图中央裁出正方形（防止把底部标题条一起压扁），再等比缩放到各图标尺寸。
/// 若想要“整张图等比居中（上下留黑边）”的裁法，或换成别的图，改 IconPath 后重跑菜单即可。
/// </summary>
public static class GGJIconSetup
{
    /// <summary>游戏图标源文件（相对 Assets 的路径，需真实存在于工程内）。</summary>
    public const string IconPath = "Assets/0_File_GGJ2026/3_Textures/GGJ2026_GameIcon.jpg";

    // ------------------------------------------------------------------ 自动执行

    [InitializeOnLoadMethod]
    private static void AutoApplyWhenScriptsCompile()
    {
        EditorApplication.delayCall += () => ApplyIfPending();
    }

    private sealed class IconAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            // 素材（含图标本体）导入完成后兜底一次：如果当时图还没就绪，也能补上
            ApplyIfPending();
        }
    }

    private static void ApplyIfPending()
    {
        if (!File.Exists(IconPath))
            return;

        Texture2D[] existing = PlayerSettings.GetIconsForTargetGroup(BuildTargetGroup.Standalone);
        if (existing != null && Array.Exists(existing, tex => tex != null && tex.width >= 64))
            return; // 已经配置过图标，不再自动覆盖

        try
        {
            ApplyIcons();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[GGJIconSetup] 自动应用图标失败：{ex.Message}\n"
                             + $"可稍后手动执行菜单：GGJ2026 → 图标 → 重新应用游戏图标。");
        }
    }

    // ------------------------------------------------------------------ 手动菜单

    [MenuItem("GGJ2026/图标/重新应用游戏图标（3_Textures/GGJ2026_GameIcon.jpg）")]
    public static void ApplyIconsFromMenu()
    {
        try
        {
            ApplyIcons();
            EditorUtility.DisplayDialog("GGJ2026", "游戏图标已应用到 Player Settings（Standalone）。\n"
                                                  + "可在 Edit → Project Settings → Player → Icon 里查看。", "好");
        }
        catch (Exception ex)
        {
            EditorUtility.DisplayDialog("GGJ2026", "应用图标失败：" + ex.Message, "好");
        }
    }

    // ------------------------------------------------------------------ 核心逻辑

    public static void ApplyIcons()
    {
        if (!File.Exists(IconPath))
            throw new FileNotFoundException("图标源文件不存在，请先确认：" + IconPath);

        // 直接从磁盘读图（不依赖导入设置，加载出来即可读像素）
        byte[] bytes = File.ReadAllBytes(IconPath);
        Texture2D source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!source.LoadImage(bytes))
            throw new InvalidDataException("无法解析图片：" + IconPath);

        try
        {
            int[] sizes = PlayerSettings.GetIconSizesForTargetGroup(BuildTargetGroup.Standalone);
            if (sizes == null || sizes.Length == 0)
            {
                Debug.LogWarning("[GGJIconSetup] 当前平台没返回任何图标尺寸，用常见桌面尺寸兜底。");
                sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
            }

            Texture2D[] icons = new Texture2D[sizes.Length];
            for (int i = 0; i < sizes.Length; i++)
            {
                icons[i] = CropCenterSquare(source, sizes[i], sizes[i]);
            }

            PlayerSettings.SetIconsForTargetGroup(BuildTargetGroup.Standalone, icons);

            for (int i = 0; i < icons.Length; i++)
            {
                if (icons[i] != null)
                    UnityEngine.Object.DestroyImmediate(icons[i]);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[GGJIconSetup] 已把 {IconPath} 应用为桌面平台图标（共 {sizes.Length} 个尺寸）。");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(source);
        }
    }

    /// <summary>从源图中央裁出正方形，再等比缩放到目标尺寸。</summary>
    private static Texture2D CropCenterSquare(Texture2D source, int width, int height)
    {
        int side = Mathf.Min(source.width, source.height);
        int x0 = (source.width - side) / 2;
        int y0 = (source.height - side) / 2;

        Color[] srcPixels = source.GetPixels(x0, y0, side, side);

        Texture2D result = new Texture2D(width, height, TextureFormat.RGBA32, false);
        Color[] dst = new Color[width * height];

        for (int y = 0; y < height; y++)
        {
            float sy = (y + 0.5f) / height * side - 0.5f;
            int sy0 = Mathf.Clamp(Mathf.FloorToInt(sy), 0, side - 1);
            int sy1 = Mathf.Clamp(sy0 + 1, 0, side - 1);
            float fy = sy - sy0;

            for (int x = 0; x < width; x++)
            {
                float sx = (x + 0.5f) / width * side - 0.5f;
                int sx0 = Mathf.Clamp(Mathf.FloorToInt(sx), 0, side - 1);
                int sx1 = Mathf.Clamp(sx0 + 1, 0, side - 1);
                float fx = sx - sx0;

                Color c00 = srcPixels[sy0 * side + sx0];
                Color c10 = srcPixels[sy0 * side + sx1];
                Color c01 = srcPixels[sy1 * side + sx0];
                Color c11 = srcPixels[sy1 * side + sx1];

                Color top = Color.Lerp(c00, c10, fx);
                Color bottom = Color.Lerp(c01, c11, fx);
                dst[y * width + x] = Color.Lerp(top, bottom, fy);
            }
        }

        result.SetPixels(dst);
        result.Apply(false);
        result.name = $"GameIcon_{width}x{height}";
        return result;
    }
}
