using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using Brushes     = System.Windows.Media.Brushes;
using Cursors     = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using Button      = System.Windows.Controls.Button;

namespace RemoteX
{
    public partial class MainWindow
    {
        // ── 会话基础接口 ──────────────────────────────────────────────────────

        private interface ITabSession
        {
            ServerInfo Server     { get; set; }
            string     Address    { get; set; }
            long       InstanceId { get; }
            bool       IsConnected { get; }

            Border?    StatusBtn  { get; set; }
            TextBlock? StatusDot  { get; set; }
            TextBlock? StatusName { get; set; }

            void Dispose();
        }

        // ── RDP 会话 ──────────────────────────────────────────────────────────

        private sealed class RdpTabSession : ITabSession
        {
            public required ServerInfo        Server     { get; set; }
            public required string            Address    { get; set; }
            public required RdpSessionService Session    { get; init; }
            public required long              InstanceId { get; init; }
            public IDisposable? SocksBridge { get; set; }

            public bool IsConnected => Session.IsConnected;

            public Border?    StatusBtn  { get; set; }
            public TextBlock? StatusDot  { get; set; }
            public TextBlock? StatusName { get; set; }

            public int LastDisplayWidth  { get; set; }
            public int LastDisplayHeight { get; set; }

            public void Dispose()
            {
                try { SocksBridge?.Dispose(); } catch { }
                try { Session.Dispose(); } catch { }
            }
        }

        // ── 终端会话（SSH / Telnet）──────────────────────────────────────────

        private sealed class TerminalTabSession : ITabSession
        {
            public required ServerInfo            Server     { get; set; }
            public required string                Address    { get; set; }
            public required TerminalSessionService Service    { get; init; }
            public required long                  InstanceId { get; init; }
            public IDisposable? SocksBridge { get; set; }

            public bool IsConnected => Service.IsConnected;

            public Border?    StatusBtn  { get; set; }
            public TextBlock? StatusDot  { get; set; }
            public TextBlock? StatusName { get; set; }

            public void Dispose()
            {
                try { SocksBridge?.Dispose(); } catch { }
                try { Service.Dispose(); } catch { }
            }
        }

        // ── 标签拖拽排序 ──────────────────────────────────────────────────────────

        private Border? _draggedTabBtn;
        private long    _draggedTabId;
        private System.Windows.Point _tabDragOrigin;
        private bool    _isDraggingTab;

        // ── 分屏状态 ──────────────────────────────────────────────────────────

        private bool _splitMode          = false;
        private bool _splitLayoutVisible = false; // 分屏布局当前是否展开（切到分屏外时折叠但保留状态）
        private long _primaryTabId       = 0;     // 左侧（主侧）session，独立跟踪以免与 _activeTabId 混淆
        private long _secondaryTabId     = 0;     // 右侧（次侧）session，0 = 未分屏
        /// <summary>
        /// true = RDP 分屏（需展开 C# 列布局）；
        /// false = Terminal 分屏（WebView2 内部处理，C# 列保持单栏）
        /// </summary>
        private bool _splitIsRdp         = false;

        // ── RDP 会话创建 ──────────────────────────────────────────────────────

        private RdpTabSession CreateTabSession(ServerInfo server)
        {
            var address    = BuildServerAddress(server);
            var session    = new RdpSessionService();
            var instanceId = System.Threading.Interlocked.Increment(ref _nextSessionInstanceId);

            var tab = new RdpTabSession
            {
                Server     = server,
                Address    = address,
                InstanceId = instanceId,
                Session    = session
            };

            session.Host.Visibility = Visibility.Collapsed;
            RdpContainer.Children.Add(session.Host);

            session.Connected    += () => Dispatcher.BeginInvoke(() => OnTabConnected(instanceId));
            session.Disconnected += () => Dispatcher.BeginInvoke(() => OnTabDisconnected(instanceId));

            AddStatusTabButton(tab);
            return tab;
        }

        // ── 分屏：在右侧显示指定 session ─────────────────────────────────────

        internal void ShowInSplitPane(long instanceId)
        {
            if (!_tabSessions.TryGetValue(instanceId, out var secTab)) return;

            // 选中了当前活动的 session，不能自分屏
            if (instanceId == _activeTabId)
            {
                AppMsg.Show(this, "右侧分屏需选择与左侧不同的会话。", "分屏提示", AppMsgIcon.Info);
                return;
            }

            // 检查类型兼容性（同类分屏）
            if (!_tabSessions.TryGetValue(_activeTabId, out var priTab)) return;
            bool bothRdp  = priTab is RdpTabSession      && secTab is RdpTabSession;
            bool bothTerm = priTab is TerminalTabSession  && secTab is TerminalTabSession;
            if (!bothRdp && !bothTerm)
            {
                AppMsg.Show(this, "分屏仅支持同类型会话（RDP ↔ RDP 或 终端 ↔ 终端）。", "分屏提示", AppMsgIcon.Info);
                return;
            }

            // 已在分屏中则先退出
            if (_splitMode) ExitSplitMode();

            _splitMode      = true;
            _splitIsRdp     = bothRdp;
            _primaryTabId   = _activeTabId;
            _secondaryTabId = instanceId;

            ExitSplitButton.Visibility = Visibility.Visible;
            ApplySplitLayout();

            UpdateStatusTabSelection();
            AppLogger.Info($"split screen: primary={_activeTabId} secondary={instanceId}");
        }

        // ── 分屏布局展开 / 折叠 ───────────────────────────────────────────────

        /// <summary>将分屏布局展开显示（_primaryTabId / _secondaryTabId / _splitIsRdp 必须已设置）。</summary>
        private void ApplySplitLayout()
        {
            if (_splitIsRdp)
            {
                if (_tabSessions.TryGetValue(_secondaryTabId, out var secTab)
                    && secTab is RdpTabSession secRdp)
                {
                    if (!RdpSecondaryContainer.Children.Contains(secRdp.Session.Host))
                    {
                        RdpContainer.Children.Remove(secRdp.Session.Host);
                        RdpSecondaryContainer.Children.Add(secRdp.Session.Host);
                    }
                    secRdp.Session.Host.Visibility = Visibility.Visible;
                }
                SplitSeparatorCol.Width    = new System.Windows.GridLength(4);
                SecondaryPaneCol.Width     = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
                ContentSplitter.Visibility = Visibility.Visible;
                SecondaryPane.Visibility   = Visibility.Visible;
                RdpContainer.Visibility    = Visibility.Visible;
                TerminalWebView.Visibility = Visibility.Collapsed;
                IdlePanel.Visibility       = Visibility.Collapsed;
            }
            else
            {
                TerminalWebView.Visibility = Visibility.Visible;
                RdpContainer.Visibility    = Visibility.Collapsed;
                IdlePanel.Visibility       = Visibility.Collapsed;
                SendWebViewMessage(new
                {
                    type        = "split",
                    primaryId   = (int)_primaryTabId,
                    secondaryId = (int)_secondaryTabId
                });
            }
            _splitLayoutVisible = true;
        }

        /// <summary>折叠分屏布局（保留 _splitMode 等状态，让用户切回分屏 tab 时可恢复）。</summary>
        private void HideSplitLayout()
        {
            if (_splitIsRdp)
            {
                if (_tabSessions.TryGetValue(_secondaryTabId, out var secTab)
                    && secTab is RdpTabSession secRdp)
                {
                    RdpSecondaryContainer.Children.Remove(secRdp.Session.Host);
                    RdpContainer.Children.Add(secRdp.Session.Host);
                    secRdp.Session.Host.Visibility = Visibility.Collapsed;
                }
                ContentSplitter.Visibility = Visibility.Collapsed;
                SecondaryPane.Visibility   = Visibility.Collapsed;
                SplitSeparatorCol.Width    = new System.Windows.GridLength(0);
                SecondaryPaneCol.Width     = new System.Windows.GridLength(0);
            }
            else
            {
                SendWebViewMessage(new { type = "unsplit" });
            }
            _splitLayoutVisible = false;
        }

        // ── 退出分屏 ──────────────────────────────────────────────────────────

        /// <summary>彻底退出分屏：折叠布局并清空所有状态字段，不重新激活 session。</summary>
        private void TearDownSplitLayout()
        {
            HideSplitLayout();
            ExitSplitButton.Visibility = Visibility.Collapsed;
            _splitMode          = false;
            _splitIsRdp         = false;
            _splitLayoutVisible = false;
            _primaryTabId       = 0;
            _secondaryTabId     = 0;
        }

        internal void ExitSplitMode()
        {
            if (!_splitMode) return;
            TearDownSplitLayout();
            // 重新激活主侧 session
            if (_activeTabId > 0 && _tabSessions.ContainsKey(_activeTabId))
                SwitchToSession(_activeTabId);
        }

        // ── 会话辅助方法 ──────────────────────────────────────────────────────

        private bool TryGetSessionForServer(int serverId, out long instanceId, out ITabSession? session)
        {
            foreach (var kv in _tabSessions.OrderBy(k => k.Key))
            {
                if (kv.Value.Server.Id == serverId)
                {
                    instanceId = kv.Key;
                    session    = kv.Value;
                    return true;
                }
            }
            instanceId = 0;
            session    = null;
            return false;
        }

        private void RefreshTabLabelsForServer(int serverId)
        {
            var sessions = _tabSessions
                .Where(kv => kv.Value.Server.Id == serverId)
                .OrderBy(kv => kv.Key)
                .Select(kv => kv.Value)
                .ToList();

            for (int i = 0; i < sessions.Count; i++)
            {
                if (sessions[i].StatusName == null) continue;
                sessions[i].StatusName!.Text = sessions.Count > 1
                    ? $"{sessions[i].Server.Name} #{i + 1}"
                    : sessions[i].Server.Name;
            }
        }

        // ── 标签切换 ──────────────────────────────────────────────────────────

        private void SwitchToSession(long instanceId)
        {
            if (!_tabSessions.TryGetValue(instanceId, out var activeTab)) return;
            var isTerminal = activeTab is TerminalTabSession;

            if (_splitMode)
            {
                bool outsideSplit = instanceId != _primaryTabId && instanceId != _secondaryTabId;

                if (outsideSplit)
                {
                    // 切到分屏外：折叠布局但保留分屏状态，让切回时可恢复
                    if (_splitLayoutVisible) HideSplitLayout();
                    // 继续往下正常显示该 session（全屏）
                }
                else if (instanceId == _secondaryTabId)
                {
                    // 点击次侧标签：恢复布局（若被折叠），只更新焦点不改变布局
                    if (!_splitLayoutVisible) ApplySplitLayout();
                    _activeTabId = instanceId;
                    UpdateStatusTabSelection();
                    UpdateCurrentTabStatus();
                    return;
                }
                else
                {
                    // 切换到主侧（或重新激活主侧）：恢复布局（若被折叠）
                    if (!_splitLayoutVisible) ApplySplitLayout();
                    // 检查类型兼容性
                    if (_tabSessions.TryGetValue(_secondaryTabId, out var secTab))
                    {
                        bool typeMismatch = (activeTab is TerminalTabSession) != (secTab is TerminalTabSession);
                        if (typeMismatch) ExitSplitMode();
                    }
                }
            }

            // RDP Host 显示控制（分屏时跳过次侧）
            foreach (var (id, tab) in _tabSessions)
            {
                if (tab is RdpTabSession rdpTab)
                {
                    if (_splitMode && id == _secondaryTabId) continue; // 次侧由 RdpSecondaryContainer 管理
                    rdpTab.Session.Host.Visibility =
                        (!isTerminal && id == instanceId) ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            // 内容区域切换
            if (isTerminal)
            {
                TerminalWebView.Visibility = Visibility.Visible;
                RdpContainer.Visibility    = Visibility.Collapsed;
                IdlePanel.Visibility       = Visibility.Collapsed;
                // 在分屏模式下，activate 会在 JS 侧切换主侧 session
                SendWebViewMessage(new { type = "activate", sessionId = (int)instanceId });
            }
            else
            {
                TerminalWebView.Visibility = Visibility.Collapsed;
                RdpContainer.Visibility    = Visibility.Visible;
                IdlePanel.Visibility       = Visibility.Collapsed;
            }

            _activeTabId = instanceId;
            UpdateStatusTabSelection();
            UpdateCurrentTabStatus();

            if (activeTab is RdpTabSession { IsConnected: true })
            {
                _syncDisplaySettingsRequested = true;
                _resizeTimer.Stop();
                _resizeTimer.Start();
            }
        }

        // ── 标签关闭 ──────────────────────────────────────────────────────────

        private void RequestRemoveTabSession(long instanceId)
        {
            if (!_tabSessions.TryGetValue(instanceId, out var tab)) return;

            if (tab.IsConnected)
            {
                var proto = tab.Server.Protocol switch
                {
                    ServerProtocol.SSH    => "SSH",
                    ServerProtocol.Telnet => "Telnet",
                    _                     => "RDP"
                };
                var result = AppMsg.Show(this,
                    $"确定要断开并关闭「{tab.Server.Name}」的 {proto} 会话吗？",
                    "关闭确认", AppMsgIcon.Question, AppMsgButton.YesNo);
                if (result != AppMsgResult.Yes) return;
            }

            RemoveTabSession(instanceId);
        }

        private void RemoveTabSession(long instanceId)
        {
            if (!_tabSessions.TryGetValue(instanceId, out var tab)) return;

            var serverId = tab.Server.Id;
            CancelConnectTimeout(instanceId);

            // 分屏清理：仅当关闭的是分屏中的主侧或次侧时才退出分屏
            long switchToAfterSplit = 0;
            if (_splitMode)
            {
                if (instanceId == _secondaryTabId)
                {
                    // 次侧关闭：退出分屏，主侧继续
                    ExitSplitMode();
                }
                else if (instanceId == _activeTabId)
                {
                    // 主侧关闭：退出分屏，切换到次侧
                    switchToAfterSplit = _secondaryTabId;
                    ExitSplitMode();
                }
                // 关闭的是分屏之外的其他 tab：不影响分屏，继续保持
            }

            _tabSessions.Remove(instanceId);
            RemoveStatusTabButton(tab);

            // 确定下一个激活的 session
            if (switchToAfterSplit > 0 && _tabSessions.ContainsKey(switchToAfterSplit))
            {
                _activeTabId = 0;
                SwitchToSession(switchToAfterSplit);
            }
            else if (_activeTabId == instanceId)
            {
                _activeTabId = 0;
                var next = _tabSessions.Keys.FirstOrDefault();
                if (next > 0) SwitchToSession(next);
            }

            // 协议特定的清理
            if (tab is RdpTabSession rdp)
            {
                // 可能在主容器或次侧容器中，两处都尝试移除
                RdpContainer.Children.Remove(rdp.Session.Host);
                RdpSecondaryContainer.Children.Remove(rdp.Session.Host);
            }
            else if (tab is TerminalTabSession)
                SendWebViewMessage(new { type = "destroy", sessionId = (int)instanceId });

            try { tab.Dispose(); } catch { }

            RefreshTabLabelsForServer(serverId);

            if (_tabSessions.Count == 0)
            {
                TerminalWebView.Visibility = Visibility.Collapsed;
                RdpContainer.Visibility    = Visibility.Collapsed;
                UpdateDisconnectedState();
            }
            else
            {
                UpdateCurrentTabStatus();
            }
        }

        // ── 新建会话（复制）──────────────────────────────────────────────────

        private async Task NewSessionAsync(ServerInfo server)
        {
            if (server.Protocol == ServerProtocol.RDP)
            {
                var tab = CreateTabSession(server);
                _tabSessions[tab.InstanceId] = tab;
                RefreshTabLabelsForServer(server.Id);
                BeginConnect(tab, server);
            }
            else
            {
                await ConnectTerminalAsync(server, forceNew: true);
            }
        }

        // ── 状态栏标签按钮 ────────────────────────────────────────────────────

        private void AddStatusTabButton(ITabSession tab)
        {
            var instanceId = tab.InstanceId;
            var server     = tab.Server;

            var protoIcon = server.Protocol switch
            {
                ServerProtocol.SSH    => "S",
                ServerProtocol.Telnet => "T",
                _                     => "R"
            };

            var dot = new TextBlock
            {
                Text              = protoIcon,
                FontSize          = server.Protocol == ServerProtocol.RDP ? 9 : 11,
                Foreground        = server.Protocol == ServerProtocol.RDP ? ColBlue : ColGreen,
                Margin            = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var nameText = new TextBlock
            {
                Text              = server.Name,
                FontSize          = 11,
                Foreground        = ColSubtext,
                MaxWidth          = 110,
                TextTrimming      = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 5, 0)
            };

            var closeBtn = new Button
            {
                Content           = "×",
                Width             = 13, Height = 13,
                Padding           = new Thickness(0),
                BorderThickness   = new Thickness(0),
                Background        = Brushes.Transparent,
                Foreground        = ColOverlay0,
                Cursor            = Cursors.Hand,
                FontSize          = 10,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeBtn.Click += (_, e) => { e.Handled = true; RequestRemoveTabSession(instanceId); };

            var inner = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            inner.Children.Add(dot);
            inner.Children.Add(nameText);
            inner.Children.Add(closeBtn);

            var protoBrush = server.Protocol switch
            {
                ServerProtocol.SSH    => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA6, 0xE3, 0xA1)),
                ServerProtocol.Telnet => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF9, 0xE2, 0xAF)),
                _                     => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x89, 0xB4, 0xFA))
            };

            var btn = new Border
            {
                Background      = Brushes.Transparent,
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(7, 3, 7, 2),
                Margin          = new Thickness(1, 0, 1, 0),
                Cursor          = Cursors.Hand,
                Child           = inner,
                ToolTip         = server.Name,
                BorderBrush     = protoBrush,
                BorderThickness = new Thickness(0, 0, 0, 2)
            };

            // ── 拖拽排序 ──────────────────────────────────────────────────────
            btn.PreviewMouseLeftButtonDown += (s, e) =>
            {
                _tabDragOrigin = e.GetPosition(RdpTabStrip);
                _draggedTabBtn = btn;
                _draggedTabId  = instanceId;
                _isDraggingTab = false;
            };

            btn.MouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _draggedTabBtn != btn) return;

                var pos = e.GetPosition(RdpTabStrip);

                if (!_isDraggingTab)
                {
                    if (Math.Abs(pos.X - _tabDragOrigin.X) < 6) return;
                    _isDraggingTab = true;
                    btn.Opacity = 0.55;
                    btn.CaptureMouse();
                }

                int currentIdx = RdpTabStrip.Children.IndexOf(btn);
                int targetIdx  = FindTabDropIndex(pos.X, btn);

                if (targetIdx != currentIdx)
                {
                    RdpTabStrip.Children.Remove(btn);
                    int insertAt = Math.Clamp(
                        targetIdx > currentIdx ? targetIdx - 1 : targetIdx,
                        0, RdpTabStrip.Children.Count);
                    RdpTabStrip.Children.Insert(insertAt, btn);
                }
                e.Handled = true;
            };

            btn.MouseLeftButtonUp += (s, e) =>
            {
                if (_isDraggingTab && _draggedTabBtn == btn)
                {
                    btn.Opacity = 1.0;
                    btn.ReleaseMouseCapture();
                    _isDraggingTab = false;
                    _draggedTabBtn = null;
                    e.Handled = true;
                    return;
                }
                SwitchToSession(instanceId);
            };

            btn.MouseEnter += (_, __) =>
            {
                if (_activeTabId != instanceId) btn.Background = ColSurface0;
            };
            btn.MouseLeave += (_, __) =>
            {
                btn.Background = _activeTabId == instanceId ? ColSurface1 : Brushes.Transparent;
            };

            // 右键菜单
            var cm = new ContextMenu
            {
                Background      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x31, 0x32, 0x44)),
                BorderBrush     = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x45, 0x47, 0x5A)),
                BorderThickness = new Thickness(1)
            };
            var miNew = new MenuItem { Header = "新建会话  Ctrl+T", Foreground = ColText, Background = Brushes.Transparent };
            miNew.Click += async (_, __) => await NewSessionAsync(server);

            var miSplit = new MenuItem { Header = "在右侧分屏显示", Foreground = ColText, Background = Brushes.Transparent };
            miSplit.Click += (_, __) => ShowInSplitPane(instanceId);

            var miClose = new MenuItem { Header = "关闭会话  Ctrl+W", Foreground = ColText, Background = Brushes.Transparent };
            miClose.Click += (_, __) => RequestRemoveTabSession(instanceId);

            cm.Items.Add(miNew);
            cm.Items.Add(miSplit);
            cm.Items.Add(new Separator());
            cm.Items.Add(miClose);
            btn.ContextMenu = cm;

            tab.StatusBtn  = btn;
            tab.StatusDot  = dot;
            tab.StatusName = nameText;

            RdpTabStrip.Children.Add(btn);
            RdpTabScrollViewer.Visibility = Visibility.Visible;
            RdpTabSeparator.Visibility    = Visibility.Visible;
        }

        private void RemoveStatusTabButton(ITabSession tab)
        {
            if (tab.StatusBtn != null)
                RdpTabStrip.Children.Remove(tab.StatusBtn);

            if (RdpTabStrip.Children.Count == 0)
            {
                RdpTabScrollViewer.Visibility = Visibility.Collapsed;
                RdpTabSeparator.Visibility    = Visibility.Collapsed;
            }
        }

        private void UpdateStatusTabSelection()
        {
            foreach (var (id, tab) in _tabSessions)
            {
                if (tab.StatusBtn == null) continue;
                bool isSel = id == _activeTabId;
                // 次侧 tab 也高亮（但使用不同色调）
                bool isSecondary = _splitMode && id == _secondaryTabId;
                tab.StatusBtn.Background = isSel
                    ? ColSurface1
                    : isSecondary
                        ? ColSurface0
                        : Brushes.Transparent;
                if (tab.StatusName != null)
                    tab.StatusName.Foreground = (isSel || isSecondary) ? ColText : ColSubtext;
            }
        }

        private static void UpdateTabDot(ITabSession tab, bool? connected)
        {
            if (tab.StatusDot == null) return;

            if (tab is TerminalTabSession)
            {
                tab.StatusDot.Text       = "●";
                tab.StatusDot.FontSize   = 11;
                tab.StatusDot.Foreground = connected switch
                {
                    null  => ColBlue,
                    true  => ColGreen,
                    false => ColOverlay0
                };
            }
            else
            {
                (tab.StatusDot.Text, tab.StatusDot.Foreground) = connected switch
                {
                    null  => ("○", ColBlue),
                    true  => ("●", ColGreen),
                    false => ("●", ColOverlay0)
                };
            }
        }

        private static void UpdateTabHeader(ITabSession tab, string title)
        {
            if (tab.StatusName != null) tab.StatusName.Text = title;
        }

        private bool TryGetLiveTabSession(long instanceId, out ITabSession? session)
            => _tabSessions.TryGetValue(instanceId, out session) && session != null;

        /// <summary>
        /// 根据鼠标 X 坐标（相对于 RdpTabStrip）找到插入位置索引。
        /// 以每个子元素中点为分界：鼠标在中点左侧 → 插在该元素之前。
        /// </summary>
        private int FindTabDropIndex(double mouseX, Border dragged)
        {
            for (int i = 0; i < RdpTabStrip.Children.Count; i++)
            {
                if (RdpTabStrip.Children[i] is not Border child || child == dragged) continue;
                var mid = child.TranslatePoint(
                    new System.Windows.Point(child.ActualWidth / 2, 0), RdpTabStrip);
                if (mouseX < mid.X) return i;
            }
            return RdpTabStrip.Children.Count;
        }

        private long _nextSessionInstanceId;
    }
}
