using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MarkColorDemo;

/// <summary>
/// 自包含 HTML 检测报告：先看结论与位置，再看超限原因，原始数值折叠显示。
/// 逐张写文件，不保留整个批次的位图；图片直接嵌入，离线复制单个 HTML 仍可查看。
/// </summary>
public sealed class VisualReportWriter : IDisposable
{
    private readonly string path;
    private readonly string temporaryPath;
    private readonly StreamWriter writer;
    private readonly Dictionary<string, int> counts = [];
    private readonly bool batch;
    private bool disposed;
    private bool aborted;

    public void Abort() => aborted = true;

    private string? referenceJpeg;
    private Analysis? cachedReference;

    // 路径、标签、错误文本都先编码，避免文件名中的 <、& 或引号成为 HTML 标记。
    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static string Number(double value, string format = "F2") => double.IsFinite(value) ? value.ToString(format, CultureInfo.InvariantCulture) : "—";

    public VisualReportWriter(string path, bool batch = false)
    {
        this.path = Path.GetFullPath(path);
        this.batch = batch;
        temporaryPath = this.path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        writer = new StreamWriter(temporaryPath, false, new UTF8Encoding(false));
        writer.Write("""
            <!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Mark 颜色检测报告</title><style>
            *{box-sizing:border-box}body{margin:0;background:#edf1f5;color:#203246;font:15px/1.65 'Segoe UI','Microsoft YaHei',sans-serif}
            header,main{max-width:1220px;margin:auto}header{padding:38px 24px 20px}h1{margin:0;font-size:29px}h2{font-size:20px;margin:0 0 8px}h3{font-size:16px;margin:0 0 8px}
            main{display:flex;flex-direction:column;gap:20px;padding:0 24px 36px}.card{background:white;border:1px solid #dce3ec;border-radius:14px;padding:24px;break-inside:avoid}
            .muted{color:#61758a;font-size:13px}.badge{display:inline-block;border-radius:8px;padding:4px 13px;font-weight:700}.ok{color:#087a50;background:#def5e9}.ng{color:#b82541;background:#ffeaee}.pending{color:#925c00;background:#fff0ca}
            .heading{display:flex;align-items:center;gap:18px;flex-wrap:wrap}.metrics{display:flex;gap:12px;flex-wrap:wrap;margin:16px 0}.metric{background:#f3f6fa;border-radius:9px;padding:12px 18px;min-width:135px}.metric strong{display:block;font-size:23px}
            .images{display:grid;grid-template-columns:1fr 1fr;gap:18px;margin:18px 0}.image{background:#f5f7fa;border-radius:9px;padding:12px}svg{width:100%;height:auto;display:block}table{width:100%;border-collapse:collapse}th{background:#f1f5f9;color:#486079;text-align:left;font-size:13px}td,th{padding:12px;border-bottom:1px solid #e3eaf1;vertical-align:middle}.fail td{background:#fff8f8}.swatch{display:inline-block;width:32px;height:23px;vertical-align:middle;border:1px solid #8090a0;border-radius:4px;margin-right:5px}.bar{height:7px;background:#e1e8ee;border-radius:6px;width:170px;max-width:100%;margin-top:5px;overflow:hidden}.bar i{display:block;height:100%;background:#0d9970}.bar i.over{background:#d53551}.table-wrap{overflow-x:auto}.reason{font-size:13px;max-width:340px}.note{border-left:4px solid #d79c2a;background:#fffaed;padding:10px 14px;margin:12px 0}
            details>summary{cursor:pointer;font-weight:600}details.expert{margin-top:18px;border-top:1px solid #e3eaf1;padding-top:14px}details.expert table{font-size:12px;margin-top:10px}details.expert th,details.expert td{padding:8px}.path{overflow-wrap:anywhere}footer{margin-top:16px;font-size:12px;color:#61758a}.batch-summary{order:-1}.entry>summary{display:flex;gap:16px;align-items:center;flex-wrap:wrap}.entry[open]>summary{margin-bottom:18px}
            @media(max-width:750px){.images{grid-template-columns:1fr}header,main{padding-left:12px;padding-right:12px}.card{padding:16px}td,th{padding:8px}}
            @media print{body{background:white;font-size:11px}header{padding-top:0}main{padding:0}.card{box-shadow:none;border-radius:0;break-inside:auto}.images{break-inside:avoid}tr{break-inside:avoid}.expert{display:none}header .muted{font-size:11px}}
            </style></head><body><header><div class="muted">MARK COLOR INSPECTION</div><h1>Mark 颜色检测报告</h1>
            """);
        writer.Write($"<div class=muted>生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss} · 支持离线查看和浏览器打印</div></header><main>");
    }

    /// <summary>标准/待测必须来自同一检测任务。未可靠配准时不显示“确认缺失”或颜色合格。</summary>
    public void Write(StandardProfile? standard, Analysis? reference, Analysis image, Inspection? result)
    {
        string verdict = result?.Decision.ReportLabel ?? "仅量化";
        string tone = verdict switch
        {
            "OK" => "ok",
            "NG" => "ng",
            _ => "pending"
        };
        string fileName = Html(Path.GetFileName(image.Path));
        writer.Write(batch ? $"<details class='card entry'><summary><span class='badge {tone}'>{verdict}</span>{fileName}</summary>" : "<article class=card>");
        writer.Write($"<div class=heading><span class='badge {tone}'>{verdict}</span><h2>{fileName}</h2></div>");
        writer.Write($"<div class='muted path'>标准：{Html(standard?.Name ?? "未设置")} · 待测图像：{image.Width} × {image.Height}</div>");
        if (standard != null)
        {
            writer.Write($"<div class=muted>{Html(standard.Settings.ColorDifference.Description)} · 参数因子，不是百分比</div>");
        }
        writer.Write($"<p>{Html(result?.Summary(standard!.Marks.Count) ?? $"提取 {image.Marks.Count} 个圆点；未设置标准，不作合格判定。")}</p>");
        if (result != null)
        {
            writer.Write($"<div class=muted>{Html(result.AlignmentDescription)}</div>");
            if (!result.HasReliableAlignment)
            {
                writer.Write("<p class=note>对应关系尚未确定。图中标注为候选，色差不作为合格结论；请检查版式及匹配参数。</p>");
            }
            else if (result.Alignment.Extra > 0)
            {
                writer.Write($"<p class=note>检测到 {result.Alignment.Extra} 个多余点。即使已匹配点的颜色通过，整图仍为 NG。</p>");
            }
        }

        writer.Write("<div class=images><section class=image><h3>标准 OK 图</h3>");
        WriteImage(reference, standard, null, true);
        writer.Write("</section><section class=image><h3>待测图 · 点位与状态</h3>");
        WriteImage(image, null, result, false);
        writer.Write("</section></div><p class=muted>绿色：通过 · 红色：颜色超限 / 确认缺失 · 橙色：候选 / 多余点。图像为显示预览，测色使用原图像素。</p>");
        WriteTable(standard, image, result);
        WriteDetails(standard, image, result);
        writer.Write(batch ? "</details>" : "</article>");
        Count(verdict);
    }

    private void Count(string verdict) => counts[verdict] = counts.GetValueOrDefault(verdict) + 1;

    // 缩略图编码只影响报告大小，不改变分析结果；同一批次的标准 JPEG 只编码一次。
    private string Jpeg(Analysis analysis, bool reference)
    {
        if (reference && ReferenceEquals(cachedReference, analysis) && referenceJpeg != null)
        {
            return referenceJpeg;
        }

        ImagePreview.Prepare(analysis);
        if (analysis.Preview == null)
        {
            return "";
        }

        BitmapSource bitmap = analysis.Preview;
        if (bitmap.PixelWidth > 1200)
        {
            double scale = 1200.0 / bitmap.PixelWidth;
            bitmap = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
            bitmap.Freeze();
        }

        var encoder = new JpegBitmapEncoder
        {
            QualityLevel = 86
        };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        string encoded = Convert.ToBase64String(buffer.ToArray());
        if (reference)
        {
            cachedReference = analysis;
            referenceJpeg = encoded;
        }

        return encoded;
    }

    // SVG viewBox 保持原图像素坐标，缩略 JPEG 只是背景；轮廓不在缩略图上重新检测。
    /// <summary>预览可压缩，但 SVG 仍采用原图坐标；轮廓和标签共同缩放，不改变测量结果。</summary>
    private void WriteImage(Analysis? analysis, StandardProfile? standard, Inspection? result, bool reference)
    {
        if (analysis == null || Jpeg(analysis, reference) is not { Length: > 0 } jpeg)
        {
            writer.Write("<p class=muted>无可用图像预览；已保存的标准数值仍可参与检测。</p>");
            return;
        }

        writer.Write($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {analysis.Width} {analysis.Height}' role=img aria-label='圆点检测标注图'>");
        writer.Write($"<image width='{analysis.Width}' height='{analysis.Height}' href='data:image/jpeg;base64,{jpeg}'/>");
        double lineWidth = analysis.Width / 450.0;
        for (int index = 0; index < analysis.Marks.Count; ++index)
        {
            var mark = analysis.Marks[index];
            var row = result?.Rows.FirstOrDefault(candidate => candidate.ObservedIndex == index);
            string color = OverlayColor(result, row);
            string label = result?.ObservedLabel(index) ?? $"#{standard?.Marks.ElementAtOrDefault(index)?.Id ?? index + 1}";
            if (index < analysis.Contours.Count && analysis.Contours[index].Length > 0)
            {
                string points = string.Join(" ", analysis.Contours[index].Select(point => $"{Number(point.X)},{Number(point.Y)}"));
                writer.Write($"<polygon points='{points}' fill=none stroke='{color}' stroke-width='{Number(lineWidth)}'/>");
            }
            else
            {
                Circle(mark.X, mark.Y, mark.Radius, color, lineWidth, false);
            }

            Label(mark.X, mark.Y - mark.Radius, label, color, analysis.Width, analysis.Height);
        }

        if (result != null)
        {
            for (int index = 0; index < result.Comparisons.Length; ++index)
            {
                if (!result.IsConfirmedMissing(index))
                {
                    continue;
                }

                var comparison = result.Comparisons[index];
                Circle(comparison.ExpectedX, comparison.ExpectedY, comparison.ExpectedRadius, "#e02746", lineWidth, true);
                Label(
                    comparison.ExpectedX,
                    comparison.ExpectedY - comparison.ExpectedRadius,
                    $"#{result.Rows[index].Id} 缺失",
                    "#e02746",
                    analysis.Width,
                    analysis.Height);
            }
        }

        writer.Write("</svg>");
    }

    private static string OverlayColor(Inspection? result, ResultRow? row)
    {
        if (result == null)
        {
            return "#008fac";
        }

        if (!result.HasReliableAlignment || row == null)
        {
            return "#cf8200";
        }

        return row.Pass ? "#008b52" : "#e02746";
    }

    private void Circle(double x, double y, double radius, string color, double width, bool dashed) => writer.Write($"<circle cx='{Number(x)}' cy='{Number(y)}' r='{Number(radius)}' fill=none stroke='{color}' stroke-width='{Number(width)}' {(dashed ? "stroke-dasharray='12 8'" : "")}/>");

    private void Label(double x, double y, string label, string color, int width, int height)
    {
        double fontSize = width / 55.0;
        double boxWidth = Math.Min(width, Math.Max(fontSize * 3, label.Length * fontSize));
        x = Math.Clamp(x - boxWidth / 2, 0, width - boxWidth);
        y = Math.Clamp(y - fontSize * 1.7, 0, height - fontSize * 1.4);
        writer.Write($"<rect x='{Number(x)}' y='{Number(y)}' width='{Number(boxWidth)}' height='{Number(fontSize * 1.4)}' rx='8' fill='{color}'/>");
        writer.Write($"<text x='{Number(x + fontSize * .2)}' y='{Number(y + fontSize)}' fill='white' font-size='{Number(fontSize)}'>{Html(label)}</text>");
    }

    private static string Swatch(Mark? mark) => mark is Mark value ? $"<span class=swatch style='background:{Inspection.Swatch(value)}'></span>" : "<span class=muted>—</span>";

    // 展示行复用 Inspection 的强类型结论；质量失败不能被临时计算的低色差覆盖。
    /// <summary>复用检测行的结论和质量原因；报告不能绕过测量资格自行给出 OK。</summary>
    private void WriteTable(StandardProfile? standard, Analysis image, Inspection? result)
    {
        writer.Write("<h3>逐点结论</h3><div class=table-wrap><table><thead><tr><th>Mark / 对应墨路</th><th>标准 → 待测</th><th>ΔE00 / 阈值</th><th>结论与原因</th></tr></thead><tbody>");
        if (standard != null && result != null)
        {
            for (int index = 0; index < standard.Marks.Count; ++index)
            {
                var target = standard.Marks[index];
                var comparison = result.Comparisons[index];
                var row = result.Rows[index];
                Mark? measured = comparison.ObservedIndex >= 0 ? image.Marks[comparison.ObservedIndex] : null;
                bool reliable = result.HasReliableAlignment && comparison.Status == 0 && row.Decision.Verdict != DetectionVerdict.Unmeasurable;
                writer.Write($"<tr class='{(row.Pass ? "" : "fail")}'><td><strong>#{target.Id} {Html(target.Name)}</strong><div class=muted>{Html(target.InkChannel)}</div></td>");
                writer.Write($"<td>{Swatch(target.Value)} → {Swatch(measured)}</td><td>");
                if (reliable)
                {
                    double ratio = comparison.DeltaE00 / target.DeltaEThreshold;
                    writer.Write($"<strong>{Number(comparison.DeltaE00, "F3")}</strong> / {Number(target.DeltaEThreshold)}<div class=bar><i class='{(ratio > 1 ? "over" : "")}' style='width:{Number(Math.Clamp(ratio * 100, 0, 100))}%'></i></div>");
                }
                else
                {
                    writer.Write("<span class=muted>不判定</span>");
                }

                writer.Write($"</td><td><strong>{Html(row.Status)}</strong><div class=reason>{Html(row.Reason)}</div></td></tr>");
            }
        }
        else
        {
            for (int index = 0; index < image.Marks.Count; ++index)
            {
                writer.Write($"<tr><td>#{index + 1}</td><td>— → {Swatch(image.Marks[index])}</td><td>无标准</td><td>仅量化，不作 OK/NG 判定</td></tr>");
            }

            if (image.Marks.Count == 0)
            {
                writer.Write("<tr><td colspan=4>未检测到圆点；请检查图像和提取参数。</td></tr>");
            }
        }

        writer.Write("</tbody></table></div><div class=muted>条形长度表示 ΔE00 相对阈值的比例，满条代表达到阈值，超限为红色；其他已启用的通道差阈值仍独立生效。色块为采集 RGB 均值示意，不能替代数值判定。</div>");
    }

    /// <summary>解释与 Inspection 相同的阈值语义：0 表示未启用，等于阈值允许通过。</summary>
    public static string Reason(StandardMark target, Comparison comparison, bool reliable) => reliable ? MeasurementDecision.Color(target, comparison).Reason : "配准不确定，请先确认对应关系。";

    private void WriteDetails(StandardProfile? standard, Analysis image, Inspection? result)
    {
        writer.Write("<details class=expert><summary>展开测量明细与追溯信息</summary><div class=table-wrap><table><tr><th>Mark</th><th>标准 L* / a* / b*</th><th>待测 L* / a* / b*</th><th>ΔL* / Δa* / Δb*</th><th>σL / σa / σb</th><th>测量质量</th></tr>");
        var rows = result?.Rows ?? Inspection.Quantify(image);
        foreach (var row in rows)
        {
            writer.Write($"<tr><td>#{row.Id}</td><td>{Html(row.ReferenceLab)}</td><td>{Html(row.CurrentLab)}</td><td>{Html(row.Deltas)}</td><td>{Html(row.Uniformity)}</td><td>{Html(row.Quality)}<br>{Html(row.Reason)}</td></tr>");
        }

        writer.Write($"</table></div><p class='muted path'>待测：{Html(image.Path)}<br>标准：{Html(standard?.SourcePath)}<br>标准 SHA-256：{Html(standard?.SourceSha256)}<br>标准 ID：{Html(standard?.Id)} / v{standard?.Revision} / {standard?.Samples.Count} 次采样</p></details>");
    }

    public void Error(string imagePath, string message)
    {
        Count("错误");
        writer.Write($"<article class=card><span class='badge ng'>处理错误</span><h2>{Html(Path.GetFileName(imagePath))}</h2><p>{Html(message)}</p></article>");
    }

    public void Cancelled(int completed, int total) => writer.Write($"<p class=note>批量已取消：已处理 {completed}/{total} 张，未处理的图像没有检测结论。</p>");

    // 正常完成或用户取消后写入统计并关闭 HTML，最后替换目标文件，避免留下半份文档。
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (aborted)
        {
            writer.Dispose();
            return;
        }

        try
        {
            writer.Write($"<section class='card batch-summary'><h2>{(batch ? "批次汇总" : "检测概览")}</h2><div class=metrics>");
            foreach (string status in new[]
            {
                "OK",
                "NG",
                "待确认",
                "无法可靠测量",
                "仅量化",
                "错误"
            })
            {
                writer.Write($"<div class=metric>{status}<strong>{counts.GetValueOrDefault(status)}</strong></div>");
            }

            writer.Write("</div><div class=muted>数量按图像计；待确认与处理错误均不计为 OK。批量报告请展开每张图查看详情；打印前先展开需要的条目。</div></section></main></body></html>");
        }
        finally
        {
            writer.Dispose();
        }

        File.Move(temporaryPath, path, true);
    }
}
