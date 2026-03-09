using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using Brushes   = System.Windows.Media.Brushes;
using Cursors   = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using Button    = System.Windows.Controls.Button;

namespace RemoteX
{
    public partial class MainWindow
    {
        // ── 会话基础接口 ──────────────────────────────────────────────────────

        private interface ITabSession
        {
            ServerInfo Server  { get; set; }
            string     Address { get; set; }
            bool       IsConnected { get; }

            Border?    StatusBtn  { get; set; }
            TextBlock? StatusDot  { get; set; }
            TextBlock? StatusName { get; set; }

            void Dispose();
        }

        // ── RDP 会话 ──────────────────────────────────────────────────────────

        private sealed class RdpTabSession : ITabSession
        {
            public required ServerInfo        Server  { get; set; }
            public required string            Address { get; set; }
            public required RdpSessionService Session { get; init; }
            /// <summary>经 SOCKS 连接时使用的本地端口桥，断开时释放</summary>
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

        // 终端会话（SSH / Telnet）

        private sealed class TerminalTabSession : ITabSession
        {
            public required ServerInfo           Server  { get; set; }
            public required string               Address { get; set; }
            public required TerminalSessionService Service { get; init; }
            /// <summary>经 SOCKS 连接时的本地端口桥，断开时释放</summary>
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

        // 会话创建（RDP 专用）

        private RdpTabSession CreateTabSession(ServerInfo server)
        {
            var address  = BuildServerAddress(server);
            var session  = new RdpSessionService();
            var serverId = server.Id;

            var tab = new RdpTabSession
            {
                Server  = server,
                Address = address,
                Session = session
            };

            session.Host.Visibility = Visibility.Collapsed;
            RdpContainer.Children.Add(session.Host);

            session.Connected    += () => Dispatcher.BeginInvoke(() => OnTabConnected(serverId));
            session.Disconnected += () => Dispatcher.BeginInvoke(() => OnTabDisconnected(serverId));

            AddStatusTabButton(tab);
            return tab;
        }

        // ── 标签切换 ──────────────────────────────────────────────────────────

        private void SwitchToSession(int serverId)
        {
            if (!_tabSessions.ContainsKey(serverId)) return;

            var activeTab  = _tabSessions[serverId];
            var isTerminal = activeTab is TerminalTabSession;

            // RDP hosts：仅显示当前激活的 RDP 会话
            foreach (var (id, tab) in _tabSessions)
            {
                if (tab is RdpTabSession rdpTab)
                    rdpTab.Session.Host.Visibility =
                        (!isTerminal && id == serverId) ? Visibility.Visible : Visibility.Collapsed;
            }

            // 内容区域切换
            if (isTerminal)
            {
                TerminalWebView.Visibility = Visibility.Visible;
                RdpContainer.Visibility    = Visibility.Collapsed;
                IdlePanel.Visibility       = Visibility.Collapsed;
                SendWebViewMessage(new { type = "activate", sessionId = serverId });
            }
            else
            {
                TerminalWebView.Visibility = Visibility.Collapsed;
                RdpContainer.Visibility    = Visibility.Visible;
                IdlePanel.Visibility       = Visibility.Collapsed;
            }

            _activeServerId = serverId;
            UpdateStatusTabSelection();
            UpdateCurrentTabStatus();

            // RDP 连接需要同步分辨率
            if (activeTab is RdpTabSession { IsConnected: true })
            {
                _syncDisplaySettingsRequested = true;
                _resizeTimer.Stop();
                _resizeTimer.Start();
            }
        }

        // 标签关闭（带确认）

        private void RequestRemoveTabSession(int serverId)
        {
            if (!_tabSessions.TryGetValue(serverId, out var tab)) return;

            if (tab.IsConnected)
            {
                var proto = tab.Server.Protocol switch
                {
                    ServerProtocol.SSH    => "SSH",
                    ServerProtocol.Telnet => "Telnet",
                    _                     => "RDP"
                };
                var result = System.Windows.MessageBox.Show(
                    $"确定要断开并关闭「{tab.Server.Name}」的 {proto} 会话吗？",
                    "关闭确认",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);
                if (result != System.Windows.MessageBoxResult.Yes) return;
            }

            RemoveTabSession(serverId);
        }

        private void RemoveTabSession(int serverId)
        {
            if (!_tabSessions.TryGetValue(serverId, out var tab)) return;

            _tabSessions.Remove(serverId);
            RemoveStatusTabButton(tab);

            if (_activeServerId == serverId)
            {
                _activeServerId = 0;
                var next = _tabSessions.Keys.FirstOrDefault();
                if (next > 0) SwitchToSession(next);
            }

            // 协议特定的清理
            if (tab is RdpTabSession rdp)
                RdpContainer.Children.Remove(rdp.Session.Host);
            else if (tab is TerminalTabSession)
                SendWebViewMessage(new { type = "destroy", sessionId = serverId });

            try { tab.Dispose(); } catch { }

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

        // 状态栏标签

        private void AddStatusTabButton(ITabSession tab)
        {
            var serverId = tab.Server.Id;

            // 协议图标
            var protoIcon = tab.Server.Protocol switch
            {
                ServerProtocol.SSH    => "S",
                ServerProtocol.Telnet => "T",
                _                     => "R"
            };

            var dot = new TextBlock
            {
                Text              = protoIcon,
                FontSize          = tab.Server.Protocol == ServerProtocol.RDP ? 9 : 11,
                Foreground        = tab.Server.Protocol == ServerProtocol.RDP ? ColBlue : ColGreen,
                Margin            = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var nameText = new TextBlock
            {
                Text              = tab.Server.Name,
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
            closeBtn.Click += (_, e) => { e.Handled = true; RequestRemoveTabSession(serverId); };

            var inner = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            inner.Children.Add(dot);
            inner.Children.Add(nameText);
            inner.Children.Add(closeBtn);

            // 协议颜色底边
            var protoBrush = tab.Server.Protocol switch
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
                ToolTip         = tab.Address,
                BorderBrush     = protoBrush,
                BorderThickness = new Thickness(0, 0, 0, 2)
            };

            btn.MouseLeftButtonUp += (_, __) => SwitchToSession(serverId);
            btn.MouseEnter += (_, __) =>
            {
                if (_activeServerId != serverId) btn.Background = ColSurface0;
            };
            btn.MouseLeave += (_, __) =>
            {
                btn.Background = _activeServerId == serverId ? ColSurface1 : Brushes.Transparent;
            };

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
                var isSel = id == _activeServerId;
                tab.StatusBtn.Background = isSel ? ColSurface1 : Brushes.Transparent;
                if (tab.StatusName != null)
                    tab.StatusName.Foreground = isSel ? ColText : ColSubtext;
            }
        }

        private static void UpdateTabDot(ITabSession tab, bool? connected)
        {
            if (tab.StatusDot == null) return;

            if (tab is TerminalTabSession)
            {
                // 终端 Tab：圆点表示连接状态
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
                // RDP Tab：圆点图标
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
    }
}
