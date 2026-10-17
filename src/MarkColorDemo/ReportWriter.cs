using System.Globalization;
using System.IO;
using System.Text;

namespace MarkColorDemo;

/// <summary>固定列顺序的详细 CSV；用具名列填充行，避免缺失/错误行因空串数量而错列。</summary>
public static class ReportWriter
{
    // 既有 37 列顺序保留，新参数只追加到末尾，便于现有导入程序兼容。
    private enum Column
    {
        Image, StandardName, StandardSha256, Verdict, Id, Name, Ink, Status,
        ReferenceL, ReferenceA, ReferenceB, CurrentL, CurrentA, CurrentB,
        DeltaE00, DeltaE76, DeltaL, DeltaA, DeltaB, Threshold, LimitL, LimitA, LimitB,
        X, Y, Radius, Circularity, SampleCount, StdL, StdA, StdB, BackgroundL,
        Extra, Angle, Scale, PositionRms, Reason, KL, KC, KH, Count
    }

    private static readonly string[] Headers =
    [
        "图像", "标准名称", "标准图像SHA256", "全图结果", "ID", "名称", "墨路", "逐点结果",
        "标准L", "标准a", "标准b", "待测L", "待测a", "待测b",
        "ΔE00", "ΔE76", "ΔL", "Δa", "Δb", "ΔE阈值", "|ΔL|阈值", "|Δa|阈值", "|Δb|阈值",
        "中心X", "中心Y", "半径", "圆度", "样本像素", "σL", "σa", "σb", "背景L",
        "多余点数", "旋转度", "比例", "匹配RMS", "备注", "kL", "kC", "kH"
    ];

    private static string Cell(object? value)
    {
        string text = value is double number
            ? double.IsFinite(number) ? number.ToString("G10", CultureInfo.InvariantCulture) : ""
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        // 用户标签/路径按文本导出，防止被表格软件解释为公式；数值负号不受影响。
        if (value is string && text.Length > 0 && "=+-@\t\r".Contains(text[0]))
        {
            text = "'" + text;
        }
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    public static StreamWriter Open(string path)
    {
        if (Headers.Length != (int)Column.Count)
        {
            throw new InvalidOperationException("CSV 列名和字段定义不一致。");
        }
        var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine(string.Join(",", Headers.Select(Cell)));
        return writer;
    }

    private static void Row(TextWriter writer, params (Column Column, object? Value)[] cells)
    {
        var values = new object?[(int)Column.Count];
        foreach (var cell in cells)
        {
            values[(int)cell.Column] = cell.Value;
        }
        writer.WriteLine(string.Join(",", values.Select(Cell)));
    }

    public static void Write(TextWriter writer, StandardProfile? standard, Analysis image, Inspection? result)
    {
        if (standard != null && result != null)
        {
            for (int index = 0; index < standard.Marks.Count; index++)
            {
                var target = standard.Marks[index];
                var comparison = result.Comparisons[index];
                Mark? observed = comparison.ObservedIndex >= 0 ? image.Marks[comparison.ObservedIndex] : null;
                var factors = standard.Settings.ColorDifference;
                Row(writer,
                    (Column.Image, image.Path),
                    (Column.StandardName, standard.Name),
                    (Column.StandardSha256, standard.SourceSha256),
                    (Column.Verdict, result.Decision.ReportLabel),
                    (Column.Id, target.Id),
                    (Column.Name, target.Name),
                    (Column.Ink, target.InkChannel),
                    (Column.Status, result.Rows[index].Status),
                    (Column.ReferenceL, target.L),
                    (Column.ReferenceA, target.A),
                    (Column.ReferenceB, target.B),
                    (Column.CurrentL, observed?.L),
                    (Column.CurrentA, observed?.A),
                    (Column.CurrentB, observed?.B),
                    (Column.DeltaE00, comparison.DeltaE00),
                    (Column.DeltaE76, comparison.DeltaE76),
                    (Column.DeltaL, comparison.DeltaL),
                    (Column.DeltaA, comparison.DeltaA),
                    (Column.DeltaB, comparison.DeltaB),
                    (Column.Threshold, target.DeltaEThreshold),
                    (Column.LimitL, target.MaxDeltaL),
                    (Column.LimitA, target.MaxDeltaA),
                    (Column.LimitB, target.MaxDeltaB),
                    (Column.X, observed?.X),
                    (Column.Y, observed?.Y),
                    (Column.Radius, observed?.Radius),
                    (Column.Circularity, observed?.Circularity),
                    (Column.SampleCount, observed?.SampleCount),
                    (Column.StdL, observed?.StdL),
                    (Column.StdA, observed?.StdA),
                    (Column.StdB, observed?.StdB),
                    (Column.BackgroundL, observed?.BackgroundL),
                    (Column.Extra, result.Alignment.Extra),
                    (Column.Angle, result.Alignment.AngleDegrees),
                    (Column.Scale, result.Alignment.Scale),
                    (Column.PositionRms, result.Alignment.Rms),
                    (Column.Reason, result.Rows[index].Reason),
                    (Column.KL, factors.KL),
                    (Column.KC, factors.KC),
                    (Column.KH, factors.KH));
            }
            return;
        }

        // 没有标准时只写量化字段，所有阈值、色差和参数因子列保持空白。
        foreach (var (mark, index) in image.Marks.Select((mark, index) => (mark, index)))
        {
            Row(writer,
                (Column.Image, image.Path), (Column.Verdict, "未判定"),
                (Column.Id, index + 1), (Column.Name, "Mark"), (Column.Status, "已量化"),
                (Column.CurrentL, mark.L), (Column.CurrentA, mark.A), (Column.CurrentB, mark.B),
                (Column.X, mark.X), (Column.Y, mark.Y), (Column.Radius, mark.Radius),
                (Column.Circularity, mark.Circularity), (Column.SampleCount, mark.SampleCount),
                (Column.StdL, mark.StdL), (Column.StdA, mark.StdA), (Column.StdB, mark.StdB),
                (Column.BackgroundL, mark.BackgroundL));
        }
        if (image.Marks.Count == 0)
        {
            Error(writer, image.Path, "未检测到圆点；无标准时不作 OK 判定。");
        }
    }

    public static void Error(TextWriter writer, string path, string message) =>
        Row(writer, (Column.Image, path), (Column.Verdict, "错误"), (Column.Status, "处理失败"), (Column.Reason, message));
}
