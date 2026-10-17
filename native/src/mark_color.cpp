// DLL 接口层：校验调用方缓冲区、管理结果生命周期、隔离 C++ 异常。
// 算法入口分别在 mark_detection.cpp / mark_matching.cpp / color_difference.cpp。
#include "mark_color_internal.h"

#include <exception>
#include <cmath>
#include <limits>
#include <string>

namespace
{
    thread_local std::string lastError; // 每个调用线程独立，错误文本不可跨线程延后读取。
}

// 对外结构体布局版本；C# 先核对此值，防止加载不兼容 DLL 后错误解释内存。
int mc_abi_version()
{
    return 3;
}

// 返回当前线程最近记录的错误；指针属于 DLL，调用方只复制、不释放。
const char* mc_last_error()
{
    // 调用方应在当前线程的下一次算法调用之前复制这段 UTF-8 文本。
    return lastError.c_str();
}

// 成功返回 0，并把拥有结果的句柄交给调用方；失败返回 -1 且 *result 保持空。
// path 为 UTF-16；settings 只在本次调用中读取，结果必须最终交给 mc_release。
int mc_analyze(const wchar_t* path, const McSettings* settings, void** result)
{
    if (result != nullptr)
    {
        *result = nullptr;
    }

    try
    {
        mark_color::require(path && settings && result, "Null argument.");
        lastError.clear();
        auto analysis = mark_color::analyzeImage(path, *settings);

        // 仅成功时移交所有权；失败时 unique_ptr 自动释放中间结果。
        *result = analysis.release();
        return 0;
    }
    catch (const std::exception& error)
    {
        lastError = error.what();
        return -1;
    }
    catch (...)
    {
        lastError = "Unknown native error.";
        return -1;
    }
}

// 读取尺寸和点数，以便托管端分配正确大小的结果缓冲。所有输出指针都由调用方提供。
int mc_result_info(void* result, int* width, int* height, int* count)
{
    if (!result || !width || !height || !count)
    {
        return -1;
    }

    const auto& analysis = *static_cast<mark_color::AnalysisResult*>(result);
    *width = analysis.width;
    *height = analysis.height;
    *count = static_cast<int>(analysis.marks.size());
    return 0;
}

// 按检测下标复制一个 POD 结构体；index 不是标准编号，对应关系由 mc_compare 计算。
int mc_get_mark(void* result, int index, McMark* mark)
{
    if (!result || !mark || index < 0)
    {
        return -1;
    }

    const auto& analysis = *static_cast<mark_color::AnalysisResult*>(result);
    if (index >= static_cast<int>(analysis.marks.size()))
    {
        return -1;
    }

    *mark = analysis.marks[index];
    return 0;
}

// 质量证据与 mc_get_mark 使用同一检测下标，交由业务层判定本次颜色测量是否可信。
int mc_get_quality(void* result, int index, McQuality* quality)
{
    if (!result || !quality || index < 0 ||
        index >= static_cast<int>(static_cast<mark_color::AnalysisResult*>(result)->qualities.size()))
    {
        lastError = "Invalid quality result/index/output.";
        return -1;
    }
    *quality = static_cast<mark_color::AnalysisResult*>(result)->qualities[index];
    return 0;
}

// 返回当前句柄对应单次分析的分阶段耗时，不使用全局状态，支持独立并发调用。
int mc_result_timings(void* result, McTimings* timings)
{
    if (!result || !timings)
    {
        return -1;
    }
    *timings = static_cast<mark_color::AnalysisResult*>(result)->timings;
    return 0;
}

// 让 WPF 复制已经解码的原图，而非再次从磁盘用另一套解码器读取。
// 返回的步长含行对齐；length 是可读字节数。指针借用期限严格受 result 句柄约束。
int mc_result_bgr(void* result, const uint8_t** pixels, int* stride, int* length)
{
    if (!result || !pixels || !stride || !length)
    {
        return -1;
    }
    const auto& image = static_cast<mark_color::AnalysisResult*>(result)->sourceBgr;
    *pixels = image.data;
    *stride = static_cast<int>(image.step);
    *length = static_cast<int>(image.step * image.rows);
    return 0;
}

// 将轮廓逐点复制到调用方数组；capacity 必须 >= McMark.contourCount。
// 返回复制点数，参数无效返回 -1；不返回 vector 内存指针，避免跨语言生命周期问题。
int mc_get_contour(void* result, int index, McPoint* points, int capacity)
{
    if (!result || index < 0)
    {
        return -1;
    }

    const auto& analysis = *static_cast<mark_color::AnalysisResult*>(result);
    if (index >= static_cast<int>(analysis.contours.size()))
    {
        return -1;
    }

    const auto& contour = analysis.contours[index];
    if (!points || capacity < static_cast<int>(contour.size()))
    {
        return -1;
    }

    for (size_t pointIndex = 0; pointIndex < contour.size(); ++pointIndex)
    {
        points[pointIndex] = {static_cast<double>(contour[pointIndex].x), static_cast<double>(contour[pointIndex].y)};
    }
    return static_cast<int>(contour.size());
}

// 句柄所有权的唯一释放入口；C# SafeHandle 在读取完结果后调用，空句柄可安全释放。
void mc_release(void* result)
{
    delete static_cast<mark_color::AnalysisResult*>(result);
}

// 两组浮点 CIELAB 的感知色差，L* 为 0..100，a*/b* 带符号；不是 OpenCV Lab8 编码。
double mc_delta_e00(double l1, double a1, double b1, double l2, double a2, double b2)
{
    return mark_color::calculateDeltaE00(l1, a1, b1, l2, a2, b2);
}

// 只消费几何/测色快照，不重新读图。comparisons 至少容纳 referenceCount 个元素。
// 标准→待测配准后输出逐点状态与色差；几何不确定也成功返回 0，由 status/ambiguous 表达。
int mc_compare(const McMark* reference, int referenceCount, int referenceWidth, int referenceHeight,
               const McMark* observed, int observedCount, int observedWidth, int observedHeight,
               const McMatchSettings* settings, McComparison* comparisons, McAlignment* alignment)
{
    const McColorDifferenceSettings defaults{};
    return mc_compare_weighted(reference, referenceCount, referenceWidth, referenceHeight, observed, observedCount,
                               observedWidth, observedHeight, settings, &defaults, comparisons, alignment);
}

// 标量接口没有整数状态码，失败统一返回 NaN；调用方应立即复制线程局部错误。
double mc_delta_e00_weighted(double l1, double a1, double b1, double l2, double a2, double b2,
                             const McColorDifferenceSettings* settings)
{
    try
    {
        mark_color::require(settings != nullptr, "Missing color difference settings.");
        mark_color::validateColorSettings(*settings);
        for (double value : {l1, a1, b1, l2, a2, b2})
        {
            mark_color::require(std::isfinite(value), "Lab components must be finite.");
        }
        lastError.clear();
        return mark_color::calculateDeltaE00(l1, a1, b1, l2, a2, b2, *settings);
    }
    catch (const std::exception& error)
    {
        lastError = error.what();
    }
    catch (...)
    {
        lastError = "Unknown color difference error.";
    }
    return std::numeric_limits<double>::quiet_NaN();
}

// 几何参数、色差参数分开传入，避免调整 kL 时意外改变圆点配准。
int mc_compare_weighted(const McMark* reference, int referenceCount, int referenceWidth, int referenceHeight,
                        const McMark* observed, int observedCount, int observedWidth, int observedHeight,
                        const McMatchSettings* settings, const McColorDifferenceSettings* colorSettings,
                        McComparison* comparisons, McAlignment* alignment)
{
    try
    {
        mark_color::require(reference && referenceCount > 0 && referenceCount <= mark_color::kMaximumMarkCount &&
                                observedCount >= 0 && observedCount <= mark_color::kMaximumMarkCount &&
                                (observedCount == 0 || observed) && settings && colorSettings && comparisons &&
                                alignment,
                            "Invalid comparison buffers (maximum 16 marks).");

        mark_color::compareMarks(reference, referenceCount, referenceWidth, referenceHeight, observed, observedCount,
                                 observedWidth, observedHeight, *settings, comparisons, *alignment, *colorSettings);
        lastError.clear();
        return 0;
    }
    catch (const std::exception& error)
    {
        lastError = error.what();
        return -1;
    }
    catch (...)
    {
        lastError = "Unknown comparison error.";
        return -1;
    }
}
