using VoiceIn.Ui;
using Xunit;

namespace VoiceIn.Tests.Ui;

/// <summary>
/// Ui/SettingsWindow.TryGetModelEnvValueToWrite (モデル名入力欄が空欄のときに環境変数へ
/// 書き込まないためのガード判定ロジック) のテスト。
///
/// 設定画面に追加した Groq Whisper モデル / Gemini 整形モデル / NVIDIA 整形モデルの
/// 3つの ComboBox (IsEditable="True") はいずれも、保存時にこの関数の戻り値で
/// 「環境変数へ書き込むかどうか」を判定する。誤って欄を空にして保存しても、
/// Ai/GroqProvider.cs や Ai/GeminiRefineProvider.cs / Ai/NvidiaRefineProvider.cs 側の
/// コード既定値 (whisper-large-v3 / gemini-flash-lite-latest /
/// nvidia/nemotron-3.5-lightning-30b-a3b) がそのまま使われ続けることを保証する。
///
/// SettingsWindowResetToDefaultTests.cs / SettingsWindowDetectedAppAssignmentTests.cs と同じ理由
/// (Ui/SettingsWindow は WPF の Window でありテストホスト上でインスタンス化できない) により、
/// internal static なヘルパーメソッドのみを直接呼び出して検証する。
/// Environment.SetEnvironmentVariable や EnvLoader.TryWriteKey などの副作用は一切呼ばず、
/// SettingsManager.Instance / HistoryManager.Instance / Logger にも一切触れない
/// (実ユーザーの %AppData%\VoiceIn を作らない)。
/// </summary>
public class SettingsWindowModelEnvGuardTests
{
    [Fact]
    public void TryGetModelEnvValueToWrite_Null_ReturnsFalseAndEmptyValue()
    {
        bool shouldWrite = SettingsWindow.TryGetModelEnvValueToWrite(null, out string value);

        Assert.False(shouldWrite);
        Assert.Equal(string.Empty, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r\n  \t ")]
    public void TryGetModelEnvValueToWrite_EmptyOrWhitespace_ReturnsFalseAndEmptyValue(string inputText)
    {
        bool shouldWrite = SettingsWindow.TryGetModelEnvValueToWrite(inputText, out string value);

        Assert.False(shouldWrite);
        Assert.Equal(string.Empty, value);
    }

    [Theory]
    [InlineData("whisper-large-v3")]
    [InlineData("gemini-flash-lite-latest")]
    [InlineData("nvidia/nemotron-3.5-lightning-30b-a3b")]
    public void TryGetModelEnvValueToWrite_NonEmptyValue_ReturnsTrueAndSameValue(string inputText)
    {
        bool shouldWrite = SettingsWindow.TryGetModelEnvValueToWrite(inputText, out string value);

        Assert.True(shouldWrite);
        Assert.Equal(inputText, value);
    }

    [Fact]
    public void TryGetModelEnvValueToWrite_LeadingAndTrailingWhitespace_IsTrimmed()
    {
        bool shouldWrite = SettingsWindow.TryGetModelEnvValueToWrite("  gemini-flash-lite-latest  ", out string value);

        Assert.True(shouldWrite);
        Assert.Equal("gemini-flash-lite-latest", value);
    }

    [Fact]
    public void TryGetModelEnvValueToWrite_InternalWhitespaceIsPreserved_OnlyEndsAreTrimmed()
    {
        // ComboBox の自由入力によりユーザーが独自のモデル名 (空白を含む) を入れる可能性を考慮し、
        // 前後の空白だけを取り除き、値の内部までは変更しないことを確認する。
        bool shouldWrite = SettingsWindow.TryGetModelEnvValueToWrite(" my custom model ", out string value);

        Assert.True(shouldWrite);
        Assert.Equal("my custom model", value);
    }

    [Fact]
    public void TryGetModelEnvValueToWrite_WhitespaceOnly_DoesNotThrow()
    {
        // string.Empty へのフォールバックが確実に行われ、Trim() 等で例外が発生しないことを確認する。
        var exception = Record.Exception(() => SettingsWindow.TryGetModelEnvValueToWrite("     ", out _));

        Assert.Null(exception);
    }
}
