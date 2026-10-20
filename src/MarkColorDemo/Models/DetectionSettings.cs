using System.IO;

namespace MarkColorDemo;

/// <summary>算法参数。提取参数变化需要重取色；几何容差和色差阈值变化只需重新比较。</summary>
public sealed class DetectionSettings
{
    public QualitySettings Quality { get; set; } = new();

    public ColorDifferenceSettings ColorDifference { get; set; } = new();

    public double MinRadius { get; set; } = 8;

    // 半径单位为原图 px；圆度/填充率是无量纲比例。
    public double MaxRadius { get; set; } = 500;

    public double MinCircularity { get; set; } = .70;

    public double MinFill { get; set; } = .80;

    public double SaturationMin { get; set; } = 40;

    // 深色分支按灰度 0..255；浅色分支按局部背景亮差，BrightContrast=0 关闭浅色检测。
    public double DarkMax { get; set; } = 110;

    public double BrightContrast { get; set; } = 3;

    public double InnerRatio { get; set; } = .65;

    // ROI 原点与宽高均相对于整幅图像归一化；内圈比例用于避免圆点边缘混入背景。
    public double RoiX
    {
        get; set;
    }

    public double RoiY
    {
        get; set;
    }

    public double RoiWidth { get; set; } = 1;

    public double RoiHeight { get; set; } = 1;

    // 平移相对图像对角线；允许同版式全视野偏移，已保存 JSON 的显式值不会被重置。
    public double MaxTranslation { get; set; } = 1;

    public double MaxRotation { get; set; } = 10;

    // 旋转单位为度；尺度变化和位置容差分别是比例、标准半径倍数。
    public double MaxScaleChange { get; set; } = .15;

    public double PositionTolerance { get; set; } = .7;

    public double DefaultDeltaE { get; set; } = 3;

    public NativeSettings ToNative() => new()
    {
        MinRadius = MinRadius,
        MaxRadius = MaxRadius,
        MinCircularity = MinCircularity,
        MinFill = MinFill,
        SaturationMin = SaturationMin,
        DarkMax = DarkMax,
        BrightContrast = BrightContrast,
        InnerRatio = InnerRatio,
        RoiX = RoiX,
        RoiY = RoiY,
        RoiWidth = RoiWidth,
        RoiHeight = RoiHeight
    };

    public NativeMatchSettings ToMatch() => new()
    {
        MaxTranslation = MaxTranslation,
        MaxRotation = MaxRotation,
        MaxScaleChange = MaxScaleChange,
        PositionTolerance = PositionTolerance
    };

    // 嵌套参数必须深复制；对话框取消和后台处理不能反向修改正在使用的标准。
    public DetectionSettings Copy()
    {
        var copy = (DetectionSettings)MemberwiseClone();
        copy.Quality = Quality.Copy();
        copy.ColorDifference = ColorDifference.Copy();
        return copy;
    }

    public void Validate()
    {
        if (Quality == null)
        {
            throw new InvalidDataException("缺少测量质量参数。");
        }

        Quality.Validate();
        if (ColorDifference == null)
        {
            throw new InvalidDataException("缺少 ΔE00 参数因子。");
        }
        ColorDifference.Validate();
        foreach (var p in GetType().GetProperties().Where(p => p.PropertyType == typeof(double)))
        {
            if (!double.IsFinite((double)p.GetValue(this)!))
            {
                throw new InvalidDataException("参数必须为有限数值。");
            }
        }

        if (MinRadius < 3 || MaxRadius <= MinRadius || MaxRadius > 2000)
        {
            throw new InvalidDataException("半径范围必须为 3 ≤ 最小 < 最大 ≤ 2000 像素。");
        }

        if (MinCircularity <= 0 || MinCircularity > 1 || MinFill <= 0 || MinFill > 1)
        {
            throw new InvalidDataException("圆度和填充率应在 (0, 1] 内。");
        }

        if (SaturationMin < 1 || SaturationMin > 255 || DarkMax < 0 || DarkMax > 254 || BrightContrast < 0 || BrightContrast > 80)
        {
            throw new InvalidDataException("分割阈值超出范围。");
        }

        if (InnerRatio < .2 || InnerRatio > .9)
        {
            throw new InvalidDataException("内圈半径比例应在 0.2–0.9 内。");
        }

        if (RoiX < 0 || RoiY < 0 || RoiWidth <= 0 || RoiHeight <= 0 || RoiX + RoiWidth > 1 || RoiY + RoiHeight > 1)
        {
            throw new InvalidDataException("ROI 应完全位于图像内，使用 0–1 比例坐标。");
        }

        if (MaxTranslation < 0 || MaxTranslation > 1 || MaxRotation < 0 || MaxRotation > 30 || MaxScaleChange < 0 || MaxScaleChange > .4 || PositionTolerance < .1 || PositionTolerance > 2 || DefaultDeltaE <= 0 || DefaultDeltaE > 100)
        {
            throw new InvalidDataException("匹配或色差阈值超出允许范围。");
        }
    }

    // 提取/取色 ABI 参数定义缓存失效边界；质量、匹配、色差变化复用测量快照。
    // 新增参数时同步核对 ToNative 的映射，并运行设置回归。
    public bool SameExtraction(DetectionSettings other) => ToNative().Equals(other.ToNative());
}
