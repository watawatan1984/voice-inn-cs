using System.Text.Json;
using VoiceIn.Core;
using Xunit;

namespace VoiceIn.Tests.Core;

/// <summary>
/// AppSettings.RefineProvider (整形バックエンド選択、"gemini"/"nvidia") の JSON
/// シリアライズ/デシリアライズ契約と既定値のテスト。AppSettingsTests.cs / LocalSettingsTests.cs
/// と同じく、SettingsManager が使用する JsonSerializerOptions
/// { WriteIndented = true, PropertyNameCaseInsensitive = true } を模した設定でテストする。
///
/// 注意: このテストは AppSettings という POCO を直接 JsonSerializer で往復させるだけであり、
/// SettingsManager.Instance や Logger には一切触れない (実ユーザーの %AppData%\VoiceIn を作らない)。
/// </summary>
public class RefineProviderSettingsTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void DefaultAppSettings_RefineProviderIsGemini()
    {
        var settings = new AppSettings();

        Assert.Equal("gemini", settings.RefineProvider);
    }

    [Fact]
    public void RoundTrip_SerializeThenDeserialize_PreservesRefineProvider()
    {
        var original = new AppSettings { RefineProvider = "nvidia" };

        string json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal("nvidia", restored!.RefineProvider);
    }

    [Fact]
    public void Serialize_UsesSnakeCasePropertyName()
    {
        var settings = new AppSettings();

        string json = JsonSerializer.Serialize(settings, Options);

        Assert.Contains("\"refine_provider\"", json);
        // C# の PascalCase 名がそのまま出力されていないことも確認
        Assert.DoesNotContain("\"RefineProvider\"", json);
    }

    [Fact]
    public void Deserialize_JsonWithoutRefineProviderKey_FallsBackToGeminiDefault()
    {
        // refine_provider キーを一切含まない、本設定追加前の既存ユーザーの settings.json を模す。
        // 後方互換: このキーが無くても例外にならず、既定値 "gemini" で動くこと。
        string json = """
        {
          "audio": { "max_record_seconds": 45 },
          "ui": { "language": "ja" },
          "context_aware_enabled": true
        }
        """;

        var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(settings);
        Assert.Equal("gemini", settings!.RefineProvider);
    }

    [Fact]
    public void Deserialize_EmptyJsonObject_FallsBackToGeminiDefault()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}", Options);

        Assert.NotNull(settings);
        Assert.Equal("gemini", settings!.RefineProvider);
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("nvidia")]
    [InlineData("NVIDIA")]
    public void Deserialize_ExplicitRefineProviderValue_IsPreservedAsIs(string value)
    {
        // AppSettings 自体は大文字小文字を正規化しない (正規化は Ai/RefineProviderFactory の責務)。
        // ここでは保存された文字列がそのまま復元されることのみを確認する。
        string json = "{ \"refine_provider\": \"" + value + "\" }";

        var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(settings);
        Assert.Equal(value, settings!.RefineProvider);
    }

    [Fact]
    public void RoundTrip_RefineProviderCoexistsWithOtherSections_AllValuesIndependentlyPreserved()
    {
        var original = new AppSettings
        {
            RefineProvider = "nvidia",
            ContextAwareEnabled = false
        };
        original.Local.ModelSize = "medium";
        original.Audio.MaxRecordSeconds = 90;

        string json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal("nvidia", restored!.RefineProvider);
        Assert.Equal("medium", restored.Local.ModelSize);
        Assert.Equal(90, restored.Audio.MaxRecordSeconds);
        Assert.False(restored.ContextAwareEnabled);
    }
}
