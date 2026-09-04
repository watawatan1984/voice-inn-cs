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
        string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceIn");
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
                    Settings = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load settings: {ex.Message}");
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
            Console.WriteLine($"Failed to save settings: {ex.Message}");
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
