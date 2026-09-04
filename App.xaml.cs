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
    /// <summary>
    /// settings.Dictionary (辞書置換ルール) への同時アクセスを保護するロック。
    /// バックグラウンドスレッドでの辞書置換処理 (本クラス) と、
    /// 設定画面での保存処理 (Ui/SettingsWindow.OnSaveAndApply) が同時に走ることで
    /// 発生する InvalidOperationException (コレクション変更) を防ぐため、
    /// 両方が同じロックオブジェクトを使用する。
    /// </summary>
    internal static readonly object DictionaryLock = new();

    private Mutex? _mutex;
    private bool _mutexOwned;
    private Forms.NotifyIcon? _notifyIcon;
    private OverlayWindow? _overlayWindow;
    private SettingsWindow? _settingsWindow;
    private HistoryWindow? _historyWindow;
    private SetupWindow? _setupWindow;

    private readonly KeyboardHook _keyboardHook = new();
    private readonly AudioRecorder _audioRecorder = new();

    private WindowInfo? _targetWindow;

    // OnKeyPressed/OnKeyReleased (UIスレッド) と Task.Run 内の finally (スレッドプールスレッド) の
    // 両方から読み書きされるため、volatile でスレッド間のメモリ可視性を保証する。
    private volatile bool _isProcessing = false;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 二重起動防止
        _mutex = new Mutex(true, "VoiceIn_Application_SingleInstance_Mutex", out bool createdNew);
        _mutexOwned = createdNew;
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

        // 初回起動判定: GEMINI_API_KEY / GROQ_API_KEY のどちらも未設定ならセットアップウィザードを開く
        // (移植元 Python 版 src/main.py の check_first_run と同じ判定)。
        // OnStartup の中で同期的に ShowDialog() を呼ぶと起動処理をブロックしてしまうため、
        // Dispatcher.BeginInvoke でメッセージループが回り始めてから (オーバーレイ表示・トレイアイコン
        // 初期化が完了した後) モードレスに Show() する。
        bool hasGeminiKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_API_KEY"));
        bool hasGroqKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GROQ_API_KEY"));
        if (!hasGeminiKey && !hasGroqKey)
        {
            Dispatcher.BeginInvoke(new Action(OpenSetupWizard));
        }
    }

    private void SetupNotifyIcon()
    {
        // 状態別のトレイアイコンを起動時に一度だけ生成してキャッシュしておく
        // (GDI ハンドルリークを避けるため。詳細は Ui/TrayIcons.cs のクラスコメント参照)。
        TrayIcons.Initialize();

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Voice In - 音声入力ツール",
            Visible = true,
            Icon = TrayIcons.GetIcon("idle", SettingsManager.Instance.CurrentProvider)
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

        menu.Items.Add("セットアップウィザード...", null, (s, e) => OpenSetupWizard());
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
        SetAppState("idle");
        _notifyIcon?.ShowBalloonTip(1500, "Voice In", $"AIプロバイダを {provider} に切り替えました。", Forms.ToolTipIcon.Info);
    }

    /// <summary>
    /// アプリの状態 (idle/recording/processing/success/error) をオーバーレイと
    /// タスクトレイアイコンの両方に反映する。状態変更はここに一元化しており、
    /// 個別に _overlayWindow.SetState(...) を直接呼ばないこと (トレイだけ状態が
    /// 取り残されるため)。UI スレッド・バックグラウンドスレッド (Task.Run や
    /// Task.Delay().ContinueWith の中) のどちらから呼んでも安全。
    /// </summary>
    private void SetAppState(string state)
    {
        // OverlayWindow.SetState は内部で Dispatcher.Invoke するため、
        // 呼び出し元のスレッドを問わず安全に呼び出せる。
        _overlayWindow?.SetState(state);

        if (_notifyIcon == null)
        {
            return;
        }

        // NotifyIcon.Icon への代入は UI スレッドで行う必要があるため、明示的に marshal する。
        // OnAutoStop と同様、シャットダウン中/済みの Dispatcher に Invoke すると
        // 未処理例外でプロセスが落ちうるため、事前チェックと try/catch で保護する。
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            Dispatcher.Invoke(() =>
            {
                if (_notifyIcon != null)
                {
                    _notifyIcon.Icon = TrayIcons.GetIcon(state, SettingsManager.Instance.CurrentProvider);
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SetAppState の Dispatcher.Invoke に失敗しました: {ex.Message}");
        }
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
            SetAppState("recording");
            _audioRecorder.Start(audioSettings.InputDevice, audioSettings.MaxRecordSeconds);
        }
        catch (Exception ex)
        {
            SetAppState("error");
            _notifyIcon?.ShowBalloonTip(2000, "Voice In エラー", $"録音の開始に失敗しました: {ex.Message}", Forms.ToolTipIcon.Error);
            ResetOverlayDelayed(1500);
        }
    }

    private void OnAutoStop()
    {
        // スレッドプールのタイマスレッドから呼ばれるため、シャットダウン中/済みの
        // Dispatcher に対して呼び出すと未処理例外でプロセスが落ちうる。事前チェックに加え、
        // チェックと呼び出しの間の競合にも備えて try/catch で保護する。
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            Dispatcher.Invoke(() => OnKeyReleased());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OnAutoStop の Dispatcher.Invoke に失敗しました: {ex.Message}");
        }
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
            SetAppState("idle");
            return;
        }

        _isProcessing = true;
        SetAppState("processing");

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

                // "STD" は「コンテキスト認識が無効」「該当カテゴリなし」のいずれでも返るため、
                // ContextAwareEnabled が真かつ実際にカテゴリを検出できた場合のみ
                // カテゴリ別プロンプトを使用する。それ以外は素のプロンプトをそのまま使う。
                bool useCategoryPrompt = settings.ContextAwareEnabled && category != "STD";

                string promptText;
                string currentProvider = SettingsManager.Instance.CurrentProvider.ToLowerInvariant();

                if (currentProvider == "groq")
                {
                    promptText = useCategoryPrompt && settings.CategoryPrompts.TryGetValue(category, out var catPrompt)
                        ? catPrompt
                        : settings.Prompts.GroqRefineSystemPrompt;
                }
                else
                {
                    promptText = useCategoryPrompt && settings.CategoryPrompts.TryGetValue(category, out var catPrompt)
                        ? $"{settings.Prompts.GeminiTranscribePrompt}\n\n【追加コンテキスト指示 ({category})】\n{catPrompt}"
                        : settings.Prompts.GeminiTranscribePrompt;
                }

                var provider = AiProviderFactory.CreateProvider(currentProvider);
                string text = await provider.TranscribeAsync(wavPath, promptText);

                // 辞書置換
                // ・置換中に設定画面側で Dictionary が変更されても影響を受けないよう、
                //   DictionaryLock (Ui/SettingsWindow.OnSaveAndApply と共有) の下でスナップショットを取る。
                // ・Dictionary の列挙順序は保証されない (string.GetHashCode がプロセスごとに
                //   ランダム化されるため) ので、キー文字列長の降順に明示ソートしてから適用し、
                //   部分文字列衝突による連鎖置換や起動ごとの結果ぶれを防ぐ。
                KeyValuePair<string, string>[] dictSnapshot;
                lock (DictionaryLock)
                {
                    dictSnapshot = settings.Dictionary
                        .OrderByDescending(kv => kv.Key.Length)
                        .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                        .ToArray();
                }

                foreach (var (k, v) in dictSnapshot)
                {
                    if (!string.IsNullOrEmpty(k))
                    {
                        text = text.Replace(k, v);
                    }
                }

                // 履歴保存
                HistoryManager.Instance.AppendItem(text, null, currentProvider);

                SetAppState("success");

                // 自動貼り付け
                if (!string.IsNullOrWhiteSpace(text) && audioSettings.AutoPaste)
                {
                    IntPtr hwnd = _targetWindow?.Hwnd ?? IntPtr.Zero;
                    bool pasted = await TextPaster.PasteTextAsync(text, hwnd, audioSettings.PasteDelayMs);
                    if (!pasted)
                    {
                        _notifyIcon?.ShowBalloonTip(2000, "Voice In", "自動貼り付けに失敗しました。変換結果は履歴から確認できます。", Forms.ToolTipIcon.Warning);
                    }
                }

                ResetOverlayDelayed(1000);
            }
            catch (Exception ex)
            {
                HistoryManager.Instance.AppendItem(string.Empty, ex.Message, SettingsManager.Instance.CurrentProvider);
                SetAppState("error");
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
            try
            {
                SetAppState("idle");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ResetOverlayDelayed の継続処理に失敗しました: {ex.Message}");
            }
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
                    SetAppState("idle");
                    // ホットキー設定が変更された可能性があるため、KeyboardHook のキャッシュを更新する
                    _keyboardHook.RefreshHoldKey();
                };
            }
            _settingsWindow.Show();
            _settingsWindow.Activate();
        });
    }

    private void OpenSetupWizard()
    {
        Dispatcher.Invoke(() =>
        {
            if (_setupWindow == null || !_setupWindow.IsLoaded)
            {
                _setupWindow = new SetupWindow();
                _setupWindow.SettingsSaved += () =>
                {
                    UpdateTrayMenu();
                    SetAppState("idle");
                    // ホットキー設定が変更された可能性があるため、KeyboardHook のキャッシュを更新する
                    _keyboardHook.RefreshHoldKey();
                };
            }
            _setupWindow.Show();
            _setupWindow.Activate();
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
        // 後片付けの一部が失敗しても (例: 所有していない Mutex の解放など)
        // シャットダウン自体は必ず継続させるため、OnExit 全体を try/catch で保護する。
        try
        {
            _keyboardHook.Dispose();
            _audioRecorder.Dispose();

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }

            // 状態別にキャッシュしたトレイアイコンをまとめて破棄する (Ui/TrayIcons.cs 参照)。
            TrayIcons.DisposeAll();

            // 自分が所有している (=作成した) Mutex のみ解放する。
            // 二重起動時は createdNew=false であり、所有していない Mutex に対して
            // ReleaseMutex() を呼ぶと ApplicationException が発生する。
            if (_mutexOwned)
            {
                _mutex?.ReleaseMutex();
            }
            _mutex?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OnExit のクリーンアップ処理に失敗しました: {ex.Message}");
        }
        finally
        {
            base.OnExit(e);
        }
    }
}
