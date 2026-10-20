namespace MarkColorDemo;

/// <summary>质量阈值是演示起点，随标准保存；全部比例为 0..1，亮差为 BGR8 RMS，边缘宽度相对半径。</summary>
public sealed class QualitySettings
{
    public double MaxOverexposedFraction { get; set; } = .05;

    public double MaxUnderexposedFraction { get; set; } = .4;

    public double MinBackgroundL { get; set; } = 15;

    public double MaxGlareFraction { get; set; } = .04;

    // EdgeWidth/Radius 为比例；EdgeContrast 为 BGR8 RMS，ContourConfidence 为非概率指标。
    public double MaxEdgeWidthRatio { get; set; } = .35;

    public double MinEdgeContrast { get; set; } = 2;

    public double MinEdgeSupport { get; set; } = .55;

    public double MinContourConfidence { get; set; } = .2;

    public double MinValidSampleFraction { get; set; } = .8;

    public double MinSamplePixels { get; set; } = 64;

    public QualitySettings Copy() => (QualitySettings)MemberwiseClone();

    public void Validate()
    {
        // 本模型公开属性当前均为 double；新增非数值属性时必须同步调整这里的反射校验。
        foreach (var property in GetType().GetProperties())
        {
            double value = (double)property.GetValue(this)!;
            double upper = property.Name switch
            {
                nameof(MinBackgroundL) => 100,
                nameof(MinEdgeContrast) => 255,
                nameof(MinSamplePixels) => 10000000,
                _ => 1 // 其余属性均为无量纲比例或置信指标。
            };
            if (!double.IsFinite(value) || value < 0 || value > upper)
            {
                throw new System.IO.InvalidDataException($"质量参数 {property.Name} 超出 0..{upper}。");
            }
        }

        if (MinSamplePixels < 16)
        {
            throw new System.IO.InvalidDataException("有效取色像素至少为 16。");
        }
    }

    /// <summary>配准成功后按待测下标评估测量资格；仅返回证据不足/通过，不判产品颜色。</summary>
    public MeasurementDecision Assess(Analysis image, int index)
    {
        if (index < 0 || index >= image.Qualities.Count)
        {
            return new(DetectionVerdict.Unmeasurable, "缺少图像质量证据，请用新版算法重新提取。");
        }

        var mark = image.Marks[index];
        var quality = image.Qualities[index];
        if (new[]
        {
            quality.OverexposedFraction,
            quality.UnderexposedFraction,
            quality.GlareFraction,
            quality.EdgeContrast,
            quality.EdgeWidth,
            quality.EdgeSupport,
            quality.ContourConfidence,
            quality.FitResidual,
            quality.ValidSampleFraction
        }.Any(x => !double.IsFinite(x) || x < 0))
        {
            return new(DetectionVerdict.Unmeasurable, "质量证据包含无效数值。");
        }

        // 收集所有不满足条件的原因；低对比度不能靠扩大色差阈值变成有效测量。
        List<string> reasons = [];
        if (quality.Refined == 0)
        {
            reasons.Add("仅有粗定位，原分辨率轮廓精修未成功");
        }

        if (quality.OverexposedFraction > MaxOverexposedFraction)
        {
            reasons.Add($"过曝像素 {quality.OverexposedFraction:P1}");
        }

        // 黑色 mark 本身允许较暗；同时检查外围基材，避免将正常 K 点误判为整图欠曝。
        if (quality.UnderexposedFraction > MaxUnderexposedFraction && mark.BackgroundL < MinBackgroundL)
        {
            reasons.Add($"严重欠曝（背景 L*={mark.BackgroundL:F1}）");
        }

        if (quality.GlareFraction > MaxGlareFraction)
        {
            reasons.Add($"疑似反光覆盖 {quality.GlareFraction:P1}");
        }

        if (quality.EdgeContrast >= MinEdgeContrast && quality.EdgeWidth / mark.Radius > MaxEdgeWidthRatio)
        {
            reasons.Add($"边缘模糊，过渡宽度 {quality.EdgeWidth:F1}px");
        }

        if (quality.EdgeContrast < MinEdgeContrast || quality.EdgeSupport < MinEdgeSupport || quality.ContourConfidence < MinContourConfidence)
        {
            reasons.Add($"轮廓精度不足（置信指标 {quality.ContourConfidence:F2}，边缘支持 {quality.EdgeSupport:P0}，亮差 {quality.EdgeContrast:F1}）");
        }

        if (mark.SampleCount < MinSamplePixels || quality.ValidSampleFraction < MinValidSampleFraction)
        {
            reasons.Add($"有效采样不足（{mark.SampleCount} 像素，覆盖 {quality.ValidSampleFraction:P0}）");
        }

        return reasons.Count == 0 ? new(DetectionVerdict.Pass, "测量质量检查通过。") : new(DetectionVerdict.Unmeasurable, string.Join("；", reasons));
    }
}
