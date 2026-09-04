using System;
using VoiceIn.Core;
using Xunit;

namespace VoiceIn.Tests.Core;

/// <summary>
/// EnvLoader.Load(customPath) に追加されたパース挙動のテスト:
///   ・"export KEY=value" の export 接頭辞を除去してキーを取る
///   ・クォートされていない値に限り " #" (半角スペース+ハッシュ) 以降を行末コメントとして切り捨てる
///   ・クォートで囲まれた値の中の # は値の一部として保持する
///
/// EnvLoader.Load は Environment.SetEnvironmentVariable でプロセス全体の環境変数を
/// 書き換えるため、EnvLoaderTests.cs と同様に各テストは TempEnvFile フィクスチャで
/// 必ず環境変数を後始末する。アセンブリ全体の並列実行無効化 (AssemblyInfo.cs) と
/// 合わせて、テスト間の環境変数汚染を防ぐ。
/// </summary>
public class EnvLoaderExportAndInlineCommentTests
{
    [Fact]
    public void Load_ExportPrefixedLine_StripsExportAndSetsCorrectKey()
    {
        using var env = new TempEnvFile("export FOO=bar", "FOO");

        EnvLoader.Load(env.FilePath);

        Assert.Equal("bar", Environment.GetEnvironmentVariable("FOO"));
        // "export FOO" という空白入りのキーとして登録されていないことも確認
        Assert.Null(Environment.GetEnvironmentVariable("export FOO"));
    }

    [Fact]
    public void Load_KeyStartingWithExportButNoWhitespaceAfter_IsNotTruncated()
    {
        // "exported" は "export" で始まるが直後が空白ではないため、export 接頭辞とはみなされない。
        // もし "export" の6文字を無条件に切り詰めると "exported" が誤って "ed" になってしまう
        // (EnvLoader.cs の LoadFile 内コメントが明示的に警告している境界条件)。
        using var env = new TempEnvFile("exported=1", "exported");

        EnvLoader.Load(env.FilePath);

        Assert.Equal("1", Environment.GetEnvironmentVariable("exported"));
        Assert.Null(Environment.GetEnvironmentVariable("ed"));
    }

    [Fact]
    public void Load_UnquotedValueWithHashComment_TruncatesAtSpaceHash()
    {
        using var env = new TempEnvFile("FOO=bar # trailing comment", "FOO");

        EnvLoader.Load(env.FilePath);

        Assert.Equal("bar", Environment.GetEnvironmentVariable("FOO"));
    }

    [Fact]
    public void Load_UnquotedValueWithHashButNoPrecedingSpace_IsNotTruncated()
    {
        // " #" (スペース+ハッシュ) というパターンに一致しない限り、コメント切り捨ては
        // 行われない。スペースを挟まない "#" は値の一部として扱われる。
        using var env = new TempEnvFile("FOO=bar#baz", "FOO");

        EnvLoader.Load(env.FilePath);

        Assert.Equal("bar#baz", Environment.GetEnvironmentVariable("FOO"));
    }

    [Theory]
    [InlineData("FOO=\"bar #baz\"", "bar #baz")]
    [InlineData("FOO='bar #baz'", "bar #baz")]
    public void Load_QuotedValueContainingHashComment_PreservesHashCharacter(string line, string expected)
    {
        // クォートで囲まれた値の中の " #" はコメント区切りとして扱われず、そのまま保持される。
        using var env = new TempEnvFile(line, "FOO");

        EnvLoader.Load(env.FilePath);

        Assert.Equal(expected, Environment.GetEnvironmentVariable("FOO"));
    }

    [Fact]
    public void Load_ExportPrefixedLineWithQuotedHashValue_ComposesBothBehaviors()
    {
        // export 接頭辞除去とクォート内 # 保持が両方同時に効く、実運用に近い組み合わせ。
        using var env = new TempEnvFile("export FOO=\"bar #baz\"", "FOO");

        EnvLoader.Load(env.FilePath);

        Assert.Equal("bar #baz", Environment.GetEnvironmentVariable("FOO"));
    }
}
