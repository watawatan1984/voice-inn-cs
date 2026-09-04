using System;
using System.IO;

namespace VoiceIn.Tests.Core;

/// <summary>
/// テスト用に一時 .env ファイルを作成するフィクスチャ。
/// EnvLoader.Load は Environment.SetEnvironmentVariable でプロセス全体（=テストプロセス全体）の
/// 環境変数を書き換えるため、Dispose 時に生成したファイルの削除と、
/// このテストが設定したキーの環境変数のクリーンアップを必ず行う。
/// これを怠ると後続のテストや同一プロセス内の他のテストが汚染された環境変数を読んでしまい、
/// フレーキーな失敗の原因になる。
/// </summary>
public sealed class TempEnvFile : IDisposable
{
    public string FilePath { get; }

    private readonly string[] _keysToCleanUp;

    public TempEnvFile(string content, params string[] keysToCleanUp)
    {
        FilePath = Path.Combine(Path.GetTempPath(), $"voicein-envtest-{Guid.NewGuid():N}.env");
        File.WriteAllText(FilePath, content);
        _keysToCleanUp = keysToCleanUp;
    }

    public void Dispose()
    {
        foreach (var key in _keysToCleanUp)
        {
            Environment.SetEnvironmentVariable(key, null);
        }

        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch
        {
            // ベストエフォート: 一時ファイル削除の失敗はテスト結果に影響させない
        }
    }
}
