using UnityEditor;
using UnityEngine;

/// <summary>
/// 在场景里一键创建“绑定风场的沙尘暴”：
///   - GameObject/Effects/Sand Storm (绑定风场)
/// 如果场景里还没有挂 WindDirection.cs 的风向物体，会自动创建一个（旋转它即可改变风/沙的方向）。
/// </summary>
public static class Editor_SandStormCreate
{
    [MenuItem("GameObject/Effects/Sand Storm (绑定风场)", false, 10)]
    public static void CreateSandStorm()
    {        // 尽量生成在 Scene 相机视线前方，方便立刻看到
        Vector3 pos = Vector3.zero;
        Vector3 fwd = Vector3.forward;

        Camera cam = null;
        if (SceneView.lastActiveSceneView != null && SceneView.lastActiveSceneView.camera != null)
            cam = SceneView.lastActiveSceneView.camera;
        if (cam == null) cam = Camera.main;

        if (cam != null)
        {
            pos = cam.transform.position + cam.transform.forward * 22f;
            pos.y = Mathf.Max(pos.y, 0f);
            fwd = cam.transform.forward;
        }

        Vector3 flat = Vector3.ProjectOnPlane(fwd, Vector3.up);
        if (flat.sqrMagnitude < 0.0001f) flat = Vector3.forward;
        Quaternion rot = Quaternion.LookRotation(flat.normalized);

        // ---- 沙尘暴本体 ----
        var stormGo = new GameObject("SandStorm (Wind)");
        Undo.RegisterCreatedObjectUndo(stormGo, "Create Sand Storm");
        Undo.AddComponent<ParticleSystem>(stormGo);
        var storm = Undo.AddComponent<Sc_SandStorm>(stormGo);

        // ---- 风向源（场景里已有就复用，没有则创建一个） ----
        var wind = FindWindDirection();
        if (wind == null)
        {
            var windGo = new GameObject("Wind Direction Source");
            Undo.RegisterCreatedObjectUndo(windGo, "Create Wind Direction Source");
            windGo.transform.SetPositionAndRotation(pos, rot);
            Undo.AddComponent<WindDirection>(windGo);
            storm.windSource = windGo.transform;
        }
        else
        {
            storm.windSource = wind.transform;
        }

        storm.ApplyPreset();
        Selection.activeGameObject = stormGo;
        SceneView.lastActiveSceneView?.FrameSelected();
    }

    [MenuItem("Tools/Dexiang TA/沙尘 SandStorm (绑定风场)", false, 10)]
    public static void CreateSandStormFromTools()
    {
        CreateSandStorm();
    }

    static WindDirection FindWindDirection()
    {
#if UNITY_2022_2_OR_NEWER
        return Object.FindFirstObjectByType<WindDirection>();
#else
        return Object.FindObjectOfType<WindDirection>();
#endif
    }
}
