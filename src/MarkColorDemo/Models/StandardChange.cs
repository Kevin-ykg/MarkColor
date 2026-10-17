namespace MarkColorDemo;

/// <summary>人工调整审计；Before/After 按标准点顺序保存，Reason 同时记录参数变更。</summary>
public sealed record StandardChange(DateTime At, string Reason, List<LabValue> Before, List<LabValue> After);
