using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace MarkColorDemo;

// 单图导出、批量进度与取消；数值检测和文件编码交给后台流水线。
public partial class MainWindow
{
    /// <summary>导出现有检测快照，不为导出再次检测；HTML 和 CSV 共享同一份 Inspection 结论。</summary>
    private async void Export_Click(object sender, RoutedEventArgs e) => await Execute(async () =>
    {
        if (current == null)
        {
            throw new InvalidOperationException("请先加载图像。");
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出检测报告（默认可视化报告，也可选择详细 CSV）",
            Filter = ReportExport.Filter,
            FileName = $"Mark检测_{DateTime.Now:yyyyMMdd_HHmmss}",
            DefaultExt = ".html",
            InitialDirectory = DemoRoot
        };
        if (dialog.ShowDialog(this) == true)
        {
            // 编码预览/写磁盘可能比色差计算更慢，交给工作线程，避免导出期间窗口失去响应。
            await Task.Run(() =>
            {
                using var report = new ReportExport(dialog.FileName);
                report.Write(standard, standardAnalysis, current, inspection);
            });
            StatusLine.Text = $"结果已导出：{dialog.FileName}";
        }
    });

    // 批量按图串行处理，限制位图常驻量；取消在单图边界生效，已完成的结果仍写成有效报告。
    private async void Batch_Click(object sender, RoutedEventArgs e)
    {
        if (standard == null)
        {
            MessageBox.Show(this, "请先设置标准。批量文件应使用同一打印方向和视野。", "批量检测");
            return;
        }

        var images = ImageDialog(true);
        if (images.ShowDialog(this) != true)
        {
            return;
        }

        var output = new SaveFileDialog
        {
            Title = "保存批量检测报告",
            Filter = ReportExport.Filter,
            FileName = $"批量检测_{DateTime.Now:yyyyMMdd_HHmmss}",
            DefaultExt = ".html",
            InitialDirectory = DemoRoot
        };
        if (output.ShowDialog(this) != true)
        {
            return;
        }

        await Execute(async () =>
        {
            cancellation = new CancellationTokenSource();
            CancelBatch.Visibility = Visibility.Visible;
            try
            {
                int done = 0;
                var counts = new Dictionary<string, int>();
                var progress = new Progress<BatchDetector.Progress>(p =>
                {
                    done = p.Completed;
                    string verdict = p.Item.Result?.Inspection?.Decision.ReportLabel ?? "错误";
                    counts[verdict] = counts.GetValueOrDefault(verdict) + 1;
                    // CSV 批次不生成预览，不在 UI 再读图；HTML 已有冻结位图时才更新视图。
                    if (p.Item.Result is { } item && item.Image.Preview != null)
                    {
                        current = item.Image;
                        inspection = item.Inspection;
                        Refresh();
                    }

                    StatusLine.Text = $"批量 {done}/{p.Total} · {string.Join(" / ", counts.Select(c => $"{c.Key} {c.Value}"))} · 报告 {p.ReportMs:F1} ms";
                });
                await BatchDetector.RunAsync(images.FileNames, settings.Copy(), standard!.Copy(), standardAnalysis, output.FileName, progress, cancellation.Token);
                StatusLine.Text = $"批量{(cancellation.IsCancellationRequested ? "已取消" : "完成")}，报告：{output.FileName}。";
            }
            finally
            {
                cancellation.Dispose();
                cancellation = null;
                CancelBatch.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void CancelBatch_Click(object sender, RoutedEventArgs e)
    {
        cancellation?.Cancel();
        StatusLine.Text = "将在当前图像处理完成后取消。";
    }
}
