namespace VoiceIn.Ai;

/// <summary>
/// 整形バックエンド (Ai/GeminiRefineProvider.cs / Ai/NvidiaRefineProvider.cs) が返した
/// 整形結果を、整形前の生テキスト (rawText) へのフォールバックと合成する純粋関数。
///
/// 【背景 (2026-09-10)】従来はどちらのプロバイダも
/// <c>xxx.GetString()?.Trim() ?? rawText</c> という式で結果を組み立てていた。
/// <c>??</c> 演算子は左辺が null のときしか右辺を使わないため、モデルが「空文字列」を
/// 返した場合は
/// "" がそのまま返ってしまっていた。
///
/// この "" は App.xaml.cs の処理で以下のように扱われるため、発話が跡形もなく消える:
///   1. HistoryManager.Instance.AppendItem("", ...) で空文字が履歴に保存される。
///   2. SetAppState("success") が呼ばれ、画面上は「成功」の表示になる。
///   3. その直後の string.IsNullOrWhiteSpace(text) 判定により、クリップボードへの格納・
///      自動貼り付けの両方がスキップされる。
/// 結果として、生の文字起こし結果はどこにも残らず、画面は成功と表示されたまま発話だけが
/// 消失する。
///
/// ただし、2026-09-10 に 16 モデルを実際の整形リクエストで呼んだ範囲では、空文字列を返す
/// モデルは見つかっていない (失敗はすべて例外で、呼び出し側の GroqProvider / LocalProvider が
/// 生テキストへフォールバックする)。gemini-3.5-transcribe は system 指示なしなら空応答を返すが、
/// アプリの整形リクエスト (systemInstruction 付き) には 400 を返す。したがって本関数は、実害の
/// 確認された不具合の修正ではなく、整形モデルを一覧から自由に選べるようになったことへの防御である。
///
/// 本関数は refined が null・空・空白のみのいずれであっても rawText へフォールバックする
/// ことで、この事故を防ぐ。テスト容易性のため、Core.Logger を含むあらゆる外部依存に
/// 一切触れない (空応答の検知ログは呼び出し側の各プロバイダが個別に出す)。
/// </summary>
internal static class RefineOutput
{
    /// <summary>
    /// refined が null・空・空白のみなら rawText を返す。それ以外の場合は
    /// refined を Trim() した文字列を返す。
    /// </summary>
    /// <param name="refined">整形バックエンドの応答から取り出した整形結果 (null・空・空白のみもありうる)。</param>
    /// <param name="rawText">整形前の生テキスト (Whisper 等による文字起こし結果)。</param>
    internal static string OrRaw(string? refined, string rawText)
    {
        return string.IsNullOrWhiteSpace(refined) ? rawText : refined.Trim();
    }
}
