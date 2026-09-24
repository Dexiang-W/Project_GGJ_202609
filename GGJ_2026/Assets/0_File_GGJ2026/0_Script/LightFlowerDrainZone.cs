using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using StarterAssets;

/// <summary>
/// “发光花吸干养分”危险区（P_LightFlower 专用）。
///
/// 玩法：
///   · 玩家进入以发光花为中心的【危险半径】→ 开始倒计时（默认 5 秒），
///     屏幕中部偏左的文字每秒刷新一次剩余秒数（“还有 5 秒……身上的养分就会被它吸干”）；
///   · 倒计时归零时玩家还在区域里 → 直接死亡：黑幕淡入 → 显示死亡文字 → 传送回重生点 → 黑幕淡出；
///   · 中途走出危险半径 → 立刻停表、清文字、暗角复原，下一次进来重新从 5 秒开始。
///
/// 画面 / 声音表现（三者共用同一条“紧迫感曲线”，见 Urgency Exponent）：
///   · 暗角：闪得越来越快、越来越黑、波形越来越尖（像在抽搐）；
///   · 嘀声：间隔从 1 秒缩到 Beep Final Interval，音高从 1 升到 Beep Final Pitch，音量也往上顶；
///   · 氛围嗡鸣：音量随倒计时涨满，同时音高上顶、脉冲（心跳）从 1 次/秒加快到 7 次/秒；
///   离开区域立刻把暗角写回初始值、把 Feature 还原成原来的开关状态、停掉嗡鸣。
///
/// 实现要点（与 FullScreenPassFader 保持一致，避免卡顿与资源污染）：
///   · 运行时【克隆】一份材质再改，绝不直接写工程里的 .mat 资产；
///   · Feature 只在“进入区域 / 离开区域 / 销毁”这几个时刻开关，闪烁期间只写材质 float；
///   · 死亡流程走全局复活总闸 RespawnFlow，与 RespawnZone / PipelineHazard 互斥，
///     不会两套复活流程同时跑（表现为“躺着复活 / 黑幕卡住 / 走不动”）。
///
/// 挂法：
///   挂到 P_LightFlower 上（或场景里任意空物体上，再把发光花拖到 Danger Center）。
///   Inspector 里填：危险半径、倒计时秒数、倒计时 / 死亡文字、重生点、M_PP_Vignette 即可。
/// </summary>
[DisallowMultipleComponent]
public class LightFlowerDrainZone : MonoBehaviour
{
    /// <summary>
    /// 结局演出期间把它打开：花周围的暗角后处理【完全不再出现】。
    /// 由 EndingFinaleDirector / GiveUpEndingDirector 在开演时置 true、收工时还原。
    /// </summary>
    public static bool SuppressVignette { get; set; }

    [Header("危险区域")]
    [Tooltip("危险区中心（发光花）。留空 = 用挂本脚本的物体自身；再找不到就按名字 P_LightFlower 在场景里找一次。")]
    [SerializeField] private Transform dangerCenter;

    [Tooltip("找不到中心时，按这个名字在场景里查找（默认 P_LightFlower）")]
    [SerializeField] private string dangerCenterName = "P_LightFlower";

    [Tooltip("危险半径（米）：玩家进入这个距离就开始倒计时")]
    [SerializeField] private float dangerRadius = 8f;

    [Tooltip("勾选：只算水平距离（忽略高低差）。横版场景里花可能在高处/低处，一般保持勾选。")]
    [SerializeField] private bool useHorizontalDistance = true;

    [Tooltip("匹配角色的标签（默认 Player，与角色一致）")]
    [SerializeField] private string playerTag = "Player";

    [Header("倒计时")]
    [Tooltip("进入危险区后还能撑多少秒（归零即死）")]
    [SerializeField] private float countdownSeconds = 5f;

    [Tooltip("倒计时期间显示的文字，{0} 会被替换成剩余整秒数。支持 \\n 换行。")]
    [SerializeField, TextArea(1, 3)]
    private string countdownText = "还有 {0} 秒……身上的养分就会被它吸干。\nAll your nutrients will be drained in {0}s.";

    [Header("死亡演出")]
    [Tooltip("死亡后被传送到的重生点（空物体即可，与 RespawnZone 用的 RespawnPoint 一样）")]
    [SerializeField] private Transform respawnPoint;

    [Tooltip("黑幕全黑时显示的一段文字。支持 \\n 换行。")]
    [SerializeField, TextArea(1, 3)]
    private string deathText = "养分被抽干的一瞬间，你看见根系深处有光在跳动。\nAs it drained you, something deep in the roots pulsed with light.";

    [Tooltip("淡入黑幕时长")]
    [SerializeField] private float fadeToBlackSeconds = 0.5f;
    [Tooltip("全黑停留时长（文字在此期间显示，玩家被传送）")]
    [SerializeField] private float blackHoldSeconds = 1.2f;
    [Tooltip("黑幕淡出时长")]
    [SerializeField] private float fadeFromBlackSeconds = 0.8f;
    [Tooltip("淡出后死亡文字额外停留的时长")]
    [SerializeField] private float messageStaySeconds = 1.2f;

    [Tooltip("传送到重生点后是否把角色朝向重置为重生点的朝向（不勾则保持角色原朝向）")]
    [SerializeField] private bool useRespawnFacing = true;

    [Tooltip("勾选：死亡时把身上能量一并清零（被吸干了，理所当然）")]
    [SerializeField] private bool drainEnergyOnDeath = true;

    [Header("紧迫感曲线（嘀声 / 氛围音 / 闪烁共用）")]
    [Tooltip("所有“越来越急”的变化都走这一条曲线：1 = 匀速变快；越大 = 前面慢、后面突然爆发。2~3 最像心脏狂跳")]
    [SerializeField] private float urgencyExponent = 2.2f;

    [Header("倒计时嘀嘀声")]
    [Tooltip("播放嘀声的音频源；留空会用本物体上的 AudioSource，没有就自动创建一个 2D 声源（不随距离衰减）")]
    [SerializeField] private AudioSource beepAudioSource;

    [Tooltip("嘀声片段。留空就用脚本合成的短促正弦“嘀”（880Hz，自带衰减包络，不会爆音）")]
    [SerializeField] private AudioClip beepClip;

    [Tooltip("嘀声音量")]
    [SerializeField, Range(0f, 1f)] private float beepVolume = 0.35f;

    [Tooltip("第一声的间隔（秒），也是倒计时的“基准节拍”")]
    [SerializeField] private float beepInterval = 1f;

    [Tooltip("归零瞬间的间隔（秒）。越小，最后越像连成一串的心跳，建议 0.12~0.2")]
    [SerializeField] private float beepFinalInterval = 0.16f;

    [Tooltip("勾选：嘀声全程连续加速（配合上面的紧迫感曲线）。取消 = 始终按固定间隔响")]
    [SerializeField] private bool accelerateWithCountdown = true;

    [Tooltip("第一声的音高")]
    [SerializeField] private float beepBasePitch = 1f;

    [Tooltip("最后一声的音高（越接近归零越尖，紧张感来自这里）")]
    [SerializeField] private float beepFinalPitch = 2f;

    [Tooltip("归零时嘀声的音量倍率（1 = 不变，1.3 = 最后一串明显更响），最终不会超过 1")]
    [SerializeField] private float beepFinalVolumeBoost = 1.3f;

    [Tooltip("勾选：被吸干那一刻补一记下扫的闷响（420Hz → 60Hz，脚本合成）")]
    [SerializeField] private bool playDrainSoundOnDeath = true;

    [Tooltip("闷响片段；留空用脚本合成的")]
    [SerializeField] private AudioClip drainClip;

    [Header("危险区氛围音（嗡鸣 / 音乐）")]
    [Tooltip("进入危险区后循环播放的低频嗡鸣，例如 AMB_Core_Hum.wav。留空且勾选了自动生成 = 用脚本合成的 4 秒无缝低频嗡鸣（41/55/82.5/110Hz 叠层 + 0.5Hz 呼吸起伏）")]
    [SerializeField] private AudioClip humClip;

    [Tooltip("勾选：没有拖入 Hum Clip 时，用脚本合成一段低频嗡鸣顶上，保证一定有“音乐”在走")]
    [SerializeField] private bool synthesizeAmbienceIfMissing = true;

    [Tooltip("嗡鸣音量（会被下面的脉冲包络再压一次）")]
    [SerializeField, Range(0f, 1f)] private float humVolume = 0.25f;

    [Tooltip("勾选：嗡鸣音量随倒计时从 0 涨到满（越危险越吵）；取消则一进区域就是满音量")]
    [SerializeField] private bool humFadesInWithCountdown = true;

    [Tooltip("勾选：嗡鸣音高上顶 + 脉冲加快（越接近归零越喘、越急）")]
    [SerializeField] private bool humTightensWithCountdown = true;

    [Tooltip("进入时的音高")]
    [SerializeField] private float humStartPitch = 1f;

    [Tooltip("归零时的音高（1.2~1.5 之间最自然，再高会发尖）")]
    [SerializeField] private float humEndPitch = 1.35f;

    [Tooltip("脉冲（一鼓一鼓的“心跳”）的起始频率，次/秒")]
    [SerializeField] private float humPulseStartRate = 1f;

    [Tooltip("归零时的脉冲频率，次/秒（6~8 已经很像狂跳）")]
    [SerializeField] private float humPulseEndRate = 7f;

    [Tooltip("脉冲的深度：0 = 平的，0.5 = 一半音量在起伏")]
    [SerializeField, Range(0f, 0.9f)] private float humPulseDepth = 0.45f;

    [Header("暗角闪烁（M_PP_Vignette）")]
    [Tooltip("挂载 Full Screen Pass 的 Renderer Data；留空自动使用当前 URP 管线的 Renderer Data")]
    [SerializeField] private ScriptableRendererData rendererData;

    [Tooltip("用来匹配 Feature 的暗角材质（把 M_PP_Vignette 拖进来）；留空则按材质名含 Vignette 自动匹配")]
    [SerializeField] private Material vignetteMaterial;

    [Tooltip("材质里控制暗角强度的 float 属性名（M_PP_Vignette 里是 _Intensity）")]
    [SerializeField] private string intensityProperty = "_Intensity";

    [Tooltip("已废弃的开关（保留只为兼容旧场景数据）。现在 Renderer Feature 【默认完全关闭】，" +
             "只有玩家进入危险半径时才打开，离开立刻关闭，任何情况下都不会常驻。\n" +
             "打包版能正常显隐靠的是 Graphics Settings → Always Included Shaders 里已加入 Sh_PP_Vignette。")]
    [SerializeField] private bool keepVignetteFeatureActive = false;

    [Tooltip("闪烁的最小值")]
    [SerializeField] private float minIntensity = 0f;

    [Tooltip("闪烁的最大值")]
    [SerializeField] private float maxIntensity = 0.5f;

    [Tooltip("闪烁频率（次/秒），这是刚进区域时的速度")]
    [SerializeField] private float blinkFrequency = 3f;

    [Tooltip("勾选：越接近归零闪得越快，紧张感更强")]
    [SerializeField] private bool blinkFasterAsTimeRunsOut = true;

    [Tooltip("归零时闪到多少倍频（3 = 快 3 倍，6 = 几乎在抽搐）")]
    [SerializeField] private float blinkFinalFrequencyMultiplier = 5f;

    [Tooltip("勾选：暗角整体越来越黑（不只是闪得快，亮的那一瞬也回不到原来的亮度了）")]
    [SerializeField] private bool blinkDarkensOverTime = true;

    [Tooltip("归零时暗角下限抬到最大值几成（0.7 = 最亮也只剩三成亮度）")]
    [SerializeField, Range(0f, 1f)] private float blinkFinalDarkness = 0.7f;

    [Tooltip("波形的锐利度：1 = 平滑正弦；2~3 = 暗得快、亮得突然，像在抽搐。随倒计时从 1 长到这个值")]
    [SerializeField] private float blinkFinalSharpness = 2.5f;

    // ---------------------------------------------------------------- 运行时

    private ThirdPersonController player;
    private Transform center;

    private bool inside;
    private bool dying;
    private float remaining;
    private int lastShownSecond = -1;
    private bool messageShownByThis;

    private FullScreenPassRendererFeature targetFeature;
    private Material runtimeMaterial;
    private Material originalMaterial;
    private float originalIntensity;
    private bool featureWasActive;
    private bool vignetteResolved;

    private AudioSource beepSource;
    private AudioClip generatedBeepClip;
    private AudioClip generatedDrainClip;
    private float beepTimer;

    private AudioSource humSource;
    private AudioClip generatedHumClip;
    private float humPulsePhase;

    private float blinkPhase;

    // ---------------------------------------------------------------- 生命周期

    private void Awake()
    {
        ResolveCenter();
    }

    private void Start()
    {
        ResolvePlayer();

        // 一进场景就把暗角 Feature 解析好并置为“可见但强度 0”：
        //   ① 打包版里“走进危险区才临时开 Feature”有可能不生效（编辑器可见、打包后消失），提前开最稳；
        //   ② 顺带把 shader / 渲染缓冲在加载期准备好，真正开始闪烁时不会顿一下。
        PrewarmVignette();
    }

    private void OnEnable()
    {
        // 关卡重置 / 重新激活后重新计时（按 R 回溯、重开关卡都走这里）
        inside = false;
        dying = false;
        remaining = countdownSeconds;
        lastShownSecond = -1;
        messageShownByThis = false;
        beepTimer = 0f;
        blinkPhase = 0f;
        humPulsePhase = 0f;

        // 防卡死：重新激活时把暗角强度压回 0
        // （否则上一次死在花区 / 中途被禁用时的红色会一直留在画面上）
        if (runtimeMaterial != null && runtimeMaterial.HasProperty(intensityProperty))
            runtimeMaterial.SetFloat(intensityProperty, 0f);
    }

    private void OnDisable()
    {
        CleanupVignette();
        CleanupBeepAudio();
    }

    private void OnDestroy()
    {
        // 卸载期把 Renderer Data 还原：还原材质引用、销毁运行时克隆、把 Feature 恢复成原开关状态
        CleanupVignette();
        CleanupBeepAudio();
    }

    private void Update()
    {
        // 结局 / 放弃演出的全程：哪怕玩家正好站在花的范围里，也绝不再点亮暗角。
        // （两个结局导演各自会把 SuppressVignette 打开；这里再兜一层 IsRunning，防止有地方漏设）
        if (SuppressVignette || EndingFinaleDirector.IsRunning || GiveUpEndingDirector.IsRunning)
        {
            EnsureVignetteClosed();
            return;
        }

        if (player == null)
            ResolvePlayer();

        if (player == null || center == null)
        {
            // 连玩家 / 花的中心都还没取到，暗角必须保持关闭
            EnsureVignetteClosed();
            return;
        }

        // 死亡演出进行中不再重复判定（流程结束后会重新按距离判定）
        if (dying)
            return;

        bool nowInside = DistanceTo(player.transform.position) <= dangerRadius;

        if (nowInside && !inside)
            EnterDanger();
        else if (!nowInside && inside)
            ExitDanger();

        inside = nowInside;
        if (!inside)
        {
            // 保险（每帧）：危险区外强度恒为 0，且整个 Pass 处于关闭状态
            EnsureVignetteClosed();

            return;
        }

        // 暂停菜单期间不扣时间（与提示文字 / HUD 时间基准一致）
        if (PauseMenuManager.IsPaused)
            return;

        remaining -= Time.deltaTime;

        // progress：0（刚进）→ 1（归零），线性
        float progress = countdownSeconds > 0.001f
            ? 1f - Mathf.Clamp01(remaining / countdownSeconds)
            : 1f;

        // urgency：把 progress 过一遍指数曲线 → 前面慢慢加压，最后突然爆发。
        // 嘀声、氛围音、闪烁全都读这一个值，所以三者的“急”永远是同步的
        float urgency = Mathf.Pow(Mathf.Clamp01(progress), Mathf.Max(0.01f, urgencyExponent));

        RefreshCountdownText();
        UpdateVignetteBlink(urgency);
        UpdateBeep(urgency);
        UpdateHum(progress, urgency);

        if (remaining <= 0f)
            StartCoroutine(DeathRoutine());
    }

    // ---------------------------------------------------------------- 危险区进出

    private void EnterDanger()
    {
        remaining = countdownSeconds;
        lastShownSecond = -1;

        EnsureVignetteReady();
        SetFeatureActive(true);

        // 进入瞬间先“嘀”一声，告诉玩家开始计时了；之后按间隔继续
        EnsureBeepAudioReady();
        EnsureHumReady();
        beepTimer = Mathf.Max(beepInterval, 0.0001f);
        blinkPhase = 0f;
        humPulsePhase = 0f;

        RefreshCountdownText();
    }

    private void ExitDanger()
    {
        remaining = countdownSeconds;
        lastShownSecond = -1;

        HideCountdownText();
        RestoreVignette();
        StopHum();
        beepTimer = 0f;
    }

    /// <summary>整秒变化时刷新一次文字（{0} = 剩余整秒数），避免每帧重播淡入动画导致闪烁。</summary>
    private void RefreshCountdownText()
    {
        GameplayHUD.EnsureCreated();
        GameplayHUD hud = GameplayHUD.Instance;
        if (hud == null)
            return;

        int seconds = Mathf.Max(0, Mathf.CeilToInt(remaining));
        if (seconds == lastShownSecond)
            return;

        lastShownSecond = seconds;
        messageShownByThis = true;

        string text = countdownText.Replace("{0}", seconds.ToString());

        // 第一秒走 ShowMessage（正常淡入一次），之后只换字不重播淡入
        if (hud.IsMessageVisible)
            hud.SetMessageText(text);
        else
            hud.ShowMessage(text);
    }

    private void HideCountdownText()
    {
        if (!messageShownByThis)
            return;

        messageShownByThis = false;

        GameplayHUD hud = GameplayHUD.Instance;
        if (hud != null)
            hud.HideMessage();
    }

    // ---------------------------------------------------------------- 死亡流程

    private IEnumerator DeathRoutine()
    {
        dying = true;

        // 抢不到复活总闸（正在别的复活流程 / 暂停 / 转场中）就什么都不做，下一帧继续判定
        if (!RespawnFlow.TryBegin())
        {
            dying = false;
            remaining = countdownSeconds;
            yield break;
        }

        HideCountdownText();
        StopHum();   // 人已经被吸干了，嗡鸣和嘀声一起收掉

        bool hadInput = true;
        try
        {
            hadInput = SetPlayerInput(player, false);

            GameplayHUD.EnsureCreated();
            GameplayHUD hud = GameplayHUD.Instance;
            if (hud == null)
                yield break;

            // 1. 画面逐渐变黑
            yield return hud.FadeToBlackRoutine(fadeToBlackSeconds);

            // 2. 全黑：补一记“被吸干”的闷响，再显示死亡文字，养分（能量）被吸干
            if (playDrainSoundOnDeath)
                PlayDrainSound();

            hud.ShowMessage(deathText);
            messageShownByThis = false; // 这条文字由流程自己收尾，不再走倒计时的隐藏逻辑

            if (drainEnergyOnDeath)
                hud.SetEnergy(0);

            yield return new WaitForSecondsRealtime(Mathf.Max(0.05f, blackHoldSeconds));

            // 3. 传送回重生点
            if (respawnPoint != null)
            {
                // 传送期间屏蔽相机触发区：防止落点正好在某拉远/固定机位里，重生瞬间镜头被额外切换
                CameraTriggerVolume.SuppressCameraSwitching = true;

                bool alignToPoint = useRespawnFacing && RespawnFlow.HasCustomFacing(respawnPoint);
                float yaw = alignToPoint
                    ? RespawnFlow.HorizontalYawOf(respawnPoint, player.transform.eulerAngles.y)
                    : player.transform.eulerAngles.y;

                player.RespawnAt(respawnPoint.position, yaw);

                yield return new WaitForFixedUpdate();
                yield return null;

                CameraFollowController cam = CameraFollowController.Instance;
                if (cam != null)
                {
                    cam.SetNormalMode();
                    cam.SnapToCurrentTarget();
                }

                CameraTriggerVolume.SuppressCameraSwitching = false;
                CameraTriggerVolume.ReevaluateAll();
            }

            // 4. 黑幕淡出
            yield return hud.FadeFromBlackRoutine(fadeFromBlackSeconds);

            if (messageStaySeconds > 0f)
                yield return new WaitForSecondsRealtime(messageStaySeconds);

            hud.HideMessage();
        }
        finally
        {
            // 无论流程怎么结束都要收尾，否则玩家会卡在“走不动 / 再也复活不了”
            SetPlayerInput(player, hadInput);

            CameraTriggerVolume.SuppressCameraSwitching = false;
            RespawnFlow.End();

            // 暗角复原：死亡瞬间也算离开了危险区
            RestoreVignette();
            StopHum();

            inside = false;
            dying = false;
            remaining = countdownSeconds;
            lastShownSecond = -1;
        }
    }

    /// <summary>停用 / 恢复玩家的 PlayerInput（与 RespawnZone 里同一套做法）。</summary>
    private static bool SetPlayerInput(ThirdPersonController player, bool enabled)
    {
        if (player == null)
            return true;

        bool previous = true;
#if ENABLE_INPUT_SYSTEM
        var playerInput = player.GetComponent<PlayerInput>();
        if (playerInput != null)
        {
            previous = playerInput.enabled;
            if (enabled && !previous)
            {
                // 恢复前把当前输入清零，避免角色带着旧输入“滑”走
                var inputs = player.GetComponent<StarterAssetsInputs>();
                if (inputs != null)
                    inputs.MoveInput(Vector2.zero);
            }
            playerInput.enabled = enabled;
        }
#endif
        return previous;
    }

    // ---------------------------------------------------------------- 目标解析

    private void ResolveCenter()
    {
        center = dangerCenter != null ? dangerCenter : transform;

        if (dangerCenter == null && !string.IsNullOrEmpty(dangerCenterName))
        {
            GameObject found = GameObject.Find(dangerCenterName);
            if (found != null)
                center = found.transform;
        }

        if (center == null)
            Debug.LogWarning($"[LightFlowerDrainZone] {name} 没有危险区中心（Danger Center），倒计时不会开始。", this);
    }

    private void ResolvePlayer()
    {
        player = ThirdPersonController.Instance;
        if (player == null)
            player = FindObjectOfType<ThirdPersonController>();

        if (player == null && !string.IsNullOrEmpty(playerTag))
        {
            GameObject tagged = GameObject.FindWithTag(playerTag);
            if (tagged != null)
                player = tagged.GetComponent<ThirdPersonController>();
        }
    }

    private float DistanceTo(Vector3 point)
    {
        if (useHorizontalDistance)
        {
            Vector3 a = center.position;
            Vector3 b = point;
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        return Vector3.Distance(center.position, point);
    }

    // ---------------------------------------------------------------- 暗角（Vignette）

    /// <summary>闪烁：只写运行时克隆材质的 float，不碰工程里的 .mat 资产。</summary>
    private void UpdateVignetteBlink(float urgency)
    {
        if (runtimeMaterial == null || !runtimeMaterial.HasProperty(intensityProperty))
            return;

        float frequency = blinkFasterAsTimeRunsOut
            ? Mathf.Lerp(blinkFrequency, blinkFrequency * Mathf.Max(1f, blinkFinalFrequencyMultiplier), urgency)
            : blinkFrequency;

        // 累加相位而不是 Time.unscaledTime * frequency：
        // 频率一直在变，直接乘时间会让波形在频率跳变时“抖”一下，累加就没这个问题
        blinkPhase += Time.unscaledDeltaTime * frequency * Mathf.PI * 2f;
        if (blinkPhase > Mathf.PI * 2f)
            blinkPhase -= Mathf.PI * 2f;

        // sin 波映射到 0..1
        float k = 0.5f * (1f + Mathf.Sin(blinkPhase));

        // 越来越“抽搐”：波形变尖（暗得快、亮得突然）
        float sharpness = Mathf.Lerp(1f, Mathf.Max(1f, blinkFinalSharpness), urgency);
        if (sharpness > 1.001f)
            k = Mathf.Pow(k, sharpness);

        // 越来越黑：连最亮的一瞬都回不到原来的亮度，下限随紧张度往上抬
        float low = blinkDarkensOverTime
            ? Mathf.Lerp(minIntensity, Mathf.Lerp(minIntensity, maxIntensity, blinkFinalDarkness), urgency)
            : minIntensity;

        runtimeMaterial.SetFloat(intensityProperty, Mathf.Lerp(low, maxIntensity, k));
    }

    private void RestoreVignette()
    {
        if (runtimeMaterial != null && runtimeMaterial.HasProperty(intensityProperty))
            runtimeMaterial.SetFloat(intensityProperty, originalIntensity);

        // 离开危险区 / 场景收尾：把整个 Full Screen Pass 关掉，强度也归 0（双保险）
        SetFeatureActive(false);
    }

    /// <summary>危险区外的每帧保险：强度压回 0，并把整个 Pass 关掉。
    /// 不管是谁（别的脚本、死亡演出、手动勾选）把它打开的，离开范围后都会被这里收回。</summary>
    private void EnsureVignetteClosed()
    {
        if (runtimeMaterial != null && runtimeMaterial.HasProperty(intensityProperty)
            && !Mathf.Approximately(runtimeMaterial.GetFloat(intensityProperty), 0f))
        {
            runtimeMaterial.SetFloat(intensityProperty, 0f);
        }

        if (targetFeature != null && targetFeature.isActive)
            SetFeatureActive(false);
    }

    /// <summary>进场景时先解析好 Feature，并【确保它是关闭的】。
    /// 暗角只在玩家踏进危险半径的那一刻才打开，其余时间这个 Full Screen Pass 完全不参与渲染。</summary>
    private void PrewarmVignette()
    {
        EnsureVignetteReady();

        // 无论 Renderer Data 里被人手动打开过、还是上一次死亡留下的状态，这里都压回关闭，
        // Update 里还有一层保险继续盯着它
        if (targetFeature != null)
            SetFeatureActive(false);
    }

    /// <summary>首次进入危险区时解析 Feature 并克隆一份运行时材质（只做一次，之后复用）。</summary>
    private void EnsureVignetteReady()
    {
        if (vignetteResolved)
            return;

        vignetteResolved = true;

        if (!ResolveVignetteFeature())
            return;

        originalMaterial = targetFeature.passMaterial;
        runtimeMaterial = new Material(originalMaterial);
        targetFeature.passMaterial = runtimeMaterial;

        // Feature 原始就是关的；还原时也一律还原成【关闭】
        featureWasActive = false;

        if (!runtimeMaterial.HasProperty(intensityProperty))
        {
            Debug.LogWarning($"[LightFlowerDrainZone] 材质 “{runtimeMaterial.name}” 里没有名为 “{intensityProperty}” 的 float 属性，" +
                             "暗角强度链接不上（把 Intensity Property 改成材质里真实存在的属性名即可）。", this);
        }

        // 初始强度一律强制为 0，不信任 .mat 资产里存的值：
        // 打包版里 .mat 打包时是什么值运行时就是什么值——如果打包那天材质里留着调试用的高强度，
        // Pass 一开画面就是满屏红（打包版开局发红的根源）。
        // 闪烁只在危险区里由 UpdateVignetteBlink 每帧写入，退出时也归 0。
        originalIntensity = 0f;
        if (runtimeMaterial.HasProperty(intensityProperty))
            runtimeMaterial.SetFloat(intensityProperty, 0f);

        // 解析完立刻把这个 Pass 关掉：此刻玩家还在危险区外
        SetFeatureActive(false);
    }

    private bool ResolveVignetteFeature()
    {
        ScriptableRendererData[] dataList = CollectRendererDataList();

        if (dataList == null || dataList.Length == 0)
        {
            Debug.LogWarning("[LightFlowerDrainZone] 未获取到任何 Renderer Data，" +
                             "暗角闪烁不可用（把 URP-HighFidelity-Renderer 拖到 Renderer Data 字段即可）。", this);
            return false;
        }

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

                // 显式指定了材质 → 精确匹配；没指定 → 按材质名含 Vignette 匹配（M_PP_Vignette）
                bool matched = vignetteMaterial != null
                    ? featureMaterial == vignetteMaterial
                    : featureMaterial.name.IndexOf("Vignette", StringComparison.OrdinalIgnoreCase) >= 0;

                if (matched)
                {
                    targetFeature = fs;
                    Debug.Log($"[LightFlowerDrainZone] 暗角已绑定到 “{data.name} / {fs.name}” " +
                             $"（材质 “{featureMaterial.name}”，isActive={fs.isActive}）。", this);
                    return true;
                }
            }
        }

        Debug.LogWarning("[LightFlowerDrainZone] 没有找到使用 M_PP_Vignette 的 Full Screen Pass Renderer Feature，" +
                         "暗角闪烁不可用（倒计时与死亡流程不受影响）。", this);
        return false;
    }

    private ScriptableRendererData[] CollectRendererDataList()
    {
        if (rendererData != null)
            return new[] { rendererData };

        UniversalRenderPipelineAsset pipeline =
            GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset ??
            GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;

        ScriptableRendererData[] reflected = pipeline != null ? GetRendererDataList(pipeline) : null;
        if (reflected != null && reflected.Length > 0)
            return reflected;

        // 兜底：打包后如果反射读不到（URP 内部字段名变了 / 被裁剪），
        // 直接扫一遍已经加载进内存的 Renderer Data，不再依赖反射
        ScriptableRendererData[] loaded = Resources.FindObjectsOfTypeAll<ScriptableRendererData>();
        if (loaded != null && loaded.Length > 0)
            return loaded;

        return reflected;
    }

    /// <summary>
    /// UniversalRenderPipelineAsset 没有公开的 rendererDataList，只能反射读内部序列化字段
    /// （与 FullScreenPassFader 一致）。
    /// </summary>
    private static ScriptableRendererData[] GetRendererDataList(UniversalRenderPipelineAsset urp)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        foreach (string fieldName in new[] { "m_RendererDataList", "rendererDataList" })
        {
            FieldInfo field = typeof(UniversalRenderPipelineAsset).GetField(fieldName, flags);
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
        if (targetFeature == null || targetFeature.isActive == active)
            return;

        targetFeature.SetActive(active);

        // 只在状态真的翻转时打一条，方便在打包版的 Player.log 里核对开关时机
        Debug.Log($"[LightFlowerDrainZone] Full Screen Pass(Vignette) → {(active ? "ON" : "OFF")} (frame {Time.frameCount})");
    }

    /// <summary>销毁 / 停用时还原 Renderer Data：材质引用还原、克隆销毁、Feature 开关还原。</summary>
    private void CleanupVignette()
    {
        RestoreVignette();

        if (targetFeature != null && runtimeMaterial != null && targetFeature.passMaterial == runtimeMaterial)
            targetFeature.passMaterial = originalMaterial;

        if (runtimeMaterial != null)
        {
            Destroy(runtimeMaterial);
            runtimeMaterial = null;
        }

        targetFeature = null;
        vignetteResolved = false;
    }

    // ---------------------------------------------------------------- 倒计时嘀嘀声

    /// <summary>按间隔触发嘀声；越接近归零 → 间隔越短、音高越高。</summary>
    private void UpdateBeep(float urgency)
    {
        float interval = Mathf.Max(0.03f, beepInterval);
        if (accelerateWithCountdown)
            interval = Mathf.Max(0.03f, Mathf.Lerp(interval, beepFinalInterval, Mathf.Clamp01(urgency)));

        beepTimer += Time.deltaTime;
        if (beepTimer < interval)
            return;

        beepTimer -= interval;
        PlayBeep(urgency);
    }

    /// <summary>响一声。urgency=0 是第一声，1 是最后一声。</summary>
    private void PlayBeep(float urgency)
    {
        if (beepSource == null)
            return;

        AudioClip clip = beepClip != null ? beepClip : generatedBeepClip;
        if (clip == null)
            return;

        // PlayOneShot 会套用 AudioSource 的 pitch，所以只用改音高就能让声音越来越“急”
        beepSource.pitch = Mathf.Lerp(beepBasePitch, beepFinalPitch, Mathf.Clamp01(urgency));

        float volume = beepVolume * Mathf.Lerp(1f, beepFinalVolumeBoost, Mathf.Clamp01(urgency));
        beepSource.PlayOneShot(clip, Mathf.Clamp01(volume));
    }

    /// <summary>被吸干那一刻的下扫闷响。</summary>
    private void PlayDrainSound()
    {
        if (beepSource == null)
            EnsureBeepAudioReady();

        if (beepSource == null)
            return;

        AudioClip clip = drainClip != null ? drainClip : generatedDrainClip;
        if (clip == null)
            return;

        beepSource.pitch = 1f;
        beepSource.PlayOneShot(clip, beepVolume);
    }

    private void EnsureBeepAudioReady()
    {
        if (beepSource == null)
        {
            beepSource = beepAudioSource != null ? beepAudioSource : GetComponent<AudioSource>();

            if (beepSource == null)
            {
                beepSource = gameObject.AddComponent<AudioSource>();
                beepSource.spatialBlend = 0f;      // 倒计时是“界面级”提示音，2D 播放，不随距离衰减
                beepSource.playOnAwake = false;
                beepSource.loop = false;
                beepSource.dopplerLevel = 0f;
            }
        }

        if (generatedBeepClip == null && beepClip == null)
            generatedBeepClip = CreateBeepClip();

        if (generatedDrainClip == null && drainClip == null && playDrainSoundOnDeath)
            generatedDrainClip = CreateDrainClip();
    }

    /// <summary>合成一个短促的“嘀”：880Hz 正弦 + 指数衰减包络（收尾自然，不会爆音）。</summary>
    private static AudioClip CreateBeepClip()
    {
        const int sampleRate = 44100;
        const float duration = 0.12f;
        const float frequency = 880f;

        int length = Mathf.RoundToInt(sampleRate * duration);
        AudioClip clip = AudioClip.Create("LightFlower_CountdownBeep", length, 1, sampleRate, false);

        float[] data = new float[length];
        for (int i = 0; i < length; i++)
        {
            float t = (float)i / sampleRate;
            float envelope = Mathf.Exp(-t * 26f);
            data[i] = Mathf.Sin(2f * Mathf.PI * frequency * t) * envelope * 0.9f;
        }

        clip.SetData(data, 0);
        return clip;
    }

    /// <summary>合成死亡闷响：频率 420Hz → 60Hz 下扫，两头淡入淡出。</summary>
    private static AudioClip CreateDrainClip()
    {
        const int sampleRate = 44100;
        const float duration = 0.7f;

        int length = Mathf.RoundToInt(sampleRate * duration);
        AudioClip clip = AudioClip.Create("LightFlower_Drained", length, 1, sampleRate, false);

        float[] data = new float[length];
        float phase = 0f;

        for (int i = 0; i < length; i++)
        {
            float t = (float)i / sampleRate;
            float p = Mathf.Clamp01(t / duration);

            float frequency = Mathf.Lerp(420f, 60f, p);
            phase += 2f * Mathf.PI * frequency / sampleRate;

            float envelope = Mathf.Sin(Mathf.PI * p);   // 0 → 1 → 0，避免起始/结尾的“啪”
            data[i] = Mathf.Sin(phase) * envelope * 0.85f;
        }

        clip.SetData(data, 0);
        return clip;
    }

    // ---------------------------------------------------------------- 氛围嗡鸣（音乐）

    private void EnsureHumReady()
    {
        AudioClip clip = humClip;

        // 没拖 clip 就合成一段低频嗡鸣，保证“音乐”一定在走
        if (clip == null && synthesizeAmbienceIfMissing)
        {
            if (generatedHumClip == null)
                generatedHumClip = CreateAmbientHumClip();
            clip = generatedHumClip;
        }

        if (clip == null)
            return;

        if (humSource == null)
        {
            humSource = gameObject.AddComponent<AudioSource>();
            humSource.spatialBlend = 0f;
            humSource.playOnAwake = false;
            humSource.dopplerLevel = 0f;
        }

        if (humSource.clip != clip)
            humSource.clip = clip;

        humSource.loop = true;
        humSource.volume = 0f;
        humSource.pitch = humStartPitch;

        if (!humSource.isPlaying)
            humSource.Play();
    }

    private void UpdateHum(float progress, float urgency)
    {
        if (humSource == null || !humSource.isPlaying)
            return;

        float volume = humFadesInWithCountdown
            ? Mathf.Lerp(0f, humVolume, Mathf.Clamp01(progress))
            : humVolume;

        if (humTightensWithCountdown)
        {
            // 音高往上顶：同一段素材越放越“紧”
            humSource.pitch = Mathf.Lerp(humStartPitch, humEndPitch, urgency);

            // 脉冲（心跳）：频率随紧张度加快，深度也跟着涨，到后面是一鼓一鼓地砸
            float pulseRate = Mathf.Lerp(humPulseStartRate, humPulseEndRate, urgency);
            humPulsePhase += Time.unscaledDeltaTime * pulseRate * Mathf.PI * 2f;
            if (humPulsePhase > Mathf.PI * 2f)
                humPulsePhase -= Mathf.PI * 2f;

            float depth = humPulseDepth * Mathf.Clamp01(urgency);
            volume *= Mathf.Max(0f, 1f + Mathf.Sin(humPulsePhase) * depth);
        }

        humSource.volume = Mathf.Clamp01(volume);
    }

    private void StopHum()
    {
        if (humSource == null)
            return;

        humSource.Stop();
        humSource.volume = 0f;
        humSource.pitch = humStartPitch;
    }

    /// <summary>
    /// 合成 4 秒无缝循环的低频嗡鸣：41.25 / 55 / 82.5 / 110Hz 叠层 + 0.5Hz 呼吸起伏。
    /// 这些频率都是 0.25Hz（1/4 秒）的整数倍，所以首尾刚好接上，loop 时听不出接缝。
    /// </summary>
    private static AudioClip CreateAmbientHumClip()
    {
        const int sampleRate = 44100;
        const float duration = 4f;

        int length = Mathf.RoundToInt(sampleRate * duration);
        AudioClip clip = AudioClip.Create("LightFlower_AmbientHum", length, 1, sampleRate, false);

        float[] data = new float[length];
        for (int i = 0; i < length; i++)
        {
            float t = (float)i / sampleRate;

            float wave = Mathf.Sin(2f * Mathf.PI * 41.25f * t) * 0.5f
                       + Mathf.Sin(2f * Mathf.PI * 55f * t)
                       + Mathf.Sin(2f * Mathf.PI * 82.5f * t) * 0.45f
                       + Mathf.Sin(2f * Mathf.PI * 110f * t) * 0.2f;

            // 0.5Hz 的“呼吸”，让嗡鸣不是死平一条线；同样能整除，循环无缝
            float breath = 0.75f + 0.25f * Mathf.Sin(2f * Mathf.PI * 0.5f * t);

            data[i] = Mathf.Clamp(wave * 0.28f * breath, -1f, 1f);
        }

        clip.SetData(data, 0);
        return clip;
    }

    private void CleanupBeepAudio()
    {
        StopHum();

        if (generatedHumClip != null)
        {
            Destroy(generatedHumClip);
            generatedHumClip = null;
        }

        if (beepSource != null && beepSource != beepAudioSource)
        {
            // 只停自己创建的那个声源；外部拖进来的可能还在放别的东西，别给人家掐了
            beepSource.Stop();
            beepSource.pitch = 1f;
        }

        // 合成的 clip 是运行时资源，必须自己销毁，否则每次进出区域都会泄漏一份
        if (generatedBeepClip != null)
        {
            Destroy(generatedBeepClip);
            generatedBeepClip = null;
        }

        if (generatedDrainClip != null)
        {
            Destroy(generatedDrainClip);
            generatedDrainClip = null;
        }
    }

    // ---------------------------------------------------------------- 调试

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Transform c = dangerCenter != null ? dangerCenter : transform;

        Gizmos.color = new Color(1f, 0.25f, 0.6f, 0.35f);
        Gizmos.DrawWireSphere(c.position, dangerRadius);
    }
#endif
}
