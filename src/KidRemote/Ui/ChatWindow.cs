using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
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
    private readonly WrapPanel _emojis;

    public string Text => _input.Text.Trim();

    public ChatWindow(ChatLog chat)
    {
        _chat = chat;

        Title = "KidRemote";
        Width = 640;
        Height = 720;
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

        _emojis = new WrapPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
        bottom.Children.Add(_emojis);

        // Поле и кнопка отправки в одну строку: так короткая реплика пишется одним движением.
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _input = new TextBox
        {
            MinHeight = 44,
            MaxHeight = 120,
            MaxLength = MaxLength,
            FontFamily = Mono,
            FontSize = 14,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 6, 8, 6)
        };

        _input.KeyDown += OnInputKeyDown;

        var emojiButton = new Button
        {
            Content = "🙂",
            FontFamily = new FontFamily("Segoe UI Emoji"),
            FontSize = 16,
            Width = 44,
            MinHeight = 44,
            Margin = new Thickness(10, 0, 0, 0),
            Focusable = false
        };

        emojiButton.Click += (_, _) =>
        {
            _emojis.Visibility = _emojis.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;

            _input.Focus();
        };

        var send = new Button
        {
            Content = "Отправить",
            Width = 110,
            MinHeight = 44,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = true
        };

        send.Click += (_, _) => Submit();

        Grid.SetColumn(_input, 0);
        Grid.SetColumn(emojiButton, 1);
        Grid.SetColumn(send, 2);
        row.Children.Add(_input);
        row.Children.Add(emojiButton);
        row.Children.Add(send);

        BuildEmojiPanel();
        bottom.Children.Add(row);

        var close = new Button
        {
            Content = "Закрыть",
            Width = 110,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };

        close.Click += (_, _) => { DialogResult = false; };
        bottom.Children.Add(close);

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

    private void BuildEmojiPanel()
    {
        foreach (var emoji in Emoji.Popular)
        {
            var button = new Button
            {
                Content = emoji,
                FontFamily = new FontFamily("Segoe UI Emoji"),
                FontSize = 18,
                Width = 40,
                Height = 34,
                Margin = new Thickness(0, 0, 6, 6),
                Focusable = false
            };

            var value = emoji;
            button.Click += (_, _) =>
            {
                var position = Math.Clamp(_input.CaretIndex, 0, _input.Text.Length);
                _input.Text = _input.Text.Insert(position, value);
                _input.CaretIndex = position + value.Length;
                _input.Focus();
            };

            _emojis.Children.Add(button);
        }
    }

    /// <summary>Enter отправляет, Shift+Enter переносит строку.</summary>
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;

        e.Handled = true;
        Submit();
    }

    private void Submit()
    {
        if (Text.Length == 0) return;
        DialogResult = true;
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

        if (!message.FromParent)
        {
            line.Inlines.Add(new Run(message.Delivered ? "  ✓✓" : "  ⏳")
            {
                Foreground = new SolidColorBrush(message.Delivered
                    ? Color.FromRgb(0x4E, 0xA8, 0x7A)
                    : Color.FromRgb(0x7E, 0x6B, 0x3A))
            });
        }

        return line;
    }
}
