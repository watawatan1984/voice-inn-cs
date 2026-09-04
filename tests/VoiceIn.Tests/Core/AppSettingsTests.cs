using System.Collections.Generic;
using System.Text.Json;
using VoiceIn.Core;
using Xunit;

namespace VoiceIn.Tests.Core;

/// <summary>
/// AppSettings (および子設定クラス) の JSON シリアライズ/デシリアライズ契約のテスト。
/// SettingsManager が使用する JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true }
/// を模した設定でテストする。
/// </summary>
public class AppSettingsTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void DefaultAppSettings_HasExpectedDefaultValues()
    {
        var settings = new AppSettings();

        Assert.Null(settings.Audio.InputDevice);
        Assert.Equal(0.0, settings.Audio.InputGainDb);
        Assert.Equal(60, settings.Audio.MaxRecordSeconds);
        Assert.Equal(0.2, settings.Audio.MinDuration);
        Assert.True(settings.Audio.AutoPaste);
        Assert.Equal(60, settings.Audio.PasteDelayMs);
        Assert.Equal("alt_l", settings.Audio.HoldKey);

        Assert.Equal("ja", settings.Ui.Language);
        Assert.Null(settings.Ui.OverlayX);
        Assert.Null(settings.Ui.OverlayY);

        Assert.True(settings.ContextAwareEnabled);
        Assert.Empty(settings.Dictionary);

        Assert.Equal(new[] { "DEV", "BIZ", "DOC", "STD" }, settings.AppCategories.Keys);
        Assert.Empty(settings.AppCategories["STD"]);
        Assert.Contains("code", settings.AppCategories["DEV"]);
        Assert.Contains("slack", settings.AppCategories["BIZ"]);
        Assert.Contains("word", settings.AppCategories["DOC"]);

        Assert.Equal(4, settings.CategoryPrompts.Count);
        Assert.True(settings.CategoryPrompts.ContainsKey("DEV"));
        Assert.True(settings.CategoryPrompts.ContainsKey("BIZ"));
        Assert.True(settings.CategoryPrompts.ContainsKey("DOC"));
        Assert.True(settings.CategoryPrompts.ContainsKey("STD"));
    }

    [Fact]
    public void RoundTrip_SerializeThenDeserialize_PreservesValues()
    {
        var original = new AppSettings();
        original.Audio.InputDevice = 3;
        original.Audio.InputGainDb = 6.5;
        original.Audio.MaxRecordSeconds = 120;
        original.Audio.AutoPaste = false;
        original.Audio.HoldKey = "ctrl_r";
        original.Ui.Language = "en";
        original.Ui.OverlayX = 123.45;
        original.ContextAwareEnabled = false;
        original.Dictionary["ぱいそん"] = "Python";
        original.Dictionary["ぎっとはぶ"] = "GitHub";

        string json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal(3, restored!.Audio.InputDevice);
        Assert.Equal(6.5, restored.Audio.InputGainDb);
        Assert.Equal(120, restored.Audio.MaxRecordSeconds);
        Assert.False(restored.Audio.AutoPaste);
        Assert.Equal("ctrl_r", restored.Audio.HoldKey);
        Assert.Equal("en", restored.Ui.Language);
        Assert.Equal(123.45, restored.Ui.OverlayX);
        Assert.False(restored.ContextAwareEnabled);
        Assert.Equal("Python", restored.Dictionary["ぱいそん"]);
        Assert.Equal("GitHub", restored.Dictionary["ぎっとはぶ"]);
    }

    [Fact]
    public void RoundTrip_PreservesJapaneseTextInDefaultPrompts()
    {
        var original = new AppSettings();

        string json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal(original.Prompts.GroqWhisperPrompt, restored!.Prompts.GroqWhisperPrompt);
        Assert.Equal(original.Prompts.GroqRefineSystemPrompt, restored.Prompts.GroqRefineSystemPrompt);
        Assert.Equal(original.Prompts.GeminiTranscribePrompt, restored.Prompts.GeminiTranscribePrompt);
        Assert.Equal(original.CategoryPrompts["BIZ"], restored.CategoryPrompts["BIZ"]);
        Assert.Contains("ビジネス", restored.CategoryPrompts["BIZ"]);
    }

    [Fact]
    public void Serialize_UsesSnakeCasePropertyNames()
    {
        var settings = new AppSettings();

        string json = JsonSerializer.Serialize(settings, Options);

        Assert.Contains("\"input_device\"", json);
        Assert.Contains("\"input_gain_db\"", json);
        Assert.Contains("\"max_record_seconds\"", json);
        Assert.Contains("\"min_duration\"", json);
        Assert.Contains("\"auto_paste\"", json);
        Assert.Contains("\"paste_delay_ms\"", json);
        Assert.Contains("\"hold_key\"", json);
        Assert.Contains("\"overlay_x\"", json);
        Assert.Contains("\"overlay_y\"", json);
        Assert.Contains("\"context_aware_enabled\"", json);
        Assert.Contains("\"app_categories\"", json);
        Assert.Contains("\"category_prompts\"", json);
        Assert.Contains("\"groq_whisper_prompt\"", json);
        Assert.Contains("\"groq_refine_system_prompt\"", json);
        Assert.Contains("\"gemini_transcribe_prompt\"", json);

        // C# の PascalCase 名がそのまま出力されていないことも確認
        Assert.DoesNotContain("\"InputDevice\"", json);
        Assert.DoesNotContain("\"MaxRecordSeconds\"", json);
    }

    [Fact]
    public void Deserialize_SnakeCaseJson_PopulatesProperties()
    {
        string json = """
        {
          "audio": { "max_record_seconds": 45, "hold_key": "space" },
          "ui": { "language": "en" },
          "context_aware_enabled": false
        }
        """;

        var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(settings);
        Assert.Equal(45, settings!.Audio.MaxRecordSeconds);
        Assert.Equal("space", settings.Audio.HoldKey);
        Assert.Equal("en", settings.Ui.Language);
        Assert.False(settings.ContextAwareEnabled);
    }

    [Fact]
    public void Deserialize_JsonWithUnknownKeys_DoesNotThrowAndIgnoresThem()
    {
        string json = """
        {
          "audio": { "max_record_seconds": 30, "totally_unknown_field": "ignored" },
          "brand_new_top_level_field": { "nested": [1, 2, 3] },
          "context_aware_enabled": true
        }
        """;

        AppSettings? settings = null;
        var exception = Record.Exception(() =>
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
        });

        Assert.Null(exception);
        Assert.NotNull(settings);
        Assert.Equal(30, settings!.Audio.MaxRecordSeconds);
        Assert.True(settings.ContextAwareEnabled);
    }

    [Fact]
    public void Deserialize_EmptyJsonObject_FallsBackToPropertyDefaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}", Options);

        Assert.NotNull(settings);
        Assert.Equal(60, settings!.Audio.MaxRecordSeconds);
        Assert.True(settings.ContextAwareEnabled);
        Assert.Equal("ja", settings.Ui.Language);
    }

    [Theory]
    [InlineData("{ this is not valid json")]
    [InlineData("{\"audio\": { \"max_record_seconds\": }}")]
    [InlineData("not json at all")]
    public void Deserialize_MalformedJson_ThrowsCatchableJsonException(string malformedJson)
    {
        // AppSettings 自体は例外を飲み込まない (POCO なので当然)。
        // 実際に「壊れたJSONで落ちない」という本体側の保証は SettingsManager.Load() の
        // try/catch(Exception) が担っている。ここでは、その try/catch が有効であるために
        // 必要な前提条件 ── 壊れたJSONの読み込みは JsonException という
        // 通常の catch(Exception) で捕捉可能な例外型で失敗する、それ以外の
        // 予期しない例外（プロセスを落としかねないもの）にはならない ── を保証する。
        var thrown = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<AppSettings>(malformedJson, Options));

        Assert.NotNull(thrown);
    }

    [Fact]
    public void Dictionary_RoundTrip_PreservesArbitraryKeyValuePairs()
    {
        var settings = new AppSettings
        {
            Dictionary = new Dictionary<string, string>
            {
                ["api"] = "API",
                ["日本語キー"] = "日本語の値"
            }
        };

        string json = JsonSerializer.Serialize(settings, Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Dictionary.Count);
        Assert.Equal("API", restored.Dictionary["api"]);
        Assert.Equal("日本語の値", restored.Dictionary["日本語キー"]);
    }
}
