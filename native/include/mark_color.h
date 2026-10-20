#pragma once
#include <cstdint>
// DLL 工程定义 MC_EXPORTS 导出符号；C++ 调用方不定义它而导入同名符号。
// extern "C" 固定导出名，便于 C# P/Invoke 查找；不表示内部必须用 C 编写。
#ifdef MC_EXPORTS
#define MC_API extern "C" __declspec(dllexport)
#else
#define MC_API extern "C" __declspec(dllimport)
#endif

// ABI v3：新增可配置色差接口，保留 v2 结构体及旧接口的默认行为。
// Windows x64，cdecl，8 字节对齐；输入/输出缓冲由调用方提供。
// 坐标采用原图像素，左上为原点，x 向右、y 向下；输入 BGR8，测色假设为 sRGB。
// STL/OpenCV 对象和 C++ 异常不跨 DLL；分析句柄持有内部轮廓，由 mc_release 释放。
#pragma pack(push, 8)

struct McSettings
{
    // 半径为 px，圆度/填充率为 (0,1]，分割阈值采用 8 位灰度/饱和度单位。
    double minRadius, maxRadius, minCircularity, minFill;
    double saturationMin, darkMax, brightContrast, innerRatio;
    double roiX, roiY, roiWidth, roiHeight; // 相对于整图归一化 [0,1]
};

struct McMark
{
    // 圆心/半径：px，面积：px²；circularity=4πA/P²。
    double x, y, radius, area, circularity;
    // 浮点 Lab：L*=0..100，a*/b* 带符号；std 为所有内圈样本的标准差。
    double l, a, b, stdL, stdA, stdB;
    // BGR 是原图内圈均值 0..255；背景环 L* 仅用于诊断光照。
    double blue, green, red, backgroundL;
    int32_t sampleCount, contourCount;
};

struct McPoint
{
    double x, y;
};

struct McMatchSettings
{
    double maxTranslation;    // 分辨率换算后，平移长度 / 待测图对角线
    double maxRotation;       // 最大旋转角（度），不允许镜像
    double maxScaleChange;    // 分辨率换算后的相对比例偏差
    double positionTolerance; // 位置容差 / 标准半径
};

struct McComparison
{
    int32_t observedIndex, status; // 待测数组下标/-1；状态 0=匹配，1=缺失，2=配准不确定
    double deltaE00, deltaE76, deltaL, deltaA, deltaB, positionError;
    double expectedX, expectedY, expectedRadius;
};

// 色差参数与几何约束分开：参数因子越大，对应分量越不敏感，不是百分比。
struct McColorDifferenceSettings
{
    double kL = 1; // 明度参数因子，允许 0.1..10。
    double kC = 1; // 彩度参数因子；a*/b* 合成彩度与色相，不独立加权。
    double kH = 1; // 色相参数因子。
};

static_assert(sizeof(McColorDifferenceSettings) == 24, "McColorDifferenceSettings ABI");

struct McAlignment
{
    // 标准→待测相似变换；角度为度，平移/RMS 为待测图像素。extra 为未分配的待测点数。
    double scale, angleDegrees, translateX, translateY, rms;
    int32_t matched, extra, ambiguous, reserved;
};

// 附加诊断接口：单次墙钟耗时（毫秒），不参与算法和判定；不改变已有 ABI 结构体布局。
struct McTimings
{
    double decodeMs, segmentationMs, measurementMs;
};

// 原始质量证据；阈值由托管业务层配置，避免两端重复判定。
struct McQuality
{
    double overexposedFraction, underexposedFraction, glareFraction;
    // 依次为 BGR8 差 RMS、边宽 px、支持比例、非概率置信指标、径向残差 px、内圈有效比例。
    double edgeContrast, edgeWidth, edgeSupport, contourConfidence, fitResidual, validSampleFraction;
    int32_t refined, reserved;
};

static_assert(sizeof(McQuality) == 80, "McQuality ABI");
#pragma pack(pop)
MC_API int mc_abi_version();
MC_API const char* mc_last_error(); // thread-local UTF-8, copy before next call
// 成功返回 0 并创建唯一拥有的句柄；失败返回 -1。调用方不可复制句柄后重复释放。
MC_API int mc_analyze(const wchar_t* path, const McSettings* settings, void** result);
MC_API int mc_result_info(void* result, int* width, int* height, int* count);
MC_API int mc_result_timings(void* result, McTimings* timings);
// 借用原图 BGR8 缓冲；只读指针在 mc_release 前有效，调用方必须在释放句柄前完成复制。
MC_API int mc_result_bgr(void* result, const uint8_t** pixels, int* stride, int* length);
MC_API int mc_get_quality(void* result, int index, McQuality* quality);
MC_API int mc_get_mark(void* result, int index, McMark* mark);
// capacity 按 McPoint 元素数计；成功返回复制点数，其他读取接口成功返回 0。
MC_API int mc_get_contour(void* result, int index, McPoint* points, int capacity);
MC_API void mc_release(void* result);
MC_API int mc_compare(const McMark* reference, int referenceCount, int refWidth, int refHeight, const McMark* observed,
                      int observedCount, int width, int height, const McMatchSettings* settings,
                      McComparison* comparisons, McAlignment* alignment);
MC_API double mc_delta_e00(double l1, double a1, double b1, double l2, double a2, double b2);
// 新接口显式接收色差参数；旧 mc_compare / mc_delta_e00 始终使用 1/1/1。
// comparisons 必须容纳 referenceCount 项，按标准数组顺序输出；返回 0 不代表产品颜色合格。
MC_API int mc_compare_weighted(const McMark* reference, int referenceCount, int refWidth, int refHeight,
                               const McMark* observed, int observedCount, int width, int height,
                               const McMatchSettings* settings, const McColorDifferenceSettings* colorSettings,
                               McComparison* comparisons, McAlignment* alignment);
// 无效参数返回 NaN，错误说明通过 mc_last_error 取得。
MC_API double mc_delta_e00_weighted(double l1, double a1, double b1, double l2, double a2, double b2,
                                    const McColorDifferenceSettings* settings);
