// Pandre 接管扩展 · 站点共享逻辑
// 加载顺序：config.js → content-shared.js → content-<站点>.js（同一隔离世界，共享 globalThis）。
// 站点脚本只需组装 SITE 配置并调用 globalThis.__pandreBoot(SITE)。
//
// 两条入口：
//   1) 浮动按钮：勾选文件后出现「用 Pandre 下载（n）」，任意大小都可用（用户显式点）。
//   2) 下载入口接管：点网页自带的「下载」时，若文件大小过了站点门槛（百度 50MB / 夸克 100MB），
//      拦下原生行为直接交 Pandre —— 不拦的话网页会弹「请使用客户端」或干脆失败。
"use strict";

globalThis.__pandreBoot = function (SITE) {
  if (window.top !== window) return;

  // ---------- 注入 UI（浮动按钮 + 结果提示） ----------
  let fab = null, fabLabel = null, toast = null, toastTimer = 0;

  function ensureUi() {
    if (fab) return;
    fab = document.createElement("div");
    fab.style.cssText = [
      "position:fixed", "right:24px", "bottom:88px", "z-index:2147483646",
      "display:none", "align-items:center", "gap:8px",
      "padding:10px 16px", "border-radius:22px",
      "background:#C81E1E", "color:#fff",
      "font:13px/1.4 system-ui,'Microsoft YaHei',sans-serif",
      "box-shadow:0 4px 16px rgba(0,0,0,.35)", "cursor:pointer", "user-select:none",
    ].join(";");
    fabLabel = document.createElement("span");
    fab.appendChild(fabLabel);
    fab.addEventListener("click", onFabClick);
    document.documentElement.appendChild(fab);

    toast = document.createElement("div");
    toast.style.cssText = [
      "position:fixed", "right:24px", "bottom:144px", "z-index:2147483646",
      "display:none", "max-width:360px",
      "padding:10px 14px", "border-radius:10px",
      "background:rgba(24,24,24,.92)", "color:#eee",
      "font:12px/1.5 system-ui,'Microsoft YaHei',sans-serif",
      "box-shadow:0 4px 16px rgba(0,0,0,.35)", "white-space:pre-wrap",
    ].join(";");
    document.documentElement.appendChild(toast);
  }

  function showToast(text, isError) {
    ensureUi();
    toast.textContent = text;
    toast.style.background = isError ? "rgba(140,20,20,.94)" : "rgba(24,24,24,.92)";
    toast.style.display = "block";
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { toast.style.display = "none"; }, 4500);
  }

  // ---------- 选择读取（React fiber 反查行数据； greasyfork 586983 同款思路） ----------
  function reactFiberOf(dom) {
    for (const key of Object.keys(dom)) {
      if (key.startsWith("__reactFiber$") || key.startsWith("__reactInternalInstance$")) return dom[key];
    }
    return null;
  }

  // 在一个 props 对象里找「文件记录」：带站点 id 键 + 文件名/大小键。
  // 深度限 2：行组件的 props.record 一般在第 1-2 层；再往深挖容易挖到整表数据（拿错文件）。
  function digProps(obj, depth) {
    if (!obj || typeof obj !== "object" || depth > 2) return null;
    if (obj.$$typeof) return null; // React 元素：体积大且有环，不进
    const id = obj[SITE.idKey];
    if ((typeof id === "string" && id) || typeof id === "number") {
      if (SITE.nameKeys.some((k) => typeof obj[k] === "string") || obj.size != null) return obj;
    }
    for (const v of Object.values(obj)) {
      if (v && typeof v === "object") {
        const hit = digProps(v, depth + 1);
        if (hit) return hit;
      }
    }
    return null;
  }

  function filePropsOf(dom) {
    if (!dom) return null;   // 点的是工具栏/菜单项等不在行内的元素时 rowOfStrict 会给 null
    let fiber = reactFiberOf(dom);
    for (let hops = 0; fiber && hops < 24; hops++, fiber = fiber.return) {
      const hit = digProps(fiber.memoizedProps, 0);
      if (hit) return hit;
      if (fiber.stateNode && !(fiber.stateNode instanceof Node)) {
        const hit2 = digProps(fiber.stateNode, 0);
        if (hit2) return hit2;
      }
    }
    return null;
  }

  // 文件记录 → 提交给后台的最小结构
  function toEntry(p) {
    const id = String(p[SITE.idKey]);
    const name = SITE.nameKeys.map((k) => p[k]).find((v) => typeof v === "string" && v) || id;
    const size = Number(p.size ?? p.file_size ?? 0) || 0;
    return { id, name, size };
  }

  function readSelection() {
    const files = [];
    let skippedDirs = 0;
    let missed = 0;
    const seen = new Set();
    for (const node of SITE.checkedNodes()) {
      const row = SITE.rowOf(node);
      if (!row) continue;
      const p = filePropsOf(row) || filePropsOf(node);
      if (!p) { missed++; continue; }
      const id = String(p[SITE.idKey]);
      if (seen.has(id)) continue;
      seen.add(id);
      if (SITE.isDir(p)) { skippedDirs++; continue; }
      files.push(toEntry(p));
    }
    return { files, skippedDirs, missed };
  }

  // ---------- 轮询与按钮状态 ----------
  let current = { files: [], skippedDirs: 0, missed: 0 };
  let currentSig = "";
  let sending = false;
  let warnedMiss = false;

  function refresh() {
    if (!SITE.pageOk()) { if (fab) fab.style.display = "none"; return; }
    const sel = readSelection();
    const sig = sel.files.map((f) => f.id).join(",");
    if (sig === currentSig) return;
    currentSig = sig;
    current = sel;
    if (sel.missed > 0 && sel.files.length === 0 && !warnedMiss) {
      warnedMiss = true;
      console.warn("[pandre] 检测到勾选但未能从页面组件状态取到文件数据（站点结构可能已变），请把这条日志反馈给 Pandre。");
    }
    ensureUi();
    renderFab();
  }

  function renderFab() {
    const n = current.files.length;
    if (!fab) { if (n === 0) return; ensureUi(); }
    if (n === 0) { fab.style.display = "none"; return; }
    fabLabel.textContent = sending ? "正在交给 Pandre…" : `用 Pandre 下载（${n}）`;
    fab.style.display = "flex";
    fab.style.opacity = sending ? ".6" : "1";
    fab.style.pointerEvents = sending ? "none" : "auto";
  }

  function onFabClick() {
    if (sending || current.files.length === 0) return;
    submitToPandre(current.files, current.skippedDirs, "");
  }

  // ---------- 提交给 Pandre ----------
  async function submitToPandre(files, skippedDirs, note) {
    if (sending || files.length === 0) return;
    sending = true;
    renderFab();
    try {
      const res = await chrome.runtime.sendMessage({
        type: "pandre-download",
        provider: SITE.provider,
        files,
      });
      if (res?.ok) {
        let msg = `已交给 Pandre 下载 ${res.enqueued ?? files.length} 个文件。`;
        if (note) msg += note;
        if (skippedDirs > 0) msg += `（跳过 ${skippedDirs} 个文件夹）`;
        showToast(msg, false);
      } else {
        showToast(res?.error || "Pandre 返回了未知错误。", true);
      }
    } catch (e) {
      showToast("扩展后台通信失败：" + (e?.message || e), true);
    } finally {
      sending = false;
      renderFab();
    }
  }

  // ---------- 下载入口接管 ----------
  // 三个条件同时成立才拦：点的是下载动作 + 能取到文件 + 大小过站点门槛。
  // 任一不成立就放行，交回网页原生行为（小文件仍走浏览器自己下载）。
  function actionLabelHit(el, labels) {
    let n = el;
    for (let hops = 0; n && hops < 4; hops++, n = n.parentElement) {
      const raw = n.getAttribute?.("aria-label") || n.getAttribute?.("title") || n.textContent || "";
      if (labels.includes(String(raw).trim())) return true;
    }
    return false;
  }

  // 只有点到「文件行内部」才算行级入口（rowOf 的 li 兜底太宽，会把工具栏/侧栏也算成行，可能拿错文件）。
  function rowOfStrict(el) {
    return SITE.rowOfStrict ? SITE.rowOfStrict(el) : SITE.rowOf(el);
  }

  // 菜单类入口（右键/更多菜单）点下去时已经离开行 DOM，靠 pointerdown 记下最近一次落在行上的文件。
  let lastRowEntry = null, lastRowAt = 0;

  function rememberRow(el) {
    const p = filePropsOf(rowOfStrict(el));
    if (!p || SITE.isDir(p)) return;
    lastRowEntry = toEntry(p);
    lastRowAt = Date.now();
  }

  // 优先级：被点那一行 > 当前勾选集合 > 最近一次落在行上的文件（5 秒内）。
  function resolveClickedFiles(el) {
    const p = filePropsOf(rowOfStrict(el));
    if (p) {
      if (SITE.isDir(p)) return { files: [], skippedDirs: 1, missed: 0, from: "row" };
      return { files: [toEntry(p)], skippedDirs: 0, missed: 0, from: "row" };
    }
    const sel = readSelection();
    if (sel.files.length > 0) return { ...sel, from: "selection" };
    if (lastRowEntry && Date.now() - lastRowAt < 5000) {
      return { files: [lastRowEntry], skippedDirs: 0, missed: 0, from: "recent-row" };
    }
    return { ...sel, from: "none" };
  }

  function onDocumentClickCapture(e) {
    const gate = SITE.downloadGate;
    if (!gate || e.button !== 0 || e.defaultPrevented || !SITE.pageOk()) return;
    const el = e.target instanceof Element ? e.target : null;
    if (!el || !actionLabelHit(el, gate.labels)) return;

    const sel = resolveClickedFiles(el);
    if (sel.files.length === 0) return;   // 取不到文件或选的是文件夹：不拦
    const total = sel.files.reduce((sum, f) => sum + (f.size || 0), 0);
    const over = gate.byTotal
      ? total > gate.limitBytes
      : sel.files.some((f) => f.size > gate.limitBytes);
    if (!over) return;                    // 没过门槛：网页原生下载照旧

    // 拦在捕获阶段，网页自己的点击处理器（React 委派在根容器上）不会再收到这次点击。
    e.preventDefault();
    e.stopPropagation();
    e.stopImmediatePropagation();

    const limitMb = Math.round(gate.limitBytes / 1048576);
    console.info(
      `[pandre] 接管下载入口：${sel.files.length} 个文件，合计 ${total} 字节（${sel.from} 来源，` +
      `门槛 ${limitMb}MB ${gate.byTotal ? "按合计" : "按单文件"}）`);
    submitToPandre(sel.files, sel.skippedDirs,
      `（超过网页端 ${limitMb}MB 的下载会跳官方客户端，这里已直接交给 Pandre）`);
  }

  function onDocumentPointerDownCapture(e) {
    if (!SITE.downloadGate || !SITE.pageOk()) return;
    const el = e.target instanceof Element ? e.target : null;
    if (el) rememberRow(el);
  }

  document.addEventListener("pointerdown", onDocumentPointerDownCapture, true);
  document.addEventListener("click", onDocumentClickCapture, true);

  setInterval(refresh, 700);
};
