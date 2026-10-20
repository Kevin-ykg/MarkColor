// CIEDE2000，默认 kL=kC=kH=1，可由标准配置调整。内部角度均使用弧度。
// 公式符号保留 C、h、G 等通用含义，分步骤说明色度校正、色相分支和权重。
#include "mark_color_internal.h"

#include <algorithm>
#include <cmath>

namespace mark_color
{
    namespace
    {
        // 将 a*/b* 平面的色相统一到 [0,2π)，无色度点固定返回 0，避免 atan2(0,0) 的语义差异。
        double hueRadians(double a, double b)
        {
            if (a == 0 && b == 0)
            {
                return 0;
            }
            const double angle = std::atan2(b, a);
            return angle < 0 ? angle + 2 * kPi : angle;
        }
    } // namespace

    // 接口边界统一验证，避免每个 mark 的数学内核重复检查同一参数。
    void validateColorSettings(const McColorDifferenceSettings& settings)
    {
        for (double factor : {settings.kL, settings.kC, settings.kH})
        {
            require(std::isfinite(factor) && factor >= .1 && factor <= 10,
                    "CIEDE2000 kL/kC/kH must be finite and within [0.1, 10].");
        }
    }

    // 输入/输出为浮点 CIELAB 单位。色相跨零、无色度和蓝色交互项须通过参考值回归。
    double calculateDeltaE00(double l1, double a1, double b1, double l2, double a2, double b2,
                             const McColorDifferenceSettings& settings)
    {
        // 1. 由平均色度计算 G，校正 a*，得到 C' 和 h'。
        // Lab 差异为输入测量值，kL/kC/kH 为无量纲敏感度因子；本函数不决定业务合格阈值。
        const double c1 = std::hypot(a1, b1);
        const double c2 = std::hypot(a2, b2);
        const double meanC = (c1 + c2) / 2;
        const double meanCPower7 = std::pow(meanC, 7);
        const double g = .5 * (1 - std::sqrt(meanCPower7 / (meanCPower7 + std::pow(25., 7))));
        const double adjustedA1 = (1 + g) * a1;
        const double adjustedA2 = (1 + g) * a2;
        const double adjustedC1 = std::hypot(adjustedA1, b1);
        const double adjustedC2 = std::hypot(adjustedA2, b2);
        const double hue1 = hueRadians(adjustedA1, b1);
        const double hue2 = hueRadians(adjustedA2, b2);

        // 2. 色相差使用最短角距离；任一色度为零时色相差定义为零。
        double deltaHue = hue2 - hue1;
        if (adjustedC1 * adjustedC2 == 0)
        {
            deltaHue = 0;
        }
        else if (deltaHue > kPi)
        {
            deltaHue -= 2 * kPi;
        }
        else if (deltaHue < -kPi)
        {
            deltaHue += 2 * kPi;
        }
        const double deltaL = l2 - l1;
        const double deltaC = adjustedC2 - adjustedC1;
        const double deltaH = 2 * std::sqrt(adjustedC1 * adjustedC2) * std::sin(deltaHue / 2);
        const double meanL = (l1 + l2) / 2;
        const double meanAdjustedC = (adjustedC1 + adjustedC2) / 2;

        // 3. 平均色相跨越 0/2π 时也要折返，不能直接使用算术均值。
        double meanHue;
        if (adjustedC1 * adjustedC2 == 0)
        {
            meanHue = hue1 + hue2;
        }
        else if (std::abs(hue1 - hue2) <= kPi)
        {
            meanHue = (hue1 + hue2) / 2;
        }
        else
        {
            meanHue = (hue1 + hue2 + (hue1 + hue2 < 2 * kPi ? 2 * kPi : -2 * kPi)) / 2;
        }

        // 4. SL/SC/SH 是随当前颜色变化的明度/彩度/色相尺度，不是 L/a/b 的固定占比。
        // 标准通式分别除以 kL*SL、kC*SC、kH*SH；所有因子在公开入口验证。
        // 增大 kL 会减小明度项，但也会降低对真实缺墨变亮的敏感度，不能代替光照校正。
        // RT 是彩度和色相的交互修正；不能随意改成三个 Lab 通道独立加权的欧氏距离。
        const double t = 1 - .17 * std::cos(meanHue - kPi / 6) + .24 * std::cos(2 * meanHue) +
                         .32 * std::cos(3 * meanHue + kPi / 30) - .20 * std::cos(4 * meanHue - 63 * kPi / 180);
        const double sl = 1 + .015 * (meanL - 50) * (meanL - 50) / std::sqrt(20 + (meanL - 50) * (meanL - 50));
        const double sc = 1 + .045 * meanAdjustedC;
        const double sh = 1 + .015 * meanAdjustedC * t;
        const double rc = 2 * std::sqrt(std::pow(meanAdjustedC, 7) / (std::pow(meanAdjustedC, 7) + std::pow(25., 7)));
        const double rt = -rc * std::sin(kPi / 3 * std::exp(-std::pow((meanHue * 180 / kPi - 275) / 25, 2)));
        // RT 同时作用于已经按 kC/kH 缩放的两项，不能遗漏交互项的参数因子。
        const double normalizedL = deltaL / (settings.kL * sl);
        const double normalizedC = deltaC / (settings.kC * sc);
        const double normalizedH = deltaH / (settings.kH * sh);
        return std::sqrt(std::max(0.0, std::pow(normalizedL, 2) + std::pow(normalizedC, 2) + std::pow(normalizedH, 2) +
                                           rt * normalizedC * normalizedH));
    }
} // namespace mark_color
