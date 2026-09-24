using System.Collections;
using UnityEngine;

/// <summary>
/// 无缝循环音乐播放器（两个 AudioSource 轮流接力）：
///
///   一首曲子播到【尾部】时开始淡出 → 下一轮从【开头】同时淡入 → 两段声音在这段时间里叠在一起
///   → 接缝处听到的是“正在淡出去的尾巴 + 正在淡进来的开头”，不会有“突然消失、突然开始”的断口。
///
/// 另外素材结尾常常自带一段“越来越没声音”的尾巴（mp3 编码填充 / 制作时自带的收尾），
/// 这段用 Tail Trim Seconds 直接【跳过不播】，循环点在 Hz/by 之前的那一刻就交棒，
/// 所以循环里再也听不到那截没声音的部分。
///
/// 用法：AudioManager 勾选“无缝循环”后自动用它接管主音乐通道；一般不手动摆组件。
/// </summary>
[DisallowMultipleComponent]
public class MusicLoopCrossfade : MonoBehaviour
{
    [Header("接缝处理")]
    [Tooltip("重叠时长（秒）：曲子尾巴淡出 / 下一轮开头淡入的时间。\n" +
             "太短会听出接缝，太长会变成两段音乐混着放 —— 2~3 秒比较自然。")]
    [SerializeField] private float crossfadeSeconds = 2.5f;

    [Tooltip("素材结尾【不播】的长度（秒）：直接跳过。\n" +
             "这就是你说的“完全没声音的那一段”——把它跳掉，循环点就落在还有声音的位置上。\n" +
             "不确定就先给 0.8，听着还有没声音的尾巴就往上加（比如 1.5 / 2）。")]
    [SerializeField] private float tailTrimSeconds = 0.8f;

    [Tooltip("勾选（默认）= 等功率交叉淡化（cos/sin），重叠区中间的总响度不会鼓起来；\n" +
             "取消勾选 = 线性交叉（可能会在重叠中段感觉“变响一点”）。")]
    [SerializeField] private bool equalPowerCrossfade = true;

    // ---------------------------------------------------------------- 运行时

    private AudioSource sourceA;
    private AudioSource sourceB;
    private AudioSource current;
    private AudioSource next;

    private AudioClip clip;
    private bool running;

    private double currentStartDsp;
    private double nextStartDsp;
    private bool nextScheduled;

    private float volume;

    /// <summary>当前是否在播。</summary>
    public bool IsPlaying => running;
    /// <summary>当前正在播的素材（没播 = null）。</summary>
    public AudioClip Clip => clip;

    // ---------------------------------------------------------------- 对 AudioManager 的接口

    /// <summary>绑定两个音源（AudioManager 创建的那两个音乐通道）。</summary>
    public void Setup(AudioSource a, AudioSource b)
    {
        sourceA = a;
        sourceB = b;
        current = a;
        next = b;
    }

    /// <summary>同步参数（AudioManager 每次调用音乐切换时都会把它的配置推过来）。</summary>
    public void ApplySettings(float crossfade, float tailTrim)
    {
        crossfadeSeconds = crossfade;
        tailTrimSeconds = tailTrim;
    }

    /// <summary>
    /// 播放某条素材（循环）。已经在播同一条时只把音量平滑改到目标值，不打断。
    /// fadeInSeconds ≤ 0 = 一开口就是目标音量。
    /// </summary>
    public IEnumerator PlayRoutine(AudioClip newClip, float targetVolume, float fadeInSeconds)
    {
        if (sourceA == null || sourceB == null)
            yield break;

        if (newClip == null)
        {
            yield return FadeOutAndStopRoutine(0f);
            yield break;
        }

        // 已经在播同一条：只调整音量，不做交叉重启（否则换场景时音乐会从头开始）
        if (running && clip == newClip)
        {
            yield return FadeVolumeRoutine(volume, targetVolume, fadeInSeconds);
            yield break;
        }

        StopImmediate();

        clip = newClip;
        nextScheduled = false;
        nextStartDsp = 0d;

        double startDsp = AudioSettings.dspTime + 0.1d;
        currentStartDsp = startDsp;
        volume = fadeInSeconds > 0f ? 0f : targetVolume;

        BeginSource(current, currentStartDsp);
        running = true;

        if (fadeInSeconds > 0f)
            yield return FadeVolumeRoutine(0f, targetVolume, fadeInSeconds);
    }

    /// <summary>淡出后停掉（换曲 / 关掉 BGM）。</summary>
    public IEnumerator FadeOutAndStopRoutine(float seconds)
    {
        if (!running)
            yield break;

        if (seconds > 0f)
            yield return FadeVolumeRoutine(volume, 0f, seconds);

        StopImmediate();
    }

    /// <summary>立刻停（不留淡出）。</summary>
    public void StopImmediate()
    {
        if (sourceA != null) sourceA.Stop();
        if (sourceB != null) sourceB.Stop();

        running = false;
        clip = null;
        nextScheduled = false;
        volume = 0f;
    }

    // ---------------------------------------------------------------- 交叉淡化主循环

    private void Update()
    {
        if (!running || clip == null || current == null || next == null)
            return;

        // 有效循环长度：素材全长 减去 结尾那段要跳过的
        float loopLength = Mathf.Max(0.5f, clip.length - Mathf.Max(0f, tailTrimSeconds));
        // 重叠最短 0.05 秒，最长不超过半个循环，避免参数填坏时整首都在交叉
        float overlap = Mathf.Clamp(Mathf.Max(0.05f, crossfadeSeconds), 0.05f, loopLength * 0.5f);

        double now = AudioSettings.dspTime;

        if (!nextScheduled)
        {
            // 还没到交棒时刻：当前这条保持目标音量
            current.volume = volume;

            double elapsed = now - currentStartDsp;
            if (elapsed < loopLength - overlap)
                return;

            // 到点了：排好下一轮的开头，让它在 currentStartDsp + loopLength 那一刻【准时】响起来
            nextStartDsp = currentStartDsp + loopLength;
            if (nextStartDsp <= now + 0.02d)
                nextStartDsp = now + 0.05d;      // PlayScheduled 不能早于“现在”，兜个底

            BeginSource(next, nextStartDsp);
            nextScheduled = true;
            return;
        }

        // 交棒窗口：按 dsp 时间算进度（比 AudioSource.time 稳，不受帧率抖动影响）
        float k = Mathf.Clamp01((float)((now - nextStartDsp) / overlap));

        float gainCurrent;
        float gainNext;

        if (equalPowerCrossfade)
        {
            gainCurrent = Mathf.Cos(k * Mathf.PI * 0.5f);
            gainNext = Mathf.Sin(k * Mathf.PI * 0.5f);
        }
        else
        {
            gainCurrent = 1f - k;
            gainNext = k;
        }

        current.volume = volume * gainCurrent;
        next.volume = volume * gainNext;

        if (k < 1f)
            return;

        // 交接完成：旧的这条已经淡到 0，直接停掉（它本来就不会播到素材结尾那段）
        current.Stop();

        AudioSource finished = current;
        current = next;
        next = finished;

        currentStartDsp = nextStartDsp;
        nextScheduled = false;
        current.volume = volume;
        next.volume = 0f;
    }

    private void BeginSource(AudioSource src, double startDsp)
    {
        if (src == null)
            return;

        src.Stop();
        src.clip = clip;
        src.loop = false;
        src.volume = volume;
        src.time = 0f;
        src.PlayScheduled(startDsp);
    }

    private IEnumerator FadeVolumeRoutine(float from, float to, float seconds)
    {
        if (seconds <= 0f)
        {
            volume = to;
            current.volume = to;
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < seconds)
        {
            elapsed += Time.deltaTime;
            volume = Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / seconds));
            yield return null;
        }

        volume = to;
    }
}
