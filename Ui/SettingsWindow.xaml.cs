using System;
using System.Collections.ObjectModel;
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
        var settings = SettingsManager.Instance.Settings;

        // プロバイダ
        string selectedProvider = (CmbProvider.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "gemini";
        SettingsManager.Instance.CurrentProvider = selectedProvider;

        // Geminiモデル
        if (!string.IsNullOrWhiteSpace(TxtGeminiModel.Text))
        {
            Environment.SetEnvironmentVariable("GEMINI_MODEL", TxtGeminiModel.Text.Trim());
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

        // 数値
        if (int.TryParse(TxtMaxRecord.Text, out int maxRec)) settings.Audio.MaxRecordSeconds = maxRec;
        if (int.TryParse(TxtPasteDelay.Text, out int delay)) settings.Audio.PasteDelayMs = delay;

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
}
