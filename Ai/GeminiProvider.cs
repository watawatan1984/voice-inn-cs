using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VoiceIn.Ai;

public class GeminiProvider : IAiProvider
{
    public string ProviderName => "gemini";

    // 発話のたびに AiProviderFactory.CreateProvider() が呼ばれ Provider インスタンスが
    // 都度生成されるため、HttpClient はインスタンスフィールドではなく static で
    // プロセス全体で使い回す（毎回 new すると常駐アプリでソケット枯渇に至るため）。
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<string> TranscribeAsync(string audioFilePath, string prompt)
    {
        string? apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("GEMINI_API_KEY が設定されていません。.env ファイルを確認してください。");
        }

        string model = Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? "gemini-2.5-flash";
        // API キーは URL のクエリ文字列ではなく x-goog-api-key ヘッダで送る。
        // クエリ文字列はプロキシ・DLP 機器の URL ログに平文で残りうるため。
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

        byte[] audioBytes = await File.ReadAllBytesAsync(audioFilePath);
        string base64Audio = Convert.ToBase64String(audioBytes);

        var requestObj = new
        {
            contents = new object[]
            {
                new
                {
                    parts = new object[]
                    {
                        new
                        {
                            inline_data = new
                            {
                                mime_type = "audio/wav",
                                data = base64Audio
                            }
                        },
                        new
                        {
                            text = prompt
                        }
                    }
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
        // Google が現在推奨する方式: API キーは URL クエリ文字列ではなく専用ヘッダで送る。
        request.Headers.Add("x-goog-api-key", apiKey);

        using var response = await _httpClient.SendAsync(request);
        string responseString = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            // 例外メッセージは App.xaml.cs 経由で history.json に平文保存され、バルーン通知にも
            // 表示される。API レスポンス本文を無制限に含めないよう、先頭 500 文字程度に切り詰める。
            throw new HttpRequestException($"Gemini API エラー ({(int)response.StatusCode}): {TruncateForError(responseString)}");
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
                    return textElem.GetString()?.Trim() ?? string.Empty;
                }
            }
        }

        return string.Empty;
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
