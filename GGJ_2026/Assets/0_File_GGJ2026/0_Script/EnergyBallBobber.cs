using UnityEngine;

/// <summary>
/// 能量球“待机小动作”：整体轻微上下 + 左右浮动，并且慢慢自转。
///
/// 用法：把这个组件挂到 P_EnergyBall（或任何想飘起来的物体）上就行，
///      所有幅度/速度都在 Inspector 上直接调（都有中文标签）。
///
/// 说明：
///   · 上下和左右用的是【两个不同频率】的正弦，合起来是个缓慢的“李萨如”轨迹，
///     看起来像在水里漂，不会是死板的来回直线；
///   · 位置/旋转都以【启动时记录的初始 transform】为基准每帧重算，
///     所以不会被别的脚本（比如被吸收时飞向玩家的动画）带跑，也不会累积漂移；
///   · 勾选“随机初始相位”后，每个球起落会错开，不会整齐划一地一起跳。
/// </summary>
[DisallowMultipleComponent]
public class EnergyBallBobber : MonoBehaviour
{
    [Header("浮动")]
    [Tooltip("上下浮动幅度（米）")]
    [SerializeField] private float upAmplitude = 0.12f;
    [Tooltip("上下浮动速度（越大越快）")]
    [SerializeField] private float upSpeed = 0.9f;
    [Tooltip("左右浮动幅度（米）")]
    [SerializeField] private float sideAmplitude = 0.08f;
    [Tooltip("左右浮动速度（和上下速度不一样才会走出漂移感）")]
    [SerializeField] private float sideSpeed = 0.62f;
    [Tooltip("左右摆动的方向：勾上 = 沿物体自己的右方向；不勾 = 沿世界 X 轴")]
    [SerializeField] private bool sideUseLocalRight = true;
    [Tooltip("每个球的起落错开一点，看起来不是整齐划一地跳")]
    [SerializeField] private bool randomizePhase = true;

    [Header("自转")]
    [Tooltip("自转轴向（本地空间），默认绕自身 Y 轴")]
    [SerializeField] private Vector3 spinAxis = Vector3.up;
    [Tooltip("自转速度（度/秒）")]
    [SerializeField] private float spinSpeed = 25f;

    private Vector3 baseLocalPos;
    private Quaternion baseLocalRot;
    private float phaseUp;
    private float phaseSide;
    private float spinAngle;

    private void Awake()
    {
        CacheBase();
        InitPhase();
    }

    private void OnEnable()
    {
        // 重新激活时以当前摆放为准（比如被吸收后又重新出现的球）
        CacheBase();
    }

    private void Update()
    {
        float t = Time.time;

        // —— 位置：初始位置 + 上下 + 左右 ——
        Vector3 sideDir = sideUseLocalRight
            ? (baseLocalRot * Vector3.right)
            : (Quaternion.Inverse(transform.parent ? transform.parent.rotation : Quaternion.identity) * Vector3.right);

        Vector3 pos = baseLocalPos;
        pos.y += Mathf.Sin(t * upSpeed + phaseUp) * upAmplitude;
        pos += sideDir * (Mathf.Sin(t * sideSpeed + phaseSide) * sideAmplitude);
        transform.localPosition = pos;

        // —— 旋转：以初始朝向为基准累加自转，不会累积误差 ——
        if (spinSpeed != 0f)
        {
            spinAngle += spinSpeed * Time.deltaTime;
            Vector3 axis = spinAxis.sqrMagnitude > 0.0001f ? spinAxis.normalized : Vector3.up;
            transform.localRotation = baseLocalRot * Quaternion.AngleAxis(spinAngle, axis);
        }
    }

    /// <summary>记下当前的摆放作为“静止姿态”（之后所有浮动都以它为基准）。</summary>
    public void CacheBase()
    {
        baseLocalPos = transform.localPosition;
        baseLocalRot = transform.localRotation;
    }

    private void InitPhase()
    {
        if (!randomizePhase)
        {
            phaseUp = 0f;
            phaseSide = 1.7f;
            spinAngle = 0f;
            return;
        }

        phaseUp = Random.Range(0f, Mathf.PI * 2f);
        phaseSide = Random.Range(0f, Mathf.PI * 2f);
        spinAngle = Random.Range(0f, 360f);
    }
}
