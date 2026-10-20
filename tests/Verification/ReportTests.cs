// 报告回归覆盖 CSV 列对齐、文本转义及 HTML 状态；它不证明浏览器外观或现场测色准确度。
using System.IO;
using MarkColorDemo;

/// <summary>检查报告结论与 UI 共用同一判定，并生成可人工查看的回归样例。</summary>
/// <summary>验证导出证据、失败/取消提交和预览复用；报告只展示已有判定，不能改变测量。</summary>
internal static class ReportTests
{
    public static void Run(
        string directory,
        StandardProfile profile,
        Analysis reference,
        Analysis observed,
        Inspection result,
        Inspection uncertain)
    {
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception("报告验证：" + message);
            }
        }

        string visualPath = Path.Combine(directory, "visual-report.html");
        using (var report = new ReportExport(visualPath))
        {
            report.Write(profile, reference, observed, result);
        }

        string html = File.ReadAllText(visualPath);
        Check(html.Contains("data:image/jpeg;base64,") && html.Contains("<polygon"), "嵌入图像与原始轮廓");
        Check(html.Contains("ΔE00 超限") && html.Contains("测量明细与追溯信息"), "显示原因并折叠明细");
        Check(html.EndsWith("</html>"), "HTML 完整关闭");
        string batchPath = Path.Combine(directory, "visual-batch-report.html");
        using (var report = new ReportExport(batchPath, batch: true))
        {
            report.Write(profile, reference, reference, Inspection.Run(profile, reference));
            report.Write(profile, reference, observed, result);
            report.Write(profile, reference, observed, uncertain);
            report.Write(null, null, new Analysis { Width = observed.Width, Height = observed.Height }, null);
            report.Error("<script>文件.bmp", "错误消息 <script>alert(1)</script>");
            report.Cancelled(5, 7);
        }

        string batch = File.ReadAllText(batchPath);
        Check(!batch.Contains("<script>") && batch.Contains("&lt;script&gt;"), "外部文本必须 HTML 转义");
        Check(batch.Contains("待确认<strong>1") && batch.Contains("错误<strong>1"), "不确定与错误不能计入 OK");
        Check(batch.Contains("已处理 5/7") && batch.Contains("未处理的图像没有检测结论"), "取消后保留部分报告并解释未处理项");
        Check(batch.Contains("候选 P") && !batch.Contains("缺失</text>"), "歧义图只标候选，不画确认缺失");
        var missing = new Analysis
        {
            Path = observed.Path,
            Width = observed.Width,
            Height = observed.Height,
            Marks = [.. observed.Marks]
        };
        missing.Marks.RemoveAt(2);
        using (var report = new ReportExport(Path.Combine(directory, "visual-missing-report.html")))
        {
            report.Write(profile, reference, missing, Inspection.Run(profile, missing));
        }

        Check(File.ReadAllText(Path.Combine(directory, "visual-missing-report.html")).Contains("缺失</text>"), "可靠匹配的真实缺点必须标注");
        var channelTarget = profile.Marks[0].Copy();
        channelTarget.MaxDeltaL = 1;
        string reason = VisualReportWriter.Reason(channelTarget, new Comparison { Status = 0, DeltaE00 = 0, DeltaL = 2 }, true);
        Check(reason.Contains("|ΔL*|"), "ΔE 未超限时也解释通道阈值 NG");
        // 相同对象更新时必须复用冻结位图；相同路径的新分析仍重新准备快照。
        var view = new ImageViewport();
        var copied = DetectionPipeline.Load(observed.Path, profile.Settings);
        Check(
            copied.Preview != null && copied.Preview.IsFrozen && copied.Preview.PixelWidth == observed.Width,
            "Native 句柄释放后预览仍有效且维持原尺寸");
        var scanline = new byte[observed.Width * 3];
        copied.Preview!.CopyPixels(new System.Windows.Int32Rect(0, 0, observed.Width, 1), scanline, scanline.Length, 0);
        view.Set(observed, null, result, profile.Settings.InnerRatio);
        var preview = observed.Preview;
        view.Set(observed, null, uncertain, profile.Settings.InnerRatio);
        Check(preview != null && preview.IsFrozen && ReferenceEquals(preview, observed.Preview), "刷新复用可跨线程位图");
        var reloaded = new Analysis
        {
            Path = observed.Path,
            Width = observed.Width,
            Height = observed.Height
        };
        view.Set(reloaded, null, null, profile.Settings.InnerRatio);
        Check(reloaded.Preview != null && !ReferenceEquals(preview, reloaded.Preview), "新加载不能按路径误用旧图");
    }
}
