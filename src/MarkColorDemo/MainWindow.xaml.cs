using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace MarkColorDemo;

/// <summary>窗口生命周期与单图加载；设置、建标、报告、展示细节分别在同名分部文件。</summary>
public partial class MainWindow : Window
{
    // 以下状态由 UI 线程提交。Execute 禁用入口期间，后台任务只读取稳定快照。
    private DetectionSettings settings = new();
    private StandardProfile? standard;
    private Analysis? standardAnalysis, current;
    private Inspection? inspection;
    private bool busy;
    // 原图不存在/哈希不一致时关闭预览，但已保存目标 Lab 仍可用于检测。
    private bool standardPreviewValid = true;
    private string lastFolder;
    private CancellationTokenSource? cancellation;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp",
        ".png",
        ".jpg",
        ".jpeg",
        ".tif",
        ".tiff"
    };

    /// <summary>从程序目录向上查找源码根目录；便携包没有源码入口时退回程序目录，不依赖固定盘符。</summary>
    public static string DemoRoot
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "build.ps1")))
                {
                    return d.FullName;
                }
            }

            return AppContext.BaseDirectory;
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        lastFolder = Directory.GetParent(DemoRoot)?.FullName ?? DemoRoot;
        Closing += OnWindowClosing;
        WireViewInteractions();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!busy)
        {
            e.Cancel = !CanReplaceStandard();
            return;
        }

        cancellation?.Cancel();
        e.Cancel = true;
        StatusLine.Text = "正在完成当前图像处理，完成后可关闭。";
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private OpenFileDialog ImageDialog(bool multi = false) => new()
    {
        Title = "选择相同版式 / 视野的图像",
        Filter = "彩色图像|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff",
        InitialDirectory = lastFolder,
        Multiselect = multi
    };

    /// <summary>串行化用户操作：处理中禁用按钮，避免标准/参数在后台任务读取期间被修改。</summary>
    private async Task Execute(Func<Task> operation)
    {
        if (busy)
        {
            return;
        }

        busy = true;
        lastAttempt = null;
        ErrorBanner.Visibility = Visibility.Collapsed;
        Actions.IsEnabled = false;
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            string message = ex is DllNotFoundException ? "未找到算法或 OpenCV 运行库。请通过 build.ps1 构建后运行 publish 中的程序。" : ex.Message;
            Verdict.Text = "操作失败";
            VerdictBadge.Background = new SolidColorBrush(Color.FromRgb(255, 228, 230));
            Summary.Text = "本次操作失败；图像和表格保留上一次成功结果。";
            CurrentCaption.Text = $"上一次成功结果：{current?.Path ?? "无"}\n本次尝试：{lastAttempt ?? "设置/导出操作"}";
            StatusLine.Text = message;
            ErrorBanner.Text = message;
            ErrorBanner.Visibility = Visibility.Visible;
        }
        finally
        {
            Actions.IsEnabled = true;
            busy = false;
        }
    }

    /// <summary>在 Execute 的操作保护内加载单图；后台全部完成后再提交新图和结论，失败保留旧快照。</summary>
    private async Task LoadCurrent(string path)
    {
        lastAttempt = path;
        StatusLine.Text = "正在提取圆点与计算颜色…";
        var watch = Stopwatch.StartNew();
        // 一个工作任务完成计算和预览解码，避免连续跨线程调度和在 UI 线程读大图。
        var processed = await Task.Run(() => DetectionPipeline.Run(path, settings, standard));
        current = processed.Image;
        inspection = processed.Inspection;
        lastFolder = Path.GetDirectoryName(path)!;
        var refreshTimer = Stopwatch.StartNew();
        Refresh();
        measurementTiming = $"提取/取色 {processed.AnalyzeMs:F1} ms · 匹配/结果 {processed.CompareMs:F1} ms · 预览复制 {processed.PreviewMs:F1} ms · 界面提交 {refreshTimer.Elapsed.TotalMilliseconds:F1} ms";
        timingImage = current;
        UpdateEvidence(ResultsGrid.SelectedItem as ResultRow);
        StatusLine.Text = $"检测完成 · {watch.Elapsed.TotalMilliseconds:F0} ms · 详细耗时见检测依据";
        if (current.Marks.Count > 16)
        {
            StatusLine.Text = $"检测到 {current.Marks.Count} 个候选点，已保留图像供预览；请在设置中框选目标膜片 ROI（最多 16 点参与配准）。";
        }
    }

    /// <summary>加载标准/待测对的公共入口，供演示和离屏验证复用真实界面数据流。</summary>
    public async Task LoadPairAsync(string reference, string test)
    {
        var baseline = await Task.Run(() => DetectionPipeline.Load(reference, settings));
        var p = StandardProfile.From(baseline, settings);
        var sample = await Task.Run(() => DetectionPipeline.Load(test, settings));
        var result = await Task.Run(() => Inspection.Run(p, sample));
        standard = p;
        standardDirty = true;
        standardAnalysis = baseline;
        current = sample;
        inspection = result;
        standardPreviewValid = true;
        Refresh();
        measurementTiming = "";
        UpdateEvidence(ResultsGrid.SelectedItem as ResultRow);
        StatusLine.Text = "检测完成 · 点击圆点查看对应区域，展开检测依据查看配准详情。";
    }

    /// <summary>只绑定已有结果并提交重绘，不运行算法；相同 Analysis 的位图由视图复用。</summary>
    private void Refresh()
    {
        StandardCaption.Text = standard == null ? "尚未设置标准" : $"{standard.Name} · {standard.Marks.Count} 个圆点";
        StandardCaption.ToolTip = standard == null ? null : $"{standard.SourcePath}\n{standard.Width} × {standard.Height} · v{standard.Revision}\n{settings.ColorDifference.Description}";
        if (standard != null)
        {
            StandardCaption.Text += $" · {Math.Max(1, standard.Samples.Count)} 次采样 · {(standardDirty ? "未保存" : "已保存")}";
        }

        CurrentCaption.Text = current == null ? "可拖入 BMP / PNG / JPEG / TIFF" : $"{Path.GetFileName(current.Path)} · {current.Marks.Count} 个圆点";
        CurrentCaption.ToolTip = current == null ? null : $"{current.Path}\n{current.Width} × {current.Height}";
        ColorParameterSummary.Text = settings.ColorDifference.Description;
        ColorParameterSummary.ToolTip = "在设置 / 微调标准 → ΔE00 参数中修改；参数随标准保存。";
        StandardView.Set(standardAnalysis, standard, null, settings.InnerRatio, standardPreviewValid);
        CurrentView.Set(current, null, inspection, settings.InnerRatio);
        ResultsGrid.ItemsSource = inspection?.Rows ?? (current != null ? Inspection.Quantify(current) : null);
        if (inspection != null)
        {
            Verdict.Text = inspection.Decision.Label;
            Color verdictColor = inspection.Decision.Verdict switch
            {
                DetectionVerdict.AlignmentUncertain or DetectionVerdict.Unmeasurable => Color.FromRgb(255, 239, 200),
                DetectionVerdict.Pass => Color.FromRgb(213, 243, 234),
                _ => Color.FromRgb(255, 227, 231)
            };
            VerdictBadge.Background = new SolidColorBrush(verdictColor);
            Summary.Text = inspection.Summary(standard!.Marks.Count);
        }
        else
        {
            Verdict.Text = current == null ? "待检测" : "仅量化";
            VerdictBadge.Background = new SolidColorBrush(Color.FromRgb(221, 230, 238));
            if (current != null)
            {
                Summary.Text = $"提取到 {current.Marks.Count} 个圆点；设置标准后可比较颜色。";
            }
            else
            {
                Summary.Text = standard == null
                    ? "先建立或读取标准，再加载待测图像。"
                    : "标准已就绪，请加载同版式的待测图像。";
            }
        }
        UpdateEvidence(ResultsGrid.SelectedItem as ResultRow);
    }

    private async void LoadImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = ImageDialog();
        if (dialog.ShowDialog(this) == true)
        {
            await Execute(() => LoadCurrent(dialog.FileName));
        }
    }

    private async void LoadStandard_Click(object sender, RoutedEventArgs e)
    {
        if (!CanReplaceStandard())
        {
            return;
        }

        var dialog = ImageDialog();
        if (dialog.ShowDialog(this) == true)
        {
            await BuildStandardAsync([dialog.FileName], review: false);
        }
    }

    private async void UseCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (current == null)
        {
            StatusLine.Text = "请先加载图像。";
            return;
        }

        if (CanReplaceStandard())
        {
            await BuildStandardAsync([current.Path], review: false);
        }
    }

    private async void SaveStandard_Click(object sender, RoutedEventArgs e) => await Execute(() =>
    {
        SaveStandardCore();
        return Task.CompletedTask;
    });

    // 哈希验证决定原图是否可作为预览/重提取来源；标准保存的目标 Lab 始终是判定依据。
    private async void ReadStandard_Click(object sender, RoutedEventArgs e)
    {
        if (!CanReplaceStandard())
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "读取已保存标准",
            Filter = "标准配置|*.json",
            InitialDirectory = DemoRoot
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await Execute(async () =>
        {
            var p = StandardProfile.Load(dialog.FileName);
            Analysis? a = null;
            bool available = File.Exists(p.SourcePath) && StandardProfile.Hash(p.SourcePath) == p.SourceSha256;
            if (available)
            {
                a = await Task.Run(() => DetectionPipeline.Load(p.SourcePath, p.Settings));
            }

            // 恢复的目标 Lab（含人工微调）才是判定依据，不用重提取值覆盖它。
            var sample = current == null ? null : await Task.Run(() => DetectionPipeline.Load(current.Path, p.Settings));
            var result = sample == null ? null : await Task.Run(() => Inspection.Run(p, sample));
            standard = p;
            standardAnalysis = a;
            settings = p.Settings.Copy();
            current = sample;
            inspection = result;
            standardPreviewValid = available;
            standardDirty = false;
            standardFile = dialog.FileName;
            Refresh();
            StatusLine.Text = available ? "标准、逐点阈值和算法参数已恢复。" : "标准数值已恢复；原图缺失或哈希不一致，图像预览不可用，不能按原图重提取。";
        });
    }

    /// <summary>复位整个当前会话，包括 ROI、预览、选择和参数；已保存的标准 JSON 不从磁盘删除。</summary>
    private void ClearStandard_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !CanReplaceStandard())
        {
            return;
        }

        // 清除是一次完整的工作会话复位。ROI 属于 settings，而设置预览来自 current；
        // 仅清空 standard 会让下一张建标图片继续使用上一膜片的 ROI 和预览来源。
        // 保留磁盘 JSON：CanReplaceStandard 已处理未保存提示，这里只释放内存状态。
        settings = new DetectionSettings();
        standardDirty = false;
        standardFile = null;
        standard = null;
        standardAnalysis = null;
        current = null;
        inspection = null;
        standardPreviewValid = true;
        lastAttempt = null;
        lastFolder = Directory.GetParent(DemoRoot)?.FullName ?? DemoRoot;
        measurementTiming = "";
        timingImage = null;
        ErrorBanner.Text = "";
        ErrorBanner.Visibility = Visibility.Collapsed;
        ResultsGrid.SelectedItem = null;
        SyncViews.IsChecked = true;
        MaskToggle.IsChecked = false;
        ContoursToggle.IsChecked = true;
        DetailsToggle.IsChecked = false;
        EvidencePanel.IsExpanded = false;
        Refresh();
        StatusLine.Text = "已清除标准及图像，ROI 恢复全图，所有检测参数恢复默认。可重新建标；磁盘上已保存的标准 JSON 保留。";
    }

    /// <summary>按同目录图像文件的排序循环切换；复用当前标准和参数，不隐式建立新标准。</summary>
    private async Task Navigate(int direction)
    {
        if (busy || current == null)
        {
            return;
        }

        var paths = Directory.EnumerateFiles(Path.GetDirectoryName(current.Path)!)
            .Where(path => Extensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        int index = Array.FindIndex(paths, p => string.Equals(p, current.Path, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || paths.Length == 0)
        {
            return;
        }

        await Execute(() => LoadCurrent(paths[(index + direction + paths.Length) % paths.Length]));
    }

    private async void Previous_Click(object sender, RoutedEventArgs e) => await Navigate(-1);

    private async void Next_Click(object sender, RoutedEventArgs e) => await Navigate(1);

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (busy || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        string? path = paths.FirstOrDefault(p => Extensions.Contains(Path.GetExtension(p)));
        if (path != null)
        {
            await Execute(() => LoadCurrent(path));
        }
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        StandardView.ResetView();
        CurrentView.ResetView();
    }

    private void Overlay_Checked(object sender, RoutedEventArgs e)
    {
        if (StandardView == null || CurrentView == null)
        {
            return;
        }

        StandardView.ShowContours = CurrentView.ShowContours = ContoursToggle.IsChecked == true;
        StandardView.InvalidateVisual();
        CurrentView.InvalidateVisual();
    }

    private void ResultSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        int id = (ResultsGrid.SelectedItem as ResultRow)?.Id ?? -1;
        StandardView.Select(id);
        CurrentView.Select(id);
        if (id > 0 && inspection?.HasReliableAlignment != false)
        {
            StandardView.FocusMark(id);
            CurrentView.FocusMark(id);
        }

        var row = ResultsGrid.SelectedItem as ResultRow;
        UpdateEvidence(row);
    }
}
