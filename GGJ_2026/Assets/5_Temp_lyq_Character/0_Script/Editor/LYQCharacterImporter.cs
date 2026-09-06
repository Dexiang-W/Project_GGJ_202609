// ============================================================================
//  GGJ 新角色导入工具 —— 由 CodeBuddy 生成
//  适用: 5_Temp_lyq_Character/1_Model/lyq_rig_char.fbx (body_part1(1).fbx)
//  用法(在 Unity 里跑):
//    菜单 GGJ/LYQ角色/① 配置Generic并提取动画到新Animation文件夹
//    菜单 GGJ/LYQ角色/② 尝试Humanoid自动映射(替换PlayerGroup用, 供检测)
// ============================================================================
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GGJ.LYQ
{
    public static class LYQCharacterImporter
    {
        const string ModelPath = "Assets/5_Temp_lyq_Character/1_Model/lyq_rig_char.fbx";
        const string AnimDir  = "Assets/5_Temp_lyq_Character/4_Animation/lyq_rig_char";

        // -------------------------------------------------------------
        // ① 按 Generic 导入并把这个 FBX 自带的动画(如 root|rigAction.001)
        //    提取成独立 .anim 文件, 放到新文件夹 4_Animation/lyq_rig_char/
        // -------------------------------------------------------------
        [MenuItem("GGJ/LYQ角色/① 配置Generic并提取动画到新Animation文件夹", priority = 1)]
        public static void ConfigureGenericAndExtract()
        {
            if (!File.Exists(GetProjectPath(ModelPath)))
            {
                EditorUtility.DisplayDialog("LYQ新角色", "找不到模型文件:\n" + ModelPath + "\n\n请先把 fbx 放到该路径后重试。", "OK");
                return;
            }

            var imp = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if (imp == null)
            {
                Debug.LogError("[LYQ] 不是模型资产: " + ModelPath);
                return;
            }

            // 1) 以 Generic 导入(动画才能以可提取的 clip 出现)
            imp.importAnimation = true;
            imp.animationType = ModelImporterAnimationType.Generic;
            imp.SaveAndReimport();
            AssetDatabase.ImportAsset(ModelPath, ImportAssetOptions.ForceUpdate);

            // 2) 确保输出目录存在
            EnsureFolder(AnimDir);

            // 3) 把每个 AnimationClip 复制成独立 .anim
            int count = 0;
            foreach (var clip in AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<AnimationClip>())
            {
                if (clip.name.StartsWith("__preview")) continue;
                if (clip.name.StartsWith("Take")) continue;   // 空的默认 Take

                string safe = SanitizeName(clip.name);
                string dst = AnimDir + "/lyq_" + safe + ".anim";
                if (AssetDatabase.LoadAssetAtPath<AnimationClip>(dst) != null)
                {
                    Debug.Log("[LYQ] 已存在, 跳过: " + dst);
                    continue;
                }

                var copy = UnityEngine.Object.Instantiate(clip);
                copy.name = "lyq_" + safe;
                AssetDatabase.CreateAsset(copy, dst);
                count++;
                Debug.Log("[LYQ] 提取动画 -> " + dst + "  帧数=" + Mathf.RoundToInt(clip.length * clip.frameRate));
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (count == 0)
                Debug.LogWarning("[LYQ] 没有从 FBX 里发现可提取的动画 clip(可能该 Take 无有效关键帧)。");
            else
                EditorUtility.DisplayDialog("LYQ新角色", "已提取 " + count + " 个动画到:\n" + AnimDir, "OK");
        }

        // -------------------------------------------------------------
        // ② 尝试 Humanoid 自动映射 —— 检测新骨架能否被 Unity 自动认成人形
        //    结果会打印到 Console。自动识别不了就说明需要手工映射。
        // -------------------------------------------------------------
        [MenuItem("GGJ/LYQ角色/② 尝试Humanoid自动映射(检测用)", priority = 2)]
        public static void TryHumanoidAuto()
        {
            var imp = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if (imp == null) { EditorUtility.DisplayDialog("LYQ新角色", "找不到: " + ModelPath, "OK"); return; }

            imp.importAnimation = true;
            imp.animationType = ModelImporterAnimationType.Human;
            imp.SaveAndReimport();
            AssetDatabase.ImportAsset(ModelPath, ImportAssetOptions.ForceUpdate);

            var avatar = AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<Avatar>().FirstOrDefault();
            if (avatar == null)
            {
                Debug.LogWarning("[LYQ] 未生成 Avatar —— 该骨架命名无法自动识别为人形(Hips/LeftUpLeg 等标准命名缺失)。");
                return;
            }
            Debug.Log("[LYQ] Humanoid Avatar: isValid=" + avatar.isValid + " isHuman=" + avatar.isHuman
                      + " 骨骼名=" + string.Join(",", avatar.humanDescription.skeleton.Select(s => s.name).Take(12)) + " ...");
        }

        // ==================== helpers ====================

        static string GetProjectPath(string assetPath) =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath.Replace('/', Path.DirectorySeparatorChar));

        static void EnsureFolder(string assetDir)
        {
            string cur = "Assets";
            foreach (var part in assetDir.Substring("Assets/".Length).Split('/'))
            {
                string next = cur + "/" + part;
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(cur, part);
                }
                cur = next;
            }
        }

        static string SanitizeName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            s = s.Replace('|', '_').Replace(':', '_').Replace('/', '_').Replace('\\', '_').Trim();
            return s;
        }
    }
}
