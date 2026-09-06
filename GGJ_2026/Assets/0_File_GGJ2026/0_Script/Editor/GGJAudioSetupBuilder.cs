using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 自动把 4_Temp_Xiaoteng_Audio 下的音频素材接入到 Resources/GGJ_AudioManager 预制体
/// （预制体只保存素材引用和音量，方便你在 Inspector 里改；运行时由 AudioManager.EnsureCreated 实例化）。
///
/// 用法：
///  · 脚本编译后会自动检测一次：缺少预制体时自动生成（无需手动操作）；
///  · 素材文件被移动 / 需要重新接一遍时：菜单 GGJ2026/音频/重建音频管理预制体（自动接入素材）。
/// </summary>
public static class GGJAudioSetupBuilder
{
    private const string AudioRoot = "Assets/4_Temp_Xiaoteng_Audio";
    private const string PrefabDir = "Assets/0_File_GGJ2026/Resources";
    private const string PrefabPath = PrefabDir + "/GGJ_AudioManager.prefab";

    private const string MusicClipPath = AudioRoot + "/Music/MUS_Gameplay_Main.wav";
    private const string AmbienceClipPath = AudioRoot + "/Ambience/AMB_Lab_Main.wav";

    private const string PickupClipPath = AudioRoot + "/SFX/SFX_Energy/SFX_Energy_Pickup.wav";
    private const string ReturnClipPath = AudioRoot + "/SFX/SFX_Energy/SFX_Energy_Return.wav";
    private const string InjectClipPath = AudioRoot + "/SFX/SFX_Energy/SFX_Energy_Inject.wav";

    private const string FootstepClipPrefix = AudioRoot + "/SFX/SFX_Footstep/SFX_Footstep_Lab/SFX_Footstep_Lab_";
    private const string JumpClipPrefix = AudioRoot + "/SFX/SFX_Player/SFX_Player_Jump_";
    private const string LandClipPrefix = AudioRoot + "/SFX/SFX_Player/SFX_Player_Land_";

    private const int FootstepClipCount = 6;
    private const int JumpClipCount = 5;
    private const int LandClipCount = 5;

    // ---------------------------------------------------------------- 自动补建

    /// <summary>编译/进入编辑器后自动跑一次：预制体不存在时自动生成。已存在则跳过，不影响手动调过的音量。</summary>
    [InitializeOnLoadMethod]
    private static void AutoBuildIfMissing()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        if (File.Exists(PrefabPath))
            return;

        try
        {
            BuildAudioManagerPrefab();
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[GGJAudio] 自动接入音频素材失败：" + e.Message +
                             "\n可稍后手动执行菜单 GGJ2026/音频/重建音频管理预制体 重试。");
        }
    }

    // ---------------------------------------------------------------- 菜单入口

    [MenuItem("GGJ2026/音频/重建音频管理预制体（自动接入素材）")]
    public static void BuildAudioManagerFromMenu()
    {
        BuildAudioManagerPrefab();
        EditorUtility.DisplayDialog("GGJ2026 音频",
            $"已生成 / 重建音频管理预制体：\n{PrefabPath}\n\n" +
            "素材引用与音量都已接好，可在 Project 窗口打开该预制体微调响度。", "知道了");
    }

    // ---------------------------------------------------------------- 构建

    private static void BuildAudioManagerPrefab()
    {
        EnsureFolder(PrefabDir);

        // 关掉旧预制体对应的临时文件缓存（存在则先卸载）
        AudioClip music = LoadClip(MusicClipPath);
        AudioClip ambience = LoadClip(AmbienceClipPath);
        AudioClip pickup = LoadClip(PickupClipPath);
        AudioClip ret = LoadClip(ReturnClipPath);
        AudioClip inject = LoadClip(InjectClipPath);
        AudioClip[] footsteps = LoadSequentialClips(FootstepClipPrefix, FootstepClipCount);
        AudioClip[] jumps = LoadSequentialClips(JumpClipPrefix, JumpClipCount);
        AudioClip[] lands = LoadSequentialClips(LandClipPrefix, LandClipCount);

        if (music == null || ambience == null)
        {
            Debug.LogError("[GGJAudio] 音乐 / 环境音素材缺失，中止生成。请确认 4_Temp_Xiaoteng_Audio 里" +
                           "存在 MUS_Gameplay_Main.wav 与 AMB_Lab_Main.wav。");
            return;
        }

        GameObject go = new GameObject("GGJ_AudioManager");
        AudioManager am = go.AddComponent<AudioManager>();

        am.musicClip = music;
        am.ambienceClip = ambience;
        am.pickupClip = pickup;
        am.returnClip = ret;
        am.injectClip = inject;
        am.footstepClips = footsteps;
        am.jumpClips = jumps;
        am.landClips = lands;
        am.ambienceSceneName = "Level1";

        // 响度规范（可在预制体上再微调）：
        //   环境音微微可闻 < 音乐中等 < 一次性音效最大且明显盖过其它
        am.musicVolume = 0.5f;
        am.ambienceVolume = 0.1f;
        am.sfxVolume = 1f;

        // 覆盖同名预制体
        PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
        Object.DestroyImmediate(go);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log("[GGJAudio] 音频管理预制体已生成：" + PrefabPath + "（素材引用 / 音量已接好）");
    }

    // ---------------------------------------------------------------- 工具

    private static AudioClip LoadClip(string path)
    {
        AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        if (clip == null)
            Debug.LogWarning("[GGJAudio] 找不到音频素材：" + path);
        return clip;
    }

    private static AudioClip[] LoadSequentialClips(string prefix, int count)
    {
        List<AudioClip> clips = new List<AudioClip>(count);
        for (int i = 1; i <= count; i++)
        {
            AudioClip clip = LoadClip(prefix + i + ".wav");
            if (clip != null)
                clips.Add(clip);
        }
        return clips.ToArray();
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
            return;

        int idx = path.LastIndexOf('/');
        string parent = idx > 0 ? path.Substring(0, idx) : "Assets";
        string name = path.Substring(idx + 1);

        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, name);
    }
}
