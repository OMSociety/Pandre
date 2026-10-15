using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pandre.Core;
using Pandre.Desktop.Services;

namespace Pandre.Desktop.ViewModels;

/// <summary>文件列表的一行（PanFile + 展示辅助 + 缩略图懒加载）。</summary>
public partial class FileRowViewModel : ViewModelBase
{
    public FileRowViewModel(PanFile file)
    {
        File = file;
        SizeText = file.IsDirectory ? "—" : Fmt.Size(file.Size);
        ModifiedText = file.ModifiedAt?.ToString("yyyy-MM-dd HH:mm") ?? "";
        if (file.ThumbnailUrl != null) _ = LoadThumbAsync(file.ThumbnailUrl);
    }

    public PanFile File { get; }
    public string SizeText { get; }
    public string ModifiedText { get; }

    /// <summary>目录用 Folder、文件用 Document（Fluent System Icons）。</summary>
    public string Icon => File.IsDirectory ? AppIcons.Folder : AppIcons.Document;

    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _thumb;

    private async Task LoadThumbAsync(string url)
    {
        try { Thumb = await ThumbCache.GetAsync(url); }
        catch { /* 缩略图失败不影响列表 */ }
    }
}

/// <summary>分享管理弹窗里的一行（一次分享记录）。</summary>
public partial class ShareRowViewModel : ViewModelBase
{
    private readonly Func<ShareRowViewModel, Task> _cancel;

    public ShareRowViewModel(PanShare share, Func<ShareRowViewModel, Task> cancel)
    {
        Share = share;
        _cancel = cancel;
    }

    public PanShare Share { get; }
    public string Title => Share.Title;
    public string UrlText => Share.ShareUrl;
    public string TimeText => Share.CreatedAt == DateTimeOffset.MinValue
        ? "—" : Share.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    public string CancelText => IsConfirmingCancel ? "确认取消？" : "取消";

    /// <summary>两步防误点：第一次点「取消」变「确认取消？」，第二次才真正取消。</summary>
    [ObservableProperty] private bool _isConfirmingCancel;
    partial void OnIsConfirmingCancelChanged(bool value) => OnPropertyChanged(nameof(CancelText));

    [RelayCommand]
    private async Task CancelShare() => await _cancel(this);
}

/// <summary>单个网盘的浏览面板：导航 + 搜索 + 选择下载 + P2 管理/上传/分享/转存/离线。</summary>
public partial class PanViewModel : ViewModelBase
{
    private readonly PanProvider _provider;
    private readonly DownloadsViewModel _downloads;

    // 目录栈（root=null 表示根目录）
    private readonly System.Collections.Generic.List<PanFile?> _dirStack = new() { null };
    private bool _isSearchMode; // 当前列表是搜索结果（无父目录，Up 退回目录列表）

    public PanViewModel(PanProvider provider, DownloadsViewModel downloads)
    {
        _provider = provider;
        _downloads = downloads;
        SelectedFiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasSingleSelection));
        };
        // 功能开关在设置页改动后，本页工具栏可见性要跟着变
        AppServices.FeatureFlagsChanged += OnFeatureFlagsChanged;
    }

    private void OnFeatureFlagsChanged()
    {
        OnPropertyChanged(nameof(ShowSaveShareButton));
        OnPropertyChanged(nameof(ShowOfflineDownloadButton));
        OnPropertyChanged(nameof(ShowShareManageButton));
    }

    /// <summary>「转存」按钮可见性（全局开关）。</summary>
    public bool ShowSaveShareButton => AppServices.ShowSaveShare;
    /// <summary>「离线下载」按钮可见性（全局开关 + 仅百度支持）。</summary>
    public bool ShowOfflineDownloadButton => AppServices.ShowOfflineDownload && IsBaidu;
    /// <summary>「分享管理」按钮可见性（全局开关）。</summary>
    public bool ShowShareManageButton => AppServices.ShowShareManage;

    public string Title => _provider == PanProvider.Baidu ? "百度网盘" : "夸克网盘";
    /// <summary>离线下载仅百度支持。</summary>
    public bool IsBaidu => _provider == PanProvider.Baidu;
    public bool HasSelection => SelectedFiles.Count > 0;
    public bool HasSingleSelection => SelectedFiles.Count == 1;

    public ObservableCollection<FileRowViewModel> Files { get; } = new();
    public ObservableCollection<FileRowViewModel> SelectedFiles { get; } = new();

    [ObservableProperty] private string _pathText = "/";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _canGoUp;

    private bool IsLoggedIn => _provider == PanProvider.Baidu
        ? AppServices.Baidu.IsAuthenticated : AppServices.Quark.IsAuthenticated;

    private IPanClient Client => _provider == PanProvider.Baidu
        ? (IPanClient)AppServices.Baidu : AppServices.Quark;

    /// <summary>进入此页面时（登录态可能刚变化）。</summary>
    public async Task ActivateAsync()
    {
        if (!IsLoggedIn)
        {
            StatusMessage = "未登录：点右上角「扫码登录」";
            Files.Clear();
            return;
        }
        if (Files.Count == 0) await RefreshAsync();
    }

    /// <summary>外部触发刷新（例如刚扫码登录成功）。</summary>
    public Task ReloadAsync() => LoadCurrentAsync();

    [RelayCommand]
    private async Task RefreshAsync() => await LoadCurrentAsync();

    [RelayCommand]
    private async Task UpAsync()
    {
        if (_isSearchMode)
        {
            // 搜索结果的「上一级」= 退回目录列表（搜索结果没有父目录；
            // 此前 CanGoUp=true 与栈深度守卫脱节，点「上一级」无动作）
            _isSearchMode = false;
            await LoadCurrentAsync();
            return;
        }
        if (_dirStack.Count > 1)
        {
            _dirStack.RemoveAt(_dirStack.Count - 1);
            await LoadCurrentAsync();
        }
    }

    [RelayCommand]
    private async Task OpenAsync(FileRowViewModel? row)
    {
        if (row == null || !row.File.IsDirectory) return;
        _dirStack.Add(row.File);
        await LoadCurrentAsync();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText)) { await LoadCurrentAsync(); return; }
        if (!IsLoggedIn) { StatusMessage = "未登录"; return; }
        IsLoading = true;
        StatusMessage = $"搜索「{SearchText}」…";
        try
        {
            var items = await Client.SearchAsync(SearchText.Trim(), CancellationToken.None);
            RebuildRows(items);
            PathText = $"搜索: {SearchText}";
            _isSearchMode = true;
            CanGoUp = true;
            StatusMessage = $"共 {items.Count} 条结果";
        }
        catch (PanException ex) { StatusMessage = $"搜索失败：{ex.Message}"; }
        catch (Exception ex) { StatusMessage = $"搜索异常：{ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        var files = SelectedFiles.Select(r => r.File).Where(f => !f.IsDirectory).ToArray();
        if (files.Length == 0) { StatusMessage = "请先勾选要下载的文件"; return; }
        if (!IsLoggedIn) { StatusMessage = "未登录"; return; }
        IsLoading = true;
        StatusMessage = "正在取下载链…";
        try
        {
            var links = await Client.GetDownloadLinksAsync(files);
            var dir = AppServices.DownloadDir;
            foreach (var l in links)
                await AppServices.Downloads.EnqueueAsync(l, dir);
            StatusMessage = $"已加入下载：{links.Count} 个文件 → {dir}";
            _downloads.Refresh();
        }
        catch (PanException ex) { StatusMessage = $"取下载链失败：{ex.Message}"; }
        catch (Exception ex) { StatusMessage = $"下载加入失败：{ex.Message}"; }
        finally { IsLoading = false; }
    }

    private async Task LoadCurrentAsync()
    {
        if (!IsLoggedIn) { StatusMessage = "未登录"; return; }
        IsLoading = true;
        StatusMessage = "加载中…";
        try
        {
            var dir = _dirStack[^1];
            var items = await Client.ListAsync(dir, CancellationToken.None);
            RebuildRows(items);
            PathText = _dirStack.Count <= 1 ? "/" : "/ " + string.Join(" / ", _dirStack.Skip(1).Select(d => d?.Name ?? "?"));
            _isSearchMode = false;
            CanGoUp = _dirStack.Count > 1;
            StatusMessage = $"共 {items.Count} 项";
        }
        catch (PanException ex) when (ex.IsAuthExpired)
        {
            StatusMessage = "登录已过期，点右上角重新扫码";
            Files.Clear();
        }
        catch (PanException ex) { StatusMessage = $"加载失败：{ex.Message}"; }
        catch (Exception ex) { StatusMessage = $"加载异常：{ex.Message}"; }
        finally { IsLoading = false; }
    }

    private void RebuildRows(IReadOnlyList<PanFile> items)
    {
        Files.Clear();
        // 目录排前、文件名排序（服务端已排，但搜索/合并时保底）
        foreach (var f in items.OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name))
            Files.Add(new FileRowViewModel(f));
    }

    // ============ P2：上传 / 管理 / 分享 / 转存 / 离线 ============

    [ObservableProperty] private bool _isUploading;
    [ObservableProperty] private string _uploadProgressText = "";

    /// <summary>上传若干本地文件到当前目录（由视图文件选择器驱动，逐个上传并刷新）。</summary>
    public async Task UploadFilesAsync(IReadOnlyList<string> paths)
    {
        if (!IsLoggedIn) { StatusMessage = "未登录"; return; }
        if (paths.Count == 0) return;
        IsUploading = true;
        try
        {
            for (var i = 0; i < paths.Count; i++)
            {
                var name = System.IO.Path.GetFileName(paths[i]);
                var progress = new Progress<PanUploadProgress>(p =>
                    UploadProgressText = $"[{i + 1}/{paths.Count}] {name}：{p.Phase} " +
                        (p.TotalBytes > 0 ? $"{p.DoneBytes * 100 / Math.Max(p.TotalBytes, 1)}%" : ""));
                var result = await Client.UploadAsync(paths[i], _dirStack[^1], PanConflictPolicy.Skip, progress);
                UploadProgressText = result.Rapid
                    ? $"[{i + 1}/{paths.Count}] {name}：秒传命中"
                    : $"[{i + 1}/{paths.Count}] {name}：完成";
            }
            StatusMessage = $"上传完成：{paths.Count} 个文件";
            await LoadCurrentAsync();
        }
        catch (PanException ex) { StatusMessage = $"上传失败：{ex.Message}"; }
        catch (Exception ex) { StatusMessage = $"上传异常：{ex.Message}"; }
        finally { IsUploading = false; UploadProgressText = ""; }
    }

    // ---- 伪弹窗（输入/确认/展示/分享列表 四态复用） ----
    public enum PanDialogKind { Input, TwoInput, Confirm, Text, Shares }

    [ObservableProperty] private bool _isDialogOpen;
    [ObservableProperty] private PanDialogKind _dialogKind;
    [ObservableProperty] private string _dialogTitle = "";
    [ObservableProperty] private string _dialogInput = "";
    [ObservableProperty] private string _dialogInput2 = "";
    [ObservableProperty] private string _dialogText = "";
    [ObservableProperty] private string _dialogInputWatermark = "";
    [ObservableProperty] private string _dialogInput2Watermark = "";
    /// <summary>分享管理弹窗的行集合。</summary>
    public ObservableCollection<ShareRowViewModel> DialogShares { get; } = new();
    public bool DialogHasInput => DialogKind is PanDialogKind.Input or PanDialogKind.TwoInput;
    public bool DialogHasInput2 => DialogKind is PanDialogKind.TwoInput;
    public bool DialogHasText => DialogKind is PanDialogKind.Text;
    public bool DialogIsConfirm => DialogKind is PanDialogKind.Confirm;
    public bool DialogIsShares => DialogKind is PanDialogKind.Shares;
    /// <summary>Text 与 Confirm 两种弹窗都展示正文。</summary>
    public bool DialogShowText => DialogKind is PanDialogKind.Text or PanDialogKind.Confirm;
    /// <summary>「确定」按钮只在有提交动作的弹窗显示（列表型没有）。</summary>
    public bool DialogHasConfirm => DialogKind is not PanDialogKind.Shares;

    private Func<string, string, Task>? _dialogAction;

    partial void OnDialogKindChanged(PanDialogKind value)
    {
        OnPropertyChanged(nameof(DialogHasInput));
        OnPropertyChanged(nameof(DialogHasInput2));
        OnPropertyChanged(nameof(DialogHasText));
        OnPropertyChanged(nameof(DialogIsConfirm));
        OnPropertyChanged(nameof(DialogIsShares));
        OnPropertyChanged(nameof(DialogHasConfirm));
        OnPropertyChanged(nameof(DialogShowText));
    }

    private void OpenDialog(PanDialogKind kind, string title, string text = "",
        string wm1 = "", string wm2 = "", string input1 = "",
        Func<string, string, Task>? action = null)
    {
        DialogKind = kind; DialogTitle = title; DialogText = text;
        DialogInputWatermark = wm1; DialogInput2Watermark = wm2;
        DialogInput = input1; DialogInput2 = "";
        _dialogAction = action;
        IsDialogOpen = true;
    }

    [RelayCommand]
    private async Task DialogConfirmAsync()
    {
        var action = _dialogAction;
        var i1 = DialogInput; var i2 = DialogInput2;
        _dialogAction = null; IsDialogOpen = false;
        if (action == null) return;
        try { await action(i1, i2); }
        catch (PanException ex) { StatusMessage = ex.Message; }
        catch (Exception ex) { StatusMessage = $"操作异常：{ex.Message}"; }
    }

    [RelayCommand]
    private void DialogCancel() { _dialogAction = null; IsDialogOpen = false; }

    // ---- 各操作入口 ----

    [RelayCommand]
    private void NewFolder() => OpenDialog(PanDialogKind.Input, "新建文件夹", wm1: "文件夹名称",
        action: async (name, _) =>
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            await Client.CreateFolderAsync(_dirStack[^1], name.Trim());
            await LoadCurrentAsync();
            StatusMessage = $"已新建「{name.Trim()}」";
        });

    [RelayCommand]
    private void RenameSelected()
    {
        var row = SelectedFiles.FirstOrDefault();
        if (row == null) return;
        var file = row.File;
        OpenDialog(PanDialogKind.Input, "重命名", wm1: "新名称", input1: file.Name,
            action: async (name, _) =>
            {
                if (string.IsNullOrWhiteSpace(name) || name.Trim() == file.Name) return;
                await Client.RenameAsync(file, name.Trim());
                await LoadCurrentAsync();
                StatusMessage = $"已重命名为「{name.Trim()}」";
            });
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        var files = SelectedFiles.Select(r => r.File).ToArray();
        if (files.Length == 0) return;
        OpenDialog(PanDialogKind.Confirm, "删除确认",
            text: $"将删除 {files.Length} 项（移入回收站）：\n" +
                  string.Join("\n", files.Take(5).Select(f => "· " + f.Name)) +
                  (files.Length > 5 ? $"\n… 等共 {files.Length} 项" : ""),
            action: async (_, _) =>
            {
                await Client.DeleteAsync(files);
                await LoadCurrentAsync();
                StatusMessage = $"已删除 {files.Length} 项";
            });
    }

    [RelayCommand]
    private void ShareSelected()
    {
        var files = SelectedFiles.Select(r => r.File).ToArray();
        if (files.Length == 0) { StatusMessage = "请先勾选要分享的文件"; return; }
        if (!IsLoggedIn) { StatusMessage = "未登录"; return; }
        var folders = files.Count(f => f.IsDirectory);
        var title = files[0].Name + (files.Length > 1 ? $" 等{files.Length}项" : "");
        OpenDialog(PanDialogKind.TwoInput, "创建分享",
            wm1: "提取码（4 位字母/数字，留空随机生成）",
            wm2: "有效期（留空/0=永久；1/7/30 天）",
            action: async (pwd, expire) =>
            {
                var code = pwd.Trim();
                if (code.Length > 0)
                {
                    if (code.Length != 4 || !System.Text.RegularExpressions.Regex.IsMatch(code, "^[a-zA-Z0-9]{4}$"))
                    {
                        StatusMessage = "提取码需为 4 位字母或数字，已取消";
                        return;
                    }
                    code = code.ToLowerInvariant();
                }
                var expireText = expire.Trim();
                var expireDays = expireText.Length == 0 ? 0 :
                    int.TryParse(expireText, out var d) ? d : -1;
                if (expireDays is not (0 or 1 or 7 or 30))
                {
                    StatusMessage = "有效期只能是 0(永久)/1/7/30 天，已取消";
                    return;
                }
                IsLoading = true; StatusMessage = "正在创建分享…";
                try
                {
                    var share = await Client.CreateShareAsync(
                        new PanShareOptions(files, title, expireDays, code.Length == 0 ? null : code));
                    var url = share.Passcode != null && !share.ShareUrl.Contains("?pwd=")
                        ? $"{share.ShareUrl}?pwd={share.Passcode}" : share.ShareUrl;
                    var period = expireDays == 0 ? "永久有效" : $"有效期 {expireDays} 天";
                    OpenDialog(PanDialogKind.Text, $"分享链接（{period}）", text: url);
                    StatusMessage = "分享已创建";
                }
                catch (PanException ex) { StatusMessage = $"创建分享失败：{ex.Message}"; }
                catch (Exception ex) { StatusMessage = $"分享异常：{ex.Message}"; }
                finally { IsLoading = false; }
            });
    }

    [RelayCommand]
    private async Task ShareManageAsync()
    {
        if (!IsLoggedIn) { StatusMessage = "未登录"; return; }
        IsLoading = true; StatusMessage = "正在获取分享列表…";
        try
        {
            var shares = await Client.ListSharesAsync();
            DialogShares.Clear();
            foreach (var s in shares) DialogShares.Add(new ShareRowViewModel(s, row => CancelShareAsync(row)));
            OpenDialog(PanDialogKind.Shares, "我的分享");
            StatusMessage = $"共 {shares.Count} 条分享";
        }
        catch (PanException ex) { StatusMessage = ex.Message; }
        catch (Exception ex) { StatusMessage = $"取分享列表异常：{ex.Message}"; }
        finally { IsLoading = false; }
    }

    /// <summary>两步取消：第一次只把按钮变「确认取消？」，第二次真正调接口。由 ShareRowViewModel 回掉触发。</summary>
    private async Task CancelShareAsync(ShareRowViewModel row)
    {
        if (!row.IsConfirmingCancel) { row.IsConfirmingCancel = true; return; }
        try
        {
            await Client.DeleteSharesAsync([row.Share.ShareId]);
            DialogShares.Remove(row);
            StatusMessage = $"已取消分享「{row.Share.Title}」";
        }
        catch (PanException ex) { StatusMessage = ex.Message; row.IsConfirmingCancel = false; }
        catch (Exception ex) { StatusMessage = $"取消分享异常：{ex.Message}"; row.IsConfirmingCancel = false; }
    }

    [RelayCommand]
    private void SaveShare() => OpenDialog(PanDialogKind.TwoInput, "转存分享链接",
        wm1: "夸克链接 / 百度链接（/s/1… 或 share/init?surl=…）",
        wm2: "提取码（链接含 pwd 可留空）",
        action: async (url, pwd) =>
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var link = url.Trim();
            var code = pwd.Trim();
            // 提取码留空时从链接里取：百度两种形态都出现在 URL 里（/s/xxx?pwd= 与
            // share/init?surl=xxx&pwd=，后者是网页「复制链接」的实际形态，pwd 前是 & 不是 ?）
            if (code.Length == 0)
            {
                var mp = System.Text.RegularExpressions.Regex.Match(link, @"[?&]pwd=([^&\s]+)");
                if (mp.Success) code = mp.Groups[1].Value;
            }
            var res = await Client.SaveShareAsync(link, code.Length == 0 ? null : code, _dirStack[^1]);
            await LoadCurrentAsync();
            StatusMessage = $"已转存 {res.SavedCount} 项 → {res.SavedToDirName}";
        });

    [RelayCommand]
    private void OfflineDownload() => OpenDialog(PanDialogKind.Input, "离线下载（百度）",
        wm1: "magnet / ed2k 链接",
        action: async (url, _) =>
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var t = await Client.AddOfflineTaskAsync(url.Trim(), _dirStack[^1]);
            StatusMessage = $"已添加离线任务（id={t.TaskId}），完成后会出现在 {t.SavePath}";
        });
}
