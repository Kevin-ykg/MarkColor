// 多尺度颜色边缘只负责提出圆点候选；最终轮廓和 Lab 均在原图局部计算。
#include "mark_color_internal.h"
#include <opencv2/imgproc.hpp>
#include <algorithm>
#include <array>
#include <cmath>

namespace mark_color
{
    void appendEdgeCandidates(const cv::Mat& image, const McSettings& settings, std::vector<Contour>& contours)
    {
        // 每层覆盖约 4..40 px 半径，再逐层缩小一半。缩放由目标半径决定，
        // 不是由整图长边或 ROI 大小决定，因此扩大视野不会把小圆点缩没。
        double scale = std::min(1.0, 4.0 / settings.minRadius);
        for (; settings.maxRadius * scale >= 4; scale *= .5)
        {
            cv::Mat reduced;
            cv::resize(image, reduced, {}, scale, scale, cv::INTER_AREA);
            if (reduced.rows < 8 || reduced.cols < 8)
            {
                break;
            }
            // resize 会取整宽高，X/Y 分别用真实尺寸比还原，不能统一使用 1/scale。
            const double restoreScaleX = static_cast<double>(image.cols) / reduced.cols;
            const double restoreScaleY = static_cast<double>(image.rows) / reduced.rows;
            // 三个通道互不依赖，并行求候选后仍按 B/G/R 顺序合并，保持确定的去重优先级。
            // OpenCV 负责线程池调度，避免每个图像临时创建线程或引入跨图共享缓存。
            std::array<std::vector<Contour>, 3> channelContours;
            cv::parallel_for_(
                cv::Range(0, 3),
                [&](const cv::Range& range)
                {
                    for (int channel = range.start; channel < range.end; ++channel)
                    {
                        cv::Mat plane, edges;
                        cv::extractChannel(reduced, plane, channel);
                        cv::GaussianBlur(plane, plane, {3, 3}, .7);
                        // 这里的固定阈值用于生成候选，不是最终的颜色容差；候选还需在原分辨率确认。
                        cv::Canny(plane, edges, 10, 25);
                        std::vector<Contour> candidates;
                        cv::findContours(edges, candidates, cv::RETR_LIST, cv::CHAIN_APPROX_SIMPLE);
                        for (auto& contour : candidates)
                        {
                            cv::Point2f center;
                            float radius;
                            double area = cv::contourArea(contour), perimeter = cv::arcLength(contour, true);
                            // 非圆纹理通常点数最多；先判圆度，避免为必定被拒绝的背景求外接圆。
                            if (perimeter <= 0 || 4 * kPi * area / (perimeter * perimeter) < .68)
                            {
                                continue;
                            }
                            cv::minEnclosingCircle(contour, center, radius);
                            if (radius < std::max(2.5, settings.minRadius * scale * .8) ||
                                radius > std::min(40.0, settings.maxRadius * scale * 1.2) ||
                                area / (kPi * radius * radius) < .7)
                            {
                                continue;
                            }
                            for (auto& point : contour)
                            {
                                point.x = cvRound(point.x * restoreScaleX);
                                point.y = cvRound(point.y * restoreScaleY);
                            }
                            channelContours[channel].push_back(std::move(contour));
                        }
                    }
                },
                3);
            for (auto& candidates : channelContours)
            {
                for (auto& contour : candidates)
                {
                    contours.push_back(std::move(contour));
                }
            }
        }
    }
} // namespace mark_color
