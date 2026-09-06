using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VoiceIn.Ai;

/// <summary>
/// Gemini API を使ったテキスト整形バックエンド (既定の整形バックエンド)。
/// Ai/GeminiProvider.cs (音声ファイルを直接文字起こしする用途) とはエンドポイントの叩き方は
/// 同じだが、扱う対象が「音声 + 指示」ではなく「テキスト + 指示」である点が異なるため、
/// 別クラスとして分離している (GEMINI_API_KEY は共用するが、モデルは別の環境変数で指定する)。
///
/// 【実測 (管理側で確認済み)】既定モデル gemini-flash-lite-latest で 1,032ms、
/// 専門用語の変換も良好 (「エヌピーエム ランビルド」→「npm run build」まで正しく変換)。
/// 参考: gemini-3.8-flash は 5,623ms と遅く、gemini-flash-latest は高負荷エラーを返した。
/// </summary>
public class GeminiRefineProvider : IRefineProvider
{
    public string ProviderName => "gemini";

    // 発話のたびに RefineProviderFactory.CreateProvider() が呼ばれインスタンスが都度生成される
    // ため、HttpClient はインスタンスフィールドではなく static でプロセス全体で使い回す
    // (Ai/GeminiProvider.cs 等、既存プロバイダと同じ方針。毎回 new すると常駐アプリで
    // ソケット枯渇に至るため)。タイムアウトも既存プロバイダに揃えて 60 秒とする。
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<string> RefineAsync(string rawText, string systemPrompt)
    {
        string? apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("GEMINI_API_KEY が設定されていません。.env ファイルを確認してください。");
        }

        // 【注意】GEMINI_MODEL (Ai/GeminiProvider.cs が音声から直接文字起こしする際に使う
        // モデル) とは別の環境変数にしている。用途が異なるため、片方の変更がもう片方に
        // 意図せず影響しないようにするため。
        //
        // 既定モデルについて (実測に基づく判断):
        // gemini-flash-lite-latest のように "-latest" が付くエイリアスは常に最新版を指すため、
        // Groq のチャットモデル提供終了で整形が壊れた事故と同種の問題が構造的に起きにくい。
        // 特定バージョン名 (例: gemini-2.5-flash) を固定すると、そのバージョンの提供が
        // 終了した時点で同じ事故を繰り返すおそれがある。
        string model = Environment.GetEnvironmentVariable("GEMINI_REFINE_MODEL") ?? "gemini-flash-lite-latest";
        // API キーは URL のクエリ文字列ではなく x-goog-api-key ヘッダで送る。
        // クエリ文字列はプロキシ・DLP 機器の URL ログに平文で残りうるため。
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

        // テキスト整形は systemInstruction にシステムプロンプト、contents に対象テキストを
        // 入れるだけで動作する (音声を扱う GeminiProvider.TranscribeAsync とは異なり
        // inline_data は不要)。
        var requestObj = new
        {
            systemInstruction = new
            {
                parts = new object[] { new { text = systemPrompt } }
            },
            contents = new object[]
            {
                new
                {
                    parts = new object[] { new { text = rawText } }
                }
            },
            generationConfig = new
            {
                temperature = 0.0
            }
        };

        string jsonPayload = JsonSerializer.Serialize(requestObj);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-goog-api-key", apiKey);

        using var response = await _httpClient.SendAsync(request);
        string responseString = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            // 例外メッセージは App.xaml.cs 経由で history.json に平文保存され、バルーン通知にも
            // 表示される。API レスポンス本文を無制限に含めないよう、先頭 500 文字程度に切り詰める。
            throw new HttpRequestException($"Gemini 整形 API エラー ({(int)response.StatusCode}): {TruncateForError(responseString)}");
        }

        using var doc = JsonDocument.Parse(responseString);
        var root = doc.RootElement;

        if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            var firstCandidate = candidates[0];
            if (firstCandidate.TryGetProperty("content", out var contentElem) &&
                contentElem.TryGetProperty("parts", out var parts) &&
                parts.GetArrayLength() > 0)
            {
                var firstPart = parts[0];
                if (firstPart.TryGetProperty("text", out var textElem))
                {
                    return textElem.GetString()?.Trim() ?? rawText;
                }
            }
        }

        // レスポンスの形が想定と異なる場合も、整形前の生テキストへフォールバックする
        // (発話そのものを失わせないため。呼び出し側の GroqProvider/LocalProvider は
        // さらに例外時のフォールバックも別途持つ)。
        return rawText;
    }

    /// <summary>
    /// 例外メッセージに載せる API レスポンス本文を先頭 maxLength 文字に切り詰める。
    /// ステータスコードは呼び出し側で別途メッセージに含めているため、ここでは本文のみを扱う。
    /// </summary>
    private static string TruncateForError(string text, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength] + "…(以下省略)";
    }
}
