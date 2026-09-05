using System.Text.Json;
using VoiceIn.Core;
using Xunit;

namespace VoiceIn.Tests.Core;

/// <summary>
/// AppSettings.Local (LocalSettings) の JSON シリアライズ/デシリアライズ契約のうち、
/// 既存の Core/LocalSettingsTests.cs では扱っていない観点を補うテスト。
///
/// 既存の LocalSettingsTests.cs は「Local セクション単体の既定値・往復・欠落時のフォールバック」を
/// 網羅済みのため、本ファイルでは重複を避け、以下の 3 点のみを追加でカバーする:
///   1. SupportedModelSizes に載っている 5 種類のモデルサイズすべてが ModelSize として往復できること
///      (ModelDownloader が新たに導入した「選択可能な5種類」全部が Settings 層でも問題なく
///      扱えることを保証する)
///   2. Local セクションが Dictionary / Audio など他セクションと同居する完全な JSON からでも、
///      Local の値だけが独立して正しく復元されること (他セクションの変更に巻き込まれないこと)
///   3. ModelPath に日本語を含むパスを設定しても正しく往復できること
///      (本アプリは日本語ユーザー向けであり、Windows 上では日本語ディレクトリ名の
///      ユーザーフォルダも珍しくないため)
///
/// 注意: 既存テストと同じく、AppSettings という POCO を直接 JsonSerializer で往復させるだけであり、
/// SettingsManager.Instance や Logger には一切触れない (実ユーザーの %AppData%\VoiceIn を作らない)。
/// </summary>
public class LocalSettingsAdditionalRoundTripTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    [Theory]
    [InlineData("tiny")]
    [InlineData("base")]
    [InlineData("small")]
    [InlineData("medium")]
    [InlineData("large-v3")]
    public void RoundTrip_EachSupportedModelSize_PreservesValue(string modelSize)
    {
        // Ai/ModelDownloader.SupportedModelSizes が選択肢として提供する 5 種類すべてが、
        // 設定の保存・読み込みでも問題なく扱えることを保証する
        // (Ai 層への依存を避けるため、ここでは文字列リテラルとして直接列挙する)。
        var original = new AppSettings();
        original.Local.ModelSize = modelSize;

        string json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal(modelSize, restored!.Local.ModelSize);
    }

    [Fact]
    public void RoundTrip_LocalCoexistsWithOtherSections_AllValuesIndependentlyPreserved()
    {
        // Local 単体ではなく、Dictionary / Audio など他セクションも同時に変更した状態の
        // AppSettings 全体を往復させ、Local の値が隣接セクションの変更に巻き込まれず
        // (上書きされたり欠落したりせず) 正しく復元されることを確認する。
        var original = new AppSettings();
        original.Local.ModelSize = "medium";
        original.Local.UseGpu = false;
        original.Local.RefineWithCloud = true;
        original.Local.ModelPath = @"D:\voicein-models\ggml-medium.bin";
        original.Audio.MaxRecordSeconds = 90;
        original.Audio.HoldKey = "ctrl_l";
        original.Dictionary["おーぷんえーあい"] = "OpenAI";
        original.ContextAwareEnabled = false;

        string json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(restored);

        // Local
        Assert.Equal("medium", restored!.Local.ModelSize);
        Assert.False(restored.Local.UseGpu);
        Assert.True(restored.Local.RefineWithCloud);
        Assert.Equal(@"D:\voicein-models\ggml-medium.bin", restored.Local.ModelPath);

        // 隣接セクション (巻き込まれていないことの確認)
        Assert.Equal(90, restored.Audio.MaxRecordSeconds);
        Assert.Equal("ctrl_l", restored.Audio.HoldKey);
        Assert.Equal("OpenAI", restored.Dictionary["おーぷんえーあい"]);
        Assert.False(restored.ContextAwareEnabled);
    }

    [Fact]
    public void RoundTrip_ModelPathWithJapaneseCharacters_PreservesExactString()
    {
        // Windows 環境では日本語のユーザー名・ディレクトリ名 (例: C:\Users\渡辺\...) も珍しくない。
        // System.Text.Json は既定で非 ASCII 文字を \uXXXX にエスケープして出力するため、
        // その状態からの復元でも元の文字列と完全に一致することを確認する。
        var original = new AppSettings();
        original.Local.ModelPath = @"C:\Users\渡辺\AppData\Roaming\VoiceIn\models\ggml-small.bin";

        string json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal(@"C:\Users\渡辺\AppData\Roaming\VoiceIn\models\ggml-small.bin", restored!.Local.ModelPath);
    }
}
