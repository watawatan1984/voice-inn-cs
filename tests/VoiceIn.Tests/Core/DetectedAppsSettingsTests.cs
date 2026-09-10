using System.Collections.Generic;
using System.Text.Json;
using VoiceIn.Core;
using Xunit;

namespace VoiceIn.Tests.Core;

/// <summary>
/// AppSettings.DetectedApps (検出済みアプリ履歴) / DetectedAppInfo の JSON シリアライズ/
/// デシリアライズ契約のテスト。AppSettingsTests.cs / AppSettingsCategoryTests.cs と同じ方針
/// (SettingsManager が使う JsonSerializerOptions { WriteIndented = true,
/// PropertyNameCaseInsensitive = true } を模す) に倣う。
///
/// SettingsManager.Instance / HistoryManager.Instance / Logger には一切触れない
/// (これらはシングルトン初回アクセス時に %AppData%\VoiceIn を作成してしまうため)。
/// </summary>
public class DetectedAppsSettingsTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void DefaultAppSettings_DetectedApps_IsEmptyNotNull()
    {
        var settings = new AppSettings();

        Assert.NotNull(settings.DetectedApps);
        Assert.Empty(settings.DetectedApps);
    }

    [Fact]
    public void DefaultDetectedAppInfo_HasExpectedDefaultValues()
    {
        var info = new DetectedAppInfo();

        Assert.Equal(string.Empty, info.TitleSample);
        Assert.Equal("STD", info.AutoCategory);
        Assert.Null(info.UserCategory);
    }

    [Fact]
    public void RoundTrip_DetectedAppWithUserCategory_PreservesAllFields()
    {
        var original = new AppSettings();
        original.DetectedApps["chrome"] = new DetectedAppInfo
        {
            TitleSample = "Google - Google Chrome",
            AutoCategory = "STD",
            UserCategory = "BIZ"
        };

        string json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(restored);
        Assert.True(restored!.DetectedApps.ContainsKey("chrome"));
        var entry = restored.DetectedApps["chrome"];
        Assert.Equal("Google - Google Chrome", entry.TitleSample);
        Assert.Equal("STD", entry.AutoCategory);
        Assert.Equal("BIZ", entry.UserCategory);
    }

    [Fact]
    public void RoundTrip_DetectedAppWithNullUserCategory_PreservesNull()
    {
        var original = new AppSettings();
        original.DetectedApps["code"] = new DetectedAppInfo
        {
            TitleSample = "main.cs - Visual Studio Code",
            AutoCategory = "DEV",
            UserCategory = null
        };

        string json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(restored);
        Assert.Null(restored!.DetectedApps["code"].UserCategory);
        Assert.Equal("DEV", restored.DetectedApps["code"].AutoCategory);
    }

    [Fact]
    public void RoundTrip_MultipleDetectedApps_PreservesAllEntriesIndependently()
    {
        var original = new AppSettings();
        original.DetectedApps["chrome"] = new DetectedAppInfo { TitleSample = "t1", AutoCategory = "STD", UserCategory = null };
        original.DetectedApps["slack"] = new DetectedAppInfo { TitleSample = "t2", AutoCategory = "BIZ", UserCategory = "BIZ" };
        original.DetectedApps["notion"] = new DetectedAppInfo { TitleSample = "t3", AutoCategory = "DOC", UserCategory = null };

        string json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal(3, restored!.DetectedApps.Count);
        Assert.Equal("t1", restored.DetectedApps["chrome"].TitleSample);
        Assert.Equal("BIZ", restored.DetectedApps["slack"].UserCategory);
        Assert.Equal("DOC", restored.DetectedApps["notion"].AutoCategory);
    }

    [Fact]
    public void Serialize_UsesSnakeCasePropertyNames_ForDetectedApps()
    {
        var settings = new AppSettings();
        settings.DetectedApps["chrome"] = new DetectedAppInfo
        {
            TitleSample = "sample title",
            AutoCategory = "STD",
            UserCategory = "BIZ"
        };

        string json = JsonSerializer.Serialize(settings, Options);

        Assert.Contains("\"detected_apps\"", json);
        Assert.Contains("\"title_sample\"", json);
        Assert.Contains("\"auto_category\"", json);
        Assert.Contains("\"user_category\"", json);

        // C# の PascalCase 名がそのまま出力されていないことも確認
        Assert.DoesNotContain("\"TitleSample\"", json);
        Assert.DoesNotContain("\"AutoCategory\"", json);
        Assert.DoesNotContain("\"UserCategory\"", json);
    }

    [Fact]
    public void Deserialize_JsonWithoutDetectedAppsKey_FallsBackToEmptyDictionary()
    {
        // 移植前 (detected_apps キーを持たない) の既存 settings.json を模したケース。
        // このキーが無くても例外にならず、既定値 (空辞書、null ではない) で動作すること
        // (後方互換性) を確認する。
        string json = """
        {
          "audio": { "max_record_seconds": 45 },
          "context_aware_enabled": true
        }
        """;

        var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(settings);
        Assert.NotNull(settings!.DetectedApps);
        Assert.Empty(settings.DetectedApps);
        // 他の既定値も道連れで壊れていないことの確認
        Assert.Equal(45, settings.Audio.MaxRecordSeconds);
        Assert.True(settings.ContextAwareEnabled);
    }

    [Fact]
    public void Deserialize_EmptyJsonObject_DetectedAppsFallsBackToEmptyDictionary()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}", Options);

        Assert.NotNull(settings);
        Assert.NotNull(settings!.DetectedApps);
        Assert.Empty(settings.DetectedApps);
    }

    [Fact]
    public void Deserialize_JsonWithUnknownFieldInsideDetectedAppEntry_DoesNotThrowAndIgnoresIt()
    {
        string json = """
        {
          "detected_apps": {
            "chrome": {
              "title_sample": "Google - Google Chrome",
              "auto_category": "STD",
              "user_category": null,
              "totally_unknown_field": "ignored"
            }
          }
        }
        """;

        AppSettings? settings = null;
        var exception = Record.Exception(() =>
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
        });

        Assert.Null(exception);
        Assert.NotNull(settings);
        Assert.Equal("Google - Google Chrome", settings!.DetectedApps["chrome"].TitleSample);
        Assert.Null(settings.DetectedApps["chrome"].UserCategory);
    }

    [Fact]
    public void RoundTrip_JapaneseTitleSample_PreservesTextExactly()
    {
        // title_sample には日本語のウィンドウタイトル (文書名など) が入りうる。
        var original = new AppSettings();
        original.DetectedApps["winword"] = new DetectedAppInfo
        {
            TitleSample = "議事録_2026年度計画.docx - Word",
            AutoCategory = "DOC",
            UserCategory = null
        };

        string json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal("議事録_2026年度計画.docx - Word", restored!.DetectedApps["winword"].TitleSample);
    }
}
