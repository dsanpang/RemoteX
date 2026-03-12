using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

using Renci.SshNet;
using Renci.SshNet.Sftp;

using WpfColor    = System.Windows.Media.Color;
using WpfBrush    = System.Windows.Media.SolidColorBrush;
using WpfHA       = System.Windows.HorizontalAlignment;
using WpfVA       = System.Windows.VerticalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfButton   = System.Windows.Controls.Button;
using WpfBorder   = System.Windows.Controls.Border;
using WpfStackPanel = System.Windows.Controls.StackPanel;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfTextBox  = System.Windows.Controls.TextBox;
using WpfTreeViewItem = System.Windows.Controls.TreeViewItem;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;


namespace RemoteX;

// DTO for file list items
internal sealed class SftpFileItem
{
    public string Name        { get; init; } = "";
    public string SizeDisplay { get; init; } = "";
    public string ModifyTime  { get; init; } = "";
    public string Permissions { get; init; } = "";
    public bool   IsDirectory { get; init; }
    public string FullPath    { get; init; } = "";
}

public partial class SftpBrowserWindow : Window
{
    private readonly ServerInfo       _server;
    private readonly SocksProxyEntry? _proxy;
    private SftpClient?               _sftp;
    private string                    _currentPath = "/";

    public SftpBrowserWindow(ServerInfo server, SocksProxyEntry? proxy = null)
    {
        InitializeComponent();
        _server = server;
        _proxy  = proxy;
        Title   = $"SFTP — {server.Name}";
    }

    // ── Connect on load ──────────────────────────────────────────────────────

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SetStatus("正在连接...");
        try
        {
            _sftp = await Task.Run(() =>
            {
                var conn   = BuildConnectionInfo();
                var client = new SftpClient(conn);
                client.Connect();
                return client;
            });
            SetStatus("已连接");
            await LoadRootTreeAsync();
            await NavigateToAsync("/");
        }
        catch (Exception ex)
        {
            SetStatus($"连接失败: {ex.Message}");
            AppMsg.Show(this, $"SFTP 连接失败：\n{ex.Message}", "错误", AppMsgIcon.Error);
        }
    }

    private ConnectionInfo BuildConnectionInfo()
    {
        AuthenticationMethod auth;
        if (!string.IsNullOrWhiteSpace(_server.SshPrivateKeyPath))
        {
            var pp = string.IsNullOrWhiteSpace(_server.Password) ? null : _server.Password;
            var kf = pp == null
                ? new PrivateKeyFile(_server.SshPrivateKeyPath)
                : new PrivateKeyFile(_server.SshPrivateKeyPath, pp);
            auth = new PrivateKeyAuthenticationMethod(_server.Username, kf);
        }
        else
        {
            auth = new PasswordAuthenticationMethod(_server.Username, _server.Password ?? "");
        }

        var methods = new[] { auth };

        if (_proxy is { Host.Length: > 0 })
        {
            return new ConnectionInfo(
                _server.IP, _server.Port, _server.Username,
                ProxyTypes.Socks5, _proxy.Host, _proxy.Port,
                string.IsNullOrWhiteSpace(_proxy.Username) ? null : _proxy.Username,
                string.IsNullOrWhiteSpace(_proxy.Password) ? null : _proxy.Password,
                methods);
        }

        return new ConnectionInfo(_server.IP, _server.Port, _server.Username, methods);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        try { _sftp?.Disconnect(); } catch { }
        try { _sftp?.Dispose(); }    catch { }
    }

    // ── Directory tree ───────────────────────────────────────────────────────

    private async Task LoadRootTreeAsync()
    {
        await Dispatcher.InvokeAsync(() => DirTree.Items.Clear());

        var rootItem = new WpfTreeViewItem
        {
            Header = "/",
            Tag    = "/"
        };
        rootItem.Items.Add(new WpfTreeViewItem { Header = "..." });

        await Dispatcher.InvokeAsync(() =>
        {
            DirTree.Items.Add(rootItem);
            rootItem.IsExpanded = true;
        });
    }

    private async void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfTreeViewItem item) return;
        if (item.Items.Count == 1 && item.Items[0] is WpfTreeViewItem dummy && dummy.Header?.ToString() == "...")
        {
            item.Items.Clear();
            var path = item.Tag as string ?? "/";
            await LoadTreeChildrenAsync(item, path);
        }
    }

    private async Task LoadTreeChildrenAsync(WpfTreeViewItem parentItem, string path)
    {
        if (_sftp == null) return;
        try
        {
            var dirs = await Task.Run(() =>
                _sftp.ListDirectory(path)
                    .Where(f => f.IsDirectory && f.Name != "." && f.Name != "..")
                    .OrderBy(f => f.Name)
                    .ToList());

            foreach (var dir in dirs)
            {
                var child = new WpfTreeViewItem
                {
                    Header = dir.Name,
                    Tag    = dir.FullName
                };
                child.Items.Add(new WpfTreeViewItem { Header = "..." });
                parentItem.Items.Add(child);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"sftp tree load error for {path}: {ex.Message}");
        }
    }

    private async void DirTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is WpfTreeViewItem item && item.Tag is string path)
            await NavigateToAsync(path);
    }

    // ── File list ────────────────────────────────────────────────────────────

    private async Task NavigateToAsync(string path)
    {
        if (_sftp == null) return;
        _currentPath = path;
        PathBar.Text = path;
        SetStatus($"正在加载 {path} ...");

        try
        {
            var files = await Task.Run(() =>
                _sftp.ListDirectory(path)
                    .Where(f => f.Name != "." && f.Name != "..")
                    .OrderBy(f => !f.IsDirectory)
                    .ThenBy(f => f.Name)
                    .Select(f => new SftpFileItem
                    {
                        Name        = f.Name + (f.IsDirectory ? "/" : ""),
                        SizeDisplay = f.IsDirectory ? "<DIR>" : FormatSize(f.Length),
                        ModifyTime  = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        Permissions = f.Attributes?.GetMode() ?? "",
                        IsDirectory = f.IsDirectory,
                        FullPath    = f.FullName
                    })
                    .ToList());

            FileList.ItemsSource = files;
            SetStatus($"{path}  ({files.Count} 项)");
        }
        catch (Exception ex)
        {
            SetStatus($"加载失败: {ex.Message}");
            AppMsg.Show(this, $"无法列出目录 {path}：\n{ex.Message}", "错误", AppMsgIcon.Error);
        }
    }

    private async void FileList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is SftpFileItem item && item.IsDirectory)
        {
            await NavigateToAsync(item.FullPath);
            ExpandTreeNode(item.FullPath);
        }
    }

    private void ExpandTreeNode(string path)
    {
        foreach (WpfTreeViewItem root in DirTree.Items)
        {
            if (ExpandNodeRecursive(root, path)) break;
        }
    }

    private static bool ExpandNodeRecursive(WpfTreeViewItem node, string targetPath)
    {
        if (node.Tag as string == targetPath)
        {
            node.IsSelected = true;
            node.BringIntoView();
            return true;
        }

        if (targetPath.StartsWith(node.Tag as string ?? "", StringComparison.OrdinalIgnoreCase))
        {
            node.IsExpanded = true;
            foreach (WpfTreeViewItem child in node.Items)
            {
                if (ExpandNodeRecursive(child, targetPath)) return true;
            }
        }
        return false;
    }

    // ── Toolbar actions ──────────────────────────────────────────────────────

    private async void BtnUpload_Click(object sender, RoutedEventArgs e)
    {
        if (_sftp == null) return;

        var dlg = new OpenFileDialog
        {
            Title       = "选择要上传的文件",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;

        foreach (var localPath in dlg.FileNames)
        {
            var fileName   = Path.GetFileName(localPath);
            var remotePath = _currentPath.TrimEnd('/') + "/" + fileName;
            SetProgress(true, $"正在上传 {fileName}...", 0);

            try
            {
                var fileInfo   = new FileInfo(localPath);
                long totalBytes = fileInfo.Length;

                await Task.Run(() =>
                {
                    using var fs = File.OpenRead(localPath);
                    _sftp.UploadFile(fs, remotePath, uploadCallback: bytesUploaded =>
                    {
                        var pct = totalBytes > 0 ? (int)((long)bytesUploaded * 100 / totalBytes) : 0;
                        Dispatcher.InvokeAsync(() => SetProgress(true, $"上传 {fileName}  {pct}%", pct));
                    });
                });

                SetStatus($"上传完成: {fileName}");
            }
            catch (Exception ex)
            {
                SetProgress(false);
                AppMsg.Show(this, $"上传失败 {fileName}：\n{ex.Message}", "错误", AppMsgIcon.Error);
                return;
            }
        }

        SetProgress(false);
        await NavigateToAsync(_currentPath);
    }

    private async void BtnDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_sftp == null) return;
        if (FileList.SelectedItem is not SftpFileItem item || item.IsDirectory)
        {
            AppMsg.Show(this, "请先选择一个文件", "提示", AppMsgIcon.Info);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title    = "保存文件到",
            FileName = item.Name
        };
        if (dlg.ShowDialog() != true) return;

        var localPath  = dlg.FileName;
        var remotePath = item.FullPath;

        SetProgress(true, $"正在下载 {item.Name}...", 0);

        try
        {
            await Task.Run(() =>
            {
                using var fs = File.Create(localPath);
                _sftp.DownloadFile(remotePath, fs, downloaded =>
                    Dispatcher.InvokeAsync(() =>
                        SetProgress(true, $"下载 {item.Name}  {downloaded} 字节", -1)));
            });

            SetProgress(false);
            SetStatus($"下载完成: {item.Name}  →  {localPath}");
            AppMsg.Show(this, $"下载完成：{localPath}", "完成", AppMsgIcon.Success);
        }
        catch (Exception ex)
        {
            SetProgress(false);
            AppMsg.Show(this, $"下载失败：\n{ex.Message}", "错误", AppMsgIcon.Error);
        }
    }

    private async void BtnMkDir_Click(object sender, RoutedEventArgs e)
    {
        if (_sftp == null) return;

        var name = PromptInput("新建文件夹", "请输入文件夹名称：", "新建文件夹");
        if (string.IsNullOrWhiteSpace(name)) return;

        var path = _currentPath.TrimEnd('/') + "/" + name.Trim();
        try
        {
            await Task.Run(() => _sftp.CreateDirectory(path));
            SetStatus($"已创建目录: {name}");
            await NavigateToAsync(_currentPath);
        }
        catch (Exception ex)
        {
            AppMsg.Show(this, $"创建失败：\n{ex.Message}", "错误", AppMsgIcon.Error);
        }
    }

    private async void BtnRename_Click(object sender, RoutedEventArgs e)
    {
        if (_sftp == null) return;
        if (FileList.SelectedItem is not SftpFileItem item)
        {
            AppMsg.Show(this, "请先选择一个文件或文件夹", "提示", AppMsgIcon.Info);
            return;
        }

        var oldName = item.Name.TrimEnd('/');
        var newName = PromptInput("重命名", $"将 \"{oldName}\" 重命名为：", oldName);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == oldName) return;

        var newPath = _currentPath.TrimEnd('/') + "/" + newName.Trim();
        try
        {
            await Task.Run(() => _sftp.RenameFile(item.FullPath, newPath));
            SetStatus($"已重命名: {oldName} → {newName.Trim()}");
            await NavigateToAsync(_currentPath);
        }
        catch (Exception ex)
        {
            AppMsg.Show(this, $"重命名失败：\n{ex.Message}", "错误", AppMsgIcon.Error);
        }
    }

    private async void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_sftp == null) return;
        if (FileList.SelectedItem is not SftpFileItem item)
        {
            AppMsg.Show(this, "请先选择一个文件或文件夹", "提示", AppMsgIcon.Info);
            return;
        }

        var name = item.Name.TrimEnd('/');
        var result = AppMsg.Show(this, $"确认删除「{name}」？此操作不可撤销。",
            "删除确认", AppMsgIcon.Warning, AppMsgButton.YesNo);
        if (result != AppMsgResult.Yes) return;

        try
        {
            await Task.Run(() =>
            {
                if (item.IsDirectory)
                    _sftp.DeleteDirectory(item.FullPath);
                else
                    _sftp.DeleteFile(item.FullPath);
            });
            SetStatus($"已删除: {name}");
            await NavigateToAsync(_currentPath);
        }
        catch (Exception ex)
        {
            AppMsg.Show(this, $"删除失败：\n{ex.Message}", "错误", AppMsgIcon.Error);
        }
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        await NavigateToAsync(_currentPath);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetStatus(string text) => TransferStatus.Text = text;

    private void SetProgress(bool visible, string? text = null, int value = 0)
    {
        TransferProgress.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (text  != null) TransferStatus.Text   = text;
        if (value >= 0)    TransferProgress.Value = value;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)             return $"{bytes} B";
        if (bytes < 1024 * 1024)      return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private string? PromptInput(string title, string label, string defaultValue = "")
    {
        var win = new Window
        {
            Title                 = title,
            Width                 = 360,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize,
            Background            = new WpfBrush(WpfColor.FromRgb(0x1E, 0x1E, 0x2E))
        };

        var sp = new WpfStackPanel { Margin = new Thickness(20, 16, 20, 20) };
        var lbl = new WpfTextBlock
        {
            Text       = label,
            Foreground = new WpfBrush(WpfColor.FromRgb(0xCD, 0xD6, 0xF4)),
            FontSize   = 12,
            Margin     = new Thickness(0, 0, 0, 8)
        };
        var tb = new WpfTextBox
        {
            Text                     = defaultValue,
            Background               = new WpfBrush(WpfColor.FromRgb(0x31, 0x32, 0x44)),
            Foreground               = new WpfBrush(WpfColor.FromRgb(0xCD, 0xD6, 0xF4)),
            BorderBrush              = new WpfBrush(WpfColor.FromRgb(0x45, 0x47, 0x5A)),
            BorderThickness          = new Thickness(1),
            CaretBrush               = new WpfBrush(WpfColor.FromRgb(0x89, 0xB4, 0xFA)),
            FontSize                 = 13,
            Height                   = 34,
            Padding                  = new Thickness(8, 0, 8, 0),
            VerticalContentAlignment = WpfVA.Center,
            Margin                   = new Thickness(0, 0, 0, 14)
        };

        var btnPanel = new WpfStackPanel
        {
            Orientation         = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHA.Right
        };
        var btnOk     = new WpfButton { Content = "确定", Width = 80, Height = 30, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var btnCancel = new WpfButton { Content = "取消", Width = 80, Height = 30, IsCancel  = true };

        string? result = null;
        btnOk.Click     += (_, __) => { result = tb.Text; win.DialogResult = true; };
        btnCancel.Click += (_, __) => { win.DialogResult = false; };

        btnPanel.Children.Add(btnOk);
        btnPanel.Children.Add(btnCancel);
        sp.Children.Add(lbl);
        sp.Children.Add(tb);
        sp.Children.Add(btnPanel);
        win.Content = sp;
        win.Loaded += (_, __) => { tb.Focus(); tb.SelectAll(); };

        return win.ShowDialog() == true ? result : null;
    }
}

// Extension to get permission string from SftpFileAttributes
internal static class SftpAttributesExtensions
{
    public static string GetMode(this SftpFileAttributes attrs)
    {
        try
        {
            var s = new StringBuilder(10);
            s.Append(attrs.IsDirectory ? 'd' : attrs.IsSymbolicLink ? 'l' : '-');
            s.Append(attrs.OwnerCanRead    ? 'r' : '-');
            s.Append(attrs.OwnerCanWrite   ? 'w' : '-');
            s.Append(attrs.OwnerCanExecute ? 'x' : '-');
            s.Append(attrs.GroupCanRead    ? 'r' : '-');
            s.Append(attrs.GroupCanWrite   ? 'w' : '-');
            s.Append(attrs.GroupCanExecute ? 'x' : '-');
            s.Append(attrs.OthersCanRead    ? 'r' : '-');
            s.Append(attrs.OthersCanWrite   ? 'w' : '-');
            s.Append(attrs.OthersCanExecute ? 'x' : '-');
            return s.ToString();
        }
        catch
        {
            return "";
        }
    }
}
