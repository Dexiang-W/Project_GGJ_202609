using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 绑定风场方向的沙尘暴粒子系统。
///
/// 使用方式：
///  1. 场景里放一个空物体，把 Sc_WindDirection.cs 挂上去旋转，决定风吹方向；
///  2. 新建一个空物体，挂上本组件（会自动带上 ParticleSystem），
///     把风向物体的 Transform 拖到 windSource；不拖则读取全局 _WindDirection。
///
/// 挂上后粒子会持续沿 windSource.forward / _WindDirection 的水平方向被风吹走，
/// 可通过 Inspector 参数实时微调沙尘密度、大小、风力和湍流。
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(ParticleSystem))]
[DisallowMultipleComponent]
public class Sc_SandStorm : MonoBehaviour
{
    // ---------------- 风向来源（优先级：手动 > windSource > 全局 _WindDirection） ----------------
    [Header("Wind Input / 风向来源")]
    [Tooltip("勾选后忽略其他来源，直接用 manualWind 方向")]
    public bool useManualWind = false;
    [Tooltip("手动指定一个风向（自动忽略 Y）")]
    public Vector3 manualWind = new Vector3(1f, 0f, 0.3f);

    [Tooltip("拖入挂有 WindDirection.cs 的物体：用它 transform.forward 作为风向")]
    public Transform windSource;

    [Tooltip("未指定 windSource 时，读取 WindDirection.cs 每帧写入的全局向量 _WindDirection")]
    public bool readGlobalWind = true;

    // ---------------- 沙尘视觉 ----------------
    [Header("Sand Look / 沙尘")]
    [Tooltip("单个沙粒存活时间范围（秒），越大飞得越远")]
    public Vector2 lifetime = new Vector2(1.6f, 3.2f);
    [Tooltip("单粒沙尘尺寸范围")]
    public Vector2 grainSize = new Vector2(0.15f, 0.55f);
    [Tooltip("粒子上限")]
    public int maxParticles = 3000;
    public Color sandColorLo = new Color(0.93f, 0.76f, 0.52f);
    public Color sandColorHi = new Color(0.70f, 0.52f, 0.36f);

    // ---------------- 风力 ----------------
    [Header("Wind Force / 风力")]
    [Tooltip("沙粒沿风向的基础速度")]
    public float windSpeed = 9f;
    [Tooltip("初始随机速度上限（制造快慢不一的飞沙）")]
    public float speedRandom = 2.5f;
    [Tooltip("垂直上升的扬尘速度")]
    public float liftY = 1.6f;
    [Range(0f, 1f), Tooltip("湍流/乱流强度（沙尘翻滚感）")]
    public float turbulence = 0.5f;
    [Tooltip("湍流噪声滚动速度（越小越柔和）")]
    public float turbulenceScroll = 0.25f;
    [Range(0f, 2f), Tooltip("阵风强弱（沙尘一阵一阵）")]
    public float gustStrength = 0.5f;
    [Tooltip("阵风频率")]
    public float gustFrequency = 0.9f;

    // ---------------- 发射区域 ----------------
    [Header("Emission Area / 发射区域")]
    [Tooltip("发射区域尺寸: X=横向宽度, Y=高度, Z=顺风长度")]
    public Vector3 areaSize = new Vector3(20f, 6f, 46f);
    [Tooltip("每秒发射沙粒数量（密度）")]
    public float areaRate = 550f;
    [Range(0f, 1f), Tooltip("发射速率随机抖动")]
    public float rateVariance = 0.3f;
    [Tooltip("锚在地面：发射区域从物体所在高度往上长（建议把物体放在地面）")]
    public bool anchorToGround = true;

    // ---------------- 渲染 ----------------
    [Header("Render / 渲染")]
    [Tooltip("Billboard=团状沙尘; Stretch=拉成条状飞沙(拖尾更明显)")]
    public ParticleSystemRenderMode renderMode = ParticleSystemRenderMode.Stretch;
    [Tooltip("条状拉长倍率，越大拖尾越长")]
    public float stretch = 0.8f;
    [Tooltip("按速度拉长系数")]
    public float velocityScale = 0.18f;
    [Tooltip("整体透明度（乘在粒子颜色上）")]
    [Range(0f, 1f)] public float opacity = 0.9f;

    // ---------------- runtime 缓存 ----------------
    private ParticleSystem _ps;
    private ParticleSystemRenderer _psr;
    private Material _mat;
    private Texture2D _dotTex;
    private Vector3 _lastWind;
    private float _lastSpeed = -1f;
    private float _lastLift = -1f;
    private static readonly int WindDirId = Shader.PropertyToID("_WindDirection");

    public ParticleSystem Particle => _ps;

    // =================================================================================================
    void Reset()
    {
        EnsureRefs();
        ApplyPreset();
    }

    void Awake()
    {
        EnsureRefs();
    }

    void OnEnable()
    {
        EnsureRefs();
        ApplyPreset();
    }

    void OnValidate()
    {
        // 编辑器里改参数实时刷新；运行时改参数在 Reset()/OnValidate 同样生效
        if (!Application.isPlaying)
        {
            EnsureRefs();
            ApplyPreset();
        }
    }

    void Update()
    {
        if (_ps == null) return;

        Vector3 wind = ResolveWindDir();
        if (wind != _lastWind || !Mathf.Approximately(windSpeed, _lastSpeed) || !Mathf.Approximately(liftY, _lastLift))
        {
            SetWindOrientation(wind);
            _lastWind = wind;
            _lastSpeed = windSpeed;
            _lastLift = liftY;
        }

        // 阵风：发射速率随时间起伏
        if (Application.isPlaying && gustStrength > 0.001f)
        {
            var em = _ps.emission;
            em.rateOverTimeMultiplier = 1f + gustStrength * Mathf.Sin(Time.time * gustFrequency * Mathf.PI * 2f);
        }
    }

    void OnDestroy()
    {
        if (_dotTex != null) { if (Application.isPlaying) Destroy(_dotTex); else DestroyImmediate(_dotTex); _dotTex = null; }
        if (_mat != null) { if (Application.isPlaying) Destroy(_mat); else DestroyImmediate(_mat); _mat = null; }
    }

    // =================================================================================================
    /// <summary> 获取当前风向（世界空间，水平归一化） </summary>
    public Vector3 ResolveWindDir()
    {
        Vector3 w;
        if (useManualWind)
        {
            w = manualWind;
        }
        else if (windSource != null)
        {
            w = windSource.forward;
        }
        else if (readGlobalWind)
        {
            Vector4 g = Shader.GetGlobalVector(WindDirId);
            w = new Vector3(g.x, 0f, g.z);
            if (w.sqrMagnitude < 1e-6f) w = manualWind; // 全局还没写入时用兜底方向
        }
        else
        {
            w = transform.forward;
        }

        w.y = 0f;
        if (w.sqrMagnitude < 1e-6f)
        {
            w = Vector3.forward;
        }
        return w.normalized;
    }

    /// <summary> 把发射盒朝向 + 粒子飞行速度朝向风向 </summary>
    public void SetWindOrientation(Vector3 worldWindDir)
    {
        if (_ps == null) return;

        Vector3 n = worldWindDir;
        n.y = 0f;
        if (n.sqrMagnitude < 1e-6f) n = Vector3.forward;
        n.Normalize();

        // 让发射盒的“顺风方向(本地Z)”对准风
        float yaw = Mathf.Atan2(n.x, n.z) * Mathf.Rad2Deg;
        var shape = _ps.shape;
        Vector3 rot = new Vector3(0f, yaw, 0f);
        if (shape.rotation != rot) shape.rotation = rot;

        // 恒定风速：世界空间，把粒子一直往风向推
        var vel = _ps.velocityOverLifetime;
        if (!vel.enabled) { vel.enabled = true; vel.space = ParticleSystemSimulationSpace.World; }
        vel.x = n.x * windSpeed;
        vel.y = liftY;
        vel.z = n.z * windSpeed;
    }

    // =================================================================================================
    /// <summary> 用当前 Inspector 参数整体重建粒子外观（可在代码或 ContextMenu 里调用） </summary>
    [ContextMenu("Rebuild Sandstorm Modules")]
    public void ApplyPreset()
    {
        EnsureRefs();
        if (_ps == null) return;

        // 必须先彻底停止系统（StopEmittingAndClear），否则在播放状态设置
        // main.duration / emission / shape 等模块会抛
        // "Setting the duration while system is still playing is not supported."
        _ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        // ---- Main ----
        var main = _ps.main;
        main.loop = true;
        main.playOnAwake = true;
        main.duration = Mathf.Max(lifetime.y * 2.2f, 5f);
        main.startLifetime = new ParticleSystem.MinMaxCurve(lifetime.x, lifetime.y);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0f, speedRandom);
        main.startSize = new ParticleSystem.MinMaxCurve(grainSize.x, grainSize.y);
        main.startColor = new ParticleSystem.MinMaxGradient(sandColorLo, sandColorHi);
        main.gravityModifier = 0f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = maxParticles;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;

        // ---- Emission ----
        var em = _ps.emission;
        em.enabled = true;
        em.rateOverTime = new ParticleSystem.MinMaxCurve(
            Mathf.Max(1f, areaRate * (1f - rateVariance)),
            areaRate * (1f + rateVariance));
        em.rateOverTimeMultiplier = 1f;
        em.SetBursts(new ParticleSystem.Burst[0]);

        // ---- Shape（矩形体发射区） ----
        var shape = _ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(areaSize.x, areaSize.y, areaSize.z);
        shape.position = new Vector3(0f, anchorToGround ? areaSize.y * 0.5f : 0f, 0f);
        shape.randomDirectionAmount = 1f;
        shape.sphericalDirectionAmount = 0f;
        shape.alignToDirection = false;

        // ---- Velocity over lifetime（每帧会被 SetWindOrientation 更新方向） ----
        var vel = _ps.velocityOverLifetime;
        vel.enabled = true;
        vel.space = ParticleSystemSimulationSpace.World;

        // ---- Noise（湍流翻滚） ----
        var noise = _ps.noise;
        if (turbulence > 0.001f)
        {
            noise.enabled = true;
            noise.strength = new ParticleSystem.MinMaxCurve(turbulence);
            noise.frequency = 0.6f;
            noise.scrollSpeed = new ParticleSystem.MinMaxCurve(turbulenceScroll);
            noise.damping = true;
            noise.quality = ParticleSystemNoiseQuality.High;
            noise.octaveCount = 2;
        }
        else
        {
            noise.enabled = false;
        }

        // ---- Color over lifetime（出现淡入 + 末端淡出） ----
        var col = _ps.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        float a = Mathf.Clamp01(opacity);
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(a, 0.12f),
                new GradientAlphaKey(a, 0.75f),
                new GradientAlphaKey(0f, 1f)
            });
        col.color = new ParticleSystem.MinMaxGradient(g);

        // ---- Size over lifetime（小变大，末端略收） ----
        var so = _ps.sizeOverLifetime;
        so.enabled = true;
        var sizeCurve = new AnimationCurve(
            new Keyframe(0f, 0.15f),
            new Keyframe(0.15f, 1f),
            new Keyframe(0.75f, 1f),
            new Keyframe(1f, 0.85f));
        so.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

        // ---- Renderer / Material ----
        EnsureParticleMaterial();
        if (_mat != null)
        {
            _psr.material = _mat;
            _psr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _psr.receiveShadows = false;
            _psr.renderMode = renderMode;
            _psr.lengthScale = stretch;
            _psr.velocityScale = velocityScale;
        }

        // 重新出发一次，让改动立即生效
        _ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        _ps.Play();

        Vector3 w0 = ResolveWindDir();
        SetWindOrientation(w0);
        _lastWind = w0;
        _lastSpeed = windSpeed;
        _lastLift = liftY;
    }

    // =================================================================================================
    void EnsureRefs()
    {
        if (_ps == null)
        {
            _ps = GetComponent<ParticleSystem>();
            if (_ps == null) _ps = gameObject.AddComponent<ParticleSystem>();
        }
        if (_psr == null) _psr = GetComponent<ParticleSystemRenderer>();
    }

    const string ParticleShaderName = "TA/SandStorm/Particle";

    void EnsureParticleMaterial()
    {
        if (_mat != null) return;

        if (_dotTex == null) _dotTex = CreateSoftDotTexture();

        Shader sh = Shader.Find(ParticleShaderName);
        if (sh == null) sh = Shader.Find("Particles/Standard Unlit");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (sh == null)
        {
            Debug.LogWarning("[SandStorm] 找不到沙尘粒子 Shader(" + ParticleShaderName +
                             ")，请确认 Shader 已导入，或手动给 ParticleSystemRenderer 指定材质。", this);
            return;
        }

        _mat = new Material(sh) { name = "SandStorm_ParticleMat" };
        if (_mat.HasProperty("_MainTex")) _mat.SetTexture("_MainTex", _dotTex);
        if (_mat.HasProperty("_BaseMap")) _mat.SetTexture("_BaseMap", _dotTex);
        if (_mat.HasProperty("_Tint")) _mat.SetColor("_Tint", Color.white);
        if (_mat.HasProperty("_BaseColor")) _mat.SetColor("_BaseColor", Color.white);
        if (_mat.HasProperty("_TintColor")) _mat.SetColor("_TintColor", Color.white);
        if (_mat.HasProperty("_Color")) _mat.SetColor("_Color", Color.white);

        _mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        _mat.SetOverrideTag("RenderType", "Transparent");
    }

    /// <summary> 生成一个柔和圆点贴图，用来做沙尘粒子 </summary>
    static Texture2D CreateSoftDotTexture()
    {
        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "SandStorm_SoftDot",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.DontSave
        };

        var px = new Color[size * size];
        float half = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x - half) / half;
                float dy = (y - half) / half;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float a = 1f - Mathf.SmoothStep(0.55f, 1.05f, r);
                a *= a;
                px[y * size + x] = new Color(1f, 1f, 1f, a);
            }
        }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }
}
