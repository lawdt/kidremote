using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using KidRemote.Interop;
using Forms = System.Windows.Forms;

namespace KidRemote.Ui;

public partial class LockOverlayWindow : Window
{
    private const double BugSpeed = 70;          // пикселей в секунду
    private const double BugFleeSpeed = 260;     // когда убегает от курсора
    private const double BugTurnRate = 220;      // градусов в секунду
    private const double BugFleeRadius = 150;    // на таком расстоянии курсор уже пугает
    private const double BugFleeDistance = 340;  // насколько далеко отбегает

    private const string DefaultHint = "напишите родителям и нажмите Enter";

    /// <summary>Названия дней и месяцев берём русские независимо от настроек системы.</summary>
    private static readonly System.Globalization.CultureInfo Russian = new("ru-RU");

    private readonly DispatcherTimer _bugTimer;
    private readonly DispatcherTimer _hintTimer;
    private readonly Random _random = new();

    private Point _bugTarget;
    private double _bugAngle;
    private double _bugPauseLeft;
    private bool _allowClose;
    private bool _scheduleAllowed = true;

    /// <summary>Ребёнок отправил реплику прямо с экрана блокировки.</summary>
    public event Action<string>? MessageSubmitted;

    public LockOverlayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        _bugTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _bugTimer.Tick += (_, _) => MoveBug();

        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _hintTimer.Tick += (_, _) => ResetHint();

        BuildEmojiPanel();
        EmojiButton.Content = EmojiButtonContent("🙂");

        // Отступы по умолчанию у абзаца делают поле выше, чем нужно.
        ChatInput.Document.PagePadding = new Thickness(0);
        ChatInput.Document.Blocks.Clear();
        ChatInput.Document.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
    }

    /// <summary>Жук ползёт к случайной точке, иногда замирает и выбирает новую.</summary>
    private void MoveBug()
    {
        var width = Bugs.ActualWidth;
        var height = Bugs.ActualHeight;
        if (width < 100 || height < 100) return;

        const double step = 0.033;

        if (_bugPauseLeft > 0)
        {
            _bugPauseLeft -= step;
            return;
        }

        var x = Canvas.GetLeft(Bug);
        var y = Canvas.GetTop(Bug);
        if (double.IsNaN(x) || double.IsNaN(y))
        {
            x = width / 2;
            y = height / 2;
            PickTarget(width, height);
        }

        var fleeing = Flee(x, y, width, height);

        var dx = _bugTarget.X - x;
        var dy = _bugTarget.Y - y;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        if (distance < 6)
        {
            PickTarget(width, height);

            // Настоящая коровка больше сидит, чем ходит: почти всегда делаем долгую паузу.
            if (!fleeing && _random.NextDouble() < 0.85) _bugPauseLeft = 4 + _random.NextDouble() * 16;
            return;
        }

        // Эмодзи нарисовано головой вверх, поэтому к углу направления добавляем прямой.
        var desired = Math.Atan2(dy, dx) * 180 / Math.PI + 90;
        _bugAngle = TurnTowards(_bugAngle, desired, BugTurnRate * step);
        BugRotation.Angle = _bugAngle;

        var move = (fleeing ? BugFleeSpeed : BugSpeed) * step;
        Canvas.SetLeft(Bug, x + dx / distance * move);
        Canvas.SetTop(Bug, y + dy / distance * move);
    }

    /// <summary>
    /// Коровка боится курсора: когда тот подбирается близко, она разворачивается
    /// и удирает в противоположную сторону.
    /// </summary>
    private bool Flee(double x, double y, double width, double height)
    {
        Point cursor;

        try
        {
            var screen = Forms.Cursor.Position;
            cursor = PointFromScreen(new Point(screen.X, screen.Y));
        }
        catch
        {
            return false;
        }

        var dx = x - cursor.X;
        var dy = y - cursor.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance > BugFleeRadius) return false;

        // Курсор ровно на коровке — направление выбираем случайно.
        if (distance < 1)
        {
            var angle = _random.NextDouble() * Math.PI * 2;
            dx = Math.Cos(angle);
            dy = Math.Sin(angle);
            distance = 1;
        }

        const double margin = 30;
        _bugPauseLeft = 0;
        _bugTarget = new Point(
            Math.Clamp(x + dx / distance * BugFleeDistance, margin, Math.Max(margin, width - margin)),
            Math.Clamp(y + dy / distance * BugFleeDistance, margin, Math.Max(margin, height - margin)));

        return true;
    }

    /// <summary>Новая цель неподалёку: короткая перебежка выглядит естественнее броска через весь экран.</summary>
    private void PickTarget(double width, double height)
    {
        const double margin = 40;

        var x = Canvas.GetLeft(Bug);
        var y = Canvas.GetTop(Bug);

        if (double.IsNaN(x) || double.IsNaN(y))
        {
            _bugTarget = new Point(
                margin + _random.NextDouble() * Math.Max(1, width - margin * 2),
                margin + _random.NextDouble() * Math.Max(1, height - margin * 2));

            return;
        }

        var angle = _random.NextDouble() * Math.PI * 2;
        var distance = 60 + _random.NextDouble() * 160;

        _bugTarget = new Point(
            Math.Clamp(x + Math.Cos(angle) * distance, margin, Math.Max(margin, width - margin)),
            Math.Clamp(y + Math.Sin(angle) * distance, margin, Math.Max(margin, height - margin)));
    }

    private static double TurnTowards(double current, double target, double maxStep)
    {
        var diff = (target - current + 540) % 360 - 180;
        if (Math.Abs(diff) <= maxStep) return target;

        return current + Math.Sign(diff) * maxStep;
    }

    /// <summary>Растягивает окно ровно по границам конкретного монитора.</summary>
    internal void BindToScreen(Forms.Screen screen)
    {
        AdjustLayout(screen.Bounds.Width);

        var bounds = screen.Bounds;
        var source = PresentationSource.FromVisual(this);
        var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        Left = bounds.Left / scaleX;
        Top = bounds.Top / scaleY;
        Width = bounds.Width / scaleX;
        Height = bounds.Height / scaleY;
    }

    internal void Render(string phrase)
    {
        // Вместо констатации факта — фраза, ради которой стоит встать.
        Glyph.Text = "🔒";
        Headline.Text = phrase;
        Subtitle.Visibility = Visibility.Collapsed;

        FadeIn();
    }

    private void FadeIn()
    {
        Headline.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(600),
            FillBehavior = FillBehavior.Stop
        });
    }

    private const string HealthText =
        "Играть в это время врачи не рекомендуют: поздние игры сбивают сон и бьют по самочувствию на следующий день.";

    /// <summary>Часы и ночная приписка. Вызывается раз в секунду, пока экран закрыт.</summary>
    internal void UpdateClock(DateTime now, bool lateHours)
    {
        Clock.Text = now.ToString("HH:mm");

        var date = now.ToString("dddd, d MMMM", Russian);
        DateLine.Text = char.ToUpper(date[0], Russian) + date[1..];

        var note = lateHours ? HealthText : string.Empty;
        if (HealthNote.Text != note) HealthNote.Text = note;

        HealthNote.Visibility = lateHours ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Переписка в духе старых чатов: моноширинные строки вида [время] &lt;ник&gt; текст.
    /// Так она читается одним взглядом и не занимает половину экрана пузырями.
    /// </summary>
    internal void ShowChat(IReadOnlyList<Core.ChatMessage> messages)
    {
        ChatLines.Children.Clear();

        if (messages.Count == 0)
        {
            ChatLines.Children.Add(new TextBlock
            {
                Text = "* пока тихо",
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontSize = 15,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0x5B, 0x7E))
            });
        }

        foreach (var message in messages) ChatLines.Children.Add(ChatLine(message));

        ChatPanel.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(() => ChatScroll.ScrollToEnd()), DispatcherPriority.Loaded);

        // Проявляется только свежая строка: анимация всей панели выглядела бы морганием.
        if (ChatLines.Children.Count > 0 && ChatLines.Children[^1] is UIElement last)
        {
            last.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(400),
                FillBehavior = FillBehavior.Stop
            });
        }
    }

    private static TextBlock ChatLine(Core.ChatMessage message)
    {
        var line = new TextBlock
        {
            FontFamily = new FontFamily("Consolas, Courier New"),
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 3)
        };

        line.Inlines.Add(new Run($"[{Core.TimeFormat.ChatStamp(message.Time)}] ")
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

        EmojiRenderer.AppendTo(line.Inlines, message.Text,
            new SolidColorBrush(Color.FromRgb(0xDC, 0xE4, 0xF7)), line.FontSize);

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

    /// <summary>Расписание на завтра в углу экрана.</summary>
    internal void ShowSchedule(string title, IReadOnlyList<string> lines)
    {
        if (!_scheduleAllowed || lines.Count == 0 || title.Length == 0)
        {
            SchedulePanel.Visibility = Visibility.Collapsed;
            return;
        }

        ScheduleTitle.Text = title;
        ScheduleList.ItemsSource = lines;
        SchedulePanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Переключатель раскладки мышью. Нужен потому, что экран блокировки глушит часть
    /// системных сочетаний, и привычный способ сменить язык на нём не срабатывает.
    /// </summary>
    private void OnLayoutClick(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        NativeMethods.PostMessage(handle, NativeMethods.WM_INPUTLANGCHANGEREQUEST,
            new IntPtr(NativeMethods.INPUTLANGCHANGE_FORWARD), IntPtr.Zero);

        // Системе нужно мгновение, чтобы применить раскладку.
        Dispatcher.BeginInvoke(new Action(RefreshLayoutCaption), DispatcherPriority.Background);
        ChatInput.Focus();
    }

    private void RefreshLayoutCaption()
    {
        var thread = NativeMethods.GetWindowThreadProcessId(new WindowInteropHelper(this).Handle, out _);
        var layout = NativeMethods.GetKeyboardLayout(thread);

        // Младшее слово дескриптора раскладки — идентификатор языка.
        var language = (int)(layout.ToInt64() & 0xFFFF);

        LayoutButton.Content = language switch
        {
            0x0419 => "RU",
            0x0409 => "EN",
            0x0422 => "UA",
            0x0423 => "BE",
            _ => System.Globalization.CultureInfo
                     .GetCultureInfo(language).TwoLetterISOLanguageName.ToUpperInvariant()
        };
    }

    private void BuildEmojiPanel()
    {
        foreach (var emoji in Core.Emoji.Popular)
        {
            var button = new Button
            {
                Content = EmojiButtonContent(emoji),
                Width = 40,
                Height = 34,
                Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1C, 0x30)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF7, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x3C, 0x66)),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false
            };

            var value = emoji;
            button.Click += (_, _) => InsertEmoji(value);

            EmojiPanel.Children.Add(button);
        }
    }

    /// <summary>Картинка для кнопки, с запасным вариантом текстом, если отрисовать не вышло.</summary>
    private static object EmojiButtonContent(string emoji)
    {
        var image = EmojiRenderer.Render(emoji, 22);

        return image is null
            ? emoji
            : new Image { Source = image, Width = 20, Height = 20 };
    }

    private void OnEmojiToggle(object sender, RoutedEventArgs e)
    {
        EmojiPanel.Visibility = EmojiPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (EmojiPanel.Visibility == Visibility.Visible) ChatInput.Focus();
    }

    /// <summary>Добавляет смайлик картинкой в конец набранного текста.</summary>
    private void InsertEmoji(string emoji)
    {
        var paragraph = InputParagraph();
        var inline = EmojiRenderer.Inline(emoji, ChatInput.FontSize);

        if (inline is null) paragraph.Inlines.Add(new Run(emoji));
        else paragraph.Inlines.Add(inline);

        ChatInput.CaretPosition = ChatInput.Document.ContentEnd;
        ChatInput.Focus();
    }

    private Paragraph InputParagraph()
    {
        if (ChatInput.Document.Blocks.LastBlock is Paragraph last) return last;

        var paragraph = new Paragraph();
        ChatInput.Document.Blocks.Add(paragraph);
        return paragraph;
    }

    private void OnSendClick(object sender, RoutedEventArgs e) => SubmitInput();

    private void SubmitInput()
    {
        var text = EmojiRenderer.ReadText(ChatInput.Document).Trim();
        if (text.Length == 0) return;

        Core.Log.Write("экран блокировки: отправлена реплика");
        MessageSubmitted?.Invoke(text);
    }

    private void OnChatInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;

        // Поле очищается только после успешной отправки: при отказе текст не пропадёт.
        SubmitInput();
    }

    /// <summary>Писать можно только с основного монитора, на остальных поле лишнее.</summary>
    internal void HideChatInput()
    {
        ChatInput.Visibility = Visibility.Collapsed;
        ChatHint.Visibility = Visibility.Collapsed;
        LayoutButton.Visibility = Visibility.Collapsed;
        EmojiButton.Visibility = Visibility.Collapsed;
        EmojiPanel.Visibility = Visibility.Collapsed;
    }

    internal void ClearChatInput()
    {
        ChatInput.Document.Blocks.Clear();
        ChatInput.Document.Blocks.Add(new Paragraph());
        ChatInput.CaretPosition = ChatInput.Document.ContentEnd;
        ResetHint();
    }

    /// <summary>Короткое сообщение под полем ввода — например, когда пишут слишком часто.</summary>
    internal void ShowChatNotice(string notice)
    {
        ChatHint.Text = notice;
        ChatHint.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x81));

        _hintTimer.Stop();
        _hintTimer.Start();
    }

    private void ResetHint()
    {
        _hintTimer.Stop();
        ChatHint.Text = DefaultHint;
        ChatHint.Foreground = new SolidColorBrush(Color.FromRgb(0x5D, 0x6C, 0x94));
    }

    internal void FocusChatInput()
    {
        if (ChatInput.Visibility != Visibility.Visible) return;

        ChatInput.Focus();
        SafeRefreshLayoutCaption();
    }

    private void SafeRefreshLayoutCaption()
    {
        try
        {
            RefreshLayoutCaption();
        }
        catch
        {
            LayoutButton.Content = "RU";
        }
    }

    /// <summary>
    /// На узком экране боковым панелям не хватает места, и они лезут на фразу по центру.
    /// Поэтому на небольших разрешениях чат ужимаем, а расписание убираем совсем.
    /// </summary>
    private void AdjustLayout(int screenWidth)
    {
        if (screenWidth >= 1600)
        {
            ChatPanel.Width = 620;
            _scheduleAllowed = true;
        }
        else if (screenWidth >= 1280)
        {
            ChatPanel.Width = 460;
            _scheduleAllowed = true;
        }
        else
        {
            ChatPanel.Width = 380;
            _scheduleAllowed = false;
        }

        if (!_scheduleAllowed) SchedulePanel.Visibility = Visibility.Collapsed;

        // Мелкий экран: крупные надписи по центру занимают всю ширину.
        if (screenWidth < 1280)
        {
            Clock.FontSize = 44;
            DateLine.FontSize = 16;
            Glyph.FontSize = 52;
            Headline.FontSize = 30;
            Headline.MaxWidth = 420;
            HealthNote.FontSize = 14;
            HealthNote.MaxWidth = 420;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongAuto(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongAuto(handle, NativeMethods.GWL_EXSTYLE, new IntPtr(style));

        // Убираем системное меню окна: Alt+Space с пунктами «Переместить» и «Закрыть» здесь лишний.
        var basic = NativeMethods.GetWindowLongAuto(handle, NativeMethods.GWL_STYLE).ToInt64();
        basic &= ~(long)NativeMethods.WS_SYSMENU;
        NativeMethods.SetWindowLongAuto(handle, NativeMethods.GWL_STYLE, new IntPtr(basic));

        _bugTimer.Start();
    }

    public void CloseForReal()
    {
        _bugTimer.Stop();
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 не должен убирать блокировку.
        if (!_allowClose) e.Cancel = true;
        base.OnClosing(e);
    }
}
