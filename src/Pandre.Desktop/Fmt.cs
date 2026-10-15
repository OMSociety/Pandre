using System;

namespace Pandre.Desktop;

/// <summary>字节量展示格式（全仓唯一切换点：LoginCard / PanView 文件行 / Downloads 行共用）。</summary>
public static class Fmt
{
    public static string Size(long b) => b switch
    {
        >= 1L << 30 => $"{b / 1073741824.0:F1} GB",
        >= 1L << 20 => $"{b / 1048576.0:F1} MB",
        >= 1L << 10 => $"{b / 1024.0:F1} KB",
        _ => $"{b} B",
    };
}
