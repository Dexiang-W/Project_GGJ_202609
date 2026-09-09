using System;
using System.Collections;
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
/// 能量植物（充能-回收）模式：
///   · 勾选 Energy Plant Mode 后该物体变成一个“能量球”：靠近按 E = 消耗 1 格左上角能量
///     （能量不足不触发），充能后能量球视觉（Hidden When Charged）消失，On Interact 仍会触发
///     （通常给植物 Animator SetTrigger("IsRise") 让植物生长）；
///   · 已充能时靠近，描边自动变黄提示，此时按 Q = 收回能量（能量 +1、球重现），并把
///     Plant Animators 里登记的植物动画退回“未长成”初始状态（默认状态名 None），可反复 E/Q。
///   · 编辑器菜单“GGJ2026/能量植物 → 把选中物体升级为…能量球”可自动开启该模式并填入植物 Animator。
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

    [Header("能量植物模式（E 消耗能量→能量球消失+植物生长；Q 站在旁边→收回能量+植物退回初始形态）")]
    [Tooltip("勾选后：E 会给“能量球”充能（左上角能量 -1，能量不足则不触发）；充能后靠近高亮提示改按 Q，可把能量收回来")]
    [SerializeField] private bool energyPlantMode = false;
    [Tooltip("按一次 E 消耗的能量格数（默认 1，Q 回收时原样还回）")]
    [SerializeField] private int chargeEnergyCost = 1;
    [Tooltip("充能后要隐藏的物体（通常是“能量球”本身的视觉物体/发光球），空数组表示不隐藏")]
    [SerializeField] private GameObject[] hiddenWhenCharged = null;
    [Tooltip("被本能量球催长的植物 Animator（Q 回收时把它们的动画退回初始/未长成形态），可多个")]
    [SerializeField] private Animator[] plantAnimators = null;
    [Tooltip("植物 Animator 的“初始/未长成”状态名（Animator 默认状态名，当前项目里是 None）")]
    [SerializeField] private string ungrownStateName = "None";

    private static readonly int NoneStateHash = Animator.StringToHash("None");

    // 能被“倒放”回初始状态的正向生长状态。Q 回收时直接 speed=-1 倒放，不依赖控制器里额外写反向状态。
    private static readonly Dictionary<int, string> ReverseStateNames = new Dictionary<int, string>
    {
        { Animator.StringToHash("Plant_Rise"), "Plant_Rise" },
        { Animator.StringToHash("Plant_Move"), "Plant_Move" },
        { Animator.StringToHash("Door_Rise"), "Door_Rise" },
    };
    [Tooltip("已充能（等 Q 回收）时描边换成该颜色，区别于“按 E 充能”的颜色")]
    [SerializeField] private Color reclaimHighlightColor = new Color(1f, 0.85f, 0.3f, 1f);

    private const string OutlineShaderName = "GGJ2026/OutlineShell";

    private readonly List<GameObject> outlineShells = new List<GameObject>();
    private Material outlineMaterial;
    private bool warnedNoMesh;
    private bool consumed;
    private bool highlightOn;
    private Coroutine hintRoutine;

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
        // 已用掉/已充能
        if (consumed)
        {
            // 能量植物模式：consumed = “能量已经给了能量球”，此时改为等 Q 回收
            if (energyPlantMode && TryGetNearestReclaimable(out InteractableObject nearestReclaimable)
                && nearestReclaimable == this
                && DistanceToPlayer() <= interactDistance)
            {
                SetHighlight(true, true);   // 换成“可回收”颜色
                if (WasQPressedThisFrame())
                    ReclaimEnergy();
            }
            else
            {
                SetHighlight(false);
            }
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

        // —— 能量植物模式：E = 把能量给能量球（消耗能量格、球消失、植物生长） ——
        if (energyPlantMode)
        {
            GameplayHUD.EnsureCreated();
            GameplayHUD hud = GameplayHUD.Instance;
            if (hud == null)
                return;

            // 能量不足：不消耗也不生长，给个提示
            if (!hud.SpendEnergy(chargeEnergyCost))
            {
                ShowInsufficientEnergyHint();
                return;
            }

            // 用掉 = 已充能，进入“等 Q 回收”状态
            consumed = true;
            SetHighlight(false);

            // 能量球视觉消失（能量已经给了它）
            SetHiddenWhenCharged(true);

            // 交互音效：SFX_Energy_Inject
            if (AudioManager.Instance != null)
                AudioManager.Instance.PlayInject();

            // 玩家按 E 之前接好的命令（通常 = 给植物 Animator SetTrigger(IsRise) 让它生长）
            OnInteract?.Invoke();
            return;
        }

        // —— 普通可交互物体（非能量植物）：保持原有行为 ——
        if (consumeOnInteract)
            consumed = true;

        // 交互音效：SFX_Energy_Inject
        if (AudioManager.Instance != null)
            AudioManager.Instance.PlayInject();

        OnInteract?.Invoke();
    }

    /// <summary>
    /// Q 回收：站在已充能能量球旁按 Q —— 能量还回玩家、能量球重新出现、
    /// 植物动画退回“初始/未长成”形态。之后可以再次按 E 重新充能（可逆）。
    /// </summary>
    private void ReclaimEnergy()
    {
        if (!energyPlantMode || !consumed)
            return;

        GameplayHUD.EnsureCreated();
        GameplayHUD hud = GameplayHUD.Instance;
        if (hud == null)
            return;

        // 能量已满 → 收不进去，植物保持已充能形态
        if (hud.CurrentEnergy >= GameplayHUD.MaxEnergy)
            return;

        // 状态回到“未充能”：能量球重现
        consumed = false;
        SetHiddenWhenCharged(false);
        SetHighlight(false);

        // 植物动画退回初始/未长成形态
        RevertPlantsToUngrown();

        // 能量还回玩家 + 音效
        hud.AddEnergy(chargeEnergyCost);
        if (AudioManager.Instance != null)
            AudioManager.Instance.PlayEnergyReturn();
    }

    /// <summary>能量植物模式：是否处于“已充能、可被 Q 回收”状态。</summary>
    public bool IsReclaimable => energyPlantMode && consumed;

    /// <summary>能量植物模式：E 时消耗的能量格数（供 UI/编辑器提示使用）。</summary>
    public int ChargeEnergyCost => chargeEnergyCost;

    private void ShowInsufficientEnergyHint()
    {
        GameplayHUD hud = GameplayHUD.Instance;
        if (hud == null)
            return;

        hud.ShowMessage("能量不足，先去找能量球收集能量！");

        if (hintRoutine != null)
            StopCoroutine(hintRoutine);
        hintRoutine = StartCoroutine(HideHintRoutine(hud));
    }

    private IEnumerator HideHintRoutine(GameplayHUD hud)
    {
        yield return new WaitForSecondsRealtime(1.2f);
        if (hud != null)
            hud.HideMessage();
        hintRoutine = null;
    }

    private void SetHiddenWhenCharged(bool hidden)
    {
        if (hiddenWhenCharged == null)
            return;

        for (int i = 0; i < hiddenWhenCharged.Length; i++)
        {
            if (hiddenWhenCharged[i] != null)
                hiddenWhenCharged[i].SetActive(!hidden);
        }
    }

    private void RevertPlantsToUngrown()
    {
        if (plantAnimators == null)
            return;

        for (int i = 0; i < plantAnimators.Length; i++)
        {
            Animator animator = plantAnimators[i];
            if (animator == null || !animator.gameObject.activeInHierarchy)
                continue;

            // 重置所有 Trigger，防止残留的 IsRise/IsMove 在倒放结束后又把植物带回去
            ResetAllPlantTriggers(animator);

            animator.enabled = true;

            // 获取当前状态；只有“正向生长状态”才值得倒放
            AnimatorStateInfo info = animator.IsInTransition(0)
                ? animator.GetNextAnimatorStateInfo(0)
                : animator.GetCurrentAnimatorStateInfo(0);

            int stateHash = info.shortNameHash;
            if (stateHash == 0 || stateHash == NoneStateHash || !ReverseStateNames.TryGetValue(stateHash, out string stateName))
                continue;

            // 当前进度 t：0 = 起点，1 = 终点。回收时把动画时钟从 t 拨回 0。
            float t = Mathf.Clamp01(info.normalizedTime);
            if (t <= 0.001f)
            {
                // 本来就在起点：直接回初始状态即可
                StopAndEnterUngrown(animator);
                continue;
            }

            StartCoroutine(ReversePlaybackFinish(animator, stateName, t));
        }
    }

    /// <summary>
    /// 用参数名精确重置 Animator 上所有 Trigger（IsRise/IsMove/IsReturn…），
    /// 避免旧 Trigger 残留导致植物回落到底后又自动长回去。
    /// </summary>
    private static void ResetAllPlantTriggers(Animator animator)
    {
        if (animator == null)
            return;

        // 常见参数名直接点名清掉（防止遍历参数列表漏掉）
        animator.ResetTrigger("IsRise");
        animator.ResetTrigger("IsMove");
        animator.ResetTrigger("IsReturn");

        // 再把控制器里所有 Trigger 型参数全部清一遍，双保险
        foreach (AnimatorControllerParameter p in animator.parameters)
        {
            if (p.type == AnimatorControllerParameterType.Trigger)
                animator.ResetTrigger(p.name);
        }
    }

    /// <summary>把 Animator 切回“未长成”的初始状态（None），速度恢复 1。</summary>
    private void StopAndEnterUngrown(Animator animator)
    {
        if (animator == null)
            return;

        ResetAllPlantTriggers(animator);
        animator.speed = 1f;
        string targetState = string.IsNullOrEmpty(ungrownStateName) ? "None" : ungrownStateName;
        animator.Play(targetState, 0, 0f);
    }

    /// <summary>
    /// 把动画时钟从 startT（0~1）手动拨回 0，等效“倒放”正向生长 clip。
    /// 每帧调用 Play(状态, 0, t) 让 Animator 在 t 处采样并把位置写进物体，
    /// 因此物体沿原路径反向移动回初始位，耗时与正向动画一致（位移相同、方向相反）。
    /// 不用负 speed，规避 Unity 从终点负速播放瞬间结束的问题。
    /// </summary>
    private IEnumerator ReversePlaybackFinish(Animator animator, string stateName, float startT)
    {
        animator.speed = 0f;   // 停止自走，只按我们拨的时间采样

        // 取该状态对应 clip 的原始时长，保证反向与正向速度一致
        AnimatorClipInfo[] clips = animator.IsInTransition(0)
            ? animator.GetNextAnimatorClipInfo(0)
            : animator.GetCurrentAnimatorClipInfo(0);
        float clipDur = (clips != null && clips.Length > 0 && clips[0].clip != null)
            ? clips[0].clip.length
            : 1f;
        if (clipDur <= 0f)
            clipDur = 1f;

        float t = startT;
        while (animator != null && t > 0f)
        {
            // 每帧往回收一点时间，并强制在该时间点采样
            t -= Time.deltaTime / clipDur;
            animator.Play(stateName, 0, Mathf.Max(t, 0f));
            yield return null;
        }

        if (animator == null)
            yield break;

        // 让最后一帧（起点帧）真正被 Animator 采样稳定，再切走，避免跳变
        yield return null;

        if (animator == null)
            yield break;

        // 切回初始状态前把 Trigger 清干净，防止残留 IsRise 又触发一次正向生长
        StopAndEnterUngrown(animator);

        // 再等一帧，若仍有残留 Trigger 触发了过渡，这里再清一次并压回 None
        yield return null;
        if (animator != null)
            StopAndEnterUngrown(animator);
    }

    /// <summary>是否处于高亮状态。</summary>
    public bool IsHighlighted => highlightOn;

    /// <summary>是否已被使用（consumeOnInteract 用掉后为 true）。供安全点快照还原读取。</summary>
    public bool IsConsumed => consumed;

    /// <summary>
    /// 设置“已使用”状态（由 GGJLevelResetManager 还原安全点快照时调用）。
    /// 还原成未使用时高亮会立即熄灭，之后可再次按 E 交互。
    /// </summary>
    public void SetConsumed(bool value)
    {
        consumed = value;
        if (consumed)
            SetHighlight(false);
    }

    // ---------------------------------------------------------------- 高亮

    private void SetHighlight(bool on, bool reclaimColor = false)
    {
        // 描边颜色随用途切换（E 充能绿 / Q 回收黄）
        if (on && outlineMaterial != null)
            outlineMaterial.SetColor("_Color", reclaimColor ? reclaimHighlightColor : highlightColor);

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

    /// <summary>
    /// 找离玩家最近的、还“能按 E”的可交互物体。
    /// 已充能的能量球（等 Q 回收）不参与 E 目标竞争，避免挡住其它 E 交互。
    /// </summary>
    private static bool TryGetNearestActiveObject(out InteractableObject nearest)
    {
        nearest = null;
        float best = float.MaxValue;

        foreach (InteractableObject obj in ActiveObjects)
        {
            if (obj == null || !obj.enabled)
                continue;
            // 已充能的能量球等待的是 Q 回收，不再作为 E 目标
            if (obj.IsReclaimable)
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

    /// <summary>找离玩家最近的、已充能等待回收的能量植物（Q 目标）。</summary>
    private static bool TryGetNearestReclaimable(out InteractableObject nearest)
    {
        nearest = null;
        float best = float.MaxValue;

        foreach (InteractableObject obj in ActiveObjects)
        {
            if (obj == null || !obj.enabled)
                continue;
            if (!obj.IsReclaimable)
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

    private static bool WasQPressedThisFrame()
    {
#if ENABLE_INPUT_SYSTEM
        var keyboard = Keyboard.current;
        return keyboard != null && keyboard.qKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.Q);
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
