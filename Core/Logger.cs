using System;
using System.IO;

namespace VoiceIn.Core;

/// <summary>
/// 常駐アプリの障害調査用の簡易ファイルロガー。
/// 出力先は %AppData%\VoiceIn\app.log (Environment.SpecialFolder.ApplicationData 配下、
/// ユーザー単位で保護される場所)。VoiceIn.csproj の OutputType は WinExe のため、通常の
/// 起動方法ではコンソールが存在せず Console.WriteLine の出力はどこにも残らない。
/// 移植元 Python 版の app.log 相当の調査手掛かりを残すために本クラスを用意する。
///
/// 【プライバシー上の厳守事項】呼び出し側は以下を絶対にログへ渡さないこと:
///   ・API キー (GEMINI_API_KEY / GROQ_API_KEY の値)
///   ・音声から文字起こしされたテキスト本文 (ユーザーの発話内容そのもの。機微情報を含みうる)
///   ・クリップボードの内容
///
/// UI スレッド・スレッドプール・タイマスレッドなど複数スレッドから同時に呼ばれるため、
/// ファイル I/O は _lock で排他制御する。ログ書き込み自体の失敗がアプリ本体の動作へ
/// 波及しないよう、公開メソッドは内部で発生した例外を握りつぶし、外へは絶対に投げない。
/// </summary>
public static class Logger
{
    /// <summary>
    /// このサイズ (バイト) を超えたら app.log を app.log.1 へローテーションする。
    /// 常駐アプリのため、ログファイルを無制限に肥大化させないための上限。
    /// </summary>
    private const long MaxFileSizeBytes = 1 * 1024 * 1024; // 1MB

    private static readonly object _lock = new();

    private static readonly string _logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceIn");

    private static readonly string _logFilePath = Path.Combine(_logDirectory, "app.log");

    /// <summary>参考情報レベルのログを記録する。</summary>
    public static void Info(string message) => Write("INFO", message);

    /// <summary>警告レベルのログを記録する (異常ではないが留意すべき事象、例: 処理のタイムアウト)。</summary>
    public static void Warn(string message) => Write("WARN", message);

    /// <summary>エラーメッセージのみを記録する (例外オブジェクトが無い場合)。</summary>
    public static void Error(string message) => Write("ERROR", message);

    /// <summary>
    /// 例外の種別とメッセージを記録する。スタックトレースは記録しない
    /// (1行1エントリを保つため。また、message / ex.Message に API キーや文字起こし本文を
    /// 含めないのは呼び出し側の責務)。
    /// </summary>
    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message} -- {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            // ex.Message 等に改行が含まれるケースへの保険として、1行1エントリを保つために
            // 改行文字を除去してから書き込む。
            string sanitized = message.Replace("\r", " ").Replace("\n", " ");
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {sanitized}";

            lock (_lock)
            {
                Directory.CreateDirectory(_logDirectory);
                RotateIfNeeded();
                File.AppendAllLines(_logFilePath, [line]);
            }
        }
        catch
        {
            // ログ書き込み自体の失敗でアプリ本体の動作を巻き込まないよう、例外は握りつぶす。
        }
    }

    /// <summary>
    /// app.log が上限サイズを超えていれば app.log.1 へ退避する (直近1世代のみ保持)。
    /// 呼び出し元 (Write) の try/catch 内から呼ばれるため、ここでは例外を握りつぶさない。
    /// </summary>
    private static void RotateIfNeeded()
    {
        var info = new FileInfo(_logFilePath);
        if (!info.Exists || info.Length < MaxFileSizeBytes)
        {
            return;
        }

        string rotatedPath = _logFilePath + ".1";
        File.Move(_logFilePath, rotatedPath, overwrite: true);
    }
}
