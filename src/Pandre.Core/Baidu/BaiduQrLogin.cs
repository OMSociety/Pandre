using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pandre.Core.Baidu;

/// <summary>
/// 百度 passport 扫码登录（P0.5 实测链路）：
/// getqrcode 拿 sign+imgurl → 长轮询 unicast → qrbdusslogin 换凭证。
/// 关键坑：真凭证在响应体 session 字段（伪 JSON，正则抽取），Set-Cookie 里的 BDUSS 对 pan 接口无效。
/// </summary>
public sealed partial class BaiduQrLogin : IQrLoginFlow
{
    private readonly CookieContainer _jar = new();
    private readonly HttpClient _http;
    private string? _sign;

    public BaiduQrLogin()
    {
        _http = PanHttp.CreateClient(_jar, PanHttp.BrowserUa, timeoutSec: 70);
        _http.DefaultRequestHeaders.Referrer = new Uri("https://pan.baidu.com/");
    }

    public PanProvider Provider => PanProvider.Baidu;

    private static long Ts() => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    public async Task<QrCodePayload> GetQrCodeAsync(CancellationToken ct = default)
    {
        var json = JsonDocument.Parse(await _http.GetStringAsync(
            $"https://passport.baidu.com/v2/api/getqrcode?lp=pc&qrloginfrom=pc&qrcode=1&apiver=v3&tt={Ts()}&tpl=netdisk", ct));
        _sign = json.RootElement.GetProperty("sign").GetString()
            ?? throw new PanException("百度 getqrcode 未返回 sign");
        var imgurl = json.RootElement.GetProperty("imgurl").GetString()!.Replace("\\/", "/");
        if (!imgurl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            imgurl = "https://" + imgurl.TrimStart('/');
        var png = await _http.GetByteArrayAsync(imgurl, ct);
        return new QrCodePayload(png, null, "用「百度网盘 App」或「百度 App」扫码");
    }

    public async Task<PanCredential> WaitAsync(IProgress<QrLoginState>? progress = null, CancellationToken ct = default)
    {
        if (_sign == null) throw new InvalidOperationException("先调用 GetQrCodeAsync");
        progress?.Report(QrLoginState.Waiting);

        string? ticket = null;
        var deadline = DateTime.Now.AddMinutes(3);
        while (DateTime.Now < deadline && ticket == null)
        {
            ct.ThrowIfCancellationRequested();
            string raw;
            try
            {
                raw = await _http.GetStringAsync(
                    $"https://passport.baidu.com/channel/unicast?channel_id={_sign}&u=&tpl=netdisk&apiver=v3&tt={Ts()}&callback=", ct);
            }
            catch (HttpRequestException) { continue; }   // 长轮询本身会超时断连，重试即可
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { continue; }

            var uni = JsonDocument.Parse(raw);
            var cv = uni.RootElement.TryGetProperty("channel_v", out var cve) ? cve.GetString() ?? "" : "";
            if (cv.Length == 0) continue;
            var cvj = JsonDocument.Parse(cv.Replace("\\/", "/"));
            var status = cvj.RootElement.GetProperty("status").GetInt32();
            if (status == 1) progress?.Report(QrLoginState.Scanned);
            else if (status == 0 && cvj.RootElement.TryGetProperty("v", out var ve)) ticket = ve.GetString();
        }

        if (ticket == null)
        {
            progress?.Report(QrLoginState.Expired);
            throw new PanException("二维码超时未确认");
        }

        // 用票据换 BDUSS：返回 200 伪 JSON（单引号 + &quot; 转义），正则抽取
        var loginBody = await _http.GetStringAsync(
            $"https://passport.baidu.com/v3/login/main/qrbdusslogin?bduss={ticket}&u=https%3A%2F%2Fpan.baidu.com%2Fdisk%2Fmain&qrcode=1&tpl=netdisk&apiver=v3&tt={Ts()}", ct);
        var mBduss = BdussRegex().Match(loginBody);
        var mStoken = StokenRegex().Match(loginBody);
        if (!mBduss.Success || !mStoken.Success)
            throw new PanException("扫码确认后未能从响应抽取 BDUSS/STOKEN");

        // 顺手尽力抽个用户名做展示
        var mName = UsernameRegex().Match(loginBody);
        var nickname = mName.Success ? Uri.UnescapeDataString(mName.Groups[1].Value) : null;

        progress?.Report(QrLoginState.Confirmed);
        var cookie = $"BDUSS={mBduss.Groups[1].Value}; STOKEN={mStoken.Groups[1].Value}";
        return new PanCredential(PanProvider.Baidu, cookie, nickname, DateTimeOffset.Now);
    }

    [GeneratedRegex("\"bduss\":\\s*\"([^\"]+)\"")]
    private static partial Regex BdussRegex();

    [GeneratedRegex("netdisk#([0-9a-f]{64})")]
    private static partial Regex StokenRegex();

    [GeneratedRegex("\"username\":\\s*\"([^\"]+)\"")]
    private static partial Regex UsernameRegex();

    public void Dispose() => _http.Dispose();
}
