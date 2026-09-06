using UnityEditor;
using UnityEngine;

/// <summary>
/// 把新玩法机制（重生/能量拾取/弹跳板/可交互）在“当前打开的场景”里生成一套演示物体。
///
/// 用法：
///   1. 用 Unity 打开 Level1 场景；
///   2. 菜单 GGJ2026 → Level1 → 生成玩法演示物体；
///   3. 根节点 “GGJ_Demo_Gameplay（演示物体，可删）” 下就是全部演示内容；
///      你可以在 Inspector 里改文字、重生点、弹力数值，把布局调好后再删掉多余演示物。
///
/// 生成位置以场景中的 SpawnPoint 为基准向右依次排列。仅做演示，不会影响其它关卡。
/// </summary>
public static class GGJLevel1DemoBuilder
{
    private const string DemoRootName = "GGJ_Demo_Gameplay（演示物体，可删）";

    [MenuItem("GGJ2026/Level1 → 生成玩法演示物体（重生/能量/弹跳/交互）")]
    public static void BuildDemos()
    {
        GameObject spawn = GameObject.Find("SpawnPoint");
        if (spawn == null)
            spawn = GameObject.Find("Spawn");

        if (spawn == null)
        {
            EditorUtility.DisplayDialog("缺少生成点",
                "当前场景没找到名为 SpawnPoint 的物体。\n请在 Level1 中先摆好出生点，再执行本菜单。", "知道了");
            return;
        }

        bool isLevel1 = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Level1";
        if (!isLevel1 &&
            !EditorUtility.DisplayDialog("当前不是 Level1",
                $"当前场景是 {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}，不是 Level1。\n仍要继续吗？（其它关卡不会被改动）",
                "继续", "取消"))
        {
            return;
        }

        Transform existing = spawn.transform.root.Find(DemoRootName);
        if (existing != null)
            Object.DestroyImmediate(existing.gameObject);

        Vector3 s = spawn.transform.position;
        float groundY = s.y;

        GameObject root = new GameObject(DemoRootName);
        root.transform.position = Vector3.zero;

        // 用一个重生点（传送回这里，紧挨出生点前面一点点）
        Transform respawnPoint = CreateEmpty(root.transform, "RespawnPoint_Demo", new Vector3(s.x + 3f, groundY, s.z));

        // —— ① 能量拾取物 ×2（凑两格能量，方便测试弹跳板两档力度） ——
        CreatePickup(root.transform, "EnergyPickup_1（浅绿→亮绿）", new Vector3(s.x + 9f, groundY + 0.6f, s.z),
            new Vector3(3f, 1.5f, 3f), new Color(0f, 1f, 0f, 0.5f));

        CreatePickup(root.transform, "EnergyPickup_2（浅绿→亮绿）", new Vector3(s.x + 14f, groundY + 0.6f, s.z),
            new Vector3(3f, 1.5f, 3f), new Color(0f, 1f, 0f, 0.5f));

        // —— ② 弹跳板（0/1/2 格能量三档力度，数值在 BouncePad 上可调） ——
        BouncePad pad = CreateVolume<BouncePad>(root.transform, "BouncePad_Energy（踩上去会弹起）",
            new Vector3(s.x + 21f, groundY + 0.9f, s.z), new Vector3(5f, 2f, 5f), new Color(1f, 0.65f, 0f, 0.55f));

        // —— ③ 可交互物体演示：一个方块，靠近高亮，E 键交互（命令请在 On Interact 里自己填） ——
        CreateInteractable(root.transform, "Interactable_Object_Demo（靠近按E）",
            new Vector3(s.x + 29f, groundY + 1f, s.z), 5f);

        // —— ④ 重生区域演示：走进去 → 渐黑 → 左上角文字 → 传送回 RespawnPoint_Demo ——
        RespawnZone zone = CreateVolume<RespawnZone>(root.transform, "RespawnZone_Demo（触碰→黑屏→回重生点）",
            new Vector3(s.x + 42f, groundY + 2f, s.z), new Vector3(12f, 6f, 8f), new Color(0.9f, 0.2f, 0.2f, 0.5f));

        var zoneSo = new SerializedObject(zone);
        zoneSo.FindProperty("respawnPoint").objectReferenceValue = respawnPoint;
        zoneSo.FindProperty("messageText").stringValue =
            "欢迎来到 GGJ 能量演示区！\n屏幕变黑后你已被传送回重生点。";
        zoneSo.ApplyModifiedPropertiesWithoutUndo();

        Selection.activeGameObject = root;
        Debug.Log($"[GGJLevel1DemoBuilder] 已在 {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name} " +
                  "生成玩法演示物体（根节点可整体删除）。请自行把布局 / 数值 / 文字调成想要的样子。", root);
    }

    // ---------------------------------------------------------------- helpers

    private static Transform CreateEmpty(Transform parent, string name, Vector3 position)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = position;
        return go.transform;
    }

    private static void CreatePickup(Transform parent, string name, Vector3 center, Vector3 size, Color tint)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = center;

        BoxCollider col = go.AddComponent<BoxCollider>();
        col.isTrigger = true;
        col.size = size;
        AddVisual(go.transform, size, tint);

        go.AddComponent<EnergyPickup>();
    }

    private static T CreateVolume<T>(Transform parent, string name, Vector3 center, Vector3 size, Color tint)
        where T : Component
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = center;

        BoxCollider col = go.AddComponent<BoxCollider>();
        col.isTrigger = true;
        col.size = size;
        AddVisual(go.transform, size, tint);

        return go.AddComponent<T>();
    }

    private static void CreateInteractable(Transform parent, string name, Vector3 position, float interactDistance)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = name;
        cube.transform.SetParent(parent, false);
        cube.transform.position = position;
        cube.transform.localScale = Vector3.one * 1.2f;

        Renderer renderer = cube.GetComponent<Renderer>();
        if (renderer != null)
            renderer.material.color = new Color(0.45f, 0.85f, 1f);

        // 交互距离用一个小触发器线圈示意
        GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ring.name = "InteractRange_Visual";
        ring.transform.SetParent(cube.transform, false);
        ring.transform.localPosition = new Vector3(0f, 0.05f, 0f);
        ring.transform.localScale = new Vector3(interactDistance * 2f, 0.04f, interactDistance * 2f);
        Renderer ringRenderer = ring.GetComponent<Renderer>();
        if (ringRenderer != null)
            ringRenderer.material.color = new Color(0.15f, 1f, 0.6f, 0.6f);
        Object.DestroyImmediate(ring.GetComponent<Collider>());

        InteractableObject interact = cube.AddComponent<InteractableObject>();
        var so = new SerializedObject(interact);
        so.FindProperty("interactDistance").floatValue = interactDistance;
        so.FindProperty("highlightColor").colorValue = new Color(0.15f, 1f, 0.6f, 1f);
        so.FindProperty("outlineWidth").floatValue = 0.08f;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void AddVisual(Transform parent, Vector3 size, Color tint)
    {
        GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
        visual.name = "Visual";
        visual.transform.SetParent(parent, false);
        visual.transform.localScale = size;

        Collider col = visual.GetComponent<Collider>();
        if (col != null)
            Object.DestroyImmediate(col);

        Renderer renderer = visual.GetComponent<Renderer>();
        if (renderer != null)
        {
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null)
                lit = Shader.Find("Standard");

            if (lit == null)
                return;

            Material mat = new Material(lit);

            mat.color = tint;
            // 尽量做成半透明显示区域（在 Demo 里能看清范围即可）
            try
            {
                mat.SetFloat("_Surface", 1f);      // URP: Transparent
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.renderQueue = 3000;
            }
            catch { /* 部分管线字段不同，忽略即可 */ }

            renderer.sharedMaterial = mat;
        }
    }
}
