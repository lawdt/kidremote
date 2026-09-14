using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace KidRemote.Ui;

/// <summary>Что сделать после успешного ввода пароля за компьютером.</summary>
internal sealed class UnlockWindow : Window
{
    /// <summary>Сколько секунд выдать. Ноль вместе с <see cref="Unlimited"/> означает безлимит.</summary>
    public long Seconds { get; private set; }

    public bool Unlimited { get; private set; }

    public UnlockWindow(long remainingSeconds)
    {
        Title = "KidRemote";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x11, 0x16, 0x26));

        var panel = new StackPanel { Margin = new Thickness(24) };

        panel.Children.Add(new TextBlock
        {
            Text = "Сколько времени выдать?",
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF7, 0xFF)),
            Margin = new Thickness(0, 0, 0, 6)
        });

        panel.Children.Add(new TextBlock
        {
            Text = $"Сейчас на балансе {Core.TimeFormat.Human(remainingSeconds)}",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xA0, 0xC8)),
            Margin = new Thickness(0, 0, 0, 16)
        });

        var row = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(CreateButton("15 минут", () => Apply(15 * 60)));
        row.Children.Add(CreateButton("30 минут", () => Apply(30 * 60)));
        row.Children.Add(CreateButton("1 час", () => Apply(60 * 60)));
        panel.Children.Add(row);

        var unlimited = CreateButton("Безлимит до отмены", () =>
        {
            Unlimited = true;
            DialogResult = true;
        });
        unlimited.Margin = new Thickness(2, 0, 2, 10);
        panel.Children.Add(unlimited);

        var cancel = CreateButton("Отмена", () => { DialogResult = false; });
        cancel.Margin = new Thickness(2, 0, 2, 0);
        panel.Children.Add(cancel);

        Content = panel;
    }

    private void Apply(long seconds)
    {
        Seconds = seconds;
        DialogResult = true;
    }

    private static Button CreateButton(string caption, Action action)
    {
        var button = new Button
        {
            Content = caption,
            Height = 34,
            Margin = new Thickness(2)
        };

        button.Click += (_, _) => action();
        return button;
    }
}
