#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// 蘑菇踩踏动画一键配置工具。
///
/// 【为什么之前“拖了 clip 却没有反应”】
///   场景里的 P_InteractionMogu_BIg 用的是静态模型 P_InteractionMogu_BIg.obj —— 没有骨骼、没有蒙皮。
///   而 Action_mogu.fbx 是“6 根骨骼 + 蒙皮网格 + 动画”的完整蘑菇模型。
///   骨骼动画（AnimationClip.SampleAnimation 与 Animator 状态机都一样）是按 clip 里的节点路径去写 transform 的，
///   静态 .obj 里根本不存在这些骨骼节点 → 静默失效。
///   所以“加 Animator / 状态机”并不能让静态模型动起来，前提是蘑菇必须使用带骨骼的模型。
///
/// 【本工具做什么】
///   1. 用 Action_mogu.fbx 里的 AnimationClip 生成/复用 AnimatorController（Idle → Squash，播完自动回 Idle）；
///   2. 给蘑菇挂 Animator 并指定该 Controller；
///   3. 回填 BouncePad 的播放参数（squashTarget / Animator 状态名），并关掉程序化形变避免两者打架。
///
/// 【用法】
///   在 Hierarchy 里选中蘑菇（或它的 BouncePad）后执行菜单；
///   不选中任何物体时，会处理当前场景里所有 BouncePad。
/// </summary>
public static class MoguSquashSetup
{
    private const string ModelPath = "Assets/3_Temp_Dexiang_TA/1_Model/Action_mogu.fbx";
    private const string ControllerPath = "Assets/0_File_GGJ2026/8_Animation/AC_InteractionMoguSquash.controller";
    private const string StaticModelNamePrefix = "P_InteractionMogu_BIg";

    private const string IdleStateName = "Idle";
    private const string SquashStateName = "Squash";

    [MenuItem("Tools/GGJ/蘑菇踩踏动画/① 只配置状态机（模型已经带骨骼）", false, 10)]
    private static void SetupAnimatorOnly()
    {
        Run(false);
    }

    [MenuItem("Tools/GGJ/蘑菇踩踏动画/② 添加 Action_mogu 带动画的模型并配置（推荐）", false, 11)]
    private static void SetupWithAnimatedModel()
    {
        Run(true);
    }

    [MenuItem("Tools/GGJ/蘑菇踩踏动画/③ 全部改回“程序化踩踏形变”（不依赖骨骼）", false, 12)]
    private static void UseProceduralDeform()
    {
        List<BouncePad> pads = CollectTargets();
        if (pads.Count == 0)
        {
            Debug.LogWarning("[蘑菇动画] 当前场景里没有找到 BouncePad。");
            return;
        }

        foreach (BouncePad pad in pads)
        {
            SerializedObject so = new SerializedObject(pad);
            SetBool(so, "useAnimatorState", false);
            SetBool(so, "enableSquashDeform", true);
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(pad);
        }

        Debug.Log($"[蘑菇动画] 已把 {pads.Count} 个蘑菇切回程序化踩踏形变（静态模型也能被踩扁）。");
    }

    // ------------------------------------------------------------ 主流程

    private static void Run(bool addAnimatedModel)
    {
        AnimationClip clip = FindClip();
        if (clip == null)
        {
            Debug.LogError($"[蘑菇动画] 在 {ModelPath} 里没有找到 AnimationClip，请先确认该 FBX 的 Import Settings → Animation 已导入动画。");
            return;
        }

        AnimatorController controller = EnsureController(clip);

        List<BouncePad> pads = CollectTargets();
        if (pads.Count == 0)
        {
            Debug.LogWarning("[蘑菇动画] 当前场景里没有找到 BouncePad。");
            return;
        }

        int configured = 0;
        foreach (BouncePad pad in pads)
        {
            if (Configure(pad, controller, addAnimatedModel))
                configured++;
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[蘑菇动画] 完成：{configured}/{pads.Count} 个蘑菇已配置动画，Controller = {ControllerPath}。");
    }

    private static bool Configure(BouncePad pad, AnimatorController controller, bool addAnimatedModel)
    {
        Transform mushroomRoot = pad.transform.parent != null ? pad.transform.parent : pad.transform;

        Transform animTarget = FindSkinnedRoot(mushroomRoot);
        if (animTarget == null && addAnimatedModel)
            animTarget = AddAnimatedModel(mushroomRoot);

        if (animTarget == null)
        {
            Debug.LogWarning($"[蘑菇动画] 「{mushroomRoot.name}」下没有带骨骼的模型，已跳过（可改用菜单 ② 自动添加 Action_mogu.fbx）。", mushroomRoot);
            return false;
        }

        if (addAnimatedModel)
            HideStaticModels(mushroomRoot, animTarget);

        // Animator 必须和带动画的模型实例在同一层（clip 里的骨骼路径相对它解析）
        Animator animator = animTarget.GetComponent<Animator>();
        if (animator == null)
            animator = Undo.AddComponent<Animator>(animTarget.gameObject);

        Undo.RecordObject(animator, "配置蘑菇 Animator");
        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        // 回填 BouncePad（字段是 private + SerializeField，用 SerializedObject 写）
        SerializedObject so = new SerializedObject(pad);
        SerializedProperty targetProp = so.FindProperty("squashTarget");
        if (targetProp != null)
            targetProp.objectReferenceValue = animTarget;
        SetBool(so, "useAnimatorState", true);
        SetString(so, "animatorStateName", SquashStateName);
        // 用美术动画时关掉程序化形变，避免两个系统同时改缩放
        SetBool(so, "enableSquashDeform", false);
        so.ApplyModifiedProperties();

        EditorUtility.SetDirty(pad);
        return true;
    }

    private static Transform AddAnimatedModel(Transform mushroomRoot)
    {
        GameObject modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (modelAsset == null)
        {
            Debug.LogError($"[蘑菇动画] 找不到模型：{ModelPath}");
            return null;
        }

        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset, mushroomRoot);
        if (instance == null)
            return null;

        Undo.RegisterCreatedObjectUndo(instance, "添加带动画的蘑菇模型");
        instance.name = "Action_mogu (Animated)";
        instance.transform.localPosition = Vector3.zero;
        instance.transform.localRotation = Quaternion.identity;
        instance.transform.localScale = Vector3.one;

        Debug.Log($"[蘑菇动画] 已在「{mushroomRoot.name}」下添加 {instance.name}；" +
                  "若它和原静态模型的大小/朝向不一致，请调整该实例的 Scale / Rotation 对齐。", instance);
        return instance.transform;
    }

    /// <summary>隐藏蘑菇下原来的静态模型（.obj 实例），避免和带动画的模型重叠穿模。</summary>
    private static void HideStaticModels(Transform mushroomRoot, Transform animatedRoot)
    {
        foreach (Transform child in mushroomRoot)
        {
            if (child == animatedRoot || !child.name.StartsWith(StaticModelNamePrefix))
                continue;

            foreach (MeshRenderer renderer in child.GetComponentsInChildren<MeshRenderer>(true))
            {
                Undo.RecordObject(renderer, "隐藏静态蘑菇模型");
                renderer.enabled = false;
            }
        }
    }

    /// <summary>找出蘑菇下“带蒙皮网格的那一层”的顶层节点，也就是带动画的模型实例根。</summary>
    private static Transform FindSkinnedRoot(Transform root)
    {
        SkinnedMeshRenderer skinned = root.GetComponentInChildren<SkinnedMeshRenderer>(true);
        if (skinned == null)
            return null;

        Transform t = skinned.transform;
        while (t.parent != null && t.parent != root)
            t = t.parent;
        return t;
    }

    private static List<BouncePad> CollectTargets()
    {
        List<BouncePad> result = new List<BouncePad>();

        GameObject[] selection = Selection.gameObjects;
        if (selection != null && selection.Length > 0)
        {
            foreach (GameObject go in selection)
            {
                BouncePad self = go.GetComponent<BouncePad>();
                if (self != null && !result.Contains(self))
                    result.Add(self);

                foreach (BouncePad child in go.GetComponentsInChildren<BouncePad>(true))
                {
                    if (!result.Contains(child))
                        result.Add(child);
                }
            }
        }

        if (result.Count == 0)
        {
            foreach (BouncePad pad in UnityEngine.Object.FindObjectsByType<BouncePad>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                result.Add(pad);
        }

        return result;
    }

    // ------------------------------------------------------------ 动画资产

    private static AnimationClip FindClip()
    {
        foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(ModelPath))
        {
            if (asset is AnimationClip clip && !clip.name.StartsWith("__preview__"))
                return clip;
        }
        return null;
    }

    private static AnimatorController EnsureController(AnimationClip clip)
    {
        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (controller == null)
            controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;

        // Idle：空状态，作为默认状态（蘑菇平时保持绑定姿势）
        AnimatorState idle = FindState(stateMachine, IdleStateName);
        if (idle == null)
            idle = stateMachine.AddState(IdleStateName);
        stateMachine.defaultState = idle;

        // Squash：踩踏动画本体
        AnimatorState squash = FindState(stateMachine, SquashStateName);
        if (squash == null)
            squash = stateMachine.AddState(SquashStateName);
        squash.motion = clip;
        squash.speed = 1f;

        // 播完自动回到 Idle（exit time 1）
        EnsureTransition(squash, idle);

        EditorUtility.SetDirty(controller);
        return controller;
    }

    private static AnimatorState FindState(AnimatorStateMachine stateMachine, string stateName)
    {
        foreach (ChildAnimatorState child in stateMachine.states)
        {
            if (child.state.name == stateName)
                return child.state;
        }
        return null;
    }

    private static void EnsureTransition(AnimatorState from, AnimatorState to)
    {
        foreach (AnimatorStateTransition transition in from.transitions)
        {
            if (transition.destinationState == to)
                return;
        }

        AnimatorStateTransition newTransition = from.AddTransition(to);
        newTransition.hasExitTime = true;
        newTransition.exitTime = 1f;
        newTransition.duration = 0.1f;
        newTransition.hasFixedDuration = true;
    }

    // ------------------------------------------------------------ 小工具

    private static void SetBool(SerializedObject so, string propertyName, bool value)
    {
        SerializedProperty property = so.FindProperty(propertyName);
        if (property != null)
            property.boolValue = value;
    }

    private static void SetString(SerializedObject so, string propertyName, string value)
    {
        SerializedProperty property = so.FindProperty(propertyName);
        if (property != null)
            property.stringValue = value;
    }
}
#endif
