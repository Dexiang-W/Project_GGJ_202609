using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// “能量植物（充能-回收）”摆放小工具（编辑菜单）：
///   · 场景里已有的“能量球”通常就是挂 InteractableObject、OnInteract 里接了
///     Animator.SetTrigger("IsRise") 的物体。
///   · 选中它们跑一下本菜单，即可开启能量植物模式，并把 OnInteract 里触发的植物
///     Animator 自动填进 plantAnimators（Q 回收时会把这些植物退回初始形态）。
///   · 剩下唯一要手动做的一步：把“能量球”本体视觉物体拖进 Hidden When Charged，
///     这样 E 充能后球会消失、Q 回收后会重新出现。
/// </summary>
public static class GGJEnergyPlantTools
{
    [MenuItem("GGJ2026/能量植物 → 把选中物体升级为“充能-回收”能量球（自动填植物 Animator）")]
    public static void UpgradeSelectedToEnergyPlant()
    {
        List<GameObject> selected = new List<GameObject>(Selection.gameObjects);
        if (selected.Count == 0)
        {
            EditorUtility.DisplayDialog("GGJ2026",
                "请先在场景/Hierarchy 里选中一个或多个挂有 InteractableObject 的“能量球”物体。", "好");
            return;
        }

        int upgraded = 0;
        int withAnimator = 0;
        foreach (GameObject go in selected)
        {
            InteractableObject io = go.GetComponent<InteractableObject>();
            if (io == null)
                continue;

            SerializedObject so = new SerializedObject(io);
            List<Animator> found = CollectAnimatorsFromOnInteract(so);

            so.FindProperty("energyPlantMode").boolValue = true;
            SerializedProperty plantAnimatorsProp = so.FindProperty("plantAnimators");
            plantAnimatorsProp.arraySize = found.Count;
            for (int i = 0; i < found.Count; i++)
                plantAnimatorsProp.GetArrayElementAtIndex(i).objectReferenceValue = found[i];
            so.ApplyModifiedPropertiesWithoutUndo();

            upgraded++;
            withAnimator += found.Count;
            Debug.Log($"[GGJEnergyPlantTools] {go.name} 已开启能量植物模式，" +
                      (found.Count > 0
                          ? $"自动填入 {found.Count} 个植物 Animator（Q 回收时会把它们退回初始形态）。\n"
                          : "但 OnInteract 里没有找到 Animator 目标，请在 Plant Animators 里手动拖入被它催长的植物。\n") +
                      "还差一步：把“能量球”本体视觉物体拖进 Hidden When Charged（充能后它才会消失）。", go);
        }

        if (upgraded == 0)
        {
            EditorUtility.DisplayDialog("GGJ2026",
                "选中的物体里没有挂 InteractableObject，请检查选择。", "好");
            return;
        }

        EditorUtility.DisplayDialog("GGJ2026",
            $"已升级 {upgraded} 个物体为“充能-回收”能量球（自动填入 {withAnimator} 个植物 Animator）。\n\n" +
            "请在 Inspector 里完成最后一步：\n" +
            "把能量球的视觉物体拖到 “Hidden When Charged”（E 充能后球消失）。\n\n" +
            "玩法：靠近按 E = 消耗 1 格能量（球消失、植物生长）；\n" +
            "已充能时靠近（描边变黄）按 Q = 收回 1 格能量（球重现、植物退回）。",
            "好");
    }

    /// <summary>从 InteractableObject.OnInteract 的持久事件里找出触发过的 Animator（SetTrigger/Play/Set… 都算）。</summary>
    private static List<Animator> CollectAnimatorsFromOnInteract(SerializedObject so)
    {
        List<Animator> result = new List<Animator>();
        if (so == null)
            return result;

        try
        {
            SerializedProperty onInteractProp = so.FindProperty("OnInteract");
            if (onInteractProp == null)
                return result;

            SerializedProperty persistentCalls = onInteractProp.FindPropertyRelative("m_PersistentCalls");
            if (persistentCalls == null)
                return result;

            SerializedProperty calls = persistentCalls.FindPropertyRelative("m_Calls");
            if (calls == null || calls.arraySize == 0)
                return result;

            for (int i = 0; i < calls.arraySize; i++)
            {
                SerializedProperty call = calls.GetArrayElementAtIndex(i);
                if (call == null)
                    continue;

                SerializedProperty targetProp = call.FindPropertyRelative("m_Target");
                SerializedProperty methodProp = call.FindPropertyRelative("m_MethodName");
                if (targetProp == null)
                    continue;

                UnityEngine.Object target = targetProp.objectReferenceValue;
                Animator animator = null;
                if (target is Component component)
                    animator = component.GetComponent<Animator>();
                else if (target is GameObject go)
                    animator = go.GetComponent<Animator>();

                if (animator == null)
                    continue;

                string method = methodProp != null ? methodProp.stringValue : string.Empty;
                if (method.Contains("Trigger") || method.Contains("Play") || method.Contains("Set"))
                {
                    if (!result.Contains(animator))
                        result.Add(animator);
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[GGJEnergyPlantTools] 读取 OnInteract 事件目标失败：{ex.Message}");
        }

        return result;
    }
}
