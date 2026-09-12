using System.Collections;
using UnityEngine;
using StarterAssets;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// “重生点 / 传送点”触发区域（Volume）。
/// 玩法：玩家角色（标签为 Player）进入此区域后——
///   1. 屏幕逐渐变黑（淡入黑幕）；
///   2. 全黑时屏幕中部偏左显示一段本区域配置好的文字（每个区域可编辑不同文字）；
///   3. 玩家被传送回指定的重生点（RespawnPoint）；
///   4. 黑幕淡出恢复画面，文字再停留若干秒后消失。
///
/// 挂法：给空物体加 BoxCollider（勾选 Is Trigger，把大小调成区域大小），再加本组件。
/// 在 Inspector 里填“重生点 Transform / 文字 / 各段时间”即可，无需写代码。
/// </summary>
[DisallowMultipleComponent]
public class RespawnZone : MonoBehaviour
{
    [Header("触碰对象（默认标签 Player，与角色一致）")]
    [SerializeField] private string playerTag = "Player";

    [Header("黑屏文字（每个区域自己填）")]
    [Tooltip("全黑时屏幕中部偏左淡入显示的文字（会随黑幕自动淡显/淡隐）。支持 \\n 换行。")]
    [SerializeField, TextArea(1, 3)] private string messageText = "你回到了重生点……";

    [Header("传送目标")]
    [Tooltip("把玩家传送到的重生点（空物体即可）。Level1 场景里摆好后拖到这里。")]
    [SerializeField] private Transform respawnPoint;

    [Header("流程时长（秒）")]
    [Tooltip("触碰后画面逐渐变黑所需时长")]
    [SerializeField] private float fadeToBlackSeconds = 0.8f;
    [Tooltip("全黑停留时长（文字在此期间显示，玩家被传送）")]
    [SerializeField] private float blackHoldSeconds = 1.2f;
    [Tooltip("黑幕淡出所需时长")]
    [SerializeField] private float fadeFromBlackSeconds = 0.8f;
    [Tooltip("淡出后文字额外停留时长；设为 0 表示淡出一完成就隐藏文字")]
    [SerializeField] private float messageStaySeconds = 1.5f;

    [Header("选项")]
    [Tooltip("传送到重生点后是否把角色朝向重置为重生点的朝向（不勾保持角色原朝向）")]
    [SerializeField] private bool useRespawnFacing = true;
    [Tooltip("勾选后一个区域只触发一次")]
    [SerializeField] private bool oneShot = false;

    private bool inProgress;
    private bool used;

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(playerTag))
            return;

        if (inProgress || used)
            return;

        if (oneShot)
            used = true;

        if (respawnPoint == null)
            Debug.LogWarning($"[RespawnZone] {name} 没有指定重生点（RespawnPoint），传送步骤将被跳过。", this);

        inProgress = true;
        StartCoroutine(RespawnRoutine());
    }

    private IEnumerator RespawnRoutine()
    {
        ThirdPersonController player = ThirdPersonController.Instance;
        if (player == null)
            player = FindObjectOfType<ThirdPersonController>();

        if (player == null)
        {
            Debug.LogWarning("[RespawnZone] 场景里没有玩家（ThirdPersonController）。", this);
            inProgress = false;
            yield break;
        }

        GameplayHUD.EnsureCreated();

        bool hadInput = SetPlayerInput(player, false);

        // 1. 画面逐渐变黑
        yield return GameplayHUD.Instance.FadeToBlackRoutine(fadeToBlackSeconds);

        // 2. 全黑：显示提示文字
        GameplayHUD.Instance.ShowMessage(messageText);
        yield return new WaitForSecondsRealtime(Mathf.Max(0.05f, blackHoldSeconds));

        // 3. 传送回重生点（传送前禁用 CharacterController，避免穿透/抖动）
        CharacterController cc = player.GetComponent<CharacterController>();
        if (respawnPoint != null)
        {
            // 传送期间屏蔽“相机触发区”：防止落点恰好位于某拉远/固定机位的触发区内时，
            // 相机在重生瞬间被额外切换（表现为“重生后相机被拉远”）。
            CameraTriggerVolume.SuppressCameraSwitching = true;

            if (cc != null) cc.enabled = false;
            player.transform.position = respawnPoint.position;
            if (useRespawnFacing)
                player.transform.rotation = respawnPoint.rotation;
            if (cc != null) cc.enabled = true;

            // 先让物理把触发区 进入/离开 事件结算完（此刻屏幕仍全黑，玩家看不到过程）
            yield return new WaitForFixedUpdate();
            yield return null;

            // 相机回到默认“跟拍玩家”机位并瞬间就位（位置+旋转+FOV）：
            // 与重生前的常规跟拍一致，不额外拉远 / 拉近。
            CameraFollowController cam = CameraFollowController.Instance;
            if (cam != null)
            {
                cam.SetNormalMode();
                cam.SnapToCurrentTarget();
            }

            // 解除屏蔽：此刻画面仍全黑，触发区会在当帧巡检时把“落点所在区域”的机位
            // 重新应用并直接就位 —— 淡出时玩家看到的已经是正确的固定视角，不会有镜头跳动。
            CameraTriggerVolume.SuppressCameraSwitching = false;
        }

        // 4. 黑幕淡出（此时机位已确定：落点在触发区内就是该区域的固定视角，否则为普通跟拍）
        yield return GameplayHUD.Instance.FadeFromBlackRoutine(fadeFromBlackSeconds);

        // 文字再停留一小会后隐藏
        if (messageStaySeconds > 0f)
            yield return new WaitForSecondsRealtime(messageStaySeconds);
        GameplayHUD.Instance.HideMessage();

        // 恢复玩家输入
        SetPlayerInput(player, hadInput);
        inProgress = false;
    }

    /// <summary>停用 / 恢复玩家的 PlayerInput（标题流程里 GameFlow 用的是同一个开关）。</summary>
    private static bool SetPlayerInput(ThirdPersonController player, bool enabled)
    {
        if (player == null)
            return true;

        bool previous = true;
#if ENABLE_INPUT_SYSTEM
        var playerInput = player.GetComponent<PlayerInput>();
        if (playerInput != null)
        {
            previous = playerInput.enabled;
            if (enabled && !previous)
            {
                // 恢复前把当前输入清零，避免角色带着旧输入“滑”走
                var inputs = player.GetComponent<StarterAssets.StarterAssetsInputs>();
                if (inputs != null)
                    inputs.MoveInput(Vector2.zero);
            }
            playerInput.enabled = enabled;
        }
#else
        previous = true;
#endif
        return previous;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Collider col = GetComponent<Collider>();
        if (col == null)
            return;

        Gizmos.color = new Color(0.9f, 0.2f, 0.2f, 0.4f);
        DrawGizmoForCollider(col);

        if (respawnPoint != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(respawnPoint.position, 0.3f);
            Gizmos.DrawLine(transform.position, respawnPoint.position);
        }
    }

    private static void DrawGizmoForCollider(Collider col)
    {
        if (col is BoxCollider box)
        {
            Matrix4x4 old = Gizmos.matrix;
            Gizmos.matrix = col.transform.localToWorldMatrix;
            Gizmos.DrawWireCube(box.center, box.size);
            Gizmos.matrix = old;
        }
        else if (col is SphereCollider sphere)
        {
            Gizmos.DrawWireSphere(col.transform.TransformPoint(sphere.center), sphere.radius);
        }
    }
#endif
}
