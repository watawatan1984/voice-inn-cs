using System;
using System.IO;
using VoiceIn.Core;
using Xunit;

namespace VoiceIn.Tests.Core;

/// <summary>
/// EnvLoader.TryWriteKey を GEMINI_API_KEY のような API キーの書き込みに使った場合の
/// 契約テスト。
///
/// 背景: TryWriteKey の doc コメントには従来「GEMINI_API_KEY / GROQ_API_KEY を書き換える
/// 目的では使用しない」という制約が書かれていたが、設定画面から API キーを保存できるように
/// するため、その制約を解除した (Core/EnvLoader.cs 参照)。制約解除後も「対象キーの行だけを
/// 置き換え、他の行は 1 文字も変えない」という不変条件は変わらず維持されなければならない。
/// このファイルは、その不変条件が API キー自体を書き込む場合にも成り立つことを確認する。
///
/// 【最重要】EnvLoaderTryWriteKeyTests.cs と同様、TryWriteKey の書き込み先は
/// GetWritableFilePath() 経由で Load() が最後に読み込んだパスに依存する。書き込み先が
/// 実ユーザーの %AppData%\VoiceIn\.env にフォールバックしてそのディレクトリを作成して
/// しまうことを絶対に避けるため、各テストは TryWriteKey を呼ぶ前に必ず
/// EnvLoader.Load(一時ファイルのパス) を呼んで書き込み先を一時ファイルに固定する。
///
/// また Load() は Environment.SetEnvironmentVariable を呼ぶ副作用を持つため、
/// EnvLoaderTryWriteKeyTests.cs と同様に TempEnvFile フィクスチャで環境変数を後始末する
/// (AssemblyInfo.cs でテスト並列実行も無効化済み)。
/// </summary>
public class EnvLoaderApiKeyWriteTests
{
    [Fact]
    public void TryWriteKey_WritingGeminiApiKey_UpdatesOnlyThatLineAndPreservesRestExactly()
    {
        // コメント・空行・GROQ_API_KEY・AI_PROVIDER・GEMINI_MODEL を含む .env に対して、
        // GEMINI_API_KEY 自体を書き込む (今回の契約解除で新たに許可された使い方)。
        string content =
            "# Voice In 設定\n" +
            "GEMINI_API_KEY=AIzaSyDUMMY_old_key_1111111111\n" +
            "GROQ_API_KEY=gsk_dummy_not_a_real_key_0123456789\n" +
            "\n" +
            "AI_PROVIDER=gemini\n" +
            "GEMINI_MODEL=gemini-2.5-flash\n";

        using var env = new TempEnvFile(content, "GEMINI_API_KEY", "GROQ_API_KEY", "AI_PROVIDER", "GEMINI_MODEL");
        EnvLoader.Load(env.FilePath);
        string[] originalLines = File.ReadAllLines(env.FilePath);

        bool result = EnvLoader.TryWriteKey("GEMINI_API_KEY", "AIzaSyDUMMY_new_key_2222222222");

        Assert.True(result);
        string[] rewrittenLines = File.ReadAllLines(env.FilePath);

        // GEMINI_API_KEY の行だけを書き換えた期待結果を組み立てて、配列全体を比較する。
        // これにより「他の行・コメント・空行・順序がすべて保持されている」ことを
        // 1回のアサーションで厳密に検証できる。
        string[] expectedLines = (string[])originalLines.Clone();
        int apiKeyIndex = Array.FindIndex(expectedLines, l => l.StartsWith("GEMINI_API_KEY=", StringComparison.Ordinal));
        Assert.True(apiKeyIndex >= 0, "前提確認: GEMINI_API_KEY の行が見つかること");
        expectedLines[apiKeyIndex] = "GEMINI_API_KEY=AIzaSyDUMMY_new_key_2222222222";

        Assert.Equal(expectedLines, rewrittenLines);

        // 他のキーの行・コメント・空行・順序が保持されていることを明示的にも再確認する。
        Assert.Equal("# Voice In 設定", rewrittenLines[0]);
        Assert.Equal("GEMINI_API_KEY=AIzaSyDUMMY_new_key_2222222222", rewrittenLines[1]);
        Assert.Equal("GROQ_API_KEY=gsk_dummy_not_a_real_key_0123456789", rewrittenLines[2]);
        Assert.Equal(string.Empty, rewrittenLines[3]);
        Assert.Equal("AI_PROVIDER=gemini", rewrittenLines[4]);
        Assert.Equal("GEMINI_MODEL=gemini-2.5-flash", rewrittenLines[5]);
    }

    [Fact]
    public void TryWriteKey_WritingUnrelatedKey_WithExportPrefixedApiKeyPresent_LeavesApiKeyLineByteForByteUnchanged()
    {
        // GEMINI_API_KEY / GROQ_API_KEY の行に export 接頭辞が付いている構成で検証する。
        // EnvLoaderTryWriteKeyTests.TryWriteKey_OnlyRewritesTargetKeyLine_AllOtherLinesPreservedExactly は
        // export 接頭辞なしの API キー行しか検証していないため、export 接頭辞ありという
        // 別角度から「API キー行が書き換わらないこと」を確認し、既存テストと観点を重複させない。
        string content =
            "export GEMINI_API_KEY=AIzaSyDUMMY_not_a_real_key_9999999999\n" +
            "export GROQ_API_KEY=gsk_dummy_not_a_real_key_8888888888\n" +
            "AI_PROVIDER=gemini\n";

        using var env = new TempEnvFile(content, "GEMINI_API_KEY", "GROQ_API_KEY", "AI_PROVIDER");
        EnvLoader.Load(env.FilePath);

        bool result = EnvLoader.TryWriteKey("AI_PROVIDER", "groq");

        Assert.True(result);
        string[] lines = File.ReadAllLines(env.FilePath);

        // API キー行 (export 接頭辞込み) は 1 文字も変わっていない。
        Assert.Equal("export GEMINI_API_KEY=AIzaSyDUMMY_not_a_real_key_9999999999", lines[0]);
        Assert.Equal("export GROQ_API_KEY=gsk_dummy_not_a_real_key_8888888888", lines[1]);
        // 対象キー (AI_PROVIDER) の行だけが更新されている。
        Assert.Equal("AI_PROVIDER=groq", lines[2]);
    }
}
