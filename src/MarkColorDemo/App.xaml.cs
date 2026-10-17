using System.Windows;

namespace MarkColorDemo;

/// <summary>启动窗口或执行便携环境自检；自检结果写入运行目录，退出码供部署脚本判断。</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        // 自检不打开窗口，核实实际加载的运行库来源；用于发现缺 DLL 或部署版本混用。
        if (e.Args.Contains("--self-check"))
        {
            try
            {
                int abi = Native.mc_abi_version();
                if (abi != 3)
                {
                    throw new InvalidOperationException($"Native ABI {abi}, expected 3.");
                }

                string? sample = e.Args.SkipWhile(x => x != "--self-check").Skip(1).FirstOrDefault();
                var image = sample == null ? null : Native.Analyze(sample, new DetectionSettings());
                var result = new
                {
                    Status = "OK",
                    Abi = abi,
                    Runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
                    DesktopRuntime = typeof(Window).Assembly.Location,
                    BaseDirectory = AppContext.BaseDirectory,
                    Marks = image?.Marks.Count,
                    Modules = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>().Where(m => new[] { "opencv", "MarkColorNative", "vcruntime", "msvcp140" }.Any(prefix => m.ModuleName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).Select(m => m.FileName).ToArray()
                };
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "self-check.json"),
                    System.Text.Json.JsonSerializer.Serialize(result));
                Shutdown(0);
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "self-check.json"), ex.ToString());
                Shutdown(1);
            }

            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
