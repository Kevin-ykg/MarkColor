using System.Text.Json.Serialization;

namespace MarkColorDemo;

/// <summary>单个标准点：固定编号、几何位置、可微调的目标 Lab 与独立颜色阈值。</summary>
public sealed class StandardMark
{
    public int Id
    {
        get; set;
    }

    public string Name { get; set; } = "Mark";

    public string InkChannel { get; set; } = "待确认";

    public Mark Value
    {
        get; set;
    }

    // 采样均值的原始基准，人工微调只改变 Value.L/A/B，不改写此值。
    public LabValue? MeasuredLab
    {
        get; set;
    }

    // 多次拍摄 n−1 样本标准差；不同于 Value.StdL/A/B（单图内圈像素离散程度）。
    public LabValue Repeatability { get; set; } = new(0, 0, 0);

    // 各次拍摄与原始采样均值的最大 ΔE00，使用标准保存的 kL/kC/kH。
    public double MaximumSampleDeltaE
    {
        get; set;
    }

    public int CaptureCount { get; set; } = 1;

    [JsonIgnore]
    public string RawLab => (MeasuredLab ?? LabValue.From(Value)).ToString();

    [JsonIgnore]
    public string RepeatabilityText => CaptureCount < 2 ? "单次采样" : Repeatability.ToString();

    // ΔE00 必须大于 0；下方三个通道差阈值为 0 时关闭，正数时比较绝对差。
    public double DeltaEThreshold { get; set; } = 3;

    public double MaxDeltaL
    {
        get; set;
    }

    public double MaxDeltaA
    {
        get; set;
    }

    public double MaxDeltaB
    {
        get; set;
    }

    [JsonIgnore]
    public double L
    {
        get => Value.L;
        // Mark 是值类型，先复制再写回，才能让 WPF 对此属性的编辑更新 Value。
        set
        {
            var m = Value;
            m.L = value;
            Value = m;
        }
    }

    [JsonIgnore]
    public double A
    {
        get => Value.A;
        set
        {
            var m = Value;
            m.A = value;
            Value = m;
        }
    }

    [JsonIgnore]
    public double B
    {
        get => Value.B;
        set
        {
            var m = Value;
            m.B = value;
            Value = m;
        }
    }

    // Mark 为值类型，LabValue 为不可变记录，因此这里浅复制即可隔离可编辑状态。
    public StandardMark Copy() => (StandardMark)MemberwiseClone();
}
