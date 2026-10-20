// 多图均值、质量资格和批量失败/取消的集成回归；合成质量证据仅验证业务规则。
using MarkColorDemo;
using System.IO;
using System.Text.Json;

/// <summary>多图建标和测量资格回归；既校验已配准均值，也验证采集异常不得归类为颜色 NG。</summary>
internal static class UpgradeTests
{
    private static void Check(bool value, string message)
    {
        if (!value)
        {
            throw new Exception("Upgrade: " + message);
        }
    }

    // 测试同步记录回调，避免断言执行时 UI SynchronizationContext 尚未派发进度事件。
    private sealed class DirectProgress(Action<BatchDetector.Progress> action) : IProgress<BatchDetector.Progress>
    {
        public void Report(BatchDetector.Progress value) => action(value);
    }

    public static void Run(string root, string output, DetectionSettings settings)
    {
        string upper = Path.Combine(root, "优良品", "2-上.bmp"), lower = Path.Combine(root, "优良品", "2-下.bmp");
        var built = StandardBuilder.Build([upper, lower], settings);
        var profile = built.Profile;
        Check(profile.Samples.Count == 2 && profile.Marks.Count == 5, "multi-sample source count / point count");
        for (int i = 0; i < 5; ++i)
        {
            var a = profile.Samples[0].Values[i];
            var b = profile.Samples[1].Values[i];
            var m = profile.Marks[i];
            Check(
                Math.Abs(m.L - (a.L + b.L) / 2) < 1e-10 && Math.Abs(m.A - (a.A + b.A) / 2) < 1e-10 && Math.Abs(m.B - (a.B + b.B) / 2) < 1e-10,
                "equal weighting of aligned Lab");
            // 两样本标准差有独立闭式结果 |a-b|/√2，用它核对统计口径而非复写实现。
            Check(Math.Abs(m.Repeatability.L - Math.Abs(a.L - b.L) / Math.Sqrt(2)) < 1e-10, "between-capture sample standard deviation");
            double maximumDelta = Math.Max(Native.mc_delta_e00(a.L, a.A, a.B, m.L, m.A, m.B), Native.mc_delta_e00(b.L, b.A, b.B, m.L, m.A, m.B));
            Check(Math.Abs(m.MaximumSampleDeltaE - maximumDelta) < 1e-10, "capture spread is reported without relaxing the threshold");
        }

        bool rejected = false;
        try
        {
            StandardBuilder.Build([upper, upper], settings);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Check(rejected, "duplicate samples rejected");
        var impossible = settings.Copy();
        impossible.Quality.MinSamplePixels = 10000000;
        rejected = false;
        try
        {
            StandardBuilder.Build([upper, lower], impossible);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Check(rejected, "ineligible sample cannot contaminate standard");
        var raw = profile.Marks[0].MeasuredLab!;
        profile.Marks[0].L += .5;
        profile.Changes.Add(new(DateTime.Now, "verification adjustment", [raw], [LabValue.From(profile.Marks[0].Value)]));
        string path = Path.Combine(output, "multi-sample.mark-standard.json");
        profile.Save(path);
        var loaded = StandardProfile.Load(path);
        Check(
            loaded.Marks[0].MeasuredLab == raw && loaded.Marks[0].L == profile.Marks[0].L && loaded.Samples.Count == 2 && loaded.Changes.Count == 1,
            "raw mean / manual target / samples / audit round trip");
        profile = StandardProfile.From(built.Reference, settings);
        var current = Native.Analyze(upper, settings);
        Check(current.Preview == null, "numeric-only pipeline avoids bitmap");
        var crowded = new Analysis
        {
            Width = current.Width,
            Height = current.Height,
            Marks = Enumerable.Repeat(current.Marks[0], 20).ToList()
        };
        var crowdedResult = Inspection.Run(profile, crowded);
        Check(
            crowdedResult.Decision.Verdict == DetectionVerdict.AlignmentUncertain && crowdedResult.AlignmentDescription.Contains("ROI") && crowdedResult.Rows.All(r => r.Status == "匹配不确定"),
            "crowded field is retained for ROI adjustment, never truncated or classified as color NG");
        var checks = new List<object>();
        void QualityCase(string name, Func<MarkQuality, MarkQuality> modify, Action<Analysis>? other = null)
        {
            var copy = new Analysis
            {
                Path = upper,
                Width = current.Width,
                Height = current.Height,
                Marks = [.. current.Marks],
                Contours = current.Contours,
                Qualities = [.. current.Qualities]
            };
            copy.Qualities[0] = modify(copy.Qualities[0]);
            other?.Invoke(copy);
            var result = Inspection.Run(profile, copy);
            Check(
                result.HasReliableAlignment && result.Decision.Verdict == DetectionVerdict.Unmeasurable && !result.Pass,
                name + " classified separately from color NG");
            string csv = Path.Combine(output, "quality-" + name + ".csv"), html = Path.Combine(output, "quality-" + name + ".html");
            using (var report = new ReportExport(csv))
            {
                report.Write(profile, built.Reference, copy, result);
            }

            using (var report = new ReportExport(html))
            {
                report.Write(profile, built.Reference, copy, result);
            }

            Check(File.ReadAllText(csv).Contains("无法可靠测量") && File.ReadAllText(html).Contains("无法可靠测量"), name + " report parity");
            checks.Add(new
            {
                Name = name,
                Status = result.Decision.Label,
                result.Decision.Reason
            });
        }

        QualityCase("overexposure", q =>
        {
            q.OverexposedFraction = .6;
            return q;
        });
        QualityCase("underexposure", q =>
        {
            q.UnderexposedFraction = .8;
            return q;
        }, a =>
        {
            var m = a.Marks[0];
            m.BackgroundL = 3;
            a.Marks[0] = m;
        });
        QualityCase("glare", q =>
        {
            q.GlareFraction = .2;
            return q;
        });
        QualityCase("blur", q =>
        {
            q.EdgeWidth = current.Marks[0].Radius * .6;
            return q;
        });
        QualityCase("confidence", q =>
        {
            q.ContourConfidence = .01;
            return q;
        });
        QualityCase("not-refined", q =>
        {
            q.Refined = 0;
            return q;
        });
        QualityCase("invalid-evidence", q =>
        {
            q.ContourConfidence = double.NaN;
            return q;
        });
        QualityCase("samples", q =>
        {
            q.ValidSampleFraction = .2;
            return q;
        });
        // 只变颜色且质量合格时仍然是颜色 NG，不能被质量状态掩盖。
        var color = new Analysis
        {
            Width = current.Width,
            Height = current.Height,
            Marks = [.. current.Marks],
            Qualities = [.. current.Qualities]
        };
        var changed = color.Marks[0];
        changed.L += 20;
        color.Marks[0] = changed;
        Check(Inspection.Run(profile, color).Decision.Verdict == DetectionVerdict.ColorNg, "valid color anomaly remains NG");
        var strict = profile.Copy();
        strict.Settings.MaxTranslation = 0;
        var pending = Inspection.Run(strict, Native.Analyze(lower, settings));
        string pendingCsv = Path.Combine(output, "quality-pending.csv");
        using (var report = new ReportExport(pendingCsv))
        {
            report.Write(strict, null, current, pending);
        }

        Check(
            pending.Decision.Verdict == DetectionVerdict.AlignmentUncertain && File.ReadAllText(pendingCsv).Contains("待确认"),
            "uncertain CSV matches UI and HTML");
        int completed = 0;
        BatchDetector.RunAsync([upper, lower], settings, profile, built.Reference, Path.Combine(output, "pipeline.csv"), new DirectProgress(p =>
        {
            Check(p.Item.Result!.Image.Preview == null, "CSV batch never generates preview");
            completed++;
        }), default).GetAwaiter().GetResult();
        Check(completed == 2, "bounded batch pipeline completes");
        int illustrated = 0;
        string batchHtml = Path.Combine(output, "pipeline.html");
        BatchDetector.RunAsync([upper, lower], settings, profile, built.Reference, batchHtml, new DirectProgress(p =>
        {
            Check(p.Item.Result!.Image.Preview?.IsFrozen == true, "HTML worker receives a frozen preview");
            illustrated++;
        }), default).GetAwaiter().GetResult();
        Check(
            illustrated == 2 && File.ReadAllText(batchHtml).Contains("data:image/jpeg;base64,") && File.ReadAllText(batchHtml).EndsWith("</html>"),
            "report worker writes actual annotated previews and a complete footer");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        BatchDetector.RunAsync(
            [upper, lower],
            settings,
            profile,
            built.Reference,
            Path.Combine(output, "pipeline-cancelled.html"),
            new DirectProgress(_ => throw new Exception("Unexpected cancelled item")),
            cancellation.Token).GetAwaiter().GetResult();
        Check(
            File.ReadAllText(Path.Combine(output, "pipeline-cancelled.html")).Contains("已处理 0/2"),
            "cancelled batch is complete with explicit count");
        string blocker = Path.Combine(output, "report-blocker.tmp");
        File.WriteAllText(blocker, "fixture");
        rejected = false;
        try
        {
            BatchDetector.RunAsync([upper, lower, upper], settings, profile, built.Reference, Path.Combine(blocker, "out.csv"), new DirectProgress(_ =>
            {
            }), default).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        catch (IOException)
        {
            rejected = true;
        }

        Check(rejected, "output failure propagates and cancels producer without deadlock");
        File.WriteAllText(Path.Combine(output, "upgrade-validation.json"), JsonSerializer.Serialize(new
        {
            Samples = built.Profile.Samples,
            Checks = checks,
            BatchCompleted = completed
        }, StandardProfile.JsonOptions));
        Console.WriteLine("PASS: multi-sample aligned mean/std, raw target audit, quality decisions, HTML/CSV parity, preview-free batch and cancellation.");
    }
}
