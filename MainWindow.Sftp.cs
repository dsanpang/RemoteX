using System.Windows;

using WpfColor    = System.Windows.Media.Color;
using WpfBrush    = System.Windows.Media.SolidColorBrush;
using WpfFW       = System.Windows.FontWeights;
using WpfHA       = System.Windows.HorizontalAlignment;
using WpfVA       = System.Windows.VerticalAlignment;
using WpfButton   = System.Windows.Controls.Button;
using WpfBorder   = System.Windows.Controls.Border;
using WpfStackPanel = System.Windows.Controls.StackPanel;
using WpfGrid     = System.Windows.Controls.Grid;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfColDef   = System.Windows.Controls.ColumnDefinition;
using Cursors     = System.Windows.Input.Cursors;

namespace RemoteX
{
    public partial class MainWindow
    {
        // ── Refresh SFTP panel ───────────────────────────────────────────────

        private void RefreshSftpPanel()
        {
            SftpServerList.Children.Clear();

            var sshServers = _servers.FindAll(s => s.Protocol == ServerProtocol.SSH);

            if (sshServers.Count == 0)
            {
                var hint = new WpfTextBlock
                {
                    Text       = "暂无 SSH 服务器",
                    FontSize   = 12,
                    Foreground = ColSubtext,
                    HorizontalAlignment = WpfHA.Center,
                    Margin     = new Thickness(0, 20, 0, 0)
                };
                SftpServerList.Children.Add(hint);
                return;
            }

            foreach (var server in sshServers)
                SftpServerList.Children.Add(BuildSftpServerCard(server));
        }

        private UIElement BuildSftpServerCard(ServerInfo server)
        {
            var nameText = new WpfTextBlock
            {
                Text              = server.Name,
                FontSize          = 12,
                FontWeight        = WpfFW.SemiBold,
                Foreground        = ColText,
                VerticalAlignment = WpfVA.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis
            };

            var addrText = new WpfTextBlock
            {
                Text      = $"{server.IP}:{server.Port}",
                FontSize  = 10,
                Foreground = ColOverlay0,
                Margin    = new Thickness(0, 2, 0, 0)
            };

            var info = new WpfStackPanel();
            info.Children.Add(nameText);
            info.Children.Add(addrText);

            var openBtn = new WpfButton
            {
                Content           = "打开 SFTP",
                Padding           = new Thickness(10, 5, 10, 5),
                FontSize          = 11,
                Background        = new WpfBrush(WpfColor.FromRgb(0x1A, 0x32, 0x54)),
                Foreground        = ColBlue,
                BorderThickness   = new Thickness(0),
                Cursor            = Cursors.Hand,
                VerticalAlignment = WpfVA.Center
            };

            var capturedServer = server;
            openBtn.Click += (_, __) => OpenSftp(capturedServer);

            var row = new WpfGrid { VerticalAlignment = WpfVA.Center };
            row.ColumnDefinitions.Add(new WpfColDef { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new WpfColDef { Width = GridLength.Auto });
            System.Windows.Controls.Grid.SetColumn(info,    0);
            System.Windows.Controls.Grid.SetColumn(openBtn, 1);
            row.Children.Add(info);
            row.Children.Add(openBtn);

            var card = new WpfBorder
            {
                Background      = new WpfBrush(WpfColor.FromRgb(0x18, 0x18, 0x25)),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(10, 8, 10, 8),
                Margin          = new Thickness(0, 2, 0, 2),
                BorderBrush     = new WpfBrush(WpfColor.FromRgb(0x31, 0x32, 0x44)),
                BorderThickness = new Thickness(1),
                Child           = row
            };

            card.MouseEnter += (_, __) => card.Background = new WpfBrush(WpfColor.FromRgb(0x24, 0x24, 0x35));
            card.MouseLeave += (_, __) => card.Background = new WpfBrush(WpfColor.FromRgb(0x18, 0x18, 0x25));

            return card;
        }

        // ── Open SFTP browser ────────────────────────────────────────────────

        private void OpenSftp(ServerInfo server)
        {
            SocksProxyEntry? proxy = null;
            TryGetSocksProxy(server, out proxy);

            var win = new SftpBrowserWindow(server, proxy) { Owner = this };
            win.Show();
        }
    }
}
