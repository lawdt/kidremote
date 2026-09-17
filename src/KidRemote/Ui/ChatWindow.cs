using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using KidRemote.Core;

namespace KidRemote.Ui;

/// <summary>Переписка с родителями: история сверху, поле ввода снизу.</summary>
internal sealed class ChatWindow : Window
{
    private const int MaxLength = 400;

    private static readonly FontFamily Mono = new("Consolas, Courier New");

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
            Background = new SolidColorBrush(Color.FromRgb(0x07, 0x0B, 0x14)),
            Padding = new Thickness(12)
        };

        Grid.SetRow(_scroll, 1);
        grid.Children.Add(_scroll);

        var bottom = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };

        _input = new TextBox
        {
            MinHeight = 70,
            MaxLength = MaxLength,
            FontFamily = Mono,
            FontSize = 14,
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

        var messages = _chat.Tail(60);
        if (messages.Count == 0)
        {
            _history.Children.Add(new TextBlock
            {
                Text = "* чат пуст — напишите родителям, сообщение придёт им в Telegram",
                FontFamily = Mono,
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x7A, 0xA0)),
                TextWrapping = TextWrapping.Wrap
            });

            return;
        }

        foreach (var message in messages) _history.Children.Add(Line(message));
    }

    /// <summary>Строка вида [время] &lt;ник&gt; текст — как в старых чатах.</summary>
    private static TextBlock Line(ChatMessage message)
    {
        var line = new TextBlock
        {
            FontFamily = Mono,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 3)
        };

        line.Inlines.Add(new Run($"[{message.Time:HH:mm}] ")
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0x5B, 0x7E))
        });

        line.Inlines.Add(new Run($"<{message.Author}> ")
        {
            Foreground = new SolidColorBrush(message.FromParent
                ? Color.FromRgb(0x7F, 0xC5, 0xFF)
                : Color.FromRgb(0x00, 0xE6, 0x76)),
            FontWeight = FontWeights.Bold
        });

        line.Inlines.Add(new Run(message.Text)
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xE4, 0xF7))
        });

        return line;
    }
}
