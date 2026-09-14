using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 全局“复活流程”总闸：RespawnZone（走进死亡区）与 PipelineHazard（被流水线危险货物撞到）共用。
///
/// 为什么需要这个总闸：
///   两个组件原本只检查自己的 inProgress / respawning，彼此不知道对方在跑。玩家在死亡演出的
///   黑屏 → 传送 → 文字停留这段时间里（输入已关，但角色仍可能滑行 / 被货物推着走），
///   如果又碰到另一个死亡区或另一件货物，就会跑起第二套流程，于是：
///     · 第二套流程记下的“原本输入状态”是【已关闭】，结束时把输入恢复成关闭 → 复活后走不动；
///     · 两套流程各自传送、各自开关 CharacterController、各自淡入淡出黑幕 →
///       角色姿态错乱（“躺着复活”）、黑幕卡住、提示文字提前消失；
///     · 落点要是还在危险范围内，会被下一件货物立刻再撞一次，反复复活。
///
/// 用法（两套流程都一样）：
///   if (!RespawnFlow.TryBegin()) return;   // 拿不到锁就什么都不做：不关输入、不开黑幕
///   ...                                     // 淡黑 → 文字 → 传送 → 淡出
///   finally { RespawnFlow.End(); }          // 一定在 finally 里还锁
/// </summary>
public static class RespawnFlow
{
    /// <summary>复活结束后的免疫时长（秒）：这段时间内不再触发新的复活，避免落点仍在危险区里被连击。</summary>
    public const float DefaultGraceSeconds = 0.6f;

    private static bool running;
    private static float graceSeconds = DefaultGraceSeconds;
    private static float graceUntil;

    /// <summary>当前是否有一套复活流程正在跑（黑幕 / 传送 / 文字停留都算）。</summary>
    public static bool IsRunning => running;

    /// <summary>是否处于“不该触发新复活”的状态：流程正在跑，或刚复活完的免疫期内。</summary>
    public static bool IsProtected => running || Time.unscaledTime < graceUntil;

    /// <summary>
    /// 现在能不能开始一次新的复活流程：
    /// 没人在复活 + 不在免疫期 + 不在暂停 / 关卡转场 / 重开流程中。
    /// </summary>
    public static bool CanStart
    {
        get
        {
            if (IsProtected)
                return false;

            if (PauseMenuManager.IsPaused)
                return false;

            if (LevelTransitionZone.AnyTransitionRunning)
                return false;

            if (GameTitleRestart.IsRestartInProgress)
                return false;

            return true;
        }
    }

    /// <summary>
    /// 尝试独占复活流程。返回 true 才允许开始；false = 此刻不该复活，调用方直接 return 即可。
    /// </summary>
    /// <param name="graceSecondsAfterEnd">本次复活结束后要保留的免疫时长。</param>
    public static bool TryBegin(float graceSecondsAfterEnd = DefaultGraceSeconds)
    {
        if (!CanStart)
            return false;

        running = true;
        graceSeconds = Mathf.Max(0f, graceSecondsAfterEnd);
        return true;
    }

    /// <summary>流程结束：还锁，并开一小段免疫期。请在协程的 finally 里调用。</summary>
    public static void End()
    {
        if (!running)
            return;

        running = false;
        graceUntil = Time.unscaledTime + graceSeconds;
    }

    // ---------------------------------------------------------------- 朝向

    /// <summary>
    /// 复活点有没有被关卡作者“特意摆过朝向”。
    /// 摆过（旋转不是单位四元数）→ 用它指定的朝向；没摆过（默认 0,0,0）→ 保持角色原本的左右朝向，
    /// 不要把角色硬转成面朝世界 +Z（横版里会变成正对 / 背对镜头，看着像“横过来了”）。
    /// </summary>
    public static bool HasCustomFacing(Transform point)
    {
        return point != null && Quaternion.Angle(point.rotation, Quaternion.identity) > 1f;
    }

    /// <summary>
    /// 取复活点的“水平朝向”（世界 Y 轴角度，单位：度）。
    ///
    /// 不能直接把 point.rotation 整个套给玩家：复活点若是斜的，或者挂在旋转过的父物体下，
    /// 它的俯仰 / 翻滚会被一起带给角色 —— 表现为复活后“躺着 / 倒着出现在地上”。
    /// 这里把它的 forward 投影到水平面，只保留转身方向。
    /// </summary>
    /// <param name="point">复活点。</param>
    /// <param name="fallbackYaw">复活点没有可用的水平朝向时（比如竖直朝上）返回的朝向。</param>
    public static float HorizontalYawOf(Transform point, float fallbackYaw)
    {
        if (point == null)
            return fallbackYaw;

        Vector3 forward = point.forward;
        forward.y = 0f;

        if (forward.sqrMagnitude > 1e-6f)
            return Quaternion.LookRotation(forward).eulerAngles.y;

        // forward 几乎垂直：改用 right 再试一次，仍不行就保持原朝向
        Vector3 right = point.right;
        right.y = 0f;

        if (right.sqrMagnitude > 1e-6f)
            return Quaternion.LookRotation(Vector3.Cross(Vector3.up, right)).eulerAngles.y;

        return fallbackYaw;
    }

    /// <summary>强制清锁（切场景 / 重开时用）：防止流程被中断（例如物体被销毁）后锁永久残留。</summary>
    public static void Reset()
    {
        running = false;
        graceUntil = 0f;
    }

    // 关闭 Domain Reload 时静态字段不会自动清零；同时每次载入场景清一次，
    // 避免“上一关的复活流程没跑完就被切场景”导致新关卡里再也复活不了。
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Initialize()
    {
        running = false;
        graceSeconds = DefaultGraceSeconds;
        graceUntil = 0f;

        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Reset();
    }
}
