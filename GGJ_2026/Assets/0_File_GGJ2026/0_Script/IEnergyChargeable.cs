/// <summary>
/// “多档能量”目标接口：玩家可以按 E 逐档充能、按 Q 逐档回收（例如能量蘑菇）。
///
/// InteractableObject 处于“多档能量模式”时，通过本接口读取当前档位、写入新档位；
/// 目标（如 BouncePad）自己负责把档位表现成外观/手感（蘑菇变大、弹力变强等）。
/// 每个目标实例各自持有档位，互不影响（这个蘑菇 2 档、那个蘑菇 0 档）。
/// </summary>
public interface IEnergyChargeable
{
    /// <summary>当前能量档位（0 ~ MaxEnergyLevel）。</summary>
    int EnergyLevel { get; }

    /// <summary>最高能量档位（蘑菇为 2）。</summary>
    int MaxEnergyLevel { get; }

    /// <summary>设置能量档位；实现方负责刷新自己的表现（例如放大上半部分、改变弹力）。</summary>
    void SetEnergyLevel(int level);
}
