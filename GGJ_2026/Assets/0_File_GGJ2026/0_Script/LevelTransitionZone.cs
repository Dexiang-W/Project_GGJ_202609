using System.Collections;
using StarterAssets;
using UnityEngine;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// 关卡切换触发区（Volume）：玩家进入后自动完成一次“跨场景转场”。
///
/// 流程：锁定操作 → 画面渐黑 → （黑幕中）把玩家设为跨场景保留 →
///       加载 destinationSceneName → 清理目标场景里自带的“演示用玩家/相机” →
///       把玩家放到 spawnObjectName 出生点 → 黑幕淡出 → 交还操作。
///
/// 挂法：给空物体加 BoxCollider（勾 Is Trigger，大小调成区域大小），再加本组件。
/// Inspector 里填目标场景名、出生点物体名、各段时间即可，无需写代码。
///
/// 注意：
///   · 目标场景必须在 File → Build Settings 里启用（可用菜单 GGJ2026/关卡/把关卡加入 Build Settings）；
///   · 场景切换后旧场景里的一切引用都会失效，所以出生点用【物体名】在目标场景里查找，
///     请保证目标场景里名字唯一（如 Level2 里有多余的 SpawnPoint 时改个名或删掉）；
///   · 如果你不是用触发盒，而是想用“交互物体按 E 进下一关”，
///     只需在任意脚本 / UnityEvent 里调用本组件的 TriggerTransition()。
/// </summary>
[DisallowMultipleComponent]
public class LevelTransitionZone : MonoBehaviour
{
    /// <summary>全局是否已有转场在进行（防止两个区域同时触发重复加载）。</summary>
    public static bool AnyTransitionRunning { get; private set; }

    /// <summary>转场结束时释放全局锁（由执行体 LevelSceneRunner 调用）。</summary>
    public static void ReleaseTransitionLock()
    {
        AnyTransitionRunning = false;
    }

    [Header("触碰对象（默认标签 Player，与角色一致）")]
    [Tooltip("触发角色的标签")]
    [SerializeField] private string playerTag = "Player";

    [Header("目标场景")]
    [Tooltip("要加载进入的场景名（须在 File → Build Settings 中启用）")]
    [SerializeField] private string destinationSceneName = "Level2";
    [Tooltip("目标场景里的出生点物体名（跨场景引用会失效，因此按名字查找）")]
    [SerializeField] private string spawnObjectName = "SpawnPoint";
    [Tooltip("找不到 spawnObjectName 时兜底查找的名字")]
    [SerializeField] private string fallbackSpawnObjectName = "Spawn";

    [Header("黑屏文字（可选）")]
    [Tooltip("全黑瞬间显示的提示文字（如“正在前往下一区域…”），留空则不显示。支持 \\n 换行。")]
    [SerializeField, TextArea(1, 3)] private string messageText = "";

    [Header("转场时长（秒）")]
    [Tooltip("画面逐渐变黑所需时长")]
    [SerializeField] private float fadeToBlackSeconds = 0.8f;
    [Tooltip("全黑停留时长（加载场景 + 传送在此期间完成）")]
    [SerializeField] private float blackHoldSeconds = 1.0f;
    [Tooltip("黑幕淡出所需时长")]
    [SerializeField] private float fadeFromBlackSeconds = 0.9f;

    [Header("选项")]
    [Tooltip("传送到出生点后把角色朝向改为出生点朝向（Y 轴）")]
    [SerializeField] private bool useSpawnFacing = true;
    [Tooltip("一个区域只触发一次")]
    [SerializeField] private bool oneShot = false;
    [Tooltip("转场时把（除正在操作玩家外的）场景自带 PlayerGroup 停用，避免画面出现两个玩家。建议保持勾选。")]
    [SerializeField] private bool cleanupExtraPlayers = true;

    private bool inProgress;
    private bool used;

    // ---------------- 编辑器辅助：场景里画出触发范围线框（运行时无实体外观） ----------------

    private void OnDrawGizmos()
    {
        DrawVolumeGizmo(false);
    }

    private void OnDrawGizmosSelected()
    {
        DrawVolumeGizmo(true);
    }

    private void DrawVolumeGizmo(bool selected)
    {
        BoxCollider col = GetComponent<BoxCollider>();
        if (col == null)
            return;

        Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, transform.lossyScale);
        Gizmos.color = selected
            ? new Color(0.2f, 0.75f, 1f, 0.9f)
            : new Color(0.2f, 0.75f, 1f, 0.35f);
        Gizmos.DrawWireCube(col.center, col.size);

        if (selected)
        {
            Gizmos.color = new Color(0.2f, 0.75f, 1f, 0.12f);
            Gizmos.DrawCube(col.center, col.size);
        }
        Gizmos.matrix = Matrix4x4.identity;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(playerTag))
            return;

        TriggerTransition();
    }

    /// <summary>
    /// 手动触发本区域的转场（也可供交互物体 / 机关 UnityEvent 调用）。
    /// </summary>
    public void TriggerTransition()
    {
        if (inProgress || used || AnyTransitionRunning)
            return;

        if (oneShot)
            used = true;

        inProgress = true;
        StartTransition();
    }

    private void StartTransition()
    {
        AnyTransitionRunning = true;

        // 用独立常驻物体执行协程：LoadScene(Single) 会销毁当前场景里的一切（包括本 Zone），
        // 协程挂在场景物体上会被打断，所以转场逻辑放到 DontDestroyOnLoad 的执行体里跑。
        GameObject runner = new GameObject("GGJ_LevelSceneTransitionRunner");
        DontDestroyOnLoad(runner);

        LevelSceneRunner comp = runner.AddComponent<LevelSceneRunner>();
        comp.Begin(
            destinationSceneName,
            spawnObjectName,
            fallbackSpawnObjectName,
            messageText,
            fadeToBlackSeconds,
            blackHoldSeconds,
            fadeFromBlackSeconds,
            useSpawnFacing,
            cleanupExtraPlayers);
    }
}

/// <summary>
/// 关卡转场的实际执行体（挂在临时 DontDestroyOnLoad 物体上，跑完自毁）。
/// 与 LevelTransitionZone 同文件，仅供 LevelTransitionZone 内部使用。
/// </summary>
public class LevelSceneRunner : MonoBehaviour
{
    private string sceneName;
    private string spawnName;
    private string fallbackSpawnName;
    private string messageText;
    private float fadeToBlackSeconds;
    private float blackHoldSeconds;
    private float fadeFromBlackSeconds;
    private bool useSpawnFacing;
    private bool cleanupExtraPlayers;

    private ThirdPersonController player;
    private Camera mainCamera;
    private CameraFollowController camFollow;
    private CharacterController charController;

    // 输入状态恢复用
    private bool hadInputEnabled = true;
    private bool inputWasChanged;
    private bool needCleanupOnDestroy = true;

    public void Begin(
        string targetScene,
        string spawnObject,
        string fallbackSpawn,
        string message,
        float toBlack,
        float holdBlack,
        float fromBlack,
        bool useFacing,
        bool cleanupExtra)
    {
        sceneName = targetScene;
        spawnName = spawnObject;
        fallbackSpawnName = fallbackSpawn;
        messageText = message;
        fadeToBlackSeconds = toBlack;
        blackHoldSeconds = holdBlack;
        fadeFromBlackSeconds = fromBlack;
        useSpawnFacing = useFacing;
        cleanupExtraPlayers = cleanupExtra;

        StartCoroutine(Run());
    }

    private IEnumerator Run()
    {
        // —— 0. 解析玩家与相机 ——
        player = ThirdPersonController.Instance;
        if (player == null)
            player = FindObjectOfType<ThirdPersonController>();

        if (player == null)
        {
            Debug.LogError("[LevelTransitionZone] 场景里没有找到玩家（ThirdPersonController），无法转场。");
            CleanupAndDestroy();
            yield break;
        }

        charController = player.GetComponent<CharacterController>();

        mainCamera = Camera.main;
        if (mainCamera == null)
            mainCamera = player.GetComponentInChildren<Camera>(true);

        if (mainCamera != null)
            camFollow = mainCamera.GetComponent<CameraFollowController>();
        if (camFollow == null)
            camFollow = CameraFollowController.Instance;

        // 目标场景是否可用
        if (string.IsNullOrEmpty(sceneName) || !Application.CanStreamedLevelBeLoaded(sceneName))
        {
            Debug.LogError($"[LevelTransitionZone] 目标场景 “{sceneName}” 不在 Build Settings 中，转场取消。\n" +
                           "请把它加入 Build Settings（可执行菜单 GGJ2026/关卡/把关卡加入 Build Settings）。", this);
            CleanupAndDestroy();
            yield break;
        }

        // —— 1. 锁定操作 ——
        DisablePlayerInput();

        // —— 2. 画面渐黑 ——
        GameplayHUD.EnsureCreated();
        GameplayHUD hud = GameplayHUD.Instance;
        if (hud == null)
        {
            Debug.LogError("[LevelTransitionZone] 无法创建黑幕 HUD，转场取消。");
            RestorePlayerInput();
            CleanupAndDestroy();
            yield break;
        }

        // 黑幕期间屏蔽相机触发区，避免加载/传送瞬间镜头被额外切换
        CameraTriggerVolume.SuppressCameraSwitching = true;
        yield return hud.FadeToBlackRoutine(fadeToBlackSeconds);

        // —— 3. 跨场景保留玩家 / 相机 / 黑幕 ——
        MakePersistent(player.transform.root);
        if (mainCamera != null && mainCamera.transform.root != player.transform.root)
            MakePersistent(mainCamera.transform.root);

        // 清理当前场景里多余的自带玩家（黑幕中隐藏，不留跳变痕迹）
        if (cleanupExtraPlayers)
            DisableExtraPlayers();

        // —— 4. 加载目标场景 ——
        AsyncOperation load = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
        while (load != null && !load.isDone)
            yield return null;

        // 等新场景激活完成
        yield return null;
        yield return null;

        // —— 5. 清理目标场景里自带的多余玩家 / 相机 ——
        if (cleanupExtraPlayers)
        {
            DisableExtraPlayers();
            DisableExtraCameras();
        }

        // —— 6. 找到出生点并传送 ——
        Transform spawn = FindSpawnPoint();
        if (spawn == null)
        {
            Debug.LogWarning($"[LevelTransitionZone] 目标场景里没有找到出生点（{spawnName}），角色将停留在原位。", this);
        }
        else
        {
            if (charController != null) charController.enabled = false;
            player.transform.position = spawn.position;
            if (useSpawnFacing)
                player.transform.rotation = Quaternion.Euler(0f, spawn.eulerAngles.y, 0f);
            if (charController != null) charController.enabled = true;
        }

        // —— 7. 相机就位（默认跟拍玩家） ——
        if (camFollow != null)
        {
            camFollow.SetNormalMode();
            camFollow.SnapToCurrentTarget();
        }

        // 解除相机触发区屏蔽：此时画面仍全黑，若出生点位于某个相机触发区内，
        // 触发区会在下一帧巡检时重新应用并直接就位到该区域的机位（固定视角 / 拉远），
        // 黑幕淡出时玩家看到的就是正确镜头，不需要等淡出完再切。
        CameraTriggerVolume.SuppressCameraSwitching = false;

        // —— 8. 全黑：可选的提示文字 + 停留 ——
        if (!string.IsNullOrEmpty(messageText))
            hud.ShowMessage(messageText);
        else
            hud.HideMessage();

        yield return new WaitForSecondsRealtime(Mathf.Max(0.05f, blackHoldSeconds));

        hud.HideMessage();

        // —— 9. 黑幕淡出 ——
        yield return hud.FadeFromBlackRoutine(fadeFromBlackSeconds);

        // —— 10. 收尾 ——
        RestorePlayerInput();
        CleanupAndDestroy();
    }

    // ---------------------------------------------------------------- 工具

    private void DisablePlayerInput()
    {
        if (player == null)
            return;

        var inputs = player.GetComponent<StarterAssetsInputs>();
        if (inputs != null)
        {
            inputs.MoveInput(Vector2.zero);
            inputs.SprintInput(false);
            inputs.JumpInput(false);
        }

#if ENABLE_INPUT_SYSTEM
        var playerInput = player.GetComponent<PlayerInput>();
        if (playerInput != null)
        {
            hadInputEnabled = playerInput.enabled;
            if (hadInputEnabled)
            {
                playerInput.enabled = false;
                inputWasChanged = true;
            }
        }
#endif
    }

    private void RestorePlayerInput()
    {
        if (player == null)
            return;

        var inputs = player.GetComponent<StarterAssetsInputs>();
        if (inputs != null)
        {
            inputs.MoveInput(Vector2.zero);
            inputs.SprintInput(false);
            inputs.JumpInput(false);
        }

#if ENABLE_INPUT_SYSTEM
        var playerInput = player.GetComponent<PlayerInput>();
        if (playerInput != null && inputWasChanged)
            playerInput.enabled = hadInputEnabled;
#endif

        inputWasChanged = false;
    }

    /// <summary>停用“不是当前操作玩家”的所有自带玩家（PlayerGroup）。</summary>
    private void DisableExtraPlayers()
    {
        if (player == null)
            return;

        ThirdPersonController[] controllers = FindObjectsOfType<ThirdPersonController>(true);
        foreach (ThirdPersonController other in controllers)
        {
            if (other == null || other == player || other.transform.root == player.transform.root)
                continue;

            other.gameObject.SetActive(false);
            Debug.Log($"[LevelTransitionZone] 已停用多余的玩家：{other.gameObject.name}。", other);
        }
    }

    /// <summary>停用不属于“主玩家/主相机”根节点的相机（即目标场景自带相机）。</summary>
    private void DisableExtraCameras()
    {
        Transform cameraRoot = mainCamera != null ? mainCamera.transform.root : (player != null ? player.transform.root : null);
        if (cameraRoot == null)
            return;

        Camera[] cameras = FindObjectsOfType<Camera>(true);
        foreach (Camera cam in cameras)
        {
            if (cam == null || cam == mainCamera)
                continue;
            if (cam.transform.root == cameraRoot)
                continue;
            if (cam.transform.root == player.transform.root)
                continue;
            // 渲染到 RenderTexture 的特效相机（如踩水波纹的顶视 RT 相机）不属于“屏幕相机”，
            // 停用会导致水波纹 RT 停更、水面涟漪失效，因此必须保留。
            if (cam.targetTexture != null)
                continue;

            cam.gameObject.SetActive(false);
        }
    }

    private Transform FindSpawnPoint()
    {
        if (!string.IsNullOrEmpty(spawnName))
        {
            Transform found = FindByName(spawnName);
            if (found != null)
                return found;
        }

        if (!string.IsNullOrEmpty(fallbackSpawnName) && fallbackSpawnName != spawnName)
        {
            Transform found = FindByName(fallbackSpawnName);
            if (found != null)
                return found;
        }

        return null;
    }

    private static Transform FindByName(string name)
    {
        Scene scene = SceneManager.GetActiveScene();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root == null)
                continue;

            if (root.name == name)
                return root.transform;

            Transform found = root.transform.Find(name);
            if (found != null)
                return found;
        }

        // 兜底：跨所有已加载场景按名字找（包括常驻物体，一般用不到）
        GameObject fallback = GameObject.Find(name);
        return fallback != null ? fallback.transform : null;
    }

    private static void MakePersistent(Transform target)
    {
        if (target == null)
            return;

        DontDestroyOnLoad(target.root);
    }

    /// <summary>结束：恢复一切并自毁（保证中途出错也不会卡死操作）。</summary>
    private void CleanupAndDestroy()
    {
        if (needCleanupOnDestroy)
        {
            needCleanupOnDestroy = false;
            CameraTriggerVolume.SuppressCameraSwitching = false;
            RestorePlayerInput();
            LevelTransitionZone.ReleaseTransitionLock();
            Destroy(gameObject);
        }
    }

    private void OnDestroy()
    {
        // 兜底：无论协程怎么退出都保证锁与操作被恢复
        if (needCleanupOnDestroy)
        {
            needCleanupOnDestroy = false;
            CameraTriggerVolume.SuppressCameraSwitching = false;
            RestorePlayerInput();
            LevelTransitionZone.ReleaseTransitionLock();
        }
    }
}
