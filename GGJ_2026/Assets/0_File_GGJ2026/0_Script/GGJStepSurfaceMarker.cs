using UnityEngine;

/// <summary>
/// 地面材质标记：挂到“地面/平台”碰撞体上、或“区域体积”上，选一个地表类型，
/// 玩家经过时脚步声会自动换成对应素材组（Grass / Sand / Water / Dirt / Mushroom）。
///
/// 两种用法：
///  · 实体地面：挂在能站人的碰撞体所在的 GameObject（或它的父物体）上；
///  · 区域体积：挂在勾了 Is Trigger 的体积上（如浅水洼、草地范围）。适合“水面本身没有
///    能站人的碰撞体、人其实踩在水洼下方的地面上”这类情况 —— Trigger 不挡路，
///    但玩家站进去就会按整个区域切地表。
///
/// 判定规则（ThirdPersonController.DetectStepSurface，每步调用一次）
/// —— 只认本组件，不做任何“按物体名 / 材质名猜”的事：
///  1. 玩家所处的区域体积（Trigger）上（或它的父物体上）有本组件 → 用它指定的地表类型
///     （多个区域重叠时取包围盒最小的那个）；
///  2. 否则看脚下射线命中的 Collider 上（或它的父物体上）有没有本组件 → 用它指定的地表类型；
///  3. 没挂本组件的地方 → 默认 Floor（播 SFX_Footstep_Lab 地板组）。
/// 想改某处地表就只加/改一个本组件，改完立刻生效，不用管物体叫什么名字。
///
/// 所有素材组挂在 Resources/GGJ_AudioManager 预制体的 AudioManager 上，可单独开关/换素材。
/// </summary>
public class GGJStepSurfaceMarker : MonoBehaviour
{
    [Tooltip("这块地面属于哪种地表：决定玩家踩上去用什么脚步声素材组")]
    public AudioManager.StepSurface surface = AudioManager.StepSurface.Floor;
}
