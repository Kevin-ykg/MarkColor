namespace MarkColorDemo;

/// <summary>浮点 CIELAB 三元值；既用于目标均值，也用于按通道记录重复性（由属性语义区分）。</summary>
public sealed record LabValue(double L, double A, double B)
{
    public static LabValue From(Mark mark) => new(mark.L, mark.A, mark.B);

    public override string ToString() => $"{L:F2} / {A:F2} / {B:F2}";
}
