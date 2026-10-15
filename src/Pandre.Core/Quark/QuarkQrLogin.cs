using System.Net;
using System.Text.Json;

namespace Pandre.Core.Quark;

/// <summary>
/// 夸克扫码登录（P0.5 实测链路，逆向自桌面客户端 login-window.js）：
/// getTokenForQrcodeLogin（必须带 client_id=386 等基础参数，否则 App 确认报「登录请求已过期」）
/// → 轮询 getServiceTicketByQrcodeToken → account/info?st= 落 Cookie → auth/pc/flush 补发 __puus。
/// </summary>
public sealed class QuarkQrLogin : IQrLoginFlow
{
    private readonly CookieContainer _jar = new();
    private readonly HttpClient _http;
    private string? _token;
    private string _uopBase = "";

    public QuarkQrLogin()
    {
        _http = PanHttp.CreateClient(_jar, PanHttp.BrowserUa);
    }

    public PanProvider Provider => PanProvider.Quark;

    public async Task<QrCodePayload> GetQrCodeAsync(CancellationToken ct = default)
    {
        _uopBase = "pr=ucpro&fr=pc&sys=win32&client_id=386&v=1.2&uc_param_str=utprfr&request_id=" + Guid.NewGuid();
        var json = JsonDocument.Parse(await _http.GetStringAsync(
            "https://uop.quark.cn/cas/ajax/getTokenForQrcodeLogin?" + _uopBase, ct).ConfigureAwait(false));
        _token = json.RootElement.GetProperty("data").GetProperty("members").GetProperty("token").GetString()
            ?? throw new PanException("夸克未返回扫码 token");

        // QR 内容照抄桌面客户端格式：不 URL 编码，参数顺序一致
        var qrText = "https://su.quark.cn/4_eMHBJ?token=" + _token
            + "&client_id=386&sch=pckk@other_ch&sve=3.24.0&ssb=weblogin&uc_param_str="
            + "&uc_biz_str=S:custom|OPT:SAREA@0|OPT:IMMERSIVE@1|OPT:BACK_BTN_STYLE@0";
        return new QrCodePayload(null, qrText, "用「夸克网盘 App」或「夸克 App」扫码");
    }

    public async Task<PanCredential> WaitAsync(IProgress<QrLoginState>? progress = null, CancellationToken ct = default)
    {
        if (_token == null) throw new InvalidOperationException("先调用 GetQrCodeAsync");
        progress?.Report(QrLoginState.Waiting);

        string? st = null;
        var deadline = DateTime.Now.AddMinutes(3);
        var unknownStreak = 0;   // 未知状态码连续计数（透传而非静默吞掉）
        var netFailStreak = 0;   // 网络抖动连续计数（连续 5 次按超时处理）
        while (DateTime.Now < deadline && st == null)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(2000, ct).ConfigureAwait(false);
            string body;
            try
            {
                body = await _http.GetStringAsync(
                    $"https://uop.quark.cn/cas/ajax/getServiceTicketByQrcodeToken?{_uopBase}&token={Uri.EscapeDataString(_token)}", ct).ConfigureAwait(false);
                netFailStreak = 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException && ++netFailStreak <= 5)
            {
                continue; // 单次网络抖动容错；连续 5 次失败跳出容错，按超时收尾
            }
            var resp = JsonDocument.Parse(body);
            var status = resp.RootElement.GetProperty("status").GetInt64();
            switch (status)
            {
                case 2000000:
                    st = resp.RootElement.GetProperty("data").GetProperty("members").GetProperty("service_ticket").GetString();
                    break;
                case 50004001:
                    unknownStreak = 0;              // 等待中
                    continue;
                case 50004002:
                    progress?.Report(QrLoginState.Expired);
                    throw new PanException("二维码已过期，请重新生成");
                default:
                    // 未知状态码透传：连续多次未知按错误终止，避免静默空转到超时
                    if (++unknownStreak >= 3)
                        throw new PanException($"夸克扫码轮询返回未知状态码 {status}");
                    continue;
            }
        }
        if (st == null)
        {
            progress?.Report(QrLoginState.Expired);
            throw new PanException("二维码超时未确认");
        }

        // 兑换 Cookie（Set-Cookie 进 jar），并从 account/info 拿昵称
        var authBody = await _http.GetStringAsync(
            $"https://pan.quark.cn/account/info?st={Uri.EscapeDataString(st)}", ct).ConfigureAwait(false);
        string? nickname = null;
        try
        {
            nickname = JsonDocument.Parse(authBody).RootElement
                .GetProperty("data").GetProperty("nickname").GetString();
        }
        catch { /* 昵称尽力而为 */ }

        // 补发 __puus（下载链 OSS 回调鉴权需要；此接口同时是会话续期口）
        await _http.GetStringAsync(
            "https://drive-pc.quark.cn/1/clouddrive/auth/pc/flush?pr=ucpro&fr=pc&sys=win32&uc_param_str=", ct).ConfigureAwait(false);

        var cookie = PanHttp.JarToCookieString(_jar, PanHttp.QuarkCookieHosts);
        if (!cookie.Contains("__puus="))
            throw new PanException("扫码成功但未拿到 __puus（flush 未补发），下载将不可用");

        progress?.Report(QrLoginState.Confirmed);
        return new PanCredential(PanProvider.Quark, cookie, nickname, DateTimeOffset.Now);
    }

    public void Dispose() => _http.Dispose();
}
