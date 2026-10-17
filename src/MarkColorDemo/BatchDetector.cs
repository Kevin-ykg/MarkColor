using System.Diagnostics;
using System.Threading.Channels;

namespace MarkColorDemo;

/// <summary>有界生产/消费流水线：检测与报告编码分线程，队列最多保留两张图，写盘失败停止生产。</summary>
public static class BatchDetector
{
    public sealed record Item(string Path, DetectionPipeline.Result? Result, string? Error);

    public sealed record Progress(int Completed, int Total, Item Item, double ReportMs);

    public static async Task RunAsync(
        string[] paths,
        DetectionSettings settings,
        StandardProfile standard,
        Analysis? reference,
        string output,
        IProgress<Progress> progress,
        CancellationToken cancellation)
    {
        bool preview = ReportExport.NeedsPreview(output);
        // 容量 2 提供背压：报告编码较慢时等待消费者，避免整批高分辨率图像常驻内存。
        var channel = Channel.CreateBounded<Item>(new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true });
        // 用户取消只停止下一张生产，队列中已完成结果仍写入；报告失败则立即解除写队列等待。
        using var failed = new CancellationTokenSource();
        var producer = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                foreach (var path in paths)
                {
                    if (cancellation.IsCancellationRequested || failed.IsCancellationRequested)
                    {
                        break;
                    }

                    Item item;
                    try
                    {
                        item = new(path, DetectionPipeline.Run(path, settings, standard, preview), null);
                    }
                    catch (Exception ex)
                    {
                        // 单图失败作为报告条目传递，不能让整批其他图像失去结果。
                        item = new(path, null, ex.Message);
                    }

                    await channel.Writer.WriteAsync(item, failed.Token);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                channel.Writer.TryComplete(failure);
            }
        });
        var consumer = Task.Run(async () =>
        {
            try
            {
                using var report = new ReportExport(output, batch: true);
                int completed = 0;
                await foreach (var item in channel.Reader.ReadAllAsync())
                {
                    var timer = Stopwatch.StartNew();
                    if (item.Result is { } result)
                    {
                        report.Write(standard, reference, result.Image, result.Inspection);
                    }
                    else
                    {
                        report.Error(item.Path, item.Error!);
                    }

                    progress.Report(new(++completed, paths.Length, item, timer.Elapsed.TotalMilliseconds));
                }

                if (completed < paths.Length)
                {
                    report.Cancelled(completed, paths.Length);
                }
            }
            catch
            {
                failed.Cancel();
                throw;
            }
        });
        // 两端都结束后才返回，保证报告页脚完成；消费端异常通过 failed 解除生产端的等待。
        await Task.WhenAll(producer, consumer);
    }
}
