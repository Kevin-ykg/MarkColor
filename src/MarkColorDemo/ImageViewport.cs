using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MarkColorDemo;

/// <summary>原图像素到屏幕坐标的显示层；缩放、平移和选择不重新计算任何检测结果。</summary>
public sealed partial class ImageViewport : FrameworkElement
{
    // 相对于“适应窗口”的倍率；显示缩放不会重采样用于测色的原图。
    private const double MinimumZoom = 1;
    private const double MaximumZoom = 64;
    private const double WheelZoomStep = 1.35;

    public double ZoomFactor => zoom;

    private BitmapSource? bitmap;
    private Analysis? analysis;
    private StandardProfile? standard;
    private Inspection? inspection;
    private double zoom = 1, innerRatio = .65;
    private Vector pan; // WPF 设备无关单位 DIP；几何/取色始终使用原图 px。
    private Point? dragStart;
    private Vector dragPan;
    private int selectedId = -1;

    public event Action<int>? MarkClicked;

    public event Action? ViewChanged;

    public event Action? ViewReset;

    public event Action<Rect>? RoiSelected;

    public bool RoiSelectionEnabled
    {
        get; set;
    }

    public bool ShowSampleMask
    {
        get; set;
    }

    private Rect? drawnRoi; // 拖框内部保存原图 px；RoiSelected 对外返回 0..1 比例。
    private bool pendingFocus, focusOnResize;

    // 屏幕 DIP = 原图 px × Scale + (X,Y)，包含适应窗口倍率、用户缩放和平移。
    private (double Scale, double X, double Y) Transform()
    {
        if (bitmap == null)
        {
            return (1, 0, 0);
        }

        double scale = Math.Min(ActualWidth / bitmap.PixelWidth, ActualHeight / bitmap.PixelHeight) * zoom;
        return (scale, (ActualWidth - bitmap.PixelWidth * scale) / 2 + pan.X, (ActualHeight - bitmap.PixelHeight * scale) / 2 + pan.Y);
    }

    private Point ImagePoint(Point screen)
    {
        var t = Transform();
        return new(
            Math.Clamp((screen.X - t.X) / t.Scale, 0, analysis?.Width ?? 1),
            Math.Clamp((screen.Y - t.Y) / t.Scale, 0, analysis?.Height ?? 1));
    }

    private Mark? SelectedMark()
    {
        if (analysis == null)
        {
            return null;
        }

        // 左右图的候选索引可能不同，必须按标准编号定位已选择的同一个 mark。
        for (int index = 0; index < analysis.Marks.Count; index++)
        {
            var row = inspection?.Rows.FirstOrDefault(candidate => candidate.ObservedIndex == index);
            if (ReferenceId(index, row) == selectedId)
            {
                return analysis.Marks[index];
            }
        }

        return null;
    }

    public void FocusMark(int id)
    {
        selectedId = id;
        pendingFocus = focusOnResize = true;
        InvalidateVisual();
    }

    private void CenterSelectedMark()
    {
        if (bitmap == null || ActualWidth <= 0 || ActualHeight <= 0 || SelectedMark() is not Mark m)
        {
            return;
        }

        double fit = Math.Min(ActualWidth / bitmap.PixelWidth, ActualHeight / bitmap.PixelHeight);
        zoom = Math.Clamp(Math.Min(ActualWidth, ActualHeight) / (m.Radius * 4 * fit), MinimumZoom, MaximumZoom);
        pan = new Vector((bitmap.PixelWidth / 2.0 - m.X) * fit * zoom, (bitmap.PixelHeight / 2.0 - m.Y) * fit * zoom);
    }

    // 已选中同一标准点时对齐该点在窗口中的相对位置；未选点时同步整体平移比例。
    public void CopyViewFrom(ImageViewport source)
    {
        if (bitmap == null || source.bitmap == null || ActualWidth <= 0 || source.ActualWidth <= 0)
        {
            return;
        }

        zoom = source.zoom;
        if (SelectedMark() is Mark target && source.SelectedMark() is Mark origin)
        {
            var sourceTransform = source.Transform();
            var targetTransform = Transform();
            double dx = (sourceTransform.X + origin.X * sourceTransform.Scale - source.ActualWidth / 2) / source.ActualWidth;
            double dy = (sourceTransform.Y + origin.Y * sourceTransform.Scale - source.ActualHeight / 2) / source.ActualHeight;
            pan = new(
                (bitmap.PixelWidth / 2.0 - target.X) * targetTransform.Scale + dx * ActualWidth,
                (bitmap.PixelHeight / 2.0 - target.Y) * targetTransform.Scale + dy * ActualHeight);
        }
        else
        {
            pan = new(source.pan.X / source.ActualWidth * ActualWidth, source.pan.Y / Math.Max(1, source.ActualHeight) * ActualHeight);
        }

        InvalidateVisual(); // 不再次触发事件，避免两侧互相递归。
    }

    public void SetRoi(Rect normalized)
    {
        if (analysis == null || normalized.X < 0 || normalized.Y < 0 || normalized.Right > 1 || normalized.Bottom > 1 || normalized.Width <= 0 || normalized.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(normalized));
        }

        drawnRoi = new Rect(
            normalized.X * analysis.Width,
            normalized.Y * analysis.Height,
            normalized.Width * analysis.Width,
            normalized.Height * analysis.Height);
        RoiSelected?.Invoke(normalized);
        InvalidateVisual();
    }

    public bool ShowContours { get; set; } = true;

    public ImageViewport()
    {
        ClipToBounds = true;
        Focusable = true;
        ToolTip = "滚轮以鼠标为中心缩放（1–64 倍）· 按住左键拖动 · 双击适应窗口";
        MouseWheel += OnMouseWheel;
        MouseLeftButtonDown += OnDragStart;
        MouseMove += OnDragMove;
        MouseLeftButtonUp += OnDragEnd;
        LostMouseCapture += (_, _) => dragStart = null;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 一格为 120；保留高分辨率滚轮的小增量，避免每个输入事件都跳一整格。
        ZoomAt(e.GetPosition(this), Math.Pow(WheelZoomStep, e.Delta / 120.0));
        e.Handled = true;
    }

    /// <summary>围绕屏幕位置缩放，保持鼠标下原图坐标不变；倍率不影响检测结果。</summary>
    public void ZoomAt(Point position, double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor));
        }

        if (bitmap == null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        pendingFocus = focusOnResize = false;
        double nextZoom = Math.Clamp(zoom * factor, MinimumZoom, MaximumZoom);
        double scaleRatio = nextZoom / zoom;
        var mouseOffset = position - new Point(ActualWidth / 2, ActualHeight / 2);
        // 以鼠标位置为中心缩放，保持鼠标下的图像内容不跳动。
        pan = mouseOffset - (mouseOffset - pan) * scaleRatio;
        zoom = nextZoom;
        if (zoom == 1)
        {
            pan = default;
        }

        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ResetView();
            ViewReset?.Invoke();
            return;
        }

        dragStart = e.GetPosition(this);
        dragPan = pan;
        CaptureMouse();
        Cursor = Cursors.Hand;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (dragStart is Point start)
        {
            if (RoiSelectionEnabled && analysis != null)
            {
                var from = ImagePoint(start);
                var to = ImagePoint(e.GetPosition(this));
                drawnRoi = new Rect(from, to);
            }
            else
            {
                pan = dragPan + e.GetPosition(this) - start;
                ViewChanged?.Invoke();
            }

            InvalidateVisual();
        }
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (RoiSelectionEnabled && analysis != null && drawnRoi is Rect roi && roi.Width >= 8 && roi.Height >= 8)
        {
            SetRoi(new Rect(roi.X / analysis.Width, roi.Y / analysis.Height, roi.Width / analysis.Width, roi.Height / analysis.Height));
        }
        else if (dragStart is Point start && (e.GetPosition(this) - start).Length < 4 && analysis != null)
        {
            var point = ImagePoint(start);
            int index = analysis.Marks.FindIndex(m => double.Hypot(m.X - point.X, m.Y - point.Y) <= m.Radius * 1.2);
            if (index >= 0)
            {
                MarkClicked?.Invoke(ReferenceId(index, inspection?.Rows.FirstOrDefault(r => r.ObservedIndex == index)));
            }
        }

        dragStart = null;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }

    public void ResetView()
    {
        pendingFocus = focusOnResize = false;
        zoom = 1;
        pan = default;
        InvalidateVisual();
    }

    public void Select(int id)
    {
        selectedId = id;
        InvalidateVisual();
    }

    public void Set(
        Analysis? a,
        StandardProfile? profile,
        Inspection? result,
        double coreRatio,
        bool allowImage = true)
    {
        bool imageChanged = !ReferenceEquals(analysis, a) || bitmap == null || !allowImage;
        analysis = a;
        standard = profile;
        inspection = result;
        innerRatio = coreRatio;
        // Refresh 也会因改阈值/更新结果触发；相同 Analysis 的预览只解码一次。
        // 正常交互已在工作线程 Prepare；保留按需加载以支持测试和独立复用控件。
        if (imageChanged)
        {
            // 拖框和选中点属于旧图坐标系。换图/清空时一并释放，不能投影到下一张图上；
            // 同一 Analysis 的阈值刷新走下方分支，仍保留操作员的缩放和选择。
            drawnRoi = null;
            selectedId = -1;
            dragStart = null;
            dragPan = default;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            bitmap = null;
            if (allowImage && a != null)
            {
                ImagePreview.Prepare(a);
                bitmap = a.Preview;
            }

            ResetView();
        }
        else
        {
            // 仅颜色阈值变化时保留用户当前的缩放和平移。
            InvalidateVisual();
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (focusOnResize)
        {
            pendingFocus = true;
        }
    }

}
