using System.Globalization;

namespace MarkColorDemo;

/// <summary>将原生几何/色差结果转换为逐点判定；显示和报告共用同一结果。</summary>
public sealed class Inspection
{
    private const int MatchedStatus = 0;
    private const int MissingStatus = 1;
    private string? alignmentIssue;

    public List<ResultRow> Rows { get; set; } = [];

    public Comparison[] Comparisons { get; set; } = [];

    public Alignment Alignment
    {
        get; set;
    }

    public bool HasReliableAlignment => Alignment.Ambiguous == 0;

    public MeasurementDecision Decision
    {
        get
        {
            // 整图优先级：配准不确定 → 无法测量 → 逐点失败 → 多余点 → 全部通过。
            // 质量问题覆盖颜色 NG，避免把采集条件异常当成已确认的产品缺陷。
            if (!HasReliableAlignment)
            {
                return new(DetectionVerdict.AlignmentUncertain, AlignmentDescription);
            }

            var unavailable = Rows.FirstOrDefault(r => r.Decision.Verdict == DetectionVerdict.Unmeasurable);
            if (unavailable != null)
            {
                return unavailable.Decision;
            }

            var failure = Rows.FirstOrDefault(r => !r.Pass);
            if (failure != null)
            {
                return failure.Decision;
            }

            if (Alignment.Extra > 0)
            {
                return new(DetectionVerdict.Extra, $"多余 {Alignment.Extra} 个候选圆点。");
            }

            return Rows.Count > 0 ? new(DetectionVerdict.Pass, "全部圆点通过。") : new(DetectionVerdict.Unmeasurable, "没有有效测量。");
        }
    }

    public bool Pass => Decision.Pass;

    /// <summary>把待测数组下标映射为界面标签；配准不明确时只显示候选编号，避免暗示已确认墨路。</summary>
    public string ObservedLabel(int observedIndex)
    {
        if (!HasReliableAlignment)
        {
            return $"候选 P{observedIndex + 1} · 待配准";
        }

        var row = Rows.FirstOrDefault(candidate => candidate.ObservedIndex == observedIndex);
        return row == null ? $"多余 P{observedIndex + 1}" : $"#{row.Id} {row.Status}";
    }

    /// <summary>只有配准可靠且对应项明确缺失时才画缺失圈；referenceIndex 是标准数组下标。</summary>
    public bool IsConfirmedMissing(int referenceIndex) => HasReliableAlignment && Comparisons[referenceIndex].Status == MissingStatus && Comparisons[referenceIndex].ObservedIndex < 0;

    public string Summary(int referenceCount)
    {
        if (!HasReliableAlignment)
        {
            return $"检测到 {Alignment.Matched + Alignment.Extra} 个候选 · 配准不确定，暂不判断缺失或颜色";
        }

        int missing = Comparisons.Count(comparison => comparison.Status == MissingStatus);
        int colorFailures = Rows.Count(row => row.Decision.Verdict == DetectionVerdict.ColorNg);
        return $"匹配 {Alignment.Matched}/{referenceCount} · 缺失 {missing} · 多余 {Alignment.Extra} · 色差超限 {colorFailures} · 无法测量 {Rows.Count(r => r.Decision.Verdict == DetectionVerdict.Unmeasurable)}";
    }

    public string AlignmentDescription
    {
        get
        {
            if (alignmentIssue != null)
            {
                return alignmentIssue;
            }

            if (!HasReliableAlignment)
            {
                return "匹配不确定：请检查是否为同版式图像，以及设置中的最大平移、旋转、比例和 ROI。";
            }

            return $"配准：X {Alignment.TranslateX:+0.0;-0.0;0.0} px，Y {Alignment.TranslateY:+0.0;-0.0;0.0} px；" +
                $"旋转 {Alignment.AngleDegrees:F2}°，比例 {Alignment.Scale:F4}，位置 RMS {Alignment.Rms:F2} px。";
        }
    }

    public static string Lab(Mark mark) => FormattableString.Invariant($"{mark.L:F2} / {mark.A:F2} / {mark.B:F2}");

    public static string Swatch(Mark mark) => $"#{(int)Math.Clamp(mark.Red, 0, 255):X2}{(int)Math.Clamp(mark.Green, 0, 255):X2}{(int)Math.Clamp(mark.Blue, 0, 255):X2}";

    private static string Uniformity(Mark mark) => FormattableString.Invariant($"{mark.StdL:F2} / {mark.StdA:F2} / {mark.StdB:F2}");

    private static ResultRow CreateRow(StandardMark standard, Comparison comparison, Analysis image, QualitySettings quality)
    {
        Mark? observed = comparison.ObservedIndex >= 0 ? image.Marks[comparison.ObservedIndex] : null;
        var decision = MeasurementDecision.Color(standard, comparison);
        if (comparison.Status == MatchedStatus)
        {
            // 先有对应关系，才检查该点的测量资格；质量失败须覆盖暂算色差结论。
            // 这样失焦/反光不会在界面或报告中冒充产品颜色 NG，也不会因色差小误判 OK。
            var eligibility = quality.Assess(image, comparison.ObservedIndex);
            if (!eligibility.Pass)
            {
                decision = eligibility;
            }
        }

        return new ResultRow
        {
            Id = standard.Id,
            Name = standard.Name,
            InkChannel = standard.InkChannel,
            ReferenceLab = Lab(standard.Value),
            CurrentLab = observed is Mark current ? Lab(current) : "—",
            DeltaE = double.IsFinite(comparison.DeltaE00) ? comparison.DeltaE00.ToString("F3", CultureInfo.InvariantCulture) : "—",
            Deltas = double.IsFinite(comparison.DeltaL) ? FormattableString.Invariant($"{comparison.DeltaL:+0.00;-0.00;0.00} / {comparison.DeltaA:+0.00;-0.00;0.00} / {comparison.DeltaB:+0.00;-0.00;0.00}") : "—",
            Threshold = standard.DeltaEThreshold.ToString("F2", CultureInfo.InvariantCulture),
            Uniformity = observed is Mark measured ? Uniformity(measured) : "—",
            Decision = decision,
            Quality = QualityEvidence(image, comparison.ObservedIndex),
            Swatch = observed is Mark colored ? Swatch(colored) : "#CCCCCC",
            ObservedIndex = comparison.ObservedIndex
        };
    }

    // 文本只解释同一个已匹配候选的证据，不再次判断阈值，也不按标准下标读取待测数组。
    private static string QualityEvidence(Analysis image, int observedIndex)
    {
        if (observedIndex < 0 || observedIndex >= image.Qualities.Count)
        {
            return "无质量证据";
        }

        var quality = image.Qualities[observedIndex];
        string refinement = quality.Refined == 1 ? "已精修" : "粗定位";
        return $"{refinement} / 置信 {quality.ContourConfidence:F2} / 边宽 {quality.EdgeWidth:F1}px";
    }

    /// <summary>校验标准、完成几何比较，再统一生成测量资格与颜色结论；不读图、不重取色。</summary>
    public static Inspection Run(StandardProfile profile, Analysis image)
    {
        profile.Validate();
        if (image.Marks.Count > 16)
        {
            // 点数过多时不进入组合匹配器；保留新图与全部候选，用户仍能框选 ROI。
            var pending = new Inspection
            {
                Alignment = new Alignment
                {
                    Ambiguous = 1,
                    Extra = image.Marks.Count
                },
                alignmentIssue = $"检测到 {image.Marks.Count} 个候选，超过配准上限 16 点。请在设置中框选目标膜片 ROI 后重新检测。",
                Comparisons = profile.Marks.Select(_ => new Comparison
                {
                    Status = 2,
                    ObservedIndex = -1,
                    DeltaE00 = double.NaN,
                    DeltaE76 = double.NaN,
                    DeltaL = double.NaN,
                    DeltaA = double.NaN,
                    DeltaB = double.NaN,
                    PositionError = double.NaN
                }).ToArray()
            };
            for (int i = 0; i < profile.Marks.Count; ++i)
            {
                pending.Rows.Add(CreateRow(profile.Marks[i], pending.Comparisons[i], image, profile.Settings.Quality));
            }

            return pending;
        }

        var (comparisons, alignment) = Native.Compare(profile, image);
        var result = new Inspection
        {
            Comparisons = comparisons,
            Alignment = alignment
        };
        for (int index = 0; index < profile.Marks.Count; ++index)
        {
            result.Rows.Add(CreateRow(profile.Marks[index], comparisons[index], image, profile.Settings.Quality));
        }

        return result;
    }

    /// <summary>未建标时生成只读量化行；这些行不包含相对标准的合格判定。</summary>
    public static List<ResultRow> Quantify(Analysis analysis) => analysis.Marks.Select((mark, index) => new ResultRow
    {
        Id = index + 1,
        Name = $"Mark {index + 1}",
        CurrentLab = Lab(mark),
        Uniformity = Uniformity(mark),
        Decision = new(DetectionVerdict.Quantified, "未设置标准，只展示测量数值。"),
        ObservedIndex = index,
        Swatch = Swatch(mark)
    }).ToList();
}
