using UnityEngine;

/// <summary>
/// 地面材质标记：把它挂到“地面/平台”碰撞体的 GameObject 上并选一个地表类型，
/// 玩家经过时脚步声会自动换成对应素材组（Grass / Sand / Water / Dirt / Mushroom）。
///
/// 判定规则（ThirdPersonController.OnFootstep 每步往脚下打一条射线）：
///  1. 命中的 Collider 上（或它的父物体上）有本组件 → 用它指定的地表类型；
///  2. 没有组件时按地面物体名 / 物理材质名自动识别（grass / sand / water / puddle / dirt / mushroom 等关键字）；
///  3. 都识别不出 → 默认 Floor（播 SFX_Footstep_Lab 地板组）。
///
/// 所有素材组挂在 Resources/GGJ_AudioManager 预制体的 AudioManager 上，可单独开关/换素材。
/// </summary>
public class GGJStepSurfaceMarker : MonoBehaviour
{
    [Tooltip("这块地面属于哪种地表：决定玩家踩上去用什么脚步声素材组")]
    public AudioManager.StepSurface surface = AudioManager.StepSurface.Floor;
}
