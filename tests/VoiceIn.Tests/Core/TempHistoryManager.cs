using System;
using System.IO;
using VoiceIn.Core;

namespace VoiceIn.Tests.Core;

/// <summary>
/// テスト用に一時ディレクトリ上の HistoryManager インスタンスを作成するフィクスチャ。
///
/// HistoryManager.Instance (シングルトン) は %AppData%\VoiceIn\history.json を直接組み立てるため、
/// テストからは絶対に使用しないこと。本フィクスチャは internal HistoryManager(string historyPath)
/// コンストラクタ経由で、OS の一時フォルダ配下に作った使い捨てディレクトリ上のインスタンスを渡す。
/// Dispose 時にそのディレクトリごと削除する (ベストエフォート。TempEnvFile と同様の後始末方針)。
/// </summary>
public sealed class TempHistoryManager : IDisposable
{
    public HistoryManager Manager { get; }
    public string DirectoryPath { get; }
    public string HistoryFilePath { get; }

    public TempHistoryManager()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), $"voicein-historytest-{Guid.NewGuid():N}");
        HistoryFilePath = Path.Combine(DirectoryPath, "history.json");
        Manager = new HistoryManager(HistoryFilePath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
        catch
        {
            // ベストエフォート: 一時ディレクトリ削除の失敗はテスト結果に影響させない
        }
    }
}
