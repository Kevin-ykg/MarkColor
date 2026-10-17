using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace MarkColorDemo;

/// <summary>为一次分析准备可跨线程使用的显示图。算法始终读取原分辨率像素。</summary>
public static class ImagePreview
{
    /// <summary>BitmapSource.Create 立即复制原生缓冲；本方法返回前调用方不得释放 Native 句柄。</summary>
    public static BitmapSource CopyBgr(int width, int height, IntPtr pixels, int length, int stride)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, length, stride);
        image.Freeze();
        return image;
    }

    // 无 Native 预览时的按需解码后备路径；缓存仅属于本次 Analysis，不按文件路径全局复用。
    public static void Prepare(Analysis analysis)
    {
        if (analysis.Preview != null || !File.Exists(analysis.Path))
        {
            return;
        }

        using var stream = File.OpenRead(analysis.Path);
        var image = new BitmapImage();
        image.BeginInit();
        // OnLoad 在关闭文件流前完成解码；Freeze 后可从工作线程安全交给 WPF UI 线程。
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        analysis.Preview = image;
    }
}
