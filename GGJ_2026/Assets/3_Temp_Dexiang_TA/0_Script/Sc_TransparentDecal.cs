using System.Collections;
using UnityEngine;

/// <summary>
/// 透贴(Decal)运行时组件 —— 供程序绑定调用。
///
/// 用法：
///  1. 美术用「Tools/Dexiang TA/透贴工具」把一张贴图生成材质+预制体（自动带上本组件）；
///  2. 把预制体拖到场景任意表面附近微调角度/大小；
///  3. 程序打击/受击时，调用 Sc_TransparentDecal.SpawnOnSurface(...) 现场生成一块，
///     再调 SetAlpha(1f) 让裂缝"透明度直接变 1"出现在场景中，之后可 FadeOut 消失。
///
/// 透明度说明：
///  - 材质属性 _Alpha 是全局限量；本组件通过 MaterialPropertyBlock 控制，互不影响材质资产本身，
///    所以同一材质上多个透贴可以各自独立淡入淡出。
/// </summary>
[RequireComponent(typeof(MeshRenderer))]
[DisallowMultipleComponent]
public class Sc_TransparentDecal : MonoBehaviour
{
    // ---------------- 基础参数 ----------------
    [Header("Decal / 透贴基础")]
    [Tooltip("贴图尺寸宽（单位:米）。xy 方向为贴图平面")]
    public float width = 1f;
    [Tooltip("贴图尺寸高（单位:米）")]
    public float height = 1f;
    [Tooltip("离开表面一点点的距离，防止和表面Z-fight闪烁")]
    public float surfaceOffset = 0.02f;

    [Header("Transparency / 透明度 (程序可读写)")]
    [Range(0f, 1f)]
    [Tooltip("当前透明度：0完全消失，1完全显示")]
    public float alpha = 1f;

    [Header("Life Cycle / 生命周期 (可选)")]
    [Tooltip("生成时是否淡入(0→当前alpha)")]
    public bool fadeIn = false;
    [Tooltip("淡入时长(秒)")]
    public float fadeInDuration = 0.15f;
    [Tooltip("停留时间，-1=不自动销毁")]
    public float lifetime = -1f;
    [Tooltip("到时是否淡出并销毁")]
    public bool autoFadeOut = false;
    [Tooltip("淡出时长(秒)")]
    public float fadeOutDuration = 0.4f;

    private MaterialPropertyBlock _mpb;
    private MeshRenderer _renderer;
    private Coroutine _fadeRoutine;

    private static readonly int _AlphaProp = Shader.PropertyToID("_Alpha");
    private static readonly int _BaseMapProp = Shader.PropertyToID("_BaseMap");

    private void Awake()
    {
        _renderer = GetComponent<MeshRenderer>();
        ApplyAlpha();
    }

    private void OnEnable()
    {
        ApplyAlpha();

        if (fadeIn)
        {
            float target = alpha;
            alpha = 0f;
            ApplyAlpha();                          // 先瞬间隐藏
            SetAlpha(target, fadeInDuration);      // 再淡入到目标
        }

        if (lifetime > 0f)
        {
            float wait = lifetime + (fadeIn ? fadeInDuration : 0f);
            if (autoFadeOut)
                Invoke(nameof(DoFadeOut), wait);
            else
                Invoke(nameof(DoDestroy), wait);
        }
    }

    private void OnDisable()
    {
        CancelInvoke();
    }

    // =================================================================================
    // 程序绑定 API
    // =================================================================================

    /// <summary>
    /// 打击/交互时：在世界点 spawn 一块透贴，自动贴住表面法线。
    /// 之后默认把透明度直接拉到 1（=你说的"生成1放在场景中"），再调用 FadeOut 淡出即可。
    /// </summary>
    /// <param name="decalPrefab">透贴预制体（美术用工具生成，含本组件）</param>
    /// <param name="position">命中点</param>
    /// <param name="surfaceNormal">命中表面的法线</param>
    /// <param name="width">可选：覆盖宽（<=0 时用预制体默认）</param>
    /// <param name="height">可选：覆盖高（<=0 时用预制体默认）</param>
    public static Sc_TransparentDecal SpawnOnSurface(GameObject decalPrefab, Vector3 position,
        Vector3 surfaceNormal, float width = 0f, float height = 0f, Transform parent = null)
    {
        if (decalPrefab == null)
        {
            Debug.LogError("[TransparentDecal] decalPrefab 为空，无法生成透贴");
            return null;
        }

        GameObject go = Instantiate(decalPrefab, parent);
        Sc_TransparentDecal decal = go.GetComponent<Sc_TransparentDecal>();

        Vector3 n = surfaceNormal.normalized;
        if (n.sqrMagnitude < 0.0001f) n = Vector3.up;

        // 让四边形的正面(+Z)朝向法线方向贴到表面上
        Vector3 up = Mathf.Abs(n.y) > 0.99f ? Vector3.forward : Vector3.up;
        go.transform.rotation = Quaternion.LookRotation(n, up);
        go.transform.position = position + n * decal.surfaceOffset;

        go.transform.localScale = new Vector3(
            width > 0f ? width : decal.width,
            height > 0f ? height : decal.height,
            1f);

        decal.AppearNow();       // 透明度直接 = 1，出现在场景
        return decal;
    }

    /// <summary>
    /// 透明度直接变为 1（立刻，无淡入）。
    /// </summary>
    public void AppearNow()
    {
        SetAlpha(1f, 0f);
    }

    /// <summary>
    /// 透明度直接变为 0（立刻隐藏）。
    /// </summary>
    public void HideNow()
    {
        SetAlpha(0f, 0f);
    }

    /// <summary>
    /// 设置透明度。duration<=0 时立即生效；否则从当前值渐变到 target。
    /// </summary>
    public void SetAlpha(float target, float duration = 0f)
    {
        target = Mathf.Clamp01(target);

        if (duration <= 0f)
        {
            if (_fadeRoutine != null) { StopCoroutine(_fadeRoutine); _fadeRoutine = null; }
            alpha = target;
            ApplyAlpha();
            return;
        }

        float from = alpha;
        if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);
        _fadeRoutine = StartCoroutine(FadeRoutine(from, target, duration));
    }

    /// <summary>淡出到 0（通常配合销毁使用）</summary>
    public void FadeOut(float duration = 0.4f)
    {
        SetAlpha(0f, duration);
    }

    /// <summary>淡入到 1</summary>
    public void FadeIn(float duration = 0.15f)
    {
        SetAlpha(1f, duration);
    }

    /// <summary>
    /// 运行中换贴图（也可直接换材质 _BaseMap）。
    /// </summary>
    public void SetDecalTexture(Texture tex)
    {
        EnsureBlock();
        _mpb.SetTexture(_BaseMapProp, tex);
        ApplyBlock();
    }

    /// <summary>淡出结束后销毁物体</summary>
    public void FadeOutAndDestroy(float fadeDuration, float delay = 0f)
    {
        StartCoroutine(FadeOutAndDestroyRoutine(fadeDuration, delay));
    }

    // =================================================================================
    // 内部
    // =================================================================================

    private IEnumerator FadeRoutine(float from, float to, float duration)
    {
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            alpha = Mathf.Clamp01(Mathf.Lerp(from, to, Mathf.Clamp01(t / duration)));
            ApplyAlpha();
            yield return null;
        }
        alpha = Mathf.Clamp01(to);
        ApplyAlpha();
        _fadeRoutine = null;
    }

    private IEnumerator FadeOutAndDestroyRoutine(float fadeDuration, float delay)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);
        FadeOut(fadeDuration);
        yield return new WaitForSeconds(fadeDuration + 0.05f);
        if (Application.isPlaying) Destroy(gameObject);
    }

    private void DoFadeOut()
    {
        FadeOut(fadeOutDuration);
        Invoke(nameof(DoDestroy), fadeOutDuration + 0.05f);
    }

    private void DoDestroy()
    {
        if (Application.isPlaying) Destroy(gameObject);
    }

    private void EnsureBlock()
    {
        if (_renderer == null) _renderer = GetComponent<MeshRenderer>();
        if (_mpb == null) _mpb = new MaterialPropertyBlock();
    }

    private void ApplyAlpha()
    {
        EnsureBlock();
        _mpb.SetFloat(_AlphaProp, alpha);
        ApplyBlock();
    }

    private void ApplyBlock()
    {
        if (_renderer != null)
        {
            // 用 SetPropertyBlock(null) 兜底旧 block，保证我们设的属性生效
            _renderer.SetPropertyBlock(_mpb);
        }
    }

    // 编辑器里改 alpha 时实时预览
    private void OnValidate()
    {
        if (!Application.isPlaying)
            ApplyAlpha();
    }
}
