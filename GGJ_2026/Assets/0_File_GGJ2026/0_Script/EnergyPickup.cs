using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// 能量拾取物（“能量球”，触碰后能量 +1 格，浅绿圆点变亮绿）。
/// 用法：给空物体加 BoxCollider（Is Trigger 勾选，大小即拾取范围），再加本组件即可；
/// 也可以直接挂到场景里某个“能量水晶 / 能量柱”样式的物体上（该物本身需是 Trigger 或另配一个 Trigger 子物体）。
///
/// 【重新生成（可选，默认关闭）】
///   · 想做成“可再生资源”：勾上 respawnAfterPickup，并把 respawnSeconds 填成想要的间隔（秒）——
///     被吸收后过这么多秒，球会原地重新出现、可以再次吸收；
///   · 默认不勾：吃掉就永久消失（一次性资源）；
///   · 重新出现时用 OnRespawn 事件可以接音效 / 特效 / “长回来”的动画。
///   注意：重新生成的位置就是原来那个位置，如果玩家一直站在拾取范围里不动，
///   球一出现就会立刻被再次吸收（看起来是一闪一闪）——这是设计如此，把球放在玩家不会久留的位置即可。
///
/// 与 R 键回溯（GGJLevelResetManager）的关系：
///   · 被玩家吸收（oneShot 拾取）后本组件会记下 IsCollected = true；
///   · 没有开启“重新生成”的球：按 R 还原关卡时【已经被吸收的球不会重新生成】（吸收算永久进度），
///     还没被吸收的球保持原样；玩家的能量也不会被回溯回滚，
///     所以回溯后要靠玩家自己去找剩下的能量球来补能量。
///   · 开启了“重新生成”的球：什么时候在、什么时候不在完全由它自己的计时决定（见 CanRespawn），
///     按 R 不干预它 —— 否则“快照恰好拍在它消失的那几秒里”会把已经长回来的球又按回消失状态，再也回不来。
///   · 想让所有球都回到初始状态：重新加载关卡即可（切关卡 / 重开流程会重建场景对象）。
/// </summary>
[DisallowMultipleComponent]
public class EnergyPickup : MonoBehaviour
{
    [Header("触碰对象")]
    [SerializeField] private string playerTag = "Player";

    [Header("拾取设置")]
    [Tooltip("一次拾取增加的能量格数（默认 1）")]
    [SerializeField] private int energyAmount = 1;
    [Tooltip("拾取一次后是否失效（勾选后碰到一次就被吃掉，隐藏并停用）")]
    [SerializeField] private bool oneShot = true;

    [Header("重新生成（可选，默认关闭）")]
    [Tooltip("被吸收后是否重新生成。\n" +
             "取消勾选（默认）：吃掉就永久消失，按 R 回溯也不会回来（一次性资源）。\n" +
             "勾选：过 respawnSeconds 秒原地重新出现，可以再次被吸收（可再生资源）。\n" +
             "只在勾选了上面“拾取一次后失效（oneShot）”时才有意义。")]
    [SerializeField] private bool respawnAfterPickup = false;

    [Tooltip("重新生成的间隔（秒）：从被吸收那一刻开始算，过这么多秒球再次出现。\n" +
             "只在勾选了“被吸收后重新生成”时生效；填 0 或负数 = 不自动生成。")]
    [SerializeField] private float respawnSeconds = 8f;

    [Header("事件（可选，用来接音效 / 特效 / 动画）")]
    [Tooltip("能量球重新生成、再次可以吸收时触发一次")]
    public UnityEvent OnRespawn = new UnityEvent();

    /// <summary>是否已经被玩家吸收（oneShot 拾取后为 true，重新生成后回到 false）。</summary>
    private bool collected;

    /// <summary>已经被玩家吸收（且不会自己重新生成）：按 R 回溯时这颗球保持消失。</summary>
    public bool IsCollected => collected;

    /// <summary>
    /// 这颗球被吸收后会不会自己重新生成（勾了“重新生成”且间隔有效）。
    /// 这类球的出现 / 消失完全由自己的计时决定，按 R 回溯时不要去干预它（见 GGJLevelResetManager）。
    /// </summary>
    public bool CanRespawn => respawnAfterPickup && respawnSeconds > 0f;

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(playerTag))
            return;

        GameplayHUD.EnsureCreated();
        GameplayHUD.Instance.AddEnergy(energyAmount);

        // 拾取音效：SFX_Energy_Pickup（GameplayHUD.EnsureCreated 会同时确保音频系统就绪）
        if (AudioManager.Instance != null)
            AudioManager.Instance.PlayPickup();

        if (oneShot)
        {
            // 吃掉：整个物体（连同子物体视觉）隐藏，不会再触发（需要“消失动画”可自行扩展）
            // 同时记下“已被吸收”：按 R 回溯时这颗球不再重新生成（见 GGJLevelResetManager）
            collected = true;
            gameObject.SetActive(false);

            // 可选：过 respawnSeconds 秒原地重新出现（默认不生成）。
            // 物体已经被停用，自身的 Update / 协程都会停，所以计时交给常驻的小助手 GGJRespawnTimer，
            // 到点它会 SetActive(true)，再走下面的 OnEnable 复位。
            if (CanRespawn)
                GGJRespawnTimer.Schedule(gameObject, respawnSeconds);
        }
    }

    private void OnEnable()
    {
        // 首次激活（关卡开始）时 collected 还是 false，不需要做什么；
        // 只有“被吸收之后又被激活”（自动重新生成 / 层级被重新激活）才复位成“可以再次吸收”。
        if (!collected)
            return;

        collected = false;
        OnRespawn?.Invoke();
    }
}
