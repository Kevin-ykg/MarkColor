#pragma once

// 独立于真实图库的成像扰动：断言几何误差和质量证据，而非仅断言“有输出”。
inline void qualityTests(const std::filesystem::path& directory, const McSettings& settings)
{
    check(mc_abi_version() == 3 && sizeof(McQuality) == 80, "Quality ABI version/layout");
    const auto file = directory / L"quality-fixture.bmp";
    auto analyze = [&](const cv::Mat& image)
    {
        std::vector<uchar> encoded;
        cv::imencode(".bmp", image, encoded);
        std::ofstream output(file, std::ios::binary);
        output.write(reinterpret_cast<const char*>(encoded.data()), encoded.size());
        output.close();
        void* handle = nullptr;
        check(mc_analyze(file.c_str(), &settings, &handle) == 0, mc_last_error());
        int width, height, count;
        mc_result_info(handle, &width, &height, &count);
        check(count == 1, "Quality fixture must have one real mark");
        McMark mark{};
        McQuality quality{};
        check(mc_get_mark(handle, 0, &mark) == 0 && mc_get_quality(handle, 0, &quality) == 0, "Quality read");
        check(mc_get_quality(handle, -1, &quality) < 0 && mc_get_quality(handle, 0, nullptr) < 0,
              "Quality invalid output/index");
        mc_release(handle);
        return std::make_pair(mark, quality);
    };
    // 亮/暗两个极性用相同已知圆心和半径，验证弱对比精修没有只支持单一方向。
    for (int sign : {-1, 1})
    {
        cv::Mat weak(400, 400, CV_8UC3, cv::Scalar(170, 170, 170));
        cv::circle(weak, {200, 200}, 60, cv::Scalar::all(170 + sign * 10), cv::FILLED);
        auto measured = analyze(weak);
        near(measured.first.x, 200, 1.0, "Weak contrast refined center X");
        near(measured.first.y, 200, 1.0, "Weak contrast refined center Y");
        near(measured.first.radius, 60, 2.0, "Weak contrast full-resolution radius");
        check(measured.second.refined == 1 && measured.second.contourConfidence > .7,
              "Weak bright/dark full-resolution evidence");
    }
    cv::Mat normal(400, 400, CV_8UC3, cv::Scalar::all(170));
    cv::circle(normal, {200, 200}, 60, {100, 50, 160}, cv::FILLED);
    auto sharp = analyze(normal);
    cv::Mat blurred;
    cv::GaussianBlur(normal, blurred, {}, 14);
    auto soft = analyze(blurred);
    check(soft.second.edgeWidth > sharp.second.edgeWidth + 15, "Blur must widen measured transition");
    cv::Mat glare = normal.clone();
    cv::circle(glare, {200, 200}, 18, cv::Scalar::all(240), cv::FILLED);
    check(analyze(glare).second.glareFraction > .04, "Local neutral highlight coverage");
    cv::Mat clipped(400, 400, CV_8UC3, cv::Scalar::all(170));
    cv::circle(clipped, {200, 200}, 60, cv::Scalar::all(255), cv::FILLED);
    check(analyze(clipped).second.overexposedFraction > .95, "Overexposure evidence");
    cv::Mat dark(400, 400, CV_8UC3, cv::Scalar::all(20));
    cv::circle(dark, {200, 200}, 60, cv::Scalar::all(0), cv::FILLED);
    auto under = analyze(dark);
    check(under.second.underexposedFraction > .95 && under.first.backgroundL < 15, "Severe underexposure evidence");
}
