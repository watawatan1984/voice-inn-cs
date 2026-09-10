using System.Text.Json;
using VoiceIn.Core;
using Xunit;

namespace VoiceIn.Tests.Core;

/// <summary>
/// AppSettings.Local (LocalSettings, ローカル音声認識設定) の JSON シリアライズ/デシリアライズ
/// 契約と既定値のテスト。AppSettingsTests.cs と同じく、SettingsManager が使用する
/// JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true } を
/// 模した設定でテストする。
///
/// 注意: このテストは AppSettings/LocalSettings という POCO を直接 JsonSerializer で
/// 往復させるだけであり、SettingsManager.Instance や Logger には一切触れない
/// (実ユーザーの %AppData%\VoiceIn を作らない)。Whisper のモデルロードも一切行わない。
/// </summary>
public class LocalSettingsTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void DefaultLocalSettings_HasExpectedDefaultValues()
    {
        var settings = new AppSettings();

        // 既定モデルは large-v3 (移植元 Python 版の既定) ではなく small。
        // large-v3 は約3GBあり初回ダウンロードが重く、VRAM 4GB 環境では厳しいため、
        // 押して話すツールとして待ち時間が実用的な範囲に収まる small (約500MB) を既定にしている。
        Assert.Equal("small", settings.Local.ModelSize);
        Assert.True(settings.Local.UseGpu);
        Assert.False(settings.Local.RefineWithCloud);
        Assert.Null(settings.Local.ModelPath);
    }

    [Fact]
    public void RoundTrip_SerializeThenDeserialize_PreservesLocalValues()
    {
        var original = new AppSettings();
        original.Local.ModelSize = "large-v3";
        original.Local.UseGpu = false;
        original.Local.RefineWithCloud = true;
        original.Local.ModelPath = @"D:\models\ggml-large-v3.bin";

        string json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal("large-v3", restored!.Local.ModelSize);
        Assert.False(restored.Local.UseGpu);
        Assert.True(restored.Local.RefineWithCloud);
        Assert.Equal(@"D:\models\ggml-large-v3.bin", restored.Local.ModelPath);
    }

    [Fact]
    public void RoundTrip_NullModelPath_StaysNull()
    {
        var original = new AppSettings();
        original.Local.ModelPath = null;

        string json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(restored);
        Assert.Null(restored!.Local.ModelPath);
    }

    [Fact]
    public void Serialize_UsesSnakeCasePropertyNames()
    {
        var settings = new AppSettings();

        string json = JsonSerializer.Serialize(settings, Options);

        Assert.Contains("\"local\"", json);
        Assert.Contains("\"model_size\"", json);
        Assert.Contains("\"use_gpu\"", json);
        Assert.Contains("\"refine_with_cloud\"", json);
        Assert.Contains("\"model_path\"", json);

        // C# の PascalCase 名がそのまま出力されていないことも確認
        Assert.DoesNotContain("\"ModelSize\"", json);
        Assert.DoesNotContain("\"UseGpu\"", json);
        Assert.DoesNotContain("\"RefineWithCloud\"", json);
        Assert.DoesNotContain("\"ModelPath\"", json);
    }

    [Fact]
    public void Deserialize_JsonWithoutLocalSection_FallsBackToDefaults()
    {
        // "local" セクションを一切含まない、本機能追加前の既存ユーザーの settings.json を模す。
        // 後方互換: このセクションが無くても例外にならず、既定値で動くこと。
        string json = """
        {
          "audio": { "max_record_seconds": 45 },
          "ui": { "language": "ja" },
          "context_aware_enabled": true
        }
        """;

        var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(settings);
        Assert.NotNull(settings!.Local);
        Assert.Equal("small", settings.Local.ModelSize);
        Assert.True(settings.Local.UseGpu);
        Assert.False(settings.Local.RefineWithCloud);
        Assert.Null(settings.Local.ModelPath);
    }

    [Fact]
    public void Deserialize_EmptyJsonObject_FallsBackToLocalDefaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}", Options);

        Assert.NotNull(settings);
        Assert.Equal("small", settings!.Local.ModelSize);
        Assert.True(settings.Local.UseGpu);
        Assert.False(settings.Local.RefineWithCloud);
        Assert.Null(settings.Local.ModelPath);
    }

    [Fact]
    public void Deserialize_PartialLocalSection_FillsMissingFieldsWithDefaults()
    {
        // ユーザーが model_size だけを設定ファイルに書いているケース
        // (他のフィールドは省略されている、手動編集や将来のバージョン間の想定シナリオ)。
        string json = """
        {
          "local": { "model_size": "medium" }
        }
        """;

        var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(settings);
        Assert.Equal("medium", settings!.Local.ModelSize);
        Assert.True(settings.Local.UseGpu);
        Assert.False(settings.Local.RefineWithCloud);
        Assert.Null(settings.Local.ModelPath);
    }

    [Fact]
    public void Deserialize_FullLocalSection_PopulatesAllFields()
    {
        string json = """
        {
          "local": {
            "model_size": "base",
            "use_gpu": false,
            "refine_with_cloud": true,
            "model_path": "C:\\custom\\ggml-base.bin"
          }
        }
        """;

        var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(settings);
        Assert.Equal("base", settings!.Local.ModelSize);
        Assert.False(settings.Local.UseGpu);
        Assert.True(settings.Local.RefineWithCloud);
        Assert.Equal(@"C:\custom\ggml-base.bin", settings.Local.ModelPath);
    }
}
