using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 控制 URP Full Screen Pass Renderer Feature 的雨效淡入淡出。
/// 用法：把本脚本挂到某空物体上，拖入 M_PP_ScreenRain，调时间参数。
///
/// 关键设计（避免卡顿）：
///  · 播放过程中【从不开关 Feature】。Renderer Feature 首次解析后保持常开，
///    淡入淡出只写材质里的 Alpha 属性（opacityProperty），透明度调到 0 即视为隐藏。
///    反复 toggle Full Screen Pass 会触发渲染状态重建，这正是淡入淡出卡的根源。
///  · Feature 只允许在“看不见的时候”切换：首次进入关卡（黑屏/加载期）开一次，
///    物体销毁/换场景时在卸载期关掉并还原材质引用。
///  · 淡出支持曲线（fadeOutCurve）；音频音量与画面强度同步。
///  · 运行时材质只在首次解析时克隆一次并复用，避免反复 new Material。
///  · 淡出完成后按 clearOnFadeOutComplete 立即清理（关 Feature、还原材质）；此时画面已全透明，
///    清理发生在玩家不可见的状态下，不会造成卡顿。默认开启。
/// </summary>
public class FullScreenPassFader : MonoBehaviour
{
    public enum TriggerMode
    {
        [InspectorName("对象激活时自动播放")] Auto,
        [InspectorName("玩家走进触发区域播放一次")] Trigger,
        [InspectorName("进入区域下雨，离开区域淡出")] Zone
    }

    [Header("目标 Renderer Feature")]
    [Tooltip("挂载 Full Screen Pass 的 Renderer Data；留空会自动使用当前 URP 管线的第一个 Renderer Data")]
    public ScriptableRendererData rendererData;

    [Tooltip("用来匹配 Feature 的 Pass Material（把 M_PP_ScreenRain 拖进来）")]
    public Material passMaterial;

    [Header("触发方式")]
    [Tooltip("Auto：对象激活即播放一轮；Trigger：玩家走进本物体的 Trigger Collider 时播放一轮")]
    public TriggerMode startMode = TriggerMode.Auto;

    [Tooltip("Trigger 模式下匹配的角色 Tag（默认 Player）")]
    public string playerTag = "Player";

    [Tooltip("Trigger 模式下只响应第一次进入（播放一轮后再次走进不再触发；重新播放会再次生效）")]
    public bool triggerOnlyOnce = true;

    [Header("Zone 模式（进入区域下雨 / 离开区域淡出）")]
    [Tooltip("Zone 模式：对象启用后立即开始下雨（适合“关卡一开始就下雨”）；取消则等玩家进入触发区域才开始")]
    public bool zoneRainOnStart = true;

    [Tooltip("Zone 模式：玩家离开触发区域时自动淡出；取消则只能由外部调用 StopRain 淡出")]
    public bool zoneFadeOutOnExit = true;

    [Tooltip("播放结束后是否循环")]
    [SerializeField] private bool repeat = false;

    [Header("时间区间（秒，以播放开始时刻为 0）")]
    [Tooltip("从开始到开始淡入的等待时间")]
    public float startDelay = 0f;

    [Tooltip("淡入时长")]
    public float fadeInDuration = 1f;

    [Tooltip("完全开启后保持的时长")]
    public float holdDuration = 3f;

    [Tooltip("淡出时长")]
    public float fadeOutDuration = 1f;

    [Header("淡入淡出属性")]
    [Tooltip("材质里用来控制整体透明度的 float 属性名（如 M_PP_ScreenRain 里的 Alpha=_Alpha）；若 Shader 里不存在则使用下方 Fallback")]
    public string opacityProperty = "_Alpha";

    [Tooltip("当 opacityProperty 不存在时，把这些属性的原始值按强度相乘，实现近似淡入淡出")]
    public List<string> fallbackIntensityProperties = new List<string>
    {
        "_RainDropsNormalStrength",
        "_RainDripsNormalStrength",
        "_RainDropsPower",
        "_RainDropsVariationPower"
    };

    [Tooltip("淡出曲线：X=淡出进度(0~1)，Y=画面强度(0~1)。默认先慢后快再缓的缓出")]
    public AnimationCurve fadeOutCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);

    [Header("结束后处理")]
    [Tooltip("勾选（默认）：淡出完成且非循环播放时立即清理——关闭 Feature、还原 Renderer Data 材质引用并销毁运行时克隆。\n此刻画面已全透明、玩家看不见，清理不会造成卡顿。\n取消：淡出后保持 Feature 常开、透明度 0，直到本物体销毁/换场景时才清理。")]
    public bool clearOnFadeOutComplete = true;

    [Header("同步音频（随画面一起淡入淡出）")]
    [Tooltip("淡入淡出期间同步播放的音频源（例如雨声）。留空会用本物体上的 AudioSource 或自动创建")]
    public AudioSource audioSource;

    [Tooltip("要播放的音频片段（例如 AMB_Rain_Light.wav）；留空则用 audioSource 上已设置的 Clip")]
    public AudioClip audioClip;

    [Tooltip("淡入到位后的目标音量（雨声建议 0.2~0.35）")]
    [Range(0f, 1f)]
    public float audioVolume = 0.25f;

    [Tooltip("未指定 audioSource 时，是否自动在本物体上创建 2D 循环声源")]
    public bool autoCreateAudioSource = true;

    [Header("优化（解决首帧卡顿）")]
    [Tooltip("Trigger 模式下，场景加载后提前把 Feature 开启并保持（透明度 0），让 shader 首帧编译/缓冲分配发生在加载期，触发淡入时不再顿卡")]
    public bool prewarmOnStart = true;

    private FullScreenPassRendererFeature targetFeature;
    private Material runtimeMaterial;
    private Material originalMaterial;
    private float originalOpacity = 1f;
    private readonly Dictionary<string, float> originalFallbackValues = new Dictionary<string, float>();
    private Coroutine routine;
    private bool hasTriggeredOnce;
    private bool zoneInside;
    private bool zoneStopRequested;
    private bool resolved;
    private bool valuesCached;
    private bool prewarmStarted;
    private bool featurePatched;
    private static bool warnedNoAudioClip;

    public bool IsPlaying => routine != null;

    // ---------------------------------------------------------------- 生命周期

    private void OnEnable()
    {
        if (startMode == TriggerMode.Auto)
            Play();
        else if (startMode == TriggerMode.Zone && zoneRainOnStart)
            StartRain();
    }

    private void OnDisable()
    {
        hasTriggeredOnce = false;
        zoneInside = false;
        zoneStopRequested = false;
        Stop();
    }

    private void Start()
    {
        if (startMode != TriggerMode.Auto && prewarmOnStart)
            StartCoroutine(PrewarmOnce());
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.isTrigger)
            return;

        if (!string.IsNullOrEmpty(playerTag) && !other.CompareTag(playerTag))
            return;

        // Zone 模式：进入区域就开始下雨（若已在流程中，则取消“离开淡出”的请求）
        if (startMode == TriggerMode.Zone)
        {
            zoneInside = true;
            if (routine != null)
                zoneStopRequested = false;
            else
                StartRain();
            return;
        }

        if (startMode != TriggerMode.Trigger)
            return;

        if (triggerOnlyOnce && hasTriggeredOnce)
            return;

        hasTriggeredOnce = true;
        Play();
    }

    private void OnTriggerExit(Collider other)
    {
        if (startMode != TriggerMode.Zone)
            return;

        if (other.isTrigger)
            return;

        if (!string.IsNullOrEmpty(playerTag) && !other.CompareTag(playerTag))
            return;

        // Zone 模式：离开区域触发淡出
        zoneInside = false;
        if (zoneFadeOutOnExit)
            StopRain();
    }

    /// <summary>
    /// 物体销毁 / 换场景时把 Renderer Data 恢复原样：关掉 Feature、还原材质引用、销毁运行时克隆。
    /// 只在卸载期做（画面不可见），避免 Feature 残留到下一个场景造成雨效串场或悬挂材质。
    /// </summary>
    private void OnDestroy()
    {
        StopAudio();
        CleanupRendererResources();
    }

    // ---------------------------------------------------------------- 对外控制

    [ContextMenu("播放")]
    public void Play()
    {
        Stop();
        routine = StartCoroutine(Run());
    }

    /// <summary>立即停止播放并停掉音频、把透明度归 0（不关 Feature，避免开关卡顿）。</summary>
    public void Stop()
    {
        if (routine != null)
        {
            StopCoroutine(routine);
            routine = null;
        }

        FinishSequence();
    }

    /// <summary>Zone 模式：开始下雨并一直保持，直到 StopRain 或玩家离开区域。
    /// 也可由外部脚本（例如自定义触发器）直接调用。</summary>
    [ContextMenu("开始下雨并保持")]
    public void StartRain()
    {
        if (routine != null)
        {
            zoneStopRequested = false;
            return;
        }

        zoneStopRequested = false;
        routine = StartCoroutine(RunZone());
    }

    /// <summary>Zone 模式：请求淡出。协程会在保持阶段收到请求后按 fadeOutDuration 淡出。</summary>
    [ContextMenu("停止下雨（淡出）")]
    public void StopRain()
    {
        if (routine != null)
            zoneStopRequested = true;
    }

    /// <summary>立即清理并关闭：停掉播放与音频，关闭 Feature、还原材质引用、销毁运行时克隆。
    /// 供手动测试（Inspector 右键菜单）或其它脚本/动画事件在确定不再需要雨效时调用。</summary>
    [ContextMenu("立即清理并关闭特效（测试）")]
    public void CleanupNow()
    {
        Stop();
        CleanupRendererResources();
    }

    // ---------------------------------------------------------------- 预热

    /// <summary>
    /// Trigger 模式在场景加载期调用：把 Feature 开启并保持（Alpha=0，看不见），
    /// 让 shader 首帧编译 / 渲染缓冲分配提前完成。之后播放只做 Alpha 插值，不再开关 Feature。
    /// </summary>
    private IEnumerator PrewarmOnce()
    {
        if (prewarmStarted)
            yield break;
        prewarmStarted = true;

        // 等一两帧，确保场景相机已就绪
        yield return null;
        yield return null;

        // Auto 模式已经在播放 / 已解析并处于开启状态时不需要再预热
        if (routine != null)
            yield break;
        if (resolved && targetFeature != null && targetFeature.isActive)
            yield break;

        if (!EnsureFeatureReady())
            yield break;

        // 保持开启并渲染一帧（此时 Alpha=0，画面无雨，只完成 shader/缓冲初始化）
        ApplyIntensity(0f);
        yield return null;
    }

    // ---------------------------------------------------------------- 播放流程

    private IEnumerator Run()
    {
        if (!EnsureFeatureReady())
            yield break;

        // 确保 Feature 已开启；先归零透明度，避免开启瞬间闪现完整强度的雨
        SetFeatureActive(true);
        ApplyIntensity(0f);

        if (startDelay > 0f)
            yield return new WaitForSeconds(startDelay);

        // 淡入（线性，只调 Alpha 属性）
        yield return FadeIn(fadeInDuration);

        // 保持
        if (holdDuration > 0f)
            yield return new WaitForSeconds(holdDuration);

        // 淡出（按 fadeOutCurve 走，结束时透明度为 0）
        yield return FadeOut(fadeOutDuration);

        FinishSequence();
        routine = null;

        if (repeat)
        {
            // 循环播放：需要保留运行时材质 / Feature 常开，不清理
            Play();
        }
        else if (clearOnFadeOutComplete)
        {
            // 一轮结束立即释放：关 Feature + 还原材质 + 销毁克隆。
            // 此刻画面已全透明（玩家看不见），清理不会造成卡顿。
            CleanupRendererResources();
        }
    }

    /// <summary>Zone 模式流程：淡入 → 持续保持（直到收到停止请求）→ 淡出 → 清理。</summary>
    private IEnumerator RunZone()
    {
        if (!EnsureFeatureReady())
        {
            routine = null;
            yield break;
        }

        SetFeatureActive(true);
        ApplyIntensity(0f);

        if (startDelay > 0f)
            yield return new WaitForSeconds(startDelay);

        yield return FadeIn(fadeInDuration);

        // 一直下雨，直到玩家离开区域 / 外部调用 StopRain
        while (!zoneStopRequested)
            yield return null;

        yield return FadeOut(fadeOutDuration);

        FinishSequence();
        routine = null;

        if (clearOnFadeOutComplete)
            CleanupRendererResources();

        // 淡出期间玩家又回到区域内：重新开始下雨
        if (startMode == TriggerMode.Zone && zoneInside)
            StartRain();
    }

    /// <summary>一轮结束 / 被中断时的收尾：停音频、透明度归 0。刻意不关 Feature、不还原材质。</summary>
    private void FinishSequence()
    {
        StopAudio();
        ApplyIntensity(0f);
    }

    private IEnumerator FadeIn(float duration)
    {
        if (duration <= 0f)
        {
            ApplyIntensity(1f);
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            ApplyIntensity(Mathf.Clamp01(elapsed / duration));
            yield return null;
        }

        ApplyIntensity(1f);
    }

    private IEnumerator FadeOut(float duration)
    {
        if (duration <= 0f)
        {
            ApplyIntensity(0f);
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            float v = fadeOutCurve != null && fadeOutCurve.length > 0
                ? fadeOutCurve.Evaluate(t)
                : 1f - t;
            ApplyIntensity(Mathf.Clamp01(v));
            yield return null;
        }

        ApplyIntensity(0f);
    }

    /// <summary>把“画面强度”0~1 映射到材质：优先写 opacityProperty（真实透明度），
    /// 其次才乘 fallback 属性。同时把音量与强度同步。</summary>
    private void ApplyIntensity(float intensity)
    {
        intensity = Mathf.Clamp01(intensity);

        if (runtimeMaterial != null && valuesCached)
        {
            if (runtimeMaterial.HasProperty(opacityProperty))
            {
                // 只写一个属性，淡入淡出期间 GPU 状态零变化，最省、最顺
                runtimeMaterial.SetFloat(opacityProperty, Mathf.Lerp(0f, originalOpacity, intensity));
            }
            else
            {
                foreach (var prop in fallbackIntensityProperties)
                {
                    if (runtimeMaterial.HasProperty(prop) && originalFallbackValues.ContainsKey(prop))
                    {
                        runtimeMaterial.SetFloat(prop, originalFallbackValues[prop] * intensity);
                    }
                }
            }
        }

        SyncAudio(intensity);
    }

    // ---------------------------------------------------------------- 资源准备

    /// <summary>首次使用时解析 Feature 并克隆一份运行时材质（只做一次，之后复用）。</summary>
    private bool EnsureFeatureReady()
    {
        if (resolved)
            return targetFeature != null;

        if (!ResolveFeatureAndMaterial())
            return false;

        resolved = true;
        CacheOriginalValues();
        valuesCached = true;
        EnsureAudioReady();

        // 首轮解析后就打开 Feature 并保持；此后淡入淡出不再触发开关
        SetFeatureActive(true);
        return true;
    }

    private void CacheOriginalValues()
    {
        originalOpacity = runtimeMaterial.HasProperty(opacityProperty)
            ? runtimeMaterial.GetFloat(opacityProperty)
            : 1f;

        originalFallbackValues.Clear();
        foreach (var prop in fallbackIntensityProperties)
        {
            if (runtimeMaterial.HasProperty(prop))
                originalFallbackValues[prop] = runtimeMaterial.GetFloat(prop);
        }
    }

    /// <summary>确保音频源可用并准备好要播的 Clip（自动创建/补齐）。</summary>
    private void EnsureAudioReady()
    {
        if (audioSource == null && autoCreateAudioSource)
        {
            audioSource = GetComponent<AudioSource>();
            if (audioSource == null)
            {
                audioSource = gameObject.AddComponent<AudioSource>();
                audioSource.spatialBlend = 0f;      // 全屏雨声用 2D，不随距离衰减
                audioSource.playOnAwake = false;
                audioSource.dopplerLevel = 0f;
            }
        }

        if (audioSource == null)
            return;

        if (audioClip != null && audioSource.clip != audioClip)
            audioSource.clip = audioClip;

        audioSource.loop = true;
        audioSource.volume = 0f;

        if (audioSource.clip == null && !warnedNoAudioClip)
        {
            warnedNoAudioClip = true;
            Debug.LogWarning("[FullScreenPassFader] 没有可播放的音频。请把 AMB_Rain_Light.wav 拖到 Audio Clip 字段。", this);
        }
    }

    /// <summary>音量与画面强度 0..1 同步：淡入时播放并抬高音量，淡出到底时停止。</summary>
    private void SyncAudio(float intensity)
    {
        if (audioSource == null || audioSource.clip == null)
            return;

        if (intensity > 0f && !audioSource.isPlaying)
            audioSource.Play();

        audioSource.volume = audioVolume * intensity;

        if (intensity <= 0f && audioSource.isPlaying)
            audioSource.Stop();
    }

    private void StopAudio()
    {
        if (audioSource == null)
            return;

        if (audioSource.isPlaying)
            audioSource.Stop();

        audioSource.volume = 0f;
    }

    private bool ResolveFeatureAndMaterial()
    {
        ScriptableRendererData[] dataList = CollectRendererDataList();

        if (dataList == null || dataList.Length == 0)
        {
            Debug.LogWarning("[FullScreenPassFader] 未获取到任何 Renderer Data。请把 URP-HighFidelity-Renderer.asset 拖到 Renderer Data 字段，或确认默认渲染管线已设置。", this);
            return false;
        }

        // 未显式指定材质时，按“材质名含 Rain”优先匹配，避免误中其它 Full Screen Pass（如 M_PP_Vignette）
        FullScreenPassRendererFeature rainCandidate = null;
        FullScreenPassRendererFeature fallbackCandidate = null;

        foreach (var data in dataList)
        {
            if (data == null)
                continue;

            foreach (var feature in data.rendererFeatures)
            {
                if (!(feature is FullScreenPassRendererFeature fs))
                    continue;

                Material featureMaterial = fs.passMaterial;
                if (featureMaterial == null)
                    continue;

                if (passMaterial != null)
                {
                    if (featureMaterial == passMaterial)
                    {
                        BindFeature(fs, featureMaterial);
                        return true;
                    }
                    continue;
                }

                if (rainCandidate == null &&
                    featureMaterial.name.IndexOf("Rain", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    rainCandidate = fs;
                }

                if (fallbackCandidate == null)
                    fallbackCandidate = fs;
            }
        }

        if (rainCandidate != null)
        {
            BindFeature(rainCandidate, rainCandidate.passMaterial);
            return true;
        }

        if (fallbackCandidate != null)
        {
            BindFeature(fallbackCandidate, fallbackCandidate.passMaterial);
            return true;
        }

        Debug.LogWarning("[FullScreenPassFader] 未找到目标 Full Screen Pass Renderer Feature。" +
            "请确认 Renderer Data 的 Feature 列表里有 Full Screen Pass，并把 M_PP_ScreenRain 拖到 Pass Material 字段。", this);
        return false;
    }

    private void BindFeature(FullScreenPassRendererFeature fs, Material featureMaterial)
    {
        targetFeature = fs;
        originalMaterial = featureMaterial;
        runtimeMaterial = new Material(featureMaterial);
        fs.passMaterial = runtimeMaterial;
        featurePatched = true;
    }

    /// <summary>卸载期把 Renderer Data 还原：关掉 Feature、还原材质引用并销毁运行时克隆。</summary>
    private void CleanupRendererResources()
    {
        SetFeatureActive(false);

        if (featurePatched && targetFeature != null && targetFeature.passMaterial == runtimeMaterial)
            targetFeature.passMaterial = originalMaterial;
        featurePatched = false;

        if (runtimeMaterial != null)
            Destroy(runtimeMaterial);
        runtimeMaterial = null;

        resolved = false;
        valuesCached = false;
    }

    private ScriptableRendererData[] CollectRendererDataList()
    {
        if (rendererData != null)
            return new[] { rendererData };

        UniversalRenderPipelineAsset pipeline = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
        if (pipeline == null)
            pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;

        return pipeline != null ? GetRendererDataList(pipeline) : null;
    }

    /// <summary>
    /// UniversalRenderPipelineAsset 没有公开的 rendererDataList，只能反射读内部序列化字段
    /// （m_RendererDataList 的实际类型是 List&lt;ScriptableRendererData&gt;，这里做兼容读取）。
    /// </summary>
    private static ScriptableRendererData[] GetRendererDataList(UniversalRenderPipelineAsset urp)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        foreach (string fieldName in new[] { "m_RendererDataList", "rendererDataList" })
        {
            var field = typeof(UniversalRenderPipelineAsset).GetField(fieldName, flags);
            if (field == null)
                continue;

            object value = field.GetValue(urp);

            if (value is List<ScriptableRendererData> list && list.Count > 0)
                return list.ToArray();

            if (value is ScriptableRendererData[] array && array.Length > 0)
                return array;
        }

        return null;
    }

    private void SetFeatureActive(bool active)
    {
        if (targetFeature != null && targetFeature.isActive != active)
            targetFeature.SetActive(active);
    }
}
