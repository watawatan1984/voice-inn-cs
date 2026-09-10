using System;
using System.IO;
using System.Linq;
using VoiceIn.Ai;
using Xunit;

namespace VoiceIn.Tests.Ai;

/// <summary>
/// ModelDownloader のうち、ネットワーク I/O を一切伴わない部分 (保存先パスの決定、モデルサイズ名から
/// ファイル名への変換、既存ファイルの検出、SupportedModelSizes の整合性) のテスト。
///
/// 【厳守事項】
/// ・ModelDownloader.DownloadModelAsync (実際に Hugging Face 経由でモデルを取得するメソッド) は
///   本テストファイルからは一切呼び出さない。数百MB〜数GBの通信が走ってしまい、テストとして
///   実用にならないため。
/// ・SettingsManager.Instance / HistoryManager.Instance / Logger にも一切触れない。
///   これらのいずれかに触れた時点で実ユーザーの %AppData%\VoiceIn が作成されてしまうが、
///   本テストが対象とする ModelDownloader のメソッド群は、いずれにも依存しない
///   (純粋なパス計算と File.Exists のみで構成されている)。
/// ・"モデルが存在する" ケースを検証するために一時ファイルを作る際も、実際の %AppData%\VoiceIn の
///   下には絶対に作らない。VOICEIN_PORTABLE=1 に切り替えて EnvLoader.GetAppDataDirectory() の
///   戻り値をテスト実行ファイル自身の出力ディレクトリ (AppDomain.CurrentDomain.BaseDirectory、
///   既にビルド成果物が置かれている書き込み可能な場所) へ差し替えたうえで一時ファイルを作り、
///   テスト後に必ず削除する (Core/PortableModeTests.cs と同じ手法)。
/// ・VOICEIN_PORTABLE はプロセス全体の環境変数であるため、他のテストと同様に必ず try/finally で
///   元の値へ復元する (AssemblyInfo.cs でアセンブリ全体のテスト並列実行を無効化している前提)。
/// </summary>
public class ModelDownloaderTests
{
    private const string PortableEnvKey = "VOICEIN_PORTABLE";

    [Theory]
    [InlineData("tiny", "ggml-tiny.bin")]
    [InlineData("base", "ggml-base.bin")]
    [InlineData("small", "ggml-small.bin")]
    [InlineData("medium", "ggml-medium.bin")]
    [InlineData("large-v3", "ggml-large-v3.bin")]
    public void GetModelFileName_KnownSizes_ProducesExpectedGgmlFileName(string modelSize, string expectedFileName)
    {
        Assert.Equal(expectedFileName, ModelDownloader.GetModelFileName(modelSize));
    }

    [Fact]
    public void GetModelsDirectory_EndsWithModelsSubdirectory()
    {
        string dir = ModelDownloader.GetModelsDirectory();

        Assert.Equal("models", Path.GetFileName(dir));
    }

    [Fact]
    public void GetModelsDirectory_PortableModeOff_IsUnderAppDataVoiceIn()
    {
        string? original = Environment.GetEnvironmentVariable(PortableEnvKey);
        try
        {
            Environment.SetEnvironmentVariable(PortableEnvKey, null);

            string expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceIn", "models");

            Assert.Equal(expected, ModelDownloader.GetModelsDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(PortableEnvKey, original);
        }
    }

    [Fact]
    public void GetModelsDirectory_PortableModeOn_IsUnderExecutableBaseDirectory()
    {
        string? original = Environment.GetEnvironmentVariable(PortableEnvKey);
        try
        {
            Environment.SetEnvironmentVariable(PortableEnvKey, "1");

            string expected = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");

            Assert.Equal(expected, ModelDownloader.GetModelsDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(PortableEnvKey, original);
        }
    }

    [Fact]
    public void GetModelFilePath_CombinesModelsDirectoryAndFileName()
    {
        string expected = Path.Combine(ModelDownloader.GetModelsDirectory(), "ggml-small.bin");

        Assert.Equal(expected, ModelDownloader.GetModelFilePath("small"));
    }

    [Fact]
    public void ModelExists_FileAbsent_ReturnsFalse()
    {
        // VOICEIN_PORTABLE を明示的に切り替え、テスト実行ファイル自身の出力ディレクトリを対象にする
        // (実 %AppData%\VoiceIn には一切触れない)。存在しないはずのランダムなモデルサイズ名を使う
        // ことで、他のテストや実行環境の状態に依存しないようにする。
        string? original = Environment.GetEnvironmentVariable(PortableEnvKey);
        try
        {
            Environment.SetEnvironmentVariable(PortableEnvKey, "1");
            string randomModelSize = $"nonexistent-{Guid.NewGuid():N}";

            Assert.False(ModelDownloader.ModelExists(randomModelSize));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PortableEnvKey, original);
        }
    }

    [Fact]
    public void ModelExists_FilePresent_ReturnsTrue()
    {
        string? original = Environment.GetEnvironmentVariable(PortableEnvKey);
        string modelSize = $"unittest-{Guid.NewGuid():N}";
        string? createdFilePath = null;
        try
        {
            Environment.SetEnvironmentVariable(PortableEnvKey, "1");

            string filePath = ModelDownloader.GetModelFilePath(modelSize);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "dummy");
            createdFilePath = filePath;

            Assert.True(ModelDownloader.ModelExists(modelSize));
        }
        finally
        {
            try
            {
                if (createdFilePath != null && File.Exists(createdFilePath))
                {
                    File.Delete(createdFilePath);
                }
            }
            catch
            {
                // ベストエフォート: 一時ファイル削除の失敗はテスト結果に影響させない
                // (TempEnvFile / TempHistoryManager と同じ後始末方針)。
            }

            Environment.SetEnvironmentVariable(PortableEnvKey, original);
        }
    }

    [Theory]
    [InlineData("tiny")]
    [InlineData("TINY")]
    [InlineData("Small")]
    [InlineData("LARGE-V3")]
    public void FindModelSizeInfo_KnownSizeCaseInsensitive_ReturnsMatchingInfo(string modelSize)
    {
        var info = ModelDownloader.FindModelSizeInfo(modelSize);

        Assert.NotNull(info);
        Assert.Equal(modelSize, info!.Id, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("tiny-en")]
    [InlineData("huge")]
    [InlineData("")]
    [InlineData("large-v2")]
    [InlineData("large-v1")]
    public void FindModelSizeInfo_UnsupportedOrUnknownSize_ReturnsNull(string modelSize)
    {
        // tiny-en/large-v1/large-v2 は Whisper.net の GgmlType には存在するが、本アプリの
        // 選択肢 (SupportedModelSizes) には含めていない (英語専用モデルや旧バージョンのため)。
        Assert.Null(ModelDownloader.FindModelSizeInfo(modelSize));
    }

    [Fact]
    public void SupportedModelSizes_ContainsExactlyExpectedFiveSizesInOrder()
    {
        var ids = ModelDownloader.SupportedModelSizes.Select(i => i.Id).ToArray();

        Assert.Equal(new[] { "tiny", "base", "small", "medium", "large-v3" }, ids);
    }

    [Fact]
    public void SupportedModelSizes_ApproxBytesStrictlyIncreaseWithModelSize()
    {
        var sizes = ModelDownloader.SupportedModelSizes;

        for (int i = 1; i < sizes.Count; i++)
        {
            Assert.True(
                sizes[i].ApproxBytes > sizes[i - 1].ApproxBytes,
                $"{sizes[i].Id} ({sizes[i].ApproxBytes} bytes) should be larger than {sizes[i - 1].Id} ({sizes[i - 1].ApproxBytes} bytes)");
        }
    }

    [Theory]
    [InlineData("tiny", 75L * 1024 * 1024, "約75MB")]
    [InlineData("base", 142L * 1024 * 1024, "約142MB")]
    [InlineData("small", 466L * 1024 * 1024, "約466MB")]
    [InlineData("medium", 1610612736L, "約1.5GB")]
    [InlineData("large-v3", 3L * 1024 * 1024 * 1024, "約3GB")]
    public void SupportedModelSizes_MatchesInstructedApproximateSizes(string modelSize, long expectedBytes, string expectedLabel)
    {
        var info = ModelDownloader.FindModelSizeInfo(modelSize);

        Assert.NotNull(info);
        Assert.Equal(expectedBytes, info!.ApproxBytes);
        Assert.Equal(expectedLabel, info.ApproxSizeLabel);
    }
}
