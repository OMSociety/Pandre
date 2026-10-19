using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Pandre.Core;

/// <summary>HTTP 与 UA 常量、Cookie 工具。UA 口径来自逆向取证，勿随意改。</summary>
public static class PanHttp
{
    /// <summary>网页浏览器 UA：夸克网页端接口的默认身份。</summary>
    public const string BrowserUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    /// <summary>
    /// 夸克桌面客户端 UA：file/download 必须用它，否则服务端按网页身份限 100MB（23018）。
    /// 取证自 QuarkPanTool v0.0.5（Apache-2.0）与本机实测。
    /// </summary>
    public const string QuarkDesktopUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) quark-cloud-drive/2.5.56 Chrome/100.0.4896.160 Electron/18.3.5.12-a038f7b798 Safari/537.36 Channel/pckk_other_ch";

    /// <summary>百度下载链（dlink/pcs streaming）要求的 UA。</summary>
    public const string BaiduNetdiskUa = "netdisk";

    /// <summary>百度网盘客户端 UA：PCS REST 端点（superfile2 上传等）用它，否则服务器断连。</summary>
    public const string BaiduNetdiskClientUa =
        "netdisk;P2SP;3.0.0.8;netdisk;11.12.3;ANG-AN00;android-android;10.0;JSbridge4.4.0;jointBridge;1.1.0;";

    public static readonly string[] QuarkCookieHosts =
        ["https://pan.quark.cn", "https://drive-pc.quark.cn", "https://uop.quark.cn", "https://quark.cn"];

    public static readonly string[] BaiduCookieHosts =
        ["https://pan.baidu.com", "https://baidu.com", "https://d.pcs.baidu.com", "https://c.pcs.baidu.com"];

    public static HttpClient CreateClient(CookieContainer jar, string userAgent, int timeoutSec = 40)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = jar,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(new CharsetFixHandler(handler)) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        return client;
    }

    /// <summary>
    /// 百度若干端点把 Content-Type 写成 charset=utf8（无连字符），.NET 的 Encoding.GetEncoding("utf8")
    /// 不认这个别名，HttpClient.GetStringAsync 会直接抛
    /// "The character set provided in ContentType is invalid / 'utf8' is not a supported encoding name"。
    /// 在管道里统一改写成 .NET 认识的写法，免得每个调用点各自兜底（CreateFolderAsync 曾因此必失败）。
    /// </summary>
    private sealed class CharsetFixHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = await base.SendAsync(request, ct).ConfigureAwait(false);
            var contentType = resp.Content.Headers.ContentType;
            var charset = contentType?.CharSet;
            if (contentType?.MediaType is not { } mediaType || string.IsNullOrEmpty(charset)) return resp;
            var normalized = NormalizeCharset(charset);
            if (normalized != null)
                resp.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = normalized };
            return resp;
        }

        /// <summary>已认识的编码返回 null（不动）；别名归一；其余按 UTF-8（两盘接口实际都是 UTF-8）。</summary>
        private static string? NormalizeCharset(string charset)
        {
            try { Encoding.GetEncoding(charset); return null; }
            catch (ArgumentException) { }
            return charset.Replace("-", "").Replace("_", "").Trim().ToLowerInvariant() switch
            {
                "utf8" => "utf-8",
                "utf16" => "utf-16",
                "utf16le" => "utf-16",
                "utf16be" => "utf-16BE",
                "utf32" => "utf-32",
                _ => "utf-8",
            };
        }
    }

    /// <summary>把 "k1=v1; k2=v2" 形式的 Cookie 串种进 jar（按主机逐一登记，合并去重由 jar 负责）。</summary>
    public static CookieContainer SeedCookies(string cookieString, params string[] hosts)
    {
        var jar = new CookieContainer();
        foreach (var pair in cookieString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var name = pair[..eq].Trim();
            var value = pair[(eq + 1)..].Trim();
            foreach (var host in hosts)
            {
                try { jar.Add(new Uri(host), new Cookie(name, value, "/")); }
                catch (CookieException) { /* 忽略个别不合规格式 */ }
            }
        }
        return jar;
    }

    /// <summary>把 jar 里这些主机的 Cookie 汇成一串（同名后者覆盖前者）。</summary>
    public static string JarToCookieString(CookieContainer jar, params string[] hosts)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var host in hosts)
            foreach (Cookie ck in jar.GetCookies(new Uri(host)))
                map[ck.Name] = ck.Value;
        return string.Join("; ", map.Select(kv => kv.Key + "=" + kv.Value));
    }
}
