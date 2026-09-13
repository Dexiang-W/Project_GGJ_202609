using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 编辑器辅助：根据 Begin_Menu 里的原画片自动生成“标题焦点中心点”，
/// 并一键填入 GameFlowController 的 Title Focus Points。
///
/// 菜单：
/// - GGJ2026/标题/根据原画片生成焦点          → 每张原画 1 个焦点（原画中心）
/// - GGJ2026/标题/标题焦点工具（左中右）      → 可指定每张原画生成几个焦点，在切片中心左右等距铺开
///
/// 每张原画生成多个焦点的意义：玩家点「开始」后只会跑向最近的那个焦点，
/// 所以焦点越密，跑动距离越短，不会再出现“只停在切片正中、要跑好久”的情况。
/// </summary>
public static class GGJTitleFlowTools
{
    private const string FocusNamePrefix = "TitleFocusPoint_";

    /// <summary>每张原画只生成中心 1 个焦点（保留最初的行为）。</summary>
    [MenuItem("GGJ2026/标题/根据原画片生成焦点")]
    private static void GenerateCenterOnly()
    {
        Generate(1, -1f);
    }

    /// <summary>打开带参数的焦点生成工具（可生成左 / 中 / 右多个焦点）。</summary>
    [MenuItem("GGJ2026/标题/标题焦点工具（左中右）")]
    private static void OpenToolWindow()
    {
        GGJTitleFocusToolWindow window = EditorWindow.GetWindow<GGJTitleFocusToolWindow>("标题焦点工具");
        window.minSize = new Vector2(360f, 200f);
        window.Show();
    }

    /// <summary>
    /// 生成 / 更新标题焦点，并填入 GameFlowController。
    /// </summary>
    /// <param name="pointsPerSlice">每张原画生成几个焦点（1 = 只有中心，3 = 左中右）。</param>
    /// <param name="manualSpacing">手动指定相邻焦点的间距（米）；&lt;= 0 表示按原画片间距自动均分。</param>
    public static void Generate(int pointsPerSlice, float manualSpacing)
    {
        pointsPerSlice = Mathf.Clamp(pointsPerSlice, 1, 9);

        List<Transform> slices = FindSlices();
        if (slices.Count == 0)
        {
            Debug.LogWarning("[GGJTitleFlowTools] 场景里没有找到竖立的原画片（Plane），未生成焦点。");
            return;
        }

        // 自动模式：相邻焦点间距 = 原画片间距 / 每片数量，
        // 这样整条标题带上所有焦点是等距的（例：每片 3 个 → 间距变成原来的 1/3）。
        float spacing = manualSpacing > 0f
            ? manualSpacing
            : ComputeSliceSpacing(slices) / pointsPerSlice;

        Dictionary<string, GameObject> existing = CollectExistingFocusPoints();

        List<Transform> points = new List<Transform>(slices.Count * pointsPerSlice);
        int index = 0;
        for (int s = 0; s < slices.Count; s++)
        {
            Vector3 center = slices[s].position;
            for (int k = 0; k < pointsPerSlice; k++)
            {
                // 以原画中心为轴对称铺开：k=0 最左，最后一个最右
                float offset = (k - (pointsPerSlice - 1) * 0.5f) * spacing;
                index++;

                string goName = FocusNamePrefix + index;
                if (!existing.TryGetValue(goName, out GameObject go) || go == null)
                {
                    go = new GameObject(goName);
                    Undo.RegisterCreatedObjectUndo(go, "Create Title Focus Point");
                }
                else
                {
                    Undo.RecordObject(go.transform, "Move Title Focus Point");
                    Undo.RecordObject(go, "Activate Title Focus Point");
                    go.SetActive(true);
                }

                // 焦点只用 x 决定跑停位置；y / z 跟随原画片，方便在 Scene 里对齐查看
                go.transform.position = new Vector3(center.x + offset, center.y, center.z);
                go.transform.rotation = Quaternion.identity;
                points.Add(go.transform);
            }
        }

        // 上一次生成得更多时留下的多余焦点：停用（而不是删除），
        // 避免按名字回退查找 TitleFocusPoint_1/2/... 时把旧点当成有效焦点。
        int disabled = 0;
        foreach (KeyValuePair<string, GameObject> kv in existing)
        {
            if (kv.Value == null)
                continue;

            string tail = kv.Key.Substring(FocusNamePrefix.Length);
            if (int.TryParse(tail, out int n) && n > index && kv.Value.activeSelf)
            {
                Undo.RecordObject(kv.Value, "Disable Title Focus Point");
                kv.Value.SetActive(false);
                disabled++;
            }
        }

        AssignToController(points);

        Debug.Log($"[GGJTitleFlowTools] 已生成 {points.Count} 个标题焦点" +
                  $"（{slices.Count} 张原画 × 每张 {pointsPerSlice} 个，间距 {spacing:F2} 米）并填入 GameFlowController。" +
                  (disabled > 0 ? $" 另有 {disabled} 个多余旧焦点已停用。" : string.Empty));
    }

    /// <summary>删除场景里所有由本工具生成的标题焦点。</summary>
    public static void ClearFocusPoints()
    {
        List<GameObject> targets = CollectExistingFocusPoints().Values.Where(g => g != null).ToList();
        if (targets.Count == 0)
        {
            Debug.Log("[GGJTitleFlowTools] 场景里没有标题焦点可清除。");
            return;
        }

        if (!EditorUtility.DisplayDialog("清除标题焦点",
                $"确定删除场景里的 {targets.Count} 个 {FocusNamePrefix}* 吗？（可以 Ctrl+Z 撤销）", "删除", "取消"))
            return;

        foreach (GameObject go in targets)
            Undo.DestroyObjectImmediate(go);

        AssignToController(new List<Transform>());

        Debug.Log($"[GGJTitleFlowTools] 已删除 {targets.Count} 个标题焦点。");
    }

    /// <summary>收集场景中竖立的原画片（排除地面等躺平的物体、未激活的对象），按 x 从左到右排序。</summary>
    private static List<Transform> FindSlices()
    {
        List<Transform> slices = new List<Transform>();

        Scene scene = SceneManager.GetActiveScene();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>())
            {
                if (t == null)
                    continue;

                if (!t.name.StartsWith("Plane"))
                    continue;

                // 竖立：本地 Y 轴近似世界水平方向（Plane 默认躺平，躺平的是地面）
                bool vertical = Mathf.Abs(Vector3.Dot(t.up, Vector3.up)) < 0.2f;
                if (!vertical)
                    continue;

                slices.Add(t);
            }
        }

        return slices.OrderBy(t => t.position.x).ToList();
    }

    /// <summary>相邻原画片的平均间距（米）；有效值不足时回退到 10 米。</summary>
    private static float ComputeSliceSpacing(List<Transform> slices)
    {
        if (slices.Count < 2)
            return 10f;

        float total = 0f;
        for (int i = 1; i < slices.Count; i++)
            total += slices[i].position.x - slices[i - 1].position.x;

        float average = total / (slices.Count - 1);
        return average > 0.01f ? average : 10f;
    }

    /// <summary>场景根节点里所有名为 TitleFocusPoint_N 的物体（含已停用的）。</summary>
    private static Dictionary<string, GameObject> CollectExistingFocusPoints()
    {
        Dictionary<string, GameObject> map = new Dictionary<string, GameObject>();

        Scene scene = SceneManager.GetActiveScene();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name.StartsWith(FocusNamePrefix))
                map[root.name] = root;
        }

        return map;
    }

    /// <summary>把焦点按从左到右的顺序写进 GameFlowController 的 Title Focus Points。</summary>
    private static void AssignToController(List<Transform> points)
    {
        GameFlowController controller = Object.FindObjectOfType<GameFlowController>();
        if (controller == null)
        {
            Debug.LogWarning("[GGJTitleFlowTools] 场景里没有 GameFlowController，已生成焦点但未被引用。" +
                             "请手动把 TitleFocusPoint_1 ~ 拖到 GameFlow/Title Focus Points 里。");
            return;
        }

        // 保证顺序从左到右：GameFlowController 依赖这个顺序挑“下一个焦点”
        List<Transform> ordered = points.Where(p => p != null).OrderBy(p => p.position.x).ToList();

        Undo.RecordObject(controller, "Assign Title Focus Points");
        SerializedObject so = new SerializedObject(controller);
        SerializedProperty sp = so.FindProperty("titleFocusPoints");
        sp.arraySize = ordered.Count;
        for (int i = 0; i < ordered.Count; i++)
            sp.GetArrayElementAtIndex(i).objectReferenceValue = ordered[i];
        so.ApplyModifiedProperties();
    }
}

/// <summary>标题焦点生成工具窗口：可指定每张原画生成几个焦点、间距多少，一键生成。</summary>
public class GGJTitleFocusToolWindow : EditorWindow
{
    private int pointsPerSlice = 3;
    private bool useManualSpacing;
    private float manualSpacing = 4f;

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "按场景里竖立的原画片（Plane）生成焦点空物体，并自动填入 GameFlowController 的 Title Focus Points。\n\n" +
            "每张原画生成多个焦点时，会以该片中心为轴对称铺开；玩家点「开始」后只跑向最近的那个焦点，" +
            "所以焦点越密、跑动越短。\n\n" +
            "想要“左 / 中 / 右各一个”，把「每片焦点数量」设为 3 即可。",
            MessageType.Info);

        EditorGUILayout.Space();
        pointsPerSlice = EditorGUILayout.IntSlider("每片焦点数量", pointsPerSlice, 1, 9);

        useManualSpacing = EditorGUILayout.ToggleLeft("手动指定焦点间距（米）", useManualSpacing);
        using (new EditorGUI.DisabledScope(!useManualSpacing))
            manualSpacing = Mathf.Max(0.01f, EditorGUILayout.FloatField("间距", manualSpacing));

        if (!useManualSpacing)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("自动：原画片间距 ÷ 每片数量", EditorStyles.miniLabel);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField(string.Empty, GUI.skin.horizontalSlider);

        if (GUILayout.Button("生成 / 更新焦点，并填入 GameFlowController", GUILayout.Height(30f)))
            GGJTitleFlowTools.Generate(pointsPerSlice, useManualSpacing ? manualSpacing : -1f);

        if (GUILayout.Button("清除场景里的 TitleFocusPoint_*"))
            GGJTitleFlowTools.ClearFocusPoints();
    }
}
