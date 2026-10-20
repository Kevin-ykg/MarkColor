using System.Diagnostics;

namespace MarkColorDemo;

/// <summary>工作线程上的单图流水线。图像解码、Native 调用和结果整理完成后才交给界面。</summary>
public static class DetectionPipeline
{
    // 毫秒均为本次墙钟计时；AnalyzeMs 已扣除 PreviewMs，CompareMs 含托管质量判定。
    public sealed record Result(Analysis Image, Inspection? Inspection, double AnalyzeMs, double CompareMs, double PreviewMs);

    /// <summary>由 Task.Run 调用；调用期间 UI 不得修改传入的标准与设置。</summary>
    public static Result Run(string path, DetectionSettings settings, StandardProfile? standard, bool preparePreview = true)
    {
        var timer = Stopwatch.StartNew();
        var analysis = Native.Analyze(path, settings, preparePreview: preparePreview);
        double analyzeMs = timer.Elapsed.TotalMilliseconds - analysis.PreviewMilliseconds;
        timer.Restart();
        // 未建标只返回量化值；有标准才运行统一判定，报告生成不在这段检测计时内。
        var inspection = standard == null ? null : Inspection.Run(standard, analysis);
        double compareMs = timer.Elapsed.TotalMilliseconds;
        return new Result(analysis, inspection, analyzeMs, compareMs, analysis.PreviewMilliseconds);
    }

    /// <summary>建立/恢复标准时只提取和准备预览，不隐式改变已保存的目标 Lab。</summary>
    public static Analysis Load(string path, DetectionSettings settings) => Run(path, settings, null).Image;
}
