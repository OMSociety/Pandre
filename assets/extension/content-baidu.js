// Pandre 接管 · 百度网盘（pan.baidu.com）
// 选择锚点：勾选的 checkbox + 带 selected 样式的行；行数据（fs_id/server_filename/size）在 React fiber 的 props 里。
// 只在自己的网盘页（/disk/*）出现；分享页（/s/、/share/*）不接管——转存走应用内的「转存」。
globalThis.__pandreBoot({
  provider: "baidu",
  idKey: "fs_id",
  nameKeys: ["server_filename", "filename", "name"],
  sizeKeys: ["size", "file_size"],
  isDir: (p) => p.isdir === 1 || p.isdir === true || p.is_dir === true || p.dir === true,
  pageOk: () => location.pathname.startsWith("/disk"),
  checkedNodes: () => {
    const set = new Set();
    document.querySelectorAll('input[type="checkbox"]:checked').forEach((n) => set.add(n));
    document
      .querySelectorAll(".wp-s-pan-list [class*='selected'], tr[class*='selected'], dd[class*='selected'], li[class*='selected']")
      .forEach((n) => set.add(n));
    return [...set];
  },
  rowOf: (n) =>
    n.closest("tr") ||
    n.closest('[class*="file-item"]') ||
    n.closest('[class*="selected"]') ||
    n.closest("li") ||
    n,
});
