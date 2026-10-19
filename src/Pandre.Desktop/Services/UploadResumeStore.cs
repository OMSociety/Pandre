using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Pandre.Core;

namespace Pandre.Desktop.Services;

/// <summary>
/// 上传断点续传记录的本地落盘实现：JSON 存 %APPDATA%\Pandre\upload-resume.json（与 settings.json 同目录）。
/// 只承载服务端上传会话标识（upload_id/task_id 等）与分片落点，不含任何 Cookie/账号凭据。
/// 写盘走「临时文件 + 原子替换」（同凭据存盘手法），进程中断不会留下半截 JSON；
/// 所有故障按「无记录」降级并留调试痕迹，不打断上传。
/// </summary>
public sealed class JsonUploadResumeStore : IUploadResumeStore
{
    private readonly string _filePath;
    private readonly object _gate = new();

    public JsonUploadResumeStore(string directory)
    {
        _filePath = Path.Combine(directory, "upload-resume.json");
    }

    public UploadResumeEntry? Load(string key)
    {
        lock (_gate)
        {
            try
            {
                var map = ReadMap();
                return map.TryGetValue(key, out var e) ? e : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[UploadResume] 读取失败，按无记录处理: " + ex.Message);
                return null;
            }
        }
    }

    public void Save(string key, UploadResumeEntry entry)
    {
        lock (_gate)
        {
            try
            {
                var map = ReadMap();
                map[key] = entry;
                WriteMap(map);
            }
            catch (Exception ex)
            {
                // 记录写失败只损失续传能力，不打断上传
                System.Diagnostics.Debug.WriteLine("[UploadResume] 写盘失败: " + ex.Message);
            }
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            try
            {
                var map = ReadMap();
                if (map.Remove(key)) WriteMap(map);
            }
            catch (Exception ex)
            {
                // 清理失败仅遗留废记录，保留期后不再命中
                System.Diagnostics.Debug.WriteLine("[UploadResume] 清理失败: " + ex.Message);
            }
        }
    }

    private Dictionary<string, UploadResumeEntry> ReadMap()
    {
        if (!File.Exists(_filePath)) return new Dictionary<string, UploadResumeEntry>();
        return JsonSerializer.Deserialize<Dictionary<string, UploadResumeEntry>>(File.ReadAllText(_filePath))
               ?? new Dictionary<string, UploadResumeEntry>();
    }

    private void WriteMap(Dictionary<string, UploadResumeEntry> map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _filePath, overwrite: true);
    }
}
