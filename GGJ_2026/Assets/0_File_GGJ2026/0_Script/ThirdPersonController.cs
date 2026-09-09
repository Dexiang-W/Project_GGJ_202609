using UnityEngine;
#if ENABLE_INPUT_SYSTEM 
using UnityEngine.InputSystem;
#endif

namespace StarterAssets
{
    [RequireComponent(typeof(CharacterController))]
#if ENABLE_INPUT_SYSTEM 
    [RequireComponent(typeof(PlayerInput))]
#endif
    public class ThirdPersonController : MonoBehaviour
    {
        /// <summary>
        /// 运行时玩家入口。玩家在 Bandeng_Test 场景里被 DontDestroyOnLoad 保留，
        /// Level1 里的脚本（如 CameraTriggerVolume）无法在 Inspector 直接拖它，运行时用这个静态引用解析。
        /// </summary>
        public static ThirdPersonController Instance { get; private set; }

        /// <summary>当前是否处于“自由移动”状态（WASD 全向移动，默认 false = 强制横向只左右）。</summary>
        public bool FreeMovementEnabled => freeMoveDepth > 0;

        [Header("Player")]
        [Tooltip("角色朝右时的Y轴旋转角度（默认90°）")]
        public float RightFacingAngle = 90f;
        [Tooltip("角色朝左时的Y轴旋转角度（默认-90°）")]
        public float LeftFacingAngle = -90f;

        [Tooltip("Move speed of the character in m/s")]
        public float MoveSpeed = 2.0f;
        [Tooltip("Sprint speed of the character in m/s")]
        public float SprintSpeed = 5.335f;
        [Tooltip("How fast the character turns to face movement direction")]
        [Range(0.0f, 0.3f)]
        public float RotationSmoothTime = 0.12f;
        [Tooltip("Acceleration and deceleration")]
        public float SpeedChangeRate = 10.0f;

        [Header("Stamina (冲刺耐力)")]
        [Tooltip("耐力上限")]
        [SerializeField] private float maxStamina = 100f;
        [Tooltip("按住 Shift 冲刺（落地跑动）时每秒消耗的耐力")]
        [SerializeField] private float sprintStaminaDrainPerSecond = 22f;
        [Tooltip("停止冲刺后，经过多少秒才开始恢复耐力")]
        [SerializeField] private float staminaRegenDelaySeconds = 0.8f;
        [Tooltip("耐力恢复速度（每秒）")]
        [SerializeField] private float staminaRegenPerSecond = 28f;
        [Tooltip("耐力耗尽后需恢复到多少才允许再次冲刺（防“走一步跑一步”反复抖动）")]
        [SerializeField] private float staminaRestartThreshold = 35f;

        /// <summary>当前耐力（0 ~ MaxStamina），供 HUD / 其它系统读取。</summary>
        public float CurrentStamina { get; private set; }

        /// <summary>耐力上限（供 HUD / 其它系统读取）。</summary>
        public float MaxStamina => maxStamina;

        public AudioClip LandingAudioClip;
        public AudioClip[] FootstepAudioClips;
        [Range(0, 1)] public float FootstepAudioVolume = 0.5f;

        [Space(10)]
        [Tooltip("The height the player can jump")]
        public float JumpHeight = 1.2f;
        [Tooltip("The character uses its own gravity value. The engine default is -9.81f")]
        public float Gravity = -15.0f;
        [Tooltip("Invert gravity so the player falls upward")]
        public bool InvertGravity = false;
        [Tooltip("Optional visual root to flip when gravity is inverted. If empty, the animator root is used")]
        public Transform CharacterModelRoot;

        [Space(10)]
        [Tooltip("Time required to pass before being able to jump again. Set to 0f to instantly jump again")]
        public float JumpTimeout = 0.50f;
        [Tooltip("Time required to pass before entering the fall state. Useful for walking down stairs")]
        public float FallTimeout = 0.15f;

        [Header("Player Grounded")]
        [Tooltip("If the character is grounded or not. Not part of the CharacterController built in grounded check")]
        public bool Grounded = true;
        [Tooltip("Useful for rough ground")]
        public float GroundedOffset = -0.14f;
        [Tooltip("The radius of the grounded check. Should match the radius of the CharacterController")]
        public float GroundedRadius = 0.28f;
        [Tooltip("What layers the character uses as ground")]
        public LayerMask GroundLayers;

        [Header("Cinemachine")]
        [Tooltip("The follow target set in the Cinemachine Virtual Camera that the camera will follow")]
        public GameObject CinemachineCameraTarget;
        [Tooltip("How far in degrees can you move the camera up")]
        public float TopClamp = 70.0f;
        [Tooltip("How far in degrees can you move the camera down")]
        public float BottomClamp = -30.0f;
        [Tooltip("Additional degress to override the camera. Useful for fine tuning camera position when locked")]
        public float CameraAngleOverride = 0.0f;
        [Tooltip("For locking the camera position on all axis")]
        public bool LockCameraPosition = false;

        // cinemachine
        private float _cinemachineTargetYaw;
        private float _cinemachineTargetPitch;

        // player
        private float _speed;
        private float _animationBlend;
        private float _targetRotation = 0.0f;
        private float _rotationVelocity;
        private float _verticalVelocity;
        private float _terminalVelocity = 53.0f;

        // timeout deltatime
        private float _jumpTimeoutDelta;
        private float _fallTimeoutDelta;

        // stamina（冲刺耐力）
        private bool staminaExhausted;
        private bool staminaReleasePending;
        private float staminaTimeSinceLastDrain = float.MaxValue;

        // animation IDs
        private int _animIDSpeed;
        private int _animIDGrounded;
        private int _animIDJump;
        private int _animIDFreeFall;
        private int _animIDMotionSpeed;

#if ENABLE_INPUT_SYSTEM 
        private PlayerInput _playerInput;
#endif
        private Animator _animator;
        private CharacterController _controller;
        private StarterAssetsInputs _input;
        private GameObject _mainCamera;

        private const float _threshold = 0.01f;

        private bool _hasAnimator;
        private bool _wasGravityInverted;

        // 自由移动区域计数：进入 CameraTriggerVolume（勾了解锁WASD）加一、离开减一，
        // 用计数而不是布尔，避免玩家同时待在多个解锁区域内、离开其中一个就被错误锁回。
        private int freeMoveDepth;

        /// <summary>进入一个“自由移动区”：+1，期间 WASD 全向移动。</summary>
        public void EnableFreeMovement() => freeMoveDepth++;

        /// <summary>离开一个“自由移动区”：-1（减到 0 后恢复默认的强制横向移动）。</summary>
        public void DisableFreeMovement() => freeMoveDepth = Mathf.Max(0, freeMoveDepth - 1);

        private bool IsCurrentDeviceMouse
        {
            get
            {
#if ENABLE_INPUT_SYSTEM
                return _playerInput.currentControlScheme == "KeyboardMouse";
#else
                return false;
#endif
            }
        }

        private void Awake()
        {
            if (_mainCamera == null)
            {
                _mainCamera = GameObject.FindGameObjectWithTag("MainCamera");
            }

            if (Instance == null)
                Instance = this;

            CurrentStamina = maxStamina;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        private void Start()
        {
            _cinemachineTargetYaw = CinemachineCameraTarget.transform.rotation.eulerAngles.y;

            _hasAnimator = TryGetComponent(out _animator);
            _controller = GetComponent<CharacterController>();
            _input = GetComponent<StarterAssetsInputs>();
#if ENABLE_INPUT_SYSTEM
            _playerInput = GetComponent<PlayerInput>();
#else
            Debug.LogError("Starter Assets package is missing dependencies. Please use Tools/Starter Assets/Reinstall Dependencies to fix it");
#endif

            AssignAnimationIDs();

            _jumpTimeoutDelta = JumpTimeout;
            _fallTimeoutDelta = FallTimeout;
            _wasGravityInverted = InvertGravity;

            ApplyInversionVisuals();
        }

        private void Update()
        {
            _hasAnimator = TryGetComponent(out _animator);

            if (_wasGravityInverted != InvertGravity)
            {
                _wasGravityInverted = InvertGravity;
                ApplyInversionVisuals();
            }

            JumpAndGravity();
            GroundedCheck();
            Move();
        }

        private void LateUpdate()
        {
            CameraRotation();
        }

        public void InvertGravityDirection()
        {
            InvertGravity = !InvertGravity;
        }

        /// <summary>
        /// 把角色竖直弹起（弹跳板 BouncePad 等使用）。沿当前世界的“上”方向给速度，
        /// 兼容 InvertGravity（翻转重力时会在翻转后的“上”方向弹）。数值为速率绝对值。
        /// </summary>
        public void LaunchUp(float strength)
        {
            if (strength <= 0f)
                return;

            float target = Mathf.Abs(strength);
            if (_verticalVelocity < target)
            {
                _verticalVelocity = target;
                _fallTimeoutDelta = FallTimeout;
            }
        }

        /// <summary>耐力直接回满（进入正式关卡 / 回到安全点时调用）。</summary>
        public void RestoreFullStamina()
        {
            CurrentStamina = maxStamina;
            staminaExhausted = false;
            staminaReleasePending = false;
            staminaTimeSinceLastDrain = float.MaxValue;
        }

        /// <summary>
        /// 回到安全点/出生点：瞬移到指定点并清空速度、自由移动计数等运动残留
        /// （GGJLevelResetManager 按 R 重置时使用；重力按安全点记录状态恢复）。
        /// </summary>
        public void PlaceAtSafePoint(Vector3 worldPosition, bool gravityInvertedAfter)
        {
            if (InvertGravity != gravityInvertedAfter)
            {
                InvertGravity = gravityInvertedAfter;
                _wasGravityInverted = gravityInvertedAfter;
                ApplyInversionVisuals();
            }

            if (_controller != null)
                _controller.enabled = false;

            transform.position = worldPosition;
            _verticalVelocity = 0f;
            _fallTimeoutDelta = FallTimeout;
            _jumpTimeoutDelta = 0f; // 允许回到安全点后立即起跳
            freeMoveDepth = 0;
            _speed = 0f;
            _animationBlend = 0f;

            if (_controller != null)
                _controller.enabled = true;

            RestoreFullStamina();
        }

        private void AssignAnimationIDs()
        {
            _animIDSpeed = Animator.StringToHash("Speed");
            _animIDGrounded = Animator.StringToHash("Grounded");
            _animIDJump = Animator.StringToHash("Jump");
            _animIDFreeFall = Animator.StringToHash("FreeFall");
            _animIDMotionSpeed = Animator.StringToHash("MotionSpeed");
        }

        private void GroundedCheck()
        {
            Vector3 upDirection = GetUpDirection();
            Vector3 spherePosition = transform.position + (upDirection * GroundedOffset);
            Grounded = Physics.CheckSphere(spherePosition, GroundedRadius, GroundLayers,
                QueryTriggerInteraction.Ignore);

            if (_hasAnimator)
            {
                _animator.SetBool(_animIDGrounded, Grounded);
            }
        }

        private void CameraRotation()
        {
            if (_input.look.sqrMagnitude >= _threshold && !LockCameraPosition)
            {
                float deltaTimeMultiplier = IsCurrentDeviceMouse ? 1.0f : Time.deltaTime;
                _cinemachineTargetYaw += _input.look.x * deltaTimeMultiplier;
                _cinemachineTargetPitch += _input.look.y * deltaTimeMultiplier;
            }

            _cinemachineTargetYaw = ClampAngle(_cinemachineTargetYaw, float.MinValue, float.MaxValue);
            _cinemachineTargetPitch = ClampAngle(_cinemachineTargetPitch, BottomClamp, TopClamp);

            float cameraRoll = InvertGravity ? 180.0f : 0.0f;
            CinemachineCameraTarget.transform.rotation = Quaternion.Euler(_cinemachineTargetPitch + CameraAngleOverride,
                _cinemachineTargetYaw, cameraRoll);
        }

        private void Move()
        {
            bool freeMove = FreeMovementEnabled;

            // ----- 玩家的移动输入 -----
            // 默认：强制横向移动，只取 X（左右），忽略 W/S 的纵向输入；
            // 处于“自由移动区”时：WASD 全向移动（A/D=世界X 左右，W/S=世界Z 前后）。
            Vector2 rawInput = _input.move;
            Vector2 horizontalInput = freeMove
                ? rawInput
                : new Vector2(rawInput.x, 0f);

            // 冲刺受耐力约束：耐力不足 / 耗尽后恢复中会被降回普通跑速
            // （标题 Attract / 出生演出等“非正式游玩”阶段不限耐力，保持原有表现）
            bool staminaContext = IsStaminaContextActive();
            bool sprintHeld = _input.sprint;
            if (staminaContext)
                sprintHeld = sprintHeld && CanSprintWithStamina();

            float targetSpeed = sprintHeld ? SprintSpeed : MoveSpeed;
            if (horizontalInput == Vector2.zero) targetSpeed = 0.0f;

            float currentHorizontalSpeed = new Vector3(_controller.velocity.x, 0.0f, _controller.velocity.z).magnitude;
            float speedOffset = 0.1f;
            float inputMagnitude = _input.analogMovement ? horizontalInput.magnitude : 1f;

            if (currentHorizontalSpeed < targetSpeed - speedOffset ||
                currentHorizontalSpeed > targetSpeed + speedOffset)
            {
                _speed = Mathf.Lerp(currentHorizontalSpeed, targetSpeed * inputMagnitude,
                    Time.deltaTime * SpeedChangeRate);
                _speed = Mathf.Round(_speed * 1000f) / 1000f;
            }
            else
            {
                _speed = targetSpeed;
            }

            _animationBlend = Mathf.Lerp(_animationBlend, targetSpeed, Time.deltaTime * SpeedChangeRate);
            if (_animationBlend < 0.01f) _animationBlend = 0f;

            // 自由移动：角色沿输入方向跑（XZ 平面全向）；默认：只沿世界 X 左右跑
            Vector3 moveDirection = freeMove
                ? new Vector3(horizontalInput.x, 0f, horizontalInput.y)
                : Vector3.right * horizontalInput.x;
            if (InvertGravity) moveDirection.x *= -1.0f;

            if (moveDirection != Vector3.zero)
            {
                float targetAngle;
                if (freeMove)
                {
                    // 自由移动：面朝实际移动方向（平面角度）
                    targetAngle = Mathf.Atan2(moveDirection.x, moveDirection.z) * Mathf.Rad2Deg;
                    if (InvertGravity) targetAngle += 180f;
                }
                else
                {
                    // 强制横向：只向左或向右摆向
                    targetAngle = moveDirection.x > 0 ? RightFacingAngle : LeftFacingAngle;
                }

                float rotation = Mathf.SmoothDampAngle(transform.eulerAngles.y, targetAngle, ref _rotationVelocity, RotationSmoothTime);
                float characterRoll = InvertGravity ? 180.0f : 0.0f;
                transform.rotation = Quaternion.Euler(0.0f, rotation, characterRoll);
            }

            Vector3 upDirection = GetUpDirection();
            Vector3 targetDirection = moveDirection.normalized * _speed;
            _controller.Move(targetDirection * Time.deltaTime + upDirection * _verticalVelocity * Time.deltaTime);

            if (_hasAnimator)
            {
                _animator.SetFloat(_animIDSpeed, _animationBlend);
                _animator.SetFloat(_animIDMotionSpeed, inputMagnitude);
            }

            // 更新冲刺耐力（在地面跑动时消耗，松开冲刺 / 停跑 / 滞空后延迟恢复）
            UpdateStamina(staminaContext, sprintHeld, horizontalInput != Vector2.zero, Grounded);
        }

        // ---------------------------------------------------------------- 冲刺耐力

        /// <summary>耐力系统是否在正式游玩中生效（标题 Attract / 过场等不消耗）。</summary>
        private bool IsStaminaContextActive()
        {
            // HUD 只在正式游玩创建；过场 / 结局演出会禁用 PlayerInput，同样不消耗耐力
            if (!GameplayHUD.Exists)
                return false;

#if ENABLE_INPUT_SYSTEM
            if (_playerInput != null && !_playerInput.enabled)
                return false;
#endif
            return true;
        }

        private bool CanSprintWithStamina()
        {
            // 耐力耗尽后必须先松开冲刺键，再等耐力恢复到阈值之上才能重新冲刺
            if (staminaReleasePending)
                return false;

            if (CurrentStamina <= 0f)
                return false;

            if (staminaExhausted && CurrentStamina < staminaRestartThreshold)
                return false;

            return true;
        }

        private void UpdateStamina(bool contextActive, bool sprinting, bool moving, bool grounded)
        {
            if (!contextActive)
            {
                // 非正式游玩不消耗，随时回满（避免从标题/过场进入正式关卡时耐力不是满的）
                CurrentStamina = maxStamina;
                staminaExhausted = false;
                staminaReleasePending = false;
                staminaTimeSinceLastDrain = float.MaxValue;
                return;
            }

            // 玩家松开了冲刺键：解除“必须松开才能再冲刺”的限制
            if (!_input.sprint)
                staminaReleasePending = false;

            float delta = Time.deltaTime;
            staminaTimeSinceLastDrain += delta;

            if (sprinting && moving && grounded && CurrentStamina > 0f)
            {
                // 落地跑动冲刺中：消耗耐力
                staminaTimeSinceLastDrain = 0f;
                CurrentStamina = Mathf.Max(0f, CurrentStamina - sprintStaminaDrainPerSecond * delta);
                if (CurrentStamina <= 0.001f)
                {
                    CurrentStamina = 0f;
                    staminaExhausted = true;
                    staminaReleasePending = true;
                }
            }
            else if (CurrentStamina < maxStamina &&
                     staminaTimeSinceLastDrain >= staminaRegenDelaySeconds)
            {
                // 本轮没在冲刺消耗、且过了恢复延迟：恢复耐力（站着不动按住 Shift 也会慢慢回）
                CurrentStamina = Mathf.Min(maxStamina, CurrentStamina + staminaRegenPerSecond * delta);
                if (CurrentStamina >= staminaRestartThreshold)
                    staminaExhausted = false;
            }
        }

        private void JumpAndGravity()
        {
            float gravityMagnitude = Mathf.Abs(Gravity);

            if (Grounded)
            {
                _fallTimeoutDelta = FallTimeout;

                if (_hasAnimator)
                {
                    _animator.SetBool(_animIDJump, false);
                    _animator.SetBool(_animIDFreeFall, false);
                }

                if (_verticalVelocity < 0.0f)
                {
                    _verticalVelocity = -2f;
                }

                if (_input.jump && _jumpTimeoutDelta <= 0.0f)
                {
                    _verticalVelocity = Mathf.Sqrt(JumpHeight * 2f * gravityMagnitude);
                    if (_hasAnimator)
                    {
                        _animator.SetBool(_animIDJump, true);
                    }

                    // 起跳音效（SFX_Player_Jump_x 随机）
                    if (AudioManager.Instance != null)
                        AudioManager.Instance.PlayJump();

                    // 消耗本次跳跃输入并立即加跳锁：
                    // CharacterController 实际离开地面前的 1~2 帧仍会误判 Grounded，
                    // 不处理会出现“同一次起跳被判两次”→ 跳跃音效连响两下。
                    _jumpTimeoutDelta = JumpTimeout;
                    _input.jump = false;
                }

                if (_jumpTimeoutDelta >= 0.0f)
                {
                    _jumpTimeoutDelta -= Time.deltaTime;
                }
            }
            else
            {
                _jumpTimeoutDelta = JumpTimeout;

                if (_fallTimeoutDelta >= 0.0f)
                {
                    _fallTimeoutDelta -= Time.deltaTime;
                }
                else
                {
                    if (_hasAnimator)
                    {
                        _animator.SetBool(_animIDFreeFall, true);
                    }
                }

                _input.jump = false;
            }

            if (_verticalVelocity < _terminalVelocity)
            {
                _verticalVelocity -= gravityMagnitude * Time.deltaTime;
            }
        }

        private Vector3 GetUpDirection()
        {
            return InvertGravity ? Vector3.down : Vector3.up;
        }

        private void ApplyInversionVisuals()
        {
            float roll = InvertGravity ? 180.0f : 0.0f;

            Vector3 controllerEuler = transform.eulerAngles;
            transform.rotation = Quaternion.Euler(0.0f, controllerEuler.y, roll);

            Transform modelRoot = CharacterModelRoot;
            if (modelRoot == null && _hasAnimator)
            {
                modelRoot = _animator.transform;
            }

            if (modelRoot != null)
            {
                Vector3 modelEuler = modelRoot.localEulerAngles;
                modelRoot.localRotation = Quaternion.Euler(modelEuler.x, modelEuler.y, roll);
            }

            if (CinemachineCameraTarget != null)
            {
                Vector3 cameraEuler = CinemachineCameraTarget.transform.localEulerAngles;
                CinemachineCameraTarget.transform.localRotation = Quaternion.Euler(cameraEuler.x, cameraEuler.y, roll);
            }
        }

        private static float ClampAngle(float lfAngle, float lfMin, float lfMax)
        {
            if (lfAngle < -360f) lfAngle += 360f;
            if (lfAngle > 360f) lfAngle -= 360f;
            return Mathf.Clamp(lfAngle, lfMin, lfMax);
        }

        private void OnDrawGizmosSelected()
        {
            Color transparentGreen = new Color(0.0f, 1.0f, 0.0f, 0.35f);
            Color transparentRed = new Color(1.0f, 0.0f, 0.0f, 0.35f);

            if (Grounded) Gizmos.color = transparentGreen;
            else Gizmos.color = transparentRed;

            Gizmos.DrawSphere(
                transform.position + (GetUpDirection() * GroundedOffset),
                GroundedRadius);
        }

        private void OnFootstep(AnimationEvent animationEvent)
        {
            if (animationEvent.animatorClipInfo.weight > 0.5f)
            {
                // 优先用 Inspector 里手动指定的脚步素材（老通道，整场通用）；
                // 没指定时走 AudioManager：先检测脚下地表，再播对应地表素材组。
                if (FootstepAudioClips != null && FootstepAudioClips.Length > 0)
                {
                    var index = Random.Range(0, FootstepAudioClips.Length);
                    AudioSource.PlayClipAtPoint(FootstepAudioClips[index], transform.TransformPoint(_controller.center), FootstepAudioVolume);
                }
                else if (AudioManager.Instance != null)
                {
                    AudioManager.Instance.PlayFootstep(DetectStepSurface());
                }
            }
        }

        /// <summary>
        /// 检测脚底当前踩的地表类型（每走一步调用一次）。
        /// 先找 GGJStepSurfaceMarker 组件（挂在碰撞体或它父物体上）；
        /// 没有就按地面物体名 / 物理材质名里的关键字兜底识别；都识别不出返回 Floor。
        /// </summary>
        private AudioManager.StepSurface DetectStepSurface()
        {
            Vector3 upDirection = GetUpDirection();
            Vector3 origin = transform.position + (upDirection * GroundedOffset);

            if (Physics.SphereCast(origin, GroundedRadius, -upDirection, out RaycastHit hit,
                    0.5f, GroundLayers, QueryTriggerInteraction.Ignore))
            {
                // 1) 碰撞体 / 父物体上的显式标记优先
                GGJStepSurfaceMarker marker = hit.collider.GetComponentInParent<GGJStepSurfaceMarker>();
                if (marker != null)
                    return marker.surface;

                // 2) 按物体名 / 物理材质名兜底识别
                string hint = hit.collider.gameObject.name;
                if (hit.collider.sharedMaterial != null)
                    hint += " " + hit.collider.sharedMaterial.name;
                return ClassifySurfaceName(hint);
            }

            return AudioManager.StepSurface.Floor;
        }

        private static AudioManager.StepSurface ClassifySurfaceName(string nameHint)
        {
            if (string.IsNullOrEmpty(nameHint))
                return AudioManager.StepSurface.Floor;

            string key = nameHint.ToLowerInvariant();
            if (key.Contains("grass"))  return AudioManager.StepSurface.Grass;
            if (key.Contains("sand"))   return AudioManager.StepSurface.Sand;
            if (key.Contains("water") || key.Contains("puddle")) return AudioManager.StepSurface.Water;
            if (key.Contains("dirt") || key.Contains("mud") || key.Contains("soil")) return AudioManager.StepSurface.Dirt;
            if (key.Contains("mushroom")) return AudioManager.StepSurface.Mushroom;
            return AudioManager.StepSurface.Floor;
        }

        private void OnLand(AnimationEvent animationEvent)
        {
            if (animationEvent.animatorClipInfo.weight > 0.5f)
            {
                // 优先用手动指定的落地素材；没指定时自动走 AudioManager 的落地随机池
                if (LandingAudioClip != null)
                {
                    AudioSource.PlayClipAtPoint(LandingAudioClip, transform.TransformPoint(_controller.center), FootstepAudioVolume);
                }
                else if (AudioManager.Instance != null)
                {
                    AudioManager.Instance.PlayLand();
                }
            }
        }
    }
}