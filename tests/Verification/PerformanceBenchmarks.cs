using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MarkColorDemo;

/// <summary>固定实拍对、预热后重复测量；将算法和显示更新分开，避免只测 DLL 就宣称整界面提速。</summary>
internal static class PerformanceBenchmarks
{
    public static int Run(string root, string label)
    {
        const int iterations = 20;
        var settings = new DetectionSettings();
        string referencePath = Path.Combine(root, "优良品", "2-上.bmp");
        string observedPath = Path.Combine(root, "优良品", "2-下.bmp");
        var reference = Native.Analyze(referencePath, settings);
        var observed = Native.Analyze(observedPath, settings);
        var profile = StandardProfile.From(reference, settings);
        var result = Inspection.Run(profile, observed);
        var app = new App();
        app.InitializeComponent();
        var referenceView = new ImageViewport();
        var currentView = new ImageViewport();
        var times = new Dictionary<string, List<double>>
        {
            ["NativeAnalyze"] = [],
            ["CompareAndRows"] = [],
            ["RefreshExistingViews"] = [],
            ["LoadPairAndRender"] = [],
            ["Decode"] = [],
            ["Segmentation"] = [],
            ["Measurement"] = []
        };
        void Measure(string stage, Action action, bool record)
        {
            var watch = Stopwatch.StartNew();
            action();
            if (record)
            {
                times[stage].Add(watch.Elapsed.TotalMilliseconds);
            }
        }

        // 初次初始化和 JIT 不混入稳定态统计；输入为 2448×2048 五色实拍图。
        for (int iteration = -2; iteration < iterations; ++iteration)
        {
            bool record = iteration >= 0;
            Measure("NativeAnalyze", () => observed = Native.Analyze(observedPath, settings), record);
            if (record)
            {
                times["Decode"].Add(observed.Timings.DecodeMs);
                times["Segmentation"].Add(observed.Timings.SegmentationMs);
                times["Measurement"].Add(observed.Timings.MeasurementMs);
            }

            Measure("CompareAndRows", () => result = Inspection.Run(profile, observed), record);
            Measure("RefreshExistingViews", () =>
            {
                referenceView.Set(reference, profile, null, settings.InnerRatio);
                currentView.Set(reference, profile, null, settings.InnerRatio);
                Render(referenceView, 650, 430);
                Render(currentView, 650, 430);
            }, record);
        }

        var window = new MainWindow();
        Exception? failure = null;
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                for (int iteration = -2; iteration < iterations; ++iteration)
                {
                    var watch = Stopwatch.StartNew();
                    await window.LoadPairAsync(referencePath, observedPath);
                    Render((FrameworkElement)window.Content, 1400, 920);
                    if (iteration >= 0)
                    {
                        times["LoadPairAndRender"].Add(watch.Elapsed.TotalMilliseconds);
                    }
                }
            }
            catch (Exception error)
            {
                failure = error;
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

#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var summary = times.ToDictionary(pair => pair.Key, pair =>
        {
            var sorted = pair.Value.Order().ToArray();
            return new
            {
                MedianMs = sorted[sorted.Length / 2],
                P95Ms = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1]
            };
        });
        string output = Path.Combine(MainWindow.DemoRoot, "verification", $"performance-{label}-{configuration}.json");
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Configuration = configuration,
            Iterations = iterations,
            WarmupIterations = 2,
            Image = "优良品/2-下.bmp",
            Width = observed.Width,
            Height = observed.Height,
            Stages = summary
        }, StandardProfile.JsonOptions));
        Console.WriteLine(File.ReadAllText(output));
        return 0;
    }

    private static void Render(FrameworkElement view, int width, int height)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(view);
    }
}
