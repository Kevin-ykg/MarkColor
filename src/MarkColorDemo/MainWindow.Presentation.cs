using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MarkColorDemo;

/// <summary>菜单、明细层级与证据文本的展示交互，不参与检测结论计算。</summary>
public partial class MainWindow
{
    private string measurementTiming = "";
    // 耗时与产生它的图像对象绑定；切换图片或重算结果时不能继续展示另一张图的计时。
    private Analysis? timingImage;

    private void OpenActionMenu_Click(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button { ContextMenu: { } menu } button)
        {
            return;
        }
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void DetailsToggle_Changed(object sender, RoutedEventArgs e)
    {
        // 初始化事件可能早于列实例创建；初始折叠状态由 XAML 指定。
        if (ReferenceLabColumn == null)
        {
            return;
        }
        var visibility = DetailsToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        foreach (var column in new[] { ReferenceLabColumn, CurrentLabColumn, DeltaLabColumn, UniformityColumn })
        {
            column.Visibility = visibility;
        }
    }

    private void UpdateEvidence(ResultRow? row)
    {
        string selected = row == null
            ? "点击圆点或表格行，可聚焦对应区域。"
            : $"#{row.Id} {row.Name} · {row.Status}\n{row.Reason}\n{row.Quality} · ΔE00 {row.DeltaE} / 阈值 {row.Threshold}";
        EvidenceText.Text = selected + "\n" + settings.ColorDifference.Description
            + (inspection == null ? "" : "\n" + inspection.AlignmentDescription)
            + (ReferenceEquals(timingImage, current) && !string.IsNullOrEmpty(measurementTiming)
                ? "\n最近加载时：" + measurementTiming : "");
    }
}
