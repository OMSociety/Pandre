// Pandre 接管 · 夸克网盘（drive.quark.cn）
// 选择锚点：Ant Design 表格勾选框（input.ant-checkbox-input:checked），行数据在 React fiber 的 props 里。
globalThis.__pandreBoot({
  provider: "quark",
  idKey: "fid",
  nameKeys: ["file_name", "fileName", "name"],
  sizeKeys: ["size", "file_size"],
  isDir: (p) => p.dir === true || p.is_dir === true || p.isdir === 1 || p.isdir === true,
  pageOk: () => location.pathname.startsWith("/list"),
  // 下载入口接管：网页端对 100MB 以上文件的下载答 23018（Web 端 UA 门，见开发文档 §4），
  // 单个文件过 100MB 时拦下来直接交 Pandre。
  downloadGate: {
    labels: ["下载", "下载文件"],
    limitBytes: 100 * 1024 * 1024,
    byTotal: false,
  },
  checkedNodes: () => [...document.querySelectorAll("input.ant-checkbox-input:checked")],
  rowOf: (n) => n.closest("tr.ant-table-row") || n.closest("tr") || n.closest('[class*="row"]') || n,
  // 行级判定收紧：只有 ant-table-row 才当作文件行（右键菜单挂在表格行上，菜单项本身不算）。
  rowOfStrict: (n) => n.closest("tr.ant-table-row"),
});
