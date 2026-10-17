namespace MarkColorDemo;

/// <summary>一次拍摄的溯源记录；Values 按标准点顺序排列，Alignment 将首张几何映射到本次拍摄。</summary>
public sealed class StandardSample
{
    public string Path { get; set; } = "";

    public string Sha256 { get; set; } = "";

    public List<LabValue> Values { get; set; } = [];

    public Alignment Alignment
    {
        get; set;
    }
}
