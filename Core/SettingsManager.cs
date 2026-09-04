using System;
using System.IO;
using System.Text.Json;

namespace VoiceIn.Core;

public class SettingsManager
{
    private static readonly Lazy<SettingsManager> _instance = new(() => new SettingsManager());
    public static SettingsManager Instance => _instance.Value;

    public AppSettings Settings { get; private set; } = new();

    private readonly string _settingsPath;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private SettingsManager()
    {
        // 保存先ディレクトリの決定は EnvLoader.GetAppDataDirectory() に一元化している。
        // 既定 (VOICEIN_PORTABLE 未設定) では従来と全く同じ %AppData%\VoiceIn を返すため、
        // 既存ユーザーの settings.json はそのまま読まれ続ける。
        string baseDir = EnvLoader.GetAppDataDirectory();
        Directory.CreateDirectory(baseDir);
        _settingsPath = Path.Combine(baseDir, "settings.json");

        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                string json = File.ReadAllText(_settingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
                if (loaded != null)
                {
                    FillMissingDictionaryDefaults(loaded);
                    Settings = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load settings", ex);
        }
    }

    /// <summary>
    /// System.Text.Json は辞書型プロパティをセッター経由で丸ごと置換するため、settings.json に
    /// 一部のキーしか書かれていないと (例: "app_categories": {"DEV": [...]})、既定値に存在する
    /// 他のキー (BIZ/DOC/STD) が失われてしまう。移植元 Python 版の deep_merge_dict
    /// (src/core/utils.py:24-33) 相当の処理として、既定値にのみ存在するキーを loaded 側へ
    /// 補完する。ユーザーが明示的に設定したキーは上書きしない。
    ///
    /// Dictionary (ユーザー単語置換辞書) は対象外とする。ユーザーが意図的に空にしている
    /// 可能性があり、既定値 (空辞書) との補完は意味を持たないため。
    /// </summary>
    private static void FillMissingDictionaryDefaults(AppSettings loaded)
    {
        var defaults = new AppSettings();

        loaded.AppCategories ??= [];
        foreach (var (key, value) in defaults.AppCategories)
        {
            if (!loaded.AppCategories.ContainsKey(key))
            {
                loaded.AppCategories[key] = value;
            }
        }

        loaded.CategoryPrompts ??= [];
        foreach (var (key, value) in defaults.CategoryPrompts)
        {
            if (!loaded.CategoryPrompts.ContainsKey(key))
            {
                loaded.CategoryPrompts[key] = value;
            }
        }
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(Settings, _jsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save settings", ex);
        }
    }

    public string CurrentProvider
    {
        get => Environment.GetEnvironmentVariable("AI_PROVIDER") ?? "gemini";
        set
        {
            Environment.SetEnvironmentVariable("AI_PROVIDER", value);

            // トレイメニュー/設定画面でのプロバイダ切り替えが次回起動後も保持されるよう、
            // .env にも書き戻す。書き戻しに失敗しても (ファイル権限など)、実行時の
            // 切り替え自体 (上の SetEnvironmentVariable) は継続させる。
            EnvLoader.TryWriteKey("AI_PROVIDER", value);
        }
    }

    /// <summary>
    /// GEMINI_MODEL を環境変数と .env の両方へ永続化する。CurrentProvider セッターと
    /// 同じ仕組み (EnvLoader.TryWriteKey) を使い、次回起動後もモデル設定を保持する。
    /// </summary>
    public void PersistGeminiModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        Environment.SetEnvironmentVariable("GEMINI_MODEL", model);
        EnvLoader.TryWriteKey("GEMINI_MODEL", model);
    }
}
