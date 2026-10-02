using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace MemoryStudio;

public static class BundledAssets
{
    public static string Resolve(string name)
    {
        if (name != "DemoTarget.exe") throw new ArgumentException("Unknown bundled asset.", nameof(name));
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("MemoryStudio.Assets." + name);
        if (resource == null) throw new FileNotFoundException("演示程序未打包，请重新运行 build.cmd 或 package.cmd。");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemoryStudio", "Tools", hash);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        if (File.Exists(path) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(hash, StringComparison.OrdinalIgnoreCase)) return path;
        string temporary = Path.Combine(directory, name + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }
}
