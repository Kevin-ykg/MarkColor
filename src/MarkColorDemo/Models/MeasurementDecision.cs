namespace MarkColorDemo;

/// <summary>业务状态只有一个来源；界面、CSV、HTML 不再各自推断 OK/NG。</summary>
public enum DetectionVerdict
{
    /// <summary>尚未设置标准，仅有量化结果，不代表通过。</summary>
    Quantified,
    Pass,
    /// <summary>对应关系已确定，颜色数值超出启用的阈值；仍需测量资格有效。</summary>
    ColorNg,
    Missing,
    Extra,
    /// <summary>几何对应不唯一，暂不据候选关系判定缺点或色差。</summary>
    AlignmentUncertain,
    /// <summary>当前图像或证据不足以可靠测量，与产品颜色不合格分开。</summary>
    Unmeasurable,
    /// <summary>文件、调用或处理失败；不是颜色测量结果。</summary>
    Error
}

/// <summary>机器可判断的结论与面向操作员的原因；业务分支应比较 Verdict，避免依赖中文显示字符串。</summary>
public sealed record MeasurementDecision(DetectionVerdict Verdict, string Reason)
{
    public bool Pass => Verdict == DetectionVerdict.Pass;

    public string Label => Verdict switch
    {
        DetectionVerdict.Pass => "OK",
        DetectionVerdict.ColorNg => "色差 NG",
        DetectionVerdict.Missing => "缺少圆点",
        DetectionVerdict.Extra => "多余圆点",
        DetectionVerdict.AlignmentUncertain => "匹配不确定",
        DetectionVerdict.Unmeasurable => "无法可靠测量",
        DetectionVerdict.Error => "处理错误",
        _ => "已量化"
    };

    public string ReportLabel => Verdict switch
    {
        DetectionVerdict.Pass => "OK",
        DetectionVerdict.ColorNg or DetectionVerdict.Missing or DetectionVerdict.Extra => "NG",
        DetectionVerdict.AlignmentUncertain => "待确认",
        DetectionVerdict.Unmeasurable => "无法可靠测量",
        DetectionVerdict.Error => "错误",
        _ => "仅量化"
    };

    /// <summary>只解释几何状态与颜色阈值；Inspection 随后检查测量资格，必要时覆盖本结论。</summary>
    public static MeasurementDecision Color(StandardMark standard, Comparison comparison)
    {
        if (comparison.Status == 2)
        {
            return new(DetectionVerdict.AlignmentUncertain, "配准不确定，请先确认对应关系。");
        }

        if (comparison.Status == 1)
        {
            return new(DetectionVerdict.Missing, "预期位置未找到圆点。");
        }

        if (!double.IsFinite(comparison.DeltaE00))
        {
            return new(DetectionVerdict.Unmeasurable, "色差数值无效。");
        }

        List<string> reasons = [];
        // |ΔL|/|Δa|/|Δb| 是附加的独立限值，不是 ΔE00 权重；0 仅关闭该附加条件。
        if (comparison.DeltaE00 > standard.DeltaEThreshold)
        {
            reasons.Add($"ΔE00 超限 {comparison.DeltaE00 - standard.DeltaEThreshold:F3}");
        }

        foreach (var (name, value, limit) in new[]
        {
            ("L*", comparison.DeltaL, standard.MaxDeltaL),
            ("a*", comparison.DeltaA, standard.MaxDeltaA),
            ("b*", comparison.DeltaB, standard.MaxDeltaB)
        })
        {
            if (limit > 0 && Math.Abs(value) > limit)
            {
                reasons.Add($"|Δ{name}| = {Math.Abs(value):F2} > {limit:F2}");
            }
        }

        return reasons.Count == 0 ? new(DetectionVerdict.Pass, "所有启用的颜色阈值均通过。") : new(DetectionVerdict.ColorNg, string.Join("；", reasons));
    }
}
