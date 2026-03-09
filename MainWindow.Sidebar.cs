using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using Color   = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;

namespace RemoteX
{
    public partial class MainWindow
    {
        // 最近连接面板

        private void RefreshRecentSection()
        {
            var validIds = _appSettings.RecentServerIds
                .Where(id => _servers.Exists(s => s.Id == id))
                .Take(5)
                .ToList();

            // 侧边栏迷你列表
            if (validIds.Count == 0)
            {
                RecentSection.Visibility = Visibility.Collapsed;
            }
            else
            {
                RecentSection.Visibility = Visibility.Visible;

                RecentList.Children.Clear();
                foreach (var id in validIds)
                {
                    var server = _servers.Find(s => s.Id == id);
                    if (server == null) continue;

                    var btn = new Border
                    {
                        Background   = Brushes.Transparent,
                        CornerRadius = new CornerRadius(5),
                        Padding      = new Thickness(6, 4, 6, 4),
                        Margin       = new Thickness(0, 1, 0, 1),
                        Cursor       = Cursors.Hand,
                        Tag          = server
                    };

                    var inner = new Grid();
                    inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var dot = new TextBlock
                    {
                Text              = "●",
                        FontSize          = 7,
                        Foreground        = ColBlue,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin            = new Thickness(0, 0, 6, 0)
                    };
                    var name = new TextBlock
                    {
                        Text              = server.Name,
                        FontSize          = 11,
                        Foreground        = ColSubtext,
                        TextTrimming      = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var ip = new TextBlock
                    {
                        Text              = server.AddressDisplay,
                        FontSize          = 10,
                        Foreground        = ColOverlay0,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin            = new Thickness(6, 0, 0, 0)
                    };

                    Grid.SetColumn(dot,  0);
                    Grid.SetColumn(name, 1);
                    Grid.SetColumn(ip,   2);
                    inner.Children.Add(dot);
                    inner.Children.Add(name);
                    inner.Children.Add(ip);
                    btn.Child = inner;

                    var capturedServer = server;
                    btn.MouseEnter        += (_, __) => btn.Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44));
                    btn.MouseLeave        += (_, __) => btn.Background = Brushes.Transparent;
                    btn.MouseLeftButtonUp += async (_, __) => await ConnectToServerAsync(capturedServer);

                    RecentList.Children.Add(btn);
                }
            }

            // 主区域空闲面板大卡片
            RefreshIdlePanelCards(validIds);
        }

        private void RefreshIdlePanelCards(System.Collections.Generic.List<int> validIds)
        {
            RecentCardsPanel.Items.Clear();

            if (validIds.Count == 0)
            {
                NoRecentHint.Visibility = Visibility.Visible;
                return;
            }
            NoRecentHint.Visibility = Visibility.Collapsed;

            foreach (var id in validIds)
            {
                var server = _servers.Find(s => s.Id == id);
                if (server == null) continue;

                var (protoBg, protoFg, protoText) = server.Protocol switch
                {
                    ServerProtocol.SSH    => (Color.FromRgb(0x1E, 0x3A, 0x2F), Color.FromRgb(0xA6, 0xE3, 0xA1), "SSH"),
                    ServerProtocol.Telnet => (Color.FromRgb(0x2D, 0x2B, 0x1E), Color.FromRgb(0xF9, 0xE2, 0xAF), "TEL"),
                    _                     => (Color.FromRgb(0x1A, 0x32, 0x54), Color.FromRgb(0x89, 0xB4, 0xFA), "RDP")
                };

                // 协议徽章
                var badge = new Border
                {
                    Background   = new SolidColorBrush(protoBg),
                    CornerRadius = new CornerRadius(4),
                    Padding      = new Thickness(6, 2, 6, 2),
                    Margin       = new Thickness(0, 0, 0, 8),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    Child = new TextBlock
                    {
                        Text       = protoText,
                        FontSize   = 10,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(protoFg)
                    }
                };

                var nameText = new TextBlock
                {
                    Text         = server.Name,
                    FontSize     = 13,
                    FontWeight   = FontWeights.SemiBold,
                    Foreground   = ColText,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin       = new Thickness(0, 0, 0, 4)
                };
                var addrText = new TextBlock
                {
                    Text         = server.AddressDisplay,
                    FontSize     = 11,
                    Foreground   = ColOverlay0,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };

                var content = new StackPanel();
                content.Children.Add(badge);
                content.Children.Add(nameText);
                content.Children.Add(addrText);

                var card = new Border
                {
                    Width        = 180,
                    Background   = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x25)),
                    CornerRadius = new CornerRadius(10),
                    Padding      = new Thickness(14, 12, 14, 12),
                    Margin       = new Thickness(0, 0, 12, 12),
                    Cursor       = Cursors.Hand,
                    Child        = content,
                    BorderBrush  = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
                    BorderThickness = new Thickness(1)
                };

                var capturedServer = server;
                card.MouseEnter        += (_, __) => card.Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x35));
                card.MouseLeave        += (_, __) => card.Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x25));
                card.MouseLeftButtonUp += async (_, __) => await ConnectToServerAsync(capturedServer);

                RecentCardsPanel.Items.Add(card);
            }
        }

        private void ClearRecentButton_Click(object sender, RoutedEventArgs e)
        {
            _appSettings.RecentServerIds.Clear();
            _appSettings.Save();
            RefreshRecentSection();
        }

    }
}
