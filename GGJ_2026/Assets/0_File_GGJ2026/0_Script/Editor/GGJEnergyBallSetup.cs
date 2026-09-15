using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 能量球（P_EnergyBall）配置小工具（编辑菜单）：
///   · 选中场景里的 P_EnergyBall（或任何想当能量球的物体）跑一下本菜单，会：
///       1) 自动挂上 EnergyPickup；
///       2) 从模型资源（FBX）里把动画 Clip 找出来填进 Idle Clip —— Play 时循环播放，球就动起来；
///       3) 没有 Trigger 碰撞体时补一个 SphereCollider（按模型包围盒算半径，Is Trigger 已勾）。
///   · 模型里找不到 Clip 也没关系：EnergyPickup 会用“上下浮动 + 自转”兜底，球不会是死的。
///     如果 Blender 里的动画是【程序化 / Geometry Nodes】做的，FBX 导出时记得烘焙成关键帧
///     （Blender 导出面板勾选 Bake Animation / 或把动画烘焙成 Action），否则 FBX 里不会带 Clip。
/// </summary>
public static class GGJEnergyBallSetup
{
    private const string MenuPath = "GGJ2026/能量球 → 把选中物体配成能量球（自动填动画 Clip）";

    [MenuItem(MenuPath)]
    public static void SetupSelected()
    {
        GameObject[] selection = Selection.gameObjects;
        if (selection == null || selection.Length == 0)
        {
            EditorUtility.DisplayDialog("GGJ2026",
                "请先在场景 / Hierarchy 里选中一个或多个 P_EnergyBall（能量球）物体，再跑这个菜单。", "好");
            return;
        }

        int configured = 0;
        int withClip = 0;
        int withCollider = 0;

        foreach (GameObject go in selection)
        {
            if (go == null)
                continue;

            Undo.RegisterCompleteObjectUndo(go, "GGJ 能量球配置");

            EnergyPickup pickup = go.GetComponent<EnergyPickup>();
            if (pickup == null)
                pickup = Undo.AddComponent<EnergyPickup>(go);

            SerializedObject so = new SerializedObject(pickup);

            // 1) 动画 Clip：从模型资源里找
            AnimationClip clip = FindModelClip(go);
            if (clip != null)
            {
                so.FindProperty("idleClip").objectReferenceValue = clip;
                so.FindProperty("playIdleClipOnStart").boolValue = true;
                withClip++;
            }
            else
            {
                // 没有 Clip → 用兜底浮动 / 自转
                so.FindProperty("fallbackIdleMotion").boolValue = true;
            }

            // 2) 吸收表现：飞向玩家 + 缩小到 0
            so.FindProperty("flyToPlayerOnPickup").boolValue = true;

            // 3) 拾取范围：没有 Trigger 就补一个 SphereCollider
            if (!HasTrigger(go))
            {
                SphereCollider sphere = go.GetComponent<SphereCollider>();
                if (sphere == null)
                    sphere = Undo.AddComponent<SphereCollider>(go);

                sphere.isTrigger = true;
                sphere.radius = CalcRadius(go);
                sphere.center = CalcCenter(go, sphere.radius);
                withCollider++;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(go);
            configured++;

            Debug.Log(clip != null
                    ? $"[GGJEnergyBallSetup] {go.name}：已挂 EnergyPickup，动画 Clip = “{clip.name}”，Play 时会循环播放；被吸收时会飞向玩家并缩小消失。"
                    : $"[GGJEnergyBallSetup] {go.name}：已挂 EnergyPickup，但这个模型里没有找到动画 Clip，已改用“浮动 + 自转”兜底。" +
                      "（若你想要 Blender 里那段程序化动画：Blender 导出 FBX 时把动画烘焙成关键帧 Action 再导）",
                go);
        }

        EditorUtility.DisplayDialog("GGJ2026",
            $"已配置 {configured} 个能量球：\n" +
            $"· 自动填到模型动画 Clip：{withClip} 个\n" +
            $"· 补了 Trigger 碰撞体：{withCollider} 个\n" +
            "（剩下的：Walk 过去碰一下就会被吸收，飞向玩家并缩小到 0 消失，能量 +1）", "好");
    }

    /// <summary>在物体对应的模型资源（FBX）里找第一个可用的 AnimationClip。</summary>
    private static AnimationClip FindModelClip(GameObject go)
    {
        string path = FindModelPath(go);
        if (string.IsNullOrEmpty(path))
            return null;

        List<AnimationClip> clips = new List<AnimationClip>();
        foreach (Object asset in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
        {
            AnimationClip clip = asset as AnimationClip;
            if (clip == null)
                continue;

            // 跳过 Unity 内部用的预览片段
            if (clip.name.StartsWith("__"))
                continue;

            clips.Add(clip);
        }

        if (clips.Count == 0)
            return null;

        // 优先挑名字里带能量球常见关键词的
        foreach (AnimationClip clip in clips)
        {
            string lower = clip.name.ToLower();
            if (lower.Contains("energy") || lower.Contains("ball") || lower.Contains("idle") || lower.Contains("spin"))
                return clip;
        }

        return clips[0];
    }

    private static string FindModelPath(GameObject go)
    {
        // 场景里的模型实例 → 找到它来源的 FBX
        Object source = PrefabUtility.GetCorrespondingObjectFromSource(go);
        if (source == null)
            source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);

        string path = source != null ? AssetDatabase.GetAssetPath(source) : null;
        if (!string.IsNullOrEmpty(path))
            return path;

        return PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
    }

    private static bool HasTrigger(GameObject go)
    {
        Collider[] colliders = go.GetComponentsInChildren<Collider>(true);
        foreach (Collider c in colliders)
        {
            if (c != null && c.isTrigger)
                return true;
        }
        return false;
    }

    /// <summary>按所有 Renderer 的包围盒算一个合适的球半径（抵消物体自身缩放）。</summary>
    private static float CalcRadius(GameObject go)
    {
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
            return 0.5f;

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);

        float scale = Mathf.Max(Mathf.Abs(go.transform.lossyScale.x),
            Mathf.Max(Mathf.Abs(go.transform.lossyScale.y), Mathf.Abs(go.transform.lossyScale.z)));
        if (scale < 0.0001f)
            scale = 1f;

        Vector3 extents = bounds.extents;
        float maxExtent = Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z));
        return Mathf.Max(0.05f, maxExtent / scale * 1.15f);
    }

    private static Vector3 CalcCenter(GameObject go, float radius)
    {
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
            return Vector3.zero;

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);

        Vector3 localCenter = go.transform.InverseTransformPoint(bounds.center);

        // 半径已经按“最大边”算过，中心稍微往物体中心收一点即可
        return localCenter;
    }
}
