// 只在候选圆点附近按原分辨率精修；本文件不修改用于 Lab 统计的原图。
#include "mark_color_internal.h"
#include <opencv2/imgproc.hpp>
#include <algorithm>
#include <cmath>
#include <numeric>

namespace mark_color
{
    namespace
    {
        struct LocalEvidence
        {
            cv::Rect bounds;    // 证据区域在原图中的范围，左上角用于还原全图坐标。
            cv::Point2f center; // 相对于 bounds 左上角的圆心，原分辨率 px。
            float radius{};     // 粗轮廓外接圆半径，原分辨率 px。
            cv::Mat projection; // CV_32F 颜色差投影，内部约 1、背景约 0。
            double contrast{};  // BGR8 通道差 RMS，不是 CIELAB 色差。
            double noise{};     // 外环背景通道标准差 RMS，用于与边缘对比度比较。
        };

        // 用内核和外环的颜色差方向投影 BGR，兼顾微亮、微暗以及等亮度色边缘。
        // projection≈1 为圆点内部，≈0 为背景；背景噪声用于衡量定位证据。
        LocalEvidence buildLocalEvidence(const cv::Mat& image, const Contour& contour)
        {
            LocalEvidence localEvidence;
            cv::Point2f globalCenter;
            cv::minEnclosingCircle(contour, globalCenter, localEvidence.radius);
            int margin = cvCeil(localEvidence.radius * 1.65);
            localEvidence.bounds = cv::Rect(cvRound(globalCenter.x) - margin, cvRound(globalCenter.y) - margin,
                                            margin * 2 + 1, margin * 2 + 1) &
                                   cv::Rect(0, 0, image.cols, image.rows);
            localEvidence.center = globalCenter - cv::Point2f(static_cast<float>(localEvidence.bounds.x),
                                                              static_cast<float>(localEvidence.bounds.y));
            cv::Mat core = cv::Mat::zeros(localEvidence.bounds.size(), CV_8U), ring = core.clone();
            cv::circle(core, localEvidence.center, cvRound(localEvidence.radius * .55), 255, cv::FILLED);
            cv::circle(ring, localEvidence.center, cvRound(localEvidence.radius * 1.55), 255, cv::FILLED);
            cv::circle(ring, localEvidence.center, cvRound(localEvidence.radius * 1.2), 0, cv::FILLED);
            cv::Scalar background, deviation;
            cv::meanStdDev(image(localEvidence.bounds), background, deviation, ring);
            const auto foreground = cv::mean(image(localEvidence.bounds), core);
            cv::Vec3d direction(foreground[0] - background[0], foreground[1] - background[1],
                                foreground[2] - background[2]);
            localEvidence.contrast = cv::norm(direction) / std::sqrt(3.0); // 原始 8 位颜色差的 RMS，非 ΔE。
            localEvidence.noise = std::sqrt(
                (deviation[0] * deviation[0] + deviation[1] * deviation[1] + deviation[2] * deviation[2]) / 3.0);
            direction /= std::max(direction.dot(direction), 1.0);
            localEvidence.projection.create(localEvidence.bounds.size(), CV_32F);
            for (int y = 0; y < localEvidence.bounds.height; ++y)
            {
                const auto* source = image.ptr<cv::Vec3b>(y + localEvidence.bounds.y) + localEvidence.bounds.x;
                auto* target = localEvidence.projection.ptr<float>(y);
                for (int x = 0; x < localEvidence.bounds.width; ++x)
                {
                    target[x] = static_cast<float>((source[x][0] - background[0]) * direction[0] +
                                                   (source[x][1] - background[1]) * direction[1] +
                                                   (source[x][2] - background[2]) * direction[2]);
                }
            }
            return localEvidence;
        }

        // 径向质量剖面采用最近像素取样并夹紧到局部范围；此值不用于 Lab 统计。
        double sampleProjection(const cv::Mat& image, cv::Point2f point)
        {
            int x = std::clamp(cvRound(point.x), 0, image.cols - 1),
                y = std::clamp(cvRound(point.y), 0, image.rows - 1);
            return image.at<float>(y, x);
        }
    } // namespace

    // 在颜色投影半高线附近寻找包围原圆心的闭合轮廓，再恢复为整图坐标。
    bool refineContour(const cv::Mat& image, Contour& contour)
    {
        auto localEvidence = buildLocalEvidence(image, contour);
        if (localEvidence.contrast < 1.5)
        {
            return false;
        }
        cv::Mat smooth, mask;
        cv::GaussianBlur(localEvidence.projection, smooth, {3, 3}, .8);
        mask = smooth > .5; // 半高位置，避免因调低粗分割阈值扩大最终边界。
        cv::morphologyEx(mask, mask, cv::MORPH_CLOSE, cv::getStructuringElement(cv::MORPH_ELLIPSE, {3, 3}));
        std::vector<Contour> candidates;
        cv::findContours(mask, candidates, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_SIMPLE);
        double bestCenterDistance = 1e9;
        Contour replacement;
        for (auto& candidate : candidates)
        {
            if (candidate.size() < 8 || cv::pointPolygonTest(candidate, localEvidence.center, false) < 0)
            {
                continue;
            }
            cv::Point2f center;
            float radius;
            cv::minEnclosingCircle(candidate, center, radius);
            double distance = cv::norm(center - localEvidence.center), perimeter = cv::arcLength(candidate, true);
            double circularity = 4 * kPi * cv::contourArea(candidate) / (perimeter * perimeter);
            if (distance > localEvidence.radius * .25 || radius < localEvidence.radius * .7 ||
                radius > localEvidence.radius * 1.3 || circularity < .65)
            {
                continue;
            }
            if (distance < bestCenterDistance)
            {
                bestCenterDistance = distance;
                replacement = std::move(candidate);
            }
        }
        if (replacement.empty())
        {
            return false; // 保留粗轮廓，并由质量状态说明精度不足。
        }
        for (auto& point : replacement)
        {
            point += localEvidence.bounds.tl();
        }
        contour = std::move(replacement);
        return true;
    }

    McQuality measureQuality(const cv::Mat& image, const Contour& contour, const McMark& mark, double innerRatio,
                             bool refined)
    {
        auto localEvidence = buildLocalEvidence(image, contour);
        McQuality quality{};
        quality.refined = refined ? 1 : 0;
        quality.edgeContrast = localEvidence.contrast;
        // 每条射线在预期边缘附近寻找 90%→10% 过渡宽度；宽度采用中位数。
        // 这是边缘模糊指标，不把缺墨造成的低对比度直接当作离焦。
        std::vector<double> widths;
        for (int angle = 0; angle < 72; ++angle)
        {
            double theta = angle * 2 * kPi / 72;
            cv::Point2f direction(static_cast<float>(std::cos(theta)), static_cast<float>(std::sin(theta)));
            double transitionStart = -1; // 尚未遇到圆内 90% 强度时的哨兵。
            for (double radialDistance = localEvidence.radius * .55; radialDistance <= localEvidence.radius * 1.4;
                 radialDistance += 1)
            {
                double value = sampleProjection(localEvidence.projection,
                                                localEvidence.center + direction * static_cast<float>(radialDistance));
                if (value >= .9)
                {
                    transitionStart = radialDistance;
                }
                if (transitionStart >= 0 && value <= .1)
                {
                    widths.push_back(radialDistance - transitionStart);
                    break;
                }
            }
        }
        quality.edgeSupport = widths.size() / 72.0;
        if (!widths.empty())
        {
            std::sort(widths.begin(), widths.end());
            quality.edgeWidth = widths[widths.size() / 2];
        }
        else
        {
            quality.edgeWidth = mark.radius;
        }
        // 相对最小外接圆的径向 RMS，不是椭圆拟合误差，也不是拟合统计概率。
        double squaredResidual = 0;
        for (const auto& point : contour)
        {
            double residual = std::hypot(point.x - mark.x, point.y - mark.y) - mark.radius;
            squaredResidual += residual * residual;
        }
        quality.fitResidual = std::sqrt(squaredResidual / std::max<size_t>(1, contour.size()));
        quality.contourConfidence =
            quality.edgeSupport *
            std::clamp(localEvidence.contrast / std::max(3.0, 2 * localEvidence.noise), 0.0, 1.0) *
            std::clamp(1 - quality.fitResidual / std::max(1.0, mark.radius * .2), 0.0, 1.0);

        cv::Mat mask = cv::Mat::zeros(localEvidence.bounds.size(), CV_8U), core = mask.clone();
        Contour local;
        for (auto point : contour)
        {
            local.push_back(point - localEvidence.bounds.tl());
        }
        cv::drawContours(mask, std::vector<Contour>{local}, 0, 255, cv::FILLED);
        cv::circle(core, localEvidence.center, cvRound(mark.radius * innerRatio), 255, cv::FILLED);
        cv::bitwise_and(mask, core, mask);
        quality.validSampleFraction = cv::countNonZero(mask) / static_cast<double>(std::max(1, cv::countNonZero(core)));
        const double average = (mark.blue + mark.green + mark.red) / 3;
        for (int y = 0; y < mask.rows; ++y)
        {
            const auto* row = image.ptr<cv::Vec3b>(y + localEvidence.bounds.y) + localEvidence.bounds.x;
            const auto* selected = mask.ptr<uint8_t>(y);
            for (int x = 0; x < mask.cols; ++x)
            {
                if (!selected[x])
                {
                    continue;
                }
                int minimumChannel = std::min({row[x][0], row[x][1], row[x][2]}),
                    maximumChannel = std::max({row[x][0], row[x][1], row[x][2]});
                quality.overexposedFraction += minimumChannel >= 250;
                quality.underexposedFraction += maximumChannel <= 5;
                // 中性高亮局部斑块；是反光风险指标，不能仅凭 RGB 证明物理反光。
                quality.glareFraction +=
                    minimumChannel >= 230 && maximumChannel - minimumChannel <= 20 && minimumChannel > average + 20;
            }
        }
        // 分母与 measureMark 的轮廓∩内圈像素数一致，避免把局部包围框背景计入比例。
        double count = std::max(1, mark.sampleCount);
        quality.overexposedFraction /= count;
        quality.underexposedFraction /= count;
        quality.glareFraction /= count;
        return quality;
    }
} // namespace mark_color
