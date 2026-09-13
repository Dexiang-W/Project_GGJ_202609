using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 编辑器辅助：批量统一所有正式场景的主相机为 "Main Camera.prefab"。
/// 
/// 用法：菜单 GGJ2026/相机/统一应用主相机预制体设置。
/// 
/// 规则：
/// · Begin_Menu：保留它现有的相机覆盖（正交 / 标题偏移），只确保它身上有 CameraFollowController；
/// · Level1~Level5：激活 Main Camera.prefab 实例，禁用场景里其它冗余相机；
/// · 若某场景没有该预制体，则自动拖一个进去。
/// </summary>
public static class MainCameraSetupHelper
{
    private const string MainCameraPrefabPath = "Assets/0_File_GGJ2026/5_Prefab/Main Camera.prefab";
    private const string ScenesFolder = "Assets/0_File_GGJ2026/6_Scene";

    [MenuItem("GGJ2026/相机/统一应用主相机预制体设置")]
    private static void ApplyMainCameraToAllScenes()
    {
        GameObject cameraPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(MainCameraPrefabPath);
        if (cameraPrefab == null)
        {
            Debug.LogError($"[MainCameraSetupHelper] 找不到预制体：{MainCameraPrefabPath}");
            return;
        }

        string[] sceneGuids = AssetDatabase.FindAssets("t:SceneAsset", new[] { ScenesFolder });
        if (sceneGuids.Length == 0)
        {
            Debug.LogWarning($"[MainCameraSetupHelper] 在 {ScenesFolder} 下没有找到场景文件。");
            return;
        }

        // 保存当前打开的脏场景，避免数据丢失
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        string currentScenePath = SceneManager.GetActiveScene().path;

        foreach (string guid in sceneGuids)
        {
            string scenePath = AssetDatabase.GUIDToAssetPath(guid);
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            string sceneName = scene.name;
            bool isTitle = sceneName == "Begin_Menu";

            // 收集场景里所有根级相机
            var rootObjects = scene.GetRootGameObjects();
            var allCameras = new List<Camera>();
            foreach (GameObject root in rootObjects)
                allCameras.AddRange(root.GetComponentsInChildren<Camera>(true));

            // 找出 Main Camera.prefab 的实例
            GameObject prefabInstance = FindPrefabInstance(rootObjects, cameraPrefab);

            if (prefabInstance == null)
            {
                // 本场景没有该预制体，自动实例化一个
                prefabInstance = (GameObject)PrefabUtility.InstantiatePrefab(cameraPrefab, scene);
                prefabInstance.name = "Main Camera";
                Debug.Log($"[MainCameraSetupHelper] {sceneName}: 新建 Main Camera.prefab 实例。", prefabInstance);
            }

            // 激活预制体实例
            if (!prefabInstance.activeSelf)
            {
                prefabInstance.SetActive(true);
                Debug.Log($"[MainCameraSetupHelper] {sceneName}: 激活 Main Camera.prefab 实例。", prefabInstance);
            }

            // 标题场景保留自己的覆盖，不强制改回跟拍参数；但要确保有 CameraFollowController
            var follow = prefabInstance.GetComponent<CameraFollowController>();
            if (follow == null)
            {
                follow = prefabInstance.AddComponent<CameraFollowController>();
                Debug.Log($"[MainCameraSetupHelper] {sceneName}: 补加 CameraFollowController。", prefabInstance);
            }

            // 非标题关卡：禁用所有不是该预制体实例的其它相机
            if (!isTitle)
            {
                foreach (Camera cam in allCameras)
                {
                    if (cam == null)
                        continue;

                    // 判断这个相机是否属于 Main Camera.prefab 实例
                    if (cam.transform.root.gameObject == prefabInstance)
                        continue;

                    // 往 RenderTexture 上渲染的相机（水波纹顶视机、小地图机等）不是用来输出的，
                    // 关掉会直接让场景里的特效失效，必须跳过。
                    if (cam.targetTexture != null)
                    {
                        Debug.Log($"[MainCameraSetupHelper] {sceneName}: 跳过 RenderTexture 相机 {cam.name}（不可禁用）。", cam.gameObject);
                        continue;
                    }

                    if (cam.gameObject.activeSelf || cam.gameObject.activeInHierarchy)
                    {
                        cam.gameObject.SetActive(false);
                        Debug.Log($"[MainCameraSetupHelper] {sceneName}: 禁用冗余相机 {cam.name}。", cam.gameObject);
                    }
                }
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        // 尽量恢复用户之前打开的场景
        if (!string.IsNullOrEmpty(currentScenePath))
            EditorSceneManager.OpenScene(currentScenePath, OpenSceneMode.Single);

        Debug.Log("[MainCameraSetupHelper] 主相机统一设置完成。");
    }

    private static GameObject FindPrefabInstance(GameObject[] rootObjects, GameObject prefabAsset)
    {
        foreach (GameObject root in rootObjects)
        {
            if (PrefabUtility.GetCorrespondingObjectFromSource(root) == prefabAsset)
                return root;

            // 也查一层子物体（有些场景可能把相机放在某个管理器下）
            foreach (Transform child in root.transform)
            {
                if (PrefabUtility.GetCorrespondingObjectFromSource(child.gameObject) == prefabAsset)
                    return child.gameObject;
            }
        }
        return null;
    }
}
