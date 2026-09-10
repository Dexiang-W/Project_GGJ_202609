using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 用 Unity Shuriken（粒子系统）制作「水珠贴在球体外表面、持续绕球旋转流动」的效果。
///
/// 核心思路（不逐帧改粒子位置，纯 Shuriken 模块实现）：
///   1) Shape = Sphere，Radius Thickness ≈ 0  → 粒子只生成在球面薄壳上（贴附表面）
///   2) Velocity over Lifetime 的 Orbital     → 粒子绕发射器的某条轴做圆周运动
///      球面上的点绕「过球心的任意轴」旋转后仍然落在球面上，所以水珠会一直贴着球面走，不会飞出去
///   3) 多条不同倾角的 Band（子粒子系统）叠加 → 交织、自然的水流
///
/// 使用步骤：
///   1. 场景里放一个实心球（Sphere），把它的 Scale 调成你想要的尺寸
///   2. 新建一个空物体，位置放到球心（0,0,0 或球心处）
///   3. 把这个脚本挂到该空物体上，把球体拖进 Sphere Reference（自动读取半径）
///   4. 直接 Play（也可以在组件右键菜单点「重新生成水珠」做编辑器内预览）
/// </summary>
[DisallowMultipleComponent]
public class WaterOrbitParticles : MonoBehaviour
{
    // ------------------------------------------------------------------
    //  Inspector
    // ------------------------------------------------------------------

    [Header("球体 (Sphere)")]
    [Tooltip("可选：把场景里的球体拖进来，会自动读取它的半径。留空则使用下面的 Sphere Radius")]
    public Transform sphereReference;

    [Tooltip("粒子贴附的球体半径（要和场景里球的半径一致，球默认 Scale=1 时半径为 0.5）")]
    [Min(0.01f)] public float sphereRadius = 1f;

    [Tooltip("在球面外再外扩一点，避免水珠和球面产生深度冲突(Z-fighting)")]
    [Min(0f)] public float surfaceOffset = 0.02f;

    [Header("水珠数量与大小")]
    [Tooltip("水珠总数（会平均分配到各条水带）")]
    [Min(1)] public int dropletCount = 400;

    [Tooltip("单颗水珠的直径（世界单位）")]
    [Min(0.001f)] public float dropletSize = 0.12f;

    [Tooltip("水珠大小随机差异。太大就会出现很多小碎点，看起来像雾气")]
    [Range(0f, 1f)] public float sizeVariation = 0.2f;

    [Header("环绕流动 (Orbit)")]
    [Tooltip("每条水带绕其自身轴旋转的速度（度/秒）")]
    public float orbitSpeed = 55f;

    [Range(0f, 1f)] public float speedVariation = 0.3f;

    [Tooltip("水带数量：多条不同倾角的水带交织，比单一方向自然得多")]
    [Min(1)] public int flowBands = 4;

    [Tooltip("每条水带相对水平面的最大随机倾角（度）")]
    [Range(0f, 90f)] public float tiltVariation = 60f;

    [Tooltip("水珠在球面上随机乱流的强度（0 = 完全规整的圆周运动）。太大会有飘散的烟雾感")]
    [Range(0f, 1f)] public float turbulence = 0.08f;

    [Header("外观 (Look)")]
    [Tooltip("全局水珠颜色（只取 RGB）。它是颜色扰动和逐带色相的基准色；A 通道不参与计算")]
    public Color waterColor = new Color(0.42f, 0.74f, 0.96f, 1f);

    [Tooltip("整体不透明度（总开关）。水珠最实能到多少 = 它 × 下面水的质感里的 Core Opacity，两项都拉满才完全不透明")]
    [Range(0f, 1f)] public float opacity = 1f;

    [Tooltip("水珠贴图。留空则自动生成一张程序化水滴轮廓遮罩")]
    public Texture2D dropletTexture;

    public ParticleSystemRenderMode renderMode = ParticleSystemRenderMode.Billboard;

    [Header("水的质感 (Water Look)")]
    [Tooltip("水珠中心的不透明度上限。1 = 完全实心（推荐）；调低会让中心比边缘透一点、多一层体积感，但整体会发虚")]
    [Range(0f, 1f)] public float coreOpacity = 1f;

    [Tooltip("珠体的明暗。小于 1 会让水珠比背景暗，是「液体」而不是「发光气团」的关键")]
    [Range(0f, 2f)] public float bodyBrightness = 0.85f;

    [Tooltip("迎光侧亮边的亮度（水的菲涅尔反光）")]
    [Range(0f, 4f)] public float rimBoost = 2.4f;

    [Tooltip("边缘反光的收窄程度：值越大，亮边越窄越锐")]
    [Range(0.5f, 8f)] public float rimPower = 2.4f;

    [Tooltip("背光侧暗边有多暗。0 = 边缘一整圈均匀发亮（那是能量罩的样子）")]
    [Range(0f, 1f)] public float shadowRimDarkness = 0.62f;

    [Tooltip("内圈折光暗带的强度，珠子「透镜感」的关键")]
    [Range(0f, 1f)] public float innerBandStrength = 0.45f;

    [Tooltip("高光点强度")]
    [Range(0f, 4f)] public float highlightIntensity = 1.2f;

    [Tooltip("高光点的锐利程度：值越大亮点越小越锐，太低会糊成一大团白斑")]
    [Range(2f, 200f)] public float highlightSharpness = 80f;

    [Header("屏幕空间折射 (需要先开 Opaque Texture)")]
    [Tooltip("真正弯折背景的折射。开启前必须先在 URP Asset 勾上 Opaque Texture，否则水珠会全黑")]
    public bool screenRefraction = false;

    [Tooltip("折射弯折的幅度")]
    [Range(0f, 0.25f)] public float refractionStrength = 0.08f;

    [Tooltip("折射区域占珠体的比例，太大会让中心显空")]
    [Range(0f, 1f)] public float refractionAmount = 0.7f;

    [Header("颜色扰动 (Colour Variation)")]
    [Tooltip("每颗水珠颜色的随机扰动幅度。0 = 所有水珠一模一样；0.3 左右最自然，能看到细微的深浅差异")]
    [Range(0f, 1f)] public float colorVariation = 0.3f;

    [Tooltip("各条水带之间的色相差异（度）。把每条带的颜色在色轮上均匀拉开；0 = 所有带同色")]
    [Range(0f, 180f)] public float bandHueSpread = 30f;

    [Tooltip("勾选后，忽略全局「水珠颜色」，改用下面每条水带各自的设置")]
    public bool overrideBandLooks = false;

    [Tooltip("逐条设置水带外观（颜色 / 大小 / 速度）。条目不够时会循环取用")]
    public BandLook[] bandLooks = new BandLook[]
    {
        new BandLook { tint = new Color(0.38f, 0.78f, 1.00f), sizeScale = 1.00f, speedScale = 1.00f },
        new BandLook { tint = new Color(0.60f, 0.94f, 0.92f), sizeScale = 1.35f, speedScale = -0.80f },
        new BandLook { tint = new Color(0.24f, 0.58f, 0.96f), sizeScale = 0.75f, speedScale = 1.35f },
        new BandLook { tint = new Color(0.74f, 0.96f, 1.00f), sizeScale = 1.15f, speedScale = -1.20f },
    };

    [Header("生命周期")]
    [Tooltip("单颗水珠存活时长（秒）。越长越稳定，越短越有流动/闪烁感")]
    [Min(0.5f)] public float dropletLifetime = 6f;

    /// <summary>单条水带的外观覆盖设置。</summary>
    [System.Serializable]
    public struct BandLook
    {
        [Tooltip("这条水带的颜色。A 通道不参与计算")]
        public Color tint;

        [Tooltip("这条水带的水珠大小倍率")]
        [Range(0.1f, 3f)] public float sizeScale;

        [Tooltip("这条水带的环绕速度倍率。负数 = 反向旋转，几条带反向交织起来会更有生气")]
        [Range(-3f, 3f)] public float speedScale;
    }

    /// <summary>单条水带最终生效的运行参数。</summary>
    struct BandSetup
    {
        public float rate;
        public float speedMul;
        public float sizeScale;
        public Color tintMin;
        public Color tintMax;
    }

    // ------------------------------------------------------------------
    //  内部
    // ------------------------------------------------------------------

    const string RootName = "__WaterDroplets";
    const string MaterialName = "M_WaterDrop(Runtime)";
    const string WaterShaderName = "Hongjia/WaterDropletParticle";

    Transform _root;
    Material _sharedMaterial;
    readonly List<GameObject> _spawned = new List<GameObject>();

    static Texture2D _cachedDropletTexture;

    void Awake()
    {
        Rebuild();
    }

    void OnDestroy()
    {
        Clear();
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        flowBands = Mathf.Max(1, flowBands);
        dropletCount = Mathf.Max(1, dropletCount);

        // 让逐带数组至少覆盖所有水带：不足就按已有项循环补齐，省得手动一条条加
        if (bandLooks == null || bandLooks.Length >= flowBands) return;

        var grown = new BandLook[flowBands];
        for (int i = 0; i < grown.Length; i++)
        {
            grown[i] = bandLooks.Length > 0
                ? bandLooks[i % bandLooks.Length]
                : new BandLook { tint = Color.white, sizeScale = 1f, speedScale = 1f };
        }
        bandLooks = grown;
    }
#endif

    /// <summary>重新生成整套水珠环绕效果。</summary>
    [ContextMenu("重新生成水珠 (Rebuild)")]
    public void Rebuild()
    {
        if (sphereReference != null)
        {
            var rend = sphereReference.GetComponentInChildren<Renderer>();
            if (rend != null) sphereRadius = Mathf.Max(0.01f, rend.bounds.extents.x);
        }

        Clear();

        _sharedMaterial = CreateWaterMaterial(
            dropletTexture != null ? dropletTexture : GetDropletTexture());

        _root = new GameObject(RootName).transform;
        _root.SetParent(transform, false);
        _root.localPosition = Vector3.zero;
        _root.localRotation = Quaternion.identity;
        _root.localScale = Vector3.one;

        int bands = Mathf.Max(1, flowBands);
        int baseCount = Mathf.Max(1, dropletCount / bands);
        int remainder = dropletCount % bands;
        float life = Mathf.Max(0.5f, dropletLifetime);

        for (int i = 0; i < bands; i++)
        {
            int countForThisBand = baseCount + (i < remainder ? 1 : 0);
            float rate = countForThisBand / life;

            // 每条水带一个随机倾角 + 随机起始方位，形成交织的水流
            float tilt = bands == 1 ? 0f : Random.Range(0f, tiltVariation) * (Random.value < 0.5f ? -1f : 1f);
            float yaw = Random.Range(0f, 360f);

            var go = new GameObject($"WaterDroplets_Band{i:00}");

            // 先禁用：AddComponent<ParticleSystem> 出来的系统默认 playOnAwake = true，
            // 会在"正在播放"的状态下被我们改 duration，从而触发 Unity 的警告。
            // 放到未激活的物体上配置，配置完再激活并 Play，就完全避开了这个问题。
            go.SetActive(false);

            go.transform.SetParent(_root, false);
            go.transform.localRotation = Quaternion.Euler(tilt, yaw, 0f);

            var ps = go.AddComponent<ParticleSystem>();
            var psr = go.GetComponent<ParticleSystemRenderer>();

            ConfigureSystem(ps, psr, BuildBandSetup(i, bands, rate));

            _spawned.Add(go);

            go.SetActive(true);

            // 若父物体此刻未激活，先不调用 Play；等它被激活时会由 playOnAwake 自动播放
            if (go.activeInHierarchy) ps.Play();
        }
    }

    /// <summary>把 Shuriken 各模块配置成「贴球面环绕流动」。</summary>
    void ConfigureSystem(ParticleSystem ps, ParticleSystemRenderer psr, BandSetup band)
    {
        // ---------- Main ----------
        var main = ps.main;
        main.duration = 5f;
        main.loop = true;
        main.prewarm = true;              // 一播放就是铺满的状态，不用等它攒粒子
        main.playOnAwake = true;
        main.startLifetime = new ParticleSystem.MinMaxCurve(dropletLifetime * 0.75f, dropletLifetime);
        main.startSpeed = 0f;             // 初速 0：原地生成，运动完全交给 Orbital
        main.startSize = new ParticleSystem.MinMaxCurve(
            dropletSize * (1f - sizeVariation) * band.sizeScale,
            dropletSize * (1f + sizeVariation) * band.sizeScale);

        // TwoColors 模式：粒子系统在两端点色之间随机取色，逐颗颜色扰动不用写代码
        main.startColor = new ParticleSystem.MinMaxGradient(band.tintMin, band.tintMax);

        // 不随机自转：反光由 Shader 按 UV 算出来，贴图一旦被旋转，高光就会跟着乱转
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, 0f);
        main.gravityModifier = 0f;        // 水珠贴在表面，不受重力
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        main.maxParticles = Mathf.CeilToInt(band.rate * dropletLifetime * 1.5f) + 32;

        // ---------- Emission ----------
        var emission = ps.emission;
        emission.enabled = true;
        emission.rateOverTime = band.rate;

        // ---------- Shape：只在球面薄壳上生成 ----------
        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = sphereRadius + surfaceOffset;
        shape.radiusThickness = 0.05f;    // 0 = 只有最外层球面，1 = 整个球体内部体积
        shape.position = Vector3.zero;
        shape.rotation = Vector3.zero;
        shape.randomDirectionAmount = 0f;

        // ---------- Velocity over Lifetime：绕轴圆周运动（关键） ----------
        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.space = ParticleSystemSimulationSpace.Local;
        vel.orbitalY = new ParticleSystem.MinMaxCurve(orbitSpeed * band.speedMul * Mathf.Deg2Rad); // 单位：弧度/秒
        vel.radial = 0f;                  // 不要径向速度，否则会飞离球面
        vel.speedModifier = 1f;

        // ---------- Size over Lifetime ----------
        // 恒定尺寸，生灭交给 alpha：0→1→0 的尺寸曲线会有"长大再缩小"的烟雾观感
        var sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = 1f;

        // ---------- Color over Lifetime ----------
        var colorOverLifetime = ps.colorOverLifetime;
        colorOverLifetime.enabled = true;
        colorOverLifetime.color = new ParticleSystem.MinMaxGradient(BuildFadeGradient());

        // ---------- Rotation over Lifetime：关闭 ----------
        // 自转会让 Shader 按 UV 算出的高光跟着乱转
        var rotationOverLifetime = ps.rotationOverLifetime;
        rotationOverLifetime.enabled = false;

        // ---------- Noise：让水珠在球面上有机地乱流 ----------
        var noise = ps.noise;
        noise.enabled = turbulence > 0.0001f;
        noise.quality = ParticleSystemNoiseQuality.Medium;
        noise.strength = new ParticleSystem.MinMaxCurve(turbulence);
        noise.frequency = 1.2f;
        noise.scrollSpeed = new ParticleSystem.MinMaxCurve(0.6f);
        noise.octaveCount = 2;
        noise.damping = true;

        // ---------- Renderer ----------
        psr.renderMode = renderMode;
        psr.alignment = ParticleSystemRenderSpace.View;
        psr.sortMode = ParticleSystemSortMode.None;
        psr.material = _sharedMaterial;
        psr.minParticleSize = 0f;
        psr.maxParticleSize = 0.5f;
        psr.shadowCastingMode = ShadowCastingMode.Off;
        psr.receiveShadows = false;

        // 注意：ParticleSystemRenderer 上并没有 trailEnabled 属性，
        // 控制拖尾要改 ParticleSystem 的 Trail 模块。
        var trails = ps.trails;
        trails.enabled = false;
    }

    /// <summary>把「全局设置 + 该条水带的覆盖设置」算成这条带最终要用的参数。</summary>
    BandSetup BuildBandSetup(int bandIndex, int bandCount, float rate)
    {
        BandLook look = ResolveBandLook(bandIndex);

        // 颜色基准：勾了逐带覆盖就用这条带自己的颜色，否则用全局色 + 色相拉开
        Color baseTint = overrideBandLooks
            ? look.tint
            : HueShiftedWaterColor(bandIndex, bandCount);
        baseTint.a = 1f;

        return new BandSetup
        {
            rate = rate,
            speedMul = Random.Range(1f - speedVariation, 1f + speedVariation) * look.speedScale,
            sizeScale = Mathf.Max(0.01f, look.sizeScale),

            // 取基准色两侧各一次作为扰动区间的两端；
            // 粒子系统会在两端之间逐颗随机取色，这就是"颜色扰动"
            tintMin = JitterColor(baseTint, colorVariation, -1f),
            tintMax = JitterColor(baseTint, colorVariation, +1f),
        };
    }

    /// <summary>取第 N 条水带的覆盖设置。未勾选逐带覆盖时返回中性值，即完全不干预。</summary>
    BandLook ResolveBandLook(int bandIndex)
    {
        if (overrideBandLooks && bandLooks != null && bandLooks.Length > 0)
        {
            return bandLooks[bandIndex % bandLooks.Length];
        }

        return new BandLook { tint = Color.white, sizeScale = 1f, speedScale = 1f };
    }

    /// <summary>以全局色为中心，按水带序号在色轮上均匀拉开色相。</summary>
    Color HueShiftedWaterColor(int bandIndex, int bandCount)
    {
        var baseColor = new Color(waterColor.r, waterColor.g, waterColor.b, 1f);
        if (bandCount <= 1 || bandHueSpread <= 0.0001f) return baseColor;

        Color.RGBToHSV(baseColor, out float h, out float s, out float v);

        float t = bandIndex / (float)(bandCount - 1);              // 0..1
        float offset = (t - 0.5f) * 2f * (bandHueSpread / 360f);   // 以基准色为中心往两边铺开

        var shifted = Color.HSVToRGB(Mathf.Repeat(h + offset, 1f), s, v);
        shifted.a = 1f;
        return shifted;
    }

    /// <summary>
    /// 把一个颜色沿色相 / 饱和度 / 明度各偏一点。
    /// sign 取 -1 / +1 得到「扰动区间的两端」，Shuriken 的 TwoColors 模式会在两端之间随机取色。
    /// </summary>
    static Color JitterColor(Color c, float amount, float sign)
    {
        var flat = new Color(c.r, c.g, c.b, 1f);
        if (amount <= 0.0001f) return flat;

        Color.RGBToHSV(flat, out float h, out float s, out float v);

        h += sign * 0.055f * amount;                          // 色相 ±20°
        s = Mathf.Clamp01(s * (1f + sign * 0.35f * amount));  // 饱和度 ±35%
        v = Mathf.Clamp01(v * (1f + sign * 0.28f * amount));  // 明度 ±28%

        var jittered = Color.HSVToRGB(Mathf.Repeat(h, 1f), s, v);
        jittered.a = 1f;
        return jittered;
    }

    Gradient BuildFadeGradient()
    {
        var g = new Gradient();
        g.SetKeys(
            new[]
            {
                // 颜色交给材质了，这里的顶点色只承载 alpha
                new GradientColorKey(Color.white, 0f),
                new GradientColorKey(Color.white, 1f),
            },
            new[]
            {
                // 快速出现，只在末尾淡出：淡出段越长，屏幕上半透明的水珠越多，整体越显虚
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(1f, 0.025f),
                new GradientAlphaKey(1f, 0.93f),
                new GradientAlphaKey(0f, 1f),
            });
        return g;
    }

    void Clear()
    {
        for (int i = _spawned.Count - 1; i >= 0; i--)
        {
            if (_spawned[i] != null) DestroySafe(_spawned[i]);
        }
        _spawned.Clear();

        if (_root != null)
        {
            DestroySafe(_root.gameObject);
            _root = null;
        }

        if (_sharedMaterial != null)
        {
            DestroySafe(_sharedMaterial);
            _sharedMaterial = null;
        }
    }

    static void DestroySafe(Object obj)
    {
        if (obj == null) return;
        if (Application.isPlaying) Destroy(obj);
        else DestroyImmediate(obj);
    }

    // ------------------------------------------------------------------
    //  材质 & 贴图
    // ------------------------------------------------------------------

    /// <summary>
    /// 创建水珠材质。优先用自定义 Shader（它用 UV 反推半球法线，能算出边缘光和高光）；
    /// 退回 URP 内置粒子 Shader 的话就只是一张平贴的半透明图。
    /// </summary>
    Material CreateWaterMaterial(Texture2D tex)
    {
        Shader shader = Shader.Find(WaterShaderName);

        if (shader != null)
        {
            var waterMat = new Material(shader) { name = MaterialName };
            waterMat.SetTexture("_MainTex", tex);

            // 颜色走顶点色，材质保持白色，只在 A 通道放整体不透明度
            waterMat.SetColor("_Color", new Color(1f, 1f, 1f, Mathf.Clamp01(opacity)));

            waterMat.SetFloat("_CoreOpacity", Mathf.Clamp01(coreOpacity));
            waterMat.SetFloat("_RimBoost", Mathf.Max(0f, rimBoost));
            waterMat.SetFloat("_RimPower", Mathf.Max(0.1f, rimPower));
            waterMat.SetFloat("_SpecIntensity", Mathf.Max(0f, highlightIntensity));
            waterMat.SetFloat("_SpecPower", Mathf.Max(1f, highlightSharpness));

            // 这三项都是让水珠「变暗」的，是液体感的关键
            waterMat.SetFloat("_BodyBrightness", Mathf.Max(0f, bodyBrightness));
            waterMat.SetFloat("_RimShadow", Mathf.Clamp01(shadowRimDarkness));
            waterMat.SetFloat("_BandStrength", Mathf.Clamp01(innerBandStrength));

            // 默认关闭；开了但 URP Asset 没勾 Opaque Texture 的话会采样到全黑
            waterMat.SetFloat("_RefractionStrength", Mathf.Clamp(refractionStrength, 0f, 0.25f));
            waterMat.SetFloat("_RefractionAmount", Mathf.Clamp01(refractionAmount));
            if (screenRefraction) waterMat.EnableKeyword("_REFRACTION_ON");
            else waterMat.DisableKeyword("_REFRACTION_ON");

            waterMat.renderQueue = (int)RenderQueue.Transparent;
            return waterMat;
        }

        Debug.LogWarning(
            $"[WaterOrbitParticles] 找不到 Shader \"{WaterShaderName}\"，已退回 URP 内置粒子材质，水珠质感会差一截。\n" +
            "请确认 Assets/2_Temp_Hongjia_3D/4_Shader/WaterDropletParticle.shader 存在，且 Console 里没有它的编译报错。\n" +
            "如果要出包体，还需要把它加进 Project Settings > Graphics > Always Included Shaders。",
            this);

        return CreateFallbackMaterial(tex);
    }

    /// <summary>自定义 Shader 不可用时的兜底：URP 内置的半透明粒子材质。</summary>
    Material CreateFallbackMaterial(Texture2D tex)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Particles/Lit");
        if (shader == null) shader = Shader.Find("Particles/Standard Unlit");
        if (shader == null) shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");

        var mat = new Material(shader) { name = MaterialName };
        // 同理：颜色走顶点色，材质只负责整体不透明度
        var tint = new Color(1f, 1f, 1f, Mathf.Clamp01(opacity));

        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
        if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);

        // URP 的透明设置（Surface = Transparent + Alpha Blend + 关深度写入）
        if (mat.HasProperty("_Surface"))
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetFloat("_AlphaClip", 0f);
            if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);

            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.DisableKeyword("_ALPHAMODULATE_ON");
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.renderQueue = (int)RenderQueue.Transparent;
        }

        return mat;
    }

    static Texture2D GetDropletTexture()
    {
        if (_cachedDropletTexture == null) _cachedDropletTexture = CreateDropletTexture(128);
        return _cachedDropletTexture;
    }

    /// <summary>
    /// 生成「水珠轮廓遮罩」：RGB 全白，只有 alpha 是圆形（边缘略微柔化）。
    /// 光影全部由 Shader 逐像素算，贴图只负责告诉 Shader 哪里是水珠。
    /// </summary>
    static Texture2D CreateDropletTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "T_WaterDropletMask(Generated)",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.DontSave,
        };

        var pixels = new Color[size * size];
        float half = size * 0.5f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // +0.5 取像素中心，避免边缘出现半像素偏差
                float dx = (x + 0.5f - half) / half;
                float dy = (y + 0.5f - half) / half;
                float d = Mathf.Sqrt(dx * dx + dy * dy);

                // 只柔化最外圈 4%（128px 上约 2.6px）：再多会软成烟，再少轮廓会出锯齿
                float alpha = 1f - Mathf.SmoothStep(0.96f, 1f, d);
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }

        tex.SetPixels(pixels);
        tex.Apply(false, false);
        return tex;
    }
}
