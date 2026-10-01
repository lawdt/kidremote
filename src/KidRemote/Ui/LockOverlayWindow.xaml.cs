using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Shapes;
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

    /// <summary>Чем сейчас занята коровка.</summary>
    private enum BugMood
    {
        Wander,
        GoingHome,
        AtHome,
        Peeking,
        Fetching,
        Carrying,
        UnderLock
    }

    /// <summary>Сутки коровки: за время блокировки успевает смениться день и ночь.</summary>
    private static readonly TimeSpan BugDay = TimeSpan.FromMinutes(6);

    /// <summary>Какая часть суток светлая.</summary>
    private const double DayShare = 0.62;

    /// <summary>До этого момента ночи в окошке ещё горит свет.</summary>
    private const double LightsOutShare = 0.82;

    /// <summary>Сколько коровка отсиживается дома, если её не трогают.</summary>
    private static readonly TimeSpan HomeRest = TimeSpan.FromSeconds(30);

    /// <summary>Столько стуков она терпит, прежде чем обидеться и погасить свет.</summary>
    private const int KnocksToAnnoy = 5;

    /// <summary>А столько нужно, чтобы добудиться до обиженной.</summary>
    private const int KnocksToWake = 10;

    private static readonly TimeSpan OfflineNap = TimeSpan.FromSeconds(20);

    /// <summary>Насколько близко к замку нужно загнать коровку, чтобы она нашла тайник.</summary>
    private const double SecretRadius = 90;

    /// <summary>Как часто коровка соблазняется смайликом из переписки.</summary>
    private const double ChatTheftChance = 0.45;

    /// <summary>Передышка у домика после занесённой крошки, секунды.</summary>
    private const double DeliveryPause = 5;

    /// <summary>Сколько коровка гоняется за отнятой крошкой, прежде чем обидеться.</summary>
    private static readonly TimeSpan ChaseLimit = TimeSpan.FromMinutes(1);

    /// <summary>С такого расстояния испуганная коровка может спрятаться дома.</summary>
    private const double HomeLureRadius = 200;

    /// <summary>Но не всегда: иначе её невозможно прогнать мимо домика к другим местам.</summary>
    private const double HomeLureChance = 0.35;

    private static readonly Color[] TreatColors =
    {
        Color.FromRgb(0xE8, 0x7A, 0x2B),
        Color.FromRgb(0xD7, 0x4B, 0x5E),
        Color.FromRgb(0x6F, 0xB2, 0x4C),
        Color.FromRgb(0xE3, 0xC4, 0x4F)
    };

    private const string DefaultHint = "напишите родителям и нажмите Enter";

    /// <summary>Названия дней и месяцев берём русские независимо от настроек системы.</summary>
    private static readonly System.Globalization.CultureInfo Russian = new("ru-RU");

    private readonly DispatcherTimer _bugTimer;
    private readonly DispatcherTimer _hintTimer;
    private readonly DispatcherTimer _speechTimer;
    private readonly Random _random = new();

    private readonly List<FrameworkElement> _treats = new();

    /// <summary>Смайлики, что сейчас видны в переписке: коровка считает их едой.</summary>
    private readonly List<Image> _chatEmoji = new();

    private Point _bugTarget;
    private Point _bugHome;
    private BugMood _bugMood = BugMood.Wander;
    private FrameworkElement? _carried;
    private double _walkPhase;
    private FrameworkElement? _dragged;
    private FrameworkElement? _fetchTreat;
    private readonly DateTime _dayStart = DateTime.Now;
    private DateTime _nextBeg;
    private DateTime _chaseUntil;
    private Point _dragGrabbedAt;
    private Vector _dragOffset;
    private bool _secretFound;
    private int _secretStage;
    private DateTime _secretUntil;
    private Point _secretSpot;
    private DateTime _atHomeSince;
    private DateTime _peekUntil;
    private DateTime _sleepUntil;
    private int _knocks;
    private int _knocksWhileAsleep;
    private bool _sleepEnded = true;
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

        _speechTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _speechTimer.Tick += (_, _) =>
        {
            _speechTimer.Stop();
            if (_bugMood != BugMood.Peeking) SpeechBubble.Visibility = Visibility.Collapsed;
        };

        Bugs.MouseMove += OnLayerMouseMove;
        Bugs.MouseLeftButtonUp += OnLayerMouseUp;

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

        PlaceHome(width, height);
        UpdateDaylight();

        if (ReferenceEquals(_dragged, Bug)) return;

        if (_bugMood == BugMood.AtHome)
        {
            // Обиженная коровка отсыпается и на возню снаружи не отзывается.
            if (DateTime.Now < _sleepUntil) return;

            if (_sleepEnded)
            {
                _sleepEnded = false;
                SetWindowLit(IsBugNight());
            }

            // Ночью из домика не выходит: сначала сидит при свете, потом гасит его и спит.
            if (IsBugNight())
            {
                SetWindowLit(DayPhase() < LightsOutShare);
                return;
            }

            SetWindowLit(false);

            // Пока рядом крутится курсор, коровка отсиживается и наружу не идёт.
            if (CursorNear(_bugHome.X + 29, _bugHome.Y + 25, 130))
            {
                _atHomeSince = DateTime.Now;
                return;
            }

            // Оставили в покое — выходит и принимается таскать вкусности домой.
            if (DateTime.Now - _atHomeSince > HomeRest)
            {
                LeaveHome();
                StartFetch();
            }

            return;
        }

        if (_bugMood == BugMood.UnderLock)
        {
            AdvanceSecret();
            return;
        }

        if (_bugMood == BugMood.Peeking)
        {
            LookAtCursor();

            if (DateTime.Now >= _peekUntil) HideBack();
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

        // Ночь застала снаружи — бросает дела и возвращается в домик.
        if (IsBugNight() && _bugMood is BugMood.Wander or BugMood.Fetching)
        {
            _bugMood = BugMood.GoingHome;
            _bugPauseLeft = 0;
            _fetchTreat = null;
            _bugTarget = new Point(_bugHome.X + 29, _bugHome.Y + 30);
        }

        // Крошку могли утащить мышью — цель едет вместе с ней.
        if (_bugMood == BugMood.Fetching && _fetchTreat is not null)
        {
            _bugTarget = new Point(Canvas.GetLeft(_fetchTreat) - 8, Canvas.GetTop(_fetchTreat) - 8);

            // Добычу держат в курсоре и не отдают — коровка время от времени возмущается.
            if (ReferenceEquals(_dragged, _fetchTreat) && DateTime.Now >= _nextBeg)
            {
                _nextBeg = DateTime.Now.AddSeconds(4 + _random.NextDouble() * 5);
                SayAt(Core.BugTalk.Beg(), Canvas.GetLeft(Bug) + 34, Canvas.GetTop(Bug) - 44);
            }

            // Гонялась долго и без толку — обижается и уходит домой.
            if (_chaseUntil != default && DateTime.Now >= _chaseUntil)
            {
                GiveUpChase();
                return;
            }
        }

        // Курсор проверяем до паузы: сидящая коровка тоже должна срываться с места.
        var fleeing = Flee(x, y, width, height);

        if (!fleeing && _bugPauseLeft > 0)
        {
            _bugPauseLeft -= step;
            return;
        }

        var dx = _bugTarget.X - x;
        var dy = _bugTarget.Y - y;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        if (distance < 6)
        {
            if (_bugMood == BugMood.Carrying)
            {
                StoreTreat();

                if (IsBugNight())
                {
                    EnterHome();
                    return;
                }

                // Днём в домик не заходит: занесла добычу, передохнула и снова за работу.
                StartFetch();
                _bugPauseLeft = DeliveryPause;

                return;
            }

            if (_bugMood == BugMood.GoingHome)
            {
                EnterHome();
                return;
            }

            if (_bugMood == BugMood.Fetching)
            {
                PickUpTreat();
                return;
            }

            // Изредка коровка уходит к себе и там отсиживается.
            if (!fleeing && _random.NextDouble() < 0.18)
            {
                _bugMood = BugMood.GoingHome;
                _bugTarget = new Point(_bugHome.X + 29, _bugHome.Y + 30);
                return;
            }

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
        var nextX = x + dx / distance * move;
        var nextY = y + dy / distance * move;

        Canvas.SetLeft(Bug, nextX);
        Canvas.SetTop(Bug, nextY);

        AnimateLegs(move);

        PlaceCarried(nextX, nextY);

        CheckSecret(nextX, nextY);
    }

    /// <summary>Доля прошедших суток коровки: ноль — рассвет, единица — снова рассвет.</summary>
    private double DayPhase() => (DateTime.Now - _dayStart).TotalSeconds % BugDay.TotalSeconds / BugDay.TotalSeconds;

    private bool IsBugNight() => DayPhase() >= DayShare;

    /// <summary>
    /// Небо задаётся опорными точками суток и плавно перетекает между ними: у горизонта
    /// на рассвете и закате теплее, чем вверху, как это и бывает.
    /// </summary>
    private static readonly (double Phase, Color Top, Color Bottom)[] Sky =
    {
        (0.00, Color.FromRgb(0x01, 0x01, 0x03), Color.FromRgb(0x05, 0x06, 0x0D)),
        (0.05, Color.FromRgb(0x08, 0x0C, 0x1E), Color.FromRgb(0x2A, 0x1E, 0x2A)),
        (0.10, Color.FromRgb(0x16, 0x20, 0x3E), Color.FromRgb(0x6B, 0x3C, 0x33)),
        (0.16, Color.FromRgb(0x1A, 0x2B, 0x4E), Color.FromRgb(0x4E, 0x54, 0x62)),
        (0.30, Color.FromRgb(0x14, 0x22, 0x40), Color.FromRgb(0x2A, 0x49, 0x6E)),
        (0.50, Color.FromRgb(0x13, 0x20, 0x3C), Color.FromRgb(0x27, 0x45, 0x6A)),
        (0.58, Color.FromRgb(0x16, 0x1F, 0x38), Color.FromRgb(0x4A, 0x3C, 0x50)),
        (0.64, Color.FromRgb(0x12, 0x18, 0x2C), Color.FromRgb(0x73, 0x3A, 0x2C)),
        (0.70, Color.FromRgb(0x0A, 0x0E, 0x1C), Color.FromRgb(0x33, 0x1E, 0x24)),
        (0.80, Color.FromRgb(0x03, 0x04, 0x0A), Color.FromRgb(0x0C, 0x0E, 0x18)),
        (1.00, Color.FromRgb(0x01, 0x01, 0x03), Color.FromRgb(0x05, 0x06, 0x0D))
    };

    /// <summary>
    /// Небо рисуется картинкой в один пиксель шириной. Градиентная кисть WPF раскладывает
    /// плавный переход ступенями — на восьми битах соседние строки округляются к одному
    /// значению. Здесь же каждая строка считается отдельно и округляется со смещением,
    /// поэтому граница уровней рассыпается и полос не видно.
    /// </summary>
    private static readonly double[] DitherRow = { -0.375, 0.125, -0.125, 0.375 };

    private Color _skyTop;
    private Color _skyBottom;

    private void UpdateDaylight()
    {
        var phase = DayPhase();

        var index = 0;
        while (index < Sky.Length - 2 && phase > Sky[index + 1].Phase) index++;

        var from = Sky[index];
        var to = Sky[index + 1];

        var span = to.Phase - from.Phase;
        var amount = span <= 0 ? 0 : (phase - from.Phase) / span;

        // Сглаживание по краям отрезка: переходы между опорными точками не видны стыками.
        amount = amount * amount * (3 - 2 * amount);

        var top = Blend(from.Top, to.Top, amount);
        var bottom = Blend(from.Bottom, to.Bottom, amount);

        // Перерисовываем, только когда цвет действительно сменился.
        if (top == _skyTop && bottom == _skyBottom) return;

        _skyTop = top;
        _skyBottom = bottom;

        Background = PaintSky(top, bottom, (int)Math.Round(Math.Max(2, ActualHeight)));
    }

    private static ImageBrush PaintSky(Color top, Color bottom, int height)
    {
        var pixels = new byte[height * 4];

        for (var y = 0; y < height; y++)
        {
            var t = y / (double)(height - 1);
            var shift = DitherRow[y % DitherRow.Length];

            pixels[y * 4 + 0] = Dither(top.B, bottom.B, t, shift);
            pixels[y * 4 + 1] = Dither(top.G, bottom.G, t, shift);
            pixels[y * 4 + 2] = Dither(top.R, bottom.R, t, shift);
            pixels[y * 4 + 3] = 255;
        }

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            1, height, 96, 96, PixelFormats.Bgra32, null, pixels, 4);

        bitmap.Freeze();

        var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
        brush.Freeze();

        return brush;
    }

    private static byte Dither(byte from, byte to, double t, double shift)
    {
        var value = from + (to - from) * t + shift;
        return (byte)Math.Clamp(Math.Round(value), 0, 255);
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        Mix(from.R, to.R, amount),
        Mix(from.G, to.G, amount),
        Mix(from.B, to.B, amount));

    private static byte Mix(byte from, byte to, double amount) =>
        (byte)Math.Round(from + (to - from) * Math.Clamp(amount, 0, 1));

    /// <summary>
    /// Шесть ножек качаются противофазой: правая средняя идёт вместе с левыми крайними,
    /// как у настоящего насекомого.
    /// </summary>
    private void AnimateLegs(double moved)
    {
        _walkPhase += moved * 0.55;

        var swing = Math.Sin(_walkPhase) * 15;

        LegL1R.Angle = swing;
        LegL3R.Angle = swing;
        LegR2R.Angle = swing;

        LegL2R.Angle = -swing;
        LegR1R.Angle = -swing;
        LegR3R.Angle = -swing;
    }

    /// <summary>Домик стоит в левом нижнем углу и не мешает ни расписанию, ни чату.</summary>
    private void PlaceHome(double width, double height)
    {
        if (_bugHome.X > 0) return;

        _bugHome = new Point(56, height - 110);

        Canvas.SetLeft(BugHome, _bugHome.X);
        Canvas.SetTop(BugHome, _bugHome.Y);

        ScatterTreats(width, height);
    }

    /// <summary>Крошки по экрану: коровке есть чем заняться, пока время закрыто.</summary>
    private void ScatterTreats(double width, double height)
    {
        const int count = 9;
        const double margin = 80;

        for (var i = 0; i < count; i++)
        {
            var treat = new Ellipse
            {
                Width = 11,
                Height = 11,
                Fill = new SolidColorBrush(TreatColors[_random.Next(TreatColors.Length)]),
                Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x1A, 0x12)),
                StrokeThickness = 1
            };

            Canvas.SetLeft(treat, margin + _random.NextDouble() * Math.Max(1, width - margin * 2));
            Canvas.SetTop(treat, margin + _random.NextDouble() * Math.Max(1, height - margin * 2));

            treat.Cursor = System.Windows.Input.Cursors.Hand;
            treat.MouseLeftButtonDown += OnTreatGrab;

            Bugs.Children.Add(treat);
            _treats.Add(treat);
        }
    }

    /// <summary>Идём за ближайшей крошкой; когда их не осталось — просто гуляем.</summary>
    private void StartFetch()
    {
        // Смайлик из чата — добыча поинтереснее обычной крошки.
        if (_treats.Count == 0 || _random.NextDouble() < ChatTheftChance) StealFromChat();

        var bug = new Point(Canvas.GetLeft(Bug), Canvas.GetTop(Bug));

        FrameworkElement? nearest = null;
        var best = double.MaxValue;

        foreach (var treat in _treats)
        {
            var point = new Point(Canvas.GetLeft(treat), Canvas.GetTop(treat));
            var distance = Math.Sqrt(Math.Pow(point.X - bug.X, 2) + Math.Pow(point.Y - bug.Y, 2));

            if (distance >= best) continue;

            best = distance;
            nearest = treat;
        }

        if (nearest is null)
        {
            _bugMood = BugMood.Wander;
            PickTarget(Bugs.ActualWidth, Bugs.ActualHeight);
            return;
        }

        _bugMood = BugMood.Fetching;
        _bugPauseLeft = 0;
        _fetchTreat = nearest;
        _bugTarget = new Point(Canvas.GetLeft(nearest) - 8, Canvas.GetTop(nearest) - 8);
    }

    private void PickUpTreat()
    {
        var bug = new Point(Canvas.GetLeft(Bug), Canvas.GetTop(Bug));

        _carried = _treats.FirstOrDefault(treat =>
            Math.Sqrt(Math.Pow(Canvas.GetLeft(treat) - bug.X, 2) + Math.Pow(Canvas.GetTop(treat) - bug.Y, 2)) < 40);

        if (_carried is null)
        {
            StartFetch();
            return;
        }

        _treats.Remove(_carried);
        _fetchTreat = null;
        _chaseUntil = default;
        Panel.SetZIndex(_carried, 1);

        _bugMood = BugMood.Carrying;
        _bugTarget = new Point(_bugHome.X + 29, _bugHome.Y + 30);
    }

    private void GiveUpChase()
    {
        _chaseUntil = default;
        _fetchTreat = null;

        SayAt(Core.BugTalk.Offend(), Canvas.GetLeft(Bug) + 34, Canvas.GetTop(Bug) - 44);

        _bugMood = BugMood.GoingHome;
        _bugPauseLeft = 0;
        _bugTarget = new Point(_bugHome.X + 29, _bugHome.Y + 30);
    }

    /// <summary>Роняет ношу на месте: так бывает, когда находится добыча получше.</summary>
    private void StoreTreatWhereItIs()
    {
        if (_carried is null) return;

        _treats.Add(_carried);
        _carried = null;
    }

    private void StoreTreat()
    {
        if (_carried is null) return;

        Bugs.Children.Remove(_carried);
        _carried = null;
    }

    private void EnterHome()
    {
        _bugMood = BugMood.AtHome;
        _bugPauseLeft = 0;
        _atHomeSince = DateTime.Now;
        Bug.Visibility = Visibility.Collapsed;

        SetWindowLit(IsBugNight());
    }

    /// <summary>Свет в окошке показывает, дома ли коровка.</summary>
    private void SetWindowLit(bool lit)
    {
        HomeWindow.Fill = new SolidColorBrush(lit
            ? Color.FromRgb(0xFF, 0xD9, 0x66)
            : Color.FromRgb(0x3A, 0x27, 0x18));
    }

    private void LeaveHome()
    {
        _bugMood = BugMood.Wander;
        Bug.Visibility = Visibility.Visible;

        SetWindowLit(false);

        Canvas.SetLeft(Bug, _bugHome.X + 40);
        Canvas.SetTop(Bug, _bugHome.Y - 20);

        _bugTarget = new Point(_bugHome.X + 260, _bugHome.Y - 160);
    }

    /// <summary>
    /// Под замком спрятано золотое яблоко. Само оно коровке не попадается — забрести
    /// туда её надо загнать курсором.
    /// </summary>
    private void CheckSecret(double bugX, double bugY)
    {
        if (_secretFound) return;
        if (_bugMood is not (BugMood.Wander or BugMood.Fetching or BugMood.GoingHome)) return;

        var centre = LockCentre();
        if (centre.X <= 0) return;

        var distance = Math.Sqrt(Math.Pow(bugX + 15 - centre.X, 2) + Math.Pow(bugY + 17 - centre.Y, 2));
        if (distance > SecretRadius) return;

        Core.Log.Write("коровка забралась под замок");
        _secretFound = true;

        // Крошку, если несла, оставляет снаружи — под замком руки понадобятся.
        if (_carried is not null) StoreTreatWhereItIs();

        // Заползла целиком: снаружи её не видно, дальше всё происходит под замком.
        Bug.Visibility = Visibility.Collapsed;

        _secretSpot = centre;
        _secretStage = 1;
        _secretUntil = DateTime.Now.AddSeconds(3);
        _bugMood = BugMood.UnderLock;
        _bugPauseLeft = 0;
    }

    /// <summary>
    /// Добыча яблока по шагам: сначала тишина, потом замок начинает подрагивать,
    /// снова тишина — и коровка выбирается наружу с находкой.
    /// </summary>
    private void AdvanceSecret()
    {
        if (DateTime.Now < _secretUntil) return;

        switch (_secretStage)
        {
            case 1:
                _secretStage = 2;
                _secretUntil = DateTime.Now.AddSeconds(5);
                ShakeLock(TimeSpan.FromSeconds(5));
                break;

            case 2:
                _secretStage = 3;
                _secretUntil = DateTime.Now.AddSeconds(3);
                GlyphShake.BeginAnimation(RotateTransform.AngleProperty, null);
                break;

            default:
                _secretStage = 0;

                _carried = CreateGoldenApple(_secretSpot);
                Bugs.Children.Add(_carried);
                Panel.SetZIndex(_carried, 1);

                Bug.Visibility = Visibility.Visible;
                Canvas.SetLeft(Bug, _secretSpot.X - 15);
                Canvas.SetTop(Bug, _secretSpot.Y + 18);

                _bugMood = BugMood.Carrying;
                _bugTarget = new Point(_bugHome.X + 29, _bugHome.Y + 30);
                break;
        }
    }

    private void OnTreatGrab(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement treat) return;

        e.Handled = true;

        // Крошку отобрали прямо из лапок: коровка бросается догонять.
        if (ReferenceEquals(treat, _carried)) TakeAway(treat);

        StartDrag(treat, e);
    }

    /// <summary>Отнятая ноша возвращается в общий список, а коровка переходит в погоню.</summary>
    private void TakeAway(FrameworkElement treat)
    {
        _carried = null;
        _treats.Add(treat);

        _fetchTreat = treat;
        _bugMood = BugMood.Fetching;
        _bugPauseLeft = 0;
        _chaseUntil = DateTime.Now.Add(ChaseLimit);
    }

    /// <summary>Коровку тоже можно поднять, но далеко она в курсоре не удержится.</summary>
    private void OnBugGrab(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_bugMood is BugMood.AtHome or BugMood.UnderLock) return;

        e.Handled = true;
        StartDrag(Bug, e);
    }

    private void StartDrag(FrameworkElement element, System.Windows.Input.MouseButtonEventArgs e)
    {
        var point = e.GetPosition(Bugs);

        _dragged = element;
        _dragGrabbedAt = point;
        _dragOffset = new Vector(point.X - Canvas.GetLeft(element), point.Y - Canvas.GetTop(element));

        Panel.SetZIndex(element, 2);
        Bugs.CaptureMouse();
    }

    private void OnLayerMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragged is null) return;

        var point = e.GetPosition(Bugs);

        var left = point.X - _dragOffset.X;
        var top = point.Y - _dragOffset.Y;

        Canvas.SetLeft(_dragged, left);
        Canvas.SetTop(_dragged, top);

        if (!ReferenceEquals(_dragged, Bug)) return;

        // Ноша едет вместе с коровкой, иначе остаётся висеть на прежнем месте.
        PlaceCarried(left, top);

        // Коровка тяжёлая: дальше шестой части экрана её в курсоре не утащить.
        var limit = Bugs.ActualWidth / 6;
        var carried = Math.Sqrt(Math.Pow(point.X - _dragGrabbedAt.X, 2) + Math.Pow(point.Y - _dragGrabbedAt.Y, 2));

        if (carried >= limit) DropBug();
    }

    /// <summary>Держит ношу у коровки на спине, где бы та ни оказалась.</summary>
    private void PlaceCarried(double bugX, double bugY)
    {
        if (_carried is null) return;

        Canvas.SetLeft(_carried, bugX + 9);
        Canvas.SetTop(_carried, bugY - 12);
    }

    private void OnLayerMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (_dragged is null) return;

        Panel.SetZIndex(_dragged, 0);
        _dragged = null;
        Bugs.ReleaseMouseCapture();
    }

    /// <summary>Сорвалась с курсора: ворчит, приходит в себя и идёт дальше по делам.</summary>
    private void DropBug()
    {
        var x = Canvas.GetLeft(Bug);
        var y = Canvas.GetTop(Bug);

        EndDrag();
        PlaceCarried(x, y);

        SayAt(Core.BugTalk.Drop(), x + 34, y - 44);

        _bugPauseLeft = 1.2;
        _bugAngle = _random.NextDouble() * 360;
        BugRotation.Angle = _bugAngle;

        if (_bugMood == BugMood.Wander) PickTarget(Bugs.ActualWidth, Bugs.ActualHeight);
    }

    /// <summary>Реплика в произвольном месте экрана, а не только у домика.</summary>
    private void SayAt(string text, double x, double y)
    {
        SpeechText.Text = text;
        SpeechBubble.Visibility = Visibility.Visible;

        Canvas.SetLeft(SpeechBubble, Math.Max(8, Math.Min(x, Math.Max(8, HomeLayer.ActualWidth - 280))));
        Canvas.SetTop(SpeechBubble, Math.Max(8, y));

        _speechTimer.Stop();
        _speechTimer.Start();
    }

    /// <summary>Замок подрагивает: от стука или от возни под ним.</summary>
    private void ShakeLock(TimeSpan duration)
    {
        var shake = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(380),
            RepeatBehavior = new RepeatBehavior(duration),
            FillBehavior = FillBehavior.Stop
        };

        shake.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(-7, KeyTime.FromPercent(0.2)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(7, KeyTime.FromPercent(0.5)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(-4, KeyTime.FromPercent(0.75)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));

        GlyphShake.BeginAnimation(RotateTransform.AngleProperty, shake);
    }

    private void OnLockClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShakeLock(TimeSpan.FromMilliseconds(380));
    }

    private Point LockCentre()
    {
        if (Glyph.ActualWidth <= 0 || Bugs.ActualWidth <= 0) return new Point(0, 0);

        try
        {
            return Glyph.TransformToVisual(Bugs)
                .Transform(new Point(Glyph.ActualWidth / 2, Glyph.ActualHeight / 2));
        }
        catch
        {
            return new Point(0, 0);
        }
    }

    /// <summary>Яблоко нарочно крупное: находка должна быть заметной.</summary>
    private static Canvas CreateGoldenApple(Point at)
    {
        var apple = new Canvas { Width = 46, Height = 50 };

        apple.Children.Add(new Ellipse
        {
            Width = 44,
            Height = 40,
            Fill = new RadialGradientBrush(Color.FromRgb(0xFF, 0xE9, 0x8A), Color.FromRgb(0xD4, 0x9A, 0x16))
            {
                GradientOrigin = new Point(0.35, 0.3),
                Center = new Point(0.4, 0.35)
            },
            Stroke = new SolidColorBrush(Color.FromRgb(0x8A, 0x60, 0x0C)),
            StrokeThickness = 1.4
        });

        Canvas.SetTop(apple.Children[0], 10);

        var stem = new Rectangle
        {
            Width = 4,
            Height = 12,
            Fill = new SolidColorBrush(Color.FromRgb(0x6B, 0x4A, 0x21)),
            RadiusX = 2,
            RadiusY = 2
        };

        Canvas.SetLeft(stem, 21);
        Canvas.SetTop(stem, 2);
        apple.Children.Add(stem);

        var leaf = new Ellipse
        {
            Width = 16,
            Height = 9,
            Fill = new SolidColorBrush(Color.FromRgb(0x5A, 0xA8, 0x3C))
        };

        Canvas.SetLeft(leaf, 25);
        Canvas.SetTop(leaf, 3);
        apple.Children.Add(leaf);

        Canvas.SetLeft(apple, at.X - 23);
        Canvas.SetTop(apple, at.Y - 25);

        return apple;
    }

    /// <summary>
    /// Стук в домик. После пятого коровка гасит свет и отсыпается, не отвечая;
    /// разбудить её может только совсем настойчивый — и тогда она кричит.
    /// </summary>
    private void OnHomeClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (_bugMood is not (BugMood.AtHome or BugMood.Peeking)) return;

        if (DateTime.Now < _sleepUntil)
        {
            _knocksWhileAsleep++;
            if (_knocksWhileAsleep < KnocksToWake) return;

            // Достучались: один раз проорала и снова спать.
            _knocksWhileAsleep = 0;
            _sleepUntil = DateTime.Now.Add(OfflineNap);
            _sleepEnded = true;

            Peek(Core.BugTalk.Shout());
            return;
        }

        _knocks++;

        if (_knocks >= KnocksToAnnoy)
        {
            _knocks = 0;
            _knocksWhileAsleep = 0;
            _sleepUntil = DateTime.Now.Add(OfflineNap);
            _sleepEnded = true;

            HideBack();
            SetWindowLit(false);
            return;
        }

        Peek(Core.BugTalk.Random());
    }

    private void Peek(string text)
    {
        Say(text);

        _bugMood = BugMood.Peeking;
        _peekUntil = DateTime.Now.AddSeconds(2.6);

        Bug.Visibility = Visibility.Visible;
        Canvas.SetLeft(Bug, _bugHome.X + 14);
        Canvas.SetTop(Bug, _bugHome.Y + 12);

        LookAtCursor();
    }

    private void HideBack()
    {
        Bug.Visibility = Visibility.Collapsed;
        SpeechBubble.Visibility = Visibility.Collapsed;

        _bugMood = BugMood.AtHome;
        _atHomeSince = DateTime.Now;

        // Пока идёт сон, окно остаётся тёмным. Днём оно и так не горит.
        SetWindowLit(DateTime.Now >= _sleepUntil && IsBugNight());
    }

    private void Say(string text)
    {
        SpeechText.Text = text;
        SpeechBubble.Visibility = Visibility.Visible;

        Canvas.SetLeft(SpeechBubble, _bugHome.X + 70);
        Canvas.SetTop(SpeechBubble, _bugHome.Y - 16);
    }

    /// <summary>Разворачивает коровку мордочкой к курсору.</summary>
    private void LookAtCursor()
    {
        try
        {
            var screen = Forms.Cursor.Position;
            var cursor = PointFromScreen(new Point(screen.X, screen.Y));

            var dx = cursor.X - (Canvas.GetLeft(Bug) + 15);
            var dy = cursor.Y - (Canvas.GetTop(Bug) + 17);

            _bugAngle = Math.Atan2(dy, dx) * 180 / Math.PI + 90;
            BugRotation.Angle = _bugAngle;
        }
        catch
        {
            // Курсор недоступен — оставляем как есть.
        }
    }

    private bool CursorNear(double x, double y, double radius)
    {
        try
        {
            var screen = Forms.Cursor.Position;
            var cursor = PointFromScreen(new Point(screen.X, screen.Y));

            return Math.Sqrt(Math.Pow(cursor.X - x, 2) + Math.Pow(cursor.Y - y, 2)) <= radius;
        }
        catch
        {
            return false;
        }
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

        // За своей крошкой коровка идёт смело, даже если та в курсоре.
        if (_dragged is not null && ReferenceEquals(_dragged, _fetchTreat)) return false;

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

        // Уже спешит домой или тащит добычу — цель менять нельзя, иначе никогда не дойдёт.
        if (_bugMood is BugMood.GoingHome or BugMood.Carrying) return true;

        // Домик неподалёку — иногда коровка ныряет туда, так её и загоняют курсором.
        if (Math.Sqrt(Math.Pow(x - (_bugHome.X + 29), 2) + Math.Pow(y - (_bugHome.Y + 20), 2)) < HomeLureRadius
            && _random.NextDouble() < HomeLureChance)
        {
            _bugMood = BugMood.GoingHome;
            _bugTarget = new Point(_bugHome.X + 29, _bugHome.Y + 30);
            return true;
        }

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

        CollectChatEmoji();

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

    private void CollectChatEmoji()
    {
        _chatEmoji.Clear();

        foreach (var child in ChatLines.Children)
        {
            if (child is not TextBlock line) continue;

            foreach (var inline in line.Inlines)
            {
                if (inline is InlineUIContainer container && container.Child is Image image)
                    _chatEmoji.Add(image);
            }
        }
    }

    /// <summary>
    /// Утаскивает смайлик из переписки: на его месте остаётся пустое место, а сам он
    /// превращается в добычу и уезжает в домик.
    /// </summary>
    private FrameworkElement? StealFromChat()
    {
        var candidates = _chatEmoji.Where(image => image.Opacity > 0.5).ToList();
        if (candidates.Count == 0) return null;

        var victim = candidates[_random.Next(candidates.Count)];

        Point at;

        try
        {
            at = victim.TransformToVisual(Bugs).Transform(new Point(0, 0));
        }
        catch
        {
            return null;
        }

        if (at.X <= 0 || at.Y <= 0) return null;

        var loot = new Image
        {
            Source = victim.Source,
            Width = 18,
            Height = 18,
            Cursor = System.Windows.Input.Cursors.Hand
        };

        loot.MouseLeftButtonDown += OnTreatGrab;

        Canvas.SetLeft(loot, at.X);
        Canvas.SetTop(loot, at.Y);

        Bugs.Children.Add(loot);
        _treats.Add(loot);

        // В переписке смайлик пропадает — до следующей перерисовки чата.
        victim.Opacity = 0;
        Core.Log.Write("коровка утащила смайлик из переписки");

        return loot;
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
