using System.Threading.Tasks;

namespace VoiceIn.Ai;

/// <summary>
/// 文字起こし後の生テキストを整形する「整形バックエンド」の抽象。
///
/// 【経緯】以前は Groq のチャットモデル (Ai/GroqProvider.cs) が Whisper 文字起こしと
/// 整形の両方を担っていたが、Groq 側で整形用チャットモデルの提供が終了し整形が丸ごと
/// 壊れる事故が実際に起きた。そのため整形処理を文字起こしから切り離し、差し替え可能な
/// バックエンドとして独立させる。実装は GeminiRefineProvider (既定) / NvidiaRefineProvider
/// の 2 つで、どちらを使うかは Ai/RefineProviderFactory が Core/Settings.cs の設定値
/// (AppSettings.RefineProvider) から解決する。
///
/// IAiProvider (音声ファイル → テキスト) とは異なり、こちらはテキスト → テキストの
/// 変換のみを扱う (音声を扱わない) ため、あえて別インターフェースとして分離している。
/// </summary>
public interface IRefineProvider
{
    /// <summary>この整形バックエンドの識別名 ("gemini" / "nvidia")。</summary>
    string ProviderName { get; }

    /// <summary>
    /// 生テキスト (Whisper 等による文字起こし結果) を systemPrompt の指示に従って整形し、
    /// 整形後のテキストを返す。
    /// </summary>
    /// <param name="rawText">整形対象の生テキスト。</param>
    /// <param name="systemPrompt">整形方針を指示するシステムプロンプト。</param>
    Task<string> RefineAsync(string rawText, string systemPrompt);
}
