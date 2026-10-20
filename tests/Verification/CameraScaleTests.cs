// 专用实拍数据集回归：按既定版式检查点数及质量，不应把这些期望推广为任意用户图库的真值。
using MarkColorDemo;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

/// <summary>新旧相机实拍回归；预期点数和 ROI 是人工验收样本约定，不是生产检测参数推断。</summary>
internal static class CameraScaleTests
{
    // ROI 是这两组拍摄的人工验收标注，不是生产算法中的固定位置。
    // BMP 周围另有多张膜片，限定中央目标；JPEG 使用整图验收。
    public static int Run(string newGallery, string oldGallery)
    {
        string output = Path.Combine(MainWindow.DemoRoot, "verification", "camera-scale");
        Directory.CreateDirectory(output);
        var records = new List<object>();
        var failures = new List<string>();
        int newCount = 0, oldCount = 0;
        foreach (var (root, isNew) in new[]
        {
            (newGallery, true),
            (oldGallery, false)
        })
        {
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(p => new[] { ".bmp", ".jpg", ".png" }.Contains(Path.GetExtension(p).ToLowerInvariant())).OrderBy(p => p).ToArray();
            if (files.Length == 0)
            {
                throw new Exception("Empty camera regression gallery: " + root);
            }

            foreach (string path in files)
            {
                var settings = new DetectionSettings();
                bool centralFilm = isNew && Path.GetExtension(path).Equals(".bmp", StringComparison.OrdinalIgnoreCase);
                if (centralFilm)
                {
                    settings.RoiX = 1600.0 / 5472;
                    settings.RoiY = 600.0 / 3648;
                    settings.RoiWidth = 2400.0 / 5472;
                    settings.RoiHeight = 2200.0 / 3648;
                }

                int expected = isNew ? 7 : Path.GetFileName(path).Contains('左') || Path.GetFileName(path).Contains('右') ? 1 : 5;
                try
                {
                    var timer = Stopwatch.StartNew();
                    var image = Native.Analyze(path, settings);
                    int unavailable = Enumerable.Range(0, image.Marks.Count).Count(i => !settings.Quality.Assess(image, i).Pass);
                    if (image.Marks.Count != expected)
                    {
                        failures.Add($"{path}: {image.Marks.Count}/{expected}");
                    }

                    if (isNew && image.Marks.Any(m => m.Radius < 42 || m.Radius > 58))
                    {
                        failures.Add($"{path}: unexpected radius / background candidate");
                    }

                    records.Add(new
                    {
                        File = path,
                        Expected = expected,
                        Detected = image.Marks.Count,
                        Settings = settings,
                        Milliseconds = timer.Elapsed.TotalMilliseconds,
                        Unmeasurable = unavailable,
                        Marks = image.Marks.Select(m => new { m.X, m.Y, m.Radius, m.SampleCount }),
                        Error = (string?)null
                    });
                    if (isNew)
                    {
                        newCount++;
                    }
                    else
                    {
                        oldCount++;
                    }

                    Console.WriteLine($"{(isNew ? "NEW" : "OLD")} {Path.GetFileName(path)}: {image.Marks.Count}/{expected}, quality unavailable {unavailable}");
                }
                catch (Exception ex)
                {
                    failures.Add(path + ": " + ex.Message);
                    records.Add(new
                    {
                        File = path,
                        Expected = expected,
                        Error = ex.Message
                    });
                }
            }
        }

        File.WriteAllText(Path.Combine(output, "camera-regression.json"), JsonSerializer.Serialize(new
        {
            NewImages = newCount,
            OldImages = oldCount,
            Failures = failures,
            Records = records
        }, StandardProfile.JsonOptions));
        if (failures.Count > 0)
        {
            throw new Exception(string.Join("\n", failures));
        }

        RenderCameraWindow(newGallery, output);
        Console.WriteLine($"PASS: camera regression, new {newCount}, old {oldCount}; all independently expected mark counts and new-camera radii.");
        return 0;
    }

    private static void RenderCameraWindow(string gallery, string output)
    {
        var app = new App();
        app.InitializeComponent();
        var window = new MainWindow();
        var frame = new DispatcherFrame();
        Exception? failure = null;
        window.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                string path = Directory.EnumerateFiles(gallery, "*.jpg").OrderBy(p => p).First();
                await window.LoadPairAsync(path, path);
                if (!((TextBlock)window.FindName("Summary")).Text.Contains("匹配 7/7"))
                {
                    throw new Exception("Camera WPF view must display all seven matched marks.");
                }

                Program.Render((FrameworkElement)window.Content, 1400, 920, Path.Combine(output, "camera-ui.png"));
                var image = Native.Analyze(path, new DetectionSettings(), preparePreview: true);
                var settings = new SettingsWindow(new DetectionSettings(), null, image);
                Program.Render((FrameworkElement)settings.Content, 1100, 735, Path.Combine(output, "camera-settings.png"));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                frame.Continue = false;
            }
        });
        Dispatcher.PushFrame(frame);
        if (failure != null)
        {
            throw failure;
        }
    }
}
