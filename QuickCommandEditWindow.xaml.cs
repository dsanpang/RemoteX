using System.Collections.Generic;
using System.Windows;


using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;

namespace RemoteX;

public partial class QuickCommandEditWindow : Window
{
    private readonly QuickCommand? _existing;

    public QuickCommand? Result { get; private set; }

    public QuickCommandEditWindow(QuickCommand? existing, IEnumerable<string> existingGroups)
    {
        InitializeComponent();
        _existing = existing;

        foreach (var g in existingGroups)
            GroupBox.Items.Add(g);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_existing != null)
        {
            Title = "编辑快捷命令";
            TitleLabel.Text      = "编辑快捷命令";
            NameBox.Text         = _existing.Name;
            GroupBox.Text        = _existing.Group;
            CommandBox.Text      = _existing.Command;
            DescriptionBox.Text  = _existing.Description;
        }
        else
        {
            Title = "新增快捷命令";
            TitleLabel.Text = "新增快捷命令";
            GroupBox.Text   = "默认";
        }

        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            AppMsg.Show(this, "请输入命令名称", "提示", AppMsgIcon.Warning);
            NameBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(CommandBox.Text))
        {
            AppMsg.Show(this, "请输入命令内容", "提示", AppMsgIcon.Warning);
            CommandBox.Focus();
            return;
        }

        Result = new QuickCommand
        {
            Id          = _existing?.Id ?? 0,
            Name        = NameBox.Text.Trim(),
            Group       = string.IsNullOrWhiteSpace(GroupBox.Text) ? "默认" : GroupBox.Text.Trim(),
            Command     = CommandBox.Text,
            Description = DescriptionBox.Text,
            SortOrder   = _existing?.SortOrder ?? 0
        };

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OkButton_Click(sender, new RoutedEventArgs());
    }
}
