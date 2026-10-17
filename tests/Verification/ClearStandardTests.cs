using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MarkColorDemo;

/// <summary>清除应隔离两次建标会话：旧 ROI、图片和证据不再影响下一张标准，磁盘标准仍可读取。</summary>
internal static class ClearStandardTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, PrivateInstance)!.GetValue(target);

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, PrivateInstance)!.SetValue(target, value);

    private static object? Call(MainWindow window, string name, params object[] arguments) =>
        typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(window, arguments);

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Clear standard: " + message);
        }
    }

    public static async Task RunAsync(string root, string output)
    {
        string upper = Path.Combine(root, "优良品", "2-上.bmp");
        string lower = Path.Combine(root, "优良品", "2-下.bmp");
        var window = new MainWindow();
        await window.LoadPairAsync(upper, upper);
        var original = (StandardProfile)Field(window, "standard")!;
        var draft = original.Settings.Copy();
        draft.RoiY = .1;
        draft.RoiHeight = .3;
        draft.InnerRatio = .6;
        draft.ColorDifference.KL = 2;
        draft.Quality.MaxOverexposedFraction = .07;
        await (Task)Call(window, "ApplySettingsAsync", draft, original.Marks, "清除回归：限定上方膜片")!;
        var active = (DetectionSettings)Field(window, "settings")!;
        Check(active.RoiHeight == .3, "旧 ROI 确已通过真实设置应用流程提交");
        Check(Native.Analyze(lower, active).Marks.Count == 0, "前置条件：下方圆点确实位于旧 ROI 外");

        // 模拟已经保存的标准，点击真实清除事件时不需要自动操作保存提示框。
        string savedPath = Path.Combine(output, "clear-standard-preserved.json");
        ((StandardProfile)Field(window, "standard")!).Save(savedPath);
        string savedJson = File.ReadAllText(savedPath);
        SetField(window, "standardDirty", false);
        SetField(window, "standardFile", savedPath);
        SetField(window, "lastAttempt", lower);
        SetField(window, "measurementTiming", "旧检测耗时");
        SetField(window, "timingImage", Field(window, "current"));
        var banner = (TextBlock)window.FindName("ErrorBanner");
        banner.Text = "旧 ROI 无圆点";
        banner.Visibility = Visibility.Visible;
        ((CheckBox)window.FindName("MaskToggle")).IsChecked = true;
        ((CheckBox)window.FindName("ContoursToggle")).IsChecked = false;
        ((CheckBox)window.FindName("SyncViews")).IsChecked = false;
        ((CheckBox)window.FindName("DetailsToggle")).IsChecked = true;
        ((Expander)window.FindName("EvidencePanel")).IsExpanded = true;
        foreach (string name in new[] { "StandardView", "CurrentView" })
        {
            var view = (ImageViewport)window.FindName(name);
            view.SetRoi(new Rect(0, .1, 1, .3));
            view.Select(1);
            view.FocusMark(1);
        }

        Call(window, "ClearStandard_Click", window, new RoutedEventArgs());
        var reset = (DetectionSettings)Field(window, "settings")!;
        Check(JsonSerializer.Serialize(reset) == JsonSerializer.Serialize(new DetectionSettings()), "全部参数恢复默认，全图 ROI 和色差/质量参数不残留");
        foreach (string name in new[] {
"standard",
"standardAnalysis",
"current",
"inspection",
"standardFile",
"lastAttempt",
"timingImage"
})
        {
            Check(Field(window, name) == null, "清空会话状态：" + name);
        }
        Check((string)Field(window, "measurementTiming")! == "" && !(bool)Field(window, "standardDirty")!, "清除旧耗时与未保存标记");
        Check(banner.Visibility == Visibility.Collapsed && banner.Text == "", "清除旧错误提示");
        Check(((DataGrid)window.FindName("ResultsGrid")).Items.Count == 0, "结果表格为空");
        Check(((TextBlock)window.FindName("Verdict")).Text == "待检测", "恢复待检测状态");
        Check(
            ((CheckBox)window.FindName("MaskToggle")).IsChecked == false && ((CheckBox)window.FindName("ContoursToggle")).IsChecked == true,
            "恢复默认叠加方式");
        Check(
            ((CheckBox)window.FindName("SyncViews")).IsChecked == true && ((CheckBox)window.FindName("DetailsToggle")).IsChecked == false,
            "恢复同步与明细选项");
        Check(!((Expander)window.FindName("EvidencePanel")).IsExpanded, "收起旧证据");
        foreach (string name in new[] { "StandardView", "CurrentView" })
        {
            var view = (ImageViewport)window.FindName(name);
            Check(Field(view, "analysis") == null && Field(view, "bitmap") == null, "清空图像与位图：" + name);
            Check(
                Field(view, "drawnRoi") == null && (int)Field(view, "selectedId")! == -1 && view.ZoomFactor == 1,
                "清空视图 ROI/选择/缩放：" + name);
        }

        var dialog = new SettingsWindow(reset, null, null);
        Check(Field(dialog, "previewSource") == null, "重新进入设置没有上次的预览来源");
        Check(
            Field((ImageViewport)dialog.FindName("BeforeView"), "bitmap") == null && Field((ImageViewport)dialog.FindName("AfterView"), "bitmap") == null,
            "ROI 页前后预览均为空");
        Check(
            File.ReadAllText(savedPath) == savedJson && StandardProfile.Load(savedPath).Settings.RoiHeight == .3,
            "磁盘 JSON 原样保留并可重新读取");
        Program.Render((FrameworkElement)window.Content, 1400, 920, Path.Combine(output, "clear-standard-preview.png"));

        // 没有标准但已调整参数时仍要复位；处理中则不能从其他入口清空后台读取的状态。
        reset.RoiHeight = .02;
        SetField(window, "busy", true);
        Call(window, "ClearStandard_Click", window, new RoutedEventArgs());
        Check(ReferenceEquals(reset, Field(window, "settings")), "忙碌期间拒绝清除");
        SetField(window, "busy", false);
        Call(window, "ClearStandard_Click", window, new RoutedEventArgs());
        Check(((DetectionSettings)Field(window, "settings")!).RoiHeight == 1, "无标准时也可以清除参数");

        await (Task)Call(window, "BuildStandardAsync", new[] { lower }, false)!;
        var rebuilt = (StandardProfile)Field(window, "standard")!;
        Check(rebuilt.Marks.Count == 5 && rebuilt.SourcePath == lower, "清除后下方新图直接建标成功，不需先加载待测图");
        Check(File.ReadAllText(savedPath) == savedJson, "重新建标也不覆盖旧 JSON");
        Console.WriteLine("PASS: clear resets ROI/settings/images/evidence, preserves saved JSON, guards busy state and rebuilds outside old ROI.");
    }
}
