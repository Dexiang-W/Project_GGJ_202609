// ============================================================================
//  GGJ 新角色 -> Humanoid 自动映射 + PlayerGroup 替换工具   (CodeBuddy 生成)
//
//  前提: lyq_rig_char.fbx 已放在 1_Model 下, 先跑菜单 GGJ/LYQ角色/① 让 Unity 导入
//  流程(在 Unity 中):
//   ③ 自动 Humanoid 映射 —— 按网格权重/层级自动把 82 根骨骼映射成 Humanoid,
//      依次尝试多套候选直到 Avatar 有效, 结果写入 fbx 的导入配置(持久)
//   ④ 生成 PlayerGroup_lyq —— 新变体预制体, 删除旧骨架/网格, 挂新模型,
//      沿用 Player 上原来的 Animator 控制器/脚本, 自动按旧角色身高落地对齐
//   ⑤ 生成 PlayerGroup_lyq (保持新模型原始尺寸, 仅落地对齐)
//   ⑥ 打印新 FBX 层级 + 蒙皮权重(调试用)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GGJ.LYQ
{
    public static class LYQHumanoidMapper
    {
        const string ModelPath     = "Assets/5_Temp_lyq_Character/1_Model/lyq_rig_char.fbx";
        const string OldPrefabPath = "Assets/0_File_GGJ2026/5_Prefab/PlayerGroup.prefab";
        const string NewPrefabPath = "Assets/0_File_GGJ2026/5_Prefab/PlayerGroup_lyq.prefab";

        static Transform _rootCache;          // FindBoneEx 的查找根
        static Dictionary<string, float> _weights = new Dictionary<string, float>();

        // ---------------- ③ 自动 Humanoid 映射 ----------------
        [MenuItem("GGJ/LYQ角色/③ 自动Humanoid映射(多候选直至Avatar有效)", priority = 3)]
        public static void AutoMapHumanoid()
        {
            var imp = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if (imp == null) { EditorUtility.DisplayDialog("提示", "找不到: " + ModelPath + "\n请先跑菜单① 让 Unity 导入模型。", "OK"); return; }

            // 先转 Humanoid 导一次: 让 Unity 生成 skeleton 数据(编辑 humanDescription 的前提)
            imp.animationType = ModelImporterAnimationType.Human;
            imp.avatarSetup   = ModelImporterAvatarSetup.CreateFromThisModel;
            imp.SaveAndReimport();

            var modelRoot = AssetDatabase.LoadMainAssetAtPath(ModelPath) as GameObject;
            if (modelRoot == null) { Debug.LogError("[LYQ] 读取模型主资产失败 " + ModelPath); return; }
            _rootCache = modelRoot.transform;
            _weights = CollectBoneWeights(modelRoot.transform);

            var tries = BuildAllCandidates();

            if (tries.Count == 0)
            {
                Debug.LogError("[LYQ] 一个候选映射都构建不出来, 请把 ⑥ 的输出贴回来。\n" + DumpHierarchy(modelRoot.transform));
                return;
            }

            Avatar ok = null;
            string usedDesc = null;
            int attempts = 0;
            const int maxAttempts = 12;   // 避免失败时长时间反复导入
            foreach (var map in tries)
            {
                string desc = Describe(map);
                if (!WriteHumanMapping(imp, map.ToList())) { attempts++; continue; }
                var av = GetAvatar();
                if (av != null && av.isValid && av.isHuman)
                {
                    ok = av; usedDesc = desc;
                    Debug.Log("[LYQ] 成功! Avatar 有效。映射方案: " + desc);
                    break;
                }
                Debug.Log("[LYQ] 该候选无效, 尝试下一套: " + desc);
                if (++attempts >= maxAttempts) break;
            }

            if (ok == null)
            {
                WriteHumanMapping(imp, tries[0].ToList());
                imp.SaveAndReimport();
                Debug.LogWarning("[LYQ] 自动映射所有候选都未通过 Unity 校验。\n已把第一套方案写入配置。" +
                    "\n请在 Project 里选中 lyq_rig_char.fbx -> Inspector -> Rig -> Configure... 微调。" +
                    "\n当前层级:\n" + DumpHierarchy(modelRoot.transform));
            }
            else
            {
                Debug.Log("[LYQ] 最终映射: " + usedDesc + "\n可继续运行 ④/⑤ 生成 PlayerGroup_lyq 预制体。");
            }
        }

        // =================== 候选映射生成 ===================
        static bool HasFingers(Dictionary<string, string> m) =>
            m.ContainsKey("Left Thumb Proximal") || m.ContainsKey("Right Index Proximal");

        static List<Dictionary<string, string>> BuildAllCandidates()
        {
            var list = new List<Dictionary<string, string>>();
            string[] hipsCandidates = { "root.x", "root", "Hips", "Pelvis" };

            // 每个关节的主选/备选 (前缀, 不含左右后缀)
            var armU = PickPair("arm_stretch", "arm_twist");
            var armL = PickPair("forearm_stretch", "forearm_twist");
            var legU = PickPair("thigh_stretch", "thigh_twist");
            var legL = PickPair("leg_stretch", "leg_twist");

            var armPairs = new (string,string)[] { (armU.a, armL.a), (armU.b, armL.b), (armU.a, armL.b), (armU.b, armL.a) };
            var legPairs = new (string,string)[] { (legU.a, legL.a), (legU.b, legL.b), (legU.a, legL.b), (legU.b, legL.a) };

            foreach (var hips in hipsCandidates)
            {
                if (Bone(hips) == null) continue;
                foreach (var a in armPairs)
                    foreach (var l in legPairs)
                    {
                        var mFull = BuildOneMap(hips, a.Item1, a.Item2, l.Item1, l.Item2, true);
                        if (mFull.Count >= 12) list.Add(mFull);
                        var mCore = BuildOneMap(hips, a.Item1, a.Item2, l.Item1, l.Item2, false);
                        if (mCore.Count >= 8) list.Add(mCore);
                    }
            }
            return list;
        }

        static Dictionary<string, string> BuildOneMap(string hips, string armU, string armL,
            string legU, string legL, bool withFingers)
        {
            var m = new Dictionary<string, string>();
            Add(m, "Hips", hips);

            Add(m, "Spine", "spine_01.x");
            Add(m, "Chest", "spine_02.x");
            Add(m, "UpperChest", "spine_03.x");
            Add(m, "Neck", "neck.x");
            Add(m, "Head", "head.x");

            foreach (var s in new[] { "r", "l" })
            {
                string P = s == "r" ? "Right" : "Left";
                Add(m, P + "Shoulder", "shoulder." + s);
                Add(m, P + "UpperArm", Side(armU, s));
                Add(m, P + "LowerArm", Side(armL, s));
                Add(m, P + "Hand", "hand." + s);

                Add(m, P + "UpperLeg", Side(legU, s));
                Add(m, P + "LowerLeg", Side(legL, s));
                Add(m, P + "Foot", "foot." + s);
                Add(m, P + "Toes", "toes_01." + s);
            }

            if (withFingers)
            {
                foreach (var s in new[] { "r", "l" })
                {
                    string P = s == "r" ? "Right" : "Left";
                    AddFinger(m, P, "Thumb",  "thumb",  new[] { "1", "2", "3" }, s);   // 拇指 3 节
                    AddFinger(m, P, "Index",  "index",  new[] { "1", "2", "3" }, s);   // 1_base 视作掌骨, 用 1/2/3
                    AddFinger(m, P, "Middle", "middle", new[] { "1", "2", "3" }, s);
                    AddFinger(m, P, "Ring",   "ring",   new[] { "1", "2", "3" }, s);
                    AddFinger(m, P, "Little", "pinky",  new[] { "1", "2", "3" }, s);
                }
            }
            return m;
        }

        // 侧骨名: 候选前缀可能为 null 时直接用给定全名
        static string Side(string prefix, string side)
        {
            if (prefix == null) return null;
            return prefix.EndsWith("." + side) ? prefix : prefix + "." + side;
        }

        static void AddFinger(Dictionary<string, string> m, string P, string fingerHuman,
            string bonePrefix, string[] segs, string side)
        {
            string[] human = { "Proximal", "Intermediate", "Distal" };
            for (int i = 0; i < segs.Length && i < human.Length; i++)
            {
                string bone = bonePrefix + segs[i] + "." + side;
                if (Bone(bone) != null)
                    m[P + " " + fingerHuman + " " + human[i]] = bone;
            }
        }

        static void Add(Dictionary<string, string> m, string human, string bone)
        {
            if (bone != null && Bone(bone) != null) m[human] = bone;
        }

        static (string a, string b) PickPair(string primary, string secondary)
        {
            bool pa = Bone(primary + ".r") != null || Bone(primary + ".l") != null;
            bool sb = Bone(secondary + ".r") != null || Bone(secondary + ".l") != null;
            if (!pa && !sb) return (null, null);

            // 蒙皮权重大的作为主选
            float wA = W(primary + ".r") + W(primary + ".l");
            float wB = W(secondary + ".r") + W(secondary + ".l");
            if (wA + wB > 0f)
                return wA >= wB ? (primary, secondary) : (secondary, primary);
            return pa ? (primary, secondary) : (secondary, primary);
        }

        static float W(string bone) => _weights.TryGetValue(bone, out var v) ? v : 0f;

        static Transform Bone(string name)
        {
            if (_rootCache == null) return null;
            return FindBone(_rootCache, name);
        }

        static Transform FindBone(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t)
            {
                var r = FindBone(c, name);
                if (r != null) return r;
            }
            return null;
        }

        static Dictionary<string, float> CollectBoneWeights(Transform root)
        {
            var result = new Dictionary<string, float>();
            try
            {
                var smr = root.GetComponentInChildren<SkinnedMeshRenderer>(true);
                if (smr == null || smr.sharedMesh == null) return result;
                var mesh = smr.sharedMesh;
                if (mesh.boneWeights == null || mesh.bindposes == null) return result;
                var bones = smr.bones;
                if (bones == null || bones.Length != mesh.bindposes.Length) return result;

                float[] sums = new float[bones.Length];
                foreach (BoneWeight bw in mesh.boneWeights)
                {
                    if (bw.boneIndex0 >= 0 && bw.boneIndex0 < sums.Length) sums[bw.boneIndex0] += bw.weight0;
                    if (bw.boneIndex1 >= 0 && bw.boneIndex1 < sums.Length) sums[bw.boneIndex1] += bw.weight1;
                    if (bw.boneIndex2 >= 0 && bw.boneIndex2 < sums.Length) sums[bw.boneIndex2] += bw.weight2;
                    if (bw.boneIndex3 >= 0 && bw.boneIndex3 < sums.Length) sums[bw.boneIndex3] += bw.weight3;
                }
                for (int i = 0; i < bones.Length; i++)
                    if (bones[i] != null) result[bones[i].name] = sums[i];
            }
            catch (Exception e) { Debug.Log("[LYQ] 读取蒙皮权重失败(将用命名偏好): " + e.Message); }
            return result;
        }

        // =================== 写入 HumanDescription ===================
        static bool WriteHumanMapping(ModelImporter imp, List<KeyValuePair<string, string>> entries)
        {
            try
            {
                var so = new SerializedObject(imp);
                var humanDesc = so.FindProperty("humanDescription");
                if (humanDesc == null) { Debug.LogError("[LYQ] importer 找不到 humanDescription(Rig 先切 Humanoid 并导入一次)"); return false; }
                var human = humanDesc.FindPropertyRelative("human");
                if (human == null || !human.isArray) { Debug.LogError("[LYQ] humanDescription.human 不是数组"); return false; }

                human.arraySize = entries.Count;
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = human.GetArrayElementAtIndex(i);
                    var hn = e.FindPropertyRelative("humanName");
                    var bn = e.FindPropertyRelative("boneName");
                    if (hn == null || bn == null) continue;
                    hn.stringValue = entries[i].Key;
                    bn.stringValue = entries[i].Value;
                }
                so.ApplyModifiedPropertiesWithoutUndo();
                imp.SaveAndReimport();
                return true;
            }
            catch (Exception ex) { Debug.LogError("[LYQ] 写映射出错: " + ex); return false; }
        }

        static Avatar GetAvatar()
        {
            return AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<Avatar>().FirstOrDefault(a => a.isHuman);
        }

        static string Describe(Dictionary<string, string> m)
        {
            string g(string k) => m.TryGetValue(k, out var v) ? v : "-";
            return string.Join(" | ",
                g("Hips"), g("Spine") + "→" + g("UpperChest"),
                "R上臂=" + g("RightUpperArm"), "R前臂=" + g("RightLowerArm"),
                "R大腿=" + g("RightUpperLeg"), "R小腿=" + g("RightLowerLeg"),
                "手指=" + (m.ContainsKey("Right Index Proximal") ? "开" : "关"));
        }

        // ---------------- ④⑤ 生成 PlayerGroup_lyq ----------------
        [MenuItem("GGJ/LYQ角色/④ 生成PlayerGroup_lyq(按旧角色身高对齐)", priority = 4)]
        public static void BuildVariantScale() => BuildVariant(true);

        [MenuItem("GGJ/LYQ角色/⑤ 生成PlayerGroup_lyq(保持原尺寸,落地对齐)", priority = 5)]
        public static void BuildVariantKeepScale() => BuildVariant(false);

        static void BuildVariant(bool matchOldHeight)
        {
            if (!System.IO.File.Exists(OldPrefabPath)) { EditorUtility.DisplayDialog("提示", "找不到旧预制体: " + OldPrefabPath, "OK"); return; }
            var avatar = GetAvatar();
            if (avatar == null) { EditorUtility.DisplayDialog("提示", "新模型还没有有效 Humanoid Avatar。\n请先运行菜单③。", "OK"); return; }
            var modelRoot = AssetDatabase.LoadMainAssetAtPath(ModelPath) as GameObject;
            if (modelRoot == null) { EditorUtility.DisplayDialog("提示", "读取模型失败", "OK"); return; }

            var contents = PrefabUtility.LoadPrefabContents(OldPrefabPath);
            try
            {
                var player = contents.transform.Find("Player");
                if (player == null) { Debug.LogError("[LYQ] PlayerGroup 里找不到 Player 节点"); return; }

                var oldAnimator = player.GetComponent<Animator>();
                var controller  = oldAnimator != null ? oldAnimator.runtimeAnimatorController : null;
                var oldApplyRM  = oldAnimator != null && oldAnimator.applyRootMotion;

                Bounds oldB = default; bool hasOld = false;
                var oldSmr = player.GetComponentInChildren<SkinnedMeshRenderer>(true);
                if (oldSmr != null && oldSmr.sharedMesh != null)
                {
                    var mm = MeshLocal(oldSmr, oldSmr.sharedMesh);
                    if (mm.HasValue) { oldB = mm.Value; hasOld = true; }
                }

                // 删除旧角色子树(Skeleton / Geometry 及其下一切), 保留 PlayerCameraRoot 等功能节点
                var doomed = new List<GameObject>();
                foreach (Transform ch in player)
                {
                    if (ch.name == "Skeleton" || ch.name == "Geometry")
                        doomed.Add(ch.gameObject);
                    else if (ch.GetComponent<SkinnedMeshRenderer>() != null)
                        doomed.Add(ch.gameObject);
                }
                // 若骨骼没有包在 Skeleton 下而是直接挂在 Player 下(名字在老Avatar骨骼列表), 也删
                foreach (var d in doomed) UnityEngine.Object.DestroyImmediate(d);

                // 实例化新模型, 把其直接子物体搬到 Player 下(等价于 Avatar 记录的层级搬进 Player)
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(modelRoot);
                if (inst == null) { Debug.LogError("[LYQ] 实例化新模型失败(请确认已打开任意场景)"); return; }

                var newSmr = inst.GetComponentInChildren<SkinnedMeshRenderer>(true);
                Bounds newB = default; bool hasNew = false;
                if (newSmr != null && newSmr.sharedMesh != null)
                {
                    var mm = MeshLocal(newSmr, newSmr.sharedMesh);
                    if (mm.HasValue) { newB = mm.Value; hasNew = true; }
                }

                float scale = 1f;
                if (matchOldHeight && hasOld && hasNew)
                {
                    float oh = Mathf.Max(oldB.size.y, 0.001f);
                    float nh = Mathf.Max(newB.size.y, 0.001f);
                    scale = oh / nh;
                    Debug.Log("[LYQ] 身高缩放: 旧=" + oldB.size.y.ToString("0.00") + " 新=" + newB.size.y.ToString("0.00") + " scale=" + scale.ToString("0.000"));
                }

                // 挂载: 新模型根下的每个子物体 -> Player
                var kids = new List<Transform>();
                foreach (Transform c in inst.transform) kids.Add(c);
                foreach (var k in kids)
                {
                    k.SetParent(player, false);
                    k.localScale = new Vector3(scale, scale, scale);
                    k.localPosition = new Vector3(k.localPosition.x, 0f, k.localPosition.z);
                }
                if (inst != null) UnityEngine.Object.DestroyImmediate(inst);

                // 落地对齐: 把新网格最低点抬到旧网格最低点
                if (hasOld && hasNew)
                {
                    var ns = player.GetComponentInChildren<SkinnedMeshRenderer>(true);
                    if (ns != null && ns.sharedMesh != null)
                    {
                        var nm = MeshLocal(ns, ns.sharedMesh);
                        if (nm.HasValue)
                        {
                            float delta = oldB.min.y - nm.Value.min.y;
                            Debug.Log("[LYQ] 落地对齐: 旧底=" + oldB.min.y.ToString("0.000") + " 新底=" + nm.Value.min.y.ToString("0.000") + " 上移=" + delta.ToString("0.000"));
                            foreach (Transform ch2 in player)
                            {
                                if (ch2.name == "PlayerCameraRoot") continue;
                                ch2.localPosition = new Vector3(ch2.localPosition.x, ch2.localPosition.y + delta, ch2.localPosition.z);
                            }
                        }
                    }
                }

                // 换 Avatar, 控制器与根运动设置沿用旧 Player
                if (oldAnimator != null)
                {
                    oldAnimator.avatar = avatar;
                    if (controller != null) oldAnimator.runtimeAnimatorController = controller;
                    oldAnimator.applyRootMotion = oldApplyRM;
                }

                if (System.IO.File.Exists(NewPrefabPath)) AssetDatabase.DeleteAsset(NewPrefabPath);
                PrefabUtility.SaveAsPrefabAsset(contents, NewPrefabPath);
                Debug.Log("[LYQ] 已生成: " + NewPrefabPath + "\n把该预制体放进场景替换原 PlayerGroup 即可。");
                EditorUtility.DisplayDialog("LYQ新角色", "已生成 PlayerGroup_lyq.prefab\n\n把场景里的 PlayerGroup 替换成 PlayerGroup_lyq 后测试。\n若人物尺寸/落地不对, 把 Console 里 [LYQ] 数值贴回来我调整对齐逻辑。", "OK");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        // 网格 AABB 换算到 Player 局部空间 (用变换矩阵乘, 不受骨骼姿态影响)
        static Bounds? MeshLocal(SkinnedMeshRenderer smr, Mesh mesh)
        {
            var b = mesh.bounds;
            var tr = smr.transform;
            Vector3 mn = tr.TransformPoint(b.min);
            Vector3 mx = tr.TransformPoint(b.max);
            return new Bounds((mn + mx) * 0.5f, mx - mn);
        }

        // ---------------- ⑥ 调试 ----------------
        [MenuItem("GGJ/LYQ角色/⑥ 打印新FBX层级与蒙皮权重", priority = 6)]
        public static void DumpModel()
        {
            var modelRoot = AssetDatabase.LoadMainAssetAtPath(ModelPath) as GameObject;
            if (modelRoot == null) { EditorUtility.DisplayDialog("提示", "先跑菜单① 让 Unity 导入模型", "OK"); return; }
            _rootCache = modelRoot.transform;
            Debug.Log("[LYQ] === 模型层级 ===\n" + DumpHierarchy(modelRoot.transform));
            var w = CollectBoneWeights(modelRoot.transform);
            if (w.Count > 0)
                Debug.Log("[LYQ] 蒙皮权重Top:\n" + string.Join("\n", w.OrderByDescending(kv => kv.Value).Select(kv => "  " + kv.Key + " : " + kv.Value.ToString("0.0")).Take(40)));
        }

        static string DumpHierarchy(Transform t, int depth = 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(' ', depth * 2).Append(t.name).Append('\n');
            foreach (Transform c in t) sb.Append(DumpHierarchy(c, depth + 1));
            return sb.ToString();
        }
    }
}
