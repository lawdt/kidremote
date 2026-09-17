using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KidRemote.Core;

namespace KidRemote.Ui;

/// <summary>Переписка с родителями: история сверху, поле ввода снизу.</summary>
internal sealed class ChatWindow : Window
{
    private const int MaxLength = 400;

    private readonly ChatLog _chat;
    private readonly StackPanel _history;
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input;

    public string Text => _input.Text.Trim();

    public ChatWindow(ChatLog chat)
    {
        _chat = chat;

        Title = "KidRemote";
        Width = 520;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x11, 0x16, 0x26));

        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = "Чат с родителями",
            FontSize = 17,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF7, 0xFF)),
            Margin = new Thickness(0, 0, 0, 12)
        };

        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        _history = new StackPanel();
        _scroll = new ScrollViewer
        {
            Content = _history,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x20)),
            Padding = new Thickness(12)
        };

        Grid.SetRow(_scroll, 1);
        grid.Children.Add(_scroll);

        var bottom = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };

        _input = new TextBox
        {
            MinHeight = 70,
            MaxLength = MaxLength,
            FontSize = 15,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 6, 8, 6)
        };

        bottom.Children.Add(_input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var close = new Button { Content = "Закрыть", Width = 100, Height = 32, Margin = new Thickness(0, 0, 10, 0) };
        close.Click += (_, _) => { DialogResult = false; };

        var send = new Button { Content = "Отправить", Width = 100, Height = 32, IsDefault = true };
        send.Click += (_, _) =>
        {
            if (Text.Length == 0) return;
            DialogResult = true;
        };

        buttons.Children.Add(close);
        buttons.Children.Add(send);
        bottom.Children.Add(buttons);

        Grid.SetRow(bottom, 2);
        grid.Children.Add(bottom);

        Content = grid;

        RenderHistory();
        Loaded += (_, _) =>
        {
            _input.Focus();
            _scroll.ScrollToEnd();
        };
    }

    private void RenderHistory()
    {
        _history.Children.Clear();

        var messages = _chat.Tail(40);
        if (messages.Count == 0)
        {
            _history.Children.Add(new TextBlock
            {
                Text = "Здесь пока пусто. Напишите родителям — сообщение придёт им в Telegram.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x7A, 0xA0)),
                TextWrapping = TextWrapping.Wrap
            });

            return;
        }

        foreach (var message in messages) _history.Children.Add(Bubble(message));
    }

    private static Border Bubble(ChatMessage message)
    {
        var fromParent = message.FromParent;

        var author = new TextBlock
        {
            Text = $"{message.Author} · {message.Time:HH:mm}",
            FontSize = 12,
            Foreground = new SolidColorBrush(fromParent
                ? Color.FromRgb(0x7F, 0xC5, 0xFF)
                : Color.FromRgb(0x8F, 0xA0, 0xC8)),
            Margin = new Thickness(0, 0, 0, 3)
        };

        var body = new TextBlock
        {
            Text = message.Text,
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF7, 0xFF)),
            TextWrapping = TextWrapping.Wrap
        };

        var stack = new StackPanel();
        stack.Children.Add(author);
        stack.Children.Add(body);

        return new Border
        {
            Background = new SolidColorBrush(fromParent
                ? Color.FromRgb(0x1B, 0x2C, 0x4A)
                : Color.FromRgb(0x1A, 0x20, 0x33)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 10),
            Margin = new Thickness(fromParent ? 0 : 60, 0, fromParent ? 60 : 0, 8),
            Child = stack
        };
    }
}
