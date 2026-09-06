using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// GGJ2026 关卡流程小工具（编辑菜单）：
///   · 一键把 6_Scene 下所有关卡加入 Build Settings；
///   · 生成“跨场景过渡 Volume”（LevelTransitionZone）；
///   · 生成“结局交互物体”（InteractableObject + EndingSequencePlayer，事件自动接好）。
/// </summary>
public static class GGJLevelFlowTools
{
    private const string SceneFolder = "Assets/0_File_GGJ2026/6_Scene";

    // ---------------------------------------------------------------- Build Settings

    [MenuItem("GGJ2026/关卡/把 6_Scene 下所有关卡加入 Build Settings", priority = 20)]
    public static void AddAllLevelScenesToBuild()
    {
        string[] guids = AssetDatabase.FindAssets("t:Scene", new[] { SceneFolder });
        if (guids == null || guids.Length == 0)
        {
            EditorUtility.DisplayDialog("GGJ2026", "没在 6_Scene 里找到任何场景。", "好");
            return;
        }

        // 关卡顺序：Begin_Menu → Level1 → Level2 → Level3+4 → Level5 → 其余按名字
        List<(string path, string name)> levelScenes = new List<(string, string)>();
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".unity"))
                continue;
            levelScenes.Add((path, Path.GetFileNameWithoutExtension(path)));
        }

        levelScenes.Sort((a, b) => LevelSortKey(a.name).CompareTo(LevelSortKey(b.name)));

        // 保留原来不属于 6_Scene 的条目
        List<EditorBuildSettingsScene> result = new List<EditorBuildSettingsScene>();
        foreach (EditorBuildSettingsScene existing in EditorBuildSettings.scenes)
        {
            if (existing == null)
                continue;
            if (existing.path != null && !existing.path.StartsWith(SceneFolder))
                result.Add(existing);
        }

        int added = 0;
        foreach ((string path, string _) in levelScenes)
        {
            if (System.Array.Find(result.ToArray(), s => s.path == path) != null)
                continue;
            result.Add(new EditorBuildSettingsScene(path, true));
            added++;
        }

        EditorBuildSettings.scenes = result.ToArray();
        AssetDatabase.SaveAssets();
        Debug.Log($"[GGJ2026] 已把关卡加入 Build Settings（本次新增 {added} 个）。");
    }

    private static int LevelSortKey(string name)
    {
        switch (name)
        {
            case "Begin_Menu": return 0;
            case "Level1": return 1;
            case "Level2": return 2;
            case "Level3+4": return 3;
            case "Level5": return 4;
            default: return 100;
        }
    }

    // ---------------------------------------------------------------- 生成：跨场景过渡 Volume

    [MenuItem("GGJ2026/关卡/生成“进入下一关”过渡 Volume", priority = 30)]
    public static void CreateLevelTransitionZone()
    {
        Transform spawn = FindInActiveScene("SpawnPoint") ?? FindInActiveScene("Spawn");
        Vector3 basePos = spawn != null ? spawn.position : new Vector3(0f, 1f, 0f);
        Vector3 pos = new Vector3(basePos.x + 10f, basePos.y + 2f, basePos.z);

        GameObject root = new GameObject("LevelTransitionZone（玩家进入→切到下一场景出生点）");
        Undo.RegisterCreatedObjectUndo(root, "Create LevelTransitionZone");
        root.transform.position = pos;

        BoxCollider col = root.AddComponent<BoxCollider>();
        col.isTrigger = true;
        col.size = new Vector3(4f, 6f, 4f);

        // 范围由 OnDrawGizmos 线框显示（不占运行时画面），BoxCollider 可随意调
        root.AddComponent<LevelTransitionZone>();

        EditorSceneManager.MarkSceneDirty(root.scene);
        Selection.activeGameObject = root;
        SceneView.FrameLastActiveSceneView();
        Debug.Log("[GGJ2026] 已生成过渡 Volume。\n"
                  + "用法：在 Inspector 里填 destinationSceneName（目标场景，如 Level2）与 spawnObjectName（目标场景出生点名），"
                  + "再拖 BoxCollider 把范围调到你想要的位置。", root);
    }

    // ---------------------------------------------------------------- 生成：结局交互

    [MenuItem("GGJ2026/关卡/生成“结局”可交互物体（字幕+滚动报幕）", priority = 31)]
    public static void CreateEndingInteractable()
    {
        Transform spawn = FindInActiveScene("SpawnPoint") ?? FindInActiveScene("Spawn");
        Vector3 basePos = spawn != null ? spawn.position : new Vector3(0f, 1f, 0f);
        Vector3 pos = new Vector3(basePos.x + 14f, basePos.y + 0.5f, basePos.z);

        GameObject root = new GameObject("Ending_Interactable（靠近按 E → 结局报幕）");
        Undo.RegisterCreatedObjectUndo(root, "Create Ending Interactable");
        root.transform.position = pos;

        BoxCollider col = root.AddComponent<BoxCollider>();
        col.isTrigger = false;
        col.size = new Vector3(1f, 1f, 1f);

        InteractableObject interactable = root.AddComponent<InteractableObject>();
        EndingSequencePlayer sequence = root.AddComponent<EndingSequencePlayer>();

        // 事件自动接线：On Interact → sequence.Play()
        UnityEventTools.AddPersistentListener(interactable.OnInteract, sequence.Play);

        AddCubeVisual(root.transform, Vector3.one * 1f, new Color(1f, 0.45f, 0.25f, 1f), "Visual");
        AddRingMark(root.transform);

        EditorSceneManager.MarkSceneDirty(root.scene);
        Selection.activeGameObject = root;
        SceneView.FrameLastActiveSceneView();
        Debug.Log("[GGJ2026] 已生成结局交互物体，InteractableObject 的 On Interact 已自动接好 EndingSequencePlayer.Play()。\n"
                  + "所有字幕 / 报幕文案、字号、时长都在 EndingSequencePlayer 组件里编辑。", root);
    }

    // ---------------------------------------------------------------- 通用小工具

    private static void AddCubeVisual(Transform parent, Vector3 size, Color color, string childName)
    {
        GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
        visual.name = childName;
        Undo.RegisterCreatedObjectUndo(visual, "Create GGJ2026 Demo Visual");
        visual.transform.SetParent(parent, false);
        visual.transform.localScale = size;

        Renderer renderer = visual.GetComponent<Renderer>();
        if (renderer != null)
        {
            // 用实例材质：半透明状态可随意改，不影响工程里其它材质
            renderer.sharedMaterial = new Material(GetBaseShader());
            renderer.sharedMaterial.color = color;
            if (color.a < 1f)
                renderer.sharedMaterial.renderQueue = 3000;
        }

        Collider c = visual.GetComponent<Collider>();
        if (c != null)
            Object.DestroyImmediate(c);
    }

    private static void AddRingMark(Transform parent)
    {
        // 头顶光环，提示“可以交互”
        GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ring.name = "InteractHintRing";
        Undo.RegisterCreatedObjectUndo(ring, "Create GGJ2026 Demo Visual");
        ring.transform.SetParent(parent, false);
        ring.transform.localPosition = new Vector3(0f, 1.6f, 0f);
        ring.transform.localScale = new Vector3(0.6f, 0.02f, 0.6f);

        Renderer renderer = ring.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = new Material(GetBaseShader());
            renderer.sharedMaterial.color = new Color(1f, 0.85f, 0.3f, 0.9f);
            renderer.sharedMaterial.renderQueue = 3000;
        }

        Collider c = ring.GetComponent<Collider>();
        if (c != null)
            Object.DestroyImmediate(c);
    }

    private static Shader GetBaseShader()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");
        return shader;
    }

    private static Transform FindInActiveScene(string name)
    {
        foreach (GameObject root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
        {
            if (root == null)
                continue;
            if (root.name == name)
                return root.transform;
            Transform found = root.transform.Find(name);
            if (found != null)
                return found;
        }
        return null;
    }
}
