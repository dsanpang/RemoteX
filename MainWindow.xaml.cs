using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Color       = System.Windows.Media.Color;

using Application = System.Windows.Application;
using Orientation = System.Windows.Controls.Orientation;
using Button      = System.Windows.Controls.Button;
using Brushes     = System.Windows.Media.Brushes;
using Cursors     = System.Windows.Input.Cursors;

namespace RemoteX
{
    public partial class MainWindow : Window
    {
        // 数据
        private List<ServerInfo> _servers = new();
        private ListCollectionView _serverView = null!;
        private readonly ServerRepository _serverRepository = new();
        private readonly UiStateStore _uiStateStore = new();
        private readonly ConnectionHealthService _connectionHealthService = new();
        private AppUiState  _uiState    = new();
        private AppSettings _appSettings = null!;

        // 多会话（键为 instanceId，允许同一服务器开多个会话）
        private readonly Dictionary<long, ITabSession> _tabSessions = new();
        private long _activeTabId;

        // 批量健康检测取消令牌
        private CancellationTokenSource? _healthCheckCts;

        // 防抖计时器
        private readonly System.Windows.Threading.DispatcherTimer _resizeTimer = new()
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        private readonly System.Windows.Threading.DispatcherTimer _searchDebounceTimer = new()
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        private readonly System.Windows.Threading.DispatcherTimer _toastTimer = new()
        {
            Interval = TimeSpan.FromSeconds(3)
        };

        // 状态
        private bool _isHealthChecking;
        private bool _isBulkHealthChecking;
        private bool _syncDisplaySettingsRequested;
        private WindowState _lastWindowState;

        // 侧边栏 / 功能面板
        private bool _isSidebarCollapsed;
        private GridLength _sidebarExpandedWidth = new(268);
        private int _activePanelIndex = 0;  // 0=Servers 1=Recent 2=Forwards 3=Commands 4=Sftp

        // 拖拽排序
        private System.Windows.Point _dragStartPoint;

        // Catppuccin Mocha 调色板
        private static SolidColorBrush Br(byte r, byte g, byte b)
            => new(Color.FromRgb(r, g, b));

        internal static readonly SolidColorBrush ColBg       = Br(0x1E, 0x1E, 0x2E);
        internal static readonly SolidColorBrush ColSurface0 = Br(0x31, 0x32, 0x44);
        internal static readonly SolidColorBrush ColSurface1 = Br(0x45, 0x47, 0x5A);
        internal static readonly SolidColorBrush ColText     = Br(0xCD, 0xD6, 0xF4);
        internal static readonly SolidColorBrush ColSubtext  = Br(0xA6, 0xAD, 0xC8);
        internal static readonly SolidColorBrush ColBlue     = Br(0x89, 0xB4, 0xFA);
        internal static readonly SolidColorBrush ColGreen    = Br(0xA6, 0xE3, 0xA1);
        internal static readonly SolidColorBrush ColOverlay0 = Br(0x6C, 0x70, 0x86);

        public MainWindow() : this((Application.Current is App a ? a.AppSettings : null) ?? AppSettings.Load()) { }

        public MainWindow(AppSettings appSettings)
        {
            _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
            InitializeComponent();
            TrySetWindowIcon();
            _uiState = _uiStateStore.Load();
            ApplySavedUiState();
            UpdateDisconnectedState();
            _lastWindowState = WindowState;
            Loaded       += OnWindowLoaded;
            Closed       += OnWindowClosed;
            StateChanged += MainWindow_StateChanged;
        }

        private void TrySetWindowIcon()
        {
            try
            {
                var icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (!File.Exists(icoPath)) return;
                var uri = new Uri(icoPath, UriKind.Absolute);
                Icon = BitmapFrame.Create(uri);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"set window icon: {ex.Message}");
            }
        }

        private async void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                AppLogger.Info("main window loaded");
                RdpContainer.SizeChanged += RdpContainer_SizeChanged;
                _resizeTimer.Tick        += ResizeTimer_Tick;
                _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;
                _toastTimer.Tick         += (_, __) => { _toastTimer.Stop(); ToastPanel.Visibility = Visibility.Collapsed; };

                // 动态版本号
                var asm  = System.Reflection.Assembly.GetExecutingAssembly();
                var ver  = asm.GetName().Version;
                AppVersionText.Text = ver != null
                    ? $"RemoteX  v{ver.Major}.{ver.Minor}"
                    : "RemoteX";

                await Task.WhenAll(LoadServersAsync(), InitTerminalWebViewAsync());
                RestoreLastSelectedServer();
                RefreshRecentSection();
                await Task.WhenAll(
                    LoadPortForwardsAsync(),
                    LoadQuickCommandsAsync()
                );
            }
            catch (Exception ex)
            {
                AppLogger.Error("main window initialization failed", ex);
                AppMsg.Show(this, "初始化失败 " + ex.Message, "错误", AppMsgIcon.Error);
            }
        }

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            SaveUiState();
            _resizeTimer.Stop();
            _isHealthChecking = _isBulkHealthChecking = _syncDisplaySettingsRequested = false;
            _healthCheckCts?.Cancel();
            _healthCheckCts?.Dispose();
            DisposeAllConnectTimeouts();
            // 取消订阅 WebView2 消息事件，防止窗口关闭后事件回调持有对象引用
            try
            {
                if (_webViewReady && TerminalWebView.CoreWebView2 != null)
                    TerminalWebView.CoreWebView2.WebMessageReceived -= OnTerminalWebMessageReceived;
            }
            catch { }
            try
            {
                foreach (var tab in _tabSessions.Values)
                    tab.Dispose();
                _tabSessions.Clear();
            }
            catch { }
            AppLogger.Info("main window closed");
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (GetCurrentTabSession() is not RdpTabSession { IsConnected: true })
            {
                _lastWindowState = WindowState;
                return;
            }

            if (WindowState == _lastWindowState) return;
            _lastWindowState = WindowState;

            _syncDisplaySettingsRequested = true;
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }

        // UI 状态
        private void ApplySavedUiState()
        {
            Width  = Math.Max(MinWidth,  _uiState.WindowWidth);
            Height = Math.Max(MinHeight, _uiState.WindowHeight);

            if (double.IsFinite(_uiState.WindowLeft) && double.IsFinite(_uiState.WindowTop))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _uiState.WindowLeft;
                Top  = _uiState.WindowTop;
            }

            _sidebarExpandedWidth = new GridLength(Math.Max(180, _uiState.SidebarExpandedWidth));
            _activePanelIndex = _uiState.ActivePanelIndex;
            SetSidebarCollapsed(_uiState.IsSidebarCollapsed, persistState: false, requestDisplaySync: false);

            // Restore active panel visibility and highlight without toggling
            FP_Servers.Visibility  = _activePanelIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            FP_Recent.Visibility   = _activePanelIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            FP_Forwards.Visibility = _activePanelIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            FP_Commands.Visibility = _activePanelIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
            FP_Sftp.Visibility     = _activePanelIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
            UpdateActivityBarHighlight(_isSidebarCollapsed ? -1 : _activePanelIndex);

            if (_uiState.IsMaximized)
                WindowState = WindowState.Maximized;
        }

        private void SaveUiState()
        {
            try
            {
                var bounds = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, Width, Height)
                    : RestoreBounds;

                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    _uiState.WindowWidth  = bounds.Width;
                    _uiState.WindowHeight = bounds.Height;
                    _uiState.WindowLeft   = bounds.Left;
                    _uiState.WindowTop    = bounds.Top;
                }

                _uiState.IsMaximized          = WindowState == WindowState.Maximized;
                _uiState.IsSidebarCollapsed   = _isSidebarCollapsed;
                _uiState.SidebarExpandedWidth = Math.Max(180, _sidebarExpandedWidth.Value);
                _uiState.LastSelectedServerId = ServerList.SelectedItem is ServerInfo s ? s.Id : 0;
                _uiState.ActivePanelIndex     = _activePanelIndex;
                _uiStateStore.Save(_uiState);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"save ui state failed: {ex.Message}");
            }
        }

        private void RestoreLastSelectedServer()
        {
            if (_uiState.LastSelectedServerId <= 0) return;
            var selected = _servers.Find(s => s.Id == _uiState.LastSelectedServerId);
            if (selected == null) return;
            ServerList.SelectedItem = selected;
            ServerList.ScrollIntoView(selected);
        }

        // 侧边栏

        private void SetSidebarCollapsed(
            bool collapsed,
            bool persistState = true,
            bool requestDisplaySync = true)
        {
            if (collapsed)
            {
                if (FeaturePanelColumn.Width.Value > 0)
                    _sidebarExpandedWidth = FeaturePanelColumn.Width;
                FeaturePanelColumn.Width = new GridLength(0);
                _isSidebarCollapsed      = true;
                ToggleSidebarIcon.Text   = "▶";
                ToggleSidebarButton.ToolTip = "展开功能面板";
            }
            else
            {
                if (_sidebarExpandedWidth.Value <= 0)
                    _sidebarExpandedWidth = new GridLength(268);
                FeaturePanelColumn.Width = _sidebarExpandedWidth;
                _isSidebarCollapsed      = false;
                ToggleSidebarIcon.Text   = "◀";
                ToggleSidebarButton.ToolTip = "收起功能面板";
            }

            if (persistState)
            {
                _uiState.IsSidebarCollapsed   = _isSidebarCollapsed;
                _uiState.SidebarExpandedWidth = Math.Max(180, _sidebarExpandedWidth.Value);
            }

            if (requestDisplaySync && GetCurrentTabSession() is RdpTabSession { IsConnected: true })
            {
                _syncDisplaySettingsRequested = true;
                _resizeTimer.Stop();
                _resizeTimer.Start();
            }
        }

        private void ToggleSidebarButton_Click(object sender, RoutedEventArgs e)
        {
            var collapsing = !_isSidebarCollapsed;
            SetSidebarCollapsed(collapsing);
            UpdateActivityBarHighlight(collapsing ? -1 : _activePanelIndex);
        }

        private void ExitSplitButton_Click(object sender, RoutedEventArgs e)
        {
            ExitSplitMode();
        }

        // 活动栏面板切换

        private void SwitchActivityPanel(int panelIndex)
        {
            // 点击已激活面板且未折叠 → 折叠（VS Code 行为）
            if (_activePanelIndex == panelIndex && !_isSidebarCollapsed)
            {
                SetSidebarCollapsed(true);
                UpdateActivityBarHighlight(-1);
                return;
            }
            if (_isSidebarCollapsed) SetSidebarCollapsed(false);
            _activePanelIndex = panelIndex;
            FP_Servers.Visibility  = panelIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            FP_Recent.Visibility   = panelIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            FP_Forwards.Visibility = panelIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            FP_Commands.Visibility = panelIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
            FP_Sftp.Visibility     = panelIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
            if (panelIndex == 1) RefreshRecentPanel();
            if (panelIndex == 2) RefreshForwardsPanel();
            if (panelIndex == 3) RefreshCommandsPanel();
            if (panelIndex == 4) RefreshSftpPanel();
            UpdateActivityBarHighlight(panelIndex);
        }

        private void UpdateActivityBarHighlight(int activeIdx)
        {
            var buttons = new[] { ActivityBtn_Servers, ActivityBtn_Recent, ActivityBtn_Forwards, ActivityBtn_Commands, ActivityBtn_Sftp };
            var accents = new[] { ActivityAccent_Servers, ActivityAccent_Recent, ActivityAccent_Forwards, ActivityAccent_Commands, ActivityAccent_Sftp };
            for (int i = 0; i < buttons.Length; i++)
            {
                var isActive = i == activeIdx;
                buttons[i].Foreground = isActive ? ColBlue : ColOverlay0;
                accents[i].Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void ActivityBtn_Servers_Click(object sender, RoutedEventArgs e)  => SwitchActivityPanel(0);
        private void ActivityBtn_Recent_Click(object sender, RoutedEventArgs e)   => SwitchActivityPanel(1);
        private void ActivityBtn_Forwards_Click(object sender, RoutedEventArgs e) => SwitchActivityPanel(2);
        private void ActivityBtn_Commands_Click(object sender, RoutedEventArgs e) => SwitchActivityPanel(3);
        private void ActivityBtn_Sftp_Click(object sender, RoutedEventArgs e)     => SwitchActivityPanel(4);
        private void ActivityBtn_Settings_Click(object sender, RoutedEventArgs e) => SettingsButton_Click(sender, e);
        private void ActivityBtn_Sync_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            if (btn.ContextMenu is { } menu)
            {
                menu.PlacementTarget = btn;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
                menu.IsOpen = true;
            }
        }

        // RDP 全屏 (F11)

        private bool   _isRdpFullScreen;
        private bool   _preFullScreenSidebarCollapsed;
        private System.Windows.WindowState _preFullScreenWindowState;

        private void ToggleRdpFullScreen()
        {
            if (!_isRdpFullScreen)
            {
                // 进入全屏：仅 RDP 连接生效
                if (GetCurrentTabSession() is not RdpTabSession) return;

                _preFullScreenSidebarCollapsed = _isSidebarCollapsed;
                _preFullScreenWindowState      = WindowState;

                // 收起活动栏、功能面板、工具栏、状态栏
                ActivityBarColumn.Width = new GridLength(0);
                SetSidebarCollapsed(true, persistState: false, requestDisplaySync: false);
                ToolbarRow.Height   = new GridLength(0);
                StatusBarRow.Height = new GridLength(0);

                WindowStyle = System.Windows.WindowStyle.None;
                WindowState = System.Windows.WindowState.Maximized;

                _isRdpFullScreen = true;
                AppLogger.Info("rdp full-screen entered");
            }
            else
            {
                // 退出全屏
                WindowStyle = System.Windows.WindowStyle.SingleBorderWindow;
                WindowState = _preFullScreenWindowState;

                ActivityBarColumn.Width = new GridLength(40);
                ToolbarRow.Height   = new GridLength(46);
                StatusBarRow.Height = new GridLength(28);

                if (!_preFullScreenSidebarCollapsed)
                    SetSidebarCollapsed(false, persistState: false, requestDisplaySync: false);

                _isRdpFullScreen = false;

                // 退出后重新同步分辨率
                if (GetCurrentTabSession() is RdpTabSession { IsConnected: true })
                {
                    _syncDisplaySettingsRequested = true;
                    _resizeTimer.Stop();
                    _resizeTimer.Start();
                }
                AppLogger.Info("rdp full-screen exited");
            }
        }

        // 分辨率同步

        private void RdpContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (GetCurrentTabSession() is not RdpTabSession { IsConnected: true }) return;
            _syncDisplaySettingsRequested = true;
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }

        private void ResizeTimer_Tick(object? sender, EventArgs e)
        {
            _resizeTimer.Stop();
            if (GetCurrentTabSession() is not RdpTabSession current || !current.IsConnected) return;

            if (_syncDisplaySettingsRequested)
            {
                _syncDisplaySettingsRequested = false;
                ApplyDynamicDisplaySettingsIfNeeded();
                return;
            }
            try { current.Session.Refresh(); } catch { }
        }

        private (int physical_w, int physical_h, int logical_w, int logical_h) GetTargetDesktopSize()
        {
            var dpi   = VisualTreeHelper.GetDpi(this);
            var scaleX = dpi.DpiScaleX;
            var scaleY = dpi.DpiScaleY;

            var logW = (int)RdpContainer.ActualWidth;
            var logH = (int)RdpContainer.ActualHeight;

            if (logW < 64 || logH < 64)
            {
                logW = (int)(ActualWidth  - 240);
                logH = (int)(ActualHeight - 80);
            }

            // 4 对齐，设置最小尺寸
            logW = Math.Max(800, logW & ~3);
            logH = Math.Max(600, logH & ~3);

            // 物理像素（高 DPI 下 UpdateSessionDisplaySettings 参数 3/4）
            var phyW = Math.Max(800, (int)(logW * scaleX) & ~3);
            var phyH = Math.Max(600, (int)(logH * scaleY) & ~3);

            return (phyW, phyH, logW, logH);
        }

        private void ApplyDynamicDisplaySettingsIfNeeded()
        {
            if (GetCurrentTabSession() is not RdpTabSession current || !current.IsConnected) return;

            var (phyW, phyH, logW, logH) = GetTargetDesktopSize();
            if (logW < 64 || logH < 64) return;
            if (logW == current.LastDisplayWidth && logH == current.LastDisplayHeight) return;

            try
            {
                current.Session.UpdateDisplaySettings(phyW, phyH, logW, logH);
                current.LastDisplayWidth  = logW;
                current.LastDisplayHeight = logH;
            }
            catch
            {
                // 部分服务端不支持动态分辨率，静默忽略
            }
        }

        // 服务器列表

        private void RefreshServerView()
        {
            if (_serverView == null)
            {
                _serverView = BuildServerView();
                ServerList.ItemsSource = _serverView;
            }
            else
            {
                _serverView.Refresh();
            }
            UpdateServerCount();
        }

        private ListCollectionView BuildServerView()
        {
            var view = new ListCollectionView(_servers)
            {
                IsLiveSorting  = true,
                IsLiveGrouping = true
            };
            view.LiveSortingProperties.Add(nameof(ServerInfo.SortOrder));
            view.SortDescriptions.Add(new System.ComponentModel.SortDescription(
                nameof(ServerInfo.SortOrder), System.ComponentModel.ListSortDirection.Ascending));
            view.SortDescriptions.Add(new System.ComponentModel.SortDescription(
                nameof(ServerInfo.Name), System.ComponentModel.ListSortDirection.Ascending));
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ServerInfo.GroupDisplay)));
            view.LiveGroupingProperties.Add(nameof(ServerInfo.GroupDisplay));
            return view;
        }

        private void UpdateServerCount()
        {
            var count  = _servers.Count;
            if (count == 0)
            {
                ServerCountText.Text = "无服务器";
                return;
            }
            var online = _servers.Count(s => s.HealthState == ServerHealthState.Online);
            ServerCountText.Text = online > 0
                ? $"在线 {online} / {count} 台"
                : $"{count} 台服务器";
        }

        private void UpdateDisconnectedState()
        {
            _resizeTimer.Stop();
            _syncDisplaySettingsRequested  = false;
            _lastWindowState               = WindowState;
            IdlePanel.Visibility           = Visibility.Visible;
            RdpContainer.Visibility        = Visibility.Collapsed;
            TerminalWebView.Visibility     = Visibility.Collapsed;
            DisconnectButton.Visibility    = Visibility.Collapsed;
            StatusText.Text      = "未连接";
            DescriptionText.Text = "";
            Title = "RemoteX";
        }

        private void UpdateCurrentTabStatus()
        {
            var current = GetCurrentTabSession();
            if (current == null)
            {
                UpdateDisconnectedState();
                return;
            }

            // 有会话时显示断开按钮
            DisconnectButton.Visibility = Visibility.Visible;

            // 内容区域可见性由 SwitchToSession 管理，此处只更新文字
            StatusText.Text = current.IsConnected
                ? $"已连接 {current.Server.Name}"
                : $"已断开  {current.Server.Name}";
            DescriptionText.Text = current.Server.Description ?? "";
            Title = $"RemoteX — {current.Server.Name}";
        }

        // 搜索（防抖）

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_serverView == null) return;
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }

        private void SearchDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _searchDebounceTimer.Stop();
            if (_serverView == null) return;
            var keyword = SearchBox.Text;
            if (string.IsNullOrWhiteSpace(keyword))
            {
                _serverView.Filter = null;
            }
            else
            {
                keyword = keyword.Trim();
                var cmp = StringComparison.OrdinalIgnoreCase;
                _serverView.Filter = obj =>
                    obj is ServerInfo s &&
                    (s.Name.IndexOf(keyword, cmp) >= 0 ||
                     s.IP.IndexOf(keyword, cmp) >= 0 ||
                     (s.Description ?? "").IndexOf(keyword, cmp) >= 0 ||
                     (s.Group ?? "").IndexOf(keyword, cmp) >= 0);
            }
            _serverView.Refresh();
        }

        // Toast 通知

        private void ShowToast(string message)
        {
            ToastText.Text = message;
            ToastPanel.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _uiState.LastSelectedServerId = ServerList.SelectedItem is ServerInfo s ? s.Id : 0;
        }

        // 右键菜单

        private void ServerList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement(
                           ServerList, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item != null) item.IsSelected = true;
        }

        // 键盘快捷键

        private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Ctrl+N：新增服务器
            if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
            {
                AddServerButton_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            // Ctrl+W：关闭当前标签
            if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)
            {
                var current = GetCurrentTabSession();
                if (current != null)
                    RequestRemoveTabSession(current.InstanceId);
                e.Handled = true;
                return;
            }

            // Ctrl+T：新建会话（复制当前连接的服务器）
            if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control)
            {
                var current = GetCurrentTabSession();
                if (current != null)
                    _ = NewSessionAsync(current.Server);
                e.Handled = true;
                return;
            }

            // Ctrl+,：打开设置（不同键盘布局下逗号键可能映射不同 Key）
            if ((e.Key == Key.OemComma || e.Key == Key.OemPeriod) &&
                Keyboard.Modifiers == ModifierKeys.Control)
            {
                SettingsButton_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            // F5：批量检测
            if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
            {
                CheckAllButton_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            // F11：RDP 全屏切换
            if (e.Key == Key.F11 && Keyboard.Modifiers == ModifierKeys.None)
            {
                ToggleRdpFullScreen();
                e.Handled = true;
                return;
            }

            // 以下快捷键仅当列表有焦点或有选中项时生效
            if (ServerList.SelectedItem is not ServerInfo selectedServer) return;

            // Enter：连接选中服务器
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                _ = ConnectToServerAsync(selectedServer);
                e.Handled = true;
                return;
            }

            // Delete：删除选中服务器
            if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
            {
                DeleteServerButton_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            // Ctrl+D：克隆选中服务器
            if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control)
            {
                MenuClone_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        // 工具方法

        private static string BuildServerAddress(ServerInfo server) => server.Protocol switch
        {
            ServerProtocol.SSH    => BuildSshAddress(server),
            ServerProtocol.Telnet => server.Port == 23 ? server.IP : $"{server.IP}:{server.Port}",
            _                     => RdpSessionService.BuildAddress(server)
        };

        private static string BuildSshAddress(ServerInfo s)
        {
            var host = s.Port == 22 ? s.IP : $"{s.IP}:{s.Port}";
            return string.IsNullOrWhiteSpace(s.Username) ? host : $"{s.Username}@{host}";
        }

        private ITabSession? GetCurrentTabSession()
            => _activeTabId > 0 && _tabSessions.TryGetValue(_activeTabId, out var t) ? t : null;

        private void RestoreSelectionByServerId(int id)
        {
            if (id <= 0) return;
            var selected = _servers.Find(s => s.Id == id);
            if (selected == null) return;
            ServerList.SelectedItem = selected;
            ServerList.ScrollIntoView(selected);
        }

        // 设置按钮

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var win = new SettingsWindow(_appSettings) { Owner = this };
            win.ShowDialog();
        }

        // 代理管理按钮

        private void ProxyButton_Click(object sender, RoutedEventArgs e)
        {
            var win = new ProxyManagerWindow(_appSettings) { Owner = this };
            win.ShowDialog();
        }
    }
}
