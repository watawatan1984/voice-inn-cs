using VoiceIn.Core;
using Xunit;

namespace VoiceIn.Tests.Core;

/// <summary>
/// PromptSettings.GroqRefineSystemPrompt (整形バックエンド共通のシステムプロンプト) の
/// 内容契約のテスト。
///
/// 【背景】整形モデルがマークダウン記法 (バッククォート等) 付きでコマンド名を返すことがあり、
/// Slack や Word など任意のアプリへプレーンテキストとして貼り付けるこのツールでは、その記号が
/// そのまま混入する実害のある不具合になっていた。管理側の実測で、プロンプトに「マークダウンを
/// 使わない」旨のルールを追加すると Gemini・NVIDIA いずれの整形バックエンドでも解消することを
/// 確認済みのため、そのルールが既定プロンプトに含まれていることをここで保証する。
///
/// 注意: AppSettings という POCO のプロパティを直接読むだけであり、SettingsManager.Instance /
/// Logger には一切触れない (実ユーザーの %AppData%\VoiceIn を作らない)。
/// </summary>
public class PromptSettingsMarkdownRuleTests
{
    [Fact]
    public void DefaultGroqRefineSystemPrompt_MentionsMarkdownProhibition()
    {
        string prompt = new AppSettings().Prompts.GroqRefineSystemPrompt;

        Assert.Contains("マークダウン", prompt);
    }

    [Fact]
    public void DefaultGroqRefineSystemPrompt_MentionsPlainTextAndBacktickExample()
    {
        string prompt = new AppSettings().Prompts.GroqRefineSystemPrompt;

        // 「プレーンテキストで出力すること」という指示と、代表的なマークダウン記法である
        // バッククォートへの言及の両方がプロンプト内にあることを確認する。
        // 「`npm run build`」のようにコマンド名がバッククォート付きで返る不具合が
        // 実際に確認されていたため (Ai/GroqProvider.cs の旧整形ロジックのコメント参照)、
        // このキーワードが抜けていないことが重要。
        Assert.Contains("プレーンテキスト", prompt);
        Assert.Contains("バッククォート", prompt);
    }

    [Fact]
    public void DefaultGroqRefineSystemPrompt_StillContainsPreExistingRules()
    {
        // 新ルール追加によって既存の指示 (脱カタカナ・英単語化やフィラー除去) が
        // 消えてしまっていないことを確認する (既存ユーザー向けの回帰防止)。
        string prompt = new AppSettings().Prompts.GroqRefineSystemPrompt;

        Assert.Contains("脱カタカナ", prompt);
        Assert.Contains("フィラー", prompt);
    }
}
