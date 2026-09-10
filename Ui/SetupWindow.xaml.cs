using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VoiceIn.Ai;
using VoiceIn.Audio;
using VoiceIn.Core;

namespace VoiceIn.Ui;

/// <summary>
/// 初回セットアップウィザード。
/// 「ようこそ」「AIプロバイダとAPIキー」「マイクデバイス」「操作キー」「完了」の
/// 5ページ構成で、初回起動時に自動で開かれるほか、タスクトレイメニューからいつでも再実行できる。
/// </summary>
public partial class SetupWindow : Window
{
    /// <summary>保存が完了したことを呼び出し元 (App.xaml.cs) へ伝えるイベント。SettingsWindow.SettingsSaved と同じ役割。</summary>
    public event Action? SettingsSaved;

    private readonly UIElement[] _pages;

    private static readonly string[] PageTitles =
    [
        "ようこそ",
        "AI プロバイダと API キー",
        "マイクデバイス",
        "操作キー",
        "完了"
    ];

    private int _currentPage;

    // InitializeComponent() の実行中 (XAML パース中) に RadioButton.IsChecked などの
    // 変更イベントが早期発火すると、まだ未接続の他フィールド (PanelGeminiFields 等) への
    // アクセスで NullReferenceException になりうる。InitializeComponent 完了後に true にし、
    // それより前にイベントハンドラが呼ばれても何もしないようにするガード。
    private bool _initialized;

    // _pages の中で「マイクデバイス」ページ (PageDevice) が何番目かを覚えておく。
    // ShowPage() がこのページから離れるときにマイクテストを自動的に止めるために使う。
    private readonly int _devicePageIndex;

    // マイクテスト (レベルメーター) 専用の AudioRecorder。Ui/SettingsWindow.xaml.cs と同じ方針:
    // App.xaml.cs がホットキー録音用に保持するインスタンスとは別物であり、Start()/Stop() による
    // 実録音には一切使わず、StartMonitoring/StopMonitoring のみを呼ぶ。
    private readonly AudioRecorder _micTestRecorder = new();

    // マイクテストのバー表示更新用タイマー。NAudio のキャプチャコールバック (別スレッド) から
    // 直接 UI を更新しないよう、UI スレッドの DispatcherTimer でポーリングする。
    private DispatcherTimer? _micTestTimer;

    public SetupWindow()
    {
        InitializeComponent();

        _pages = [PageWelcome, PageProvider, PageDevice, PageControls, PageFinish];
        _devicePageIndex = Array.IndexOf(_pages, PageDevice);
        _initialized = true;

        LoadDefaults();
        UpdateProviderPanels();
        ShowPage(0);

        // マイクが開きっぱなしにならないよう、ウィンドウを閉じたら必ずマイクテストを止める。
        // _micTestRecorder.Dispose() は内部で StopMonitoring() を呼ぶため、ページ移動時の
        // 停止処理 (ShowPage 参照) を経ずに閉じられた場合でも確実にデバイスが解放される。
        Closed += (s, e) =>
        {
            _micTestTimer?.Stop();
            _micTestRecorder.Dispose();
        };
    }

    private void LoadDefaults()
    {
        var settings = SettingsManager.Instance.Settings;

        // プロバイダ (未設定時は SettingsManager.CurrentProvider の既定値である gemini を選択)
        string curProvider = SettingsManager.Instance.CurrentProvider.ToLowerInvariant();
        bool groqSelected = curProvider == "groq";
        bool localSelected = curProvider == "local";
        RbGemini.IsChecked = !groqSelected && !localSelected;
        RbGroq.IsChecked = groqSelected;
        RbLocal.IsChecked = localSelected;

        // API キー: 設定済みでも実際の値は表示せず、ステータス表示のみ行う (SettingsWindow と同じ方針)
        InitializeApiKeyField(PwdGeminiApiKey, TxtGeminiApiKeyVisible, LblGeminiApiKeyStatus, "GEMINI_API_KEY");
        InitializeApiKeyField(PwdGroqApiKey, TxtGroqApiKeyVisible, LblGroqApiKeyStatus, "GROQ_API_KEY");

        // Gemini モデル
        TxtGeminiModel.Text = Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? "gemini-2.5-flash";

        // マイク一覧 (SettingsWindow と同じ構成: index 0 = 既定のデバイス)
        var mics = AudioRecorder.GetInputDevices();
        CmbMicDevice.Items.Clear();
        CmbMicDevice.Items.Add("既定のデバイス");
        int selectedMicIndex = 0;
        for (int i = 0; i < mics.Count; i++)
        {
            CmbMicDevice.Items.Add($"[{mics[i].Index}] {mics[i].Name}");
            if (settings.Audio.InputDevice.HasValue && settings.Audio.InputDevice.Value == mics[i].Index)
            {
                selectedMicIndex = i + 1;
            }
        }
        CmbMicDevice.SelectedIndex = selectedMicIndex;

        // 録音キー
        string holdKey = settings.Audio.HoldKey?.ToLowerInvariant() ?? "alt_l";
        CmbHoldKey.SelectedIndex = holdKey switch
        {
            "alt_r" => 1,
            "ctrl_l" => 2,
            "ctrl_r" => 3,
            _ => 0
        };

        ChkAutoPaste.IsChecked = settings.Audio.AutoPaste;
    }

    /// <summary>
    /// API キー入力欄を初期化する。キーが既に設定されていても実際の値は表示せず、
    /// 「設定済み」であることが分かるステータス表示のみ行う (SettingsWindow.InitializeApiKeyField と同じ考え方)。
    /// </summary>
    private static void InitializeApiKeyField(PasswordBox pwd, System.Windows.Controls.TextBox txt, TextBlock status, string envKey)
    {
        pwd.Password = string.Empty;
        txt.Text = string.Empty;
        txt.Visibility = Visibility.Collapsed;
        pwd.Visibility = Visibility.Visible;

        string? existing = Environment.GetEnvironmentVariable(envKey);
        status.Text = string.IsNullOrEmpty(existing)
            ? "未設定"
            : "設定済み (空欄のまま進めても変更されません。変更する場合のみ入力してください)";
    }

    private void OnProviderChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        UpdateProviderPanels();
        UpdateNavState();
    }

    /// <summary>現在ラジオボタンで選択されているプロバイダ名 ("gemini"/"groq"/"local") を返す。</summary>
    private string GetSelectedProvider()
    {
        if (RbGroq.IsChecked == true) return "groq";
        if (RbLocal.IsChecked == true) return "local";
        return "gemini";
    }

    private void UpdateProviderPanels()
    {
        string provider = GetSelectedProvider();
        PanelGeminiFields.Visibility = provider == "gemini" ? Visibility.Visible : Visibility.Collapsed;
        PanelGroqFields.Visibility = provider == "groq" ? Visibility.Visible : Visibility.Collapsed;
        PanelLocalFields.Visibility = provider == "local" ? Visibility.Visible : Visibility.Collapsed;

        if (provider == "local")
        {
            // モデルのダウンロード状態はここで初めて必要になるため、表示するたびに反映する
            // (ネットワーク I/O は行わない。ファイル存在確認のみ)。
            RefreshLocalModelStatusText();
        }
    }

    /// <summary>
    /// ローカル (オフライン) 選択時に、現在のモデルサイズ設定がダウンロード済みかどうかを案内する。
    /// ここではダウンロードそのものは行わせず (任意項目)、未取得なら設定画面へ誘導するに留める。
    /// ModelDownloader.ModelExists はファイル存在確認のみでネットワーク I/O を行わない。
    /// </summary>
    private void RefreshLocalModelStatusText()
    {
        string modelSize = SettingsManager.Instance.Settings.Local.ModelSize;
        LblLocalModelStatus.Text = ModelDownloader.ModelExists(modelSize)
            ? $"現在のモデル設定 ({modelSize}) は既にダウンロード済みです。"
            : $"現在のモデル設定 ({modelSize}) はまだダウンロードされていません。";
    }

    private void OnGeminiApiKeyPasswordChanged(object sender, RoutedEventArgs e) => OnApiKeyFieldChanged();

    private void OnGeminiApiKeyTextChanged(object sender, TextChangedEventArgs e) => OnApiKeyFieldChanged();

    private void OnGroqApiKeyPasswordChanged(object sender, RoutedEventArgs e) => OnApiKeyFieldChanged();

    private void OnGroqApiKeyTextChanged(object sender, TextChangedEventArgs e) => OnApiKeyFieldChanged();

    private void OnApiKeyFieldChanged()
    {
        if (!_initialized) return;
        UpdateNavState();
    }

    private void OnToggleGeminiApiKeyVisibility(object sender, RoutedEventArgs e)
    {
        ToggleApiKeyVisibility(PwdGeminiApiKey, TxtGeminiApiKeyVisible);
    }

    private void OnToggleGroqApiKeyVisibility(object sender, RoutedEventArgs e)
    {
        ToggleApiKeyVisibility(PwdGroqApiKey, TxtGroqApiKeyVisible);
    }

    /// <summary>
    /// PasswordBox (マスク表示) と TextBox (平文表示) の表示/非表示を切り替える。
    /// 切り替え時に現在の入力値をもう一方のコントロールへ引き継ぐ (SettingsWindow と同じ実装)。
    /// </summary>
    private static void ToggleApiKeyVisibility(PasswordBox pwd, System.Windows.Controls.TextBox txt)
    {
        bool currentlyPlainText = txt.Visibility == Visibility.Visible;
        if (currentlyPlainText)
        {
            pwd.Password = txt.Text;
            txt.Visibility = Visibility.Collapsed;
            pwd.Visibility = Visibility.Visible;
        }
        else
        {
            txt.Text = pwd.Password;
            pwd.Visibility = Visibility.Collapsed;
            txt.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// 現在表示されている方 (マスクされた PasswordBox または平文の TextBox) から入力値を取得する。
    /// </summary>
    private static string ReadApiKeyInput(PasswordBox pwd, System.Windows.Controls.TextBox txt)
    {
        string raw = txt.Visibility == Visibility.Visible ? txt.Text : pwd.Password;
        return raw.Trim();
    }

    /// <summary>
    /// 現在選択中のプロバイダについて、入力欄またはすでに設定済みの環境変数のいずれかに
    /// API キーがあるかどうかを判定する。「次へ」ボタンの活性/非活性判定に使う。
    /// </summary>
    private bool HasUsableApiKey()
    {
        string provider = GetSelectedProvider();
        if (provider == "local")
        {
            // ローカルは API キーが不要なため、常に「次へ」を許可する。
            return true;
        }

        bool geminiSelected = provider == "gemini";
        string envKey = geminiSelected ? "GEMINI_API_KEY" : "GROQ_API_KEY";
        string input = geminiSelected
            ? ReadApiKeyInput(PwdGeminiApiKey, TxtGeminiApiKeyVisible)
            : ReadApiKeyInput(PwdGroqApiKey, TxtGroqApiKeyVisible);

        if (!string.IsNullOrEmpty(input))
        {
            return true;
        }
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envKey));
    }

    private void ShowPage(int index)
    {
        // マイクデバイスのページから他のページへ移動するときは、開いたままのマイクテストを
        // 必ず止める (「ページを離れたら止める」要件。ウィンドウを閉じたときの停止は
        // コンストラクタで登録した Closed ハンドラが別途担う)。
        if (_currentPage == _devicePageIndex && index != _devicePageIndex)
        {
            StopMicTest();
        }

        _currentPage = index;
        for (int i = 0; i < _pages.Length; i++)
        {
            _pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }

        TxtStepIndicator.Text = $"ステップ {index + 1} / {_pages.Length} ・ {PageTitles[index]}";

        if (index == _pages.Length - 1)
        {
            UpdateSummary();
        }

        UpdateNavState();
    }

    private void UpdateNavState()
    {
        if (!_initialized) return;

        BtnBack.IsEnabled = _currentPage > 0;
        BtnNext.Content = _currentPage == _pages.Length - 1 ? "完了" : "次へ";

        bool canProceed = true;
        if (_currentPage == 1)
        {
            // プロバイダ/APIキーのページ: 選択中プロバイダの API キーが
            // 入力欄にも環境変数にも無い場合は「次へ」を無効にする。
            canProceed = HasUsableApiKey();
        }
        BtnNext.IsEnabled = canProceed;
    }

    private void UpdateSummary()
    {
        string providerLabel = GetSelectedProvider() switch
        {
            "groq" => "Groq",
            "local" => "ローカル (オフライン)",
            _ => "Gemini"
        };

        string micLabel = CmbMicDevice.SelectedIndex <= 0
            ? "既定のデバイス"
            : CmbMicDevice.SelectedItem?.ToString() ?? "既定のデバイス";

        string holdKeyLabel = CmbHoldKey.SelectedIndex switch
        {
            1 => "alt_r (右Alt)",
            2 => "ctrl_l (左Ctrl)",
            3 => "ctrl_r (右Ctrl)",
            _ => "alt_l (左Alt)"
        };

        string autoPasteLabel = (ChkAutoPaste.IsChecked ?? true) ? "有効" : "無効";

        TxtSummary.Text =
            $"AI プロバイダ: {providerLabel}\n" +
            $"マイク入力デバイス: {micLabel}\n" +
            $"録音キー: {holdKeyLabel}\n" +
            $"自動貼り付け: {autoPasteLabel}";
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_currentPage == _pages.Length - 1)
        {
            SaveAndFinish();
            return;
        }
        ShowPage(_currentPage + 1);
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_currentPage > 0)
        {
            ShowPage(_currentPage - 1);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// マイクテストのトグルボタン。モニタリング中でなければ開始し、モニタリング中であれば停止する
    /// (Ui/SettingsWindow.OnToggleMicTest と同じ方針)。
    /// </summary>
    private void OnToggleMicTest(object sender, RoutedEventArgs e)
    {
        if (_micTestRecorder.IsMonitoring)
        {
            StopMicTest();
        }
        else
        {
            StartMicTest();
        }
    }

    /// <summary>
    /// 現在 CmbMicDevice で選択されているデバイスに対してマイクテストを開始する。
    /// デバイスが他アプリで使用中・無効化されている場合は AudioRecorder.StartMonitoring が
    /// 例外を投げるので、ここで catch して「何が起きたか分かるメッセージ」を表示する。
    /// </summary>
    private void StartMicTest()
    {
        int? deviceIndex = CmbMicDevice.SelectedIndex <= 0 ? null : CmbMicDevice.SelectedIndex - 1;

        try
        {
            _micTestRecorder.StartMonitoring(deviceIndex);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"マイクテストを開始できませんでした。デバイスが他のアプリで使用中か、無効になっている可能性があります。\n\n{ex.Message}",
                "Voice In - マイクテスト",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        PbMicTestLevel.Value = 0;
        BtnToggleMicTest.Content = "⏹ マイクテスト停止";

        if (_micTestTimer == null)
        {
            _micTestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _micTestTimer.Tick += OnMicTestTimerTick;
        }
        _micTestTimer.Start();
    }

    /// <summary>
    /// マイクテストを停止する。トグルボタン・ページ移動時・ウィンドウを閉じたときの
    /// いずれからも呼ばれる。モニタリングしていない状態で呼んでも安全。
    /// </summary>
    private void StopMicTest()
    {
        _micTestTimer?.Stop();
        _micTestRecorder.StopMonitoring();
        PbMicTestLevel.Value = 0;
        BtnToggleMicTest.Content = "▶ マイクテスト開始";
    }

    /// <summary>
    /// マイクテストのバー表示を更新するタイマーコールバック (Ui/SettingsWindow と同じ方針)。
    /// DispatcherTimer の Tick は UI スレッド上で実行されるため、NAudio のキャプチャコールバック
    /// (別スレッド) から直接 UI を更新することにはならない。
    /// </summary>
    private void OnMicTestTimerTick(object? sender, EventArgs e)
    {
        double rms = _micTestRecorder.CurrentMonitoringRms;
        PbMicTestLevel.Value = AudioSampleProcessor.RmsToBarValue(rms);
    }

    /// <summary>
    /// マイク入力デバイスの選択が変わったときに呼ばれる。マイクテスト中に古いデバイスを
    /// 監視し続けないよう、実行中であれば一旦停止する。
    /// </summary>
    private void OnMicDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_micTestRecorder.IsMonitoring)
        {
            StopMicTest();
        }
    }

    private void SaveAndFinish()
    {
        var settings = SettingsManager.Instance.Settings;

        // プロバイダ (setter が .env へも書き戻す)
        SettingsManager.Instance.CurrentProvider = GetSelectedProvider();

        // API キー: 空欄のまま完了した場合は既存のキーを一切変更しない。
        // 実際に新しい値が入力されたときのみ、環境変数への即時反映と .env への書き戻しを行う
        // (SettingsWindow.OnSaveAndApply と同じ方針)。
        string geminiApiKeyInput = ReadApiKeyInput(PwdGeminiApiKey, TxtGeminiApiKeyVisible);
        if (!string.IsNullOrEmpty(geminiApiKeyInput))
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", geminiApiKeyInput);
            EnvLoader.TryWriteKey("GEMINI_API_KEY", geminiApiKeyInput);
        }

        string groqApiKeyInput = ReadApiKeyInput(PwdGroqApiKey, TxtGroqApiKeyVisible);
        if (!string.IsNullOrEmpty(groqApiKeyInput))
        {
            Environment.SetEnvironmentVariable("GROQ_API_KEY", groqApiKeyInput);
            EnvLoader.TryWriteKey("GROQ_API_KEY", groqApiKeyInput);
        }

        // Gemini モデル
        if (!string.IsNullOrWhiteSpace(TxtGeminiModel.Text))
        {
            string geminiModel = TxtGeminiModel.Text.Trim();
            Environment.SetEnvironmentVariable("GEMINI_MODEL", geminiModel);
            EnvLoader.TryWriteKey("GEMINI_MODEL", geminiModel);
        }

        // マイクデバイス
        settings.Audio.InputDevice = CmbMicDevice.SelectedIndex <= 0
            ? null
            : CmbMicDevice.SelectedIndex - 1;

        // 録音キー
        settings.Audio.HoldKey = CmbHoldKey.SelectedIndex switch
        {
            1 => "alt_r",
            2 => "ctrl_l",
            3 => "ctrl_r",
            _ => "alt_l"
        };

        // 自動貼り付け
        settings.Audio.AutoPaste = ChkAutoPaste.IsChecked ?? true;

        SettingsManager.Instance.Save();
        SettingsSaved?.Invoke();

        MessageBox.Show("セットアップが完了しました。Voice In をお使いいただけます。", "Voice In", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }
}
