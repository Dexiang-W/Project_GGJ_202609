using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace StarterAssets
{
    /// <summary>
    /// 玩家“伸手”动画控制器（配合 E / Q 能量交互）。
    /// 行为：
    ///   · 按 E 或 Q（能量交互键）→ 触发 Animator ReachArm 层的 Reach 动画；
    ///   · 站着不动 → 完整播完再淡出；
    ///   · 播放中一旦移动 → Animator 里 “Speed > 阈值” 的过渡条件会立刻打断伸手动画，
    ///     在过渡时间内顺滑混合回正常移动动作（打断由状态机负责）。
    /// 配套：ReachArm 层由菜单 “GGJ2026/设置玩家伸手动画（E/Q）” 自动创建。
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerReachAnimation : MonoBehaviour
    {
        public static PlayerReachAnimation Instance { get; private set; }

        [Header("动画")]
        [Tooltip("伸手动画 clip（来自 RIG_Final5.fbx）")]
        public AnimationClip ReachClip;

        [Tooltip("Animator 里伸手层使用的 Trigger 参数名")]
        public string ReachTriggerName = "Reach";

        [Tooltip("Animator 里用于判断“是否在移动”的参数名（ThirdPersonController 的 Speed）")]
        public string SpeedParameterName = "Speed";

        [Header("播放规则")]
        [Tooltip("脚本自己监听 E / Q 按键（与 InteractableObject 的能量交互键一致）")]
        public bool ListenEAndQ = true;

        [Tooltip("按下瞬间如果玩家正在移动，就不播放伸手动画")]
        public bool SkipWhenMoving = true;

        [Tooltip("判定“正在移动”的速度阈值（Animator 的 Speed 参数尺度）")]
        public float MovingSpeedThreshold = 0.15f;

        [Tooltip("两次触发之间的最小间隔（秒）")]
        public float RetriggerLock = 0.12f;

        [Tooltip("时间缩放为 0（暂停 / 演出）时不响应按键")]
        public bool IgnoreWhenPaused = true;

        [Header("调试")]
        public bool DebugLog = false;

        private Animator _animator;
        private CharacterController _charController;
        private int _reachHash;
        private int _speedHash;
        private float _lastPlayTime = -999f;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _charController = GetComponent<CharacterController>();
            _reachHash = Animator.StringToHash(ReachTriggerName);
            _speedHash = Animator.StringToHash(SpeedParameterName);
            Instance = this;

            if (_animator != null && !HasParameter(_animator, ReachTriggerName))
                Debug.LogWarning("[伸手动画] Animator 里没有 Trigger 参数 “" + ReachTriggerName +
                                 "”，请执行一次菜单 “GGJ2026/设置玩家伸手动画（E/Q）”。", this);
        }

        private static bool HasParameter(Animator animator, string name)
        {
            foreach (AnimatorControllerParameter parameter in animator.parameters)
            {
                if (parameter.name == name) return true;
            }
            return false;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (!ListenEAndQ) return;
            if (IgnoreWhenPaused && Time.timeScale <= 0f) return;

            if (WasEPressed() || WasQPressed())
                Play();
        }

        /// <summary>能量交互真正发生时主动调用（可选）。</summary>
        public static void PlayFromInteraction()
        {
            if (Instance != null) Instance.Play();
        }

        /// <summary>播放一次伸手动画。</summary>
        public void Play()
        {
            if (_animator == null) return;
            if (ReachClip == null)
            {
                if (DebugLog) Debug.LogWarning("[伸手动画] 未指定 ReachClip。", this);
                return;
            }

            if (Time.time - _lastPlayTime < Mathf.Max(0.02f, RetriggerLock)) return;

            if (SkipWhenMoving && IsMoving())
            {
                if (DebugLog) Debug.Log("[伸手动画] 玩家在移动，跳过伸手动画。");
                return;
            }

            _lastPlayTime = Time.time;
            _animator.SetTrigger(_reachHash);

            if (DebugLog) Debug.Log("[伸手动画] 播放 " + ReachClip.name + " (" + ReachClip.length.ToString("F2") + "s)");
        }

        /// <summary>当前是否算“在移动”。</summary>
        public bool IsMoving()
        {
            if (_animator != null && _animator.GetFloat(_speedHash) > MovingSpeedThreshold)
                return true;

            if (_charController != null && _charController.enabled)
            {
                Vector3 v = _charController.velocity;
                v.y = 0f;
                if (v.sqrMagnitude > 0.04f) return true;
            }

            return false;
        }

        private static bool WasEPressed()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            return keyboard != null && keyboard.eKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.E);
#endif
        }

        private static bool WasQPressed()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            return keyboard != null && keyboard.qKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Q);
#endif
        }
    }
}
