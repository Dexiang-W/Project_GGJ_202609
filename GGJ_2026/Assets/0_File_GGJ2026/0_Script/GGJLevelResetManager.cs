using System.Collections;
using System.Collections.Generic;
using StarterAssets;
using UnityEngine;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// 关卡“R 键回到安全点”系统（随 GameplayHUD 一起自动常驻，任意关卡都可用，无需给场景配脚本）。
///
/// 规则：
///   · 每进一关，自动记录一次“本关开局快照”（世界 + 出生点）。
///   · 场景里摆 GGJSafePoint 触发区域后，玩家一进区域就自动刷新为“最近安全点快照”。
///   · 正式游玩中按 R：瞬间把本关世界还原成最近一次快照（植物的生长状态/机关动画等；
///     能量球与能量另见下方规则），玩家瞬移回安全点、耐力回满
///     ——整段还原在 1 秒内完成（纯内存操作，不重新加载场景）。
///
/// 快照内容（这些就是关卡里会被玩家改动的状态）：
///   · 所有物体的激活状态（能量拾取物被吃掉 = SetActive(false)，按“能量球规则”处理）；
///   · 场景所有 Animator 的播放状态（P_LongVine 这类植物/机关生长动画都走 Animator，按 E 触发）；
///   · InteractableObject 的“已使用”状态；
///   · 玩家位置 / 重力朝向。
/// 玩家本体、主相机、HUD/音频等常驻对象不在快照范围内。
///
/// 能量球（EnergyPickup）规则 —— “吸收”算永久进度，不随回溯回滚：
///   · 已经被玩家吸收、且没开“重新生成”的能量球，按 R 后【不会重新生成】（保持消失）；
///   · 还没被吸收的能量球保持原样（照旧留在原地等玩家去捡）；
///   · 开了“重新生成”的能量球（EnergyPickup.respawnAfterPickup）：出现/消失完全由它自己的计时决定，
///     按 R 不干预它 —— 否则“快照恰好拍在它消失的那几秒里”会把已经长回来的球又按回消失状态。
///
/// 能量规则 —— 能量不随回溯回滚：
///   · 按 R 后玩家身上的能量维持“按 R 之前”的数值（不用快照能量覆盖，快照里也不记录能量）；
///   · 回溯期间吸收掉的球带来的能量归玩家，但球不会再生，
///     所以玩家需要自己去把剩下的（还没吸收的）能量球找回来。
/// </summary>
[DisallowMultipleComponent]
public class GGJLevelResetManager : MonoBehaviour
{
    public static GGJLevelResetManager Instance { get; private set; }

    [Header("出生点/安全点")]
    [Tooltip("关卡开场时按这个名字找对象作为初始安全点")]
    [SerializeField] private string spawnPointObjectName = "SpawnPoint";
    [Tooltip("关卡开场找不到 SpawnPoint 时按这个名字找（都不存在则退回玩家当前位置）")]
    [SerializeField] private string fallbackSpawnObjectName = "Spawn";

    [Header("重置反馈（可选）")]
    [Tooltip("按 R 回到安全点后是否在屏幕中左显示提示文字；留空 = 不提示")]
    [SerializeField] private string resetMessageText = "";
    [SerializeField] private float resetMessageHoldSeconds = 1.2f;

    private GameFlowController cachedFlow;
    private ThirdPersonController player;

    /// <summary>当前已初始化的关卡场景名；为 null 表示还没初始化过。 </summary>
    private string activeLevelKey;
    private string initPendingLevel;
    private bool restoring;

    private bool hudWasAlive;
    private Coroutine feedbackRoutine;

    /// <summary>最近一次安全点快照（开局快照兜底，进 GGJSafePoint 后刷新为区域快照）。</summary>
    private CheckpointSnapshot checkpoint;

    public static void EnsureCreated()
    {
        if (Instance != null)
            return;

        GameObject go = new GameObject("GGJ_LevelResetManager");
        DontDestroyOnLoad(go);
        go.AddComponent<GGJLevelResetManager>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        // 兜底：还原中途被销毁时不要留下“相机触发区屏蔽”的全局残留
        CameraTriggerVolume.SuppressCameraSwitching = false;
    }

    private void Update()
    {
        // HUD 不在（标题/结算/清理后）→ 退出正式游玩，清空一切关卡快照
        if (!GameplayHUD.Exists)
        {
            if (hudWasAlive)
                ClearAllState();
            return;
        }

        hudWasAlive = true;
        RefreshReferences();
        TryInitializeForCurrentLevel();

        // 关卡切换后还没完成初始化、或正在还原中：不接受 R
        if (initPendingLevel != null || restoring)
            return;

        if (!CanInteractInGameplay())
            return;

        if (IsResetPressedThisFrame())
            RestoreToLastCheckpoint();
    }

    // ---------------------------------------------------------------- 关卡切换/初始化

    private void TryInitializeForCurrentLevel()
    {
        if (initPendingLevel != null)
            return;

        string sceneName = SceneManager.GetActiveScene().name;
        if (string.IsNullOrEmpty(sceneName) || sceneName == activeLevelKey)
            return;

        // 只有正式游玩（有流程时处于 Playing；独立 Play 关卡时无流程）才初始化
        if (!CanInteractInGameplay())
            return;

        initPendingLevel = sceneName;
        StartCoroutine(InitForSceneRoutine(sceneName));
    }

    private IEnumerator InitForSceneRoutine(string sceneName)
    {
        // 等过渡/淡幕结束、场景稳定一小段时间后再拍“本关开局快照”
        float stable = 0f;
        while (GameplayHUD.Exists && stable < 0.2f)
        {
            if (LevelTransitionZone.AnyTransitionRunning)
                stable = 0f;
            else
                stable += Time.unscaledDeltaTime;
            yield return null;
        }

        if (!GameplayHUD.Exists)
        {
            initPendingLevel = null;
            yield break;
        }

        RefreshReferences();
        checkpoint = BuildCheckpointFromSpawn(sceneName);
        activeLevelKey = sceneName;
        initPendingLevel = null;
        player?.RestoreFullStamina();
    }

    // ---------------------------------------------------------------- 记录安全点（GGJSafePoint 调用）

    /// <summary>玩家进入 GGJSafePoint 区域时调用：拍一张当前世界快照作为“最近安全点”。</summary>
    public bool TryRecordCheckpoint(GGJSafePoint safePoint)
    {
        if (safePoint == null || restoring)
            return false;
        if (!GameplayHUD.Exists)
            return false;

        // 本关尚未初始化（刚到新关卡）或正处于关卡切换初始化中 → 先不记录，交给开局快照兜底
        string sceneName = SceneManager.GetActiveScene().name;
        if (initPendingLevel != null || string.IsNullOrEmpty(activeLevelKey) || sceneName != activeLevelKey)
            return false;

        RefreshReferences();
        if (player == null)
            return false;

        checkpoint = new CheckpointSnapshot
        {
            sceneName = sceneName,
            safePosition = safePoint.GetRespawnPosition(),
            gravityInverted = player.InvertGravity
        };
        CaptureWorldState(checkpoint);
        return true;
    }

    private CheckpointSnapshot BuildCheckpointFromSpawn(string sceneName)
    {
        Transform spawn = FindObjectByNameInActiveScene(spawnPointObjectName);
        if (spawn == null)
            spawn = FindObjectByNameInActiveScene(fallbackSpawnObjectName);

        Vector3 safePos = spawn != null
            ? spawn.position
            : (player != null ? player.transform.position : Vector3.zero);

        CheckpointSnapshot snap = new CheckpointSnapshot
        {
            sceneName = sceneName,
            safePosition = safePos,
            gravityInverted = player != null && player.InvertGravity
        };
        CaptureWorldState(snap);
        return snap;
    }

    // ---------------------------------------------------------------- 按 R 还原

    private void RestoreToLastCheckpoint()
    {
        if (checkpoint == null || restoring)
            return;

        // 快照属于别的场景（理论不会到这一步，多一道保险）
        if (checkpoint.sceneName != SceneManager.GetActiveScene().name)
            return;

        RefreshReferences();
        if (player == null)
            return;

        restoring = true;
        StartCoroutine(RestoreRoutine(checkpoint, player));
    }

    private IEnumerator RestoreRoutine(CheckpointSnapshot snap, ThirdPersonController target)
    {
        try
        {
            // 传送期间屏蔽“相机触发区”，防止落点刚好在某个触发区时被额外切镜
            CameraTriggerVolume.SuppressCameraSwitching = true;
            yield return null;

            // 1) 世界状态还原（物体激活 / 机关“已用”状态 / 植物动画）
            RestoreWorldState(snap);

            // 2) 玩家瞬移回安全点并清空运动残留、耐力回满
            target.PlaceAtSafePoint(snap.safePosition, snap.gravityInverted);
            ClearPlayerInputs(target);

            // 让物理把触发区进入/离开事件先结算完，相机再吸附
            yield return new WaitForFixedUpdate();
            yield return null;

            // 3) 相机回到默认跟拍机位并瞬间就位
            CameraFollowController cam = CameraFollowController.Instance;
            if (cam != null)
            {
                cam.SetNormalMode();
                cam.SnapToCurrentTarget();
            }

            // 解除屏蔽：安全点若恰好位于某个相机触发区内，立刻让触发区重新判定一次，
            // 重新应用并立刻就位到该区域的机位（固定视角 / 拉远），而不是一直停在普通跟拍。
            CameraTriggerVolume.SuppressCameraSwitching = false;
            CameraTriggerVolume.ReevaluateAll();
        }
        finally
        {
            CameraTriggerVolume.SuppressCameraSwitching = false;
            restoring = false;
        }

        // 4) 能量不做任何处理：按 R 只还原“世界”，玩家身上的能量维持按 R 之前的状态
        //    （所以回溯后要靠玩家自己去找还没被吸收的能量球来补能量；见类头“能量规则”）
        if (!GameplayHUD.Exists)
            yield break;

        GameplayHUD hud = GameplayHUD.Instance;

        // 5) 可选提示
        if (!string.IsNullOrEmpty(resetMessageText))
            ShowResetFeedback(hud);
    }

    private void RestoreWorldState(CheckpointSnapshot snap)
    {
        // 这些能量球（含子物体）不参与“激活状态还原”：
        //   · 已被吸收、且不会自己重新生成 → 保持消失；
        //   · 开了“重新生成”的 → 由它自己的计时决定何时出现，R 不干预。
        HashSet<GameObject> untouchedPickupRoots = CollectUntouchedPickupRoots(snap);

        // ① 激活状态（父先子后的顺序 SetActive，不会受父子关系影响）
        List<NodeState> nodes = snap.nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            NodeState n = nodes[i];
            if (n == null || n.gameObject == null)
                continue;
            // 能量球：已吸收的保持消失、可再生的交给它自己计时 —— 都不还原
            if (IsUnderAny(n.gameObject.transform, untouchedPickupRoots))
                continue;
            if (n.gameObject.activeSelf != n.wasActive)
                n.gameObject.SetActive(n.wasActive);
        }

        // ② 可交互物体“已使用”状态
        List<InteractableState> interacts = snap.interactables;
        for (int i = 0; i < interacts.Count; i++)
        {
            InteractableState s = interacts[i];
            if (s == null || s.target == null)
                continue;
            s.target.SetConsumed(s.wasConsumed);
        }

        // ③ Animator 播放状态（植物生长/机关动画等）
        List<AnimatorState> anims = snap.animators;
        for (int i = 0; i < anims.Count; i++)
        {
            AnimatorState s = anims[i];
            if (s == null || s.animator == null)
                continue;
            RestoreAnimator(s);
        }
    }

    private static void RestoreAnimator(AnimatorState s)
    {
        Animator animator = s.animator;

        // 物体本身处于停用状态 → 恢复停用，之后动画回到默认初态（无法强制跳帧，跳过即可）
        if (!animator.gameObject.activeInHierarchy)
        {
            animator.enabled = s.wasEnabled;
            return;
        }

        animator.enabled = true;

        // ① 参数还原（先还原 bool/float/int，让状态机停在快照时刻的判断条件上）
        for (int p = 0; p < s.paramCount && p < s.paramHashes.Length; p++)
        {
            switch (s.paramTypes[p])
            {
                case AnimatorControllerParameterType.Float:
                    animator.SetFloat(s.paramHashes[p], s.paramFloats[p]);
                    break;
                case AnimatorControllerParameterType.Int:
                    animator.SetInteger(s.paramHashes[p], s.paramInts[p]);
                    break;
                case AnimatorControllerParameterType.Bool:
                    animator.SetBool(s.paramHashes[p], s.paramBools[p]);
                    break;
                case AnimatorControllerParameterType.Trigger:
                    // Trigger 无法读值，统一清掉，避免残留的触发把植物“啪”地带到生长状态
                    animator.ResetTrigger(s.paramHashes[p]);
                    break;
            }
        }

        // ② 直接跳到快照时的状态/进度
        int layers = Mathf.Min(animator.layerCount, s.stateHashes.Length);
        for (int layer = 0; layer < layers; layer++)
        {
            int hash = s.stateHashes[layer];
            if (hash == 0)
                continue;
            animator.Play(hash, layer, s.normalizedTimes[layer]);
        }

        animator.speed = s.speed;
        // 快照时 Animator 是关闭的 → 还原完姿态后继续保持关闭（画面冻结在那一刻）
        animator.enabled = s.wasEnabled;
    }

    // ---------------------------------------------------------------- 能量球（能量拾取物）

    /// <summary>
    /// 本关“按 R 时不还原激活状态”的能量球根节点：
    ///   · 已经被玩家吸收、且不会自己重新生成的球 —— 吸收算永久进度，按 R 后保持消失（不把它“叼”回来）；
    ///   · 开了“重新生成”的球（EnergyPickup.CanRespawn）—— 出现/消失完全由它自己的计时决定，
    ///     这里一并跳过：否则快照恰好拍在它消失的那几秒里时，会把已经长回来的球又按回消失状态、再也回不来。
    /// </summary>
    private static HashSet<GameObject> CollectUntouchedPickupRoots(CheckpointSnapshot snap)
    {
        HashSet<GameObject> roots = null;
        List<EnergyPickup> pickups = snap.pickups;
        for (int i = 0; i < pickups.Count; i++)
        {
            EnergyPickup pickup = pickups[i];
            if (pickup == null)
                continue;
            if (!pickup.IsCollected && !pickup.CanRespawn)
                continue;

            if (roots == null)
                roots = new HashSet<GameObject>();
            roots.Add(pickup.gameObject);
        }
        return roots;
    }

    /// <summary>节点自己或任意父级是否属于“已吸收的能量球”（整棵子树都不参与还原）。</summary>
    private static bool IsUnderAny(Transform t, HashSet<GameObject> roots)
    {
        if (roots == null || roots.Count == 0)
            return false;

        while (t != null)
        {
            if (roots.Contains(t.gameObject))
                return true;
            t = t.parent;
        }
        return false;
    }

    // ---------------------------------------------------------------- 世界快照采集

    private void CaptureWorldState(CheckpointSnapshot snap)
    {
        snap.nodes.Clear();
        snap.animators.Clear();
        snap.interactables.Clear();
        snap.pickups.Clear();

        Scene scene = SceneManager.GetActiveScene();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root == null || IsSkippedRoot(root))
                continue;
            CaptureNode(root.transform, snap);
        }

        // InteractableObject 的“已使用”状态单独采集（可能自身已停用也要纳入）
        InteractableObject[] interactables = FindObjectsOfType<InteractableObject>(true);
        for (int i = 0; i < interactables.Length; i++)
        {
            InteractableObject io = interactables[i];
            if (io == null)
                continue;
            Transform root = io.transform.root;
            if (root != null && IsSkippedRoot(root.gameObject))
                continue;
            snap.interactables.Add(new InteractableState { target = io, wasConsumed = io.IsConsumed });
        }

        // 能量球（能量拾取物）单独采集：还原时用来识别“已经被玩家吸收的球”（它们不再生成）
        EnergyPickup[] pickups = FindObjectsOfType<EnergyPickup>(true);
        for (int i = 0; i < pickups.Length; i++)
        {
            EnergyPickup pickup = pickups[i];
            if (pickup == null)
                continue;
            Transform root = pickup.transform.root;
            if (root != null && IsSkippedRoot(root.gameObject))
                continue;
            snap.pickups.Add(pickup);
        }
    }

    private static void CaptureNode(Transform t, CheckpointSnapshot snap)
    {
        if (t == null)
            return;

        snap.nodes.Add(new NodeState(t.gameObject));

        Animator animator = t.GetComponent<Animator>();
        if (animator != null)
        {
            AnimatorState s = new AnimatorState
            {
                animator = animator,
                wasEnabled = animator.enabled,
                speed = animator.speed
            };

            int layers = animator.layerCount;
            s.stateHashes = new int[layers];
            s.normalizedTimes = new float[layers];
            for (int l = 0; l < layers; l++)
            {
                AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(l);
                s.stateHashes[l] = info.shortNameHash;
                s.normalizedTimes[l] = info.normalizedTime;
            }

            // 参数快照（避免“还原到静止状态后又被残留的 bool/trigger 立刻带回去”）
            AnimatorControllerParameter[] ps = animator.parameters;
            s.paramCount = ps != null ? ps.Length : 0;
            s.paramHashes = new int[s.paramCount];
            s.paramTypes = new AnimatorControllerParameterType[s.paramCount];
            s.paramFloats = new float[s.paramCount];
            s.paramInts = new int[s.paramCount];
            s.paramBools = new bool[s.paramCount];
            for (int p = 0; p < s.paramCount; p++)
            {
                AnimatorControllerParameter param = ps[p];
                s.paramHashes[p] = param.nameHash;
                s.paramTypes[p] = param.type;
                switch (param.type)
                {
                    case AnimatorControllerParameterType.Float:
                        s.paramFloats[p] = animator.GetFloat(param.nameHash);
                        break;
                    case AnimatorControllerParameterType.Int:
                        s.paramInts[p] = animator.GetInteger(param.nameHash);
                        break;
                    case AnimatorControllerParameterType.Bool:
                        s.paramBools[p] = animator.GetBool(param.nameHash);
                        break;
                }
            }
            snap.animators.Add(s);
        }

        for (int i = 0; i < t.childCount; i++)
            CaptureNode(t.GetChild(i), snap);
    }

    private static bool IsSkippedRoot(GameObject root)
    {
        if (root == null)
            return true;

        // 玩家本体 / 常驻系统 / 相机相关根节点不纳入（它们的树里通常是运动系统而非关卡机关）
        if (root.GetComponentInChildren<ThirdPersonController>(true) != null)
            return true;
        if (root.GetComponentInChildren<Camera>(true) != null)
            return true;
        if (root.GetComponent<GameFlowController>() != null)
            return true;
        if (root.GetComponent<GameplayHUD>() != null)
            return true;
        if (root.GetComponent<AudioManager>() != null)
            return true;
        if (root.GetComponent<GGJLevelResetManager>() != null)
            return true;
        return false;
    }

    // ---------------------------------------------------------------- 工具

    private bool CanInteractInGameplay()
    {
        if (!GameplayHUD.Exists)
            return false;
        if (PauseMenuManager.IsPaused)
            return false;
        if (LevelTransitionZone.AnyTransitionRunning)
            return false;
        if (GameTitleRestart.IsRestartInProgress)
            return false;

        RefreshReferences();

        if (cachedFlow != null && cachedFlow.CurrentState != GameFlowController.GameFlowState.Playing)
            return false;

        // 结局报幕中不给重置
        EndingSequencePlayer ending = FindObjectOfType<EndingSequencePlayer>();
        if (ending != null && ending.IsPlaying)
            return false;

        if (player == null)
            return false;

#if ENABLE_INPUT_SYSTEM
        PlayerInput input = player.GetComponent<PlayerInput>();
        if (input != null && !input.enabled)
            return false;
#endif
        return true;
    }

    private void RefreshReferences()
    {
        if (player == null)
        {
            player = ThirdPersonController.Instance;
            if (player == null)
                player = FindObjectOfType<ThirdPersonController>();
        }

        if (cachedFlow == null)
            cachedFlow = FindObjectOfType<GameFlowController>();
    }

    private void ClearAllState()
    {
        hudWasAlive = false;
        cachedFlow = null;
        player = null;
        activeLevelKey = null;
        initPendingLevel = null;
        checkpoint = null;
        restoring = false;
    }

    private static void ClearPlayerInputs(ThirdPersonController target)
    {
        if (target == null)
            return;

        StarterAssetsInputs inputs = target.GetComponent<StarterAssetsInputs>();
        if (inputs == null)
            return;

        inputs.MoveInput(Vector2.zero);
        inputs.SprintInput(false);
        inputs.JumpInput(false);
    }

    private void ShowResetFeedback(GameplayHUD hud)
    {
        if (feedbackRoutine != null)
            StopCoroutine(feedbackRoutine);

        hud.ShowMessage(resetMessageText);
        feedbackRoutine = StartCoroutine(HideFeedbackRoutine(hud));
    }

    private IEnumerator HideFeedbackRoutine(GameplayHUD hud)
    {
        yield return new WaitForSecondsRealtime(Mathf.Max(0f, resetMessageHoldSeconds));
        if (hud != null)
            hud.HideMessage();
        feedbackRoutine = null;
    }

    private static bool IsResetPressedThisFrame()
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        return keyboard != null && keyboard.rKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.R);
#endif
    }

    private static Transform FindObjectByNameInActiveScene(string targetName)
    {
        if (string.IsNullOrEmpty(targetName))
            return null;

        Scene scene = SceneManager.GetActiveScene();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root == null)
                continue;
            if (root.name == targetName)
                return root.transform;

            Transform found = FindInChildren(root.transform, targetName);
            if (found != null)
                return found;
        }
        return null;
    }

    private static Transform FindInChildren(Transform parent, string name)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name == name)
                return child;

            Transform deeper = FindInChildren(child, name);
            if (deeper != null)
                return deeper;
        }
        return null;
    }

    // ---------------------------------------------------------------- 快照数据

    private class CheckpointSnapshot
    {
        public string sceneName;
        public Vector3 safePosition;
        public bool gravityInverted;

        public readonly List<NodeState> nodes = new List<NodeState>();
        public readonly List<AnimatorState> animators = new List<AnimatorState>();
        public readonly List<InteractableState> interactables = new List<InteractableState>();

        /// <summary>本关所有能量球（用于还原时跳过“已被吸收”的那几颗）。</summary>
        public readonly List<EnergyPickup> pickups = new List<EnergyPickup>();
    }

    private class NodeState
    {
        public readonly GameObject gameObject;
        public readonly bool wasActive;

        public NodeState(GameObject go)
        {
            gameObject = go;
            wasActive = go != null && go.activeSelf;
        }
    }

    private class AnimatorState
    {
        public Animator animator;
        public bool wasEnabled;
        public float speed;
        public int[] stateHashes;
        public float[] normalizedTimes;

        // 参数快照（bool/float/int/trigger）
        public int paramCount;
        public int[] paramHashes;
        public AnimatorControllerParameterType[] paramTypes;
        public float[] paramFloats;
        public int[] paramInts;
        public bool[] paramBools;
    }

    private class InteractableState
    {
        public InteractableObject target;
        public bool wasConsumed;
    }
}
