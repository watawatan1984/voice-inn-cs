using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using VoiceIn.Audio;
using VoiceIn.Core;

namespace VoiceIn.Ui;

public class DictEntry
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
}

public partial class SettingsWindow : Window
{
    public event Action? SettingsSaved;
    private readonly ObservableCollection<DictEntry> _dictEntries = [];

    public SettingsWindow()
    {
        InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        var settings = SettingsManager.Instance.Settings;

        // プロバイダ
        string curProvider = SettingsManager.Instance.CurrentProvider;
        CmbProvider.SelectedIndex = curProvider.ToLowerInvariant() == "groq" ? 1 : 0;

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
    }

    private void OnDeleteDictItem(object sender, RoutedEventArgs e)
    {
        if (GridDictionary.SelectedItem is DictEntry selected)
        {
            _dictEntries.Remove(selected);
        }
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

        // プロンプト
        settings.Prompts.GeminiTranscribePrompt = TxtGeminiPrompt.Text;
        settings.Prompts.GroqWhisperPrompt = TxtGroqWhisperPrompt.Text;
        settings.Prompts.GroqRefineSystemPrompt = TxtGroqRefinePrompt.Text;

        // 辞書
        // バックグラウンドスレッドでの辞書置換処理 (App.OnKeyReleased) と競合し、
        // "Collection was modified" 例外や置換結果の消失が起きないよう、
        // App 側と同じロック (App.DictionaryLock) の下で更新する。
        lock (VoiceIn.App.DictionaryLock)
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
