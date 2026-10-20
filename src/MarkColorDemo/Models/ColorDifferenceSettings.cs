using System.IO;
using System.Text.Json.Serialization;

namespace MarkColorDemo;

/// <summary>CIEDE2000 参数因子；与提取、几何匹配及逐点阈值分别管理。</summary>
public sealed class ColorDifferenceSettings
{
    public double KL { get; set; } = 1;

    public double KC { get; set; } = 1;

    public double KH { get; set; } = 1;

    [JsonIgnore]
    public string Description => FormattableString.Invariant($"ΔE00：kL={KL:G8} · kC={KC:G8} · kH={KH:G8}");

    public ColorDifferenceSettings Copy() => (ColorDifferenceSettings)MemberwiseClone();

    public NativeColorDifferenceSettings ToNative() => new() { KL = KL, KC = KC, KH = KH };

    // 限制参数因子范围，不改变产品允许的 ΔE00 阈值；默认 1/1/1 与旧标准兼容。
    public void Validate()
    {
        // 正值避免除零；两端采用同一演示调参范围，不把因子解释为百分比。
        foreach (var (name, factor) in new[] { ("kL", KL), ("kC", KC), ("kH", KH) })
        {
            if (!double.IsFinite(factor) || factor < .1 || factor > 10)
            {
                throw new InvalidDataException($"{name} 必须为 0.1–10 的有限数值；默认 1，数值越大，对该分量越不敏感。");
            }
        }
    }
}
