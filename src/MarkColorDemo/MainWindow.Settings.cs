using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace MarkColorDemo;

// 设置草稿的应用与重建；成功后才统一提交主窗口状态。
public partial class MainWindow
{
    // 提取参数变化才重新读图；仅改阈值/目标 Lab 时复用测量快照，保留人工标准微调。
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(settings, standard, current ?? standardAnalysis)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ApplySettingsAsync(dialog.Settings, dialog.Marks, dialog.ChangeReason);
    }

    /// <summary>提交已验证的设置草稿；先准备全部结果，成功后再一次性替换当前状态。</summary>
    private async Task ApplySettingsAsync(DetectionSettings draft, List<StandardMark> marks, string changeReason)
    {
        await Execute(async () =>
        {
            draft.Validate();
            // 是否需要重提取由模型定义；新增提取参数必须同时加入 SameExtraction，避免错误复用旧 Lab。
            bool extractionChanged = !settings.SameExtraction(draft);
            var newSettings = draft.Copy();
            if (!settings.ColorDifference.ToNative().Equals(newSettings.ColorDifference.ToNative()))
            {
                changeReason += $"；色差参数：{settings.ColorDifference.Description} → {newSettings.ColorDifference.Description}";
            }
            StandardProfile? updatedStandard = null;
            Analysis? referenceAnalysis = standardAnalysis;
            Analysis? observedAnalysis = current;
            if (standard != null)
            {
                updatedStandard = standard.Copy();
                updatedStandard.Settings = newSettings.Copy();
                updatedStandard.Marks = marks.Select(mark => mark.Copy()).ToList();
                if (extractionChanged)
                {
                    var sources = standard.Samples.Count > 0 ? standard.Samples : [new StandardSample
                    {
                        Path = standard.SourcePath,
                        Sha256 = standard.SourceSha256
                    }];
                    foreach (var source in sources)
                    {
                        if (!File.Exists(source.Path) || StandardProfile.Hash(source.Path) != source.Sha256)
                        {
                            throw new InvalidOperationException($"采样图像缺失或内容改变：{source.Path}。请恢复原图或重新建标。");
                        }
                    }

                    var rebuilt = await Task.Run(() => StandardBuilder.Build(sources.Select(x => x.Path).ToArray(), newSettings));
                    referenceAnalysis = rebuilt.Reference;
                    updatedStandard = rebuilt.Profile;
                    updatedStandard.Id = standard.Id;
                    updatedStandard.Name = standard.Name;
                    updatedStandard.CreatedAt = standard.CreatedAt;
                    updatedStandard.Changes = standard.Changes.ToList();
                    if (updatedStandard.Marks.Count != standard.Marks.Count)
                    {
                        throw new InvalidOperationException("新参数改变了标准圆点数。为避免标签错位，本次修改未应用；请先用新参数加载图像，再重新设置标准。");
                    }

                    for (int i = 0; i < updatedStandard.Marks.Count; i++)
                    {
                        var previousMark = marks[i];
                        var rebuiltMark = updatedStandard.Marks[i];
                        if (double.Hypot(previousMark.Value.X - rebuiltMark.Value.X, previousMark.Value.Y - rebuiltMark.Value.Y) > previousMark.Value.Radius * .5)
                        {
                            throw new InvalidOperationException("新参数改变了标准圆点位置，无法安全保留标签。请重新设置标准。");
                        }

                        // 新图重新测量后仅迁移“人工偏移”，避免用旧测量值覆盖新提取结果。
                        var originalMean = previousMark.MeasuredLab ?? LabValue.From(previousMark.Value);
                        rebuiltMark.L += previousMark.L - originalMean.L;
                        rebuiltMark.A += previousMark.A - originalMean.A;
                        rebuiltMark.B += previousMark.B - originalMean.B;
                        rebuiltMark.Id = previousMark.Id;
                        rebuiltMark.Name = previousMark.Name;
                        rebuiltMark.InkChannel = previousMark.InkChannel;
                        rebuiltMark.DeltaEThreshold = previousMark.DeltaEThreshold;
                        rebuiltMark.MaxDeltaL = previousMark.MaxDeltaL;
                        rebuiltMark.MaxDeltaA = previousMark.MaxDeltaA;
                        rebuiltMark.MaxDeltaB = previousMark.MaxDeltaB;
                    }
                }
            }

            if (extractionChanged && current != null)
            {
                observedAnalysis = await Task.Run(() => DetectionPipeline.Load(current.Path, newSettings));
            }

            // 仅调整色差因子时复用测量快照，同时刷新多图标准的采样偏差证据。
            updatedStandard?.RefreshSampleColorDifferences();
            var result = updatedStandard != null && observedAnalysis != null ? await Task.Run(() => Inspection.Run(updatedStandard, observedAnalysis)) : null;
            if (updatedStandard != null && standard != null)
            {
                updatedStandard.Revision = standard.Revision + 1;
                updatedStandard.Changes.Add(new(
                    DateTime.Now,
                    changeReason,
                    standard.Marks.Select(m => LabValue.From(m.Value)).ToList(),
                    updatedStandard.Marks.Select(m => LabValue.From(m.Value)).ToList()));
                updatedStandard.Validate();
                standardDirty = true;
            }

            // 事务式提交点：上面的重建/配准/校验任何一步失败，原状态都保持有效。
            settings = newSettings;
            standard = updatedStandard;
            standardAnalysis = referenceAnalysis;
            current = observedAnalysis;
            inspection = result;
            Refresh();
            StatusLine.Text = extractionChanged ? "已使用新参数重新提取标准和待测图；请保存更新后的标准。" : "已复用 Lab 重新比较；色差参数与标准微调已应用，请保存标准。";
        });
    }
}
