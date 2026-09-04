using System;
using System.IO;
using VoiceIn.Audio;
using Xunit;

namespace VoiceIn.Tests.Audio;

/// <summary>
/// AudioRecorder.CleanupStaleTempFiles (internal static) のテスト。
///
/// [assembly: InternalsVisibleTo("VoiceIn.Tests")] (AssemblyInfo.cs) により
/// このテストプロジェクトから internal メソッドを直接呼び出せる。
///
/// 【重要】実際の %TEMP% 直下を掃除するテストは書かない。OS の一時フォルダ配下に
/// 使い捨てのテスト専用ディレクトリを作り、そこに疑似ファイルを置いて検証する
/// (CleanupStaleTempFiles の tempDirectory 引数で対象ディレクトリを差し替えられる)。
///
/// 「古さ」の判定は最終更新時刻ベースであり、実行中の自分自身が書き込み中のファイルを
/// 誤って削除しないための猶予 (minAge) を検証するため、File.SetLastWriteTimeUtc で
/// 明示的に古い/新しい更新時刻を設定する。実時間の経過を待つ必要がなく、フレーキーにならない。
/// </summary>
public class AudioRecorderTempCleanupTests : IDisposable
{
    private readonly string _testDir;

    public AudioRecorderTempCleanupTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"voicein-cleanuptest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // ベストエフォート: 一時ディレクトリ削除の失敗はテスト結果に影響させない
        }
    }

    private static string CreateFile(string dir, string fileName, TimeSpan age)
    {
        string path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, [0x01, 0x02, 0x03]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact]
    public void CleanupStaleTempFiles_FileOlderThanMinAge_IsDeleted()
    {
        string oldFile = CreateFile(_testDir, $"voicein_{Guid.NewGuid():N}.wav", TimeSpan.FromHours(2));

        AudioRecorder.CleanupStaleTempFiles(_testDir, TimeSpan.FromHours(1));

        Assert.False(File.Exists(oldFile));
    }

    [Fact]
    public void CleanupStaleTempFiles_FileNewerThanMinAge_IsNotDeleted()
    {
        // ちょうど今書き込まれたばかり (=実行中の録音が使っている可能性がある) ファイル
        string recentFile = CreateFile(_testDir, $"voicein_{Guid.NewGuid():N}.wav", TimeSpan.Zero);

        AudioRecorder.CleanupStaleTempFiles(_testDir, TimeSpan.FromHours(1));

        Assert.True(File.Exists(recentFile));
    }

    [Fact]
    public void CleanupStaleTempFiles_MixOfOldAndNewFiles_DeletesOnlyOldOnes()
    {
        string oldFile1 = CreateFile(_testDir, $"voicein_{Guid.NewGuid():N}.wav", TimeSpan.FromHours(3));
        string oldFile2 = CreateFile(_testDir, $"voicein_{Guid.NewGuid():N}.wav", TimeSpan.FromDays(1));
        string recentFile = CreateFile(_testDir, $"voicein_{Guid.NewGuid():N}.wav", TimeSpan.FromMinutes(1));

        AudioRecorder.CleanupStaleTempFiles(_testDir, TimeSpan.FromHours(1));

        Assert.False(File.Exists(oldFile1));
        Assert.False(File.Exists(oldFile2));
        Assert.True(File.Exists(recentFile));
    }

    [Fact]
    public void CleanupStaleTempFiles_NonMatchingFileName_IsNeverDeletedEvenIfOld()
    {
        // voicein_*.wav 以外のパターンには一切触れない (無関係なファイルを誤削除しない)。
        string unrelatedWav = CreateFile(_testDir, "other-app-recording.wav", TimeSpan.FromDays(2));
        string unrelatedTxt = CreateFile(_testDir, "voicein_notes.txt", TimeSpan.FromDays(2));

        AudioRecorder.CleanupStaleTempFiles(_testDir, TimeSpan.FromHours(1));

        Assert.True(File.Exists(unrelatedWav));
        Assert.True(File.Exists(unrelatedTxt));
    }

    [Fact]
    public void CleanupStaleTempFiles_NonExistentDirectory_DoesNotThrow()
    {
        string missingDir = Path.Combine(Path.GetTempPath(), $"voicein-missing-{Guid.NewGuid():N}");

        var exception = Record.Exception(() =>
            AudioRecorder.CleanupStaleTempFiles(missingDir, TimeSpan.FromHours(1)));

        Assert.Null(exception);
    }

    [Fact]
    public void CleanupStaleTempFiles_EmptyDirectory_DoesNotThrow()
    {
        var exception = Record.Exception(() =>
            AudioRecorder.CleanupStaleTempFiles(_testDir, TimeSpan.FromHours(1)));

        Assert.Null(exception);
    }

    [Fact]
    public void CleanupStaleTempFiles_DefaultMinAge_LeavesFileYoungerThanOneHour()
    {
        // minAge を省略した既定値 (1時間) の境界確認。30分前のファイルはまだ新しいので残る。
        string recentFile = CreateFile(_testDir, $"voicein_{Guid.NewGuid():N}.wav", TimeSpan.FromMinutes(30));

        AudioRecorder.CleanupStaleTempFiles(_testDir);

        Assert.True(File.Exists(recentFile));
    }
}
