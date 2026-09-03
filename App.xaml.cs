using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VoiceIn.Ai;
using VoiceIn.Audio;
using VoiceIn.Core;
using VoiceIn.Ui;
using Forms = System.Windows.Forms;

namespace VoiceIn;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private Forms.NotifyIcon? _notifyIcon;
    private OverlayWindow? _overlayWindow;
    private SettingsWindow? _settingsWindow;
    private HistoryWindow? _historyWindow;

    private readonly KeyboardHook _keyboardHook = new();
    private readonly AudioRecorder _audioRecorder = new();

    private WindowInfo? _targetWindow;
    private bool _isProcessing = false;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 二重起動防止
        _mutex = new Mutex(true, "VoiceIn_Application_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Voice In は既に起動しています。", "Voice In", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 環境変数読み込み (.env)
        EnvLoader.Load();

        // オーバーレイUIの表示
        _overlayWindow = new OverlayWindow();
        _overlayWindow.SettingsRequested += OpenSettings;
        _overlayWindow.HistoryRequested += OpenHistory;
        _overlayWindow.ExitRequested += ExitApp;
        _overlayWindow.Show();

        // タスクトレイアイコンの設定
        SetupNotifyIcon();

        // キーフックと録音イベントの接続
        _keyboardHook.KeyPressed += OnKeyPressed;
        _keyboardHook.KeyReleased += OnKeyReleased;
        _audioRecorder.AutoStopRequested += OnAutoStop;

        try
        {
            _keyboardHook.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"グローバルホットキーの初期化に失敗しました: {ex.Message}", "Voice In エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetupNotifyIcon()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Voice In - 音声入力ツール",
            Visible = true,
            Icon = SystemIcons.Application
        };

        UpdateTrayMenu();

        _notifyIcon.DoubleClick += (s, e) =>
        {
            if (_overlayWindow != null)
            {
                if (_overlayWindow.IsVisible) _overlayWindow.Hide();
                else _overlayWindow.Show();
            }
        };
    }

    private void UpdateTrayMenu()
    {
        if (_notifyIcon == null) return;

        var menu = new Forms.ContextMenuStrip();

        string provider = SettingsManager.Instance.CurrentProvider;
        var statusItem = new Forms.ToolStripMenuItem($"現在: {provider}") { Enabled = false };
        menu.Items.Add(statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());

        var geminiItem = new Forms.ToolStripMenuItem("Gemini に切替", null, (s, e) => SwitchProvider("gemini"));
        var groqItem = new Forms.ToolStripMenuItem("Groq に切替", null, (s, e) => SwitchProvider("groq"));

        if (provider.ToLowerInvariant() == "gemini") geminiItem.Checked = true;
        if (provider.ToLowerInvariant() == "groq") groqItem.Checked = true;

        menu.Items.Add(geminiItem);
        menu.Items.Add(groqItem);
        menu.Items.Add(new Forms.ToolStripSeparator());

        menu.Items.Add("設定...", null, (s, e) => OpenSettings());
        menu.Items.Add("履歴...", null, (s, e) => OpenHistory());
        menu.Items.Add(new Forms.ToolStripSeparator());

        menu.Items.Add("表示 / 非表示", null, (s, e) =>
        {
            if (_overlayWindow != null)
            {
                if (_overlayWindow.IsVisible) _overlayWindow.Hide();
                else _overlayWindow.Show();
            }
        });

        menu.Items.Add("終了", null, (s, e) => ExitApp());

        _notifyIcon.ContextMenuStrip = menu;
    }

    private void SwitchProvider(string provider)
    {
        SettingsManager.Instance.CurrentProvider = provider;
        UpdateTrayMenu();
        _overlayWindow?.SetState("idle");
        _notifyIcon?.ShowBalloonTip(1500, "Voice In", $"AIプロバイダを {provider} に切り替えました。", Forms.ToolTipIcon.Info);
    }

    private void OnKeyPressed()
    {
        if (_audioRecorder.IsRecording || _isProcessing)
        {
            return;
        }

        // アクティブウィンドウを記憶
        _targetWindow = WindowDetector.GetActiveWindow();

        var audioSettings = SettingsManager.Instance.Settings.Audio;
        try
        {
            _overlayWindow?.SetState("recording");
            _audioRecorder.Start(audioSettings.InputDevice, audioSettings.MaxRecordSeconds);
        }
        catch (Exception ex)
        {
            _overlayWindow?.SetState("error");
            _notifyIcon?.ShowBalloonTip(2000, "Voice In エラー", $"録音の開始に失敗しました: {ex.Message}", Forms.ToolTipIcon.Error);
            ResetOverlayDelayed(1500);
        }
    }

    private void OnAutoStop()
    {
        Dispatcher.Invoke(() => OnKeyReleased());
    }

    private void OnKeyReleased()
    {
        if (!_audioRecorder.IsRecording || _isProcessing)
        {
            return;
        }

        string? wavPath = _audioRecorder.Stop();

        var audioSettings = SettingsManager.Instance.Settings.Audio;
        if (string.IsNullOrEmpty(wavPath) || _audioRecorder.IsSilence(audioSettings.MinDuration))
        {
            _audioRecorder.Cleanup();
            _overlayWindow?.SetState("idle");
            return;
        }

        _isProcessing = true;
        _overlayWindow?.SetState("processing");

        _ = Task.Run(async () =>
        {
            try
            {
                var settings = SettingsManager.Instance.Settings;
                string category = "STD";
                if (_targetWindow != null)
                {
                    category = WindowDetector.DetectCategory(_targetWindow, settings);
                }

                string promptText;
                string currentProvider = SettingsManager.Instance.CurrentProvider.ToLowerInvariant();

                if (currentProvider == "groq")
                {
                    promptText = settings.CategoryPrompts.TryGetValue(category, out var catPrompt)
                        ? catPrompt
                        : settings.Prompts.GroqRefineSystemPrompt;
                }
                else
                {
                    promptText = settings.CategoryPrompts.TryGetValue(category, out var catPrompt)
                        ? $"{settings.Prompts.GeminiTranscribePrompt}\n\n【追加コンテキスト指示 ({category})】\n{catPrompt}"
                        : settings.Prompts.GeminiTranscribePrompt;
                }

                var provider = AiProviderFactory.CreateProvider(currentProvider);
                string text = await provider.TranscribeAsync(wavPath, promptText);

                // 辞書置換
                foreach (var (k, v) in settings.Dictionary)
                {
                    if (!string.IsNullOrEmpty(k))
                    {
                        text = text.Replace(k, v);
                    }
                }

                // 履歴保存
                HistoryManager.Instance.AppendItem(text, null, currentProvider);

                _overlayWindow?.SetState("success");

                // 自動貼り付け
                if (!string.IsNullOrWhiteSpace(text) && audioSettings.AutoPaste)
                {
                    IntPtr hwnd = _targetWindow?.Hwnd ?? IntPtr.Zero;
                    await TextPaster.PasteTextAsync(text, hwnd, audioSettings.PasteDelayMs);
                }

                ResetOverlayDelayed(1000);
            }
            catch (Exception ex)
            {
                HistoryManager.Instance.AppendItem(string.Empty, ex.Message, SettingsManager.Instance.CurrentProvider);
                _overlayWindow?.SetState("error");
                _notifyIcon?.ShowBalloonTip(3000, "Voice In 変換エラー", ex.Message, Forms.ToolTipIcon.Warning);
                ResetOverlayDelayed(2000);
            }
            finally
            {
                _audioRecorder.Cleanup();
                _isProcessing = false;
            }
        });
    }

    private void ResetOverlayDelayed(int ms)
    {
        Task.Delay(ms).ContinueWith(_ =>
        {
            _overlayWindow?.SetState("idle");
        });
    }

    private void OpenSettings()
    {
        Dispatcher.Invoke(() =>
        {
            if (_settingsWindow == null || !_settingsWindow.IsLoaded)
            {
                _settingsWindow = new SettingsWindow();
                _settingsWindow.SettingsSaved += () =>
                {
                    UpdateTrayMenu();
                    _overlayWindow?.SetState("idle");
                };
            }
            _settingsWindow.Show();
            _settingsWindow.Activate();
        });
    }

    private void OpenHistory()
    {
        Dispatcher.Invoke(() =>
        {
            if (_historyWindow == null || !_historyWindow.IsLoaded)
            {
                _historyWindow = new HistoryWindow();
            }
            _historyWindow.Show();
            _historyWindow.Activate();
        });
    }

    private void ExitApp()
    {
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _keyboardHook.Dispose();
        _audioRecorder.Dispose();

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        _mutex?.ReleaseMutex();
        _mutex?.Dispose();

        base.OnExit(e);
    }
}
