using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MarkColorDemo;

/// <summary>编辑独立参数草稿；预览只产生临时结果，应用后的重建由 MainWindow.Settings 负责。</summary>
public partial class SettingsWindow : Window
{
    public DetectionSettings Settings
    {
        get; private set;
    }

    public List<StandardMark> Marks
    {
        get; private set;
    }

    public string ChangeReason => string.IsNullOrWhiteSpace(ChangeNote.Text) ? "检测参数、阈值或标签调整" : ChangeNote.Text.Trim();

    private readonly StandardProfile? profile;
    private readonly Analysis? previewSource;
    // 键是模型属性路径（如 Quality.MinSamplePixels），数值解析统一使用 InvariantCulture。
    private readonly Dictionary<string, TextBox> inputs = [];
    private Grid parameterTarget;
    private bool standardRecovery;

    /// <summary>建标恢复使用失败样图，进入 ROI 页；参数只是草稿，取消不会改写原标准。</summary>
    public void ConfigureForStandardRecovery(string reason)
    {
        standardRecovery = true;
        Title = "建标需要调整 — ROI 与采集条件";
        SettingsTitle.Text = "调整本次建标图像";
        SettingsDescription.Text = "左侧框选目标膜片全部圆点 → 预览确认数量与质量 → 继续建标。取消将保留原标准。";
        SettingsDescription.TextWrapping = TextWrapping.Wrap;
        StandardsTab.Visibility = Visibility.Collapsed;
        ChangeReasonLabel.Visibility = ChangeNote.Visibility = Visibility.Collapsed;
        RoiTab.IsSelected = true;
        ErrorText.Text = reason;
        ApplySettingsButton.Content = "使用当前参数继续建标";
        if (previewSource != null)
        {
            AfterView.Set(previewSource, null, null, Settings.InnerRatio);
            UpdatePreviewInfo(previewSource, Settings);
        }
    }

    /// <summary>复制可编辑参数和标准点；预览来源只读借用，点击应用前不改变主窗口的有效配置。</summary>
    public SettingsWindow(DetectionSettings settings, StandardProfile? standard, Analysis? preview = null)
    {
        InitializeComponent();
        Settings = settings.Copy();
        profile = standard;
        previewSource = preview;
        Marks = standard?.Marks.Select(m => m.Copy()).ToList() ?? [];
        StandardsGrid.ItemsSource = Marks;
        GlobalThreshold.Text = settings.DefaultDeltaE.ToString(CultureInfo.InvariantCulture);
        parameterTarget = ParameterGrid;
        Add("MinRadius", "最小圆点半径 (px)", "原图像素，默认 8；更小的点可能因有效取色像素不足而无法可靠测量。");
        Add("MaxRadius", "最大圆点半径 (px)", "默认 500；自动按半径范围多尺度搜索，不随 ROI 裁剪改变像素单位。");
        Add("InnerRatio", "取色内圈半径比例", "0.2–0.9，默认 0.65；取色区为实际轮廓与内圈的交集。");
        Add("DefaultDeltaE", "新建标准 ΔE00 阈值", "默认 3；已有逐点阈值在“逐点标准”页修改。");
        Add("RoiX", "ROI 左边界 / 图宽", "0–1；可在“ROI / 预览”页拖框填写。");
        Add("RoiY", "ROI 上边界 / 图高", "0–1；ROI 内触边圆点不参与检测。");
        Add("RoiWidth", "ROI 宽度 / 图宽", "0–1，默认 1。");
        Add("RoiHeight", "ROI 高度 / 图高", "0–1，默认 1。");
        parameterTarget = AdvancedGrid;
        Add("MinCircularity", "最小圆度 (0–1)", "4π × 面积 / 周长²，默认 0.70。");
        Add("MinFill", "最小圆填充率 (0–1)", "轮廓面积 / 最小外接圆面积，默认 0.80；精修后再次复核。");
        Add("SaturationMin", "彩色饱和度下限", "HSV S，0–255；默认 40。");
        Add("DarkMax", "黑色灰度上限", "0–254；默认 110。");
        Add("BrightContrast", "弱对比局部亮差阈值", "0–80，默认 3；同时搜索微亮/微暗点，0 关闭。");
        Add("MaxTranslation", "最大平移 / 对角线", "0–1，默认 1，允许视野内上下移动。");
        Add("MaxRotation", "最大旋转 (°)", "0–30；默认 10，不允许镜像。");
        Add("MaxScaleChange", "最大比例变化", "0–0.4；默认 0.15，即 ±15%。");
        Add("PositionTolerance", "位置容差 / 半径", "0.1–2；默认 0.7，越小越严格。");
        parameterTarget = QualityGrid;
        Add("Quality.MaxOverexposedFraction", "最大过曝像素比例", "0–1；B/G/R 均≥250，默认 0.05。");
        Add("Quality.MaxUnderexposedFraction", "最大全通道暗像素比例", "0–1；B/G/R 均≤5，且背景过暗才判严重欠曝，默认 0.4。");
        Add("Quality.MinBackgroundL", "欠曝背景 L* 下限", "0–100；默认 15，避免将正常黑色 K 点当成整图欠曝。");
        Add("Quality.MaxGlareFraction", "最大疑似反光覆盖比例", "0–1；局部中性高亮风险指标，默认 0.04，不能证明物理反光。");
        Add("Quality.MaxEdgeWidthRatio", "最大边缘过渡宽 / 半径", "0–1；90%→10% 过渡宽中位数，默认 0.35。");
        Add("Quality.MinEdgeContrast", "最小边缘颜色亮差", "0–255；BGR 差 RMS，非 ΔE，默认 2。");
        Add("Quality.MinEdgeSupport", "最小边缘支持比例", "0–1；有完整过渡的径向采样比例，默认 0.55。");
        Add("Quality.MinContourConfidence", "最小轮廓置信指标", "0–1；综合对比、支持度与拟合残差，默认 0.2；不是概率。");
        Add("Quality.MinValidSampleFraction", "最小内圈有效覆盖比例", "0–1；默认 0.8。");
        Add("Quality.MinSamplePixels", "最少有效取色像素", "16–10000000；默认 64。");
        parameterTarget = ColorDifferenceGrid;
        Add("ColorDifference.KL", "kL · 明度", "0.1–10；默认 1。增大数值会降低明度差的贡献。");
        Add("ColorDifference.KC", "kC · 彩度", "0.1–10；默认 1。彩度来自 a* 与 b*，不是 a* 的单独权重。");
        Add("ColorDifference.KH", "kH · 色相", "0.1–10；默认 1。色相来自 a* 与 b*，不是 b* 的单独权重。");
        BeforeView.Set(preview, null, null, settings.InnerRatio);
        BeforeView.RoiSelectionEnabled = true;
        BeforeView.RoiSelected += roi =>
        {
            SetInput("RoiX", roi.X);
            SetInput("RoiY", roi.Y);
            SetInput("RoiWidth", roi.Width);
            SetInput("RoiHeight", roi.Height);
            ErrorText.Text = "ROI 已填写到常用设置；预览或应用后生效。";
        };
    }

    // 按参数所属模型绑定草稿；避免把质量、色差和几何参数放进同一个平面结构。
    private static (object Owner, string Name) Property(string key, DetectionSettings settings)
    {
        if (key.StartsWith("Quality."))
        {
            return (settings.Quality, key[8..]);
        }
        if (key.StartsWith("ColorDifference."))
        {
            return (settings.ColorDifference, key[16..]);
        }
        return (settings, key);
    }

    private void ResetColor_Click(object sender, RoutedEventArgs e)
    {
        SetInput("ColorDifference.KL", 1);
        SetInput("ColorDifference.KC", 1);
        SetInput("ColorDifference.KH", 1);
        ErrorText.Text = "色差因子已恢复为 1 / 1 / 1，点击应用后生效；逐点阈值保持原值。";
    }

    private void SetInput(string name, double value) => inputs[name].Text = value.ToString("G8", CultureInfo.InvariantCulture);

    /// <summary>根据模型属性路径生成数值输入；输入键必须对应真实 double 属性，与 ReadDraft 共用。</summary>
    private void Add(string property, string title, string hint)
    {
        int row = parameterTarget.RowDefinitions.Count;
        parameterTarget.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var (owner, name) = Property(property, Settings);
        var label = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 7, 10, 7)
        };
        var input = new TextBox
        {
            Text = ((double)owner.GetType().GetProperty(name)!.GetValue(owner)!).ToString(CultureInfo.InvariantCulture),
            Margin = new Thickness(0, 5, 14, 5),
            ToolTip = hint
        };
        var desc = new TextBlock
        {
            Text = hint,
            Foreground = Brushes.SlateGray,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 5, 0, 5)
        };
        System.Windows.Automation.AutomationProperties.SetName(input, title);
        Grid.SetRow(label, row);
        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        Grid.SetRow(desc, row);
        Grid.SetColumn(desc, 2);
        parameterTarget.Children.Add(label);
        parameterTarget.Children.Add(input);
        parameterTarget.Children.Add(desc);
        inputs[property] = input;
    }

    private static bool HasErrors(DependencyObject value)
    {
        if (Validation.GetHasError(value))
        {
            return true;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++)
        {
            if (HasErrors(VisualTreeHelper.GetChild(value, i)))
            {
                return true;
            }
        }

        return false;
    }

    // 不直接写 Settings：先解析所有输入并验证，失败时不会留下半份参数。
    private DetectionSettings ReadDraft()
    {
        var draft = Settings.Copy();
        foreach (var pair in inputs)
        {
            pair.Value.ClearValue(Border.BorderBrushProperty);
            if (!double.TryParse(pair.Value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            {
                pair.Value.BorderBrush = Brushes.Red;
                pair.Value.Focus();
                throw new InvalidOperationException($"{pair.Key} 必须填写有限数值。");
            }

            var (owner, name) = Property(pair.Key, draft);
            owner.GetType().GetProperty(name)!.SetValue(owner, value);
        }

        draft.Validate();
        return draft;
    }

    private void ApplyThreshold_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(GlobalThreshold.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double threshold) || !double.IsFinite(threshold) || threshold <= 0 || threshold > 200)
        {
            ErrorText.Text = "统一阈值应为 (0,200] 的数值。";
            return;
        }

        StandardsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        StandardsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (var mark in Marks)
        {
            mark.DeltaEThreshold = threshold;
        }

        StandardsGrid.Items.Refresh();
        ErrorText.Text = "已更新表格，点击应用设置后生效。";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new DetectionSettings();
        foreach (var key in inputs.Keys)
        {
            var (owner, name) = Property(key, defaults);
            SetInput(key, (double)owner.GetType().GetProperty(name)!.GetValue(owner)!);
        }

        ErrorText.Text = "参数已恢复演示默认值；尚未应用，逐点标准未修改。";
    }

    private void WideRadius_Click(object sender, RoutedEventArgs e)
    {
        SetInput("MinRadius", 8);
        SetInput("MaxRadius", 500);
        double.TryParse(inputs["MinFill"].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double fill);
        SetInput("MinFill", Math.Max(.8, double.IsFinite(fill) ? fill : .8));
        ErrorText.Text = "半径已扩展为 8–500 px，圆填充率至少为 0.80，尚未应用。请先预览；ROI 和颜色阈值保持当前值。换相机后请重新建立颜色标准。";
    }

    /// <summary>撤销草稿中的人工目标偏移，恢复建标时保存的均值；仍需点击应用才提交。</summary>
    private void RestoreLab_Click(object sender, RoutedEventArgs e)
    {
        foreach (var mark in Marks)
        {
            if (mark.MeasuredLab is { } raw)
            {
                mark.L = raw.L;
                mark.A = raw.A;
                mark.B = raw.B;
            }
        }

        StandardsGrid.Items.Refresh();
        ChangeNote.Text = "恢复原始采样均值";
    }

    private async void Preview_Click(object sender, RoutedEventArgs e) => await PreviewDraftAsync();

    /// <summary>使用当前草稿重新分析预览来源，展示候选与质量；不替换标准，也不产生正式检测结论。</summary>
    public async Task PreviewDraftAsync()
    {
        try
        {
            if (previewSource == null)
            {
                throw new InvalidOperationException("先在主窗口加载图像，再打开设置预览。");
            }

            var draft = ReadDraft();
            IsEnabled = false;
            var result = await Task.Run(() => Native.Analyze(previewSource.Path, draft, preparePreview: true));
            AfterView.Set(result, null, null, draft.InnerRatio);
            AfterView.ShowSampleMask = true;
            UpdatePreviewInfo(result, draft);
            ErrorText.Text = standardRecovery ? "预览未应用。请核对实际圆点是否全部检出；点击继续后将重新校验全部建标采样。" : "预览未应用。正式应用会重建受提取参数影响的全部标准采样，并保留人工偏移。";
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
        finally
        {
            IsEnabled = true;
        }
    }

    /// <summary>解释提取数量、像素尺度和测量资格；没有标准时不能自动知道实际应有几个圆点。</summary>
    private void UpdatePreviewInfo(Analysis result, DetectionSettings draft)
    {
        var failures = Enumerable.Range(0, result.Marks.Count)
            .Select(index => (Index: index, Decision: draft.Quality.Assess(result, index)))
            .Where(item => !item.Decision.Pass)
            .ToList();
        string radii = result.Marks.Count == 0 ? "未找到圆点，请检查 ROI 是否覆盖完整圆点及半径范围。" : $"实测半径 {result.Marks.Min(m => m.Radius):F1}–{result.Marks.Max(m => m.Radius):F1} px；最少取色 {result.Marks.Min(m => m.SampleCount)} 像素。";
        string countHint = result.Marks.Count > 16 ? "候选超过 16 点，请缩小 ROI 排除其他膜片/背景。" : "请目视确认数量：未建立标准时，算法无法知道实际应该有几个圆点。";
        PreviewInfo.Text = $"调整前 {previewSource?.Marks.Count ?? 0} 点 → 预览 {result.Marks.Count} 点；质量不足 {failures.Count} 点。\n{countHint}\n{radii}\n" + string.Join("\n", failures.Select(x => $"#{x.Index + 1}：{x.Decision.Reason}"));
    }

    // DataGrid 可能仍处于编辑状态；先提交单元格/行，再检查模型和人工修改原因。
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!StandardsGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !StandardsGrid.CommitEdit(DataGridEditingUnit.Row, true) || HasErrors(StandardsGrid))
            {
                throw new InvalidOperationException("请修正表格内的无效输入。");
            }

            var draft = ReadDraft();
            if (profile != null)
            {
                var copy = profile.Copy();
                copy.Settings = draft;
                copy.Marks = Marks;
                copy.Validate();
                bool edited = Marks.Where((m, i) => m.L != profile.Marks[i].L || m.A != profile.Marks[i].A || m.B != profile.Marks[i].B).Any();
                if (edited && string.IsNullOrWhiteSpace(ChangeNote.Text))
                {
                    throw new InvalidOperationException("人工修改目标 Lab 请填写修改原因；原始采样均值将保留。");
                }
            }

            Settings = draft;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
    }
}
