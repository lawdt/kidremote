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

    private readonly DispatcherTimer _bugTimer;
    private readonly Random _random = new();

    private Point _bugTarget;
    private double _bugAngle;
    private double _bugPauseLeft;
    private bool _allowClose;

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
            // Жук иногда замирает — так он выглядит живым, а не заводным.
            if (!fleeing && _random.NextDouble() < 0.4) _bugPauseLeft = 0.5 + _random.NextDouble() * 2.5;
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

    private void PickTarget(double width, double height)
    {
        const double margin = 40;
        _bugTarget = new Point(
            margin + _random.NextDouble() * Math.Max(1, width - margin * 2),
            margin + _random.NextDouble() * Math.Max(1, height - margin * 2));
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

        // Новая реплика проявляется, иначе её легко не заметить на неподвижном экране.
        ChatPanel.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(500),
            FillBehavior = FillBehavior.Stop
        });
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

    /// <summary>Расписание на завтра в углу экрана.</summary>
    internal void ShowSchedule(string title, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0 || title.Length == 0)
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

    private void OnChatInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;

        var text = ChatInput.Text.Trim();
        if (text.Length == 0) return;

        ChatInput.Clear();
        Core.Log.Write("экран блокировки: отправлена реплика");
        MessageSubmitted?.Invoke(text);
    }

    /// <summary>Писать можно только с основного монитора, на остальных поле лишнее.</summary>
    internal void HideChatInput()
    {
        ChatInput.Visibility = Visibility.Collapsed;
        ChatHint.Visibility = Visibility.Collapsed;
        LayoutButton.Visibility = Visibility.Collapsed;
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
