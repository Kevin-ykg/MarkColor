using System.IO;

namespace MarkColorDemo;

/// <summary>逐张分析、几何配准、质量检查后才汇总。每次拍摄等权，几何坐标固定在第一张图。</summary>
public static class StandardBuilder
{
    public sealed record Result(StandardProfile Profile, Analysis Reference);

    public sealed record Prepared(Result Standard, DetectionSettings Settings);

    /// <summary>
    /// 失败只修改独立草稿；回调在调用者上下文执行，可直接展示 ROI 对话框。
    /// 用户取消返回 null，原标准、参数、待测结果均不受影响；重试保留全部采样文件。
    /// </summary>
    public static async Task<Prepared?> BuildWithRecoveryAsync(
        IReadOnlyList<string> paths,
        DetectionSettings settings,
        Func<StandardBuildException, DetectionSettings, Task<DetectionSettings?>> recover,
        Action<string>? progress = null)
    {
        var draft = settings.Copy();
        while (true)
        {
            try
            {
                var result = await Task.Run(() => Build(paths, draft, progress));
                return new(result, draft);
            }
            catch (StandardBuildException problem)
            {
                var adjusted = await recover(problem, draft.Copy());
                if (adjusted == null)
                {
                    return null;
                }

                adjusted.Validate();
                draft = adjusted.Copy();
            }
        }
    }

    public static Result Build(
        IReadOnlyList<string> paths,
        DetectionSettings settings,
        Action<string>? progress = null,
        CancellationToken cancellation = default)
    {
        if (paths.Count < 1 || paths.Count > 100)
        {
            throw new InvalidOperationException("建标需要 1–100 张图像；多图建标建议选同一合格膜片的多次独立拍摄。");
        }

        if (paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Count)
        {
            throw new InvalidOperationException("采样文件重复，请选择不同拍摄文件。");
        }

        StandardProfile? profile = null;
        Analysis? reference = null;
        // 每个数组是一张拍摄；数组内按首张标准点顺序排列，后续才能逐列统计。
        var aligned = new List<Mark[]>();
        for (int index = 0; index < paths.Count; ++index)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = paths[index];
            progress?.Invoke($"建标采样 {index + 1}/{paths.Count}：{Path.GetFileName(path)}");
            string hash = StandardProfile.Hash(path);
            Analysis image;
            try
            {
                image = Native.Analyze(path, settings, preparePreview: index == 0);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("ROI") || ex.Message.Contains("256 candidates"))
            {
                throw new StandardBuildException(StandardBuildProblem.Extraction, path, null, ex.Message, "请重新框选目标膜片，保留全部圆点与边缘余量，并检查原图像素单位的半径范围。");
            }

            if (image.Marks.Count > 16)
            {
                throw new StandardBuildException(
                    StandardBuildProblem.TooManyMarks,
                    path,
                    image,
                    $"检测到 {image.Marks.Count} 个候选，单膜片建标最多支持 16 点。",
                    "请在左侧框选目标膜片的全部圆点，排除其他膜片、二维码及背景干扰，再预览并继续建标。");
            }

            if (image.Marks.Count == 0)
            {
                throw new StandardBuildException(StandardBuildProblem.NoMarks, path, image, "未检测到完整圆点。", "请检查 ROI 是否覆盖全部圆点、圆点是否被裁切，以及最小/最大半径。也可能是对比度不足、失焦或曝光异常。");
            }

            if (StandardProfile.Hash(path) != hash)
            {
                throw new IOException($"采样期间文件发生变化：{path}");
            }

            if (profile == null)
            {
                profile = StandardProfile.From(image, settings, hash);
                profile.SourceSha256 = hash;
                reference = image;
                profile.Samples.Clear();
            }

            var (comparisons, alignment) = Native.Compare(profile, image);
            if (alignment.Ambiguous != 0 || alignment.Extra != 0 || comparisons.Any(c => c.Status != 0 || c.ObservedIndex < 0))
            {
                throw new StandardBuildException(
                    StandardBuildProblem.Correspondence,
                    path,
                    image,
                    "圆点数量或对应关系不可靠，本次建标未应用。",
                    "请核对所有采样是否为同一膜片、ROI 是否覆盖各次拍摄的全部圆点，以及是否存在漏点、多余点或超出配准范围的位移。");
            }

            var values = new Mark[profile.Marks.Count];
            for (int i = 0; i < values.Length; ++i)
            {
                int observed = comparisons[i].ObservedIndex;
                var quality = settings.Quality.Assess(image, observed);
                if (!quality.Pass)
                {
                    throw new StandardBuildException(
                        StandardBuildProblem.MeasurementQuality,
                        path,
                        image,
                        $"#{profile.Marks[i].Id} 无法可靠测量：{quality.Reason}",
                        "若误检了背景，请重新框选 ROI；若确为目标圆点，请检查曝光、反光、对焦和有效采样。采集质量问题应重新拍摄，不应靠放宽色差阈值建标。");
                }

                values[i] = image.Marks[observed];
            }

            aligned.Add(values);
            profile.Samples.Add(new()
            {
                Path = Path.GetFullPath(path),
                Sha256 = hash,
                Values = values.Select(LabValue.From).ToList(),
                Alignment = alignment
            });
        }

        // 拍摄等权平均而非按取色像素数加权，避免大圆点/大视野拍摄改变标准贡献。
        for (int i = 0; i < profile!.Marks.Count; ++i)
        {
            var target = profile.Marks[i];
            var mean = target.Value;
            mean.L = aligned.Average(a => a[i].L);
            mean.A = aligned.Average(a => a[i].A);
            mean.B = aligned.Average(a => a[i].B);
            mean.Red = aligned.Average(a => a[i].Red);
            mean.Green = aligned.Average(a => a[i].Green);
            mean.Blue = aligned.Average(a => a[i].Blue);
            target.Value = mean;
            target.MeasuredLab = LabValue.From(mean);
            target.CaptureCount = aligned.Count;
            target.MaximumSampleDeltaE = aligned.Max(a => Native.DeltaE00(LabValue.From(a[i]), LabValue.From(mean), settings.ColorDifference));
            target.Repeatability = new(
                SampleDeviation(aligned, i, mark => mark.L, mean.L),
                SampleDeviation(aligned, i, mark => mark.A, mean.A),
                SampleDeviation(aligned, i, mark => mark.B, mean.B));
        }

        profile.Name = Path.GetFileNameWithoutExtension(paths[0]) + (paths.Count > 1 ? $" · {paths.Count}次采样均值" : "");
        profile.Validate();
        return new(profile, reference!);
    }

    // 跨拍摄重复性使用样本标准差 n−1；它不同于单张图内像素的 StdL/StdA/StdB。
    // markIndex 是已按标准重新排列后的下标，不能直接用每张图的检测下标。
    private static double SampleDeviation(
        IReadOnlyList<Mark[]> aligned, int markIndex, Func<Mark, double> channel, double average)
    {
        if (aligned.Count < 2)
        {
            return 0;
        }

        return Math.Sqrt(aligned.Sum(sample => Math.Pow(channel(sample[markIndex]) - average, 2)) / (aligned.Count - 1));
    }
}
