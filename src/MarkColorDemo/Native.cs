using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace MarkColorDemo;

// ABI v3: 与 mark_color.h 的字段顺序、类型和 Pack=8 严格对应。
// 为便于核对互操作布局，此文件集中保留边界结构体和 P/Invoke 声明。
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NativeSettings
{
    public double MinRadius, MaxRadius; // 原图 px，ROI 改变不改变单位。
    public double MinCircularity, MinFill; // 面积/周长形成的无量纲几何指标。
    public double SaturationMin, DarkMax, BrightContrast; // 8 位通道阈值 / 局部灰度亮差。
    public double InnerRatio; // 取色内圈半径 / 检出半径。
    public double RoiX, RoiY, RoiWidth, RoiHeight; // 相对整图宽高的 0..1 坐标。
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct Mark
{
    public double X, Y, Radius; // 原图圆心与外接圆半径，px。
    public double Area, Circularity; // 轮廓面积 px²；圆度 4πA/P²。
    public double L, A, B; // 内圈截尾均值，CIELAB：L*=0..100，a*/b* 带符号。
    public double StdL, StdA, StdB; // 全部内圈像素的标准差，不是跨拍摄重复性。
    public double Blue, Green, Red; // 内圈 BGR8 均值，仅用于色块显示/粗命名。
    public double BackgroundL; // 外围基材环带的 L*，辅助检查欠曝。
    public int SampleCount, ContourCount; // 有效取色像素数 / 轮廓顶点数。
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NativePoint
{
    public double X, Y;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NativeMatchSettings
{
    public double MaxTranslation; // 平移长度 / 图像对角线。
    public double MaxRotation; // 最大绝对旋转角，度。
    public double MaxScaleChange; // 相对 1 的最大比例偏差。
    public double PositionTolerance; // 位置容差 / 标准圆点半径。
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NativeColorDifferenceSettings
{
    public double KL, KC, KH; // 无量纲参数因子，按此顺序与 McColorDifferenceSettings 对齐。
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct Comparison
{
    public int ObservedIndex; // 待测数组下标；-1 表示没有分配，不能当成标准 Id。
    public int Status; // Native 状态码：0 匹配、1 缺失、2 配准不确定；不等于业务 OK/NG。
    public double DeltaE00, DeltaE76;
    public double DeltaL, DeltaA, DeltaB; // 待测减标准的原始通道差，未施加 k 参数。
    public double PositionError; // 配准后圆心残差，待测原图 px。
    public double ExpectedX, ExpectedY, ExpectedRadius; // 标准映射到待测图的预测几何。
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct Alignment
{
    public double Scale, AngleDegrees; // 标准 → 待测的相似变换，比例 / 度。
    public double TranslateX, TranslateY, Rms; // 待测原图 px；Rms 为配准位置均方根误差。
    public int Matched, Extra; // 已分配点数 / 未分配候选数。
    public int Ambiguous, Reserved; // 非 0 时不得判确认缺失或颜色合格；Reserved 用于布局对齐。
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NativeTimings
{
    public double DecodeMs, SegmentationMs, MeasurementMs;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct MarkQuality
{
    public double OverexposedFraction, UnderexposedFraction, GlareFraction; // 内圈像素比例，0..1。
    public double EdgeContrast; // BGR8 内外颜色差 RMS，非 ΔE00。
    public double EdgeWidth; // 径向 90%→10% 过渡宽度中位数，px。
    public double EdgeSupport, ContourConfidence; // 支持比例 / 综合指标；置信指标不是概率。
    public double FitResidual; // 轮廓点到外接圆的径向 RMS 残差，px。
    public double ValidSampleFraction; // 实际取色区 / 理想内圈像素数。
    public int Refined, Reserved; // 1 表示原分辨率精修成功；Reserved 保留。
}

/// <summary>结果句柄唯一所有者；托管复制完成后释放，异常路径也不能泄漏 Native 图像。</summary>
public sealed class NativeResultHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public NativeResultHandle() : base(true)
    {
    }

    protected override bool ReleaseHandle()
    {
        Native.mc_release(handle);
        return true;
    }
}

/// <summary>C ABI 适配层；上层只拿托管快照，不持有 OpenCV 对象或借用像素指针。</summary>
public static class Native
{
    private const string Dll = "MarkColorNative.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mc_abi_version();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mc_last_error();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int mc_analyze(string path, in NativeSettings settings, out NativeResultHandle result);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mc_result_info(NativeResultHandle result, out int width, out int height, out int count);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mc_result_timings(NativeResultHandle result, out NativeTimings timings);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mc_result_bgr(NativeResultHandle result, out IntPtr pixels, out int stride, out int length);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mc_get_mark(NativeResultHandle result, int index, out Mark mark);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mc_get_contour(NativeResultHandle result, int index, [Out] NativePoint[] points, int capacity);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mc_release(IntPtr result);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mc_compare_weighted(
        [In] Mark[] reference,
        int referenceCount,
        int refWidth,
        int refHeight,
        [In] Mark[] observed,
        int observedCount,
        int width,
        int height,
        in NativeMatchSettings settings,
        in NativeColorDifferenceSettings colorSettings,
        [Out] Comparison[] comparisons,
        out Alignment alignment);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern double mc_delta_e00(double l1, double a1, double b1, double l2, double a2, double b2);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern double mc_delta_e00_weighted(double l1, double a1, double b1, double l2, double a2, double b2,
        in NativeColorDifferenceSettings settings);

    /// <summary>采样偏差与最终检测共用同一套参数；不修改输入 Lab。</summary>
    public static double DeltaE00(LabValue first, LabValue second, ColorDifferenceSettings settings)
    {
        settings.Validate();
        var config = settings.ToNative();
        double result = mc_delta_e00_weighted(first.L, first.A, first.B, second.L, second.A, second.B, in config);
        if (!double.IsFinite(result))
        {
            throw new InvalidOperationException(Marshal.PtrToStringUTF8(mc_last_error()) ?? "色差计算失败。");
        }
        return result;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mc_get_quality(NativeResultHandle result, int index, out MarkQuality quality);

    // 必须紧接 Native 调用且在同一线程执行：mc_last_error 是线程局部文本。
    private static void Check(int result)
    {
        if (result < 0)
        {
            throw new InvalidOperationException(Marshal.PtrToStringUTF8(mc_last_error()) ?? "本机算法调用失败。");
        }
    }

    /// <summary>同步提取入口：复制 Native 结果到托管快照，再由 SafeHandle 释放原生内存。</summary>
    /// <remarks>可选预览直接复制 Native 已解码像素；纯数值验证不分配位图，避免保存整个图库的图像。</remarks>
    public static Analysis Analyze(string path, DetectionSettings settings, bool preparePreview = false)
    {
        if (mc_abi_version() != 3 || Marshal.SizeOf<NativeColorDifferenceSettings>() != 24 || Marshal.SizeOf<MarkQuality>() != 80 || Marshal.SizeOf<Mark>() != 128 || Marshal.SizeOf<Comparison>() != 80)
        {
            throw new InvalidOperationException("本机 DLL 与界面 ABI 版本不兼容。");
        }

        var config = settings.ToNative();
        Check(mc_analyze(path, in config, out var handle));
        using (handle)
        {
            Check(mc_result_info(handle, out int width, out int height, out int count));
            var result = new Analysis
            {
                Path = path,
                Width = width,
                Height = height
            };
            Check(mc_result_timings(handle, out var timings));
            result.Timings = timings;
            if (preparePreview)
            {
                var previewWatch = Stopwatch.StartNew();
                Check(mc_result_bgr(handle, out var pixels, out int stride, out int length));
                // Create 完成复制后才退出 using；预览 Freeze 后可以跨线程显示，句柄不再被 WPF 持有。
                result.Preview = ImagePreview.CopyBgr(width, height, pixels, length, stride);
                result.PreviewMilliseconds = previewWatch.Elapsed.TotalMilliseconds;
            }

            for (int i = 0; i < count; i++)
            {
                Check(mc_get_mark(handle, i, out var mark));
                var points = new NativePoint[mark.ContourCount];
                Check(mc_get_contour(handle, i, points, points.Length));
                result.Marks.Add(mark);
                Check(mc_get_quality(handle, i, out var quality));
                result.Qualities.Add(quality);
                result.Contours.Add(points);
            }

            return result;
        }
    }

    /// <summary>标准编号对应的是输出数组下标；ObservedIndex 才是待测点下标，不能混用。</summary>
    public static (Comparison[], Alignment) Compare(StandardProfile profile, Analysis image)
    {
        var references = profile.Marks.Select(x => x.Value).ToArray();
        var config = profile.Settings.ToMatch();
        var colorConfig = profile.Settings.ColorDifference.ToNative();
        var results = new Comparison[references.Length];
        // 原生层先按几何确定一对一对应，再算色差；颜色异常不得把点“匹配”到相邻墨路。
        Check(mc_compare_weighted(
            references,
            references.Length,
            profile.Width,
            profile.Height,
            image.Marks.ToArray(),
            image.Marks.Count,
            image.Width,
            image.Height,
            in config,
            in colorConfig,
            results,
            out var alignment));
        return (results, alignment);
    }
}
