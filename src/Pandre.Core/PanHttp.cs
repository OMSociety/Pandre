using System.Net;

namespace Pandre.Core;

/// <summary>HTTP 与 UA 常量、Cookie 工具。UA 口径来自逆向取证，勿随意改。</summary>
public static class PanHttp
{
    /// <summary>网页浏览器 UA：夸克网页端接口的默认身份。</summary>
    public const string BrowserUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    /// <summary>
    /// 夸克桌面客户端 UA：file/download 必须用它，否则服务端按网页身份限 100MB（23018）。
    /// 取证自 QuarkPanTool v0.0.5（GPL）与本机实测。
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
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        return client;
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
