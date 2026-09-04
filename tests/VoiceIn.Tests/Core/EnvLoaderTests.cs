using System;
using VoiceIn.Core;
using Xunit;

namespace VoiceIn.Tests.Core;

/// <summary>
/// EnvLoader.Load(customPath) の .env パースロジックのテスト。
///
/// 注意: EnvLoader.Load は Environment.SetEnvironmentVariable を呼び出し、
/// テストプロセス全体で共有される環境変数を書き換える。そのため:
///   1. 各テストは TempEnvFile フィクスチャで必ず環境変数を後始末する。
///   2. アセンブリ全体でテスト並列実行を無効化している (AssemblyInfo.cs の
///      [assembly: CollectionBehavior(DisableTestParallelization = true)])。
/// このどちらかが欠けると、テスト間で環境変数が競合・汚染しフレーキーになる。
/// </summary>
public class EnvLoaderTests
{
    [Fact]
    public void Load_NormalLine_SetsEnvironmentVariable()
    {
        using var env = new TempEnvFile("FOO=bar", "FOO");

        EnvLoader.Load(env.FilePath);

        Assert.Equal("bar", Environment.GetEnvironmentVariable("FOO"));
    }

    [Fact]
    public void Load_SkipsBlankLines_WithoutThrowing()
    {
        string content = "FIRST=1\n\n   \nSECOND=2\n";
        using var env = new TempEnvFile(content, "FIRST", "SECOND");

        var exception = Record.Exception(() => EnvLoader.Load(env.FilePath));

        Assert.Null(exception);
        Assert.Equal("1", Environment.GetEnvironmentVariable("FIRST"));
        Assert.Equal("2", Environment.GetEnvironmentVariable("SECOND"));
    }

    [Fact]
    public void Load_SkipsCommentLines()
    {
        string content = "# this is a comment\nFOO=bar\n   # indented comment\n";
        using var env = new TempEnvFile(content, "FOO");

        EnvLoader.Load(env.FilePath);

        Assert.Equal("bar", Environment.GetEnvironmentVariable("FOO"));
        // コメント行がキー扱いされていないことを確認（コメント文字列そのものがキーになっていない）
        Assert.Null(Environment.GetEnvironmentVariable("this is a comment"));
    }

    [Fact]
    public void Load_ValueContainingEqualsSign_KeepsFullValueAfterFirstEquals()
    {
        using var env = new TempEnvFile("URL=http://example.com?a=1&b=2", "URL");

        EnvLoader.Load(env.FilePath);

        Assert.Equal("http://example.com?a=1&b=2", Environment.GetEnvironmentVariable("URL"));
    }

    [Fact]
    public void Load_TrimsWhitespaceAroundKeyAndValue()
    {
        using var env = new TempEnvFile("   FOO   =   bar   ", "FOO");

        EnvLoader.Load(env.FilePath);

        Assert.Equal("bar", Environment.GetEnvironmentVariable("FOO"));
    }

    [Theory]
    [InlineData("FOO=\"bar\"", "bar")]
    [InlineData("FOO='bar'", "bar")]
    public void Load_StripsMatchingSurroundingQuotes(string line, string expected)
    {
        using var env = new TempEnvFile(line, "FOO");

        EnvLoader.Load(env.FilePath);

        Assert.Equal(expected, Environment.GetEnvironmentVariable("FOO"));
    }

    [Fact]
    public void Load_MismatchedQuotes_AreNotStripped()
    {
        // 開始と終了のクォート文字が一致しない場合は除去されない
        using var env = new TempEnvFile("FOO=\"bar'", "FOO");

        EnvLoader.Load(env.FilePath);

        Assert.Equal("\"bar'", Environment.GetEnvironmentVariable("FOO"));
    }

    [Fact]
    public void Load_SingleQuoteCharacterValue_DoesNotThrowAndIsUnchanged()
    {
        // val の長さが1文字だけの場合、StartsWith/EndsWith が同じ文字にマッチしてしまうが
        // val.Length >= 2 ガードにより除去処理は行われない（IndexOutOfRange 防止の境界値テスト）
        using var env = new TempEnvFile("FOO=\"", "FOO");

        var exception = Record.Exception(() => EnvLoader.Load(env.FilePath));

        Assert.Null(exception);
        Assert.Equal("\"", Environment.GetEnvironmentVariable("FOO"));
    }

    [Fact]
    public void Load_LineWithoutEqualsSign_IsSkipped()
    {
        using var env = new TempEnvFile("JUSTAWORD\nFOO=bar", "FOO", "JUSTAWORD");

        EnvLoader.Load(env.FilePath);

        Assert.Equal("bar", Environment.GetEnvironmentVariable("FOO"));
        Assert.Null(Environment.GetEnvironmentVariable("JUSTAWORD"));
    }

    [Fact]
    public void Load_LineWithEmptyKey_IsSkipped()
    {
        // "=value" は eqIndex == 0 となり、EnvLoader 内の `eqIndex <= 0` ガードで
        // スキップされる（空文字キーで SetEnvironmentVariable が呼ばれることはない）
        string content = "=value\nFOO=bar";
        using var env = new TempEnvFile(content, "FOO");

        var exception = Record.Exception(() => EnvLoader.Load(env.FilePath));

        Assert.Null(exception);
        Assert.Equal("bar", Environment.GetEnvironmentVariable("FOO"));
        Assert.Null(Environment.GetEnvironmentVariable(""));
    }

    [Fact]
    public void Load_NonExistentCustomPath_DoesNotThrow()
    {
        string missingPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"voicein-does-not-exist-{Guid.NewGuid():N}.env");

        var exception = Record.Exception(() => EnvLoader.Load(missingPath));

        Assert.Null(exception);
    }

    [Fact]
    public void Load_MultipleValidLines_SetsAllVariables()
    {
        string content =
            "# config\n" +
            "\n" +
            "API_KEY=abc123\n" +
            "  MODEL = gemini-2.5-flash  \n" +
            "GREETING=\"hello world\"\n" +
            "NOTE='single quoted'\n" +
            "IGNORED_NO_EQUALS\n";

        using var env = new TempEnvFile(
            content, "API_KEY", "MODEL", "GREETING", "NOTE", "IGNORED_NO_EQUALS");

        EnvLoader.Load(env.FilePath);

        Assert.Equal("abc123", Environment.GetEnvironmentVariable("API_KEY"));
        Assert.Equal("gemini-2.5-flash", Environment.GetEnvironmentVariable("MODEL"));
        Assert.Equal("hello world", Environment.GetEnvironmentVariable("GREETING"));
        Assert.Equal("single quoted", Environment.GetEnvironmentVariable("NOTE"));
        Assert.Null(Environment.GetEnvironmentVariable("IGNORED_NO_EQUALS"));
    }
}
