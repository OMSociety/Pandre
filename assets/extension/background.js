// Pandre 接管扩展 · service worker
// 职责单一：收内容脚本的下载请求，转发给本机 Pandre（回环 HTTP + 令牌）。
// service worker 里发 fetch 不受页面私网访问（PNA）约束。
importScripts("config.js");

const CFG = globalThis.PANDRE_CONFIG || { port: 17821, token: "" };
const BASE = `http://127.0.0.1:${CFG.port}`;

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg?.type !== "pandre-download") return false;
  (async () => {
    try {
      const res = await fetch(`${BASE}/pandre/add`, {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-Pandre-Token": CFG.token },
        body: JSON.stringify({ provider: msg.provider, files: msg.files }),
      });
      const data = await res.json().catch(() => ({}));
      if (res.ok && data.ok) {
        sendResponse({ ok: true, enqueued: data.enqueued ?? 0 });
      } else {
        sendResponse({ ok: false, error: data.error || `Pandre 返回 HTTP ${res.status}` });
      }
    } catch {
      // 最常见：Pandre 没启动 / 没开启接管 / 端口被改 —— fetch 抛 TypeError: Failed to fetch
      sendResponse({ ok: false, error: "连不上本机 Pandre。请先启动 Pandre，并在设置里启用「浏览器接管」。" });
    }
  })();
  return true; // 异步 sendResponse
});
