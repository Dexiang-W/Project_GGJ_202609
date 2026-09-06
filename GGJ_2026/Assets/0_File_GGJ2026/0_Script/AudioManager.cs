using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 正式关卡（Level1）的统一音效管理：
///  · 背景音乐：MUS_Gameplay_Main 全程循环，音量中等（比环境音响、但不压过一次性音效）；
///  · 场景环境音：Level1 用 AMB_Lab_Main 循环，音量很低（微微可闻）；
///  · 一次性音效（拾取能量 / Q 消耗 / 交互注入 / 脚步、跳跃、落地）走独立通道，响度最大，明显盖过其它声音；
///  · 带变体的素材（脚步/跳跃/落地）每次随机抽一个，且避免连续抽到同一条。
///
/// 使用方式：
///  · 素材由编辑器菜单 GGJ2026/音频/… 自动接到 Resources/GGJ_AudioManager 预制体上
///    （见 GGJAudioSetupBuilder.cs，编译后会自动补建一次，也可手动重建），不用人工拖拽；
///  · 各玩法脚本通过 AudioManager.EnsureCreated() + Instance.PlayXxx() 触发音效。
///
/// 实例为 DontDestroyOnLoad：进入正式玩法（GameplayHUD 创建）后音乐全程不断；
/// 环境音只在该场景名 == ambienceSceneName（默认 Level1）时播放，离开 Level1 自动停。
/// </summary>
[DisallowMultipleComponent]
public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance { get; private set; }

    [Header("背景音乐（全程循环）")]
    [Tooltip("MUS_Gameplay_Main")]
    public AudioClip musicClip;
    [Range(0f, 1f)]
    [Tooltip("背景音量：比环境音大声、但又不能盖过 SFX（默认 0.5 左右）")]
    public float musicVolume = 0.5f;

    [Header("场景环境音（Level1，循环且很轻）")]
    [Tooltip("AMB_Lab_Main")]
    public AudioClip ambienceClip;
    [Range(0f, 1f)]
    [Tooltip("环境音音量：微微能听到即可")]
    public float ambienceVolume = 0.1f;
    [Tooltip("只在“此场景名”里播放环境音（离开自动停）")]
    public string ambienceSceneName = "Level1";

    [Header("一次性玩法音效")]
    [Tooltip("触碰 energyPoint：SFX_Energy_Pickup")]
    public AudioClip pickupClip;
    [Tooltip("按 Q：SFX_Energy_Return")]
    public AudioClip returnClip;
    [Tooltip("交互（按 E）：SFX_Energy_Inject")]
    public AudioClip injectClip;

    [Header("带变体的素材（播放时随机）")]
    [Tooltip("SFX_Footstep_Lab_1~6")]
    public AudioClip[] footstepClips;
    [Tooltip("SFX_Player_Jump_1~5")]
    public AudioClip[] jumpClips;
    [Tooltip("SFX_Player_Land_1~5")]
    public AudioClip[] landClips;

    [Header("响度")]
    [Range(0f, 1f)]
    [Tooltip("一次性音效统一音量（尽量保持 1，保证明显盖过音乐/环境音）")]
    public float sfxVolume = 1f;

    // 三个独立音源：音乐 / 环境音 / 一次性音效
    private AudioSource musicSource;
    private AudioSource ambienceSource;
    private AudioSource sfxSource;

    private bool levelAudioStarted;
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
                                 "请执行菜单 GGJ2026/音频/重建音频管理预制体（自动接入 4_Temp_Xiaoteng_Audio 素材）后重试。");
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

        if (scene.name == ambienceSceneName)
            StartAmbience();
        else
            StopAmbience();
    }

    /// <summary>正式游玩开始时调用（进入 Level1 / GameplayHUD 创建时）：启动 BGM 并刷新环境音。</summary>
    public void BeginLevelAudio()
    {
        CreateAudioSources();

        if (!levelAudioStarted)
        {
            levelAudioStarted = true;

            if (musicClip != null)
            {
                musicSource.clip = musicClip;
                musicSource.loop = true;
                musicSource.volume = musicVolume;
                musicSource.Play();
            }

            // 停掉旧流程（GameFlowController）还挂在标题阶段的音乐源，避免 BGM 双份叠加
            StopLegacyTitleMusic();
        }

        RefreshAmbience();
    }

    // ---------------------------------------------------------------- 一次性音效

    public void PlayPickup()      => PlayOneShot(pickupClip);
    public void PlayEnergyReturn() => PlayOneShot(returnClip);
    public void PlayInject()      => PlayOneShot(injectClip);
    public void PlayFootstep()    => PlayOneShot(PickRandom(footstepClips, ref lastFootstepIndex));
    public void PlayJump()        => PlayOneShot(PickRandom(jumpClips, ref lastJumpIndex));
    public void PlayLand()        => PlayOneShot(PickRandom(landClips, ref lastLandIndex));

    private void PlayOneShot(AudioClip clip)
    {
        if (clip == null)
            return;

        CreateAudioSources();
        sfxSource.PlayOneShot(clip, sfxVolume);
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

    private void RefreshAmbience()
    {
        if (SceneManager.GetActiveScene().name == ambienceSceneName)
            StartAmbience();
        else
            StopAmbience();
    }

    private void StartAmbience()
    {
        if (ambienceClip == null)
            return;

        CreateAudioSources();
        if (ambienceSource.isPlaying)
            return;

        ambienceSource.clip = ambienceClip;
        ambienceSource.loop = true;
        ambienceSource.volume = ambienceVolume;
        ambienceSource.Play();
    }

    private void StopAmbience()
    {
        if (ambienceSource != null && ambienceSource.isPlaying)
            ambienceSource.Stop();
    }

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
