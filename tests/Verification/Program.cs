using MarkColorDemo;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

/// <summary>集成验证入口（STA）：图库点数、上下位移、标准持久化、报告与真实 WPF 布局；任一断言失败返回非零。</summary>
internal static class Program
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    // WPF 控件及位图验证要求 STA；普通控制台入口的 MTA 会产生无关失败。
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            int cameraMode = Array.IndexOf(args, "--camera-scale");
            if (cameraMode >= 0)
            {
                return CameraScaleTests.Run(args[cameraMode + 1], args[cameraMode + 2]);
            }

            string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetParent(MainWindow.DemoRoot)!.FullName;
            if (args.Contains("--benchmark"))
            {
                return PerformanceBenchmarks.Run(root, args.Last());
            }

            if (args.Contains("--pair-report"))
            {
                var pairSettings = new DetectionSettings();
                var reference = Native.Analyze(Path.Combine(root, "优良品", "2-上.bmp"), pairSettings);
                var observed = Native.Analyze(Path.Combine(root, "优良品", "2-下.bmp"), pairSettings);
                foreach (var a in new[]
                {
                    reference,
                    observed
                })
                {
                    Console.WriteLine($"{Path.GetFileName(a.Path)} {a.Width}x{a.Height}: " + string.Join("; ", a.Marks.Select((m, i) => $"#{i + 1} ({m.X:F1},{m.Y:F1}) r={m.Radius:F1}")));
                }

                foreach (double limit in new[]
                {
                    .3,
                    1.0
                })
                {
                    pairSettings.MaxTranslation = limit;
                    var result = Inspection.Run(StandardProfile.From(reference, pairSettings), observed);
                    var a = result.Alignment;
                    Console.WriteLine($"limit={limit}: matched={a.Matched}, extra={a.Extra}, ambiguous={a.Ambiguous}, tx={a.TranslateX:F2}, ty={a.TranslateY:F2}, angle={a.AngleDegrees:F3}, scale={a.Scale:F5}, rms={a.Rms:F3}");
                    foreach (var row in result.Rows)
                    {
                        Console.WriteLine($"#{row.Id}: observed={row.ObservedIndex}, {row.Status}, dE={row.DeltaE}");
                    }
                }

                return 0;
            }

            string report = Path.Combine(MainWindow.DemoRoot, "verification");
            Directory.CreateDirectory(report);
            var settings = new DetectionSettings();
            settings.Validate();
            var files = Directory.EnumerateDirectories(root).SelectMany(d => Directory.EnumerateFiles(d, "*.bmp")).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            var analyses = new Dictionary<string, Analysis>();
            var timings = new List<double>();
            var counts = new List<object>();
            foreach (string path in files)
            {
                var timer = Stopwatch.StartNew();
                var a = Native.Analyze(path, settings);
                timer.Stop();
                timings.Add(timer.Elapsed.TotalMilliseconds);
                analyses[path] = a;
                int expected = Path.GetFileName(path).Contains('左') || Path.GetFileName(path).Contains('右') ? 1 : 5;
                counts.Add(new
                {
                    File = Path.GetRelativePath(root, path),
                    Detected = a.Marks.Count,
                    Expected = expected,
                    Milliseconds = timer.Elapsed.TotalMilliseconds
                });
                Check(a.Marks.Count == expected, $"圆点数不符：{path}: {a.Marks.Count}/{expected}");
                foreach (var m in a.Marks)
                {
                    Check(m.L >= 0 && m.L <= 100 && Math.Abs(m.A) < 128 && Math.Abs(m.B) < 128 && m.SampleCount > 100, "Lab单位/样本量异常");
                }
            }

            Check(files.Length > 0, "图库为空。");
            var baselinePath = Path.Combine(root, "优良品", "2-上.bmp");
            var p = StandardProfile.From(analyses[baselinePath], settings);
            Check(p.Marks.Count == 5, "五色标准");
            Check(Inspection.Run(p, analyses[baselinePath]).Pass, "同图必须通过");
            var lower = analyses[Path.Combine(root, "优良品", "2-下.bmp")];
            var upperLower = Inspection.Run(p, lower);
            Check(
                upperLower.HasReliableAlignment && upperLower.Alignment.Matched == 5 && upperLower.Alignment.Extra == 0,
                "上下实拍必须唯一匹配 5/5");
            Check(upperLower.Comparisons.Select(c => c.ObservedIndex).SequenceEqual(Enumerable.Range(0, 5)), "上下实拍对应关系必须保持墨路编号");
            Check(upperLower.Alignment.TranslateY > 1000 && upperLower.Alignment.Rms < 5, "上下实拍位移及配准残差");
            Check(!upperLower.Pass && upperLower.Rows.Count(r => r.Status == "色差 NG") == 4, "配准成功不能放宽颜色阈值或掩盖色差");
            Check(
                Enumerable.Range(0, 5).All(i => !upperLower.IsConfirmedMissing(i) && upperLower.ObservedLabel(i).StartsWith($"#{i + 1} ")),
                "匹配后叠加标签必须使用标准编号，不能画缺失");
            var reverse = Inspection.Run(StandardProfile.From(lower, settings), analyses[baselinePath]);
            Check(reverse.HasReliableAlignment && reverse.Alignment.Matched == 5 && reverse.Alignment.TranslateY < -1000, "下到上反向实拍配准");
            var strictSettings = settings.Copy();
            strictSettings.MaxTranslation = .3;
            var strict = Inspection.Run(StandardProfile.From(analyses[baselinePath], strictSettings), lower);
            Check(!strict.HasReliableAlignment && strict.Alignment.Matched == 0 && !strict.Pass, "显式严格平移上限仍需生效");
            Check(
                Enumerable.Range(0, 5).All(i => !strict.IsConfirmedMissing(i) && strict.ObservedLabel(i).StartsWith("候选 P")),
                "不确定时只能显示候选，不可标成标准点或缺失");
            Check(strict.Rows.All(r => r.Status == "匹配不确定") && strict.Summary(5).Contains("配准不确定"), "表格与摘要一致说明匹配不确定");
            ReportTests.Run(report, p, analyses[baselinePath], lower, upperLower, strict);
            UpgradeTests.Run(root, report, settings);
            ColorFactorTests.Run(report, analyses[baselinePath]);
            StandardRecoveryTests.Run(root, report);
            File.WriteAllText(Path.Combine(report, "vertical-shift.json"), JsonSerializer.Serialize(new
            {
                Reference = Path.GetRelativePath(root, baselinePath),
                Observed = Path.GetRelativePath(root, lower.Path),
                Before = new
                {
                    MaxTranslation = .3,
                    strict.Alignment
                },
                After = new
                {
                    settings.MaxTranslation,
                    upperLower.Alignment
                },
                Rows = upperLower.Rows.Select(r => new { r.Id, r.ObservedIndex, r.Status, r.DeltaE }),
                Reverse = reverse.Alignment
            }, StandardProfile.JsonOptions));
            p.Marks[0].Name = "洋红-手动";
            p.Marks[0].InkChannel = "M";
            p.Marks[0].L += .2;
            p.Marks[0].DeltaEThreshold = 2.5;
            p.Marks[0].MaxDeltaL = 1.2;
            string config = Path.Combine(report, "roundtrip.mark-standard.json");
            p.Save(config);
            var loaded = StandardProfile.Load(config);
            Check(
                loaded.Marks[0].L == p.Marks[0].L && loaded.Marks[0].MaxDeltaL == 1.2 && loaded.Marks[0].Name == "洋红-手动",
                "配置保存/恢复必须保留微调");
            p = StandardProfile.From(analyses[baselinePath], settings);
            foreach (var pair in analyses.Where(x => Path.GetFileName(Path.GetDirectoryName(x.Key)) == "优良品"))
            {
                var same = Inspection.Run(StandardProfile.From(pair.Value, settings), pair.Value);
                Check(same.Pass && same.Comparisons.All(c => c.DeltaE00 < 1e-9), "所有标准自比对");
            }

            var magenta = Inspection.Run(p, analyses[Path.Combine(root, "M通道偏小二档", "2-上.bmp")]);
            Check(!magenta.Pass && magenta.Rows[0].Status == "色差 NG" && magenta.Comparisons[0].DeltaE00 > 20, "近白色洋红仍应匹配并检出颜色异常");
            var absent = new Analysis
            {
                Path = "synthetic-blank",
                Width = p.Width,
                Height = p.Height
            };
            Check(!Inspection.Run(p, absent).Pass, "空图不得通过");
            var extra = new Analysis
            {
                Width = p.Width,
                Height = p.Height,
                Marks = [.. analyses[baselinePath].Marks]
            };
            var extraMark = extra.Marks[0];
            extraMark.X = 500;
            extraMark.Y = 1600;
            extra.Marks.Add(extraMark);
            var extraResult = Inspection.Run(p, extra);
            Check(!extraResult.Pass && extraResult.ObservedLabel(5) == "多余 P6", "多余圆点不得通过，也不可冒用标准编号");
            var missing = new Analysis
            {
                Width = lower.Width,
                Height = lower.Height,
                Marks = [.. lower.Marks]
            };
            missing.Marks.RemoveAt(2);
            var missingResult = Inspection.Run(p, missing);
            Check(
                missingResult.HasReliableAlignment && missingResult.Alignment.Matched == 4 && missingResult.IsConfirmedMissing(2) && !missingResult.Pass,
                "上下偏移后真实缺点仍应明确缺失");
            var reordered = new Analysis
            {
                Width = lower.Width,
                Height = lower.Height,
                Marks = lower.Marks.AsEnumerable().Reverse().ToList()
            };
            Check(
                Inspection.Run(p, reordered).Comparisons.Select(c => c.ObservedIndex).SequenceEqual(Enumerable.Range(0, 5).Reverse()),
                "不可用列表序号代替几何对应");
            var strictPath = Path.Combine(report, "strict-translation.mark-standard.json");
            StandardProfile.From(analyses[baselinePath], strictSettings).Save(strictPath);
            Check(StandardProfile.Load(strictPath).Settings.MaxTranslation == .3, "读取旧配置应保留用户显式设定的平移上限");
            var badProfile = StandardProfile.Load(config);
            badProfile.Marks[0].DeltaEThreshold = -1;
            bool rejected = false;
            try
            {
                badProfile.Validate();
            }
            catch
            {
                rejected = true;
            }

            Check(rejected, "拒绝无效配置");
            var rows = new List<object>();
            using (var writer = ReportWriter.Open(Path.Combine(report, "gallery-comparisons.csv")))
            {
                foreach (var pair in analyses)
                {
                    string name = Path.GetFileName(pair.Key).Replace("上-.", "上.");
                    if (name.StartsWith('1'))
                    {
                        name = "2" + name[1..];
                    }

                    string refPath = Path.Combine(root, "优良品", name);
                    if (!analyses.TryGetValue(refPath, out var baseline))
                    {
                        ReportWriter.Error(writer, pair.Key, "没有同名优良品参考");
                        continue;
                    }

                    var standard = StandardProfile.From(baseline, settings);
                    var result = Inspection.Run(standard, pair.Value);
                    Check(result.Alignment.Matched == standard.Marks.Count && result.Alignment.Ambiguous == 0, $"图库匹配失败：{pair.Key}");
                    ReportWriter.Write(writer, standard, pair.Value, result);
                    rows.Add(new
                    {
                        Folder = Path.GetFileName(Path.GetDirectoryName(pair.Key)),
                        File = Path.GetFileName(pair.Key),
                        Result = result.Decision.ReportLabel,
                        Matched = result.Alignment.Matched,
                        MaxDeltaE00 = result.Comparisons.Max(c => c.DeltaE00)
                    });
                }
            }

            File.WriteAllText(Path.Combine(report, "gallery-detection.json"), JsonSerializer.Serialize(counts, StandardProfile.JsonOptions));
            File.WriteAllText(Path.Combine(report, "gallery-summary.json"), JsonSerializer.Serialize(rows, StandardProfile.JsonOptions));
            Directory.CreateDirectory(Path.Combine(report, "generated-standards"));
            foreach (string name in new[]
            {
                "2-上.bmp",
                "2左-上.bmp",
                "3-上.bmp"
            })
            {
                StandardProfile.From(analyses[Path.Combine(root, "优良品", name)], settings).Save(Path.Combine(report, "generated-standards", Path.GetFileNameWithoutExtension(name) + ".mark-standard.json"));
            }

            // In-process WPF render exercises bindings/layout without operating the user's desktop.
            var app = new App();
            app.InitializeComponent();
            var window = new MainWindow();
            Exception? uiError = null;
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    ViewportInteractionTests.Run(analyses[baselinePath]);
                    await window.LoadPairAsync(baselinePath, lower.Path);
                    Render((FrameworkElement)window.Content, 1400, 920, Path.Combine(report, "vertical-shift-preview.png"));
                    await ColorFactorTests.VerifyUiAsync(window, report);
                    Check(((System.Windows.Controls.TextBlock)window.FindName("Summary")).Text.Contains("缺失 0"), "真实窗口摘要必须无假缺失");
                    var uncertainView = new ImageViewport();
                    uncertainView.Set(lower, null, strict, settings.InnerRatio);
                    Render(uncertainView, 1000, 680, Path.Combine(report, "uncertain-alignment-preview.png"));
                    await window.LoadPairAsync(baselinePath, Path.Combine(root, "M通道偏小二档", "2-上.bmp"));
                    Render((FrameworkElement)window.Content, 1400, 920, Path.Combine(report, "demo-preview.png"));
                    var grid = (System.Windows.Controls.DataGrid)window.FindName("ResultsGrid");
                    Console.WriteLine("UI columns: " + string.Join(", ", grid.Columns.Select(c => $"{c.Header}:{c.Width.Value}/{c.ActualWidth}")));
                    var dialog = new SettingsWindow(settings, p);
                    Render((FrameworkElement)dialog.Content, 1100, 735, Path.Combine(report, "settings-preview.png"));
                    Render((FrameworkElement)window.Content, 1100, 700, Path.Combine(report, "demo-minimum-preview.png"));
                    var buildMulti = typeof(MainWindow).GetMethod("BuildStandardAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    await (Task)buildMulti.Invoke(window, new object[] { new[] { baselinePath, lower.Path }, false })!;
                    Check(((System.Windows.Controls.TextBlock)window.FindName("StandardCaption")).Text.Contains("2 次采样"), "多图建标真实窗口显示采样数");
                    grid.SelectedIndex = 0;
                    ((System.Windows.Controls.CheckBox)window.FindName("MaskToggle")).IsChecked = true;
                    Render((FrameworkElement)window.Content, 1400, 920, Path.Combine(report, "selection-preview.png"));
                    var previewDialog = new SettingsWindow(settings, p, analyses[baselinePath]);
                    await StandardRecoveryTests.RenderAsync(report, analyses[baselinePath]);
                    var beforeView = (ImageViewport)previewDialog.FindName("BeforeView");
                    beforeView.SetRoi(new Rect(0, .1, 1, .3));
                    await previewDialog.PreviewDraftAsync();
                    Check(((System.Windows.Controls.TextBlock)previewDialog.FindName("PreviewInfo")).Text.Contains("预览 5 点"), "ROI 草稿参数预览真实图像");
                    var tabs = (System.Windows.Controls.TabControl)previewDialog.FindName("SettingsTabs");
                    tabs.SelectedIndex = 3;
                    Render((FrameworkElement)previewDialog.Content, 1100, 735, Path.Combine(report, "roi-preview.png"));
                    tabs.SelectedIndex = 2;
                    Render((FrameworkElement)previewDialog.Content, 1100, 735, Path.Combine(report, "quality-settings-preview.png"));
                    await ClearStandardTests.RunAsync(root, report);
                }
                catch (Exception ex)
                {
                    uiError = ex;
                }
                finally
                {
                    frame.Continue = false;
                }
            });
            Dispatcher.PushFrame(frame);
            if (uiError != null)
            {
                throw uiError;
            }

            string summary = $"PASS: {files.Length} images; {analyses.Values.Sum(x => x.Marks.Count)} marks; all expected counts and correspondences; all 18 self-comparisons; real upper/lower matching in both directions; strict-limit ambiguous overlays; large-shift missing/reordered marks; unchanged color thresholds; near-white missing-ink anomaly; blank/extra failures; template roundtrip; WPF rendering.\nNative analyze median={timings.OrderBy(x => x).ElementAt(timings.Count / 2):F1} ms, max={timings.Max():F1} ms (includes first-call initialization).\n";
#if DEBUG
            const string summaryFile = "verification-debug.txt";
#else
            const string summaryFile = "verification.txt";
#endif
            File.WriteAllText(Path.Combine(report, summaryFile), summary, new UTF8Encoding(false));
            Console.WriteLine(summary);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    internal static void Render(FrameworkElement element, int width, int height, string path)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() =>
        {
        }, DispatcherPriority.ApplicationIdle);
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
