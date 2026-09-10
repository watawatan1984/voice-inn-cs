using VoiceIn.Ai;
using Xunit;

namespace VoiceIn.Tests.Ai;

/// <summary>
/// Ai/RefineOutput.OrRaw のテスト。
///
/// 【背景】Ai/GeminiRefineProvider.cs / Ai/NvidiaRefineProvider.cs は以前どちらも
/// `xxx.GetString()?.Trim() ?? rawText` という式で整形結果を返していた。`??` 演算子は
/// 左辺が null のときしか右辺 (rawText) を使わないため、整形モデルが「空文字列」を返した
/// 場合は
/// "" がそのまま返っていた。この "" は App.xaml.cs で履歴保存・SetAppState("success") まで
/// 進む一方、IsNullOrWhiteSpace ガードで貼り付け・クリップボード格納の両方がスキップされる
/// ため、画面は成功と表示されたまま発話が跡形もなく消える経路になっていた (実際に空文字列を
/// 返すモデルは未確認で、これは防御としての修正。詳細は Ai/RefineOutput.cs のコメントを参照)。
///
/// RefineOutput.OrRaw は null・空・空白のみの refined をすべて rawText へフォールバックする
/// ことでこれを防ぐ純粋関数。Core.Logger を含むあらゆる外部依存に触れないため、
/// SettingsManager.Instance / HistoryManager.Instance / Logger に触れず、実ネットワークにも
/// 一切出ずに検証できる。
/// </summary>
public class RefineOutputTests
{
    [Fact]
    public void OrRaw_Null_ReturnsRawText()
    {
        Assert.Equal("raw", RefineOutput.OrRaw(null, "raw"));
    }

    [Fact]
    public void OrRaw_EmptyString_ReturnsRawText()
    {
        // 【最重要】この分岐こそが今回の欠陥そのもの。旧実装の `?? rawText` は
        // 空文字列 ("" は null ではない) を拾えず、そのまま "" を返してしまっていた。
        Assert.Equal("raw", RefineOutput.OrRaw("", "raw"));
    }

    [Fact]
    public void OrRaw_WhitespaceOnly_ReturnsRawText()
    {
        Assert.Equal("raw", RefineOutput.OrRaw("   ", "raw"));
    }

    [Theory]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r\n  \t ")]
    public void OrRaw_VariousWhitespaceOnly_ReturnsRawText(string whitespace)
    {
        Assert.Equal("raw", RefineOutput.OrRaw(whitespace, "raw"));
    }

    [Fact]
    public void OrRaw_NonEmptyValue_ReturnsTrimmedRefinedValue()
    {
        Assert.Equal("x", RefineOutput.OrRaw("  x  ", "raw"));
    }

    [Fact]
    public void OrRaw_NonEmptyValueWithoutSurroundingWhitespace_ReturnsAsIs()
    {
        Assert.Equal("整形済みのテキスト", RefineOutput.OrRaw("整形済みのテキスト", "raw"));
    }

    [Fact]
    public void OrRaw_InternalWhitespaceIsPreserved_OnlyEndsAreTrimmed()
    {
        Assert.Equal("a b", RefineOutput.OrRaw("  a b  ", "raw"));
    }
}
