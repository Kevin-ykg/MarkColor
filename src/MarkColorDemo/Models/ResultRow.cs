namespace MarkColorDemo;

/// <summary>界面和报告的展示行；字符串是格式化输出，继续计算应读取 Analysis/Comparison。</summary>
public sealed class ResultRow
{
    public int Id
    {
        get; set;
    }

    public string Name { get; set; } = "";

    public string InkChannel { get; set; } = "";

    public string ReferenceLab { get; set; } = "—";

    public string CurrentLab { get; set; } = "—";

    public string DeltaE { get; set; } = "—";

    public string Deltas { get; set; } = "—";

    public string Threshold { get; set; } = "—";

    public string Uniformity { get; set; } = "—";

    public MeasurementDecision Decision { get; set; } = new(DetectionVerdict.Quantified, "");

    public string Reason => Decision.Reason;

    public string Quality { get; set; } = "—";

    public string Status => Decision.Label;

    public bool Pass => Decision.Pass;

    public string Foreground => Decision.Verdict switch
    {
        DetectionVerdict.Pass => "#087A62",
        DetectionVerdict.Unmeasurable or DetectionVerdict.AlignmentUncertain => "#916000",
        DetectionVerdict.Quantified => "#315878",
        _ => "#BA3045"
    };

    public string Swatch { get; set; } = "#CCCCCC";

    // 与 Analysis.Marks/Qualities/Contours 同下标；缺失或未分配时为 -1。
    public int ObservedIndex { get; set; } = -1;
}
