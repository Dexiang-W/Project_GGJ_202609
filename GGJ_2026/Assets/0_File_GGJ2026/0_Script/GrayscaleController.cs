using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 画面灰度控制：运行时创建全局 Volume + ColorAdjustments，通过饱和度实现「灰屏 → 恢复彩色」。
/// 前置条件：主相机需要开启 Post Processing（Bandeng_Test 里的 Main Camera 已开启）。
/// </summary>
[DisallowMultipleComponent]
public class GrayscaleController : MonoBehaviour
{
    [Header("饱和度")]
    [Range(-100f, 0f)]
    [SerializeField] private float grayscaleSaturation = -100f;
    [SerializeField] private float normalSaturation = 0f;

    [Header("过渡")]
    [SerializeField] private float fadeDuration = 1.0f;

    private Volume volume;
    private ColorAdjustments colorAdjustments;
    private float currentSaturation;
    private Coroutine fadeRoutine;

    /// <summary>后处理是否可用（不可用时会跳过灰屏效果，不影响流程）。</summary>
    public bool IsAvailable => colorAdjustments != null;

    private void Awake()
    {
        CreateVolume();
        currentSaturation = grayscaleSaturation;
        ApplySaturation(currentSaturation);
    }

    private void OnDestroy()
    {
        if (volume != null && volume.profile != null)
            Destroy(volume.profile);
    }

    #region 对外接口

    /// <summary>立刻设置为灰屏。</summary>
    public void SetGrayscale()
    {
        SetSaturation(grayscaleSaturation);
    }

    /// <summary>立刻恢复彩色。</summary>
    public void SetFullColor()
    {
        SetSaturation(normalSaturation);
    }

    /// <summary>直接设置饱和度（-100 全灰，0 原色）。</summary>
    public void SetSaturation(float saturation)
    {
        currentSaturation = saturation;
        ApplySaturation(currentSaturation);
    }

    /// <summary>从灰屏渐变回彩色。</summary>
    public IEnumerator FadeToFullColorRoutine(float duration)
    {
        yield return FadeSaturation(currentSaturation, normalSaturation, duration);
    }

    /// <summary>渐变到灰屏。</summary>
    public IEnumerator FadeToGrayscaleRoutine(float duration)
    {
        yield return FadeSaturation(currentSaturation, grayscaleSaturation, duration);
    }

    #endregion

    private void CreateVolume()
    {
        GameObject volumeObject = new GameObject("GrayscaleVolume");
        volumeObject.transform.SetParent(transform, false);
        volumeObject.layer = 0; // Default 层，主相机的 VolumeLayerMask 默认包含它

        volume = volumeObject.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 100f;
        volume.profile = ScriptableObject.CreateInstance<VolumeProfile>();

        colorAdjustments = volume.profile.Add<ColorAdjustments>(true);

        if (colorAdjustments == null)
        {
            Debug.LogWarning("[GrayscaleController] 无法创建 ColorAdjustments，灰屏效果将不可用。", this);
            return;
        }

        colorAdjustments.saturation.overrideState = true;
        colorAdjustments.postExposure.overrideState = false;
        colorAdjustments.contrast.overrideState = false;
        colorAdjustments.colorFilter.overrideState = false;
        colorAdjustments.hueShift.overrideState = false;
    }

    private IEnumerator FadeSaturation(float from, float to, float duration)
    {
        if (fadeRoutine != null)
            StopCoroutine(fadeRoutine);

        if (duration <= 0f || !IsAvailable)
        {
            SetSaturation(to);
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            SetSaturation(Mathf.Lerp(from, to, elapsed / duration));
            yield return null;
        }

        SetSaturation(to);
        fadeRoutine = null;
    }

    private void ApplySaturation(float saturation)
    {
        if (colorAdjustments == null)
            return;

        colorAdjustments.saturation.overrideState = true;
        colorAdjustments.saturation.value = Mathf.Clamp(saturation, -100f, 100f);
    }
}
