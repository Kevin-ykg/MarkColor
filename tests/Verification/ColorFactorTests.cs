// 同时检查 Native 色差因子、JSON 往返和设置应用；因子变化应复用图像测量而重新比较。
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using MarkColorDemo;

/// <summary>参数因子的数值、持久化与界面提交回归；调权重不得改变配准或独立通道限值。</summary>
internal static class ColorFactorTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void Require(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException("Color factors: " + message);
        }
    }

    public static void Run(string output, Analysis source)
    {
        var settings = new DetectionSettings();
        var profile = StandardProfile.From(source, settings);
        var observed = new Analysis
        {
            Path = source.Path,
            Width = source.Width,
            Height = source.Height,
            Marks = source.Marks.ToList(),
            Qualities = source.Qualities.ToList(),
            Contours = source.Contours
        };
        // 合成纯明度变化，隔离 kL 对判定的影响；此图不作为现场测量样本。
        var shifted = observed.Marks[0];
        shifted.L += 5;
        observed.Marks[0] = shifted;
        var initial = Inspection.Run(profile, observed);
        double originalDifference = initial.Comparisons[0].DeltaE00;
        profile.Marks[0].DeltaEThreshold = originalDifference * .75;
        Require(!Inspection.Run(profile, observed).Rows[0].Pass, "original lightness difference exceeds threshold");
        profile.Settings.ColorDifference.KL = 2;
        var weighted = Inspection.Run(profile, observed);
        Require(
            weighted.Rows[0].Pass && Math.Abs(weighted.Comparisons[0].DeltaE00 - originalDifference / 2) < 1e-12,
            "weighted value drives actual verdict");
        Require(weighted.Alignment.Equals(initial.Alignment), "factors must not change geometric alignment");
        Require(
            weighted.Comparisons.Zip(initial.Comparisons).All(pair => pair.First.ObservedIndex == pair.Second.ObservedIndex && pair.First.DeltaL == pair.Second.DeltaL && pair.First.DeltaE76 == pair.Second.DeltaE76),
            "raw Lab differences and correspondence unchanged");
        profile.Marks[0].MaxDeltaL = 1;
        Require(!Inspection.Run(profile, observed).Rows[0].Pass, "independent DeltaL limit still applies");
        profile.Marks[0].MaxDeltaL = 0;

        var copied = profile.Settings.Copy();
        copied.ColorDifference.KL = 3;
        Require(
            profile.Settings.ColorDifference.KL == 2 && copied.SameExtraction(profile.Settings),
            "draft isolation and no extraction invalidation");
        foreach (double invalid in new[] { 0, -.5, .09, 10.1, double.NaN, double.PositiveInfinity })
        {
            copied.ColorDifference.KL = invalid;
            bool rejected = false;
            try
            {
                copied.Validate();
            }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "invalid factor rejected");
        }

        profile.Settings.ColorDifference.KC = 1.3;
        profile.Settings.ColorDifference.KH = .8;
        string path = Path.Combine(output, "weighted-standard.json");
        profile.Save(path);
        var restored = StandardProfile.Load(path);
        Require(
            restored.SchemaVersion == 3 && restored.Settings.ColorDifference.KL == 2 && restored.Settings.ColorDifference.KC == 1.3 && restored.Settings.ColorDifference.KH == .8,
            "all factors persisted with schema 3");
        var legacy = JsonNode.Parse(JsonSerializer.Serialize(profile, StandardProfile.JsonOptions))!;
        legacy["SchemaVersion"] = 2;
        legacy["Settings"]!.AsObject().Remove("ColorDifference");
        string legacyPath = Path.Combine(output, "legacy-no-factors.json");
        File.WriteAllText(legacyPath, legacy.ToJsonString());
        var migrated = StandardProfile.Load(legacyPath);
        Require(
            migrated.SchemaVersion == 3 && migrated.Settings.ColorDifference.ToNative().Equals(new ColorDifferenceSettings().ToNative()),
            "old standards default to 1/1/1");

        profile.RefreshSampleColorDifferences();
        var result = Inspection.Run(profile, observed);
        string csv = Path.Combine(output, "weighted-result.csv");
        using (var writer = ReportWriter.Open(csv))
        {
            ReportWriter.Write(writer, profile, observed, result);
            ReportWriter.Write(writer, null, source, null);
            ReportWriter.Error(writer, "synthetic", "error test");
        }
        using (var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(csv))
        {
            parser.SetDelimiters(",");
            parser.HasFieldsEnclosedInQuotes = true;
            string[] header = parser.ReadFields()!;
            Require(header.Length == 40 && header.TakeLast(3).SequenceEqual(new[] { "kL", "kC", "kH" }), "CSV factor header");
            int row = 0;
            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields()!;
                Require(fields.Length == header.Length, "CSV row/header column parity");
                if (row++ == 0)
                {
                    Require(fields.TakeLast(3).SequenceEqual(new[] { "2", "1.3", "0.8" }), "CSV actual factors");
                }
            }
        }
        string html = Path.Combine(output, "weighted-result.html");
        using (var writer = new VisualReportWriter(html))
        {
            writer.Write(profile, source, observed, result);
        }
        string htmlText = File.ReadAllText(html);
        Require(htmlText.Contains("kL=2") && htmlText.Contains("kC=1.3") && htmlText.Contains("kH=0.8"), "HTML actual factors");
        Console.WriteLine("PASS: factor verdict, geometric isolation, DeltaL limit, validation, draft copy, schema migration and HTML/CSV factors.");
    }

    public static async Task VerifyUiAsync(MainWindow window, string output)
    {
        var grid = (DataGrid)window.FindName("ResultsGrid");
        Require(grid.Columns.Count(column => column.Visibility == Visibility.Visible) == 7, "compact result columns");
        var lastVisibleRow = grid.ItemContainerGenerator.ContainerFromIndex(4) as DataGridRow;
        Require(
            lastVisibleRow != null && lastVisibleRow.TranslatePoint(new Point(0, lastVisibleRow.ActualHeight), grid).Y <= grid.ActualHeight,
            "five-mark results fully visible without vertical scrolling");
        var details = (CheckBox)window.FindName("DetailsToggle");
        details.IsChecked = true;
        Require(grid.Columns.All(column => column.Visibility == Visibility.Visible), "details toggle exposes all columns");
        Program.Render((FrameworkElement)window.Content, 1400, 920, Path.Combine(output, "expanded-details-preview.png"));
        details.IsChecked = false;
        var button = (Button)window.FindName("LoadImageButton");
        Require(button.FontSize >= 16 && button.MinHeight >= 44, "primary actions are readable");
        Require(
            ((Button)window.FindName("BuildStandardMenuButton")).ContextMenu.Items.OfType<MenuItem>().Count() == 3,
            "all build-standard actions retained");
        Require(
            ((Button)window.FindName("StandardFileMenuButton")).ContextMenu.Items.OfType<MenuItem>().Count() == 3,
            "all standard file actions retained");

        var profileField = typeof(MainWindow).GetField("standard", PrivateInstance)!;
        var imageField = typeof(MainWindow).GetField("current", PrivateInstance)!;
        var profile = (StandardProfile)profileField.GetValue(window)!;
        var before = imageField.GetValue(window);
        var dialog = new SettingsWindow(profile.Settings, profile);
        var inputs = (Dictionary<string, TextBox>)typeof(SettingsWindow).GetField("inputs", PrivateInstance)!.GetValue(dialog)!;
        inputs["ColorDifference.KL"].Text = "2";
        inputs["ColorDifference.KC"].Text = "1.3";
        inputs["ColorDifference.KH"].Text = "0.8";
        var readDraft = typeof(SettingsWindow).GetMethod("ReadDraft", PrivateInstance)!;
        var draft = (DetectionSettings)readDraft.Invoke(dialog, null)!;
        Require(profile.Settings.ColorDifference.KL == 1, "unapplied dialog does not modify standard");
        ((TabItem)dialog.FindName("ColorDifferenceTab")).IsSelected = true;
        Program.Render((FrameworkElement)dialog.Content, 1100, 735, Path.Combine(output, "color-factor-settings-preview.png"));
        inputs["ColorDifference.KL"].Text = "0";
        bool rejected = false;
        try
        {
            readDraft.Invoke(dialog, null);
        }
        catch (TargetInvocationException) { rejected = true; }
        Require(rejected, "UI rejects zero factor");
        var apply = typeof(MainWindow).GetMethod("ApplySettingsAsync", PrivateInstance)!;
        await (Task)apply.Invoke(window, new object[] { draft, profile.Marks, "测试自定义色差参数" })!;
        var updated = (StandardProfile)profileField.GetValue(window)!;
        Require(ReferenceEquals(before, imageField.GetValue(window)), "factor-only apply reuses image analysis");
        Require(
            updated.Settings.ColorDifference.KL == 2 && updated.Revision == profile.Revision + 1,
            "actual apply workflow commits factor and revision");
        Require(((TextBlock)window.FindName("ColorParameterSummary")).Text.Contains("kC=1.3"), "UI shows active factors");
        Require(updated.Changes.Last().Reason.Contains("kL=2"), "audit records factor change");
        Program.Render((FrameworkElement)window.Content, 1400, 920, Path.Combine(output, "weighted-ui-preview.png"));
        // 恢复默认因子，后续既有图库/UI 回归继续验证默认行为。
        await (Task)apply.Invoke(window, new object[] { profile.Settings, profile.Marks, "恢复默认参数测试" })!;
    }
}
