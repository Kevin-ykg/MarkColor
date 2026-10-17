# Mark 色彩检测 Demo

> 清除行为已修复：“标准文件 → 清除标准并重置”会清空当前标准、图像和结果，ROI 恢复全图，全部检测参数恢复默认；磁盘上已保存的 JSON 保留。见 [清除标准与会话复位说明](docs/清除标准与会话复位说明.md)。

> 2026-10-16 全项目代码整理：窗口设置、报告和图像绘制分文件，建标模型与质量规则分离，补充 Native/C# 单位与生命周期说明。源码导航见 [全项目代码整理说明](全项目代码整理说明.md)，验证证据见 [本轮记录](verification/project-readability-20261016/验证记录.md)。

> 2026-10-16：支持自定义 kL/kC/kH，主界面常用按钮放大、建标与标准文件分组、测量明细按需展开。使用及源码入口见 [色差参数与界面简化说明](色差参数与界面简化说明.md)。当前 Native ABI / 标准格式为 v3，旧标准可读取；编译时同步更新 WPF 与 DLL。

> 当前更新：增加 CMYK 应用图标，滚轮显示缩放上限提升为 64 倍，统一源码格式并补充关键注释。见 [图标缩放与格式维护说明](图标缩放与格式维护说明.md)；关于 L* 波动与色差判据，见 [ΔE00 权重与光照一致性](ΔE00权重与光照一致性说明.md)。

> 2026-10-15：支持不同尺寸圆点；单图建标失败可直接框选 ROI 后继续。操作与性能实测见 [建标恢复与性能优化说明](建标恢复与性能优化说明.md)，相机适配见 [相机与圆点尺寸适配说明](相机与圆点尺寸适配说明.md)。

> 2026-09-24 升级：多图采样标准、质量检查、强类型结论、弱对比精修、界面预览和便携部署见 [升级功能与使用说明](升级功能与使用说明.md)。Native ABI 已升级为 2，SDK 默认使用项目内 dependencies/opencv/build。

已实现 **C++ OpenCV 算法 DLL + C# WPF 界面**。在当前图库上完成了编译和验证，原始图库未修改。

开发入口：[项目全局导航与开发指南](项目全局导航与开发指南.md)，包含源码地图、调用链、上下位移配准、状态含义、OpenCV 配置与联合调试步骤。

代码约定见 [代码规范](代码规范.md)。C++ 按接口、检测、匹配、色差公式分文件；C# 参数和业务模型位于 `Models/`。`MarkColorNative` 的包含目录、链接目录、依赖库、Debug/Release 选项直接列在 `.vcxproj` 中，项目树的“环境与构建配置”可直接打开 SDK 配置。

## 直接运行

双击 `run.cmd`，或运行 `publish/MarkColorDemo.exe`。本机已有所需的 .NET 10 Desktop Runtime x64 和 VC++ 运行环境，`publish` 已包含算法 DLL 与 OpenCV DLL。搬到其他电脑时请复制整个 `publish` 文件夹，并安装对应运行环境；不要只复制 EXE。

1. 点击 **单图建标**，例如选择上级目录 `优良品/2-上.bmp`；或选择 **多图采样建标**，建立同一膜片多位置采样的平均标准。
2. 点击 **加载待测图像**，例如选择 `M通道偏小二档/2-上.bmp`。自动提取、取色、匹配并判定。
3. 在 **设置 / 微调标准** 中调整提取参数、标准 Lab、名称、墨路和各点阈值，点击应用后重新检测。
4. **保存标准 / 读取标准** 将图像来源、SHA-256、标准数值、逐点阈值和算法参数一起存入 JSON。已提供 `standards` 中的五色、单黑点和反向五色示例标准。
5. **批量检测 / 导出报告** 默认生成 HTML 可视化报告，可直接用浏览器离线打开：先看整图结论与标注图，再看各点 ΔE00、阈值和超限原因；Lab 明细折叠显示。保存时可切换为原有详细 CSV。支持拖入图像、上一张/下一张、滚轮缩放、拖动平移、双击适应窗口。

请按相同打印方向、同类拍摄视野建立标准。`2-*.bmp` 与 `3-*.bmp` 彩色圆点的左右次序相反，应分别建标准；单黑点视野与五色视野也应分别建标准。默认匹配允许小角度旋转、平移及比例变化，不做镜像校正。

同版式上下位置偏移现默认允许最大平移为图像对角线的 1.00 倍。`优良品/2-上.bmp → 2-下.bmp` 已纳入回归，5/5 唯一匹配。旧 JSON 标准仍保留原先的 0.30 等显式设置；需要时在设置中把“最大平移 / 对角线”改为 1 并保存。配准不确定时仅显示“候选 P编号 · 待配准”，不会再把未经确认的位置标为缺失。位置匹配成功不代表颜色一定合格，仍按原色差阈值判定。

![界面预览](verification/demo-preview.png)

## 已有功能

- 彩色、黑色及接近白色的圆点候选提取；按半径、圆度、填充率和 ROI 筛选，剔除触边圆点。
- 原图轮廓叠加及取色内圈显示；选择结果行高亮对应圆点。
- 每点 L*、a*、b*、RGB、面积、圆度、半径、采样像素数及 Lab 像素标准差。
- ΔE00 主判定，同时输出 ΔE76、带符号的 ΔL* / Δa* / Δb*。
- 逐点 ΔE00 阈值；可选 |ΔL|、|Δa|、|Δb| 阈值，设为 0 时关闭该项。
- 基于几何的一对一匹配；缺点、额外圆点、匹配不确定均不能通过。
- 原生异常边界、参数合法性校验、中文路径、SafeHandle 释放、异步计算与批量取消。
- 离线 HTML 图文报告：嵌入标准/待测图、实际轮廓、逐点阈值条与异常原因；批量按图统计 OK/NG/待确认/错误，取消后保留已完成结果。
- 复用 OpenCV 已解码像素作为 WPF 预览；修改阈值/刷新结果不重复解码。状态栏分别显示提取、匹配/结果、预览复制和界面提交耗时。

报告样例：[上下偏移样图报告](verification/visual-report.html)、[批量多状态样例](verification/visual-batch-report.html)。批量样例包含测试构造的错误和取消记录，用于展示状态表达。

## 性能与注释导航

Native 主要函数已补充中文输入/输出、单位、算法步骤、歧义处理和资源所有权说明；WPF 流水线、缓存、标准持久化和报告输出也说明了线程与生命周期约束。

`mark_detection.cpp` 对形态学按前景行段处理并保留足够边界；取色用部分选择替代完整排序，内圈/背景环复用一次 Lab 转换。`DetectionPipeline.cs` 组织后台处理，`ImagePreview.cs` 在原生句柄释放前复制并冻结位图，`ImageViewport.cs` 仅负责显示和标注。性能与数值一致性记录见 [性能优化与报告说明](性能优化与报告说明.md)。日常速度评估使用 Release；Debug 保留本机单步调试及 Debug OpenCV 检查，两者耗时不能混为一谈。

## 算法与量纲

**定位和测量分开。** 首先以 `HSV S > 40` 或灰度 `< 110` 得到彩色/黑色候选，形态学去噪后提取轮廓。浅色候选使用缩小灰度图与宽高斯背景的亮度差，默认阈值为 3 个灰度级。这个分支用于图库中已接近白色的异常圆点。再使用：

```text
8 ≤ 外接圆半径 ≤ 500 px（默认；边界允许半像素离散化偏差）
圆度 = 4π × 轮廓面积 / 周长² ≥ 0.70
填充率 = 轮廓面积 / 外接圆面积 ≥ 0.80
```

这些都是可调参数。处理坐标基于原始图像：左上角为原点，x 向右，y 向下。ROI 参数为相对于宽、高的 0–1 比例。Demo 每张图最多处理 16 个圆点；超过上限会提示收紧 ROI / 参数，避免噪声候选导致匹配耗时过长。

**取色用原始像素。** 测量区为填充轮廓与 0.65 倍半径内圈的交集。将原图 BGR8 归一化至 `[0,1]` 的 `CV_32FC3`，再调用 `cv::cvtColor(..., cv::COLOR_BGR2Lab)`。L* 范围为 0–100，a* / b* 使用带符号数值，不使用 8 位 Lab 的偏置编码。各 Lab 通道独立去除最高和最低各 5% 像素后取均值；σ 使用全部内圈像素。RGB 均值、σ 和背景环 L* 只作为辅助信息，不替代主色差指标。实现依据见 [OpenCV 色彩转换说明](https://docs.opencv.org/4.13.0/de/d25/imgproc_color_conversions.html)。实际编译版本为本机已有的 OpenCV 4.5.4。

**对应关系不依赖颜色。** 先产生受约束的相似变换假设，再用 Hungarian 分配完成一对一几何匹配，并用已匹配点细化平移、旋转、比例。优先选择匹配点数更多、位置误差更小的解；近似等价但对应关系不同的解标记为“不确定”。单黑点只能确定平移，不能独立估计旋转和比例。颜色改变不会改变该点的墨路标签。

**判定规则：** `ΔE00 ≤ 逐点阈值`，且所有启用的通道差阈值均通过，才判该点 OK。整图需测量质量满足要求、所有标准点通过、无额外点且匹配无歧义。ΔE00 使用 CIEDE2000，`kL=kC=kH=1` 为默认值，可在设置中自定义并随标准保存。包含独立实现和 [Sharma / Wu / Dalal 发布的参考测试值](https://hajim.rochester.edu/ece/sites/gsharma/ciede2000/) 回归检查。

## 标准与微调的行为

名称及墨路为可编辑标注，初始名称仅为根据 RGB 色相的猜测。**蓝、绿、红点对应哪种 CMYK 配方，需要结合实际打印配方确认**；RGB/Lab 无法直接反推出打印机各通道的出墨量。

标准 Lab 可以人工微调。只改标签、Lab、阈值或匹配设置时，不重新取标准颜色。若更改分割、ROI 或取色内圈等提取设置，程序会从哈希一致的原采样图重新测量，保留位置一致的标签、阈值和人工 Lab 偏移。若点数或位置变化，整个修改不应用；可先“清除标准并重置”，再为新图设置 ROI/参数并重新建标。

读取标准时，已保存的数值为判定依据。原图丢失或哈希改变时，仍可使用标准数值检测，但不显示可能不对应的原图，也不能从它重新提取。保存的标准是单张图像标准；尚未做多张 OK 的统计建模。

## 验证结果与边界

详见 `verification/verification.txt`、`gallery-detection.json`、`gallery-comparisons.csv`。

| 检查 | 本机结果 |
|---|---|
| C++ / WPF Release 构建 | 通过，0 警告、0 错误 |
| 图库 | 99 张 BMP，2448 × 2048 |
| 圆点提取 | 231 个；99/99 张符合预期 1 或 5 点 |
| 与对应视野标准的几何匹配 | 全部成功，无歧义 |
| 18 张优良品各自与自身比较 | 全部 ΔE00 = 0 |
| 近白色、缺点、额外点、几何变换、匹配歧义 | 检查通过 |
| 标准保存/读取、微调保持、无效参数拒绝 | 检查通过 |
| WPF 布局与绑定渲染 | 默认尺寸、最小尺寸、设置页检查通过 |
| 原生分析耗时 | Release 本机运行中位数约 49–56 ms；首次调用含初始化；Debug 未优化，耗时更长 |

示例：以 `优良品/2-上.bmp` 为标准，`M通道偏小二档/2-上.bmp` 的洋红点 ΔE00 约 **46.77**，红点约 **64.01**，蓝点约 **13.79**；五个点均有对应测量值。`gallery-comparisons.csv` 的详细判定使用默认 ΔE00=3。

这验证的是 Demo 的提取、测量和比较流程，**不等同于已验证现场误检率/漏检率**。默认阈值 3 未通过生产统计标定；同图自比不代表独立 OK 样本的重复性。文件夹名表示实验条件，不能直接当作该文件夹内每个视野、每个圆点都应 NG 的逐点真值。

目前输出的是以相机 RGB 假设为 sRGB 的相对 Lab 测量，并非分光仪的标定测色值。实际应用应固定光源、曝光、增益、白平衡、拍摄几何和材料背景，用多次拍摄的 OK 分布与已知 NG 样本确定每点阈值。界面显示的墨路名称不代表系统已定位到某个喷头或证明其出墨量异常。拉丝、局部断线等纹理缺陷也可能被平均色弱化；目前 σ 供观察，尚未建立独立纹理判定。

## 源码与构建

```text
MarkColorDemo.sln                 统一入口：C# WPF、C++ DLL、C++ / C# 验证项目
native/MarkColorNative.vcxproj    可直接在 Visual Studio 编辑、生成和调试的 C++ 工程
build-support/OpenCV.props       所有项目共用的 OpenCV 路径和版本
build-support/Native.Runtime.targets DLL / PDB / OpenCV 自动复制
native/include/mark_color.h       x64 C ABI / 结构体量纲
native/src/mark_color.cpp         DLL C ABI 入口、异常边界、结果生命周期
native/src/mark_detection.cpp     圆点定位、内圈取色与统计
native/src/mark_matching.cpp      相似变换、Hungarian 匹配与比较结果
native/src/color_difference.cpp   分步骤的 CIEDE2000 公式
native/tests/native_tests.cpp     原生数学和异常场景回归
src/MarkColorDemo/Native.cs       P/Invoke + SafeHandle
src/MarkColorDemo/Models/         标准、参数、逐点判定与持久化，按类分文件
src/MarkColorDemo/MainWindow.*    WPF 操作与异步编排
src/MarkColorDemo/ImageViewport.cs 原图显示、轮廓叠加和缩放
src/MarkColorDemo/SettingsWindow.* 参数 / 标准微调
tests/Verification/Program.cs     图库检查、CSV、WPF 离屏渲染
standards/                       示例标准 JSON
publish/                         可运行程序
```

本机使用 Visual Studio 2026 MSVC x64、.NET SDK 10.0.401、OpenCV 4.5.4。算法为 C++17，托管界面目标为 `net10.0-windows`；没有 OpenCvSharp 或额外 NuGet 包依赖。主解决方案由 Visual Studio MSBuild 统一编译，不再需要预先运行 CMake。

```powershell
# 在 MarkColorDemo 文件夹中执行。默认使用 build-support/OpenCV.props 的 SDK 配置。
.\build.ps1

# 构建用于 C# + C++ 联合调试的版本，不发布。
.\build.ps1 -Configuration Debug -NoPublish

# 其他电脑：指定 OpenCVConfig.cmake 所在目录（推荐 x64/vc*/lib）。
.\build.ps1 -OpenCvDir 'D:\opencv\build\x64\vc16\lib'

# 重新验证当前图库并生成报告和界面预览。
.\verify.ps1

# 验证 Debug 版本；另可检查复制的 PDB 是否能解析到 C++ 源码行。
.\verify.ps1 -Configuration Debug
.\build-support\check-symbols.ps1
```

## 在同一解决方案中编译和打断点

1. 重新打开根目录的 `MarkColorDemo.sln`，或接受 Visual Studio 检测到的工程变更。解决方案中会直接显示 **MarkColorDemo** 和 **MarkColorNative**，两个验证项目位于 Tests 分组。
2. 选择 **Debug / x64**，将 **MarkColorDemo** 设为启动项目，启动配置选择 **MarkColorDemo (C# + C++)**。
3. 展开 **MarkColorNative → Source Files → mark_color.cpp**，在 `mc_analyze` 内设置断点。进入 `mark_detection.cpp` 的 `measureMark` 观察取色，或进入 `mark_matching.cpp` 的 `compareMarks` 观察匹配；色差公式位于 `color_difference.cpp`。
4. 按 **F5**。WPF 工程引用了 native 工程，Visual Studio 会先编译算法，再把当前配置的算法 DLL、匹配 PDB、OpenCV DLL 复制到 WPF 输出目录，然后启动界面。
5. 在界面中加载图像触发 `mc_analyze`，即可进入 C++ 调试。只改 `mark_color.cpp` 后，停止调试再按 F5，依赖关系会触发 C++ 增量生成和自动复制，无需手动运行脚本。

启动配置中已设置 `nativeDebugging: true`，启用托管与本机混合调试，参见 [Microsoft 混合调试说明](https://learn.microsoft.com/en-us/visualstudio/debugger/how-to-debug-managed-and-native-code?view=visualstudio) 和 [.NET 启动配置说明](https://github.com/dotnet/project-system/blob/main/docs/launch-profiles.md)。Debug 下 C++ 使用 `/Od`、`/Zi`、`/MDd` 并生成 PDB，OpenCV 自动选用 `opencv_world454d.dll`；Release 则使用 `opencv_world454.dll`。

已检查 Debug / Release 统一构建、仅构建 WPF 时的 C++ 增量编译、DLL/PDB 自动复制和 PDB 源码行解析。Debug 版 99 张图库回归结果另存于 `verification/verification-debug.txt`；Debug 用于断点观察，性能评估使用 Release。

| 内容 | 输出位置 |
|---|---|
| C++ DLL / PDB / LIB | `build/msbuild/native/Debug/` 或 `Release/` |
| WPF F5 启动目录（含复制的 DLL/PDB） | `src/MarkColorDemo/bin/Debug/net10.0-windows/` 或 `Release/` |
| C++ 验证程序 | `build/msbuild/tests/Debug/` 或 `Release/` |
| C# 验证程序 | `build/verification/Debug/` 或 `Release/` |
| Release 发布目录 | `publish/` |
| Debug 发布目录（未指定 -NoPublish 时） | `publish/Debug/` |

若断点在程序刚启动时为空心，先加载一张图片，让 P/Invoke 装入 native DLL。如果仍未命中，在“调试 → 窗口 → 模块”中检查 `MarkColorNative.dll` 的路径是否位于当前 WPF 输出目录，以及符号是否已加载。可以运行 `check-symbols.ps1` 检查 DLL/PDB 哈希一致性及 `mc_analyze` 的源码行信息；该检查不替代 Visual Studio 中的实际断点操作。

更换 OpenCV SDK 时统一修改 `build-support/OpenCV.props` 的 `OpenCvRoot`、`OpenCvToolset`、`OpenCvVersion`。其中 `OpenCvToolset=vc15` 指现有 SDK 的文件夹名；本项目自身使用 VS 2026 的 v145 编译器。构建脚本的 `-OpenCvDir` 参数也可临时覆盖 SDK 路径。普通 `dotnet build` 不提供完整的 C++ MSBuild 工具链，请使用 Visual Studio“生成解决方案”或 `build.ps1`。

旧的 `native/CMakeLists.txt` 与 `build/native/MarkColorNative.slnx` 仅保留作为独立 CMake 构建入口，不参与主解决方案，日常开发使用根目录 `MarkColorDemo.sln`。DLL 必须与进程同为 x64；C# 不跨边界传递 `cv::Mat` 或 STL 类型，结果用 SafeHandle 管理，C++ 异常在 DLL 内转为错误码。

文件型标准与 CSV 输出均为本地操作，程序不调用网络服务。OpenCV 许可文本位于 `licenses`。
