using UnityEngine;

/// <summary>
/// 全局文本提示配置（ScriptableObject）：把“写死在代码里的固定提示文字”集中到一个资源里，
/// 不写代码、不改场景就能直接编辑。
///
/// 资源位置：Assets/0_File_GGJ2026/Resources/GGJTextConfig.asset
///   · 回调 Unity 编辑器时若资源不存在会自动创建（见 Editor/GGJTextConfigSetup.cs）；
///   · 也可用菜单 “GGJ2026/文本提示配置/创建或定位文本配置” 手动创建并定位；
///   · 资源不存在时，下面所有 Get 方法都会回退到内置默认文案，游戏照常运行。
///
/// 想再加一条可编辑文本：在下面加一个 [SerializeField] 字符串字段，再补一个静态 Get 方法即可。
/// </summary>
[CreateAssetMenu(fileName = "GGJTextConfig", menuName = "GGJ2026/文本提示配置（Text Config）", order = 0)]
public class GGJTextConfig : ScriptableObject
{
    /// <summary>Resources 下的文件名（不含扩展名），运行时按这个名加载。</summary>
    public const string ResourcesName = "GGJTextConfig";

    /// <summary>“能量不足”提示的内置默认文案（配置资源缺失时使用）。</summary>
    public const string DefaultInsufficientEnergyHint = "能量不足，先去找能量球收集能量！";

    /// <summary>“能量不足”提示的内置默认停留时长（秒）。</summary>
    public const float DefaultInsufficientEnergyHintSeconds = 1.2f;

    /// <summary>“被流水线危险货物撞到”复活提示的内置默认文案（配置资源缺失 / 该条留空时使用）。</summary>
    public const string DefaultPipelineHazardMessage = "你回到了重生点……";

    [Header("能量不足提示（无能量时和能量植物 / 蘑菇交互弹出）")]
    [Tooltip("玩家能量不足、无法给能量植物 / 能量蘑菇充能时，屏幕中部弹出的提示文字。\n支持 \\n 手动换行；留空则用内置默认文案。")]
    [TextArea(1, 4)]
    [SerializeField] private string insufficientEnergyHint = DefaultInsufficientEnergyHint;

    [Tooltip("该提示自动淡隐前的停留时长（秒）。")]
    [Min(0.1f)]
    [SerializeField] private float insufficientEnergyHintSeconds = DefaultInsufficientEnergyHintSeconds;

    /// <summary>“能量不足”提示文字（留空时回退到内置默认文案）。</summary>
    public string InsufficientEnergyHint =>
        string.IsNullOrEmpty(insufficientEnergyHint) ? DefaultInsufficientEnergyHint : insufficientEnergyHint;

    /// <summary>“能量不足”提示停留时长（秒，至少 0.1 秒）。</summary>
    public float InsufficientEnergyHintSeconds => Mathf.Max(0.1f, insufficientEnergyHintSeconds);

    [Header("复活提示（被流水线危险货物撞到 = PipelineHazard）")]
    [Tooltip("玩家被流水线货物撞到、复活后，屏幕左侧显示的提示文字（位置 / 字体 / 淡入淡出与 RespawnZone 完全一致）。\n" +
             "支持 \\n 手动换行；留空则用内置默认文案。\n" +
             "某件货物想单独用别的文字：在该 prefab 的 PipelineHazard 上填「复活提示文字」，填了就以它为准。")]
    [TextArea(1, 4)]
    [SerializeField] private string pipelineHazardMessage = DefaultPipelineHazardMessage;

    /// <summary>“被流水线危险货物撞到”复活提示文字（留空时回退到内置默认文案）。</summary>
    public string PipelineHazardMessage =>
        string.IsNullOrEmpty(pipelineHazardMessage) ? DefaultPipelineHazardMessage : pipelineHazardMessage;

    /// <summary>按名从 Resources 读取配置资源；不存在时返回 null（调用方用静态 Get 方法即可自动兜底）。</summary>
    public static GGJTextConfig Instance => Resources.Load<GGJTextConfig>(ResourcesName);

    /// <summary>取“能量不足”提示文字（没有配置资源时用内置默认）。</summary>
    public static string GetInsufficientEnergyHint()
    {
        GGJTextConfig config = Instance;
        return config != null ? config.InsufficientEnergyHint : DefaultInsufficientEnergyHint;
    }

    /// <summary>取“能量不足”提示停留时长（没有配置资源时用内置默认）。</summary>
    public static float GetInsufficientEnergyHintSeconds()
    {
        GGJTextConfig config = Instance;
        return config != null ? config.InsufficientEnergyHintSeconds : DefaultInsufficientEnergyHintSeconds;
    }

    /// <summary>取“被流水线危险货物撞到”复活提示文字（没有配置资源时用内置默认）。</summary>
    public static string GetPipelineHazardMessage()
    {
        GGJTextConfig config = Instance;
        return config != null ? config.PipelineHazardMessage : DefaultPipelineHazardMessage;
    }
}
