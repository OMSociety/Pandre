using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pandre.Core;

/// <summary>登录凭据：Cookie 串 + 展示名 + 取得时间。</summary>
public sealed record PanCredential(PanProvider Provider, string Cookie, string? Nickname, DateTimeOffset ObtainedAt)
{
    // Cookie 即账号等价物：ToString 一律脱敏，防日志/调试器输出泄漏（AGENTS 禁止输出 Cookie 值）。
    // JSON 落盘走序列化器不受影响。
    public override string ToString() =>
        $"{nameof(PanCredential)} {{ {nameof(Provider)} = {Provider}, {nameof(Cookie)} = ***({Cookie.Length} chars), {nameof(Nickname)} = {Nickname}, {nameof(ObtainedAt)} = {ObtainedAt:O} }}";
}

/// <summary>凭据加密存储：BDUSS 等于百度全站通行证，落盘必须加密。</summary>
public interface ICredentialStore
{
    Task SaveAsync(PanCredential credential, CancellationToken ct = default);
    Task<PanCredential?> LoadAsync(PanProvider provider, CancellationToken ct = default);
    Task ClearAsync(PanProvider provider);
}

/// <summary>
/// Windows 用 DPAPI（当前用户域加密）；macOS/Linux 的 Keychain/libsecret 在 P3 接入，
/// 非 Windows 暂降级为纯文件 + 0600 权限。
/// </summary>
public sealed class CredentialStore : ICredentialStore
{
    private readonly string _dir;

    public CredentialStore(string? dir = null)
    {
        _dir = dir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pandre", "credentials");
        Directory.CreateDirectory(_dir);
    }

    private string PathFor(PanProvider p) => Path.Combine(_dir, p.ToString().ToLowerInvariant() + ".dat");

    public async Task SaveAsync(PanCredential credential, CancellationToken ct = default)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(credential);
        var payload = Protect(plain);
        var target = PathFor(credential.Provider);
        var tmp = target + ".tmp";
        await File.WriteAllBytesAsync(tmp, payload, ct).ConfigureAwait(false);
        File.Move(tmp, target, overwrite: true);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // 非 Windows 降级为明文文件时收紧为 0600，仅属主可读写（Keychain/libsecret P3 接入）
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public async Task<PanCredential?> LoadAsync(PanProvider provider, CancellationToken ct = default)
    {
        var path = PathFor(provider);
        if (!File.Exists(path)) return null;
        try
        {
            var payload = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var plain = Unprotect(payload);
            return JsonSerializer.Deserialize<PanCredential>(plain);
        }
        catch
        {
            // 解密失败（换了机器/用户）或格式损坏：按无凭据处理，让 UI 走重新扫码
            return null;
        }
    }

    public Task ClearAsync(PanProvider provider)
    {
        var path = PathFor(provider);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private static byte[] Protect(byte[] plain)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Dpapi.Protect(plain);
        return plain; // P3: macOS Keychain / Linux libsecret
    }

    private static byte[] Unprotect(byte[] payload)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Dpapi.Unprotect(payload);
        return payload;
    }

    /// <summary>隔离的 DPAPI 封装，避免非 Windows 平台加载时触发类型解析。</summary>
    private static class Dpapi
    {
        [SupportedOSPlatform("windows")]
        public static byte[] Protect(byte[] plain) =>
            ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

        [SupportedOSPlatform("windows")]
        public static byte[] Unprotect(byte[] payload) =>
            ProtectedData.Unprotect(payload, null, DataProtectionScope.CurrentUser);
    }
}
