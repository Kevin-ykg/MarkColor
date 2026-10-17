#pragma once

#include "mark_color.h"

#include <opencv2/core.hpp>
#include <memory>
#include <stdexcept>
#include <vector>

// 仅 Native 内部使用。此文件中的 OpenCV/STL 类型不跨 DLL 边界。
namespace mark_color
{
    constexpr double kPi = 3.14159265358979323846;
    constexpr int kMaximumMarkCount = 16;
    constexpr int kMaximumCandidateCount = 256; // 允许预览大视野；配准仍限制为 16 点。

    // 整数轮廓；候选生成时相对传入 ROI，analyzeImage 统一还原整图坐标后才精修/测色。
    using Contour = std::vector<cv::Point>;

    struct AnalysisResult
    {
        int width = 0;
        int height = 0;
        McTimings timings{};
        cv::Mat sourceBgr; // 共享已解码原图，用于显示复制；不会再次读文件/解码。
        std::vector<McMark> marks;
        std::vector<McQuality> qualities;
        std::vector<Contour> contours; // 与 marks 使用相同下标，坐标均为原图像素。
    };

    inline void require(bool condition, const char* message)
    {
        if (!condition)
        {
            throw std::invalid_argument(message);
        }
    }

    // 返回独占结果；异常由导出接口捕获，sourceBgr 的引用计数保证句柄存活期间像素有效。
    std::unique_ptr<AnalysisResult> analyzeImage(const wchar_t* path, const McSettings& settings);

    // 输出按 reference 下标排列；颜色只在几何分配完成后计算，歧义通过 alignment/status 表达。
    void compareMarks(const McMark* reference, int referenceCount, int referenceWidth, int referenceHeight,
                      const McMark* observed, int observedCount, int observedWidth, int observedHeight,
                      const McMatchSettings& settings, McComparison* comparisons, McAlignment& alignment,
                      const McColorDifferenceSettings& colorSettings = {});

    // 输入/输出均为整图 px；失败返回 false 且保留粗轮廓，调用方决定拒绝或展示质量提示。
    bool refineContour(const cv::Mat& image, Contour& contour);
    // 追加的候选相对 image 左上角；不去重、不取色，最终接受与否由检测入口决定。
    void appendEdgeCandidates(const cv::Mat& image, const McSettings& settings, std::vector<Contour>& contours);
    // contour/mark 必须属于同一点、同一原图；只生成证据，业务阈值保留在 C# QualitySettings。
    McQuality measureQuality(const cv::Mat& image, const Contour& contour, const McMark& mark, double innerRatio,
                             bool refined);

    void validateColorSettings(const McColorDifferenceSettings& settings);
    double calculateDeltaE00(double l1, double a1, double b1, double l2, double a2, double b2,
                             const McColorDifferenceSettings& settings = {});
} // namespace mark_color
