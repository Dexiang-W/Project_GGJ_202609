using System;
using System.Collections.Generic;
using StarterAssets;
using UnityEngine;
using UnityEngine.Events;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// 可交互物体（需求：玩家靠近 → 到一定距离后物体边缘高亮，按 E 键执行交互命令）。
///
/// 用法：
///   · 挂到任意想交互的物体上（自身或任意子物体上有网格即可：普通 MeshFilter 或 SkinnedMeshRenderer 都支持）；
///   · Inspector 里填“触发距离”“描边颜色/粗细”；
///   · “交互后执行的事件”（On Interact）拖你想执行的对象方法 / 脚本方法即可，可填任意命令；
///   · 场景中同时有多个可交互物体时，系统会自动只高亮并响应“离玩家最近”的那一个。
///
/// 描边实现：运行时给物体上每个网格各加一个“背面挤出”的描边壳（GGJ2026/OutlineShell Shader）。
/// 子物体上的网格也会被描边，因此像 P_LongVine 这种“根节点没有网格、网格都在子物体上”的物体也能正常高亮。
/// </summary>
[DisallowMultipleComponent]
public class InteractableObject : MonoBehaviour
{
    /// <summary>所有处于激活状态的可交互物体（用于算出“谁离玩家最近”）。</summary>
    private static readonly HashSet<InteractableObject> ActiveObjects = new HashSet<InteractableObject>();

    [Header("检测范围")]
    [Tooltip("玩家与物体的中心点距离小于该值时，物体边缘开始高亮并允许按 E 交互")]
    [SerializeField] private float interactDistance = 4f;

    [Header("描边效果")]
    [Tooltip("边缘高亮颜色")]
    [SerializeField] private Color highlightColor = new Color(0.15f, 1f, 0.6f, 1f);
    [Tooltip("描边粗细（物体空间单位；物体越大可适当调大）")]
    [SerializeField] private float outlineWidth = 0.05f;

    [Header("交互设置")]
    [Tooltip("交互后是否只用一次（用完即永久取消高亮与交互）")]
    [SerializeField] private bool consumeOnInteract = false;

    [Header("交互后执行的事件（按 E 触发，命令可以自己填）")]
    public UnityEvent OnInteract = new UnityEvent();

    private const string OutlineShaderName = "GGJ2026/OutlineShell";

    private readonly List<GameObject> outlineShells = new List<GameObject>();
    private Material outlineMaterial;
    private bool warnedNoMesh;
    private bool consumed;
    private bool highlightOn;

    private void OnEnable()
    {
        ActiveObjects.Add(this);
    }

    private void OnDisable()
    {
        ActiveObjects.Remove(this);
        SetHighlight(false);
    }

    private void OnDestroy()
    {
        if (outlineMaterial != null)
            Destroy(outlineMaterial);
        outlineShells.Clear();
    }

    private void Update()
    {
        if (consumed)
        {
            SetHighlight(false);
            return;
        }

        if (TryGetNearestActiveObject(out InteractableObject nearest)
            && nearest == this
            && DistanceToPlayer() <= interactDistance)
        {
            SetHighlight(true);

            // 玩家此时按 E → 交互
            if (WasEPressedThisFrame())
                Interact();
        }
        else
        {
            SetHighlight(false);
        }
    }

    /// <summary>执行一次交互（其他脚本也可主动调用，例如机关联动）。</summary>
    public void Interact()
    {
        if (consumed)
            return;

        if (consumeOnInteract)
            consumed = true;

        // 交互音效：SFX_Energy_Inject
        if (AudioManager.Instance != null)
            AudioManager.Instance.PlayInject();

        OnInteract?.Invoke();
    }

    /// <summary>是否处于高亮状态。</summary>
    public bool IsHighlighted => highlightOn;

    // ---------------------------------------------------------------- 高亮

    private void SetHighlight(bool on)
    {
        if (highlightOn == on)
            return;

        highlightOn = on;

        if (on)
        {
            if (outlineShells.Count == 0)
                BuildOutlineShell();

            foreach (GameObject shell in outlineShells)
            {
                if (shell != null)
                    shell.SetActive(true);
            }
        }
        else
        {
            foreach (GameObject shell in outlineShells)
            {
                if (shell != null)
                    shell.SetActive(false);
            }
        }
    }

    /// <summary>
    /// 生成描边壳：在自己 + 所有子物体上收集网格，每个网格生成一个“背面挤出”的描边子物体。
    /// 支持普通 MeshFilter 网格，也支持 SkinnedMeshRenderer（克隆一份同骨骼的描边渲染器跟随动画）。
    /// 只有整个层级里一个网格都找不到时，才会提示一次“无法描边”。
    /// </summary>
    private void BuildOutlineShell()
    {
        Shader shader = Shader.Find(OutlineShaderName);
        if (shader == null)
        {
            if (!warnedNoMesh)
            {
                warnedNoMesh = true;
                Debug.LogWarning($"[InteractableObject] {name} 找不到 Shader “{OutlineShaderName}”，无法高亮。", this);
            }
            return;
        }

        outlineMaterial = new Material(shader) { name = $"{name}_OutlineShellMat" };
        outlineMaterial.SetColor("_Color", highlightColor);
        outlineMaterial.SetFloat("_Width", Mathf.Max(0.001f, outlineWidth));

        // 1) 普通网格：自己 + 所有子物体上带 Renderer 的 MeshFilter
        foreach (MeshRenderer rend in GetComponentsInChildren<MeshRenderer>(true))
        {
            if (rend == null)
                continue;
            MeshFilter mf = rend.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null)
                continue;

            CreateMeshOutlineShell(mf);
        }

        // 2) 骨骼网格：SkinnedMeshRenderer（根节点通常是空物体，P_LongVine 这类植物属于此情况）
        foreach (SkinnedMeshRenderer skinned in GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (skinned == null || skinned.sharedMesh == null)
                continue;

            CreateSkinnedOutlineShell(skinned);
        }

        if (outlineShells.Count == 0)
        {
            if (!warnedNoMesh)
            {
                warnedNoMesh = true;
                Debug.LogWarning($"[InteractableObject] {name} 自身及子物体上都没有可用的网格（MeshFilter/SkinnedMeshRenderer），" +
                                 "无法做描边高亮；交互仍可触发，只是没有高亮显示。", this);
            }
            return;
        }
    }

    private void CreateMeshOutlineShell(MeshFilter source)
    {
        GameObject shell = new GameObject("GGJ_OutlineShell");
        shell.transform.SetParent(source.transform, false);
        shell.transform.localPosition = Vector3.zero;
        shell.transform.localRotation = Quaternion.identity;
        shell.transform.localScale = Vector3.one;
        shell.layer = source.gameObject.layer;

        shell.AddComponent<MeshFilter>().sharedMesh = source.sharedMesh;
        shell.AddComponent<MeshRenderer>().sharedMaterial = outlineMaterial;

        outlineShells.Add(shell);
    }

    private void CreateSkinnedOutlineShell(SkinnedMeshRenderer source)
    {
        GameObject shell = new GameObject("GGJ_OutlineShell_Skinned");
        shell.transform.SetParent(source.transform, false);
        shell.layer = source.gameObject.layer;

        SkinnedMeshRenderer clone = shell.AddComponent<SkinnedMeshRenderer>();
        clone.sharedMesh = source.sharedMesh;
        clone.bones = source.bones;
        clone.rootBone = source.rootBone;
        clone.quality = source.quality;
        clone.updateWhenOffscreen = true;   // 镜头外也跟随骨骼更新，避免走近才“啪”地出现
        clone.sharedMaterial = outlineMaterial;

        outlineShells.Add(shell);
    }

    // ---------------------------------------------------------------- 工具

    private float DistanceToPlayer()
    {
        ThirdPersonController player = ThirdPersonController.Instance;
        if (player == null)
            return float.MaxValue;

        return Vector3.Distance(transform.position, player.transform.position);
    }

    private static bool TryGetNearestActiveObject(out InteractableObject nearest)
    {
        nearest = null;
        float best = float.MaxValue;

        foreach (InteractableObject obj in ActiveObjects)
        {
            if (obj == null || !obj.enabled)
                continue;

            float d = obj.DistanceToPlayer();
            if (d < best)
            {
                best = d;
                nearest = obj;
            }
        }

        return nearest != null;
    }

    private static bool WasEPressedThisFrame()
    {
#if ENABLE_INPUT_SYSTEM
        var keyboard = Keyboard.current;
        return keyboard != null && keyboard.eKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.E);
#endif
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.15f, 1f, 0.6f, 0.25f);
        Gizmos.DrawWireSphere(transform.position, interactDistance);
    }
#endif
}
