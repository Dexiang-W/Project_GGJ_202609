using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 正式关卡（Level1~Level5）的统一音频管理：
///  · 每个正式关卡可以在“关卡配乐表”（sceneAudioProfiles，每关一行）里配自己的
///    主背景音乐 + 环境音（以及各自音量）。切场景时按场景名自动查表并淡入淡出切换；
///  · 表里没写到的场景会回退到默认字段 musicClip / ambienceClip(+ambienceSceneName)；
///  · 一次性音效（拾取能量 / Q 消耗 / 交互注入 / 弹跳蘑菇 / 脚步、跳跃、落地）走独立通道，响度最大；
///  · 带变体的素材（脚步/跳跃/落地）每次随机抽一个，且避免连续抽到同一条。
///
/// 使用方式：
///  · 素材与关卡配乐表由编辑器菜单 GGJ2026/音频/… 自动接到 Resources/GGJ_AudioManager 预制体
///    （见 GGJAudioSetupBuilder.cs），不用人工拖拽；
///  · 想微调：Project 里打开该预制体 —— 每个关卡的音乐/环境音/音量、淡入淡出时长都在上面改；
///  · 各玩法脚本通过 AudioManager.EnsureCreated() + Instance.PlayXxx() 触发音效。
///
/// 实例为 DontDestroyOnLoad：进入正式玩法（GameplayHUD 创建）后音乐全程不断，
/// 关卡切换（加载新场景）时按新场景的配置行自动淡出旧 BGM/环境音、淡入新的。
/// </summary>
[DisallowMultipleComponent]
public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance { get; private set; }

    /// <summary>脚踩的地表类型，决定脚步声用哪组素材（默认 Floor = 上面的 footstepClips）。</summary>
    public enum StepSurface
    {
        Floor,      // 地板/实验室（footstepClips = SFX_Footstep_Lab_x）
        Grass,      // 草地
        Sand,       // 沙地
        Water,      // 水体 / 浅水洼（素材组为 Puddle）
        Dirt,       // 泥土
        Mushroom    // 蘑菇地形
    }

    // 单关可配置单位：一个正式关卡一行（主音乐 + 环境音）
    [System.Serializable]
    public class SceneAudioProfile
    {
        [Tooltip("场景名，必须与场景文件同名（如 Level2）。新增关卡时加一行即可")]
        public string sceneName;

        [Header("主背景音乐（循环）")]
        [Tooltip("该关卡的主 BGM；留空 = 本关无主音乐")]
        public AudioClip musicClip;
        [Range(0f, 1f)]
        [Tooltip("该关 BGM 音量：收敛做垫底，明显小于 SFX（默认 0.3 左右）")]
        public float musicVolume = 0.3f;

        [Header("场景环境音（循环，叠在 BGM 上面）")]
        [Tooltip("该关卡的环境音（如 AMB_Lab_Main）；留空 = 本关无环境音")]
        public AudioClip ambienceClip;
        [Range(0f, 1f)]
        [Tooltip("该关环境音音量：要能明显听到氛围声（默认 0.2 左右）")]
        public float ambienceVolume = 0.2f;
    }

    [Header("默认主音乐（关卡配乐表里没写到的场景用）")]
    [Tooltip("MUS_Gameplay_Main")]
    public AudioClip musicClip;
    [Range(0f, 1f)]
    [Tooltip("背景音量：收敛做垫底，明显小于 SFX（默认 0.3 左右）")]
    public float musicVolume = 0.3f;

    [Header("默认环境音（关卡配乐表里没写到的场景用）")]
    [Tooltip("AMB_Lab_Main")]
    public AudioClip ambienceClip;
    [Range(0f, 1f)]
    [Tooltip("环境音音量：要能明显听到氛围声（默认 0.2 左右）")]
    public float ambienceVolume = 0.2f;
    [Tooltip("只在“此场景名”里播放默认环境音（离开自动停）")]
    public string ambienceSceneName = "Level1";

    [Header("关卡配乐表（推荐在这统一按关卡配置）")]
    [Tooltip("每个正式关卡一行：主 BGM + 环境音 + 各自音量。\n" +
             "进入/切换场景时自动按场景名匹配这一行，做淡入淡出切换；\n" +
             "表里没匹配到的场景用上面的“默认”字段。")]
    public SceneAudioProfile[] sceneAudioProfiles;

    [Header("关卡切换淡入淡出（秒）")]
    [Tooltip("换关卡时主 BGM 淡出 + 淡入的总时长")]
    public float musicFadeSeconds = 1.5f;
    [Tooltip("换关卡时环境音淡出 + 淡入的总时长")]
    public float ambienceFadeSeconds = 1f;

    [Header("一次性玩法音效")]
    [Tooltip("触碰 energyPoint：SFX_Energy_Pickup")]
    public AudioClip pickupClip;
    [Tooltip("按 Q：SFX_Energy_Return")]
    public AudioClip returnClip;
    [Tooltip("交互（按 E）：SFX_Energy_Inject")]
    public AudioClip injectClip;
    [Tooltip("弹跳蘑菇（BouncePad 踩上去弹起）：SFX_Mushroom_Bounce")]
    public AudioClip bounceClip;

    [Header("带变体的素材（播放时随机）")]
    [Tooltip("地板/实验室脚步（默认地表，SFX_Footstep_Lab_1~6）。没识别出地表或地表组没配时就播这组")]
    public AudioClip[] footstepClips;
    [Tooltip("SFX_Player_Jump_1~5（跳跃响度由下方 jumpBoost 单独调）")]
    public AudioClip[] jumpClips;
    [Tooltip("SFX_Player_Land_1~5（落地响度由下方 landBoost 单独调）")]
    public AudioClip[] landClips;

    [Header("地表脚步组（不同地表自动换一组；某组没配素材会自动回退到地板组）")]
    [Tooltip("草地：SFX_Footstep_Grass_1~6")]
    public AudioClip[] grassStepClips;
    [Tooltip("沙地：SFX_Footstep_Sand_1~6")]
    public AudioClip[] sandStepClips;
    [Tooltip("水体/浅水洼：SFX_Footstep_Puddle_1~6")]
    public AudioClip[] waterStepClips;
    [Tooltip("泥土：SFX_Footstep_Dirt_1~6")]
    public AudioClip[] dirtStepClips;
    [Tooltip("蘑菇地形：SFX_Footstep_Mushroom_1~6")]
    public AudioClip[] mushroomStepClips;

    [Header("响度")]
    [Range(0f, 1f)]
    [Tooltip("一次性音效统一基础音量（尽量保持 1；脚步/跳跃/落地再乘下面各自的系数）")]
    public float sfxVolume = 1f;
    [Range(0.1f, 3f)]
    [Tooltip("脚步声最终音量系数（在 sfxVolume 基础上乘）：1 = 与其它 SFX 同响。觉得脚步不够响就往上调")]
    public float footstepBoost = 2f;
    [Range(0.05f, 2f)]
    [Tooltip("跳跃音效音量系数（在 sfxVolume 基础上乘）。觉得跳跃太吵就往低调")]
    public float jumpBoost = 0.6f;
    [Range(0.05f, 2f)]
    [Tooltip("落地音效音量系数（在 sfxVolume 基础上乘）")]
    public float landBoost = 0.9f;

    // 三个独立音源：音乐 / 环境音 / 一次性音效
    private AudioSource musicSource;
    private AudioSource ambienceSource;
    private AudioSource sfxSource;

    private bool levelAudioStarted;
    private Coroutine musicFadeRoutine;
    private Coroutine ambienceFadeRoutine;
    private int lastFootstepIndex = -1;
    private int lastJumpIndex = -1;
    private int lastLandIndex = -1;
    private static bool warnedPrefabMissing;

    // ---------------------------------------------------------------- 静态入口

    /// <summary>确保音频管理存在（从 Resources/GGJ_AudioManager 预制体实例化并常驻）。</summary>
    public static void EnsureCreated()
    {
        if (Instance != null)
            return;

        GameObject prefab = Resources.Load<GameObject>("GGJ_AudioManager");
        if (prefab != null)
        {
            Instantiate(prefab);
        }
        else
        {
            if (!warnedPrefabMissing)
            {
                warnedPrefabMissing = true;
                Debug.LogWarning("[AudioManager] 未找到 Resources/GGJ_AudioManager 预制体，音效素材尚未接入。\n" +
                                 "请执行菜单 GGJ2026/音频/重建音频管理预制体（自动接入 7_Audio 素材）后重试。");
            }

            GameObject go = new GameObject("GGJ_AudioManager");
            go.AddComponent<AudioManager>();
        }

        // 兜底：万一预制体上没有挂 AudioManager，这里再补一个
        if (Instance == null)
        {
            GameObject go = new GameObject("GGJ_AudioManager");
            go.AddComponent<AudioManager>();
        }
    }

    // ---------------------------------------------------------------- 生命周期

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded += OnSceneLoaded;
        CreateAudioSources();
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!levelAudioStarted)
            return;

        // 切到新场景 → 按关卡配乐表自动换 BGM / 环境音（带淡入淡出）
        ApplySceneProfile(scene.name);
    }

    /// <summary>正式游玩开始时调用（进入正式关卡 / GameplayHUD 创建时）：按当前场景应用配乐。</summary>
    public void BeginLevelAudio()
    {
        CreateAudioSources();

        if (!levelAudioStarted)
        {
            levelAudioStarted = true;

            // 停掉旧流程（GameFlowController）还挂在标题阶段的音乐源，避免 BGM 双份叠加
            StopLegacyTitleMusic();
        }

        // 若从标题进入，此刻活动场景还是 Begin_Menu，会走默认字段；
        // 真正加载 LevelX 后会再触发 OnSceneLoaded 命中配乐表那一行。
        ApplySceneProfile(SceneManager.GetActiveScene().name);
    }

    // ---------------------------------------------------------------- 关卡配乐表

    /// <summary>按场景名应用配乐：优先查表，查不到回退默认字段。</summary>
    private void ApplySceneProfile(string sceneName)
    {
        SceneAudioProfile profile = FindProfile(sceneName);

        if (profile != null)
        {
            SetMusic(profile.musicClip, profile.musicVolume);
            SetAmbience(profile.ambienceClip, profile.ambienceVolume);
        }
        else
        {
            SetMusic(musicClip, musicVolume);
            bool allowDefaultAmbience = sceneName == ambienceSceneName;
            SetAmbience(allowDefaultAmbience ? ambienceClip : null, ambienceVolume);
        }
    }

    private SceneAudioProfile FindProfile(string sceneName)
    {
        if (sceneAudioProfiles == null)
            return null;

        foreach (SceneAudioProfile p in sceneAudioProfiles)
        {
            if (p != null && !string.IsNullOrEmpty(p.sceneName) && p.sceneName == sceneName)
                return p;
        }
        return null;
    }

    /// <summary>
    /// 平滑切到某条主音乐：旧音乐先淡出，再淡入新音乐。
    /// 目标是当前正播的同一条时只校正音量，不打断重播。
    /// </summary>
    private void SetMusic(AudioClip clip, float targetVolume)
    {
        CreateAudioSources();

        if (musicFadeRoutine != null)
            StopCoroutine(musicFadeRoutine);

        musicFadeRoutine = StartCoroutine(SwitchSourceRoutine(musicSource, clip, targetVolume, musicFadeSeconds, true, () => musicFadeRoutine = null));
    }

    /// <summary>平滑切到某条环境音；clip 为 null 表示该关不需要环境音（淡出并停掉）。</summary>
    private void SetAmbience(AudioClip clip, float targetVolume)
    {
        CreateAudioSources();

        if (ambienceFadeRoutine != null)
            StopCoroutine(ambienceFadeRoutine);

        ambienceFadeRoutine = StartCoroutine(SwitchSourceRoutine(ambienceSource, clip, targetVolume, ambienceFadeSeconds, true, () => ambienceFadeRoutine = null));
    }

    private System.Collections.IEnumerator SwitchSourceRoutine(
        AudioSource source, AudioClip clip, float targetVolume, float totalSeconds,
        bool loop, System.Action onDone)
    {
        float half = Mathf.Max(0.001f, totalSeconds * 0.5f);

        // 1) 旧素材淡出（目标换成别条时才需要；同一条则直接续播）
        if (source.clip != null && source.isPlaying && source.clip != clip)
        {
            yield return FadeToVolume(source, 0f, half);
            source.Stop();
            source.clip = null;
        }

        // 2) 换上新素材并淡入
        if (clip != null)
        {
            if (source.clip != clip)
            {
                source.clip = clip;
                source.loop = loop;
            }

            if (!source.isPlaying)
            {
                source.volume = 0f;
                source.Play();
            }

            yield return FadeToVolume(source, targetVolume, half);
        }

        onDone?.Invoke();
    }

    private System.Collections.IEnumerator FadeToVolume(AudioSource source, float targetVolume, float seconds)
    {
        float from = source.volume;
        float elapsed = 0f;
        while (elapsed < seconds)
        {
            elapsed += Time.deltaTime;
            source.volume = Mathf.Lerp(from, targetVolume, seconds > 0f ? elapsed / seconds : 1f);
            yield return null;
        }
        source.volume = targetVolume;
    }

    // ---------------------------------------------------------------- 一次性音效

    public void PlayPickup()      => PlayOneShot(pickupClip);
    public void PlayEnergyReturn() => PlayOneShot(returnClip);
    public void PlayInject()      => PlayOneShot(injectClip);
    public void PlayBounce()      => PlayOneShot(bounceClip);
    public void PlayFootstep()    => PlayFootstep(StepSurface.Floor);

    /// <summary>播一步脚步：按当前踩的地表类型选素材组并随机一条，音量 = sfxVolume * footstepBoost。</summary>
    public void PlayFootstep(StepSurface surface)
    {
        AudioClip[] pool = GetStepSurfaceClips(surface);
        PlayOneShot(PickRandom(pool, ref lastFootstepIndex), sfxVolume * footstepBoost);
    }

    /// <summary>跳跃音效：音量 = sfxVolume * jumpBoost（独立可调，避免“跳比走路还吵”）。</summary>
    public void PlayJump() => PlayOneShot(PickRandom(jumpClips, ref lastJumpIndex), sfxVolume * jumpBoost);

    /// <summary>落地音效：音量 = sfxVolume * landBoost。</summary>
    public void PlayLand() => PlayOneShot(PickRandom(landClips, ref lastLandIndex), sfxVolume * landBoost);

    /// <summary>取该地表对应的脚步素材组；没配素材时回退到默认地板组，避免无声。</summary>
    private AudioClip[] GetStepSurfaceClips(StepSurface surface)
    {
        switch (surface)
        {
            case StepSurface.Grass:    return HasAny(grassStepClips) ? grassStepClips : footstepClips;
            case StepSurface.Sand:     return HasAny(sandStepClips) ? sandStepClips : footstepClips;
            case StepSurface.Water:    return HasAny(waterStepClips) ? waterStepClips : footstepClips;
            case StepSurface.Dirt:     return HasAny(dirtStepClips) ? dirtStepClips : footstepClips;
            case StepSurface.Mushroom: return HasAny(mushroomStepClips) ? mushroomStepClips : footstepClips;
            default:                   return footstepClips;
        }
    }

    private static bool HasAny(AudioClip[] clips) => clips != null && clips.Length > 0;

    private void PlayOneShot(AudioClip clip, float volumeScale = -1f)
    {
        if (clip == null)
            return;

        CreateAudioSources();
        // volumeScale < 0 表示走默认 sfxVolume
        sfxSource.PlayOneShot(clip, volumeScale >= 0f ? volumeScale : sfxVolume);
    }

    /// <summary>从数组里随机抽一个素材，尽量不与上一次重复。</summary>
    private static AudioClip PickRandom(AudioClip[] clips, ref int lastIndex)
    {
        if (clips == null || clips.Length == 0)
            return null;
        if (clips.Length == 1)
            return clips[0];

        int index = Random.Range(0, clips.Length);
        if (index == lastIndex)
            index = (index + 1) % clips.Length;
        lastIndex = index;
        return clips[index];
    }

    // ---------------------------------------------------------------- 内部

    private void CreateAudioSources()
    {
        if (sfxSource != null)
            return;

        // priority：数值越小越优先，保证一次性音效在声源紧张时也不会被音乐/环境音挤掉
        musicSource    = CreateSource("Music_Source", 128, musicVolume);
        ambienceSource = CreateSource("Ambience_Source", 160, ambienceVolume);
        sfxSource      = CreateSource("Sfx_Source", 0, sfxVolume);
    }

    private AudioSource CreateSource(string childName, int priority, float volume)
    {
        GameObject go = new GameObject(childName);
        go.transform.SetParent(transform, false);

        AudioSource src = go.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.loop = false;
        src.spatialBlend = 0f;      // 全 2D 直出，保证响度稳定
        src.priority = priority;
        src.volume = volume;
        src.dopplerLevel = 0f;
        return src;
    }

    private static void StopLegacyTitleMusic()
    {
        GameFlowController flow = FindObjectOfType<GameFlowController>();
        if (flow != null)
            flow.StopBackgroundAudio();
    }
}
