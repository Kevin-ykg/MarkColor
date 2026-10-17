// 圆点定位和颜色测量。分割可以缩小图像，取色始终使用原始 BGR 像素。
#include "mark_color_internal.h"

#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>
#include <algorithm>
#include <array>
#include <cmath>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <numeric>

namespace mark_color
{
    namespace
    {
        // 本文件私有的读图、分割和统计辅助函数；不暴露为 DLL 接口。
        constexpr int kMinimumSampleCount = 1; // 少量但非空样本保留数值，由质量层拒绝判色。
        constexpr double kBackgroundOuterRadiusRatio = 1.55;
        constexpr double kBackgroundInnerRadiusRatio = 1.2;

        // 所有参数先检查有限性再检查范围，避免 NaN 绕过比较并传入 OpenCV。
        // 半径单位为原图像素；ROI 则是相对于整幅图像的 [0,1] 比例。
        void validateDetectionSettings(const McSettings& settings)
        {
            const double values[] = {settings.minRadius,      settings.maxRadius,     settings.minCircularity,
                                     settings.minFill,        settings.saturationMin, settings.darkMax,
                                     settings.brightContrast, settings.innerRatio,    settings.roiX,
                                     settings.roiY,           settings.roiWidth,      settings.roiHeight};
            for (double value : values)
            {
                require(std::isfinite(value), "Non-finite detection setting.");
            }
            require(settings.minRadius >= 3 && settings.maxRadius > settings.minRadius && settings.maxRadius <= 2000,
                    "Invalid radius range (pixels).");
            require(settings.minCircularity > 0 && settings.minCircularity <= 1 && settings.minFill > 0 &&
                        settings.minFill <= 1,
                    "Shape thresholds must be in (0,1].");
            require(settings.saturationMin >= 1 && settings.saturationMin <= 255 && settings.darkMax >= 0 &&
                        settings.darkMax <= 254,
                    "Invalid segmentation threshold.");
            require(settings.brightContrast >= 0 && settings.brightContrast <= 80 && settings.innerRatio >= .2 &&
                        settings.innerRatio <= .9,
                    "Invalid contrast or inner ratio.");
            require(settings.roiX >= 0 && settings.roiY >= 0 && settings.roiWidth > 0 && settings.roiHeight > 0 &&
                        settings.roiX + settings.roiWidth <= 1.000001 && settings.roiY + settings.roiHeight <= 1.000001,
                    "ROI must be inside image.");
        }

        // 一次读取文件字节并解码；返回独立持有像素的 BGR8 Mat，不依赖文件流的生命周期。
        // 不在这里做缩放/曝光校正，以免后续 Lab 取色脱离原始采集数据。
        cv::Mat loadColorImage(const wchar_t* path)
        {
            // 通过宽字符 filesystem 路径读字节，避免 imread 在中文路径上的编码差异。
            std::ifstream stream(std::filesystem::path(path), std::ios::binary | std::ios::ate);
            require(stream.good(), "Cannot open image file.");
            const auto length = stream.tellg();
            require(length > 0 && length < 512LL * 1024 * 1024, "Image file is empty or exceeds 512 MB.");
            std::vector<uchar> bytes(static_cast<size_t>(length));
            stream.seekg(0);
            stream.read(reinterpret_cast<char*>(bytes.data()), length);
            require(stream.good(), "Incomplete image read.");

            cv::Mat image = cv::imdecode(bytes, cv::IMREAD_UNCHANGED);
            require(!image.empty() && image.depth() == CV_8U && (image.channels() == 3 || image.channels() == 4),
                    "Use an 8-bit RGB/BGRA image (BMP/PNG/JPEG/TIFF).");
            require(image.total() <= 80000000, "Image exceeds 80 megapixels.");
            if (image.channels() == 4)
            {
                cv::cvtColor(image, image, cv::COLOR_BGRA2BGR);
            }
            return image;
        }

        // 将归一化 ROI 转为像素矩形，并裁到图像内；后续候选坐标暂时相对于此矩形。
        cv::Rect detectionRegion(const cv::Mat& image, const McSettings& settings)
        {
            cv::Rect region(cvRound(settings.roiX * image.cols), cvRound(settings.roiY * image.rows),
                            cvRound(settings.roiWidth * image.cols), cvRound(settings.roiHeight * image.rows));
            region &= cv::Rect(0, 0, image.cols, image.rows);
            require(region.width >= 8 && region.height >= 8, "ROI is too small.");
            return region;
        }

        // 彩色/黑色掩膜可能漏掉接近白色的缺墨点，因此追加局部微亮/微暗候选。
        // 这里只降低定位分辨率；轮廓会映射回 ROI 原尺寸，颜色仍在原图上测量。
        void appendPaleContours(const cv::Mat& gray, const McSettings& settings, std::vector<Contour>& contours,
                                size_t& negativeStart)
        {
            negativeStart = contours.size();
            if (settings.brightContrast <= 0)
            {
                return;
            }

            // 最小半径保留约 3.5 px。另保留原有中尺度背景窗口：把最小半径调小，
            // 不应同时缩窄所有背景估计窗口，导致大而浅的圆点只剩下断裂的亮边。
            std::vector<cv::Mat> contrasts;
            const auto addBackgroundLevel = [&](double radius, double scale)
            {
                cv::Mat reduced, background;
                cv::resize(gray, reduced, {}, scale, scale, cv::INTER_AREA);
                reduced.convertTo(reduced, CV_32F);
                const double sigma = std::max(2.0, std::min(settings.maxRadius * .375, radius * 2.4) * scale);
                cv::GaussianBlur(reduced, background, {}, sigma);
                contrasts.push_back(reduced - background);
            };
            addBackgroundLevel(settings.minRadius, std::clamp(3.5 / settings.minRadius, .0625, 1.0));
            if (settings.minRadius < 25 && settings.maxRadius >= 25)
            {
                addBackgroundLevel(25, .25);
            }
            for (int polarity : {1, -1})
            {
                if (polarity < 0)
                {
                    negativeStart = contours.size();
                }
                for (const auto& contrast : contrasts)
                {
                    cv::Mat paleMask = (polarity * contrast) > settings.brightContrast;
                    cv::morphologyEx(paleMask, paleMask, cv::MORPH_CLOSE,
                                     cv::getStructuringElement(cv::MORPH_ELLIPSE, {3, 3}));
                    std::vector<Contour> paleContours;
                    cv::findContours(paleMask, paleContours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_SIMPLE);
                    const double scaleX = static_cast<double>(gray.cols) / contrast.cols;
                    const double scaleY = static_cast<double>(gray.rows) / contrast.rows;
                    for (auto& contour : paleContours)
                    {
                        for (auto& point : contour)
                        {
                            point.x = cvRound(point.x * scaleX);
                            point.y = cvRound(point.y * scaleY);
                        }
                        contours.push_back(std::move(contour));
                    }
                }
            }
        }

        // 返回未经几何筛选的外轮廓，允许包含纸边/线条；由 passesShapeFilter 排除它们。
        // 两个分割分支可能重复发现同一个圆点，主流程会按圆心/半径去重。
        std::vector<Contour> cleanColorContours(const cv::Mat& mask, double minimumRadius)
        {
            // 纸张上下边沿可能让单个包围框覆盖整图；分行段处理仍能跳过中间空白。
            // 四次半径 2 的形态学运算最多传播 8 像素；扩展每段并合并重叠范围，保留全部相互作用。
            constexpr int kMorphologyMargin = 8;
            cv::Mat rowMaximum;
            cv::reduce(mask, rowMaximum, 1, cv::REDUCE_MAX);
            std::vector<cv::Range> bands;
            for (int row = 0; row < mask.rows; ++row)
            {
                if (rowMaximum.at<uint8_t>(row) == 0)
                {
                    continue;
                }
                const int start = std::max(0, row - kMorphologyMargin);
                const int end = std::min(mask.rows, row + kMorphologyMargin + 1);
                if (bands.empty() || start > bands.back().end)
                {
                    bands.emplace_back(start, end);
                }
                else
                {
                    bands.back().end = end;
                }
            }

            std::vector<Contour> contours;
            const int kernelSize = minimumRadius < 12 ? 3 : 5;
            const auto kernel = cv::getStructuringElement(cv::MORPH_ELLIPSE, {kernelSize, kernelSize});
            // 外轮廓列表与扫描发现顺序相反；自下向上处理行段，保留原检测/去重顺序。
            for (auto band = bands.rbegin(); band != bands.rend(); ++band)
            {
                const auto bounds = cv::boundingRect(mask.rowRange(*band));
                const int left = std::max(0, bounds.x - kMorphologyMargin);
                const int right = std::min(mask.cols, bounds.br().x + kMorphologyMargin);
                const cv::Rect activeBounds(left, band->start, right - left, band->size());
                cv::Mat activeMask = mask(activeBounds).clone();
                cv::morphologyEx(activeMask, activeMask, cv::MORPH_OPEN, kernel);
                cv::morphologyEx(activeMask, activeMask, cv::MORPH_CLOSE, kernel);
                std::vector<Contour> bandContours;
                cv::findContours(activeMask, bandContours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_SIMPLE,
                                 activeBounds.tl());
                for (auto& contour : bandContours)
                {
                    contours.push_back(std::move(contour));
                }
            }
            return contours;
        }

        // 深色/彩色定位保持全分辨率；仅跳过确定没有形态学依赖的纯背景行段。
        std::vector<Contour> findCandidateContours(const cv::Mat& regionImage, const McSettings& settings,
                                                   size_t& negativeStart)
        {
            cv::Mat hsv, gray;
            cv::cvtColor(regionImage, hsv, cv::COLOR_BGR2HSV);
            cv::cvtColor(regionImage, gray, cv::COLOR_BGR2GRAY);
            cv::Mat saturation;
            // 分割只需要 S 通道，避免拆分并分配完全不用的 H、V 两幅全图。
            cv::extractChannel(hsv, saturation, 1);

            // 饱和度分支覆盖彩色点，低灰度分支覆盖黑色点。
            cv::Mat mask = (saturation > settings.saturationMin) | (gray < settings.darkMax);
            auto contours = cleanColorContours(mask, settings.minRadius);
            appendPaleContours(gray, settings, contours, negativeStart);
            return contours;
        }

        // 原地选择两端各 5% 的分界值，保留中间 90% 求均值。
        // 不需要完整排序：nth_element 的平均复杂度为 O(n)，避免原 O(n log n) 排序。
        // 相等像素落在哪一侧不影响均值；传引用并重排工作缓冲，避免再复制整通道数据。
        double trimmedMean(std::vector<float>& values)
        {
            const auto trimCount = values.size() / 20; // 每个通道分别去掉最低、最高各 5%。
            const auto first = values.begin() + trimCount;
            const auto last = values.end() - trimCount;
            if (trimCount > 0)
            {
                std::nth_element(values.begin(), first, values.end());
                std::nth_element(first, last, values.end());
            }
            return std::accumulate(first, last, 0.0) / (values.size() - 2 * trimCount);
        }

        // 只转换圆点附近的小块图像，避免为整幅图分配浮点三通道 Lab。
        // 必须先归一化到 [0,1]；直接对 BGR8 转换会得到带编码偏置的 Lab8。
        cv::Mat convertToLab(const cv::Mat& bgr)
        {
            cv::Mat normalized, lab;
            bgr.convertTo(normalized, CV_32FC3, 1.0 / 255.0);
            cv::cvtColor(normalized, lab, cv::COLOR_BGR2Lab);
            return lab; // 浮点 Lab：L*=0..100，a*/b* 带符号，无 8 位编码偏置。
        }

        // 测量圆点外 1.2r..1.55r 环带的 L*，作为排查光照变化的辅助值，不参与色差判定。
        double measureBackgroundLightness(const cv::Mat& lab, const cv::Rect& bounds, const McMark& mark)
        {
            const int outerRadius = cvRound(mark.radius * kBackgroundOuterRadiusRatio);
            cv::Mat ring = cv::Mat::zeros(bounds.size(), CV_8U);
            const cv::Point center(cvRound(mark.x) - bounds.x, cvRound(mark.y) - bounds.y);
            cv::circle(ring, center, outerRadius, 255, cv::FILLED);
            cv::circle(ring, center, cvRound(mark.radius * kBackgroundInnerRadiusRatio), 0, cv::FILLED);
            return cv::mean(lab, ring)[0];
        }

        // 输入轮廓必须已转换为整图坐标。先求几何，再构建局部取色掩膜，最后统计颜色。
        // 内圈减少模糊边缘和纸色污染；Lab 使用截尾均值，RGB/σ 保留全内圈统计口径。
        McMark measureMark(const cv::Mat& bgr, const Contour& contour, double innerRatio)
        {
            McMark mark{};
            cv::Point2f center;
            float radius;
            cv::minEnclosingCircle(contour, center, radius);
            mark.x = center.x;
            mark.y = center.y;
            mark.radius = radius;
            mark.area = cv::contourArea(contour);
            const double perimeter = cv::arcLength(contour, true);
            mark.circularity = 4 * kPi * mark.area / (perimeter * perimeter);

            const auto bounds = cv::boundingRect(contour) & cv::Rect(0, 0, bgr.cols, bgr.rows);
            cv::Mat sampleMask = cv::Mat::zeros(bounds.size(), CV_8U);
            cv::Mat coreMask = sampleMask.clone();
            Contour localContour;
            for (const auto& point : contour)
            {
                localContour.push_back(point - bounds.tl());
            }

            // 取色区 = 实际填充轮廓 ∩ 内圈，避免混入圆点边缘和背景。
            cv::drawContours(sampleMask, std::vector<Contour>{localContour}, 0, 255, cv::FILLED);
            cv::circle(coreMask, cv::Point(cvRound(mark.x) - bounds.x, cvRound(mark.y) - bounds.y),
                       cvRound(radius * innerRatio), 255, cv::FILLED);
            cv::bitwise_and(sampleMask, coreMask, sampleMask);
            mark.sampleCount = cv::countNonZero(sampleMask);
            mark.contourCount = static_cast<int>(contour.size());
            require(mark.sampleCount >= kMinimumSampleCount, "Insufficient inner sampling pixels.");

            // 内圈包含于背景外环包围框。一次转换覆盖两者，避免内圈像素重复做 BGR→Lab。
            const int outerRadius = cvRound(mark.radius * kBackgroundOuterRadiusRatio);
            const auto labBounds = cv::Rect(cvRound(mark.x) - outerRadius, cvRound(mark.y) - outerRadius,
                                            outerRadius * 2 + 1, outerRadius * 2 + 1) &
                                   cv::Rect(0, 0, bgr.cols, bgr.rows);
            const cv::Mat labWithBackground = convertToLab(bgr(labBounds));
            const cv::Mat lab = labWithBackground(cv::Rect(bounds.tl() - labBounds.tl(), bounds.size()));
            std::array<std::vector<float>, 3> channels;
            for (auto& channel : channels)
            {
                channel.reserve(mark.sampleCount);
            }
            for (int y = 0; y < lab.rows; ++y)
            {
                // Mat 的步长不保证等于 cols*像素大小；逐行 ptr 既支持 ROI，也减少每像素 at 检查。
                const auto* maskRow = sampleMask.ptr<uint8_t>(y);
                const auto* labRow = lab.ptr<cv::Vec3f>(y);
                for (int x = 0; x < lab.cols; ++x)
                {
                    if (maskRow[x] == 0)
                    {
                        continue;
                    }
                    const auto& pixel = labRow[x];
                    for (int channel = 0; channel < 3; ++channel)
                    {
                        channels[channel].push_back(pixel[channel]);
                    }
                }
            }
            mark.l = trimmedMean(channels[0]);
            mark.a = trimmedMean(channels[1]);
            mark.b = trimmedMean(channels[2]);

            // σ 使用所有内圈像素；与去极值的 Lab 均值有意采用不同统计口径。
            cv::Scalar mean, standardDeviation;
            cv::meanStdDev(lab, mean, standardDeviation, sampleMask);
            mark.stdL = standardDeviation[0];
            mark.stdA = standardDeviation[1];
            mark.stdB = standardDeviation[2];
            const auto meanBgr = cv::mean(bgr(bounds), sampleMask);
            mark.blue = meanBgr[0];
            mark.green = meanBgr[1];
            mark.red = meanBgr[2];
            mark.backgroundL = measureBackgroundLightness(labWithBackground, labBounds, mark);
            return mark;
        }

        // 输出 ROI 内圆心/半径；圆度排除线条，填充率排除空洞/弧形，触边排除被裁切的点。
        bool passesShapeFilter(const Contour& contour, const cv::Rect& region, const McSettings& settings,
                               cv::Point2f& center, float& radius)
        {
            const double area = cv::contourArea(contour);
            const double perimeter = cv::arcLength(contour, true);
            // 长膜片边缘/纹理轮廓可能包含数万点，最小外接圆在这类轮廓上非常昂贵。
            // 先用线性复杂度的面积、周长、包围框排除不可能通过原判据的候选，阈值不变。
            if (perimeter <= 0 || 4 * kPi * area / (perimeter * perimeter) < settings.minCircularity)
            {
                return false;
            }
            const auto bounds = cv::boundingRect(contour);
            if (bounds.x <= 0 || bounds.y <= 0 || bounds.br().x >= region.width || bounds.br().y >= region.height ||
                bounds.width - 1 > 2 * (settings.maxRadius + .5) || bounds.height - 1 > 2 * (settings.maxRadius + .5) ||
                area < settings.minFill * kPi * std::pow(std::max(0.0, settings.minRadius - .5), 2))
            {
                return false;
            }
            cv::minEnclosingCircle(contour, center, radius);
            if (radius + .5 < settings.minRadius || radius > settings.maxRadius + .5 ||
                area / (kPi * radius * radius) < settings.minFill)
            {
                return false;
            }
            return true;
        }

        // 与已接受点有明显重叠时跳过，防止同一圆点在深色分支和浅色分支重复计数。
        bool overlapsExistingMark(cv::Point2f center, float radius, const std::vector<McMark>& marks)
        {
            return std::any_of(marks.begin(), marks.end(),
                               [&](const McMark& mark)
                               {
                                   return std::hypot(center.x - mark.x, center.y - mark.y) <
                                          std::max(static_cast<double>(radius), mark.radius);
                               });
        }

        // 排序只生成便于显示的检测下标，不代表标准对应关系；marks/qualities/contours 同步重排。
        void sortMarksLeftToRight(AnalysisResult& result)
        {
            std::vector<size_t> order(result.marks.size());
            std::iota(order.begin(), order.end(), 0);
            std::sort(order.begin(), order.end(),
                      [&](size_t left, size_t right) { return result.marks[left].x < result.marks[right].x; });
            const auto marks = result.marks;
            const auto qualities = result.qualities;
            auto contours = std::move(result.contours);
            result.contours.resize(contours.size());
            for (size_t index = 0; index < order.size(); ++index)
            {
                result.marks[index] = marks[order[index]];
                result.qualities[index] = qualities[order[index]];
                result.contours[index] = std::move(contours[order[index]]);
            }
        }
    } // namespace

    // 单图检测的总入口。unique_ptr 保证读图/测量任一步抛异常时，中间结果自动释放。
    // 几何筛选在 ROI 内完成；进入 measureMark 前统一加上 ROI 偏移，保证所有对外坐标一致。
    std::unique_ptr<AnalysisResult> analyzeImage(const wchar_t* path, const McSettings& settings)
    {
        validateDetectionSettings(settings);
        const auto start = std::chrono::steady_clock::now();
        const cv::Mat image = loadColorImage(path);
        const auto decoded = std::chrono::steady_clock::now();
        const cv::Rect region = detectionRegion(image, settings);
        // 候选分支连续排列：[彩色/深色/微亮] [微暗] [多尺度边缘]。
        // 两个起始下标用于选择严格程度不同的精修证据，不能在中间随意重排候选。
        size_t negativeStart = 0;
        auto contours = findCandidateContours(image(region), settings, negativeStart);
        const size_t edgeStart = contours.size();
        appendEdgeCandidates(image(region), settings, contours);
        const auto segmented = std::chrono::steady_clock::now();
        auto result = std::make_unique<AnalysisResult>();
        result->width = image.cols;
        result->height = image.rows;
        result->sourceBgr = image; // Mat 引用计数延长像素生命周期，直到调用方释放分析句柄。
        for (size_t candidateIndex = 0; candidateIndex < contours.size(); ++candidateIndex)
        {
            auto& contour = contours[candidateIndex];
            cv::Point2f center;
            float radius;
            const bool edgeCandidate = candidateIndex >= edgeStart;
            auto coarseSettings = settings;
            if (edgeCandidate)
            {
                // 低分辨率边缘只做宽松预筛，不能在精修前因一两个像素的偏差丢点。
                coarseSettings.minRadius *= .8;
                coarseSettings.maxRadius *= 1.2;
                coarseSettings.minCircularity = .68;
                coarseSettings.minFill = std::min(.7, settings.minFill);
            }
            if (!passesShapeFilter(contour, region, coarseSettings, center, radius))
            {
                continue;
            }
            center += cv::Point2f(static_cast<float>(region.x), static_cast<float>(region.y));
            if (overlapsExistingMark(center, radius, result->marks))
            {
                continue;
            }
            for (auto& point : contour)
            {
                point += region.tl();
            }
            bool refined = refineContour(image, contour);
            // 新增微暗分支容易包含镜头/基材上的模糊阴影，须有原尺寸闭合边缘证据。
            // 原有彩色/微亮分支保留弱候选，由质量层给出精度不足提示。
            if (candidateIndex >= negativeStart && !refined)
            {
                continue;
            }
            const auto refinedBounds = cv::boundingRect(contour);
            if (refinedBounds.x <= region.x || refinedBounds.y <= region.y || refinedBounds.br().x >= region.br().x ||
                refinedBounds.br().y >= region.br().y)
            {
                continue;
            }
            // 先复核几何再取色。大视野的二维码、手部等候选常在精修后失去圆形特征，
            // 不应先为这些必定被拒绝的区域分配浮点 Lab、逐像素排序并计算质量。
            cv::Point2f refinedCenter;
            float refinedRadius;
            cv::minEnclosingCircle(contour, refinedCenter, refinedRadius);
            const double refinedArea = cv::contourArea(contour);
            const double refinedPerimeter = cv::arcLength(contour, true);
            const double fill = refinedArea / (kPi * refinedRadius * refinedRadius);
            const double circularity = 4 * kPi * refinedArea / (refinedPerimeter * refinedPerimeter);
            if ((refined && (circularity < settings.minCircularity || fill < settings.minFill)) ||
                (!refined && fill < .8) ||
                (edgeCandidate && (refinedRadius + .5 < settings.minRadius || refinedRadius > settings.maxRadius + .5 ||
                                   fill < std::max(.8, settings.minFill))))
            {
                continue;
            }
            auto mark = measureMark(image, contour, settings.innerRatio);
            auto quality = measureQuality(image, contour, mark, settings.innerRatio, refined);
            // 二维码小方块有时通过粗圆度筛选，但没有闭合圆边缘证据且填充不足。
            // 保留接近实心圆的低置信候选供质量层提示，拒绝这些未精修的非圆小块。
            if (!refined && mark.area / (kPi * mark.radius * mark.radius) < .8)
            {
                continue;
            }
            if (edgeCandidate)
            {
                // 背景中键盘、二维码也有闭合边缘；原尺寸填充率、圆度和边缘证据共同确认。
                if (!refined || mark.radius + .5 < settings.minRadius || mark.radius > settings.maxRadius + .5 ||
                    mark.circularity < settings.minCircularity ||
                    mark.area / (kPi * mark.radius * mark.radius) < std::max(.8, settings.minFill) ||
                    quality.contourConfidence < .5 || quality.edgeSupport < .7 || quality.edgeWidth > mark.radius * .4)
                {
                    continue;
                }
            }
            else if (candidateIndex >= negativeStart &&
                     (quality.contourConfidence < .5 || quality.edgeWidth > mark.radius * .4))
            {
                continue;
            }
            result->qualities.push_back(quality);
            result->marks.push_back(mark);
            result->contours.push_back(std::move(contour));
            require(result->marks.size() <= kMaximumCandidateCount,
                    "More than 256 candidates: restrict ROI or tighten shape thresholds.");
        }
        sortMarksLeftToRight(*result);
        const auto measured = std::chrono::steady_clock::now();
        result->timings = {std::chrono::duration<double, std::milli>(decoded - start).count(),
                           std::chrono::duration<double, std::milli>(segmented - decoded).count(),
                           std::chrono::duration<double, std::milli>(measured - segmented).count()};
        return result;
    }
} // namespace mark_color
