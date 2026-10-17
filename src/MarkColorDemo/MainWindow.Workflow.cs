using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace MarkColorDemo;

// 建标、失败恢复、保存确认与左右视图联动；不在事件处理器中重复实现测色。
public partial class MainWindow
{
    private bool standardDirty; // 与磁盘保存内容是否不同，控制替换/关闭前的保存提示。
    private string? standardFile;
    private string? lastAttempt;

    private bool SaveStandardCore()
    {
        if (standard == null)
        {
            throw new InvalidOperationException("请先建立标准。");
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存标准与参数",
            Filter = "标准配置|*.json",
            FileName = standard.Name.Replace(" · ", "_") + ".mark-standard.json",
            InitialDirectory = standardFile == null ? DemoRoot : Path.GetDirectoryName(standardFile)
        };
        if (dialog.ShowDialog(this) != true)
        {
            return false;
        }

        standard.Save(dialog.FileName);
        standardFile = dialog.FileName;
        standardDirty = false;
        Refresh();
        StatusLine.Text = $"标准已保存：{standardFile}";
        return true;
    }

    // 这是应用内防止丢失未保存工作的交互，不影响后台检测。
    private bool CanReplaceStandard()
    {
        if (!standardDirty || standard == null)
        {
            return true;
        }

        var answer = MessageBox.Show(
            this,
            "当前标准有未保存修改。是否先保存？\n选择“否”将放弃未保存修改，选择“取消”保留当前状态。",
            "未保存的标准",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (answer == MessageBoxResult.No)
        {
            return true;
        }

        try
        {
            return SaveStandardCore();
        }
        catch (Exception ex)
        {
            StatusLine.Text = "保存失败，当前标准保留：" + ex.Message;
            return false;
        }
    }

    private async void MultiStandard_Click(object sender, RoutedEventArgs e)
    {
        if (!CanReplaceStandard())
        {
            return;
        }

        var dialog = ImageDialog(true);
        dialog.Title = "选择同一合格膜片在不同位置的多次拍摄（至少两张）";
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (dialog.FileNames.Length < 2)
        {
            StatusLine.Text = "多图建标请至少选择两张图；单张图请使用“单图建标”。";
            return;
        }

        await BuildStandardAsync(dialog.FileNames, review: true);
    }

    // 先构造并审核新标准，再重新比较当前图，最后一次性替换窗口状态。
    private async Task BuildStandardAsync(string[] paths, bool review)
    {
        await Execute(async () =>
        {
            lastAttempt = paths[0];
            var progress = new Progress<string>(message => StatusLine.Text = message);
            var prepared = await StandardBuilder.BuildWithRecoveryAsync(paths, settings, AdjustStandardSourceAsync, message => ((IProgress<string>)progress).Report(message));
            if (prepared == null)
            {
                Refresh();
                StatusLine.Text = "已取消建标；原标准、检测参数和待测结果保留。";
                return;
            }

            var built = prepared.Standard;
            if (review && !ReviewStandard(built.Profile))
            {
                return;
            }

            // ROI 草稿只在建标成功后提交。已有待测图也须按新参数提取，避免新标准配旧测量。
            var sample = current;
            if (sample != null && !settings.SameExtraction(prepared.Settings))
            {
                sample = await Task.Run(() => DetectionPipeline.Load(sample.Path, prepared.Settings));
            }

            var result = sample == null ? null : await Task.Run(() => Inspection.Run(built.Profile, sample));
            settings = prepared.Settings;
            current = sample;
            standard = built.Profile;
            standardAnalysis = built.Reference;
            inspection = result;
            standardPreviewValid = true;
            standardDirty = true;
            standardFile = null;
            Refresh();
            StatusLine.Text = $"已应用 {paths.Length} 次采样均值标准；请确认墨路、阈值并保存。标准图显示第一张几何基准，判定使用采样均值。";
        });
    }

    private async Task<DetectionSettings?> AdjustStandardSourceAsync(StandardBuildException problem, DetectionSettings draft)
    {
        StatusLine.Text = "建标需要调整：" + problem.Message;
        var preview = await Task.Run(() =>
        {
            var source = problem.Image ?? new Analysis
            {
                Path = problem.ImagePath
            };
            ImagePreview.Prepare(source);
            if (source.Preview != null)
            {
                source.Width = source.Preview.PixelWidth;
                source.Height = source.Preview.PixelHeight;
            }

            return source;
        });
        // 本次失败图就是 ROI 来源；不使用 current，避免用户先加载待测图的绕行操作。
        var dialog = new SettingsWindow(draft, null, preview)
        {
            Owner = this
        };
        dialog.ConfigureForStandardRecovery(problem.Message);
        return dialog.ShowDialog() == true ? dialog.Settings : null;
    }

    private bool ReviewStandard(StandardProfile profile)
    {
        var window = new Window
        {
            Owner = this,
            Title = "复核多图标准",
            Width = 1000,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new DockPanel
        {
            Margin = new Thickness(20)
        };
        var info = new TextBlock
        {
            Text = $"{profile.Samples.Count} 次采样，{profile.Marks.Count} 个对应点。下表的 σ 是不同拍摄间的样本标准差。\n每张图片等权平均；阈值未自动放宽，请确认所选图片均属于同一合格膜片。\n{profile.Marks.Count(m => m.MaximumSampleDeltaE > m.DeltaEThreshold)} 个点的采样偏差超过当前色差阈值；请核查照明与采样波动，均值标准不保证全部参与图片通过。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(info, Dock.Top);
        panel.Children.Add(info);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancel = new Button
        {
            Content = "取消",
            IsCancel = true
        };
        var apply = new Button
        {
            Content = "应用此标准",
            IsDefault = true
        };
        apply.Click += (_, _) => window.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(apply);
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(actions);
        var sources = new TextBlock
        {
            Text = string.Join("\n", profile.Samples.Select(s => s.Path)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 10)
        };
        var scroll = new ScrollViewer
        {
            Content = sources,
            MaxHeight = 130
        };
        DockPanel.SetDock(scroll, Dock.Bottom);
        panel.Children.Add(scroll);
        var grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = true,
            ItemsSource = profile.Marks.Select(m => new { 编号 = m.Id, 名称 = m.Name, 次数 = m.CaptureCount, 平均Lab = m.RawLab, 拍摄间标准差 = m.RepeatabilityText, 最大采样色差 = m.MaximumSampleDeltaE.ToString("F3"), 色差阈值 = m.DeltaEThreshold }).ToList()
        };
        panel.Children.Add(grid);
        window.Content = panel;
        return window.ShowDialog() == true;
    }

    // 由标准 Id 联动，避免两个视图的候选排序不同造成选错点。
    private void WireViewInteractions()
    {
        StandardView.MarkClicked += id => SelectRow(id);
        CurrentView.MarkClicked += id => SelectRow(id);
        StandardView.ViewChanged += () =>
        {
            if (SyncViews.IsChecked == true)
            {
                CurrentView.CopyViewFrom(StandardView);
            }
        };
        CurrentView.ViewChanged += () =>
        {
            if (SyncViews.IsChecked == true)
            {
                StandardView.CopyViewFrom(CurrentView);
            }
        };
        StandardView.ViewReset += () =>
        {
            if (SyncViews.IsChecked == true)
            {
                CurrentView.ResetView();
            }
        };
        CurrentView.ViewReset += () =>
        {
            if (SyncViews.IsChecked == true)
            {
                StandardView.ResetView();
            }
        };
    }

    private void SelectRow(int id)
    {
        var rows = ResultsGrid.ItemsSource as IEnumerable<ResultRow>;
        var row = rows?.FirstOrDefault(r => r.Id == id);
        if (row == null)
        {
            return;
        }

        ResultsGrid.SelectedItem = row;
        ResultsGrid.ScrollIntoView(row);
    }

    private void Mask_Checked(object sender, RoutedEventArgs e)
    {
        if (StandardView == null || CurrentView == null)
        {
            return;
        }

        StandardView.ShowSampleMask = CurrentView.ShowSampleMask = MaskToggle.IsChecked == true;
        StandardView.InvalidateVisual();
        CurrentView.InvalidateVisual();
    }
}
