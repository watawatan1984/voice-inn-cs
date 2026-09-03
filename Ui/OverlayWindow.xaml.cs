using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VoiceIn.Core;

namespace VoiceIn.Ui;

public partial class OverlayWindow : Window
{
    public event Action? SettingsRequested;
    public event Action? HistoryRequested;
    public event Action? ExitRequested;

    private readonly DoubleAnimation _pulseAnimation;
    private readonly Storyboard _pulseStoryboard;

    public OverlayWindow()
    {
        InitializeComponent();

        _pulseAnimation = new DoubleAnimation
        {
            From = 0.85,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(600),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(_pulseAnimation, this);
        Storyboard.SetTargetProperty(_pulseAnimation, new PropertyPath(OpacityProperty));

        _pulseStoryboard = new Storyboard();
        _pulseStoryboard.Children.Add(_pulseAnimation);

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 保存された位置を復元、なければ右下に配置
        var uiSettings = SettingsManager.Instance.Settings.Ui;
        if (uiSettings.OverlayX.HasValue && uiSettings.OverlayY.HasValue)
        {
            Left = uiSettings.OverlayX.Value;
            Top = uiSettings.OverlayY.Value;
        }
        else
        {
            var workingArea = SystemParameters.WorkArea;
            Left = workingArea.Right - Width - 30;
            Top = workingArea.Bottom - Height - 50;
        }

        SetState("idle");
    }

    public void SetState(string state)
    {
        Dispatcher.Invoke(() =>
        {
            _pulseStoryboard.Stop();
            Opacity = 0.9;

            switch (state.ToLowerInvariant())
            {
                case "recording":
                    IconText.Text = "🎙️";
                    OverlayBorder.Background = new LinearGradientBrush(
                        Color.FromArgb(240, 220, 20, 60),
                        Color.FromArgb(240, 180, 10, 40),
                        new Point(0, 0), new Point(0, 1));
                    OverlayBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 107, 107));
                    _pulseStoryboard.Begin();
                    break;

                case "processing":
                    IconText.Text = "⏳";
                    OverlayBorder.Background = new LinearGradientBrush(
                        Color.FromArgb(240, 255, 193, 7),
                        Color.FromArgb(240, 230, 170, 0),
                        new Point(0, 0), new Point(0, 1));
                    OverlayBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 217, 61));
                    break;

                case "success":
                    IconText.Text = "✅";
                    OverlayBorder.Background = new LinearGradientBrush(
                        Color.FromArgb(240, 46, 204, 113),
                        Color.FromArgb(240, 35, 160, 90),
                        new Point(0, 0), new Point(0, 1));
                    OverlayBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(74, 222, 128));
                    break;

                case "error":
                    IconText.Text = "❌";
                    OverlayBorder.Background = new LinearGradientBrush(
                        Color.FromArgb(240, 176, 0, 32),
                        Color.FromArgb(240, 140, 0, 20),
                        new Point(0, 0), new Point(0, 1));
                    OverlayBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    break;

                default: // idle
                    IconText.Text = "🎤";
                    OverlayBorder.Background = new LinearGradientBrush(
                        Color.FromArgb(220, 60, 60, 60),
                        Color.FromArgb(220, 40, 40, 40),
                        new Point(0, 0), new Point(0, 1));

                    string provider = SettingsManager.Instance.CurrentProvider.ToLowerInvariant();
                    Color borderCol = provider == "groq" 
                        ? Color.FromRgb(245, 80, 54) 
                        : Color.FromRgb(66, 133, 244);
                    OverlayBorder.BorderBrush = new SolidColorBrush(borderCol);
                    break;
            }
        });
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();

            // 移動後の位置を保存
            var uiSettings = SettingsManager.Instance.Settings.Ui;
            uiSettings.OverlayX = Left;
            uiSettings.OverlayY = Top;
            SettingsManager.Instance.Save();
        }
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();

        var mSettings = new MenuItem { Header = "⚙️ 設定..." };
        mSettings.Click += (s, args) => SettingsRequested?.Invoke();
        menu.Items.Add(mSettings);

        var mHistory = new MenuItem { Header = "📜 履歴..." };
        mHistory.Click += (s, args) => HistoryRequested?.Invoke();
        menu.Items.Add(mHistory);

        menu.Items.Add(new Separator());

        var mExit = new MenuItem { Header = "❌ 終了" };
        mExit.Click += (s, args) => ExitRequested?.Invoke();
        menu.Items.Add(mExit);

        menu.IsOpen = true;
    }
}
