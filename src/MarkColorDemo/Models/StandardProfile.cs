using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MarkColorDemo;

/// <summary>可持久化的检测标准：来源追溯、逐点目标值、标签和参数。不会保存预览位图。</summary>
public sealed class StandardProfile
{
    public int SchemaVersion { get; set; } = 3;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public int Revision { get; set; } = 1;

    public List<StandardSample> Samples { get; set; } = [];

    public List<StandardChange> Changes { get; set; } = [];

    // JSON 深复制包含嵌套参数、点、采样和审计列表，用于编辑事务的独立草稿。
    public StandardProfile Copy() => JsonSerializer.Deserialize<StandardProfile>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;

    public string Name { get; set; } = "标准 OK";

    public string SourcePath { get; set; } = "";

    public string SourceSha256 { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public int Width
    {
        get; set;
    }

    public int Height
    {
        get; set;
    }

    public DetectionSettings Settings { get; set; } = new();

    public List<StandardMark> Marks { get; set; } = [];

    public static StandardProfile From(Analysis analysis, DetectionSettings settings) => From(analysis, settings, null);

    // 建标流水线已在分析前后核对文件哈希，无须第三次读取整幅 BMP。
    internal static StandardProfile From(Analysis analysis, DetectionSettings settings, string? sourceHash)
    {
        if (analysis.Marks.Count > 16)
        {
            throw new InvalidOperationException($"检测到 {analysis.Marks.Count} 个候选点。当前标准最多支持 16 点，请先在设置中框选目标膜片 ROI，再建立标准。");
        }

        if (analysis.Marks.Count == 0)
        {
            throw new InvalidOperationException("未检测到圆点，不能设为标准。请调整参数或 ROI。");
        }

        var profile = new StandardProfile
        {
            Name = Path.GetFileNameWithoutExtension(analysis.Path),
            SourcePath = Path.GetFullPath(analysis.Path),
            SourceSha256 = sourceHash ?? Hash(analysis.Path),
            Width = analysis.Width,
            Height = analysis.Height,
            Settings = settings.Copy()
        };
        for (int i = 0; i < analysis.Marks.Count; i++)
        {
            var mark = analysis.Marks[i];
            var (name, ink) = Guess(mark);
            profile.Marks.Add(new StandardMark
            {
                Id = i + 1,
                Name = name,
                InkChannel = ink,
                Value = mark,
                MeasuredLab = LabValue.From(mark),
                DeltaEThreshold = settings.DefaultDeltaE
            });
        }

        profile.Samples.Add(new()
        {
            Path = profile.SourcePath,
            Sha256 = profile.SourceSha256,
            Values = profile.Marks.Select(mark => LabValue.From(mark.Value)).ToList()
        });
        return profile;
    }

    // 根据颜色给出墨路建议，不能据此证明 CMYK 配方；用户仍需核对打印版式。
    private static (string, string) Guess(Mark mark)
    {
        if (mark.L < 45 && Math.Sqrt(mark.A * mark.A + mark.B * mark.B) < 25)
        {
            return ("黑色", "K通道");
        }

        double max = Math.Max(mark.Red, Math.Max(mark.Green, mark.Blue)), min = Math.Min(mark.Red, Math.Min(mark.Green, mark.Blue)), delta = max - min;
        if (delta < 20)
        {
            return ("浅色 / 中性", "待确认");
        }

        double hue;
        if (max == mark.Red)
        {
            hue = (mark.Green - mark.Blue) / delta;
        }
        else if (max == mark.Green)
        {
            hue = 2 + (mark.Blue - mark.Red) / delta;
        }
        else
        {
            hue = 4 + (mark.Red - mark.Green) / delta;
        }
        hue = (hue * 60 + 360) % 360;
        if (hue < 25 || hue > 338)
        {
            return ("红色", "混合色");
        }

        if (hue < 75)
        {
            return ("黄色", "Y通道");
        }

        if (hue < 165)
        {
            return ("绿色", "混合色");
        }

        if (hue < 205)
        {
            return ("青色", "C通道");
        }

        if (hue < 275)
        {
            return ("蓝色", "混合色");
        }

        return ("洋红", "M通道");
    }

    /// <summary>内容哈希只用于验证标准原图是否改变，不用于跨加载缓存检测结果。</summary>
    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>文件、设置对话框和比较入口共用验证；拒绝 NaN、重复编号和不合理单位。</summary>
    public void Validate()
    {
        if (SchemaVersion < 1 || SchemaVersion > 3 || Settings == null || Marks == null || Marks.Count < 1 || Marks.Count > 16 || Width < 1 || Height < 1)
        {
            throw new InvalidDataException("标准文件版本、尺寸或圆点数无效（最多 16 点）。");
        }

        Settings.Validate();
        if (Samples == null || Samples.Count > 100 || Changes == null || Changes.Count > 1000)
        {
            throw new InvalidDataException("采样/修改记录无效。");
        }

        foreach (var sample in Samples)
        {
            if (sample.Values == null || sample.Values.Count != Marks.Count || sample.Values.Any(value => !double.IsFinite(value.L) || !double.IsFinite(value.A) || !double.IsFinite(value.B)))
            {
                throw new InvalidDataException("采样数值无效。");
            }
        }

        if (Marks.Select(x => x.Id).Distinct().Count() != Marks.Count)
        {
            throw new InvalidDataException("圆点编号重复。");
        }

        foreach (var mark in Marks)
        {
            if (mark.Id < 1 || string.IsNullOrWhiteSpace(mark.Name) || mark.Name.Length > 80 || mark.InkChannel == null || mark.InkChannel.Length > 80)
            {
                throw new InvalidDataException("圆点名称或墨路标签无效。");
            }

            var value = mark.Value;
            foreach (var field in typeof(Mark).GetFields().Where(x => x.FieldType == typeof(double)))
            {
                if (!double.IsFinite((double)field.GetValue(value)!))
                {
                    throw new InvalidDataException("标准圆点含非有限数值。");
                }
            }

            if (value.X < 0 || value.Y < 0 || value.X >= Width || value.Y >= Height || value.Radius < 3 || value.L < 0 || value.L > 100 || Math.Abs(value.A) > 160 || Math.Abs(value.B) > 160 || value.SampleCount < 16)
            {
                throw new InvalidDataException("标准圆点坐标、Lab 或样本数量无效。");
            }

            foreach (double x in new[]
            {
                mark.DeltaEThreshold,
                mark.MaxDeltaL,
                mark.MaxDeltaA,
                mark.MaxDeltaB
            })
            {
                if (!double.IsFinite(x) || x < 0 || x > 200)
                {
                    throw new InvalidDataException("逐点阈值应在 0–200 范围内。");
                }
            }

            if (mark.DeltaEThreshold <= 0)
            {
                throw new InvalidDataException("ΔE00 阈值必须大于零。");
            }
        }
    }

    // 先写临时文件再替换，避免写入中断时破坏已有人工标准。
    public void Save(string path)
    {
        Validate();
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var snapshot = Copy();
        // 旧程序必须拒绝新版标准，防止忽略自定义 k 参数后误用 1/1/1。
        snapshot.SchemaVersion = 3;
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!string.IsNullOrEmpty(snapshot.SourcePath))
        {
            snapshot.SourcePath = Path.GetRelativePath(folder, snapshot.SourcePath);
        }

        foreach (var sample in snapshot.Samples)
        {
            sample.Path = Path.GetRelativePath(folder, sample.Path);
        }

        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    // 相对图片路径以 JSON 所在文件夹解析；历史版本迁移只补字段，不重新测色。
    public static StandardProfile Load(string path)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("标准 JSON 超过 4 MB。");
        }

        var profile = JsonSerializer.Deserialize<StandardProfile>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("标准文件为空。");
        profile.Validate();
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!string.IsNullOrEmpty(profile.SourcePath))
        {
            profile.SourcePath = Path.GetFullPath(profile.SourcePath, folder);
        }

        foreach (var sample in profile.Samples)
        {
            sample.Path = Path.GetFullPath(sample.Path, folder);
        }

        profile.Validate();
        if (profile.SchemaVersion == 1)
        {
            profile.SchemaVersion = 2;
            foreach (var mark in profile.Marks)
            {
                mark.MeasuredLab ??= LabValue.From(mark.Value);
            }

            profile.Changes.Add(new(DateTime.Now, "从旧版标准迁移；历史原始 Lab 不可恢复，以迁移时目标值记录。", [], []));
        }

        profile.SchemaVersion = 3; // v1/v2 未含色差因子时，由属性初始值补齐 1/1/1。
        return profile;
    }

    /// <summary>只重新计算跨拍摄的色差统计；无需读取原图或重算均值/几何。</summary>
    public void RefreshSampleColorDifferences()
    {
        for (int index = 0; index < Marks.Count; index++)
        {
            var target = Marks[index];
            var rawMean = target.MeasuredLab ?? LabValue.From(target.Value);
            var samples = Samples.Where(sample => sample.Values.Count > index).Select(sample => sample.Values[index]);
            target.MaximumSampleDeltaE = samples.Select(value => Native.DeltaE00(value, rawMean, Settings.ColorDifference)).DefaultIfEmpty(0).Max();
        }
    }
}
