using System.IO;

namespace MarkColorDemo;

/// <summary>导出会话：按扩展名选择面向查看的 HTML 或保留全部测量字段的 CSV。</summary>
public sealed class ReportExport : IDisposable
{
    public const string Filter = "可视化检测报告（浏览器打开）|*.html|详细测量数据（CSV）|*.csv";

    public static bool NeedsPreview(string path) => !Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase);

    private readonly VisualReportWriter? visual;
    private readonly StreamWriter? csv;
    private readonly string path;
    private readonly string? csvTemporary; // 成功关闭后才改名，失败时不覆盖已有报告。
    private bool failed, disposed;

    public ReportExport(string path, bool batch = false)
    {
        this.path = Path.GetFullPath(path);
        if (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            csvTemporary = this.path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            csv = ReportWriter.Open(csvTemporary);
        }
        else
        {
            visual = new VisualReportWriter(path, batch);
        }
    }

    public void Write(StandardProfile? standard, Analysis? reference, Analysis image, Inspection? result)
    {
        Attempt(() =>
        {
            if (visual != null)
            {
                visual.Write(standard, reference, image, result);
            }
            else
            {
                ReportWriter.Write(csv!, standard, image, result);
            }
        });
    }

    public void Error(string path, string message)
    {
        Attempt(() =>
        {
            if (visual != null)
            {
                visual.Error(path, message);
            }
            else
            {
                ReportWriter.Error(csv!, path, message);
            }
        });
    }

    public void Cancelled(int completed, int total)
    {
        Attempt(() =>
        {
            if (visual != null)
            {
                visual.Cancelled(completed, total);
            }
            else
            {
                ReportWriter.Error(csv!, "批次", $"用户取消；已处理 {completed}/{total}。");
            }
        });
    }

    // 写入失败必须标记整个导出会话；Dispose 不能再把部分文件当成成功结果提交。
    private void Attempt(Action write)
    {
        try
        {
            write();
        }
        catch
        {
            failed = true;
            visual?.Abort();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        visual?.Dispose();
        csv?.Dispose();
        if (!failed && csvTemporary != null)
        {
            File.Move(csvTemporary, path, true);
        }
    }
}
