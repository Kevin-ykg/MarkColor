cmake_minimum_required(VERSION 3.24)
project(MarkColorNative VERSION 1.0 LANGUAGES CXX)
# 独立 Native 构建入口；日常 C#/C++ 联合调试使用根目录 MarkColorDemo.sln。
find_package(OpenCV 4 REQUIRED COMPONENTS core imgproc imgcodecs)

# 新增 Native 源文件时，同步维护此列表及 .vcxproj/.filters。
add_library(MarkColorNative SHARED
  src/mark_color.cpp
  src/mark_detection.cpp
  src/mark_quality.cpp
  src/mark_candidates.cpp
  src/mark_matching.cpp
  src/color_difference.cpp
)
target_include_directories(MarkColorNative PUBLIC include PRIVATE ${OpenCV_INCLUDE_DIRS})
target_link_libraries(MarkColorNative PRIVATE ${OpenCV_LIBS})
target_compile_features(MarkColorNative PUBLIC cxx_std_17)
target_compile_definitions(MarkColorNative PRIVATE MC_EXPORTS NOMINMAX)
if(MSVC)
  target_compile_options(MarkColorNative PRIVATE /W4 /utf-8 /EHsc)
endif()

# 合成图与数值回归不依赖实拍图库；图库/WPF 验证由根目录 verify.ps1 执行。
add_executable(native_tests tests/native_tests.cpp)
target_include_directories(native_tests PRIVATE ${OpenCV_INCLUDE_DIRS})
target_link_libraries(native_tests PRIVATE MarkColorNative ${OpenCV_LIBS})
target_compile_features(native_tests PRIVATE cxx_std_17)
if(MSVC)
  target_compile_options(native_tests PRIVATE /utf-8 /EHsc)
endif()
enable_testing()
add_test(NAME native_regression COMMAND native_tests)
