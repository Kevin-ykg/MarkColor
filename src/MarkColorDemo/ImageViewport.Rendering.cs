using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MarkColorDemo;

// 只消费 Analysis/Inspection 快照绘制；显示掩膜不能反向参与测色或判定。
public sealed partial class ImageViewport
{
    /// <summary>绘制现有图像和检测证据；可被 WPF 频繁调用，不能在这里读图、检测或修改标准。</summary>
    protected override void OnRender(DrawingContext dc)
    {
        if (pendingFocus)
        {
            CenterSelectedMark();
            pendingFocus = false;
        }

        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(225, 231, 237)), null, new Rect(RenderSize));
        if (bitmap == null)
        {
            DrawText(dc, standard == null ? "加载图像后显示轮廓与取色区域" : "标准图像不可用，仍可使用已保存的颜色数值", new Point(24, 36), Brushes.SlateGray, 14);
            return;
        }

        double scale = Math.Min(ActualWidth / bitmap.PixelWidth, ActualHeight / bitmap.PixelHeight) * zoom;
        double ox = (ActualWidth - bitmap.PixelWidth * scale) / 2 + pan.X, oy = (ActualHeight - bitmap.PixelHeight * scale) / 2 + pan.Y;
        Point Screen(double x, double y) => new(ox + x * scale, oy + y * scale);
        dc.DrawImage(bitmap, new Rect(ox, oy, bitmap.PixelWidth * scale, bitmap.PixelHeight * scale));
        if (drawnRoi is Rect roi)
        {
            dc.DrawRectangle(null, new Pen(Brushes.Yellow, 2), new Rect(Screen(roi.X, roi.Y), Screen(roi.Right, roi.Bottom)));
        }

        if (!ShowContours)
        {
            return;
        }

        var marks = analysis?.Marks ?? standard?.Marks.Select(x => x.Value).ToList() ?? [];
        for (int i = 0; i < marks.Count; i++)
        {
            var m = marks[i];
            var row = inspection?.Rows.FirstOrDefault(r => r.ObservedIndex == i);
            int id = ReferenceId(i, row);
            var brush = MarkBrush(row);
            var pen = new Pen(brush, id > 0 && selectedId == id ? 3 : 1.6);
            if (analysis != null && analysis.Contours.Count > i && analysis.Contours[i].Length > 0)
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    var contour = analysis.Contours[i];
                    ctx.BeginFigure(Screen(contour[0].X, contour[0].Y), true, true);
                    ctx.PolyLineTo(contour.Skip(1).Select(p => Screen(p.X, p.Y)).ToArray(), true, false);
                }

                geometry.Freeze();
                if (ShowSampleMask)
                {
                    var core = new EllipseGeometry(
                        Screen(Math.Round(m.X), Math.Round(m.Y)),
                        Math.Round(m.Radius * innerRatio) * scale,
                        Math.Round(m.Radius * innerRatio) * scale);
                    var sample = new CombinedGeometry(GeometryCombineMode.Intersect, geometry, core);
                    dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(70, 20, 220, 240)), null, sample);
                }

                dc.DrawGeometry(null, pen, geometry);
            }
            else
            {
                dc.DrawEllipse(null, pen, Screen(m.X, m.Y), m.Radius * scale, m.Radius * scale);
            }

            dc.DrawEllipse(
                null,
                new Pen(brush, 1) { DashStyle = DashStyles.Dash },
                Screen(m.X, m.Y),
                m.Radius * innerRatio * scale,
                m.Radius * innerRatio * scale);
            var labelPoint = Screen(m.X - m.Radius, m.Y + (i % 2 == 0 ? -m.Radius : m.Radius));
            labelPoint.Y += i % 2 == 0 ? -27 : 8;
            string label = inspection?.ObservedLabel(i) ?? $"#{id}";
            var text = MakeText(label, Brushes.White, 12);
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(220, 21, 39, 53)),
                null,
                new Rect(labelPoint.X - 5, labelPoint.Y - 2, text.Width + 10, 23),
                3,
                3);
            dc.DrawText(text, labelPoint);
        }

        if (inspection != null)
        {
            for (int i = 0; i < inspection.Comparisons.Length; i++)
            {
                var c = inspection.Comparisons[i];
                if (!inspection.IsConfirmedMissing(i))
                {
                    continue;
                }

                var pen = new Pen(Brushes.OrangeRed, 2)
                {
                    DashStyle = DashStyles.Dash
                };
                dc.DrawEllipse(null, pen, Screen(c.ExpectedX, c.ExpectedY), c.ExpectedRadius * scale, c.ExpectedRadius * scale);
                DrawText(
                    dc,
                    $"#{inspection.Rows[i].Id} 缺失",
                    Screen(c.ExpectedX - c.ExpectedRadius, c.ExpectedY - c.ExpectedRadius - 40),
                    Brushes.OrangeRed,
                    13);
            }
        }
    }

    // Id 是面向操作者的稳定编号；observedIndex 是本张图候选的零基下标。
    /// <summary>标准图按标准顺序取编号，待测图通过结果行映射；候选序号不等于跨图身份。</summary>
    private int ReferenceId(int observedIndex, ResultRow? row)
    {
        if (inspection == null)
        {
            return standard?.Marks.ElementAtOrDefault(observedIndex)?.Id ?? observedIndex + 1;
        }

        // 未确定的对应关系不能使用标准编号，也不能被结果行选中高亮。
        return inspection.HasReliableAlignment ? row?.Id ?? -1 : -1;
    }

    private Brush MarkBrush(ResultRow? row)
    {
        if (inspection == null)
        {
            return new SolidColorBrush(Color.FromRgb(0, 195, 222));
        }

        if (!inspection.HasReliableAlignment || row == null)
        {
            return Brushes.Orange;
        }

        return row.Decision.Verdict switch
        {
            DetectionVerdict.Unmeasurable => Brushes.Goldenrod,
            DetectionVerdict.Pass => Brushes.LimeGreen,
            _ => Brushes.OrangeRed
        };
    }

    private FormattedText MakeText(string text, Brush brush, double size) => new(
        text,
        CultureInfo.GetCultureInfo("zh-CN"),
        FlowDirection.LeftToRight,
        new Typeface("Microsoft YaHei UI"),
        size,
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawText(DrawingContext dc, string text, Point point, Brush brush, double size) => dc.DrawText(MakeText(text, brush, size), point);
}
