using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KidRemote.Ui;

/// <summary>Запрос родительского пароля перед доступом к настройкам из трея.</summary>
internal sealed class PasswordWindow : Window
{
    private readonly PasswordBox _input;
    private readonly TextBlock _error;
    private readonly Func<string, bool> _validate;

    public PasswordWindow(string caption, Func<string, bool> validate)
    {
        _validate = validate;
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
            Text = caption,
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF7, 0xFF)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });

        _input = new PasswordBox { FontSize = 18, Padding = new Thickness(8, 6, 8, 6) };
        _input.KeyDown += OnKeyDown;
        panel.Children.Add(_input);

        _error = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x81)),
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed
        };
        panel.Children.Add(_error);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };

        var cancel = new Button { Content = "Отмена", Width = 96, Height = 30, Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, _) => { DialogResult = false; };

        var ok = new Button { Content = "Открыть", Width = 96, Height = 30, IsDefault = true };
        ok.Click += (_, _) => Submit();

        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) => _input.Focus();
    }

    private void Submit()
    {
        if (_validate(_input.Password))
        {
            DialogResult = true;
            return;
        }

        _error.Text = "Неверный пароль";
        _error.Visibility = Visibility.Visible;
        _input.Clear();
        _input.Focus();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Submit();
    }
}
