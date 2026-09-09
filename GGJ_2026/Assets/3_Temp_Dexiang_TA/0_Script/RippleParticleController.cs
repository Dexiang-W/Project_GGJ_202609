using System.Reflection;
using UnityEngine;

/// <summary>
/// 踩水波纹控制器（配合 GGJ_RippleSetup 一键装配使用，仅装配在 Level2）。
///
/// 职责：
///   1) 让粒子发射器每帧吸附到玩家脚底（不需要把粒子挂到玩家层级下面，独立物体也能跟随）；
///   2) 只在“着地(Grounded) + 脚底位于 Water 层 Trigger 水区”时才开发射
///      → 性能优化：非水区/空中/离地一律不产粒子；
///   3) 踩水音效开关（目前无音频素材，splashClip 留空即可；以后拖入 AudioClip 自动出声，
///      开关 = playSplashSound，不需要时关掉即可）。
///
/// 一键装配后你只需要调：
///   · 水材质 NPR_Water 里的 Ripple 强度/大小/颜色（已由你写好的参数）
///   · 场景里 "Particle System_Ripple" 的 Scale / startSize（涟漪大小）
///   · 有音效素材后拖到 splashClip
/// </summary>
public class RippleParticleController : MonoBehaviour
{
    [Header("引用设置")]
    [Tooltip("带 public Grounded 字段/属性的玩家控制器（如 ThirdPersonController）。不填时自动在 Player 标签下找")]
    public MonoBehaviour playerController;

    [Tooltip("要开关的涟漪 ParticleSystem。不填则取本物体身上的")]
    public ParticleSystem rippleParticle;

    [Tooltip("跟随目标（玩家脚底锚点）。不填自动找 Player 标签")]
    public Transform followTarget;

    [Header("脚底跟随")]
    [Tooltip("在脚底基础上额外抬高（默认 0。有 CharacterController 时自动取胶囊底部）")]
    public float followOffsetY = 0f;

    [Header("性能优化：只在有水的地方发射")]
    [Tooltip("开 = 玩家只有站在 Water 层 Trigger 上才发射；关 = 任何着地移动都发射（不建议）")]
    public bool waterEmissionOnly = true;

    [Tooltip("水区判定层。Level2 一键装配生成的 RippleZone_* 就挂在该层（默认 Water=4）")]
    public LayerMask waterLayers = 1 << 4;

    [Tooltip("脚底探测球半径")]
    public float waterCheckRadius = 0.3f;

    [Tooltip("探测点相对脚底抬高的高度（避免只贴到水面下薄片导致漏判）")]
    public float waterProbeHeight = 0.15f;

    [Header("踩水音效（素材接入开关）")]
    [Tooltip("总开关：关掉后不播踩水音（粒子照常）。没有素材前保持开着即可，不影响运行")]
    public bool playSplashSound = true;

    [Tooltip("音效源。不填时若拖了 splashClip 会在本物体上自动创建")]
    public AudioSource splashSource;

    [Tooltip("踩水音效素材，留空则不出声（GGJ 目前还没有素材，等拿到拖进来即可）")]
    public AudioClip splashClip;

    [Range(0f, 1f)] public float splashVolume = 1f;

    [Tooltip("大约走多少米踩出一声")]
    public float splashEveryMeters = 0.7f;

    [Tooltip("两次音效最短间隔（防止原地快速转身刷音）")]
    public float splashMinInterval = 0.2f;

    // ---------------------------------------------------------------- 运行时状态
    private FieldInfo groundedField;
    private PropertyInfo groundedProperty;
    private bool groundedAccessorWarned;
    private bool audioSourceWarned;
    private CharacterController cachedCC;
    private Transform cachedTarget;
    private bool hasLastPos;
    private Vector3 lastPos;
    private float splashDistanceAccum;
    private float lastSplashTime = -999f;

    private void Start()
    {
        ResolveReferences();
        lastPos = transform.position;
        hasLastPos = true;
    }

    private void Update()
    {
        ResolveReferences();
        if (playerController == null || rippleParticle == null) return;

        // 1. 跟随玩家脚底（世界空间，不依赖父子结构）
        FollowPlayerFeet();

        // 2. 着地 &&（可选）在水区 才允许发射
        bool grounded = ReadGrounded();
        bool inWater = !waterEmissionOnly || IsOverWater();
        bool shouldEmit = grounded && inWater;

        var emission = rippleParticle.emission;
        if (emission.enabled != shouldEmit)
            emission.enabled = shouldEmit;

        // 3. 踩水音效（按移动距离触发）
        UpdateSplash(shouldEmit);
    }

    // ------------------------------------------------------------ 跟随 / 判定

    private void FollowPlayerFeet()
    {
        if (followTarget == null) return;

        if (followTarget != cachedTarget)
        {
            cachedTarget = followTarget;
            cachedCC = followTarget.GetComponent<CharacterController>();
        }

        Vector3 p = followTarget.position;
        if (cachedCC != null)
        {
            // 脚底 = 目标原点 + CharacterController 底部偏移（胶囊 center.y - height/2）
            float feetY = p.y + cachedCC.center.y - cachedCC.height * 0.5f;
            p = new Vector3(p.x, feetY, p.z);
        }
        p.y += followOffsetY;

        transform.position = p;
    }

    private bool ReadGrounded()
    {
        if (groundedField == null && groundedProperty == null && playerController != null)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            groundedField = playerController.GetType().GetField("Grounded", flags);
            if (groundedField == null)
                groundedProperty = playerController.GetType().GetProperty("Grounded", flags);
        }

        if (groundedField != null)
            return (bool)groundedField.GetValue(playerController);
        if (groundedProperty != null)
            return (bool)groundedProperty.GetValue(playerController, null);

        if (!groundedAccessorWarned)
        {
            groundedAccessorWarned = true;
            Debug.LogWarning("[踩水波纹] 玩家控制器上没有找到 public Grounded 字段/属性，已停发粒子。", this);
        }
        return false;
    }

    private bool IsOverWater()
    {
        if (waterLayers.value == 0) return true; // 层为 0 时表示不限制

        Vector3 center = transform.position + Vector3.up * waterProbeHeight;
        return Physics.CheckSphere(center, waterCheckRadius, waterLayers, QueryTriggerInteraction.Collide);
    }

    private void UpdateSplash(bool emitting)
    {
        if (!playSplashSound || splashClip == null) return;
        if (splashSource == null)
        {
            // 有素材但没挂音效源：自动补一个，做到“拖入 clip 即出声”
            AudioSource src = GetComponent<AudioSource>();
            if (src == null)
            {
                src = gameObject.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 1f;
                src.volume = splashVolume;
            }
            splashSource = src;
        }

        if (splashSource == null)
        {
            if (!audioSourceWarned)
            {
                audioSourceWarned = true;
                Debug.LogWarning("[踩水波纹] 创建 AudioSource 失败，踩水音效不可用（粒子不受影响）。", this);
            }
            return;
        }

        if (!emitting)
        {
            splashDistanceAccum = 0f;
            hasLastPos = false;
            return;
        }

        if (!hasLastPos)
        {
            lastPos = transform.position;
            hasLastPos = true;
        }

        float moved = Vector3.Distance(transform.position, lastPos);
        lastPos = transform.position;
        splashDistanceAccum += moved;

        float now = Time.time;
        if (splashDistanceAccum >= splashEveryMeters && now - lastSplashTime >= splashMinInterval)
        {
            splashSource.PlayOneShot(splashClip, splashVolume);
            splashDistanceAccum = 0f;
            lastSplashTime = now;
        }
    }

    // ------------------------------------------------------------ 自动补引用

    private bool ResolveReferences()
    {
        if (rippleParticle == null)
            rippleParticle = GetComponent<ParticleSystem>();

        // 原来指向的角色被隐藏(SetActive false)/销毁后，自动让位并重新按 Player 标签找“当前激活”的角色。
        // 这样测试时随便隐藏哪个角色（比如把 Level2 默认人物藏起来换用别的角色），效果都会跟到真正在跑的人。
        if (followTarget != null && !followTarget.gameObject.activeInHierarchy)
        {
            followTarget = null;
            cachedTarget = null;
            cachedCC = null;
        }
        if (playerController != null && !playerController.gameObject.activeInHierarchy)
        {
            playerController = null;
            groundedField = null;
            groundedProperty = null;
        }

        if (followTarget == null)
        {
            GameObject player = GameObject.FindGameObjectWithTag("Player");
            if (player != null) followTarget = player.transform;
        }

        if (playerController == null)
        {
            GameObject player = GameObject.FindGameObjectWithTag("Player");
            if (player != null)
            {
                // 找带 public Grounded 字段/属性的组件（对组件名称/命名空间宽容），只挑当前激活的
                foreach (var mb in player.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    if (!mb.gameObject.activeInHierarchy) continue;
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    if (mb.GetType().GetField("Grounded", flags) != null ||
                        mb.GetType().GetProperty("Grounded", flags) != null)
                    {
                        playerController = mb;
                        break;
                    }
                }
            }
        }

        return playerController != null && rippleParticle != null;
    }

    // ------------------------------------------------------------ Gizmos（编辑器中方便调大小/看判定区）

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (rippleParticle == null) return;

        // 发射器中心
        Gizmos.color = new Color(0f, 1f, 1f, 0.9f);
        Gizmos.DrawWireCube(transform.position, Vector3.one * 0.2f);

        // 水判定球
        if (waterEmissionOnly && waterLayers.value != 0)
        {
            Gizmos.color = new Color(1f, 1f, 0f, 0.8f);
            Gizmos.DrawWireSphere(transform.position + Vector3.up * waterProbeHeight, waterCheckRadius);
        }

        // 跟随目标连线
        if (followTarget != null)
        {
            Gizmos.color = new Color(0f, 1f, 0f, 0.5f);
            Gizmos.DrawLine(transform.position, followTarget.position);
        }
    }
#endif
}
