using System;
using System.IO;
using VoiceIn.Core;
using Xunit;

namespace VoiceIn.Tests.Core;

/// <summary>
/// ポータブルモードのパス解決ヘルパー (EnvLoader.IsPortableMode / GetAppDataDirectory) のテスト。
///
/// 【重要】VOICEIN_PORTABLE は Environment.SetEnvironmentVariable でプロセス全体
/// (=テストプロセス全体) の環境変数を書き換える。EnvLoaderTests.cs / EnvLoaderTryWriteKeyTests.cs
/// と同様に、各テストは元の値を退避してから finally で必ず復元することでテスト間の
/// 環境変数汚染を防ぐ。アセンブリ全体でテスト並列実行を無効化している
/// (AssemblyInfo.cs の [assembly: CollectionBehavior(DisableTestParallelization = true)]) 前提にも依存する。
///
/// 【重要】GetAppDataDirectory() は SettingsManager.Instance / HistoryManager.Instance / Logger の
/// いずれからも独立した純粋なパス計算であり、これらのシングルトンには一切触れない
/// (%AppData%\VoiceIn を実際に作成することはない)。
/// </summary>
public class PortableModeTests
{
    private const string EnvKey = "VOICEIN_PORTABLE";

    private static string DefaultAppDataVoiceInPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceIn");

    [Fact]
    public void IsPortableMode_EnvironmentVariableNotSet_ReturnsFalse()
    {
        string? original = Environment.GetEnvironmentVariable(EnvKey);
        try
        {
            Environment.SetEnvironmentVariable(EnvKey, null);

            Assert.False(EnvLoader.IsPortableMode());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvKey, original);
        }
    }

    [Fact]
    public void IsPortableMode_ExactlyOne_ReturnsTrue()
    {
        string? original = Environment.GetEnvironmentVariable(EnvKey);
        try
        {
            Environment.SetEnvironmentVariable(EnvKey, "1");

            Assert.True(EnvLoader.IsPortableMode());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvKey, original);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("")]
    [InlineData("2")]
    public void IsPortableMode_AnyValueOtherThanExactlyOne_ReturnsFalse(string value)
    {
        string? original = Environment.GetEnvironmentVariable(EnvKey);
        try
        {
            Environment.SetEnvironmentVariable(EnvKey, value);

            Assert.False(EnvLoader.IsPortableMode());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvKey, original);
        }
    }

    [Fact]
    public void GetAppDataDirectory_EnvironmentVariableNotSet_ReturnsAppDataVoiceInPath()
    {
        string? original = Environment.GetEnvironmentVariable(EnvKey);
        try
        {
            Environment.SetEnvironmentVariable(EnvKey, null);

            Assert.Equal(DefaultAppDataVoiceInPath, EnvLoader.GetAppDataDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvKey, original);
        }
    }

    [Fact]
    public void GetAppDataDirectory_PortableModeOn_ReturnsExecutableBaseDirectory()
    {
        string? original = Environment.GetEnvironmentVariable(EnvKey);
        try
        {
            Environment.SetEnvironmentVariable(EnvKey, "1");

            Assert.Equal(AppDomain.CurrentDomain.BaseDirectory, EnvLoader.GetAppDataDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvKey, original);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("")]
    public void GetAppDataDirectory_NonOneValue_ReturnsDefaultAppDataVoiceInPath(string value)
    {
        string? original = Environment.GetEnvironmentVariable(EnvKey);
        try
        {
            Environment.SetEnvironmentVariable(EnvKey, value);

            Assert.Equal(DefaultAppDataVoiceInPath, EnvLoader.GetAppDataDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvKey, original);
        }
    }

    [Fact]
    public void GetAppDataDirectory_PortableModeOff_DoesNotEqualExecutableBaseDirectoryPath()
    {
        // ポータブルモードでない限り、テスト実行ファイルの隣ではなく %AppData%\VoiceIn を
        // 指し続けることを、既定パスとの不一致という形で再確認する
        // (テスト実行環境において両者が偶然一致することは無い前提)。
        string? original = Environment.GetEnvironmentVariable(EnvKey);
        try
        {
            Environment.SetEnvironmentVariable(EnvKey, null);

            Assert.NotEqual(AppDomain.CurrentDomain.BaseDirectory, EnvLoader.GetAppDataDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvKey, original);
        }
    }
}
