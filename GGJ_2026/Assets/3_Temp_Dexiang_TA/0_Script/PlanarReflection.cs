using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[ExecuteInEditMode]
public class PlanarReflection : MonoBehaviour
{
    [Header("反射设置")]
    public LayerMask reflectLayers = ~0;           // 要反射哪些层（建议排除 Water 层，运行时还会自动排除自身所在层）
    public float clipPlaneOffset = 0.07f;          // 裁剪偏移，避免水面穿模
    public float planeOffset = 0f;                 // 水面高度偏移
    [Range(0.1f, 1f)]
    public float resolutionScale = 0.5f;           // 分辨率缩放（性能关键）

    [Header("反射剔除 / 淡出")]
    [Tooltip("开始淡出的距离（米）：水面距相机超过它后，反射强度从 1 平滑衰减到 0")]
    public float fadeStartDistance = 20f;
    [Tooltip("反射完全消失的距离（米）：超过它反射强度为 0，并彻底停止反射渲染。0 = 不限距离")]
    public float reflectionDistance = 30f;
    [Tooltip("写入 Shader 的反射强度 Float 属性名。\n需在 ShaderGraph 中新增同名 Float，并用 Multiply 节点乘在“反射采样结果”之后。\n留空 = 不写强度（会退化成硬切换）")]
    public string fadePropertyName = "_PlanarReflectionFade";

    private Camera reflectionCamera;
    private RenderTexture reflectionRT;
    private MaterialPropertyBlock mpb;             // 每块水面独立的属性块，避免多水面互相覆盖
    private static readonly int PlanarReflectionTextureID = Shader.PropertyToID("_PlanarReflectionTexture");

    private void OnEnable()
    {
        RenderPipelineManager.beginCameraRendering += BeginCameraRendering;
        CreateResources();
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= BeginCameraRendering;
        CleanUp();
    }

    private void CreateResources()
    {
        if (reflectionCamera == null)
        {
            GameObject go = new GameObject("PlanarReflectionCamera");
            reflectionCamera = go.AddComponent<Camera>();
            reflectionCamera.enabled = false;
            reflectionCamera.gameObject.hideFlags = HideFlags.HideAndDontSave;

            var data = reflectionCamera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            data.requiresColorOption = CameraOverrideOption.Off;
            data.requiresDepthOption = CameraOverrideOption.Off;
        }
    }

    private void BeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (camera.cameraType == CameraType.Reflection || camera.cameraType == CameraType.Preview)
            return;

        Renderer r = GetComponent<Renderer>();

        // 距离剔除（最便宜，最先算）+ 淡出：
        //  - dist <= fadeStartDistance   ：强度 1，正常渲染
        //  - fadeStartDistance < dist < reflectionDistance ：强度平滑 1 -> 0（水面上反射淡出）
        //  - dist >= reflectionDistance  ：完全关闭，停止渲染反射
        float dist = (camera.transform.position - transform.position).magnitude;
        float fade = 1f;
        if (reflectionDistance > 0f)
        {
            if (dist >= reflectionDistance)
            {
                SetReflectionFade(r, 0f); // 让强度收敛到 0，水面不再有任何反射贡献
                return;
            }

            if (fadeStartDistance > 0f && dist > fadeStartDistance)
            {
                float t = Mathf.InverseLerp(fadeStartDistance, reflectionDistance, dist);
                fade = 1f - Mathf.SmoothStep(0f, 1f, t);
            }
        }

        // 视锥剔除：水面不在相机画面里时同样没必要渲染反射
        if (r != null && !IsVisibleToCamera(r, camera))
            return;

        RenderReflection(context, camera, fade);
    }

    // 把反射强度写到水面渲染器（Shader 需要把同名 Float 乘在反射采样结果上）
    private void SetReflectionFade(Renderer renderer, float fade)
    {
        if (string.IsNullOrEmpty(fadePropertyName)) return;
        if (mpb == null) mpb = new MaterialPropertyBlock();
        mpb.SetFloat(Shader.PropertyToID(fadePropertyName), fade);
        if (renderer != null)
            renderer.SetPropertyBlock(mpb);
    }

    private bool IsVisibleToCamera(Renderer r, Camera cam)
    {
        Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);
        return GeometryUtility.TestPlanesAABB(planes, r.bounds);
    }

    private void RenderReflection(ScriptableRenderContext context, Camera realCamera, float fade)
    {
        if (reflectionCamera == null) return;

        // 1. 更新反射相机参数
        reflectionCamera.CopyFrom(realCamera);
        // 多水面共处时：自动去掉"自己所在层"，避免把其它水面也当反射物渲染进反射图
        reflectionCamera.cullingMask = reflectLayers & ~(1 << gameObject.layer);
        reflectionCamera.useOcclusionCulling = false;

        // 2. 计算镜像位置（相对于当前平面）
        Vector3 normal = transform.up;
        Vector3 pos = transform.position + Vector3.up * planeOffset;

        // 镜像相机位置和旋转
        float d = -Vector3.Dot(normal, pos) - clipPlaneOffset;
        Vector4 reflectionPlane = new Vector4(normal.x, normal.y, normal.z, d);

        Matrix4x4 reflectionMat = Matrix4x4.identity;
        reflectionMat *= Matrix4x4.Scale(new Vector3(1, -1, 1));
        CalculateReflectionMatrix(ref reflectionMat, reflectionPlane);

        reflectionCamera.worldToCameraMatrix = realCamera.worldToCameraMatrix * reflectionMat;
        reflectionCamera.transform.position = ReflectPosition(realCamera.transform.position, pos);

        // 3. Oblique Projection（斜裁剪，防止水面下方穿透）
        Vector4 clipPlane = CameraSpacePlane(reflectionCamera, pos, normal, 1.0f);
        reflectionCamera.projectionMatrix = realCamera.CalculateObliqueMatrix(clipPlane);

        // 4. 创建/更新 RenderTexture
        int width = (int)(realCamera.pixelWidth * resolutionScale);
        int height = (int)(realCamera.pixelHeight * resolutionScale);

        if (reflectionRT == null || reflectionRT.width != width || reflectionRT.height != height)
        {
            if (reflectionRT != null) reflectionRT.Release();
            reflectionRT = new RenderTexture(width, height, 24, RenderTextureFormat.DefaultHDR);
            reflectionRT.name = "PlanarReflectionRT";
        }

        reflectionCamera.targetTexture = reflectionRT;

        // 5. 渲染
        UniversalRenderPipeline.RenderSingleCamera(context, reflectionCamera);

        // 6. 绑定到本水面的渲染器（MaterialPropertyBlock，不再用全局变量，多块水面各自反射互不覆盖）
        if (mpb == null) mpb = new MaterialPropertyBlock();
        mpb.SetTexture(PlanarReflectionTextureID, reflectionRT);
        if (!string.IsNullOrEmpty(fadePropertyName))
            mpb.SetFloat(Shader.PropertyToID(fadePropertyName), fade); // 同时写入本帧的反射强度（淡入淡出）

        Renderer renderer = GetComponent<Renderer>();
        if (renderer != null)
            renderer.SetPropertyBlock(mpb);
        else
            Shader.SetGlobalTexture(PlanarReflectionTextureID, reflectionRT); // 兜底：物体上没有 Renderer 时才回退全局
    }

    private Vector3 ReflectPosition(Vector3 pos, Vector3 planePos)
    {
        return new Vector3(pos.x, planePos.y * 2 - pos.y, pos.z);
    }

    private void CalculateReflectionMatrix(ref Matrix4x4 m, Vector4 plane)
    {
        m.m00 = 1F - 2F * plane[0] * plane[0];
        m.m01 = -2F * plane[0] * plane[1];
        m.m02 = -2F * plane[0] * plane[2];
        m.m03 = -2F * plane[3] * plane[0];

        m.m10 = -2F * plane[1] * plane[0];
        m.m11 = 1F - 2F * plane[1] * plane[1];
        m.m12 = -2F * plane[1] * plane[2];
        m.m13 = -2F * plane[3] * plane[1];

        m.m20 = -2F * plane[2] * plane[0];
        m.m21 = -2F * plane[2] * plane[1];
        m.m22 = 1F - 2F * plane[2] * plane[2];
        m.m23 = -2F * plane[3] * plane[2];

        m.m30 = m.m31 = m.m32 = 0F;
        m.m33 = 1F;
    }

    private Vector4 CameraSpacePlane(Camera cam, Vector3 pos, Vector3 normal, float sideSign)
    {
        Vector3 offsetPos = pos + normal * clipPlaneOffset;
        Matrix4x4 m = cam.worldToCameraMatrix;
        Vector3 cameraPos = m.MultiplyPoint(offsetPos);
        Vector3 cameraNormal = m.MultiplyVector(normal).normalized * sideSign;
        return new Vector4(cameraNormal.x, cameraNormal.y, cameraNormal.z, -Vector3.Dot(cameraPos, cameraNormal));
    }

    private void CleanUp()
    {
        if (reflectionRT != null)
        {
            reflectionRT.Release();
            reflectionRT = null;
        }
        if (reflectionCamera != null)
        {
            DestroyImmediate(reflectionCamera.gameObject);
            reflectionCamera = null;
        }
    }

    private void OnDestroy() => CleanUp();
}
