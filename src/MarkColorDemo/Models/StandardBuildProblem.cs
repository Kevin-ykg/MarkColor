namespace MarkColorDemo;

/// <summary>建标失败的业务原因。保留本次样图，使恢复流程不依赖主窗口曾加载过待测图。</summary>
public enum StandardBuildProblem
{
    NoMarks,
    TooManyMarks,
    Correspondence,
    MeasurementQuality,
    Extraction
}
