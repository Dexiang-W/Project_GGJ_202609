#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using StarterAssets;

namespace StarterAssets
{
    /// <summary>
    /// 一键给玩家加上“E/Q 伸手动画”：
    ///   1. 从 RIG_Final5.fbx 里挑伸手 clip；
    ///   2. 复制一份 StarterAssetsThirdPerson.controller 为 PlayerReach.controller；
    ///   3. 新增 ReachArm 层（上半身 AvatarMask）+ Trigger 参数 Reach + 空/播放两个状态：
    ///        · 空 → 伸手：条件 Reach，0.15s 淡入；
    ///        · 伸手 → 空：条件 Speed > 0.15（移动立刻打断，0.18s 顺滑过渡）；
    ///        · 伸手 → 空：ExitTime 0.9（站着不动播完，0.3s 淡出）；
    ///   4. 把 controller 与 PlayerReachAnimation 组件写回 PlayerGroup.prefab。
    /// </summary>
    public static class GGJPlayerReachSetup
    {
        private const string RigPath = "Assets/3_Temp_Dexiang_TA/5_Prefab/RIG_Final5.fbx";
        private const string SourceControllerPath = "Assets/1_Temp_Yufei_Level/External/StarterAssets/ThirdPersonController/Character/Animations/StarterAssetsThirdPerson.controller";
        private const string TargetControllerPath = "Assets/0_File_GGJ2026/8_Animation/PlayerReach.controller";
        private const string MaskPath = "Assets/0_File_GGJ2026/8_Animation/M_ReachUpperBody.mask";
        private const string PlayerPrefabPath = "Assets/0_File_GGJ2026/5_Prefab/PlayerGroup.prefab";

        private const string LayerName = "ReachArm";
        private const string TriggerName = "Reach";
        private const string SpeedParamName = "Speed";
        private const float MoveInterruptSpeed = 0.15f;

        [MenuItem("GGJ2026/设置玩家伸手动画（E/Q）")]
        public static void Setup()
        {
            AnimationClip clip = PickReachClip();
            if (clip == null)
            {
                Debug.LogError("[伸手动画] 没在 " + RigPath + " 里找到 AnimationClip。");
                return;
            }

            AnimatorController controller = EnsureController();
            AvatarMask mask = EnsureMask();
            EnsureReachLayer(controller, clip, mask);

            int prefabCount = ApplyToPrefab(controller, clip);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[伸手动画] clip = " + clip.name + " (" + clip.length.ToString("F2") + "s, loop=" +
                      clip.isLooping + ")；controller = " + TargetControllerPath + "；prefab 处理 " + prefabCount + " 个 Animator。\n" +
                      "移动打断阈值 Speed > " + MoveInterruptSpeed + "（可在 PlayerReach.controller 的过渡里调）。");
        }

        [MenuItem("GGJ2026/伸手动画：同步到当前打开的场景")]
        public static void SyncOpenScenes()
        {
            AnimationClip clip = PickReachClip();
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(TargetControllerPath);
            if (clip == null || controller == null)
            {
                Debug.LogError("[伸手动画] 请先执行一次 “GGJ2026/设置玩家伸手动画（E/Q）”。");
                return;
            }

            int changed = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Animator animator in root.GetComponentsInChildren<Animator>(true))
                    {
                        RuntimeAnimatorController current = animator.runtimeAnimatorController;
                        if (current == null) continue;

                        string path = AssetDatabase.GetAssetPath(current);
                        if (path != SourceControllerPath && path != TargetControllerPath) continue;

                        animator.runtimeAnimatorController = controller;
                        AttachComponent(animator.gameObject, clip);
                        EditorSceneManager.MarkSceneDirty(scene);
                        changed++;
                    }
                }
            }

            if (changed > 0) AssetDatabase.SaveAssets();
            Debug.Log("[伸手动画] 场景内同步 " + changed + " 个玩家 Animator（记得保存场景）。");
        }

        // ---------- 资源准备 ----------

        private static AnimationClip PickReachClip()
        {
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(RigPath);
            List<AnimationClip> clips = new List<AnimationClip>();
            foreach (UnityEngine.Object asset in assets)
            {
                AnimationClip clip = asset as AnimationClip;
                if (clip == null) continue;
                if (clip.name.Contains("__preview__")) continue;
                clips.Add(clip);
            }

            if (clips.Count == 0) return null;

            string log = "[伸手动画] RIG_Final5 里的 clip：";
            foreach (AnimationClip clip in clips)
                log += "\n   · " + clip.name + "  (" + clip.length.ToString("F2") + "s, loop=" + clip.isLooping + ")";
            Debug.Log(log);

            string[] keywords = { "reach", "hand", "arm", "point", "stretch", "give", "take", "push", "伸手" };
            foreach (AnimationClip clip in clips)
            {
                string lower = clip.name.ToLower();
                foreach (string key in keywords)
                {
                    if (lower.Contains(key)) return clip;
                }
            }

            // 没命中关键字就取最长的一段（一般伸手动作比默认 take 更长）
            AnimationClip best = clips[0];
            foreach (AnimationClip clip in clips)
            {
                if (clip.length > best.length) best = clip;
            }
            return best;
        }

        private static AnimatorController EnsureController()
        {
            AnimatorController existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(TargetControllerPath);
            if (existing != null) return existing;

            if (!AssetDatabase.CopyAsset(SourceControllerPath, TargetControllerPath))
            {
                Debug.LogError("[伸手动画] 复制 controller 失败：" + SourceControllerPath);
                return null;
            }

            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<AnimatorController>(TargetControllerPath);
        }

        private static AvatarMask EnsureMask()
        {
            AvatarMask mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath);
            if (mask != null) return mask;

            mask = new AvatarMask();
            mask.name = "M_ReachUpperBody";

            foreach (AvatarMaskBodyPart part in Enum.GetValues(typeof(AvatarMaskBodyPart)))
            {
                if (part == AvatarMaskBodyPart.LastBodyPart) continue;
                mask.SetHumanoidBodyPartActive(part, false);
            }

            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Body, true);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Head, true);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftArm, true);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightArm, true);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftFingers, true);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightFingers, true);

            AssetDatabase.CreateAsset(mask, MaskPath);
            AssetDatabase.SaveAssets();
            return mask;
        }

        // ---------- 状态机 ----------

        private static void EnsureReachLayer(AnimatorController controller, AnimationClip clip, AvatarMask mask)
        {
            bool hasTrigger = false;
            foreach (AnimatorControllerParameter parameter in controller.parameters)
            {
                if (parameter.name == TriggerName) hasTrigger = true;
            }
            if (!hasTrigger)
                controller.AddParameter(TriggerName, AnimatorControllerParameterType.Trigger);

            AnimatorControllerLayer[] layers = controller.layers;
            int layerIndex = -1;
            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i].name == LayerName) layerIndex = i;
            }

            if (layerIndex < 0)
            {
                controller.AddLayer(LayerName);
                layers = controller.layers;
                layerIndex = layers.Length - 1;
            }

            AnimatorControllerLayer layer = layers[layerIndex];
            layer.avatarMask = mask;
            layer.blendingMode = AnimatorLayerBlendingMode.Override;
            layer.defaultWeight = 1f;
            layer.iKPass = false;
            layers[layerIndex] = layer;
            controller.layers = layers;

            AnimatorStateMachine stateMachine = layer.stateMachine;

            // 重复执行时先清掉旧状态
            for (int i = stateMachine.states.Length - 1; i >= 0; i--)
                stateMachine.RemoveState(stateMachine.states[i].state);

            AnimatorState empty = stateMachine.AddState("Reach_Empty", new Vector3(300f, 0f, 0f));
            AnimatorState play = stateMachine.AddState("Reach_Play", new Vector3(300f, 90f, 0f));
            play.motion = clip;
            play.speed = 1f;
            stateMachine.defaultState = empty;

            // 1) 移动立刻打断（放在最前面，优先匹配）
            AnimatorStateTransition moveOut = play.AddTransition(empty);
            moveOut.hasExitTime = false;
            moveOut.hasFixedDuration = true;
            moveOut.duration = 0.18f;
            moveOut.AddCondition(AnimatorConditionMode.Greater, MoveInterruptSpeed, SpeedParamName);

            // 2) 站着不动：播完再淡出
            AnimatorStateTransition doneOut = play.AddTransition(empty);
            doneOut.hasExitTime = true;
            doneOut.exitTime = 0.9f;
            doneOut.hasFixedDuration = true;
            doneOut.duration = 0.3f;

            // 3) 按 E/Q 进入
            AnimatorStateTransition into = empty.AddTransition(play);
            into.hasExitTime = false;
            into.hasFixedDuration = true;
            into.duration = 0.15f;
            into.AddCondition(AnimatorConditionMode.If, 0f, TriggerName);

            EditorUtility.SetDirty(controller);
        }

        // ---------- 应用 ----------

        private static int ApplyToPrefab(AnimatorController controller, AnimationClip clip)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
            if (root == null)
            {
                Debug.LogError("[伸手动画] 找不到 " + PlayerPrefabPath);
                return 0;
            }

            int count = 0;
            try
            {
                foreach (Animator animator in root.GetComponentsInChildren<Animator>(true))
                {
                    animator.runtimeAnimatorController = controller;
                    AttachComponent(animator.gameObject, clip);
                    count++;
                }

                PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            return count;
        }

        private static void AttachComponent(GameObject target, AnimationClip clip)
        {
            PlayerReachAnimation component = target.GetComponent<PlayerReachAnimation>();
            if (component == null)
                component = target.AddComponent<PlayerReachAnimation>();

            component.ReachClip = clip;
            component.ReachTriggerName = TriggerName;
            EditorUtility.SetDirty(component);
        }
    }
}
#endif
