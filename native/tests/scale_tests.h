#pragma once

// 目标半径和图像尺寸独立变化；用深蓝基材复现旧全局掩膜把背景与圆点合并的问题。
inline void scaleTests(const std::filesystem::path& directory)
{
    const auto file = directory / L"camera-scale.bmp";
    McSettings settings{8, 500, .7, .8, 40, 110, 3, .65, 0, 0, 1, 1};
    auto analyze = [&](const cv::Mat& image, const McSettings& config)
    {
        std::vector<uchar> bytes;
        cv::imencode(".bmp", image, bytes);
        std::ofstream output(file, std::ios::binary);
        output.write(reinterpret_cast<const char*>(bytes.data()), bytes.size());
        output.close();
        void* handle = nullptr;
        check(mc_analyze(file.c_str(), &config, &handle) == 0, mc_last_error());
        int width, height, count;
        mc_result_info(handle, &width, &height, &count);
        std::vector<McMark> marks(count);
        for (int i = 0; i < count; ++i)
        {
            mc_get_mark(handle, i, &marks[i]);
        }
        mc_release(handle);
        return marks;
    };
    for (int radius : {8, 12, 24, 50, 90, 180, 350, 500})
    {
        for (auto background : {cv::Scalar(170, 170, 170), cv::Scalar(100, 65, 55)})
        {
            int side = std::max(320, radius * 5);
            cv::Mat image(side, side, CV_8UC3, background);
            cv::Point center(side / 2, side / 2);
            cv::circle(image, center, radius, {80, 40, 150}, cv::FILLED);
            auto marks = analyze(image, settings);
            check(marks.size() == 1, "Scale fixture must locate exactly one mark");
            near(marks[0].x, center.x, 1.5, "Scale center X");
            near(marks[0].y, center.y, 1.5, "Scale center Y");
            near(marks[0].radius, radius, 2, "Scale radius in original pixels");
            check(marks[0].sampleCount >= 64, "Smallest default mark still has meaningful sampling area");
            auto roi = settings;
            roi.roiX = roi.roiY = .1;
            roi.roiWidth = roi.roiHeight = .8;
            const auto cropped = analyze(image, roi);
            check(cropped.size() == 1, "Changing ROI must not change scale eligibility");
            near(cropped[0].x, marks[0].x, 1, "ROI global coordinate invariant");
            near(cropped[0].radius, marks[0].radius, 1, "ROI radius invariant");
            near(cropped[0].l, marks[0].l, .01, "ROI retains original Lab sampling");
        }
    }
    // 同帧混合大小，不能通过只保留最大/最多的一组半径来假装兼容。
    cv::Mat mixed(700, 1200, CV_8UC3, cv::Scalar(100, 65, 55));
    for (const auto& circle : std::vector<cv::Vec3i>{{80, 350, 8}, {180, 350, 20}, {420, 350, 80}, {850, 350, 160}})
    {
        cv::circle(mixed, {circle[0], circle[1]}, circle[2], {80, 40, 150}, cv::FILLED);
    }
    check(analyze(mixed, settings).size() == 4, "Mixed sizes in one frame");
    settings.minRadius = 30;
    check(analyze(mixed, settings).size() == 2, "Explicit minimum radius remains effective");
    cv::Mat many(600, 1200, CV_8UC3, cv::Scalar::all(180));
    for (int i = 0; i < 20; ++i)
    {
        cv::circle(many, {60 + i % 10 * 110, 180 + i / 10 * 220}, 35, {80, 40, 150}, cv::FILLED);
    }
    check(analyze(many, settings).size() == 20, "Large field stays available for ROI preview beyond matching limit");
}
