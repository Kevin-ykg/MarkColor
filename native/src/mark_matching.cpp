// 几何配准与一对一分配。颜色只用于匹配后的比较，不参与对应关系求解。
#include "mark_color_internal.h"

#include <algorithm>
#include <cmath>
#include <limits>

// 对应内部头文件声明的模块命名空间；对外仍通过 mc_* C ABI 调用。
namespace mark_color
{
    // 匿名命名空间只在本编译单元可见，隐藏匹配器的候选、求解器和辅助常量。
    namespace
    {
        constexpr double kMissingAssignmentCost = 100;     // 缺点虚拟列的代价，高于合法配对 [0,1]。
        constexpr double kForbiddenAssignmentCost = 10000; // 禁止配对比缺失更差，避免强制认错圆点。
        constexpr double kMinimumRadiusRatio = .55;        // 待测半径 / 变换后标准半径，无量纲。
        constexpr double kMaximumRadiusRatio = 1.55;
        constexpr double kAmbiguityCostMargin = .03; // 同匹配数的不同对应关系，代价相近则标记不确定。
        constexpr double kConstraintEpsilon = 1e-8;  // 浮点边界余量，不是额外位置容差。

        enum ComparisonStatus : int32_t
        {
            Matched = 0,
            Missing = 1,
            Ambiguous = 2
        };

        struct SimilarityTransform
        {
            // 标准 → 待测：x'=scaledCosine*x-scaledSine*y+translateX，y'=scaledSine*x+scaledCosine*y+translateY。
            double scaledCosine = 1; // scale * cos(angle)，比例与旋转的实部。
            double scaledSine = 0;   // scale * sin(angle)，与实部一起保证无镜像。
            double translateX = 0;   // X 平移，单位为待测图原图 px。
            double translateY = 0;   // Y 平移，单位为待测图原图 px。

            cv::Point2d apply(const McMark& mark) const
            {
                return {scaledCosine * mark.x - scaledSine * mark.y + translateX,
                        scaledSine * mark.x + scaledCosine * mark.y + translateY};
            }
        };

        struct AlignmentFit
        {
            SimilarityTransform transform;
            std::vector<int> observedIndices; // 每个标准点对应的待测下标；-1 表示未分配。
            int matchedCount = 0;             // 排序第一优先级：有效一对一配对数量。
            double cost = 0;                  // 仅累计有效配对的归一化距离平方；不包含颜色差。
        };

        // 防止标准 JSON 或外部调用者传入非法几何/Lab；只验证匹配真正使用的字段。
        bool isValidMark(const McMark& mark)
        {
            return std::isfinite(mark.x) && std::isfinite(mark.y) && std::isfinite(mark.radius) && mark.radius > 0 &&
                   std::isfinite(mark.l) && std::isfinite(mark.a) && std::isfinite(mark.b) && mark.l >= 0 &&
                   mark.l <= 100 && std::abs(mark.a) <= 160 && std::abs(mark.b) <= 160;
        }

        // 约束用于排除不可能的拍摄变化；不得为提升速度而隐式收紧这些用户可设边界。
        void validateMatchSettings(const McMatchSettings& settings)
        {
            require(std::isfinite(settings.maxTranslation) && std::isfinite(settings.maxRotation) &&
                        std::isfinite(settings.maxScaleChange) && std::isfinite(settings.positionTolerance) &&
                        settings.maxTranslation >= 0 && settings.maxTranslation <= 1 && settings.maxRotation >= 0 &&
                        settings.maxRotation <= 30 && settings.maxScaleChange >= 0 && settings.maxScaleChange <= .4 &&
                        settings.positionTolerance >= .1 && settings.positionTolerance <= 2,
                    "Invalid matching settings.");
        }

        // 分辨率差先用 resolutionScale 补偿，再判断比例变化、角度和像素平移。
        // translateX/translateY 是标准→待测的变换平移，而非各点局部残差；大幅上下移动应由 maxTranslation 允许。
        bool isTransformAllowed(const SimilarityTransform& transform, double resolutionScale, int width, int height,
                                const McMatchSettings& settings)
        {
            const double relativeScale = std::hypot(transform.scaledCosine, transform.scaledSine) / resolutionScale;
            const double angleDegrees = std::atan2(transform.scaledSine, transform.scaledCosine) * 180 / kPi;
            return relativeScale >= 1 - settings.maxScaleChange - kConstraintEpsilon &&
                   relativeScale <= 1 + settings.maxScaleChange + kConstraintEpsilon &&
                   std::abs(angleDegrees) <= settings.maxRotation + kConstraintEpsilon &&
                   std::hypot(transform.translateX, transform.translateY) <=
                       settings.maxTranslation * std::hypot(width, height) + kConstraintEpsilon;
        }

        // Hungarian 最小代价分配：行是标准点，列是待测点和缺失占位列。
        // 内部使用从 1 开始的下标，列 0 为增广路径哨兵；返回每列所属行。
        std::vector<int> solveAssignment(const std::vector<std::vector<double>>& costs)
        {
            const int rowCount = static_cast<int>(costs.size());
            const int columnCount = static_cast<int>(costs.front().size());
            // 对偶势用于把边代价约化，逐步扩展零约化代价的可达路径。
            std::vector<double> rowPotential(rowCount + 1), columnPotential(columnCount + 1);
            std::vector<int> assignedRow(columnCount + 1);    // 列 -> 当前匹配行，0 表示该列尚未占用。
            std::vector<int> previousColumn(columnCount + 1); // 增广路径前驱，用于找到空列后回溯。

            for (int row = 1; row <= rowCount; ++row)
            {
                assignedRow[0] = row;
                int currentColumn = 0;
                std::vector<double> minimumCost(columnCount + 1, 1e20); // 当前搜索树到各列的最小约化代价。
                std::vector<bool> visited(columnCount + 1);             // 本轮增广中已加入搜索树的列。
                do
                {
                    visited[currentColumn] = true;
                    const int currentRow = assignedRow[currentColumn];
                    int nextColumn = 0;
                    double delta = 1e20; // 本轮调整对偶势的最小步长，使下一个候选列可达。
                    for (int column = 1; column <= columnCount; ++column)
                    {
                        if (visited[column])
                        {
                            continue;
                        }
                        const double reducedCost =
                            costs[currentRow - 1][column - 1] - rowPotential[currentRow] - columnPotential[column];
                        if (reducedCost < minimumCost[column])
                        {
                            minimumCost[column] = reducedCost;
                            previousColumn[column] = currentColumn;
                        }
                        if (minimumCost[column] < delta)
                        {
                            delta = minimumCost[column];
                            nextColumn = column;
                        }
                    }
                    for (int column = 0; column <= columnCount; ++column)
                    {
                        if (visited[column])
                        {
                            rowPotential[assignedRow[column]] += delta;
                            columnPotential[column] -= delta;
                        }
                        else
                        {
                            minimumCost[column] -= delta;
                        }
                    }
                    currentColumn = nextColumn;
                } while (assignedRow[currentColumn] != 0);

                // 找到空列后，沿增广路径反向更新对应关系。
                do
                {
                    const int nextColumn = previousColumn[currentColumn];
                    assignedRow[currentColumn] = assignedRow[nextColumn];
                    currentColumn = nextColumn;
                } while (currentColumn != 0);
            }
            return assignedRow;
        }

        // 给定一个变换假设，为每个标准点寻找唯一待测点。缺失占位列使“少点”仍有可行解。
        // 先按半径比和位置容差禁止不合理配对，再最小化归一化距离；绝不使用颜色代价。
        AlignmentFit assignMarks(const SimilarityTransform& transform, const McMark* reference, int referenceCount,
                                 const McMark* observed, int observedCount, double tolerance)
        {
            // 每个标准点都有独立缺失占位，因此待测点为零或少于标准点时仍能完成一对一分配。
            const int columnCount = observedCount + referenceCount;
            std::vector<std::vector<double>> costs(referenceCount,
                                                   std::vector<double>(columnCount, kMissingAssignmentCost));
            const double scale = std::hypot(transform.scaledCosine, transform.scaledSine);
            for (int referenceIndex = 0; referenceIndex < referenceCount; ++referenceIndex)
            {
                for (int observedIndex = 0; observedIndex < observedCount; ++observedIndex)
                {
                    const double radius = reference[referenceIndex].radius * scale;
                    const double radiusRatio = observed[observedIndex].radius / radius;
                    const double distance = cv::norm(transform.apply(reference[referenceIndex]) -
                                                     cv::Point2d(observed[observedIndex].x, observed[observedIndex].y));
                    const bool admissible = radiusRatio >= kMinimumRadiusRatio && radiusRatio <= kMaximumRadiusRatio &&
                                            distance <= radius * tolerance;
                    // 有效匹配代价在 [0,1]；缺失占位和禁止匹配使用更大的代价。
                    costs[referenceIndex][observedIndex] =
                        admissible ? distance * distance / (radius * radius * tolerance * tolerance)
                                   : kForbiddenAssignmentCost;
                }
            }

            const auto assignedRows = solveAssignment(costs);
            AlignmentFit fit;
            fit.transform = transform;
            fit.observedIndices.assign(referenceCount, -1);
            for (int column = 1; column <= observedCount; ++column)
            {
                const int row = assignedRows[column];
                if (row > 0 && costs[row - 1][column - 1] < 2)
                {
                    fit.observedIndices[row - 1] = column - 1;
                    ++fit.matchedCount;
                    fit.cost += costs[row - 1][column - 1];
                }
            }
            return fit;
        }

        // 已有对应关系后，用所有已匹配圆心求无镜像的最小二乘相似变换。
        // 只有一个匹配点时无法唯一确定旋转/比例，故保留原假设；不会凭颜色补足几何约束。
        SimilarityTransform refineTransform(const AlignmentFit& fit, const McMark* reference, const McMark* observed)
        {
            if (fit.matchedCount < 2)
            {
                return fit.transform;
            }

            cv::Point2d referenceCenter{}, observedCenter{}; // 已配对点各自在两幅原图中的质心，单位 px。
            for (size_t index = 0; index < fit.observedIndices.size(); ++index)
            {
                if (fit.observedIndices[index] < 0)
                {
                    continue;
                }
                referenceCenter += cv::Point2d(reference[index].x, reference[index].y);
                const auto& mark = observed[fit.observedIndices[index]];
                observedCenter += cv::Point2d(mark.x, mark.y);
            }
            referenceCenter *= 1.0 / fit.matchedCount;
            observedCenter *= 1.0 / fit.matchedCount;

            // 中心化后求最小二乘相似变换；分母近零时保留原假设。
            double denominator = 0;  // 标准点中心化后的平方距离和；近零代表几何退化。
            double scaledCosine = 0; // 点积和 / denominator，得到 scale*cos(angle)。
            double scaledSine = 0;   // 二维叉积和 / denominator，得到 scale*sin(angle)。
            for (size_t index = 0; index < fit.observedIndices.size(); ++index)
            {
                if (fit.observedIndices[index] < 0)
                {
                    continue;
                }
                const auto referenceOffset = cv::Point2d(reference[index].x, reference[index].y) - referenceCenter;
                const auto& mark = observed[fit.observedIndices[index]];
                const auto observedOffset = cv::Point2d(mark.x, mark.y) - observedCenter;
                denominator += referenceOffset.dot(referenceOffset);
                scaledCosine += referenceOffset.dot(observedOffset);
                scaledSine += referenceOffset.x * observedOffset.y - referenceOffset.y * observedOffset.x;
            }
            if (denominator < kConstraintEpsilon)
            {
                return fit.transform;
            }
            scaledCosine /= denominator;
            scaledSine /= denominator;
            return {scaledCosine, scaledSine,
                    observedCenter.x - scaledCosine * referenceCenter.x + scaledSine * referenceCenter.y,
                    observedCenter.y - scaledSine * referenceCenter.x - scaledCosine * referenceCenter.y};
        }

        // 枚举分辨率、单点平移和双点相似变换；对合法候选分配，再细化一次并重新分配。
        // 保存多个解释供歧义检查使用：不能找到首个可用解就退出，否则等间距缺点可能被误判。
        std::vector<AlignmentFit> generateAlignmentFits(const McMark* reference, int referenceCount,
                                                        const McMark* observed, int observedCount, int width,
                                                        int height, double resolutionScale,
                                                        const McMatchSettings& settings)
        {
            std::vector<AlignmentFit> fits;
            const auto consider = [&](const SimilarityTransform& transform)
            {
                if (!isTransformAllowed(transform, resolutionScale, width, height, settings))
                {
                    return;
                }
                auto fit = assignMarks(transform, reference, referenceCount, observed, observedCount,
                                       settings.positionTolerance);
                if (fit.matchedCount >= 2)
                {
                    const auto refined = refineTransform(fit, reference, observed);
                    if (isTransformAllowed(refined, resolutionScale, width, height, settings))
                    {
                        fit = assignMarks(refined, reference, referenceCount, observed, observedCount,
                                          settings.positionTolerance);
                    }
                }
                fits.push_back(std::move(fit));
            };

            // 先考虑只有分辨率变化，再枚举单点平移、双点旋转/比例假设。
            consider({resolutionScale, 0, 0, 0});
            for (int referenceIndex = 0; referenceIndex < referenceCount; ++referenceIndex)
            {
                for (int observedIndex = 0; observedIndex < observedCount; ++observedIndex)
                {
                    consider({resolutionScale, 0,
                              observed[observedIndex].x - resolutionScale * reference[referenceIndex].x,
                              observed[observedIndex].y - resolutionScale * reference[referenceIndex].y});
                }
            }
            for (int first = 0; first < referenceCount; ++first)
            {
                for (int second = first + 1; second < referenceCount; ++second)
                {
                    const double referenceDeltaX = reference[second].x - reference[first].x;
                    const double referenceDeltaY = reference[second].y - reference[first].y;
                    const double denominator = referenceDeltaX * referenceDeltaX +
                                               referenceDeltaY * referenceDeltaY; // 标准点对的间距平方，px²。
                    if (denominator < 4 * reference[first].radius * reference[first].radius)
                    {
                        continue;
                    }
                    for (int firstObserved = 0; firstObserved < observedCount; ++firstObserved)
                    {
                        for (int secondObserved = 0; secondObserved < observedCount; ++secondObserved)
                        {
                            if (firstObserved == secondObserved)
                            {
                                continue;
                            }
                            const double observedDeltaX = observed[secondObserved].x - observed[firstObserved].x;
                            const double observedDeltaY = observed[secondObserved].y - observed[firstObserved].y;
                            const double scaledCosine =
                                (referenceDeltaX * observedDeltaX + referenceDeltaY * observedDeltaY) / denominator;
                            const double scaledSine =
                                (referenceDeltaX * observedDeltaY - referenceDeltaY * observedDeltaX) / denominator;
                            consider({scaledCosine, scaledSine,
                                      observed[firstObserved].x - scaledCosine * reference[first].x +
                                          scaledSine * reference[first].y,
                                      observed[firstObserved].y - scaledSine * reference[first].x -
                                          scaledCosine * reference[first].y});
                        }
                    }
                }
            }
            return fits;
        }

        // fits 已按匹配数降序、代价升序排列。不同对应关系且代价相近，说明几何信息不足。
        // 只关注“对应关系不同”，同一组配对由不同双点假设导出不构成歧义。
        bool hasAmbiguousCorrespondence(const std::vector<AlignmentFit>& fits, int referenceCount)
        {
            const auto& best = fits.front();
            if (referenceCount > 1 && best.matchedCount < 2)
            {
                return true; // 一个点不足以建立多点版式的几何配准。
            }
            return std::any_of(fits.begin(), fits.end(),
                               [&](const AlignmentFit& fit)
                               {
                                   return fit.matchedCount == best.matchedCount &&
                                          fit.observedIndices != best.observedIndices &&
                                          fit.cost <= best.cost + kAmbiguityCostMargin;
                               });
        }

        // 将最优假设转为固定 ABI 输出。没有观测点时色差写 NaN，避免 0 被误当成颜色完全一致。
        // expectedX/Y/r 是待测图中的预期位置，用于确认缺失时画虚线；歧义时界面不得画缺失。
        void writeComparisons(const AlignmentFit& fit, bool ambiguous, const McMark* reference, int referenceCount,
                              const McMark* observed, int observedCount, McComparison* comparisons,
                              McAlignment& alignment, const McColorDifferenceSettings& colorSettings)
        {
            alignment = {};
            alignment.scale = std::hypot(fit.transform.scaledCosine, fit.transform.scaledSine);
            alignment.angleDegrees = std::atan2(fit.transform.scaledSine, fit.transform.scaledCosine) * 180 / kPi;
            alignment.translateX = fit.transform.translateX;
            alignment.translateY = fit.transform.translateY;
            alignment.matched = fit.matchedCount;
            alignment.extra = observedCount - fit.matchedCount;
            alignment.ambiguous = ambiguous ? 1 : 0;

            double squaredPositionError = 0; // 待测图位置残差平方和，最终 RMS 单位为 px。
            for (int index = 0; index < referenceCount; ++index)
            {
                auto& comparison = comparisons[index];
                comparison = {};
                comparison.observedIndex = fit.observedIndices[index];
                comparison.status = ambiguous ? Ambiguous : (comparison.observedIndex < 0 ? Missing : Matched);
                const auto expected = fit.transform.apply(reference[index]);
                comparison.expectedX = expected.x;
                comparison.expectedY = expected.y;
                comparison.expectedRadius = reference[index].radius * alignment.scale;
                comparison.deltaE00 = comparison.deltaE76 = comparison.deltaL = comparison.deltaA = comparison.deltaB =
                    comparison.positionError = std::numeric_limits<double>::quiet_NaN();

                if (comparison.observedIndex < 0)
                {
                    continue;
                }
                const auto& mark = observed[comparison.observedIndex];
                comparison.deltaL = mark.l - reference[index].l;
                comparison.deltaA = mark.a - reference[index].a;
                comparison.deltaB = mark.b - reference[index].b;
                comparison.deltaE76 =
                    std::sqrt(comparison.deltaL * comparison.deltaL + comparison.deltaA * comparison.deltaA +
                              comparison.deltaB * comparison.deltaB);
                comparison.deltaE00 = calculateDeltaE00(reference[index].l, reference[index].a, reference[index].b,
                                                        mark.l, mark.a, mark.b, colorSettings);
                comparison.positionError = cv::norm(expected - cv::Point2d(mark.x, mark.y));
                squaredPositionError += comparison.positionError * comparison.positionError;
            }
            alignment.rms = fit.matchedCount ? std::sqrt(squaredPositionError / fit.matchedCount) : 0;
        }
    } // namespace

    // 匹配主流程：验证输入→生成/细化候选→排序→检测歧义→计算色差与位置残差。
    // 匹配数优先于距离；颜色正常与否由 C# 对标准逐点阈值判定，本层不包含业务阈值。
    void compareMarks(const McMark* reference, int referenceCount, int referenceWidth, int referenceHeight,
                      const McMark* observed, int observedCount, int observedWidth, int observedHeight,
                      const McMatchSettings& settings, McComparison* comparisons, McAlignment& alignment,
                      const McColorDifferenceSettings& colorSettings)
    {
        require(referenceWidth > 0 && referenceHeight > 0 && observedWidth > 0 && observedHeight > 0,
                "Invalid image dimensions.");
        validateMatchSettings(settings);
        validateColorSettings(colorSettings);
        for (int index = 0; index < referenceCount; ++index)
        {
            require(isValidMark(reference[index]), "Invalid reference mark.");
        }
        for (int index = 0; index < observedCount; ++index)
        {
            require(isValidMark(observed[index]), "Invalid observed mark.");
        }

        // 仅补偿同视野的等比例分辨率变化；更换视野或镜头后不应仅凭宽高比例沿用颜色标准。
        const double resolutionScale = static_cast<double>(observedWidth) / referenceWidth;
        require(std::abs(resolutionScale - static_cast<double>(observedHeight) / referenceHeight) <
                    .02 * resolutionScale,
                "Image aspect ratios differ: use a reference with the same field of view.");
        auto fits = generateAlignmentFits(reference, referenceCount, observed, observedCount, observedWidth,
                                          observedHeight, resolutionScale, settings);
        require(!fits.empty(), "No valid alignment hypothesis.");
        std::sort(fits.begin(), fits.end(),
                  [](const AlignmentFit& left, const AlignmentFit& right)
                  {
                      return left.matchedCount != right.matchedCount ? left.matchedCount > right.matchedCount
                                                                     : left.cost < right.cost;
                  });
        const bool ambiguous = hasAmbiguousCorrespondence(fits, referenceCount);
        writeComparisons(fits.front(), ambiguous, reference, referenceCount, observed, observedCount, comparisons,
                         alignment, colorSettings);
    }
} // namespace mark_color
