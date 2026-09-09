using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Level2 踩水系统一键装配工具（GGJ_2026 专用，不会碰 Level1/Begin_Menu）。
///
/// 用法：打开 Level2 场景 → 菜单 Dexiang/踩水波纹/一键装配当前场景 → 保存场景。
///
/// 自动完成（全部生成到场景根节点 RippleSystem 下，不写任何 Prefab）：
///   1. RippleZone_*（水面可踩水判定区）
///      找出所有用 NPR_Water 材质的水面，按它的世界包围盒生成一个 Water 层 Trigger，
///      这就是“有水的地方”——只有玩家脚踩进这些 Trigger 才发射粒子（性能优化）。
///   2. Camera_Ripple（顶视 RT 相机）
///      正交向下、只渲 InteractiveRipple 层 → RT_Ripple，挂 RippleCameraFollow 跟随玩家，
///      实时把 _RippleTex / _RippleCamPos / _RippleCamSize 传给水 ShaderGraph（水材质 RT 对接）。
///   3. Particle System_Ripple（涟漪粒子发射器）
///      世界空间水平贴片，按“米”发射；挂 RippleParticleController：
///      自动贴玩家脚底 + 着地且在水区才发射 + AudioSource 踩水音效开关。
///   4. 把 InteractiveRipple 层从场景其它相机 cullingMask 剔除，防止粒子被主相机看见。
///
/// 一键装配后，你只需要调：
///   · NPR_Water 材质里的 Ripple 强度/大小/法线强度（你自己已调好的参数）
///   · 场景里 Particle System_Ripple 的 Scale / startSize（涟漪整体大小/扩散直径）
///   · 有音频素材后：把 clip 拖到该物体的 RippleParticleController.splashClip；
///     不需要音效就取消勾选 playSplashSound。粒子不受影响。
/// </summary>
public static class GGJ_RippleSetup
{
    private const string LayerName = "InteractiveRipple";

    // 资源路径（资源有移动请同步修改）
    private const string RT_Path = "Assets/3_Temp_Dexiang_TA/3_Textures/Water/RT_Ripple.renderTexture";
    private const string RipplePng_Path = "Assets/3_Temp_Dexiang_TA/3_Textures/Water/Ripple.png";
    private const string Mat_Dir = "Assets/3_Temp_Dexiang_TA/2_Material/PP";
    private const string Mat_Path = Mat_Dir + "/M_Ripple_ParticlesUnlit.mat";
    private const string WaterMat_Path = "Assets/3_Temp_Dexiang_TA/2_Material/NPR_Water.mat";

    // 相机参数（RT 视窗大小 = 2 * CameraOrthoSize 米）
    private const float CameraHeight = 20f;
    private const float CameraOrthoSize = 15f;
    private const float CameraNear = 0.3f;
    private const float CameraFar = 1000f;

    // 粒子参数（从 PTA 抄来的原始数值，可直接改这里后重新装配）
    private const float ParticleLifetimeMin = 2f;
    private const float ParticleLifetimeMax = 3f;
    private const float ParticleSizeMin = 35f;
    private const float ParticleSizeMax = 50f;
    private const float EmitRatePerMeter = 2.5f;
    private const float EmitterScale = 0.438f;   // 想整体放大/缩小涟漪就改它，或直接改场景里发射器的 Scale
    private const int ParticleMax = 1000;

    // 水面 Trigger 判定范围（米）。水面中心 = 水材质平面所在高度
    private const float ZoneUp = 1.2f;    // 水面往上多少算“在水里”（脚略高出水面也算）
    private const float ZoneDown = 2.5f;  // 水面往下多少算“在水里”（潜下去依然能踩出波纹）

    [MenuItem("Dexiang/踩水波纹/一键装配当前场景（水区+粒子+RT相机+音效）")]
    public static void BuildRippleSystem()
    {
        if (Application.isPlaying)
        {
            EditorUtility.DisplayDialog("踩水波纹", "请在停止运行（编辑模式）状态下执行装配。", "知道了");
            return;
        }

        var current = EditorSceneManager.GetActiveScene();
        if (string.IsNullOrEmpty(current.path))
        {
            EditorUtility.DisplayDialog("踩水波纹", "请先保存并打开一个场景（推荐 Level2），再执行装配。", "知道了");
            return;
        }

        // 0. 已存在系统：让用户决定是否重建
        GameObject existed = GameObject.Find("RippleSystem");
        if (existed != null)
        {
            int opt = EditorUtility.DisplayDialogComplex(
                "踩水波纹",
                "场景里已存在 RippleSystem。\n\n· 删除并重建：清掉旧系统后按最新参数重新生成\n· 取消：不执行任何操作",
                "删除并重建", "取消", "直接退出");
            if (opt == 0)
            {
                Object.DestroyImmediate(existed);
            }
            else
            {
                return;
            }
        }

        // 1. 层 & 资源准备
        int rippleLayer = EnsureInteractiveRippleLayer();
        if (rippleLayer < 0) return;

        Material waterMat = AssetDatabase.LoadAssetAtPath<Material>(WaterMat_Path);
        if (waterMat == null)
        {
            EditorUtility.DisplayDialog("踩水波纹", "找不到水材质 NPR_Water.mat，请检查资源路径。", "知道了");
            return;
        }

        RenderTexture rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(RT_Path);
        if (rt == null)
        {
            EditorUtility.DisplayDialog("踩水波纹", "找不到 RT_Ripple.renderTexture，请检查资源路径。", "知道了");
            return;
        }

        // 2. 找出所有用该水材质的水面（按 sharedMaterial 引用/名称匹配，兼容场景实例）
        List<MeshRenderer> waterRenderers = FindWaterRenderers(waterMat);
        if (waterRenderers.Count == 0)
        {
            EditorUtility.DisplayDialog("踩水波纹",
                "当前场景里没有找到用 NPR_Water 材质的水面。\n\n此工具只应在有踩水水面的场景使用（推荐 Level2）。",
                "知道了");
            return;
        }

        Material mat = CreateOrGetParticleMaterial();
        if (mat == null) return;

        GameObject player = GameObject.FindGameObjectWithTag("Player");
        Transform playerTransform = player != null ? player.transform : null;

        // 3. 创建系统根节点
        GameObject systemRoot = new GameObject("RippleSystem");
        systemRoot.transform.SetAsFirstSibling();
        Undo.RegisterCreatedObjectUndo(systemRoot, "踩水波纹装配");

        // 3.1 水面 Trigger（性能优化：只在这些区域发射）
        int waterLayer = LayerMask.NameToLayer("Water");
        if (waterLayer < 0) waterLayer = 4;
        int zoneCount = CreateWaterZones(systemRoot.transform, waterRenderers, waterLayer);

        // 3.2 顶视 RT 相机
        Vector3 basePos = playerTransform != null
            ? playerTransform.position
            : waterRenderers[0].bounds.center;
        CreateRippleCamera(systemRoot.transform, playerTransform, rt, rippleLayer, basePos);

        // 3.3 涟漪粒子发射器 + 踩水控制 + 音效开关
        GameObject emitGO = CreateEmitter(systemRoot.transform, playerTransform, mat, rippleLayer, waterLayer);

        // 3.4 把 InteractiveRipple 层从其它相机剔除
        ExcludeLayerFromOtherCameras(systemRoot.GetComponentInChildren<Camera>(), rippleLayer);

        EditorSceneManager.MarkSceneDirty(current);
        Selection.activeGameObject = emitGO;
        SceneView.FrameLastActiveSceneView();

        string playerNote = playerTransform != null
            ? "粒子自动跟随玩家脚底"
            : "当前场景无激活的 Player（运行时会由代码自动寻找 Player 标签）";
        Debug.Log("[踩水波纹] 装配完成！\n" +
                  "- 水面 Trigger × " + zoneCount + "（Water 层，非水区不产粒子）\n" +
                  "- Camera_Ripple → RT_Ripple（水材质 _RippleTex 已对接）\n" +
                  "- Particle System_Ripple（" + playerNote + "）\n" +
                  "- AudioSource + playSplashSound 踩水音效开关（拖入 clip 即出声）\n" +
                  "现在只需调：NPR_Water 材质的 Ripple 参数、Particle System_Ripple 的 Scale/startSize。\n" +
                  "请保存场景后运行验证。");
    }

    [MenuItem("Dexiang/踩水波纹/重刷粒子美术参数（Color/Size/渲染对齐 PTA，保留手调）")]
    public static void RefreshParticleArt()
    {
        if (Application.isPlaying)
        {
            EditorUtility.DisplayDialog("踩水波纹", "请在停止运行（编辑模式）状态下执行。", "知道了");
            return;
        }

        GameObject root = GameObject.Find("RippleSystem");
        ParticleSystem ps = null;
        if (root != null)
        {
            Transform t = root.transform.Find("Particle System_Ripple");
            if (t != null) ps = t.GetComponent<ParticleSystem>();
        }
        if (ps == null)
        {
            EditorUtility.DisplayDialog("踩水波纹",
                "当前场景没有可更新的粒子（找不到 RippleSystem / Particle System_Ripple）。\n请先执行“一键装配”。",
                "知道了");
            return;
        }

        Material mat = CreateOrGetParticleMaterial();
        if (mat == null) return;

        ConfigureParticleSystem(ps, mat);   // 只刷新美术参数，不动发射器的 Scale / startSize 等手调

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("[踩水波纹] 已按 PTA 刷新粒子美术参数（Color Over Lifetime / Size 曲线切线 / 渲染排序 / GPU 实例化）。");
    }

    [MenuItem("Dexiang/踩水波纹/重指向当前激活的 Player（隐藏角色/换人后用）")]
    public static void RepointToActivePlayer()
    {
        if (Application.isPlaying)
        {
            EditorUtility.DisplayDialog("踩水波纹", "请在停止运行（编辑模式）状态下执行。", "知道了");
            return;
        }

        GameObject root = GameObject.Find("RippleSystem");
        if (root == null)
        {
            EditorUtility.DisplayDialog("踩水波纹", "当前场景没有 RippleSystem，请先执行“一键装配”。", "知道了");
            return;
        }

        // 只认“当前激活”的 Player 标签角色：被隐藏的 Level2 人物/旧角色一律不算
        GameObject player = GameObject.FindGameObjectWithTag("Player");
        if (player == null)
        {
            EditorUtility.DisplayDialog("踩水波纹",
                "当前场景里没有“激活且带 Player 标签”的角色，无法自动重指向。\n\n" +
                "请先确认你要操作的人物已激活，并把它的 Player 物体（带 CharacterController / ThirdPersonController 的那一级）\n" +
                "的 Tag 设为 Player；\n" +
                "或直接在 Camera_Ripple 的 RippleCameraFollow、Particle System_Ripple 的 RippleParticleController 上手动拖引用。",
                "知道了");
            return;
        }

        bool anyChanged = false;
        Transform camT = root.transform.Find("Camera_Ripple");
        if (camT != null)
        {
            RippleCameraFollow follow = camT.GetComponent<RippleCameraFollow>();
            if (follow != null)
            {
                follow.target = player.transform;
                anyChanged = true;
            }
        }

        Transform emitT = root.transform.Find("Particle System_Ripple");
        if (emitT != null)
        {
            RippleParticleController ctrl = emitT.GetComponent<RippleParticleController>();
            if (ctrl != null)
            {
                ctrl.followTarget = player.transform;
                ctrl.playerController = FindGroundedController(player.transform);
                anyChanged = true;
            }
        }

        if (!anyChanged)
        {
            EditorUtility.DisplayDialog("踩水波纹", "RippleSystem 里没找到 RippleCameraFollow / RippleParticleController 组件。\n建议直接删掉 RippleSystem 重新一键装配。", "知道了");
            return;
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log($"[踩水波纹] 已把相机与粒子重新指向当前激活的角色：{player.name}");
    }

    // ------------------------------------------------------------ 水面判定区

    private static List<MeshRenderer> FindWaterRenderers(Material waterMat)
    {
        List<MeshRenderer> result = new List<MeshRenderer>();
        foreach (MeshRenderer mr in Object.FindObjectsOfType<MeshRenderer>(true))
        {
            if (mr == null || !mr.gameObject.activeInHierarchy || !mr.enabled) continue;

            Material[] mats = mr.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) continue;
                if (mats[i] == waterMat || mats[i].name == waterMat.name)
                {
                    result.Add(mr);
                    break;
                }
            }
        }
        return result;
    }

    private static int CreateWaterZones(Transform root, List<MeshRenderer> waterRenderers, int waterLayer)
    {
        int count = 0;
        foreach (MeshRenderer mr in waterRenderers)
        {
            Bounds b = mr.bounds;

            // 竖直方向以水面中心为基准：上 1.2m / 下 2.5m
            float topY = b.center.y + ZoneUp;
            float bottomY = b.center.y - ZoneDown;
            Vector3 center = new Vector3(b.center.x, (topY + bottomY) * 0.5f, b.center.z);
            Vector3 size = new Vector3(b.size.x, topY - bottomY, b.size.z);
            if (size.x < 0.1f) size.x = 0.1f;
            if (size.z < 0.1f) size.z = 0.1f;

            GameObject zone = new GameObject("RippleZone_" + mr.gameObject.name);
            zone.transform.SetParent(root, false);
            zone.transform.position = center;
            zone.transform.rotation = Quaternion.identity;
            zone.transform.localScale = Vector3.one;
            zone.layer = waterLayer;

            BoxCollider col = zone.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.size = size;

            Undo.RegisterCreatedObjectUndo(zone, "踩水波纹装配");
            count++;
        }
        return count;
    }

    // ------------------------------------------------------------ 顶视 RT 相机

    private static void CreateRippleCamera(Transform root, Transform playerTransform,
        RenderTexture rt, int rippleLayer, Vector3 basePos)
    {
        GameObject camGO = new GameObject("Camera_Ripple");
        camGO.transform.SetParent(root, false);
        camGO.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        camGO.transform.position = new Vector3(basePos.x, CameraHeight, basePos.z);

        Camera cam = camGO.AddComponent<Camera>();
        cam.orthographic = true;
        cam.orthographicSize = CameraOrthoSize;
        cam.nearClipPlane = CameraNear;
        cam.farClipPlane = CameraFar;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        cam.cullingMask = 1 << rippleLayer;
        cam.targetTexture = rt;
        cam.depth = -10;
        cam.allowHDR = true;
        cam.allowMSAA = true;

        UniversalAdditionalCameraData urp = camGO.GetComponent<UniversalAdditionalCameraData>();
        if (urp == null) urp = camGO.AddComponent<UniversalAdditionalCameraData>();
        urp.renderType = CameraRenderType.Base;

        RippleCameraFollow follow = camGO.AddComponent<RippleCameraFollow>();
        follow.rippleCamera = cam;
        follow.target = playerTransform;
        follow.cameraHeight = CameraHeight;
        follow.smoothness = 0.1f;
        follow.enableSnapToPixel = true;
        follow.rippleRT = rt;

        Undo.RegisterCreatedObjectUndo(camGO, "踩水波纹装配");
    }

    // ------------------------------------------------------------ 粒子 + 控制器 + 音效

    private static GameObject CreateEmitter(Transform root, Transform playerTransform,
        Material mat, int rippleLayer, int waterLayer)
    {
        GameObject emitGO = new GameObject("Particle System_Ripple");
        emitGO.transform.SetParent(root, false);
        emitGO.transform.localPosition = Vector3.zero;
        emitGO.transform.localScale = Vector3.one * EmitterScale;
        emitGO.layer = rippleLayer;

        ParticleSystem ps = emitGO.AddComponent<ParticleSystem>();
        ConfigureParticleSystem(ps, mat);

        RippleParticleController ctrl = emitGO.AddComponent<RippleParticleController>();
        ctrl.rippleParticle = ps;
        if (playerTransform != null)
        {
            ctrl.followTarget = playerTransform;
            ctrl.playerController = FindGroundedController(playerTransform);
        }

        // 性能优化：只有 Water 层（自动生成的水面 Trigger）内才发射
        ctrl.waterEmissionOnly = true;
        ctrl.waterLayers = 1 << waterLayer;

        // 踩水音效：先给好开关与音源，splashClip 留空等素材
        AudioSource src = emitGO.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.spatialBlend = 1f;
        src.volume = 1f;
        src.minDistance = 1f;
        src.maxDistance = 40f;
        src.rolloffMode = AudioRolloffMode.Logarithmic;
        ctrl.playSplashSound = true;
        ctrl.splashSource = src;
        ctrl.splashClip = null;

        Undo.RegisterCreatedObjectUndo(emitGO, "踩水波纹装配");
        return emitGO;
    }

    private static void ConfigureParticleSystem(ParticleSystem ps, Material mat)
    {
        var main = ps.main;
        main.loop = true;
        main.prewarm = true;
        main.playOnAwake = true;
        main.duration = 5f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;   // 粒子留在世界空间，形成一圈圈足迹/扩散
        main.scalingMode = ParticleSystemScalingMode.Local;
        main.startLifetime = new ParticleSystem.MinMaxCurve(ParticleLifetimeMin, ParticleLifetimeMax);
        main.startSpeed = 0f;
        main.startSize = new ParticleSystem.MinMaxCurve(ParticleSizeMin, ParticleSizeMax);
        main.startRotation = 0f;
        main.startColor = Color.white;   // 与 PTA 一致（白色粒子，颜色交给 Color Over Lifetime 做）
        main.gravityModifier = 0f;
        main.maxParticles = ParticleMax;

        // 发射：按“米”发射（走路多少米出一圈），不随时间乱出
        var emission = ps.emission;
        emission.enabled = true;
        emission.rateOverTime = 0f;
        emission.rateOverDistance = EmitRatePerMeter;
        emission.SetBursts(new ParticleSystem.Burst[0]);

        // 形状：脚底附近极小的线段 → 等效点源
        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.SingleSidedEdge;
        shape.radius = 0.1f;
        shape.scale = Vector3.one;

        // 大小随时间扩散（涟漪圈从小涨大），曲线关键点与切线数值和 PTA 场景里完全一致
        var sol = ps.sizeOverLifetime;
        sol.enabled = true;
        var growth = new AnimationCurve(
            new Keyframe(0f, 0.0604248f, 0f, 0f),
            new Keyframe(0.0212550f, 0.1554094f, 0.39000136f, 0.39000136f),
            new Keyframe(0.7003776f, 0.5973769f, 1.0258766f, 1.0258766f),
            new Keyframe(1f, 1f, 1f, 1f)
        );
        sol.size = new ParticleSystem.MinMaxCurve(1f, growth);

        // 颜色随时间变化：蓝色水花 + 淡入→保持→末期淡出（alpha 关键点 0 / 0.0735 / 0.4118 / 1）
        var col = ps.colorOverLifetime;
        col.enabled = true;
        var rippleGradient = new Gradient();
        rippleGradient.colorKeys = new[]
        {
            new GradientColorKey(new Color(0f, 0f, 1f), 0f),   // 纯蓝水花（与 PTA 一致）
            new GradientColorKey(new Color(0f, 0f, 1f), 1f)
        };
        rippleGradient.alphaKeys = new[]
        {
            new GradientAlphaKey(0f, 0f),
            new GradientAlphaKey(1f, 0.07354f),   // 出生很快淡入
            new GradientAlphaKey(1f, 0.41177f),   // 保持不透明到约 41%
            new GradientAlphaKey(0f, 1f)          // 末期淡出
        };
        col.color = new ParticleSystem.MinMaxGradient(rippleGradient);

        var psr = ps.GetComponent<ParticleSystemRenderer>();
        psr.material = mat;
        psr.renderMode = ParticleSystemRenderMode.HorizontalBillboard; // 顶视相机拍到的水平贴片
        psr.sortMode = ParticleSystemSortMode.YoungestInFront;          // 与 PTA 一致
        psr.enableGPUInstancing = true;                                 // 与 PTA 一致
        psr.maxParticleSize = 0.5f;                                     // 与 PTA 一致
        psr.shadowCastingMode = ShadowCastingMode.Off;
        psr.receiveShadows = false;
    }

    // ------------------------------------------------------------ 粒子材质（没有就生成）

    private static Material CreateOrGetParticleMaterial()
    {
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(Mat_Path);
        if (existing != null) return existing;

        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null)
        {
            EditorUtility.DisplayDialog("踩水波纹", "找不到 URP Particles/Unlit shader，无法生成粒子材质。", "知道了");
            return null;
        }

        Texture ripple = AssetDatabase.LoadAssetAtPath<Texture>(RipplePng_Path);
        if (ripple == null)
        {
            EditorUtility.DisplayDialog("踩水波纹", "找不到 Ripple.png，无法生成粒子材质。", "知道了");
            return null;
        }

        if (!AssetDatabase.IsValidFolder(Mat_Dir))
        {
            string parent = Mat_Dir.Substring(0, Mat_Dir.LastIndexOf('/'));
            string name = Mat_Dir.Substring(Mat_Dir.LastIndexOf('/') + 1);
            AssetDatabase.CreateFolder(parent, name);
        }

        Material mat = new Material(shader);
        AssetDatabase.CreateAsset(mat, Mat_Path);

        // 还原 PTA 的 M_Ripple_ParticlesUnlit.mat 关键设置（透明、Alpha 混合、关 ZWrite）
        mat.SetTexture("_BaseMap", ripple);
        mat.SetColor("_BaseColor", Color.white);
        mat.SetFloat("_Surface", 1f);
        mat.SetFloat("_Blend", 0f);
        mat.SetFloat("_SrcBlend", 5f);          // SrcAlpha
        mat.SetFloat("_DstBlend", 10f);         // OneMinusSrcAlpha
        mat.SetFloat("_SrcBlendAlpha", 1f);
        mat.SetFloat("_DstBlendAlpha", 10f);
        mat.SetFloat("_ZWrite", 0f);
        mat.SetFloat("_Cull", 2f);
        mat.SetFloat("_AlphaClip", 0f);
        mat.SetFloat("_LightingEnabled", 0f);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = (int)RenderQueue.Transparent;
        mat.SetOverrideTag("RenderType", "Transparent");

        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
        return mat;
    }

    // ------------------------------------------------------------ 层 / 相机剔除 / 引用查找

    private static int EnsureInteractiveRippleLayer()
    {
        int existing = LayerMask.NameToLayer(LayerName);
        if (existing >= 0) return existing;

        var tagManagerAsset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
        if (tagManagerAsset == null || tagManagerAsset.Length == 0)
        {
            EditorUtility.DisplayDialog("踩水波纹", "读不到 TagManager.asset，无法自动加层。请手动在 Project Settings → Tags and Layers 添加 " + LayerName, "知道了");
            return -1;
        }

        var so = new SerializedObject(tagManagerAsset[0]);
        var layers = so.FindProperty("layers");
        for (int i = 8; i < layers.arraySize; i++)
        {
            var sp = layers.GetArrayElementAtIndex(i);
            if (string.IsNullOrEmpty(sp.stringValue))
            {
                sp.stringValue = LayerName;
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                Debug.Log("[踩水波纹] 已在层 " + i + " 添加 " + LayerName);
                return i;
            }
        }

        EditorUtility.DisplayDialog("踩水波纹", "用户自定义层 8~31 已用完，无法自动加层。", "知道了");
        return -1;
    }

    private static void ExcludeLayerFromOtherCameras(Camera keepCamera, int layer)
    {
        foreach (Camera cam in Object.FindObjectsOfType<Camera>(true))
        {
            if (cam == null || cam == keepCamera) continue;
            int mask = cam.cullingMask;
            if ((mask & (1 << layer)) != 0)
            {
                cam.cullingMask = mask & ~(1 << layer);
                EditorUtility.SetDirty(cam);
            }
        }
    }

    private static MonoBehaviour FindGroundedController(Transform playerRoot)
    {
        foreach (var mb in playerRoot.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null) continue;
            System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;
            if (mb.GetType().GetField("Grounded", flags) != null ||
                mb.GetType().GetProperty("Grounded", flags) != null)
            {
                return mb;
            }
        }
        return null;
    }
}
