using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 通用“过一会儿自己重新出现”计时助手：给某个被 SetActive(false) 的物体安排延迟重新激活。
///
/// 为什么需要它：物体一旦被停用，它自己的 Update / 协程都会跟着停，没法给自己计时，
/// 所以计时托管到这个独立的小助手物体上（运行时自动创建，不需要在场景里摆）：
///   · 到点后把目标 SetActive(true)，目标自己在 OnEnable 里复位
///     （DisappearingPlatform 恢复成完好平台、EnergyPickup 恢复成可再次吸收的能量球）；
///   · 目标若已经被别的系统提前恢复（例如按 R 回到安全点把它重新激活了），这里会跳过，
///     不会重复激活、也不会重复触发它的事件；
///   · 同一个目标可以安排多次，各自独立计时；
///   · 本物体不属于任何关卡内容，不跨场景保留（换场景时随场景销毁，未到点的恢复直接作废）。
/// </summary>
public class GGJRespawnTimer : MonoBehaviour
{
    private static GGJRespawnTimer instance;

    private readonly List<Entry> entries = new List<Entry>();

    private class Entry
    {
        public GameObject target;
        public float resumeTime;
    }

    /// <summary>安排一次“延迟重新激活”：delaySeconds 秒后把 target 激活。target 为空或 delaySeconds 小于等于 0 时忽略。</summary>
    public static void Schedule(GameObject target, float delaySeconds)
    {
        if (target == null || delaySeconds <= 0f)
            return;

        EnsureInstance();
        instance.entries.Add(new Entry
        {
            target = target,
            resumeTime = Time.time + delaySeconds
        });
    }

    private static void EnsureInstance()
    {
        if (instance != null)
            return;

        GameObject go = new GameObject("GGJ_RespawnTimer");
        instance = go.AddComponent<GGJRespawnTimer>();
    }

    private void Update()
    {
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            Entry entry = entries[i];

            if (entry.target == null)
            {
                entries.RemoveAt(i);
                continue;
            }

            if (Time.time < entry.resumeTime)
                continue;

            entries.RemoveAt(i);

            if (!entry.target.activeSelf)
                entry.target.SetActive(true);
        }
    }
}
