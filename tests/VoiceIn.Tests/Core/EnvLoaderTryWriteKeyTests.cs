using System;
using System.IO;
using VoiceIn.Core;
using Xunit;

namespace VoiceIn.Tests.Core;

/// <summary>
/// EnvLoader.TryWriteKey / LoadedFilePath / GetWritableFilePath のテスト。
///
/// 【最重要】TryWriteKey は .env への書き戻し先を GetWritableFilePath() 経由で決定し、
/// これは Load() が最後に読み込んだパス (LoadedFilePath) に依存する。LoadedFilePath が
/// null の状態で TryWriteKey を呼ぶと、書き込み先が実ユーザーの
/// %AppData%\VoiceIn\.env にフォールバックし、Directory.CreateDirectory によって
/// そのディレクトリ自体を作成してしまう。これを絶対に避けるため、このファイルの
/// すべてのテストは TryWriteKey を呼ぶ前に必ず EnvLoader.Load(一時ファイルのパス) を
/// 呼んで書き込み先を一時ファイルに固定してから検証する。
///
/// LoadedFilePath は static であり、テスト間 (さらには他のテストクラスとも) 状態が
/// 共有される。AssemblyInfo.cs で並列実行は無効化済みだが、実行順序自体は保証されない
/// ため、各テストは TryWriteKey/GetWritableFilePath を呼ぶ直前に必ず自分自身で
/// Load() して既知の状態に固定する (前のテストの状態に依存しない)。
///
/// また Load() は Environment.SetEnvironmentVariable を呼ぶ副作用を持つため、
/// EnvLoaderTests.cs と同様に TempEnvFile フィクスチャで環境変数を後始末する。
/// </summary>
public class EnvLoaderTryWriteKeyTests
{
    [Fact]
    public void TryWriteKey_OnlyRewritesTargetKeyLine_AllOtherLinesPreservedExactly()
    {
        // タスク仕様に記載されたサンプル .env そのもの。
        string content =
            "# Voice In 設定\n" +
            "GEMINI_API_KEY=AIzaSyDUMMY_not_a_real_key_0123456789\n" +
            "GROQ_API_KEY=gsk_dummy_not_a_real_key_0123456789\n" +
            "\n" +
            "AI_PROVIDER=gemini\n" +
            "GEMINI_MODEL=gemini-2.5-flash\n";

        using var env = new TempEnvFile(content, "GEMINI_API_KEY", "GROQ_API_KEY", "AI_PROVIDER", "GEMINI_MODEL");
        EnvLoader.Load(env.FilePath);
        string[] originalLines = File.ReadAllLines(env.FilePath);

        bool result = EnvLoader.TryWriteKey("AI_PROVIDER", "groq");

        Assert.True(result);
        string[] rewrittenLines = File.ReadAllLines(env.FilePath);

        // AI_PROVIDER の行だけを書き換えた期待結果を組み立てて、配列全体を比較する。
        // これにより「他の行・コメント・空行・順序がすべて保持されている」ことを
        // 1回のアサーションで厳密に検証できる。
        string[] expectedLines = (string[])originalLines.Clone();
        int aiProviderIndex = Array.FindIndex(expectedLines, l => l.StartsWith("AI_PROVIDER=", StringComparison.Ordinal));
        Assert.True(aiProviderIndex >= 0, "前提確認: AI_PROVIDER の行が見つかること");
        expectedLines[aiProviderIndex] = "AI_PROVIDER=groq";

        Assert.Equal(expectedLines, rewrittenLines);

        // 最重要不変条件を明示的に再確認: API キー行は 1 文字も変わっていない（厳密な文字列比較）。
        Assert.Equal("GEMINI_API_KEY=AIzaSyDUMMY_not_a_real_key_0123456789", rewrittenLines[1]);
        Assert.Equal("GROQ_API_KEY=gsk_dummy_not_a_real_key_0123456789", rewrittenLines[2]);
        // コメント行・空行もそのまま。
        Assert.Equal("# Voice In 設定", rewrittenLines[0]);
        Assert.Equal(string.Empty, rewrittenLines[3]);
    }

    [Fact]
    public void TryWriteKey_NonExistentKey_AppendsNewLineAtEnd()
    {
        // GEMINI_MODEL が存在しないファイル。
        string content = "AI_PROVIDER=gemini\n";
        using var env = new TempEnvFile(content, "AI_PROVIDER");
        EnvLoader.Load(env.FilePath);

        bool result = EnvLoader.TryWriteKey("GEMINI_MODEL", "gemini-2.5-flash");

        Assert.True(result);
        string[] lines = File.ReadAllLines(env.FilePath);
        Assert.Equal(2, lines.Length);
        Assert.Equal("AI_PROVIDER=gemini", lines[0]);
        Assert.Equal("GEMINI_MODEL=gemini-2.5-flash", lines[1]);
    }

    [Fact]
    public void TryWriteKey_ExportPrefixedLine_PreservesExportPrefix()
    {
        string content = "export AI_PROVIDER=gemini\nGEMINI_API_KEY=AIzaSyDUMMY_not_a_real_key\n";
        using var env = new TempEnvFile(content, "AI_PROVIDER", "GEMINI_API_KEY");
        EnvLoader.Load(env.FilePath);

        bool result = EnvLoader.TryWriteKey("AI_PROVIDER", "groq");

        Assert.True(result);
        string[] lines = File.ReadAllLines(env.FilePath);
        Assert.Equal("export AI_PROVIDER=groq", lines[0]);
        Assert.Equal("GEMINI_API_KEY=AIzaSyDUMMY_not_a_real_key", lines[1]);
    }

    [Fact]
    public void TryWriteKey_CalledTwice_SecondCallOverwritesWithoutDuplicatingLine()
    {
        string content = "AI_PROVIDER=gemini\n";
        using var env = new TempEnvFile(content, "AI_PROVIDER");
        EnvLoader.Load(env.FilePath);

        EnvLoader.TryWriteKey("AI_PROVIDER", "groq");
        bool secondResult = EnvLoader.TryWriteKey("AI_PROVIDER", "openai");

        Assert.True(secondResult);
        string[] lines = File.ReadAllLines(env.FilePath);
        Assert.Single(lines);
        Assert.Equal("AI_PROVIDER=openai", lines[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryWriteKey_EmptyOrWhitespaceKey_ReturnsFalseAndLeavesFileUnchanged(string invalidKey)
    {
        string content = "AI_PROVIDER=gemini\n";
        using var env = new TempEnvFile(content, "AI_PROVIDER");
        EnvLoader.Load(env.FilePath);
        string[] before = File.ReadAllLines(env.FilePath);

        bool result = EnvLoader.TryWriteKey(invalidKey, "groq");

        Assert.False(result);
        string[] after = File.ReadAllLines(env.FilePath);
        Assert.Equal(before, after);
    }

    [Fact]
    public void TryWriteKey_DoesNotLeaveTempFileBehind()
    {
        string content = "AI_PROVIDER=gemini\n";
        using var env = new TempEnvFile(content, "AI_PROVIDER");
        EnvLoader.Load(env.FilePath);

        bool result = EnvLoader.TryWriteKey("AI_PROVIDER", "groq");

        Assert.True(result);
        string? dir = Path.GetDirectoryName(env.FilePath);
        Assert.NotNull(dir);
        // TryWriteKey は `path + $".tmp{Guid.NewGuid():N}"` という命名で一時ファイルを作る。
        // File.Move による置換が成功していれば、この命名パターンに一致するファイルは
        // 同じディレクトリに残っていないはず。
        string[] leftoverTempFiles = Directory.GetFiles(dir!, Path.GetFileName(env.FilePath) + ".tmp*");
        Assert.Empty(leftoverTempFiles);
    }

    [Fact]
    public void Load_ValidCustomPath_SetsLoadedFilePathToThatPath()
    {
        using var env = new TempEnvFile("FOO=bar", "FOO");

        EnvLoader.Load(env.FilePath);

        Assert.Equal(env.FilePath, EnvLoader.LoadedFilePath);
    }

    [Fact]
    public void Load_NonExistentCustomPath_DoesNotUpdateLoadedFilePath()
    {
        // まず既知のパスに固定する (static state のテスト間汚染に依存しないための前提づくり)。
        using var env = new TempEnvFile("FOO=bar", "FOO");
        EnvLoader.Load(env.FilePath);
        Assert.Equal(env.FilePath, EnvLoader.LoadedFilePath); // 前提確認

        string missingPath = Path.Combine(
            Path.GetTempPath(), $"voicein-does-not-exist-{Guid.NewGuid():N}.env");

        EnvLoader.Load(missingPath);

        // 存在しないパスを Load しても LoadedFilePath は直前の値のまま変わらない
        // (= 存在しないパスが書き込み先として採用されてしまうことはない)。
        Assert.Equal(env.FilePath, EnvLoader.LoadedFilePath);
    }

    [Fact]
    public void GetWritableFilePath_AfterSuccessfulLoad_ReturnsLoadedFilePath()
    {
        using var env = new TempEnvFile("FOO=bar", "FOO");

        EnvLoader.Load(env.FilePath);

        Assert.Equal(EnvLoader.LoadedFilePath, EnvLoader.GetWritableFilePath());
        Assert.Equal(env.FilePath, EnvLoader.GetWritableFilePath());
    }
}
