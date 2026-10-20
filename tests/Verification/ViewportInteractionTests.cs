// 验证坐标变换、鼠标锚点缩放及换图复位；显示交互必须与检测像素独立。
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using MarkColorDemo;

/// <summary>缩放必须保持图像坐标稳定；不能让 ROI 与用于测色的像素错位。</summary>
internal static class ViewportInteractionTests
{
    public static void Run(Analysis analysis)
    {
        var view = Create(analysis);
        var peer = Create(analysis);
        var anchor = new Point(320, 180);
        var before = ImagePoint(view, anchor);
        view.ZoomAt(anchor, 1.35);
        Require(Math.Abs(view.ZoomFactor - 1.35) < 1e-12, "一格缩放倍率");
        Require((ImagePoint(view, anchor) - before).Length < 1e-8, "鼠标下原图坐标必须保持不变");
        peer.CopyViewFrom(view);
        Require((ImagePoint(peer, anchor) - before).Length < 1e-8 && peer.ZoomFactor == view.ZoomFactor, "双图同步");

        // 使用真实滚轮事件路径：半格增量不应被放大成一整格。
        view.ResetView();
        view.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 60) { RoutedEvent = Mouse.MouseWheelEvent });
        Require(Math.Abs(view.ZoomFactor - Math.Sqrt(1.35)) < 1e-12, "高分辨率滚轮增量");
        view.ZoomAt(anchor, 10000);
        Require(view.ZoomFactor == 64, "放大上限 64 倍");
        Rect? selectedRoi = null;
        view.RoiSelected += roi => selectedRoi = roi;
        var expectedRoi = new Rect(.2, .25, .3, .2);
        view.SetRoi(expectedRoi);
        Require(selectedRoi == expectedRoi, "高倍率 ROI 仍用原图归一化坐标");
        view.Set(analysis, null, null, .65);
        Require((Rect?)typeof(ImageViewport).GetField("drawnRoi", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view) ==
            new Rect(
                expectedRoi.X * analysis.Width,
                expectedRoi.Y * analysis.Height,
                expectedRoi.Width * analysis.Width,
                expectedRoi.Height * analysis.Height),
            "同一图像的结果刷新保留 ROI 拖框");
        view.ZoomAt(anchor, .000001);
        Require(view.ZoomFactor == 1 && (ImagePoint(view, anchor) - before).Length < 1e-8, "缩小下限恢复适应窗口");
        view.ZoomAt(anchor, 5);
        view.ResetView();
        Require(view.ZoomFactor == 1 && (ImagePoint(view, anchor) - before).Length < 1e-8, "复位后无残留平移");
        view.Select(1);
        view.Set(new Analysis
        {
            Path = analysis.Path,
            Width = analysis.Width,
            Height = analysis.Height,
            Preview = analysis.Preview
        }, null, null, .65);
        Require(typeof(ImageViewport).GetField("drawnRoi", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view) == null &&
            (int)typeof(ImageViewport).GetField("selectedId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)! == -1,
            "换图时清除旧坐标系中的 ROI 和选中点");
        Console.WriteLine("PASS: viewport wheel increments, cursor anchor, 64x limit, synchronized coordinates and ROI/reset.");
    }

    private static ImageViewport Create(Analysis analysis)
    {
        var view = new ImageViewport();
        view.Set(analysis, null, null, .65);
        view.Measure(new Size(800, 600));
        view.Arrange(new Rect(0, 0, 800, 600));
        return view;
    }

    private static Point ImagePoint(ImageViewport view, Point position) =>
        (Point)typeof(ImageViewport).GetMethod("ImagePoint", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, [position])!;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Viewport: " + message);
        }
    }
}
