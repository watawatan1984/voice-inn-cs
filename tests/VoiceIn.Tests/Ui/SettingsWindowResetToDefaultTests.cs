using VoiceIn.Core;
using VoiceIn.Ui;
using Xunit;

namespace VoiceIn.Tests.Ui;

/// <summary>
/// 「既定に戻す」機能 (Ui/SettingsWindow.xaml.cs) が使う純粋ヘルパー群のテスト。
///
/// SettingsWindowDetectedAppAssignmentTests.cs と同じ理由 (Ui/SettingsWindow は WPF の Window
/// であり、テストホスト上でインスタンス化できない) により、ここでは internal static な
/// ヘルパーメソッドのみを直接呼び出して検証する ([assembly: InternalsVisibleTo("VoiceIn.Tests")]
/// が AssemblyInfo.cs にあるため参照できる)。SettingsManager.Instance / HistoryManager.Instance /
/// Logger には一切触れない (実ユーザーの %AppData%\VoiceIn を作らない)。
/// </summary>
public class SettingsWindowResetToDefaultTests
{
    [Fact]
    public void GetDefaultGeminiTranscribePrompt_MatchesPromptSettingsDefault()
    {
        Assert.Equal(new PromptSettings().GeminiTranscribePrompt, SettingsWindow.GetDefaultGeminiTranscribePrompt());
    }

    [Fact]
    public void GetDefaultGroqWhisperPrompt_MatchesPromptSettingsDefault()
    {
        Assert.Equal(new PromptSettings().GroqWhisperPrompt, SettingsWindow.GetDefaultGroqWhisperPrompt());
    }

    [Fact]
    public void GetDefaultGroqRefineSystemPrompt_MatchesPromptSettingsDefault()
    {
        Assert.Equal(new PromptSettings().GroqRefineSystemPrompt, SettingsWindow.GetDefaultGroqRefineSystemPrompt());
    }

    [Theory]
    [InlineData("DEV")]
    [InlineData("BIZ")]
    [InlineData("DOC")]
    [InlineData("STD")]
    public void TryGetDefaultCategoryPrompt_KnownCategory_ReturnsTrueAndMatchesAppSettingsDefault(string category)
    {
        bool found = SettingsWindow.TryGetDefaultCategoryPrompt(category, out string prompt);

        Assert.True(found);
        Assert.Equal(new AppSettings().CategoryPrompts[category], prompt);
    }

    [Fact]
    public void TryGetDefaultCategoryPrompt_UnknownCustomCategory_ReturnsFalseAndEmptyString()
    {
        // settings.json の手動編集などで作られた、既定値を一切持たないカスタムカテゴリを想定。
        bool found = SettingsWindow.TryGetDefaultCategoryPrompt("そんざいしないカテゴリ", out string prompt);

        Assert.False(found);
        Assert.Equal(string.Empty, prompt);
    }
}
