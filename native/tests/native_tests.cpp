// 原生回归入口：公式参考值、几何匹配和图像合成场景；新增算法分支应补对应断言。
#include "mark_color.h"
#include <opencv2/opencv.hpp>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <vector>

void check(bool ok, const char* message)
{
    if (!ok)
    {
        throw std::runtime_error(message);
    }
}

// 数值断言使用明确绝对误差；CIEDE2000 参考表容差与几何像素容差分别由调用处给出。
void near(double a, double b, double tolerance, const char* message)
{
    check(std::abs(a - b) <= tolerance, message);
}

McMark mark(double x, double y, double l, double a, double b)
{
    McMark m{};
    m.x = x;
    m.y = y;
    m.radius = 40;
    m.l = l;
    m.a = a;
    m.b = b;
    return m;
}

#include "quality_tests.h"
#include "scale_tests.h"

int main()
{
    try
    {
        static_assert(sizeof(McMark) == 128);
        static_assert(sizeof(McSettings) == 96);
        static_assert(sizeof(McComparison) == 80);
        static_assert(sizeof(McAlignment) == 56);
        static_assert(sizeof(McTimings) == 24);
        // Published Sharma/Wu/Dalal reference cases, including hue discontinuities.
        const double data[][7] = {{50, 2.6772, -79.7751, 50, 0, -82.7485, 2.0425},
                                  {50, 3.1571, -77.2803, 50, 0, -82.7485, 2.8615},
                                  {50, 2.8361, -74.0200, 50, 0, -82.7485, 3.4412},
                                  {50, -1.3802, -84.2814, 50, 0, -82.7485, 1.0000},
                                  {50, -1.1848, -84.8006, 50, 0, -82.7485, 1.0000},
                                  {50, -.9009, -85.5211, 50, 0, -82.7485, 1.0000},
                                  {50, 0, 0, 50, -1, 2, 2.3669},
                                  {50, 2.49, -.001, 50, -2.49, .001, 7.1792},
                                  {50, 2.49, -.001, 50, -2.49, .0011, 7.2195},
                                  {50, -.001, 2.49, 50, .0011, -2.49, 4.7461},
                                  {50, 0, 0, 50, 0, 0, 0},
                                  {0, 0, 0, 100, 0, 0, 100}};
        for (auto& c : data)
        {
            near(mc_delta_e00(c[0], c[1], c[2], c[3], c[4], c[5]), c[6], .0001, "CIEDE2000 reference mismatch");
            near(mc_delta_e00(c[3], c[4], c[5], c[0], c[1], c[2]), c[6], .0001, "CIEDE2000 symmetry mismatch");
            const McColorDifferenceSettings defaults{};
            near(mc_delta_e00_weighted(c[0], c[1], c[2], c[3], c[4], c[5], &defaults),
                 mc_delta_e00(c[0], c[1], c[2], c[3], c[4], c[5]), 1e-12, "Default factors preserve legacy API");
            // 全部因子同时翻倍时，总色差减半；包含蓝区交互项，防止 RT 漏用 kC/kH。
            const McColorDifferenceSettings doubled{2, 2, 2};
            near(mc_delta_e00_weighted(c[0], c[1], c[2], c[3], c[4], c[5], &doubled),
                 mc_delta_e00(c[0], c[1], c[2], c[3], c[4], c[5]) / 2, 1e-12, "Uniform factor scaling including RT");
        }
        const McColorDifferenceSettings lightness{2, 1, 1}, chroma{1, 2, 1}, hue{1, 1, 2};
        near(mc_delta_e00_weighted(40, 20, 20, 45, 20, 20, &lightness), mc_delta_e00(40, 20, 20, 45, 20, 20) / 2, 1e-12,
             "Pure lightness factor");
        near(mc_delta_e00_weighted(50, 0, 20, 50, 0, 30, &chroma), mc_delta_e00(50, 0, 20, 50, 0, 30) / 2, 1e-12,
             "Pure chroma factor");
        near(mc_delta_e00_weighted(50, 20, 20, 50, -20, 20, &hue), mc_delta_e00(50, 20, 20, 50, -20, 20) / 2, 1e-12,
             "Pure hue factor");
        for (double invalid :
             {0.0, -1.0, .09, 10.01, std::numeric_limits<double>::infinity(), std::numeric_limits<double>::quiet_NaN()})
        {
            const McColorDifferenceSettings badColor{invalid, 1, 1};
            check(std::isnan(mc_delta_e00_weighted(40, 0, 0, 45, 0, 0, &badColor)), "Invalid factor must return NaN");
        }
        check(std::isnan(mc_delta_e00_weighted(40, 0, 0, 45, 0, 0, nullptr)), "Null factor settings");
        McSettings s{25, 160, .7, .7, 40, 110, 3, .65, 0, 0, 1, 1};
        auto dir = std::filesystem::temp_directory_path() / L"mark-color-native-测试";
        std::filesystem::create_directories(dir);
        qualityTests(dir, s);
        scaleTests(dir);
        cv::Mat im(700, 1100, CV_8UC3, cv::Scalar(170, 170, 170));
        cv::line(im, {0, 100}, {1099, 100}, {40, 40, 40}, 5); // reject dark line
        cv::circle(im, {150, 350}, 60, {120, 20, 200}, -1);
        cv::circle(im, {450, 350}, 60, {45, 45, 45}, -1);
        cv::circle(im, {750, 350}, 60, {184, 184, 184}, -1);
        cv::circle(im, {0, 550}, 50, {200, 0, 0}, -1); // reject clipped mark
        auto path = dir / L"彩色白色黑色.bmp";
        std::vector<uchar> bytes;
        cv::imencode(".bmp", im, bytes);
        std::ofstream stream(path, std::ios::binary);
        stream.write(reinterpret_cast<const char*>(bytes.data()), bytes.size());
        stream.close();
        void* handle = nullptr;
        check(mc_analyze(path.c_str(), &s, &handle) == 0, mc_last_error());
        int width = 0, height = 0, count = 0;
        check(mc_result_info(handle, &width, &height, &count) == 0, "Result metadata");
        McTimings timings{};
        check(mc_result_timings(handle, &timings) == 0 && timings.decodeMs >= 0 && timings.segmentationMs >= 0 &&
                  timings.measurementMs >= 0,
              "Per-result stage timings");
        check(mc_result_timings(nullptr, &timings) < 0 && mc_result_timings(handle, nullptr) < 0,
              "Timing null buffers");
        check(count == 3, "Color + black + pale circles / line and clipping rejection");
        const uint8_t* pixels = nullptr;
        int stride = 0, length = 0;
        check(mc_result_bgr(handle, &pixels, &stride, &length) == 0 && pixels && stride >= width * 3 &&
                  length == stride * height,
              "Borrowed BGR buffer layout");
        check(pixels[350 * stride + 150 * 3] == 120 && pixels[350 * stride + 150 * 3 + 2] == 200,
              "BGR preview matches decoded image");
        check(mc_result_bgr(nullptr, &pixels, &stride, &length) < 0 &&
                  mc_result_bgr(handle, nullptr, &stride, &length) < 0,
              "Preview null buffers");
        for (int i = 0; i < count; ++i)
        {
            McMark m{};
            check(mc_get_mark(handle, i, &m) == 0, "Get mark");
            check(m.sampleCount > 1000 && m.contourCount > 8, "Core sample/contour");
            std::vector<McPoint> contour(m.contourCount);
            check(mc_get_contour(handle, i, contour.data(), m.contourCount) == m.contourCount, "Contour copy");
        }
        mc_release(handle);
        mc_release(nullptr);
        handle = reinterpret_cast<void*>(1);
        check(mc_analyze(L"missing-file.bmp", &s, &handle) != 0 && handle == nullptr, "Error boundary / null handle");
        McSettings bad = s;
        bad.innerRatio = 0;
        check(mc_analyze(path.c_str(), &bad, &handle) != 0, "Validate settings");
        std::vector<McMark> reference = {mark(150, 350, 50, 60, -10), mark(450, 350, 40, -10, -40),
                                         mark(750, 350, 65, -50, 40)};
        auto observed = reference;
        double angle = 3 * 3.141592653589793 / 180;
        for (auto& m : observed)
        {
            double x = m.x, y = m.y;
            m.x = 1.03 * (std::cos(angle) * x - std::sin(angle) * y) + 20;
            m.y = 1.03 * (std::sin(angle) * x + std::cos(angle) * y) - 15;
            m.radius *= 1.03;
        }
        McMatchSettings ms{.3, 10, .15, .7};
        std::vector<McComparison> output(3);
        McAlignment alignment{};
        auto compare = [&]()
        {
            check(mc_compare(reference.data(), 3, 1100, 700, observed.data(), static_cast<int>(observed.size()), 1100,
                             700, &ms, output.data(), &alignment) == 0,
                  mc_last_error());
        };
        compare();
        check(alignment.matched == 3 && !alignment.ambiguous, "Similarity alignment");
        near(alignment.angleDegrees, 3, 1e-6, "Angle recovery");
        near(output[0].deltaE00, 0, 1e-9, "Identity Lab");
        observed[0].a -= 30;
        compare();
        check(output[0].observedIndex == 0 && output[0].deltaE00 > 5, "Color-independent correspondence");
        observed.erase(observed.begin() + 1);
        compare();
        check(output[1].status == 1 && output[1].observedIndex == -1, "Missing mark is not shifted onto its neighbor");
        observed.clear();
        compare();
        check(alignment.matched == 0 && alignment.ambiguous, "Blank image cannot pass");
        observed = {mark(100, 100, 50, 0, 0), mark(200, 100, 50, 0, 0)};
        McComparison single{};
        auto ref = mark(150, 100, 50, 0, 0);
        check(mc_compare(&ref, 1, 1100, 700, observed.data(), 2, 1100, 700, &ms, &single, &alignment) == 0 &&
                  alignment.ambiguous,
              "Ambiguous single-mark match fails closed");
        // Large capture-position changes must not be mistaken for missing marks.
        auto shifted = reference;
        for (auto& m : shifted)
        {
            m.y += 1200;
        }
        auto largeCompare = [&](const std::vector<McMark>& a, const std::vector<McMark>& b)
        {
            check(mc_compare(a.data(), 3, 1100, 2000, b.data(), static_cast<int>(b.size()), 1100, 2000, &ms,
                             output.data(), &alignment) == 0,
                  mc_last_error());
        };
        largeCompare(reference, shifted);
        check(alignment.matched == 0 && alignment.ambiguous, "Explicit tight translation limit is respected");
        ms.maxTranslation = 1;
        largeCompare(reference, shifted);
        check(alignment.matched == 3 && !alignment.ambiguous, "Full-field downward translation");
        near(alignment.translateY, 1200, 1e-6, "Downward translation recovery");
        largeCompare(shifted, reference);
        check(alignment.matched == 3 && !alignment.ambiguous, "Full-field upward translation");
        near(alignment.translateY, -1200, 1e-6, "Upward translation recovery");
        shifted[0].a -= 30;
        largeCompare(reference, shifted);
        check(output[0].observedIndex == 0 && output[0].deltaE00 > 5, "Large-shift color defect must remain NG");
        std::swap(shifted[0], shifted[2]);
        largeCompare(reference, shifted);
        check(output[0].observedIndex == 2 && output[2].observedIndex == 0,
              "Large-shift assignment is independent of detection order");
        shifted.erase(shifted.begin() + 1);
        largeCompare(reference, shifted);
        check(!alignment.ambiguous && alignment.matched == 2 && output[1].status == 1,
              "Large-shift genuine missing middle mark");
        shifted = {reference[0], reference[1]};
        for (auto& m : shifted)
        {
            m.y += 1200;
        }
        largeCompare(reference, shifted);
        check(alignment.ambiguous, "Repeated-spacing partial pattern remains ambiguous even when colors differ");
        shifted.clear();
        largeCompare(reference, shifted);
        check(alignment.matched == 0 && alignment.ambiguous, "Full-field blank image cannot pass");
        std::cout << "PASS: ABI, CIEDE2000, Unicode image, segmentation, contour, core sampling, alignment, full-field "
                     "bidirectional shifts, color-independent/reordered/missing/ambiguous marks, invalid inputs.\n";
        return 0;
    }
    catch (const std::exception& e)
    {
        std::cerr << "FAIL: " << e.what() << '\n';
        return 1;
    }
}
