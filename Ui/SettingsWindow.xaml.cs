using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VoiceIn.Ai;
using VoiceIn.Audio;
using VoiceIn.Core;

namespace VoiceIn.Ui;

public class DictEntry
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
}

public class CategoryKeywordEntry
{
    public string Keyword { get; set; } = string.Empty;
}

/// <summary>
/// 「検出済みアプリ履歴」一覧 (GridDetectedApps) の表示用モデル。
/// AppSettings.DetectedApps (Dictionary&lt;string, DetectedAppInfo&gt;) の 1 エントリを
/// 画面表示しやすい形に変換したものであり、settings 本体への書き戻しには使わない
/// (書き戻しは OnAssignDetectedAppCategory / OnClearDetectedApps が直接 settings を操作する)。
/// </summary>
public class DetectedAppEntry
{
    public string AppName { get; set; } = string.Empty;
    public string AutoCategory { get; set; } = string.Empty;

    /// <summary>UserCategory が未設定の場合の表示用プレースホルダーを含んだ表示文字列。</summary>
    public string UserCategoryDisplay { get; set; } = string.Empty;

    /// <summary>
    /// 検出時のウィンドウタイトルの例。プライバシー注意: 文書名・チャット相手の名前などを
    /// 含みうる値であり、Core/Logger には絶対に渡さないこと (このプロパティ自体は画面表示専用)。
    /// </summary>
    public string TitleSample { get; set; } = string.Empty;
}

public partial class SettingsWindow : Window
{
    public event Action? SettingsSaved;
    private readonly ObservableCollection<DictEntry> _dictEntries = [];

    // カテゴリ設定 (カテゴリ切り替え時に編集中の内容を失わないよう、
    // 選択中でないカテゴリの編集内容もここに保持しておき、保存時にまとめて
    // settings.AppCategories / settings.CategoryPrompts へ書き戻す。移植元 Python 版
    // (src/ui/settings.py の _build_categories_tab) と同じ「切替時に退避・保存時に一括反映」方式。
    private readonly ObservableCollection<CategoryKeywordEntry> _categoryKeywordEntries = [];
    private Dictionary<string, List<string>> _categoryKeywordsWorking = new();
    private Dictionary<string, string> _categoryPromptsWorking = new();
    private string? _currentCategoryKey;

    // 検出済みアプリ履歴 (GridDetectedApps) の表示用コレクション。分類キーワード/プロンプトと
    // 異なり、この一覧は「保存して適用」を待たずに SettingsManager.Instance.Settings を
    // 直接読み書きする (詳細は LoadDetectedApps / OnAssignDetectedAppCategory 参照)。
    private readonly ObservableCollection<DetectedAppEntry> _detectedAppEntries = [];

    // ローカルモデルのダウンロード中にキャンセルを通知するためのトークンソース。
    // ダウンロード中でないときは null。ウィンドウを閉じたときにも取りこぼさず
    // キャンセルできるよう、Closed イベントでも参照する。
    private CancellationTokenSource? _modelDownloadCts;

    // マイクテスト (レベルメーター) 専用の AudioRecorder。App.xaml.cs がホットキー録音用に
    // 保持しているインスタンスとは別物であり (App.xaml.cs は編集禁止のためインスタンスを
    // 共有できない)、Start()/Stop() による実録音には一切使わず、StartMonitoring/StopMonitoring
    // のみを呼ぶ。録音とモニタリングの二重オープン回避は AudioRecorder 側の static な
    // 排他制御 (Audio/AudioRecorder.cs 参照) が担う。
    private readonly AudioRecorder _micTestRecorder = new();

    // マイクテストのバー表示更新用タイマー。NAudio のキャプチャコールバック (別スレッド) から
    // 直接 UI を更新しないよう、UI スレッドの DispatcherTimer で _micTestRecorder の
    // CurrentMonitoringRms を定期的にポーリングする。
    private DispatcherTimer? _micTestTimer;

    public SettingsWindow()
    {
        InitializeComponent();
        LoadSettings();

        // ウィンドウを閉じた後もバックグラウンドでダウンロードが走り続けないよう、
        // 閉じた時点で確実にキャンセルする。
        Closed += (s, e) => _modelDownloadCts?.Cancel();

        // マイクが開きっぱなしにならないよう、ウィンドウを閉じたら必ずマイクテストを止める。
        // _micTestRecorder.Dispose() は内部で StopMonitoring() を呼ぶため、トグルボタンで
        // 止め忘れていた場合でも確実にデバイスが解放される。
        Closed += (s, e) =>
        {
            _micTestTimer?.Stop();
            _micTestRecorder.Dispose();
        };
    }

    private void LoadSettings()
    {
        var settings = SettingsManager.Instance.Settings;

        // プロバイダ (gemini/groq/local の3択)
        string curProvider = SettingsManager.Instance.CurrentProvider;
        CmbProvider.SelectedIndex = curProvider.ToLowerInvariant() switch
        {
            "groq" => 1,
            "local" => 2,
            _ => 0
        };

        // Gemini API キー: 設定済みでも実際の値は表示せず、ステータス表示のみ行う
        InitializeApiKeyField(PwdGeminiApiKey, TxtGeminiApiKeyVisible, LblGeminiApiKeyStatus, "GEMINI_API_KEY");

        // Groq API キー: 同上
        InitializeApiKeyField(PwdGroqApiKey, TxtGroqApiKeyVisible, LblGroqApiKeyStatus, "GROQ_API_KEY");

        // Geminiモデル
        TxtGeminiModel.Text = Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? "gemini-2.5-flash";

        // マイク一覧
        var mics = AudioRecorder.GetInputDevices();
        CmbMicDevice.Items.Clear();
        CmbMicDevice.Items.Add("既定のデバイス");
        int selectedIndex = 0;

        for (int i = 0; i < mics.Count; i++)
        {
            CmbMicDevice.Items.Add($"[{mics[i].Index}] {mics[i].Name}");
            if (settings.Audio.InputDevice.HasValue && settings.Audio.InputDevice.Value == mics[i].Index)
            {
                selectedIndex = i + 1;
            }
        }
        CmbMicDevice.SelectedIndex = selectedIndex;

        // 録音キー
        string holdKey = settings.Audio.HoldKey?.ToLowerInvariant() ?? "alt_l";
        CmbHoldKey.SelectedIndex = holdKey switch
        {
            "alt_r" => 1,
            "ctrl_l" => 2,
            "ctrl_r" => 3,
            _ => 0
        };

        // 各種数値・フラグ
        TxtMaxRecord.Text = settings.Audio.MaxRecordSeconds.ToString();
        TxtPasteDelay.Text = settings.Audio.PasteDelayMs.ToString();
        TxtMinDuration.Text = settings.Audio.MinDuration.ToString(CultureInfo.InvariantCulture);
        TxtInputGain.Text = settings.Audio.InputGainDb.ToString(CultureInfo.InvariantCulture);
        ChkAutoPaste.IsChecked = settings.Audio.AutoPaste;
        ChkContextAware.IsChecked = settings.ContextAwareEnabled;

        // プロンプト
        TxtGeminiPrompt.Text = settings.Prompts.GeminiTranscribePrompt;
        TxtGroqWhisperPrompt.Text = settings.Prompts.GroqWhisperPrompt;
        TxtGroqRefinePrompt.Text = settings.Prompts.GroqRefineSystemPrompt;

        // 辞書
        _dictEntries.Clear();
        foreach (var (k, v) in settings.Dictionary)
        {
            _dictEntries.Add(new DictEntry { From = k, To = v });
        }
        GridDictionary.ItemsSource = _dictEntries;

        // カテゴリ
        // settings.AppCategories / settings.CategoryPrompts 自体はここではまだ変更しない
        // (保存を押すまでは読み込み専用のコピーを画面上で編集する)。キー・値ともに
        // 独立した新しい Dictionary/List へコピーし、画面編集が settings 側の実体に
        // 影響しないようにする。
        _categoryKeywordsWorking = settings.AppCategories.ToDictionary(
            kv => kv.Key,
            kv => new List<string>(kv.Value));
        _categoryPromptsWorking = new Dictionary<string, string>(settings.CategoryPrompts);

        GridCategoryKeywords.ItemsSource = _categoryKeywordEntries;

        _currentCategoryKey = null;
        CmbCategory.Items.Clear();
        foreach (var categoryKey in _categoryKeywordsWorking.Keys)
        {
            CmbCategory.Items.Add(categoryKey);
        }
        if (CmbCategory.Items.Count > 0)
        {
            CmbCategory.SelectedIndex = 0;
        }

        // 検出済みアプリ履歴の割り当て先コンボボックス。キーワード編集用の CmbCategory と
        // 同じカテゴリ一覧を使うが、選択が連動すると紛らわしいため独立したコントロールにする。
        CmbAssignCategory.Items.Clear();
        foreach (var categoryKey in _categoryKeywordsWorking.Keys)
        {
            CmbAssignCategory.Items.Add(categoryKey);
        }
        if (CmbAssignCategory.Items.Count > 0)
        {
            CmbAssignCategory.SelectedIndex = 0;
        }

        LoadDetectedApps();

        // ローカル (オフライン) 設定
        PopulateLocalModelSizeCombo();
        SetLocalModelSizeSelection(settings.Local.ModelSize);
        ChkLocalUseGpu.IsChecked = settings.Local.UseGpu;
        ChkLocalRefineWithCloud.IsChecked = settings.Local.RefineWithCloud;
        RefreshLocalModelStatus();
    }

    /// <summary>
    /// ModelDownloader.SupportedModelSizes を唯一の情報源として、モデルサイズ選択コンボボックスの
    /// 選択肢を組み立てる。表示テキストにおおよそのファイルサイズを併記し、実際の値
    /// (tiny/base/small/medium/large-v3) は各項目の Tag に保持する (XAML 側にはサイズを
    /// ハードコードしない)。
    /// </summary>
    private void PopulateLocalModelSizeCombo()
    {
        CmbLocalModelSize.Items.Clear();
        foreach (var info in ModelDownloader.SupportedModelSizes)
        {
            CmbLocalModelSize.Items.Add(new ComboBoxItem
            {
                Content = $"{info.Id} ({info.ApproxSizeLabel})",
                Tag = info.Id
            });
        }
    }

    /// <summary>
    /// 指定したモデルサイズ名に対応する項目をコンボボックスで選択する。
    /// 未知のサイズ名 (settings.json の手動編集等) の場合は既定値である "small" を選択する。
    /// </summary>
    private void SetLocalModelSizeSelection(string modelSize)
    {
        for (int i = 0; i < CmbLocalModelSize.Items.Count; i++)
        {
            if (CmbLocalModelSize.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, modelSize, StringComparison.OrdinalIgnoreCase))
            {
                CmbLocalModelSize.SelectedIndex = i;
                return;
            }
        }

        for (int i = 0; i < CmbLocalModelSize.Items.Count; i++)
        {
            if (CmbLocalModelSize.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, "small", StringComparison.OrdinalIgnoreCase))
            {
                CmbLocalModelSize.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>現在コンボボックスで選択されているモデルサイズ名 (tiny/base/small/medium/large-v3) を返す。</summary>
    private string GetSelectedLocalModelSize()
    {
        return (CmbLocalModelSize.SelectedItem as ComboBoxItem)?.Tag as string ?? "small";
    }

    private void OnLocalModelSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshLocalModelStatus();
    }

    /// <summary>
    /// 現在選択中のモデルサイズについて、実際に使われるパス (LocalSettings.ModelPath が
    /// 明示されていればそれを優先し、無ければ既定の保存先) にモデルファイルが存在するかどうかを
    /// 画面に反映する。ネットワーク I/O は一切行わない。
    /// </summary>
    private void RefreshLocalModelStatus()
    {
        string modelSize = GetSelectedLocalModelSize();
        string? explicitPath = SettingsManager.Instance.Settings.Local.ModelPath;
        string effectivePath = string.IsNullOrWhiteSpace(explicitPath)
            ? ModelDownloader.GetModelFilePath(modelSize)
            : explicitPath;

        if (File.Exists(effectivePath))
        {
            LblLocalModelStatus.Text = $"モデルは見つかりました。\n{effectivePath}";
            // 成功・有効状態の色 (#5A9E6F、設定画面の配色に合わせたもの)。
            LblLocalModelStatus.Foreground = new System.Windows.Media.SolidColorBrush(Color.FromRgb(0x5A, 0x9E, 0x6F));
        }
        else
        {
            LblLocalModelStatus.Text = $"モデルが見つかりません。ダウンロードが必要です。\n(想定パス: {effectivePath})";
            // 警告・破壊的操作の色 (#C85A5A、設定画面の配色に合わせたもの)。
            LblLocalModelStatus.Foreground = new System.Windows.Media.SolidColorBrush(Color.FromRgb(0xC8, 0x5A, 0x5A));
        }
    }

    /// <summary>
    /// モデルのダウンロードを実行する。実行前に「おおよそのサイズを提示して確認」「既に存在する
    /// 場合は上書き確認」の 2 段階の確認を行い、進捗表示・キャンセル・完了後の状態表示更新までを
    /// 一通り行う。ネットワーク I/O 自体は VoiceIn.Ai.ModelDownloader に委譲する。
    /// async void だが UI イベントハンドラであり (WPF での唯一の正当な async void の用途)、
    /// 例外は内部で全て catch して MessageBox 表示にとどめ、外へは決して漏らさない。
    /// </summary>
    private async void OnDownloadModel(object sender, RoutedEventArgs e)
    {
        string modelSize = GetSelectedLocalModelSize();
        var sizeInfo = ModelDownloader.FindModelSizeInfo(modelSize);
        string sizeLabel = sizeInfo?.ApproxSizeLabel ?? "不明なサイズ";

        if (ModelDownloader.ModelExists(modelSize))
        {
            var overwriteResult = MessageBox.Show(
                $"{modelSize} モデルは既定の保存先に既にダウンロード済みです。再ダウンロードして上書きしますか?",
                "Voice In - モデルのダウンロード",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (overwriteResult != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var confirmResult = MessageBox.Show(
            $"{modelSize} モデル ({sizeLabel}) をダウンロードします。ネットワーク環境によっては数分かかる場合があります。よろしいですか?",
            "Voice In - モデルのダウンロード",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        _modelDownloadCts = new CancellationTokenSource();
        BtnDownloadModel.IsEnabled = false;
        CmbLocalModelSize.IsEnabled = false;
        BtnCancelModelDownload.Visibility = Visibility.Visible;
        PbModelDownload.Visibility = Visibility.Visible;
        PbModelDownload.Value = 0;
        LblModelDownloadStatus.Visibility = Visibility.Visible;
        LblModelDownloadStatus.Text = "ダウンロードを開始しています...";

        // Progress<T>.Report は生成時 (=ここ、UI スレッド) にキャプチャした SynchronizationContext
        // 上で実行されるため、追加の Dispatcher.Invoke なしでここから直接 UI 要素を更新できる。
        var progress = new Progress<ModelDownloader.ModelDownloadProgress>(p =>
        {
            double approxTotal = p.TotalBytesApprox > 0 ? p.TotalBytesApprox : 1;
            double ratio = Math.Min(0.99, p.BytesDownloaded / approxTotal);
            PbModelDownload.Value = ratio * 100.0;
            LblModelDownloadStatus.Text =
                $"ダウンロード中... {FormatBytes(p.BytesDownloaded)} / 約{FormatBytes(p.TotalBytesApprox)} ({ratio * 100:F0}%)";
        });

        try
        {
            await ModelDownloader.DownloadModelAsync(modelSize, progress, _modelDownloadCts.Token);
            PbModelDownload.Value = 100;
            LblModelDownloadStatus.Text = "ダウンロードが完了しました。";
            MessageBox.Show("モデルのダウンロードが完了しました。", "Voice In", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            LblModelDownloadStatus.Text = "ダウンロードをキャンセルしました。";
        }
        catch (Exception ex)
        {
            LblModelDownloadStatus.Text = "ダウンロードに失敗しました。";
            MessageBox.Show($"モデルのダウンロードに失敗しました: {ex.Message}", "Voice In - エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnDownloadModel.IsEnabled = true;
            CmbLocalModelSize.IsEnabled = true;
            BtnCancelModelDownload.Visibility = Visibility.Collapsed;
            _modelDownloadCts?.Dispose();
            _modelDownloadCts = null;
            RefreshLocalModelStatus();
        }
    }

    private void OnCancelModelDownload(object sender, RoutedEventArgs e)
    {
        _modelDownloadCts?.Cancel();
    }

    /// <summary>バイト数を MB/GB 単位の読みやすい文字列に整形する (表示専用、丸め誤差は許容する)。</summary>
    private static string FormatBytes(long bytes)
    {
        const double Mb = 1024.0 * 1024.0;
        const double Gb = Mb * 1024.0;
        return bytes >= Gb ? $"{bytes / Gb:F2}GB" : $"{bytes / Mb:F1}MB";
    }

    private void OnDeleteDictItem(object sender, RoutedEventArgs e)
    {
        if (GridDictionary.SelectedItem is DictEntry selected)
        {
            _dictEntries.Remove(selected);
        }
    }

    /// <summary>
    /// カテゴリ選択が変わったときに呼ばれる。切り替え前のカテゴリの編集内容
    /// (キーワード一覧・プロンプト) を _categoryKeywordsWorking / _categoryPromptsWorking へ
    /// 退避してから、新しく選択されたカテゴリの内容を画面へ読み込む。
    /// これにより、カテゴリを行き来しても入力中の内容が失われない。
    /// </summary>
    private void OnCategorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is string previousCategory)
        {
            CaptureCurrentCategoryEdits(previousCategory);
        }

        if (CmbCategory.SelectedItem is string newCategory)
        {
            LoadCategoryIntoEditors(newCategory);
            _currentCategoryKey = newCategory;
        }
        else
        {
            _currentCategoryKey = null;
        }
    }

    /// <summary>
    /// 画面 (GridCategoryKeywords / TxtCategoryPrompt) に表示中の内容を、指定したカテゴリの
    /// ものとして _categoryKeywordsWorking / _categoryPromptsWorking へ書き戻す。
    /// カテゴリ切り替え時と保存時 (OnSaveAndApply) の両方から呼ばれる。
    /// </summary>
    private void CaptureCurrentCategoryEdits(string category)
    {
        var keywords = new List<string>();
        foreach (var entry in _categoryKeywordEntries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Keyword))
            {
                keywords.Add(entry.Keyword.Trim());
            }
        }

        _categoryKeywordsWorking[category] = keywords;
        _categoryPromptsWorking[category] = TxtCategoryPrompt.Text;
    }

    /// <summary>
    /// _categoryKeywordsWorking / _categoryPromptsWorking に退避してある、指定カテゴリの内容を
    /// 画面 (GridCategoryKeywords / TxtCategoryPrompt) へ読み込む。
    /// </summary>
    private void LoadCategoryIntoEditors(string category)
    {
        _categoryKeywordEntries.Clear();
        if (_categoryKeywordsWorking.TryGetValue(category, out var keywords))
        {
            foreach (var kw in keywords)
            {
                _categoryKeywordEntries.Add(new CategoryKeywordEntry { Keyword = kw });
            }
        }

        TxtCategoryPrompt.Text = _categoryPromptsWorking.TryGetValue(category, out var prompt) ? prompt : string.Empty;
    }

    private void OnDeleteCategoryKeywordItem(object sender, RoutedEventArgs e)
    {
        if (GridCategoryKeywords.SelectedItem is CategoryKeywordEntry selected)
        {
            _categoryKeywordEntries.Remove(selected);
        }
    }

    // ------------------------------------------------------------------
    // 「既定に戻す」機能 (プロンプト・辞書)。
    //
    // 背景: settings.json に一度でも保存された値は AppSettings のプロパティ既定値より優先され、
    // 以後ずっと使われ続ける。そのため、アプリ更新でプロンプトの不具合を直しても、既に設定を
    // 保存したことがあるユーザーには自動的には届かない (Core/Settings.cs の PromptSettings
    // 冒頭コメント参照)。ここではその復旧手段として、画面上の値だけを既定値に戻すボタンを
    // 用意する。実際に settings.json へ反映されるのは、他の編集と同じく「保存して適用」
    // (OnSaveAndApply) を押したときのみであり、このボタン単体で SettingsManager.Instance.Save()
    // が呼ばれることはない (誤って押しても「キャンセル」で復帰できる)。
    //
    // 各 Get/TryGet ヘルパーは PromptSettings/AppSettings の「新規インスタンスのプロパティ既定値」
    // を読むだけの純粋な処理であり、SettingsManager.Instance (%AppData%\VoiceIn の実ファイル) には
    // 一切触れない。ApplyDetectedAppCategoryAssignment と同じく internal static であり、
    // WPF ホスト無しの単体テストから直接呼び出して検証できる。
    // ------------------------------------------------------------------

    internal static string GetDefaultGeminiTranscribePrompt() => new PromptSettings().GeminiTranscribePrompt;

    internal static string GetDefaultGroqWhisperPrompt() => new PromptSettings().GroqWhisperPrompt;

    internal static string GetDefaultGroqRefineSystemPrompt() => new PromptSettings().GroqRefineSystemPrompt;

    /// <summary>
    /// 指定したカテゴリキー (DEV/BIZ/DOC/STD 等) に対応する既定のカテゴリ別プロンプトを返す。
    /// settings.json の手動編集などで追加された、既定値を持たないカスタムカテゴリの場合は
    /// false を返す (呼び出し側は画面の内容を変更せず、既定値が無い旨を伝えること)。
    /// </summary>
    internal static bool TryGetDefaultCategoryPrompt(string categoryKey, out string defaultPrompt)
    {
        if (new AppSettings().CategoryPrompts.TryGetValue(categoryKey, out var value))
        {
            defaultPrompt = value;
            return true;
        }

        defaultPrompt = string.Empty;
        return false;
    }

    /// <summary>
    /// 「文字起こし用プロンプト」(Gemini プロンプト・Groq Whisper プロンプト) を既定値に戻す。
    /// 押した時点では画面上の TextBox の内容を書き換えるだけで、settings.json への反映は
    /// 通常どおり「保存して適用」を押したときのみ行われる。取り消せない操作 (現在の入力内容は
    /// 失われる) のため、既存の履歴全削除 (OnClearDetectedApps) と同じ方針で確認ダイアログを出し、
    /// 既定ボタンを「いいえ」にする。
    /// </summary>
    private void OnResetTranscribePromptsToDefault(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "文字起こし用プロンプト (Gemini プロンプト・Groq Whisper プロンプト) を既定値に戻します。\n" +
            "現在入力されている内容は失われ、元に戻せません。(この画面を「保存して適用」するまでは設定ファイルへ反映されません)\n\n" +
            "よろしいですか?",
            "Voice In",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        TxtGeminiPrompt.Text = GetDefaultGeminiTranscribePrompt();
        TxtGroqWhisperPrompt.Text = GetDefaultGroqWhisperPrompt();
    }

    /// <summary>
    /// 「整形用プロンプト」(Groq 文章整形プロンプト) を既定値に戻す。方針は
    /// OnResetTranscribePromptsToDefault と同じ (画面上のみ即時反映、確認ダイアログの既定は「いいえ」)。
    /// </summary>
    private void OnResetRefinePromptToDefault(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "整形用プロンプト (Groq 文章整形プロンプト) を既定値に戻します。\n" +
            "現在入力されている内容は失われ、元に戻せません。(この画面を「保存して適用」するまでは設定ファイルへ反映されません)\n\n" +
            "よろしいですか?",
            "Voice In",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        TxtGroqRefinePrompt.Text = GetDefaultGroqRefineSystemPrompt();
    }

    /// <summary>
    /// 現在 CmbCategory で選択中のカテゴリの「カテゴリ別プロンプト」を既定値に戻す。
    /// DEV/BIZ/DOC/STD など既定値を持つカテゴリのみが対象で、既定値の無いカスタムカテゴリが
    /// 選択されている場合は画面の内容を変更せず、その旨を伝えるメッセージだけを表示する。
    /// </summary>
    private void OnResetCategoryPromptToDefault(object sender, RoutedEventArgs e)
    {
        if (_currentCategoryKey is not string categoryKey)
        {
            MessageBox.Show("カテゴリを選択してください。", "Voice In", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryGetDefaultCategoryPrompt(categoryKey, out string defaultPrompt))
        {
            MessageBox.Show(
                $"「{categoryKey}」には既定のプロンプトが用意されていません。",
                "Voice In",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"「{categoryKey}」のカテゴリ別プロンプトを既定値に戻します。\n" +
            "現在入力されている内容は失われ、元に戻せません。(この画面を「保存して適用」するまでは設定ファイルへ反映されません)\n\n" +
            "よろしいですか?",
            "Voice In",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        TxtCategoryPrompt.Text = defaultPrompt;
    }

    /// <summary>
    /// 辞書 (単語置換ルール) を既定の空の状態に戻す (画面上の一覧をすべて削除する。
    /// AppSettings.Dictionary の既定値が空の Dictionary であることに対応する)。
    /// 画面上の _dictEntries を空にするだけであり、settings.json への反映は「保存して適用」
    /// (OnSaveAndApply) を押したときのみ行われる。
    /// </summary>
    private void OnResetDictionaryToDefault(object sender, RoutedEventArgs e)
    {
        if (_dictEntries.Count == 0)
        {
            return;
        }

        var result = MessageBox.Show(
            "辞書 (単語置換ルール) をすべて削除し、既定の状態に戻します。\n" +
            "現在の内容は失われ、元に戻せません。(この画面を「保存して適用」するまでは設定ファイルへ反映されません)\n\n" +
            "よろしいですか?",
            "Voice In",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _dictEntries.Clear();
    }

    /// <summary>
    /// SettingsManager.Instance.Settings.DetectedApps (検出済みアプリ履歴) を画面の一覧
    /// (GridDetectedApps) へ読み込む。ウィンドウを開いたときに加え、カテゴリ割り当て・
    /// 履歴クリアの直後にも呼び、画面へ即座に反映する。
    ///
    /// 分類キーワード/カテゴリ別プロンプトの編集 (_categoryKeywordsWorking 等) と異なり、
    /// この一覧は「保存して適用」を待たない (OnAssignDetectedAppCategory / OnClearDetectedApps
    /// が直接 settings を読み書きして即座に保存するため、常に最新の実データを表示する)。
    /// </summary>
    private void LoadDetectedApps()
    {
        var settings = SettingsManager.Instance.Settings;

        KeyValuePair<string, DetectedAppInfo>[] snapshot;
        lock (SettingsLock.Gate)
        {
            settings.DetectedApps ??= [];
            snapshot = settings.DetectedApps.ToArray();
        }

        _detectedAppEntries.Clear();
        foreach (var (appName, info) in snapshot.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            _detectedAppEntries.Add(new DetectedAppEntry
            {
                AppName = appName,
                AutoCategory = info.AutoCategory,
                UserCategoryDisplay = string.IsNullOrEmpty(info.UserCategory) ? "(未割り当て)" : info.UserCategory,
                TitleSample = info.TitleSample
            });
        }

        GridDetectedApps.ItemsSource = _detectedAppEntries;
    }

    /// <summary>
    /// カテゴリ割り当ての中核処理 (SettingsManager や WPF に依存しない、テスト可能な純粋処理)。
    /// 移植元 Python 版 (src/ui/settings.py:709-745) と同じく、以下をまとめて行う:
    /// (1) settings.DetectedApps[appName].user_category に targetCategory を設定する
    ///     (既存インスタンスは書き換えず、新しいインスタンスで丸ごと差し替える方式)。
    /// (2) appName を小文字化したうえで、settings.AppCategories[targetCategory] の
    ///     キーワード一覧へ追加する (大文字小文字を無視した重複チェック付き)。
    /// 両方とも SettingsLock.Gate の下、辞書の参照・小さな値の代入のみで完結させ、
    /// ファイル I/O は一切行わない (呼び出し側が SettingsManager.Instance.Save() を別途行う)。
    /// internal であり、Ui/SettingsWindow を WPF ホスト無しにインスタンス化できないテストからも、
    /// この静的メソッド単体としてなら直接呼び出して検証できる。
    /// </summary>
    internal static void ApplyDetectedAppCategoryAssignment(AppSettings settings, string appName, string targetCategory)
    {
        string lowerAppName = appName.ToLowerInvariant();

        lock (SettingsLock.Gate)
        {
            settings.DetectedApps ??= [];
            if (settings.DetectedApps.TryGetValue(appName, out var existing))
            {
                // 既存インスタンスのフィールドを直接書き換えるのではなく、新しいインスタンスで
                // 丸ごと差し替える (このロックの外で同じ辞書を読む Core.WindowDetector 側との
                // 一貫性を保つための、このコードベース全体で使われている方式)。
                settings.DetectedApps[appName] = new DetectedAppInfo
                {
                    TitleSample = existing.TitleSample,
                    AutoCategory = existing.AutoCategory,
                    UserCategory = targetCategory
                };
            }

            if (!settings.AppCategories.TryGetValue(targetCategory, out var keywordList))
            {
                keywordList = [];
                settings.AppCategories[targetCategory] = keywordList;
            }

            bool alreadyPresent = keywordList.Any(k => string.Equals(k, lowerAppName, StringComparison.OrdinalIgnoreCase));
            if (!alreadyPresent)
            {
                keywordList.Add(lowerAppName);
            }
        }
    }

    /// <summary>
    /// 選択したアプリへカテゴリを割り当てる。移植元 Python 版 (src/ui/settings.py:709-745) と
    /// 同じく、(1) detected_apps[アプリ名].user_category を設定し、(2) アプリ名を小文字化して
    /// そのカテゴリのキーワード一覧へ追加する (大文字小文字を無視した重複チェック付き) を
    /// まとめて行い、即座に SettingsManager.Instance.Save() で保存する。
    ///
    /// このウィンドウの他の編集 (分類キーワード・カテゴリ別プロンプト) は「保存して適用」
    /// (OnSaveAndApply) を押すまで settings 本体には反映されず、_categoryKeywordsWorking /
    /// _categoryPromptsWorking に退避されるだけである。一方この割り当て操作は、移植元と同じく
    /// 「割り当てたら即座に保存される」独立した操作として扱う。そのため、後で
    /// 「保存して適用」が押されたときに settings.AppCategories が _categoryKeywordsWorking の
    /// (この割り当てを知らない) 古い内容で丸ごと上書きされ、ここで追加したキーワードが
    /// 消えてしまわないよう、_categoryKeywordsWorking (および画面に表示中であればキーワード
    /// 編集グリッドの内容) にも同じキーワード追加を反映しておく。
    /// </summary>
    private void OnAssignDetectedAppCategory(object sender, RoutedEventArgs e)
    {
        if (GridDetectedApps.SelectedItem is not DetectedAppEntry selected)
        {
            MessageBox.Show("カテゴリを割り当てるアプリを選択してください。", "Voice In", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (CmbAssignCategory.SelectedItem is not string targetCategory || string.IsNullOrWhiteSpace(targetCategory))
        {
            MessageBox.Show("割り当て先のカテゴリを選択してください。", "Voice In", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = SettingsManager.Instance.Settings;
        string lowerAppName = selected.AppName.ToLowerInvariant();

        ApplyDetectedAppCategoryAssignment(settings, selected.AppName, targetCategory);

        SettingsManager.Instance.Save();

        // 「保存して適用」待ちのステージ済みコピーにも反映する (反映しないと、この後
        // 「保存して適用」を押したときに settings.AppCategories が古い _categoryKeywordsWorking
        // の内容で丸ごと上書きされ、今追加したキーワードが失われてしまう)。
        if (!_categoryKeywordsWorking.TryGetValue(targetCategory, out var workingList))
        {
            workingList = [];
            _categoryKeywordsWorking[targetCategory] = workingList;
        }
        if (!workingList.Any(k => string.Equals(k, lowerAppName, StringComparison.OrdinalIgnoreCase)))
        {
            workingList.Add(lowerAppName);
        }

        // 現在キーワード編集グリッドに表示中のカテゴリと一致する場合は、画面にも即座に反映する。
        if (_currentCategoryKey == targetCategory &&
            !_categoryKeywordEntries.Any(k => string.Equals(k.Keyword, lowerAppName, StringComparison.OrdinalIgnoreCase)))
        {
            _categoryKeywordEntries.Add(new CategoryKeywordEntry { Keyword = lowerAppName });
        }

        LoadDetectedApps();

        MessageBox.Show($"「{selected.AppName}」を {targetCategory} に割り当てました。", "Voice In", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// 検出済みアプリ履歴を全件削除する。取り消せない操作のため、既存の履歴ウィンドウ
    /// (Ui/HistoryWindow.OnDeleteAll) と同じ方針で確認ダイアログを出し、既定ボタンを
    /// 「いいえ」にすることで誤操作を防ぐ。
    /// </summary>
    private void OnClearDetectedApps(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "検出済みアプリ履歴をすべて削除します。この操作は取り消せません。よろしいですか?",
            "Voice In",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var settings = SettingsManager.Instance.Settings;
        lock (SettingsLock.Gate)
        {
            settings.DetectedApps ??= [];
            settings.DetectedApps.Clear();
        }

        SettingsManager.Instance.Save();
        LoadDetectedApps();
    }

    private void OnSaveAndApply(object sender, RoutedEventArgs e)
    {
        // 数値入力の検証を最初に行う。1件でも範囲外・パース不能な値があれば、
        // 値を書き換えたり既定値へ戻したりせず、具体的な原因を伝えて保存処理全体を中断する。
        if (!TryValidateNumericFields(
                out int maxRecordSeconds,
                out int pasteDelayMs,
                out double minDuration,
                out double inputGainDb,
                out string validationError,
                out System.Windows.Controls.TextBox? firstInvalidField))
        {
            MessageBox.Show(validationError, "Voice In - 入力内容を確認してください", MessageBoxButton.OK, MessageBoxImage.Warning);
            firstInvalidField?.Focus();
            return;
        }

        var settings = SettingsManager.Instance.Settings;

        // プロバイダ
        string selectedProvider = (CmbProvider.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "gemini";
        SettingsManager.Instance.CurrentProvider = selectedProvider;

        // Gemini API キー: 空欄のまま保存された場合は既存のキーを一切変更しない
        // (マスクされた欄に何も入力しなかっただけでキーが消えてしまう事故を防ぐため)。
        // 実際に新しい値が入力されたときのみ、環境変数への即時反映と .env への書き戻しを行う。
        string geminiApiKeyInput = ReadApiKeyInput(PwdGeminiApiKey, TxtGeminiApiKeyVisible);
        if (!string.IsNullOrEmpty(geminiApiKeyInput))
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", geminiApiKeyInput);
            EnvLoader.TryWriteKey("GEMINI_API_KEY", geminiApiKeyInput);
        }

        // Groq API キー: 同上
        string groqApiKeyInput = ReadApiKeyInput(PwdGroqApiKey, TxtGroqApiKeyVisible);
        if (!string.IsNullOrEmpty(groqApiKeyInput))
        {
            Environment.SetEnvironmentVariable("GROQ_API_KEY", groqApiKeyInput);
            EnvLoader.TryWriteKey("GROQ_API_KEY", groqApiKeyInput);
        }

        // Geminiモデル
        if (!string.IsNullOrWhiteSpace(TxtGeminiModel.Text))
        {
            string geminiModel = TxtGeminiModel.Text.Trim();
            Environment.SetEnvironmentVariable("GEMINI_MODEL", geminiModel);
            // 次回起動後もモデル設定が保持されるよう .env にも書き戻す。
            EnvLoader.TryWriteKey("GEMINI_MODEL", geminiModel);
        }

        // マイクデバイス
        if (CmbMicDevice.SelectedIndex <= 0)
        {
            settings.Audio.InputDevice = null;
        }
        else
        {
            settings.Audio.InputDevice = CmbMicDevice.SelectedIndex - 1;
        }

        // 録音キー
        settings.Audio.HoldKey = CmbHoldKey.SelectedIndex switch
        {
            1 => "alt_r",
            2 => "ctrl_l",
            3 => "ctrl_r",
            _ => "alt_l"
        };

        // 数値 (メソッド冒頭で範囲検証済みの値をそのまま反映する)
        settings.Audio.MaxRecordSeconds = maxRecordSeconds;
        settings.Audio.PasteDelayMs = pasteDelayMs;
        settings.Audio.MinDuration = minDuration;
        settings.Audio.InputGainDb = inputGainDb;

        settings.Audio.AutoPaste = ChkAutoPaste.IsChecked ?? true;
        settings.ContextAwareEnabled = ChkContextAware.IsChecked ?? true;

        // ローカル (オフライン) 設定
        settings.Local.ModelSize = GetSelectedLocalModelSize();
        settings.Local.UseGpu = ChkLocalUseGpu.IsChecked ?? true;
        settings.Local.RefineWithCloud = ChkLocalRefineWithCloud.IsChecked ?? false;

        // プロンプト
        settings.Prompts.GeminiTranscribePrompt = TxtGeminiPrompt.Text;
        settings.Prompts.GroqWhisperPrompt = TxtGroqWhisperPrompt.Text;
        settings.Prompts.GroqRefineSystemPrompt = TxtGroqRefinePrompt.Text;

        // 辞書
        // バックグラウンドスレッドでの辞書置換処理 (App.OnKeyReleased) と競合し、
        // "Collection was modified" 例外や置換結果の消失が起きないよう、
        // App 側と同じロック (SettingsLock.Gate) の下で更新する。
        lock (SettingsLock.Gate)
        {
            settings.Dictionary.Clear();
            foreach (var entry in _dictEntries)
            {
                if (!string.IsNullOrWhiteSpace(entry.From))
                {
                    settings.Dictionary[entry.From.Trim()] = entry.To ?? string.Empty;
                }
            }
        }

        // カテゴリ
        // 現在画面に表示されているカテゴリの編集内容は、まだ _categoryKeywordsWorking /
        // _categoryPromptsWorking に退避されていない (カテゴリ切り替え時にしか退避しないため)。
        // 保存前にここで一度確定させる。
        if (_currentCategoryKey != null)
        {
            CaptureCurrentCategoryEdits(_currentCategoryKey);
        }

        // settings.AppCategories / settings.CategoryPrompts は、バックグラウンドスレッドからも
        // 読まれている (Core.WindowDetector.DetectCategory が AppCategories を、
        // App.xaml.cs の Task.Run 内が CategoryPrompts.TryGetValue を参照する)。
        // どちらの読み取り側も SettingsLock.Gate を取ってから参照するようになっているため、
        // 既存の Dictionary (単語置換辞書) と同じ SettingsLock.Gate を流用して保護しつつ、
        // ロックの外で新しい Dictionary/List を先に組み立てておき、ロック内では
        // settings 側のプロパティへの参照差し替えのみを行うことで、書き換え自体を
        // 一括・最短時間にする。既存インスタンスを Clear/Add で書き換えるのではなく
        // 新しいインスタンスに丸ごと差し替えるため、差し替え中に読み取り側が既に
        // 列挙を開始していた場合でも (差し替え前の) 古いインスタンスをそのまま
        // 列挙し続けるだけで済み、「コレクションが変更されました」例外にはならない。
        var newAppCategories = new Dictionary<string, List<string>>();
        foreach (var (cat, keywords) in _categoryKeywordsWorking)
        {
            newAppCategories[cat] = new List<string>(keywords);
        }
        var newCategoryPrompts = new Dictionary<string, string>(_categoryPromptsWorking);

        lock (SettingsLock.Gate)
        {
            settings.AppCategories = newAppCategories;
            settings.CategoryPrompts = newCategoryPrompts;
        }

        SettingsManager.Instance.Save();
        SettingsSaved?.Invoke();

        MessageBox.Show("設定を保存し、適用しました。", "Voice In", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// マイクテストのトグルボタン。モニタリング中でなければ開始し、モニタリング中であれば停止する。
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
    /// 録音中 (ホットキーによるものを含む) や、デバイスが他アプリで使用中・無効化されている
    /// 場合は AudioRecorder.StartMonitoring が例外を投げるので、ここで catch して
    /// 「何が起きたか分かるメッセージ」を表示する (黙って失敗させない)。
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
        BtnToggleMicTest.Content = "マイクテスト停止";

        // Tick を二重登録しないよう、既存のタイマーがあれば使い回す (無ければ生成する)。
        if (_micTestTimer == null)
        {
            _micTestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _micTestTimer.Tick += OnMicTestTimerTick;
        }
        _micTestTimer.Start();
    }

    /// <summary>
    /// マイクテストを停止する。トグルボタン・ウィンドウを閉じたとき・デバイス切り替え時の
    /// いずれからも呼ばれる。モニタリングしていない状態で呼んでも安全 (AudioRecorder.StopMonitoring
    /// は冪等)。
    /// </summary>
    private void StopMicTest()
    {
        _micTestTimer?.Stop();
        _micTestRecorder.StopMonitoring();
        PbMicTestLevel.Value = 0;
        BtnToggleMicTest.Content = "マイクテスト開始";
    }

    /// <summary>
    /// マイクテストのバー表示を更新するタイマーコールバック。DispatcherTimer の Tick は
    /// UI スレッド (このウィンドウの Dispatcher) 上で実行されるため、NAudio のキャプチャ
    /// コールバック (別スレッド) から直接 UI を更新することにはならない。
    /// バーの値は移植元 Python 版と同じ min(100, (int)(rms * 300)) (AudioSampleProcessor.RmsToBarValue)。
    /// </summary>
    private void OnMicTestTimerTick(object? sender, EventArgs e)
    {
        double rms = _micTestRecorder.CurrentMonitoringRms;
        PbMicTestLevel.Value = AudioSampleProcessor.RmsToBarValue(rms);
    }

    /// <summary>
    /// マイク入力デバイスの選択が変わったときに呼ばれる。マイクテスト中に古いデバイスを
    /// 監視し続けないよう、実行中であれば一旦停止する (再開はユーザーがボタンを押し直す)。
    /// </summary>
    private void OnMicDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_micTestRecorder.IsMonitoring)
        {
            StopMicTest();
        }
    }

    /// <summary>
    /// 保存前に数値入力欄 (最大録音時間・貼り付け遅延・最小録音時間・入力ゲイン) を検証する。
    /// パースに失敗した場合や範囲外の場合は、その項目を黙って無視したり既定値へ戻したりせず、
    /// 具体的な原因を <paramref name="errorMessage"/> にまとめて呼び出し元に伝える。
    /// 呼び出し元はこれが false のとき保存処理そのものを中断すること。
    /// </summary>
    private bool TryValidateNumericFields(
        out int maxRecordSeconds,
        out int pasteDelayMs,
        out double minDuration,
        out double inputGainDb,
        out string errorMessage,
        out System.Windows.Controls.TextBox? firstInvalidField)
    {
        var errors = new List<string>();
        firstInvalidField = null;

        if (!int.TryParse(TxtMaxRecord.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxRecordSeconds)
            || maxRecordSeconds < 5 || maxRecordSeconds > 600)
        {
            errors.Add("最大録音時間は 5〜600 秒の範囲で入力してください。");
            firstInvalidField ??= TxtMaxRecord;
        }

        if (!int.TryParse(TxtPasteDelay.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out pasteDelayMs)
            || pasteDelayMs < 0 || pasteDelayMs > 1000)
        {
            errors.Add("貼り付け遅延は 0〜1000 ms の範囲で入力してください。");
            firstInvalidField ??= TxtPasteDelay;
        }

        if (!double.TryParse(TxtMinDuration.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out minDuration)
            || minDuration < 0.2 || minDuration > 5.0)
        {
            errors.Add("最小録音時間は 0.2〜5.0 秒の範囲で入力してください。");
            firstInvalidField ??= TxtMinDuration;
        }

        if (!double.TryParse(TxtInputGain.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out inputGainDb)
            || inputGainDb < -30.0 || inputGainDb > 30.0)
        {
            errors.Add("入力ゲインは -30.0〜30.0 dB の範囲で入力してください。");
            firstInvalidField ??= TxtInputGain;
        }

        errorMessage = errors.Count == 0
            ? string.Empty
            : "設定を保存できませんでした。以下の項目を修正してください。\n\n・" + string.Join("\n・", errors);

        return errors.Count == 0;
    }

    /// <summary>
    /// API キー入力欄を初期化する。キーが既に設定されていても実際の値は表示せず、
    /// 「設定済み」であることが分かるステータス表示のみ行う。入力欄自体は常に空の状態から
    /// 始めることで、「空欄のまま保存しても既存キーを消さない」という保存側の前提と一致させる。
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
            : "設定済み (空欄のまま保存すると変更されません。変更する場合のみ入力してください)";
    }

    /// <summary>
    /// 現在表示されている方 (マスクされた PasswordBox または平文の TextBox) から
    /// 入力値を取得する。前後の空白は Gemini モデル欄の保存処理と同様に除去する。
    /// </summary>
    private static string ReadApiKeyInput(PasswordBox pwd, System.Windows.Controls.TextBox txt)
    {
        string raw = txt.Visibility == Visibility.Visible ? txt.Text : pwd.Password;
        return raw.Trim();
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
    /// 切り替え時に現在の入力値をもう一方のコントロールへ引き継ぐことで、
    /// 表示方式を切り替えても入力途中の内容が失われないようにする。
    /// </summary>
    private static void ToggleApiKeyVisibility(PasswordBox pwd, System.Windows.Controls.TextBox txt)
    {
        bool currentlyPlainText = txt.Visibility == Visibility.Visible;
        if (currentlyPlainText)
        {
            // 表示 -> マスク
            pwd.Password = txt.Text;
            txt.Visibility = Visibility.Collapsed;
            pwd.Visibility = Visibility.Visible;
        }
        else
        {
            // マスク -> 表示
            txt.Text = pwd.Password;
            pwd.Visibility = Visibility.Collapsed;
            txt.Visibility = Visibility.Visible;
        }
    }
}
