using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

/// <summary>由 check-symbols.ps1 临时加载的 DbgHelp 探针；不编入生产 WPF 程序。</summary>
public static class MarkColorSymbolProbe
{
    // SYMBOL_INFO 缓冲区布局常量；本项目调试检查仅面向 Windows x64。
    private const int SymbolInfoSize = 88;
    private const int NameCapacity = 1024;
    private const int MaxNameLengthOffset = 80;
    private const int AddressOffset = 56;

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceLine
    {
        public uint SizeOfStruct;
        public IntPtr Key;
        public uint LineNumber;
        public IntPtr FileName;
        public ulong Address;
    }

    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SymInitializeW(IntPtr process, string searchPath, bool invade);

    [DllImport("dbghelp.dll")]
    private static extern uint SymSetOptions(uint options);

    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ulong SymLoadModuleExW(
        IntPtr process,
        IntPtr file,
        string image,
        string module,
        ulong address,
        uint size,
        IntPtr data,
        uint flags);

    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern bool SymFromName(IntPtr process, string name, IntPtr symbol);

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool SymGetLineFromAddrW64(IntPtr process, ulong address, out uint displacement, ref SourceLine line);

    [DllImport("dbghelp.dll")]
    private static extern bool SymCleanup(IntPtr process);

    private static void Check(bool success, string action)
    {
        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), action);
        }
    }

    public static string Verify(string dllPath)
    {
        IntPtr process = Process.GetCurrentProcess().Handle;
        SymSetOptions(0x616); // 保留名称与行号，要求 PDB 精确匹配，延迟加载且不弹出系统错误框。
        Check(SymInitializeW(process, Path.GetDirectoryName(dllPath), false), "SymInitialize");
        // 手工缓冲只在本次查询中存活；finally 同时释放缓冲和 DbgHelp 会话。
        IntPtr buffer = Marshal.AllocHGlobal(SymbolInfoSize + NameCapacity);
        try
        {
            Check(
                SymLoadModuleExW(process, IntPtr.Zero, dllPath, "MarkColorNative", 0x10000000, 0, IntPtr.Zero, 0) != 0,
                "Load native DLL/PDB");
            for (int i = 0; i < SymbolInfoSize + NameCapacity; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }
            Marshal.WriteInt32(buffer, 0, SymbolInfoSize);
            Marshal.WriteInt32(buffer, MaxNameLengthOffset, NameCapacity);
            Check(SymFromName(process, "MarkColorNative!mc_analyze", buffer), "Resolve mc_analyze");
            ulong address = unchecked((ulong)Marshal.ReadInt64(buffer, AddressOffset));
            var line = new SourceLine { SizeOfStruct = (uint)Marshal.SizeOf(typeof(SourceLine)) };
            uint displacement;
            Check(SymGetLineFromAddrW64(process, address, out displacement, ref line), "Resolve C++ source line");
            string source = Marshal.PtrToStringUni(line.FileName);
            if (source == null || !source.EndsWith("mark_color.cpp", StringComparison.OrdinalIgnoreCase) || !File.Exists(source))
            {
                throw new Exception("PDB source file is unavailable: " + source);
            }
            return "PASS: matching DLL/PDB in WPF output; mc_analyze => " + source + ":" + line.LineNumber;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            SymCleanup(process);
        }
    }
}
