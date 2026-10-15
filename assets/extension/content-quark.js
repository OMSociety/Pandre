// Pandre 接管 · 夸克网盘（drive.quark.cn）
// 选择锚点：Ant Design 表格勾选框（input.ant-checkbox-input:checked），行数据在 React fiber 的 props 里。
globalThis.__pandreBoot({
  provider: "quark",
  idKey: "fid",
  nameKeys: ["file_name", "fileName", "name"],
  sizeKeys: ["size", "file_size"],
  isDir: (p) => p.dir === true || p.is_dir === true || p.isdir === 1 || p.isdir === true,
  pageOk: () => location.pathname.startsWith("/list"),
  checkedNodes: () => [...document.querySelectorAll("input.ant-checkbox-input:checked")],
  rowOf: (n) => n.closest("tr.ant-table-row") || n.closest("tr") || n.closest('[class*="row"]') || n,
});
