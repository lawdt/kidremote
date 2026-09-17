using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KidRemote.Ui;

/// <summary>Окно, из которого ребёнок пишет родителям — с экрана блокировки или из трея.</summary>
internal sealed class MessageWindow : Window
{
    private const int MaxLength = 400;

    private readonly TextBox _input;

    public string Text => _input.Text.Trim();

    public MessageWindow()
    {
        Title = "KidRemote";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x11, 0x16, 0x26));

        var panel = new StackPanel { Margin = new Thickness(24) };

        panel.Children.Add(new TextBlock
        {
            Text = "Сообщение родителям",
            FontSize = 17,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF7, 0xFF)),
            Margin = new Thickness(0, 0, 0, 4)
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Придёт им в Telegram.",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xA0, 0xC8)),
            Margin = new Thickness(0, 0, 0, 14)
        });

        _input = new TextBox
        {
            MinHeight = 90,
            MaxLength = MaxLength,
            FontSize = 15,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 6, 8, 6)
        };

        panel.Children.Add(_input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var cancel = new Button { Content = "Отмена", Width = 100, Height = 32, Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, _) => { DialogResult = false; };

        var send = new Button { Content = "Отправить", Width = 100, Height = 32, IsDefault = true };
        send.Click += (_, _) =>
        {
            if (Text.Length == 0) return;
            DialogResult = true;
        };

        buttons.Children.Add(cancel);
        buttons.Children.Add(send);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) => _input.Focus();
    }
}
