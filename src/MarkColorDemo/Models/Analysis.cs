using System.Windows.Media.Imaging;

namespace MarkColorDemo;

/// <summary>一次提取的快照：原图坐标、颜色数值、轮廓与可选显示位图属于同一次加载。</summary>
public sealed class Analysis
{
    public string Path { get; set; } = "";

    public int Width
    {
        get; set;
    }

    public int Height
    {
        get; set;
    }

    // 三个列表严格同下标，Native 已按 X 排序；这个顺序不表示与标准的对应关系。
    public List<MarkQuality> Qualities { get; set; } = [];

    public List<Mark> Marks { get; set; } = [];

    public List<NativePoint[]> Contours { get; set; } = [];

    public NativeTimings Timings
    {
        get; set;
    }

    // 单独统计位图复制成本，DetectionPipeline 从总分析耗时中扣除，避免重复计时。
    public double PreviewMilliseconds
    {
        get; set;
    }

    // 仅用于界面/报告，不参与测色，不写入标准文件；更换 Analysis 时旧位图可被回收。
    // 不做按路径的全局缓存，避免文件被覆盖后还显示旧图，也避免批量任务常驻全部图像。
    public BitmapSource? Preview
    {
        get; set;
    }
}
