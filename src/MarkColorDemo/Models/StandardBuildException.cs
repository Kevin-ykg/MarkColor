using System.IO;

namespace MarkColorDemo;

/// <summary>携带失败样图和原因，供界面重新框选 ROI 后重试；不提交部分标准。</summary>
public sealed class StandardBuildException(
    StandardBuildProblem problem,
    string path,
    Analysis? image,
    string reason,
    string guidance) : InvalidOperationException($"{Path.GetFileName(path)}：{reason}\n{guidance}")
{
    public StandardBuildProblem Problem { get; } = problem;

    public string ImagePath { get; } = path;

    public Analysis? Image { get; } = image;
}
