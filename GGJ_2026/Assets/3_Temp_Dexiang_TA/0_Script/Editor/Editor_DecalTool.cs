using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 透贴(Decal)美术工具：
///  美术只要「放一张贴图」→ 点一下 → 自动生成 材质 + 预制体(自带 Sc_TransparentDecal)，
///  生成后可以拖到场景 / 交给程序用 Sc_TransparentDecal.SpawnOnSurface 生成。
///
/// 透明度来源模式：
///  - A通道   ：贴图自带透明(如裂纹镂空PNG)，默认
///  - 亮度正相：无A通道的灰度图，亮度=透明度
///  - 亮度反相：白底黑图，黑=显示(常见裂缝贴图)
/// </summary>
public class Editor_DecalTool : EditorWindow
{
    private const string ShaderName = "Dexiang/Decal/Transparent";
    private const string DefaultMatDir = "Assets/3_Temp_Dexiang_TA/2_Material/Decal";
    private const string DefaultPrefabDir = "Assets/3_Temp_Dexiang_TA/5_Prefab/Decal";

    private enum AlphaSource { AlphaChannel = 0, Luminance = 1, LuminanceInverted = 2 }

    /// <summary>共享的四边形 Mesh 资产路径(放在 Prefab 目录下)</summary>
    private const string QuadMeshAssetName = "Mesh_DecalQuad.asset";

    [SerializeField] private Texture2D texture;
    [SerializeField] private float widthMeters = 1f;
    [SerializeField] private AlphaSource alphaSource = AlphaSource.AlphaChannel;
    [SerializeField] private Color tintColor = Color.white;   // 整体染色，白色=不改色
    [SerializeField] private string matDir = DefaultMatDir;
    [SerializeField] private string prefabDir = DefaultPrefabDir;

    private string _lastPrefabPath;
    private string _lastMaterialPath;
    private Vector2 _scroll;

    [MenuItem("Tools/Dexiang TA/透贴工具 Decal Tool", false, 20)]
    private static void OpenWindow()
    {
        var w = GetWindow<Editor_DecalTool>("透贴工具 Decal Tool");
        w.minSize = new Vector2(420, 300);
    }

    [MenuItem("Assets/Dexiang TA/创建透贴 (材质+Prefab)", false)]
    private static void CreateFromSelection()
    {
        Texture2D tex = Selection.activeObject as Texture2D;
        if (tex == null) { EditorUtility.DisplayDialog("提示", "请先选中一张贴图", "OK"); return; }

        var w = GetWindow<Editor_DecalTool>("透贴工具 Decal Tool");
        w.texture = tex;
        w.Show();
    }

    [MenuItem("Assets/Dexiang TA/创建透贴 (材质+Prefab)", true)]
    private static bool CreateFromSelectionValidate()
    {
        return Selection.activeObject is Texture2D;
    }

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "放一张【带透明通道】的裂缝/弹孔/污渍贴图 → 点【生成】→ 自动创建材质+预制体。\n" +
            "生成后可拖入场景微调，或直接交给程序 SpawnOnSurface 现场生成。",
            MessageType.Info);

        EditorGUILayout.Space();
        texture = (Texture2D)EditorGUILayout.ObjectField("贴图 Texture", texture,
            typeof(Texture2D), false);

        EditorGUI.BeginDisabledGroup(texture == null);
        EditorGUILayout.BeginHorizontal();
        {
            EditorGUILayout.PrefixLabel("参考尺寸(宽/米)");
            widthMeters = EditorGUILayout.FloatField(widthMeters);
        }
        EditorGUILayout.EndHorizontal();
        widthMeters = Mathf.Max(0.05f, widthMeters);

        alphaSource = (AlphaSource)EditorGUILayout.EnumPopup("透明度来源", alphaSource);
        if (alphaSource != AlphaSource.AlphaChannel)
            EditorGUILayout.HelpBox(
                "一般用带A通道的透明PNG即可，选【A通道】。\n" +
                "亮度正相=亮度当透明；亮度反相=黑显示(白底黑图才用)。",
                MessageType.None);

        EditorGUI.BeginChangeCheck();
        tintColor = EditorGUILayout.ColorField(
            new GUIContent("颜色 Tint", "整体染色，白色=不改色。点右侧吸色器可吸取表面颜色(如把裂缝染成墙面颜色)"),
            tintColor, true, false, false);
        if (EditorGUI.EndChangeCheck())
            ApplyTintToLastMaterial();
        if (tintColor != Color.white)
            EditorGUILayout.HelpBox("已染色：贴图颜色会乘上该 Tint。吸色器可吸取场景表面颜色。", MessageType.None);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("输出路径(默认在你的TA文件夹)", EditorStyles.boldLabel);
        matDir = EditorGUILayout.TextField("材质目录", matDir);
        prefabDir = EditorGUILayout.TextField("Prefab目录", prefabDir);

        EditorGUILayout.Space();
        EditorGUI.BeginDisabledGroup(string.IsNullOrEmpty(matDir) || string.IsNullOrEmpty(prefabDir));
        if (GUILayout.Button("生成 材质 + 预制体", GUILayout.Height(32)))
        {
            if (CreateFromTexture(texture, matDir, prefabDir, widthMeters, alphaSource, tintColor, out string matPath, out string prefabPath))
            {
                EditorGUILayout.HelpBox("生成成功!\n" + prefabPath, MessageType.None);
                _lastPrefabPath = prefabPath;
                _lastMaterialPath = matPath;
            }
        }
        EditorGUI.EndDisabledGroup();
        EditorGUI.EndDisabledGroup();

        if (texture != null)
            EditorGUILayout.HelpBox($"将自动按图片宽高比换算高度({AspectText()})，不会拉变形。", MessageType.None);

        EditorGUILayout.Space(8);
        if (GUILayout.Button("创建后放置到当前选中物体位置(测试)", GUILayout.Height(24)))
        {
            PlaceIntoScene();
        }
        EditorGUILayout.HelpBox(
            "放置测试：会 raycast 贴到【选中物体的表面】并自动对齐法线（选中球/地面/墙再点即可）。\n" +
            "没选中物体时，贴到 Scene 窗口画面中央的表面。程序里请用 SpawnOnSurface 按命中法线贴。",
            MessageType.None);

        EditorGUILayout.EndScrollView();
    }

    private string AspectText()
    {
        if (texture == null) return "1:1";
        return $"{texture.width}:{texture.height}";
    }

    /// <summary>把当前 Tint 立即写进上次生成的材质，方便生成后在场景里实时预览调色</summary>
    private void ApplyTintToLastMaterial()
    {
        if (string.IsNullOrEmpty(_lastMaterialPath)) return;
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(_lastMaterialPath);
        if (mat == null) return;
        mat.SetColor("_BaseColor", new Color(tintColor.r, tintColor.g, tintColor.b, 1f));
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
    }

    // =================================================================================
    // 生成逻辑
    // =================================================================================
    private static bool CreateFromTexture(Texture2D tex, string matDir, string prefabDir,
        float widthMeters, AlphaSource alphaSource, Color tint,
        out string materialPath, out string prefabPath)
    {
        materialPath = null;
        prefabPath = null;

        if (tex == null)
        {
            EditorUtility.DisplayDialog("透贴工具", "请先放入一张贴图", "OK");
            return false;
        }

        Shader shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            EditorUtility.DisplayDialog("透贴工具",
                "找不到 Shader: " + ShaderName + "\n\n可能是刚新增 shader 还没编译完，\n请等 Unity 编译完成后重试。",
                "OK");
            return false;
        }

        // ---- 1. 材质 ----
        string matFull = EnsureAssetFolder(matDir);
        string matAssetPath = Path.Combine(matFull, $"M_Decal_{tex.name}.mat").Replace('\\', '/');
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(matAssetPath);
        bool isNew = mat == null;
        if (isNew)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, matAssetPath);
        }

        mat.shader = shader;
        mat.SetTexture("_BaseMap", tex);
        mat.SetFloat("_Alpha", 1f);
        mat.SetColor("_BaseColor", new Color(tint.r, tint.g, tint.b, 1f));

        // 关键字设置
        bool useLum = alphaSource != AlphaSource.AlphaChannel;
        bool invert = alphaSource == AlphaSource.LuminanceInverted;
        SetKeyword(mat, "_USE_LUMINANCE", useLum);
        SetKeyword(mat, "_INVERT_ALPHA", invert);
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 100;
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();

        // ---- 2. 预制体 (Quad + 控制器) ----
        string prefabFull = EnsureAssetFolder(prefabDir);
        string prefabAssetPath = Path.Combine(prefabFull, $"P_Decal_{tex.name}.prefab").Replace('\\', '/');

        float aspect = tex.height / (float)Mathf.Max(1, tex.width);
        float w = widthMeters;
        float h = Mathf.Max(0.05f, widthMeters * aspect);

        var go = new GameObject($"Decal_{tex.name}");
        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = GetOrCreateQuadMeshAsset(prefabFull);   // 存成资产，避免重编译后丢失
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

        var decal = go.AddComponent<Sc_TransparentDecal>();
        decal.width = w;
        decal.height = h;

        // 默认平放在 y=0，方便预览；程序会用 SpawnOnSurface 自动贴法线
        go.transform.localScale = new Vector3(w, h, 1f);

        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(go, prefabAssetPath);
        UnityEngine.Object.DestroyImmediate(go);

        if (prefab == null)
        {
            EditorUtility.DisplayDialog("透贴工具", "预制体保存失败: " + prefabAssetPath, "OK");
            return false;
        }

        materialPath = matAssetPath;
        prefabPath = prefabAssetPath;

        AssetDatabase.SaveAssets();
        EditorUtility.FocusProjectWindow();
        Selection.activeObject = prefab;
        EditorGUIUtility.PingObject(prefab);

        Debug.Log($"[DecalTool] 已生成透贴\n  材质: {matAssetPath}{(isNew ? " (新建)" : " (已覆盖)")}\n  Prefab: {prefabAssetPath}");
        return true;
    }

    private static void SetKeyword(Material mat, string keyword, bool enabled)
    {
        if (enabled) mat.EnableKeyword(keyword);
        else mat.DisableKeyword(keyword);
    }

    private static Mesh GetOrCreateQuadMeshAsset(string folderAssetPath)
    {
        string meshPath = Path.Combine(folderAssetPath, QuadMeshAssetName).Replace('\\', '/');
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
        if (mesh != null) return mesh;

        mesh = new Mesh { name = "DecalQuad" };
        Vector3[] v =
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3(0.5f, -0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f),
        };
        Vector2[] uv =
        {
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(1, 1), new Vector2(0, 1),
        };
        int[] tris = { 0, 2, 1, 0, 3, 2 };
        mesh.vertices = v;
        mesh.uv = uv;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        AssetDatabase.CreateAsset(mesh, meshPath);
        return mesh;
    }

    private void PlaceIntoScene()
    {
        if (string.IsNullOrEmpty(_lastPrefabPath))
        {
            // 目录还没创建（没生成过）→ FindAssets 会报 Folder not found，先拦一下
            if (!AssetDatabase.IsValidFolder(prefabDir))
            {
                EditorUtility.DisplayDialog("透贴工具",
                    "还没生成过透贴，请先点【生成 材质 + 预制体】。\n\n(目录不存在: " + prefabDir + ")", "OK");
                return;
            }

            // 尝试从 prefabDir 里找第一个 P_Decal 开头 prefab
            string[] found = AssetDatabase.FindAssets("t:Prefab P_Decal_", new[] { prefabDir });
            if (found.Length == 0)
            {
                EditorUtility.DisplayDialog("透贴工具", "还没生成过透贴，请先点【生成 材质 + 预制体】。", "OK");
                return;
            }
            _lastPrefabPath = AssetDatabase.GUIDToAssetPath(found[0]);
        }

        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(_lastPrefabPath);
        if (prefab == null) return;

        Transform parent = Selection.activeTransform;

        // ---- 计算目标表面点 + 法线，让透贴贴合表面（和程序 SpawnOnSurface 同款逻辑） ----
        TryGetSurfaceOnScene(out Vector3 surfacePos, out Vector3 surfaceNormal, parent);
        var decal = prefab.GetComponent<Sc_TransparentDecal>();
        float offset = decal != null ? decal.surfaceOffset : 0.02f;

        GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab,
            parent != null ? parent.gameObject.scene : SceneManager.GetActiveScene());
        if (parent != null) inst.transform.SetParent(parent, true);

        Vector3 n = surfaceNormal.normalized;
        if (n.sqrMagnitude < 0.0001f) n = Vector3.up;
        Vector3 up = Mathf.Abs(n.y) > 0.99f ? Vector3.forward : Vector3.up;
        inst.transform.position = surfacePos + n * offset;
        inst.transform.rotation = Quaternion.LookRotation(n, up);

        // 保持尺寸比例和 prefab 一致
        if (decal != null)
            inst.transform.localScale = new Vector3(decal.width, decal.height, 1f);

        Undo.RegisterCreatedObjectUndo(inst, "Place Decal");
        Selection.activeGameObject = inst;
        EditorGUIUtility.PingObject(inst);
    }

    /// <summary>
    /// 尽量算出要贴到的表面点与法线：
    ///  1) 选中了物体：从 Scene 相机向该物体中心打射线，命中取其表面（贴到它朝向相机的一侧）；
    ///     若没有 collider 则回退用该物体的 up(朝上)。
    ///  2) 没选中：从 Scene 相机中心向外打射线，命中场景表面。
    ///  3) 都失败：父物体位置 / 原点 + 世界 up。
    /// </summary>
    private static void TryGetSurfaceOnScene(out Vector3 pos, out Vector3 normal, Transform parent)
    {
        pos = parent != null ? parent.position : Vector3.zero;
        normal = parent != null ? parent.up : Vector3.up;

        Camera cam = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : Camera.main;
        if (cam == null) return;

        RaycastHit hit;
        if (parent != null)
        {
            Vector3 center = parent.position;
            Vector3 dir = (center - cam.transform.position).normalized;
            if (Physics.Raycast(cam.transform.position, dir, out hit, 5000f))
            {
                pos = hit.point;
                normal = hit.normal;
            }
            return;
        }

        Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        if (Physics.Raycast(ray, out hit, 5000f))
        {
            pos = hit.point;
            normal = hit.normal;
        }
    }

    /// <summary>确保资产目录存在(支持多级)，返回规整后的相对路径</summary>
    private static string EnsureAssetFolder(string folderAssetPath)
    {
        folderAssetPath = folderAssetPath.Replace('\\', '/').TrimEnd('/');
        if (AssetDatabase.IsValidFolder(folderAssetPath)) return folderAssetPath;

        string root = "Assets";
        string rel = folderAssetPath;
        if (folderAssetPath.StartsWith("Assets/")) rel = folderAssetPath.Substring("Assets/".Length);

        string[] parts = rel.Split('/');
        string current = root;
        foreach (string p in parts)
        {
            if (string.IsNullOrEmpty(p)) continue;
            string next = current + "/" + p;
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, p);
            current = next;
        }
        return folderAssetPath;
    }
}
