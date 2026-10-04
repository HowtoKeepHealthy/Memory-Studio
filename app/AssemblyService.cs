using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace MemoryStudio;

/// <summary>Assemble at the exact target origin with the bundled, standalone NASM.</summary>
public static class AssemblyService
{
    public static byte[] Assemble(string source, ulong address, int bitness)
    {
        if (bitness is not (32 or 64)) throw new ArgumentException("请选择 x86 或 x64 模式。");
        if (string.IsNullOrWhiteSpace(source) || source.Length > 65536) throw new ArgumentException("汇编内容需要 1–65536 个字符。");
        if (bitness == 32 && address > uint.MaxValue) throw new ArgumentException("地址超出 x86 范围。");
        // The editor assembles a patch, with a fixed origin and no filesystem directives.
        if (Regex.IsMatch(source, @"(?im)^\s*(%|\[|org\b|bits\b|incbin\b|section\b|segment\b|absolute\b)", RegexOptions.CultureInvariant))
            throw new ArgumentException("补丁编辑器只接受指令、标签和 db/dw/dd/dq 数据；位数与起始地址由窗口指定。");
        source = Regex.Replace(source, @"\b(byte|word|dword|qword|tword|oword|yword|zword)\s+ptr\s+", "$1 ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        string directory = Path.Combine(Path.GetTempPath(), "MemoryStudio-Assembly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string input = Path.Combine(directory, "patch.asm"), output = Path.Combine(directory, "patch.bin");
        try
        {
            File.WriteAllText(input, $"BITS {bitness}\nORG 0x{address:X}\n" + source + "\n");
            var start = new ProcessStartInfo(BundledAssets.Resolve("nasm.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = directory };
            foreach (string arg in new[] { "-f", "bin", "-o", output, input }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动汇编器。");
            Task<string> errorTask = process.StandardError.ReadToEndAsync(), stdoutTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000)) { process.Kill(true); process.WaitForExit(); throw new TimeoutException("汇编超过 5 秒，已停止本次编译。"); }
            string error = errorTask.GetAwaiter().GetResult();
            _ = stdoutTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || !File.Exists(output)) throw new ArgumentException(error.Replace(input + ":", "第 ").Trim());
            if (new FileInfo(output).Length is < 1 or > 1048576) throw new ArgumentException("补丁大小需要 1–1,048,576 字节。");
            return File.ReadAllBytes(output);
        }
        finally
        {
            // Only these known generated files are cleaned; no recursive deletion.
            foreach (string file in new[] { input, output }) { try { File.Delete(file); } catch { } }
            try { Directory.Delete(directory); } catch { }
        }
    }
}
