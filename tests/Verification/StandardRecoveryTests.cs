using MarkColorDemo;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>直接从建标进入恢复，不依赖“加载待测图像”；失败/取消不得修改调用方参数。</summary>
internal static class StandardRecoveryTests
{
    private static void Check(bool pass, string message)
    {
        if (!pass)
        {
            throw new Exception("Standard recovery: " + message);
        }
    }

    public static void Run(string root, string output)
    {
        string path = Path.Combine(root, "优良品", "2-上.bmp");
        var emptyRoi = new DetectionSettings
        {
            RoiHeight = .02
        };
        int attempts = 0;
        var recovered = StandardBuilder.BuildWithRecoveryAsync([path], emptyRoi, (problem, draft) =>
        {
            Check(
                problem.Problem == StandardBuildProblem.NoMarks && problem.Image?.Marks.Count == 0,
                "empty ROI carries the selected standard image");
            Check(
                problem.Image!.Preview?.IsFrozen == true && problem.ImagePath == path,
                "ROI source is available without a current image");
            attempts++;
            draft.RoiHeight = 1;
            return Task.FromResult<DetectionSettings?>(draft);
        }).GetAwaiter().GetResult();
        Check(
            attempts == 1 && recovered?.Standard.Profile.Marks.Count == 5 && recovered.Settings.RoiHeight == 1,
            "adjust ROI and retry same standard source");
        Check(emptyRoi.RoiHeight == .02, "draft adjustments never mutate original settings");
        var cancelled = StandardBuilder.BuildWithRecoveryAsync([path], emptyRoi, (_, draft) =>
        {
            draft.RoiHeight = .5;
            return Task.FromResult<DetectionSettings?>(null);
        }).GetAwaiter().GetResult();
        Check(cancelled == null && emptyRoi.RoiHeight == .02, "cancellation is not a partially applied standard");
        var tiny = new DetectionSettings
        {
            RoiWidth = .001,
            RoiHeight = .001
        };
        StandardBuilder.BuildWithRecoveryAsync([path], tiny, (problem, _) =>
        {
            Check(
                problem.Problem == StandardBuildProblem.Extraction && problem.Image == null && problem.ImagePath == path,
                "native ROI-too-small failure retains source for a raw-image preview");
            return Task.FromResult<DetectionSettings?>(null);
        }).GetAwaiter().GetResult();
        var quality = new DetectionSettings();
        quality.Quality.MinSamplePixels = 10000000;
        StandardBuilder.BuildWithRecoveryAsync([path], quality, (problem, _) =>
        {
            Check(
                problem.Problem == StandardBuildProblem.MeasurementQuality && problem.Image?.Marks.Count == 5 && problem.Message.Contains("有效采样"),
                "quality failure gives acquisition guidance, not color NG");
            return Task.FromResult<DetectionSettings?>(null);
        }).GetAwaiter().GetResult();
        // 多点画面必须能先进入 ROI，再用同一文件完成建标；显式构造非对称目标，避免配准歧义。
        string crowded = Path.Combine(output, "recovery-crowded.bmp");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(180, 180, 180)), null, new Rect(0, 0, 1200, 600));
            for (int i = 0; i < 20; ++i)
            {
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(150, 40, 80)), null, new Point(60 + i % 10 * 110, 180 + i / 10 * 220), 35, 35);
            }
        }

        var pixels = new RenderTargetBitmap(1200, 600, 96, 96, PixelFormats.Pbgra32);
        pixels.Render(visual);
        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(pixels));
        using (var stream = File.Create(crowded))
        {
            encoder.Save(stream);
        }

        attempts = 0;
        var single = StandardBuilder.BuildWithRecoveryAsync([crowded], new DetectionSettings(), (problem, draft) =>
        {
            Check(
                problem.Problem == StandardBuildProblem.TooManyMarks && problem.Image?.Marks.Count == 20,
                "crowded field keeps all candidates for ROI review");
            attempts++;
            draft.RoiWidth = .1;
            draft.RoiHeight = .5;
            return Task.FromResult<DetectionSettings?>(draft);
        }).GetAwaiter().GetResult();
        Check(
            attempts == 1 && single?.Standard.Profile.Marks.Count == 1,
            "crowded single-image standard recovers after ROI selection");
        Console.WriteLine("PASS: standard recovery, zero marks, >16 candidates, tiny ROI, quality, retry and cancel isolation.");
    }

    public static async Task RenderAsync(string output, Analysis source)
    {
        var draft = new DetectionSettings
        {
            RoiHeight = .02
        };
        var failed = Native.Analyze(source.Path, draft, preparePreview: true);
        var dialog = new SettingsWindow(draft, null, failed);
        dialog.ConfigureForStandardRecovery("未检测到完整圆点。请重新框选目标膜片的全部圆点，并检查半径、曝光及对焦。");
        Check(((TabControl)dialog.FindName("SettingsTabs")).SelectedIndex == 3, "recovery opens directly on ROI tab");
        Check(
            ((Button)dialog.FindName("ApplySettingsButton")).Content.ToString()!.Contains("继续建标"),
            "recovery action names the next step");
        ((ImageViewport)dialog.FindName("BeforeView")).SetRoi(new Rect(0, .1, 1, .3));
        await dialog.PreviewDraftAsync();
        Check(
            ((TextBlock)dialog.FindName("PreviewInfo")).Text.Contains("预览 5 点"),
            "recovery ROI preview works without a current test image");
        Program.Render((FrameworkElement)dialog.Content, 1100, 735, Path.Combine(output, "standard-recovery-preview.png"));
    }
}
