using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 书本 / 纸张系统的场景接入工具（编辑器菜单，不参与打包）：
/// 一键给场景里的 P_Book、P_Paper 挂上“靠近描边高亮 + 按 E 交互”的 InteractableObject
/// 与各自的业务脚本，省得一个个手点。
///
/// 菜单：
///  · GGJ2026/书本系统 → 当前场景接入 P_Book / P_Paper（自动加交互）
///  · GGJ2026/书本系统 → 把选中物体设为可收集纸张（P_Paper）
///  · GGJ2026/书本系统 → 把选中物体设为书本（P_Book）
/// </summary>
public static class GGJBookSystemSetup
{
    private const string MenuRoot = "GGJ2026/书本系统 → ";

    [MenuItem(MenuRoot + "当前场景接入 P_Book / P_Paper（自动加交互）")]
    public static void SetupInActiveScene()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid())
        {
            EditorUtility.DisplayDialog("书本系统", "当前没有打开有效场景。", "好");
            return;
        }

        int books = 0;
        int papers = 0;
        var warnings = new List<string>();

        foreach (GameObject root in scene.GetRootGameObjects())
            Walk(root.transform, ref books, ref papers, warnings);

        if (books == 0 && papers == 0)
        {
            EditorUtility.DisplayDialog("书本系统",
                "当前场景里没找到名字以 P_Book / P_Paper 开头的物体。\n\n" +
                "也可以直接选中物体后用下面两个菜单：\n" +
                "· 把选中物体设为可收集纸张（P_Paper）\n· 把选中物体设为书本（P_Book）",
                "好");
            return;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        var sb = new StringBuilder();
        sb.AppendLine($"书本（P_Book）：{books} 个");
        sb.AppendLine($"纸张（P_Paper）：{papers} 个");
        sb.AppendLine();
        sb.AppendLine("已自动挂上 InteractableObject（靠近描边高亮 + 按 E）：");
        sb.AppendLine("· P_Book → GGJBookPickup：按 E 拾取，之后按 B 翻书；");
        sb.AppendLine("· P_Paper → GGJPaperNote：按 E 弹出纸张阅读界面并收进书本。");
        sb.AppendLine();
        sb.AppendLine("接下来在 Inspector 上填内容即可：");
        sb.AppendLine("· 每张 P_Paper 的 GGJPaperNote：标题 / 正文 / 纸张贴图（背景插槽）；");
        sb.AppendLine("· P_Book 的 GGJBookPickup：书本标题、拾取提示文字。");

        if (warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("注意：");
            foreach (string w in warnings)
                sb.AppendLine("· " + w);
        }

        Debug.Log($"[GGJBookSystemSetup] {scene.name}: 书本 {books} 个，纸张 {papers} 个。\n{sb}");
        EditorUtility.DisplayDialog("书本系统接入完成", sb.ToString(), "好");
    }

    [MenuItem(MenuRoot + "把选中物体设为可收集纸张（P_Paper）")]
    public static void SetSelectionAsPaper()
    {
        int count = 0;
        foreach (GameObject go in Selection.gameObjects)
        {
            SetupPaper(go, null);
            count++;
        }

        if (count == 0)
        {
            EditorUtility.DisplayDialog("书本系统", "先在 Hierarchy 里选中要当纸张的物体。", "好");
            return;
        }

        MarkSceneDirty();
        Debug.Log($"[GGJBookSystemSetup] 已把 {count} 个选中物体设为可收集纸张（GGJPaperNote）。");
    }

    [MenuItem(MenuRoot + "把选中物体设为书本（P_Book）")]
    public static void SetSelectionAsBook()
    {
        int count = 0;
        foreach (GameObject go in Selection.gameObjects)
        {
            SetupBook(go, null);
            count++;
        }

        if (count == 0)
        {
            EditorUtility.DisplayDialog("书本系统", "先在 Hierarchy 里选中要当书本的物体。", "好");
            return;
        }

        MarkSceneDirty();
        Debug.Log($"[GGJBookSystemSetup] 已把 {count} 个选中物体设为书本（GGJBookPickup）。");
    }

    // ---------------------------------------------------------------- 内部

    private static void Walk(Transform node, ref int books, ref int papers, List<string> warnings)
    {
        if (node == null)
            return;

        GameObject go = node.gameObject;
        string name = go.name;

        if (name.StartsWith("P_Book"))
        {
            SetupBook(go, warnings);
            books++;
        }
        else if (name.StartsWith("P_Paper"))
        {
            SetupPaper(go, warnings);
            papers++;
        }

        for (int i = 0; i < node.childCount; i++)
            Walk(node.GetChild(i), ref books, ref papers, warnings);
    }

    private static void SetupBook(GameObject go, List<string> warnings)
    {
        EnsureInteractable(go);

        GGJBookPickup pickup = go.GetComponent<GGJBookPickup>();
        if (pickup == null)
        {
            pickup = Undo.AddComponent<GGJBookPickup>(go);
            var so = new SerializedObject(pickup);
            SerializedProperty prompt = so.FindProperty("promptText");
            if (prompt != null && string.IsNullOrEmpty(prompt.stringValue))
                prompt.stringValue = "按 E 拾取";
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        CheckRenderer(go, warnings);
        EditorUtility.SetDirty(go);
    }

    private static void SetupPaper(GameObject go, List<string> warnings)
    {
        EnsureInteractable(go);

        GGJPaperNote note = go.GetComponent<GGJPaperNote>();
        if (note == null)
            note = Undo.AddComponent<GGJPaperNote>(go);

        // 纸张 ID 默认用物体名，保证每张纸在书本里只收录一次（可以自己在 Inspector 改）
        var so = new SerializedObject(note);
        SerializedProperty id = so.FindProperty("paperId");
        if (id != null && string.IsNullOrEmpty(id.stringValue))
            id.stringValue = go.name;
        SerializedProperty prompt = so.FindProperty("promptText");
        if (prompt != null && string.IsNullOrEmpty(prompt.stringValue))
            prompt.stringValue = "按 E 查看";
        so.ApplyModifiedPropertiesWithoutUndo();

        CheckRenderer(go, warnings);
        EditorUtility.SetDirty(go);
    }

    private static void EnsureInteractable(GameObject go)
    {
        if (go.GetComponent<InteractableObject>() == null)
            Undo.AddComponent<InteractableObject>(go);
    }

    /// <summary>描边高亮需要网格，没有渲染器的物体给个提示，避免玩家看不到“高亮提醒”。</summary>
    private static void CheckRenderer(GameObject go, List<string> warnings)
    {
        if (warnings == null)
            return;

        if (go.GetComponentInChildren<Renderer>(true) == null)
            warnings.Add($"{go.name} 自身和子物体里都没有 Renderer，靠近时无法描边高亮（建议挂在有网格的物体上）。");
    }

    private static void MarkSceneDirty()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid())
            return;

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }
}
