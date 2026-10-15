using System;
using System.IO;

namespace Pandre.Desktop.Services;

/// <summary>
/// 把随应用打包的浏览器扩展复制到 %APPDATA%\Pandre\extension，并写入 config.js（端口 + 令牌）。
/// 浏览器以「加载已解压的扩展程序」方式装载这个目录。
/// </summary>
public static class ExtensionInstaller
{
    /// <summary>随应用发布的扩展源目录（构建时从 assets/extension 复制到输出目录）。</summary>
    public static string SourceDir => Path.Combine(AppContext.BaseDirectory, "extension");

    /// <summary>用户侧安装目录（浏览器加载的就是它）。</summary>
    public static string TargetDir => Path.Combine(AppServices.AppDataDirPath, "extension");

    /// <summary>复制扩展文件并写入真实端口/令牌；返回目标目录。源目录缺失时抛异常（打包问题）。</summary>
    public static string Install()
    {
        if (!Directory.Exists(SourceDir))
            throw new InvalidOperationException($"扩展源目录不存在：{SourceDir}");
        Directory.CreateDirectory(TargetDir);
        foreach (var src in Directory.EnumerateFiles(SourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(SourceDir, src);
            var dst = Path.Combine(TargetDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }
        File.WriteAllText(
            Path.Combine(TargetDir, "config.js"),
            $"globalThis.PANDRE_CONFIG = {{ port: {AppServices.TakeoverPort}, token: \"{AppServices.TakeoverToken}\" }};\n");
        return TargetDir;
    }
}
