using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using VoiceIn.Core;

namespace VoiceIn.Ai;

public class GroqProvider : IAiProvider
{
    public string ProviderName => "groq";

    // 発話のたびに AiProviderFactory.CreateProvider() が呼ばれ Provider インスタンスが
    // 都度生成されるため、HttpClient はインスタンスフィールドではなく static で
    // プロセス全体で使い回す（毎回 new すると常駐アプリでソケット枯渇に至るため）。
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<string> TranscribeAsync(string audioFilePath, string prompt)
    {
        string? apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("GROQ_API_KEY が設定されていません。.env ファイルを確認してください。");
        }

        var prompts = SettingsManager.Instance.Settings.Prompts;
        string whisperPrompt = prompts.GroqWhisperPrompt;
        string refineSystemPrompt = prompt; // コンテキスト最適化済みの整形プロンプト

        // 1. Whisper Transcription
        string rawText = await TranscribeAudioAsync(audioFilePath, apiKey, whisperPrompt);
        if (string.IsNullOrWhiteSpace(rawText) || rawText.Trim() == whisperPrompt.Trim())
        {
            return string.Empty;
        }

        // 2. LLaMA Refinement
        string refinedText = await RefineTextAsync(rawText, apiKey, refineSystemPrompt);
        return refinedText;
    }

    private async Task<string> TranscribeAudioAsync(string audioFilePath, string apiKey, string prompt)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/audio/transcriptions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var multipart = new MultipartFormDataContent();
        byte[] audioBytes = await File.ReadAllBytesAsync(audioFilePath);
        var fileContent = new ByteArrayContent(audioBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        // モデル名は環境変数で上書き可能にする (GeminiProvider の GEMINI_MODEL と同様の方式)。
        // 未設定の場合は従来どおりのモデルを既定値として使う。
        string whisperModel = Environment.GetEnvironmentVariable("GROQ_WHISPER_MODEL") ?? "whisper-large-v3";

        multipart.Add(fileContent, "file", Path.GetFileName(audioFilePath));
        multipart.Add(new StringContent(whisperModel), "model");
        multipart.Add(new StringContent("ja"), "language");
        multipart.Add(new StringContent("0.0"), "temperature");
        multipart.Add(new StringContent("text"), "response_format");
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            multipart.Add(new StringContent(prompt), "prompt");
        }

        request.Content = multipart;
        using var response = await _httpClient.SendAsync(request);
        string result = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            // 例外メッセージは App.xaml.cs 経由で history.json に平文保存され、バルーン通知にも
            // 表示される。API レスポンス本文を無制限に含めないよう、先頭 500 文字程度に切り詰める。
            throw new HttpRequestException($"Groq Whisper エラー ({(int)response.StatusCode}): {TruncateForError(result)}");
        }

        return result.Trim();
    }

    /// <summary>
    /// 生の文字起こしテキストを Groq の LLM で整形して返す (テキストイン・テキストアウト)。
    /// LocalProvider のハイブリッドモード (ローカルで文字起こし → クラウド LLM で整形) から
    /// 再利用するために、内部の 3 引数版から API キー解決のみを切り出して public 化している。
    /// GROQ_API_KEY が未設定の場合は例外を投げるので、呼び出し側 (LocalProvider) で
    /// キャッチし、整形をあきらめて生の文字起こし結果を返すフォールバックを行うこと。
    /// </summary>
    public async Task<string> RefineTextAsync(string rawText, string systemPrompt)
    {
        string? apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("GROQ_API_KEY が設定されていません。.env ファイルを確認してください。");
        }

        return await RefineTextAsync(rawText, apiKey, systemPrompt);
    }

    private async Task<string> RefineTextAsync(string rawText, string apiKey, string systemPrompt)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        // モデル名は環境変数で上書き可能にする (GeminiProvider の GEMINI_MODEL と同様の方式)。
        // 未設定の場合は従来どおりのモデルを既定値として使う。
        string refineModel = Environment.GetEnvironmentVariable("GROQ_REFINE_MODEL") ?? "llama-3.3-70b-versatile";

        var payload = new
        {
            model = refineModel,
            temperature = 0.0,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = rawText }
            }
        };

        string jsonPayload = JsonSerializer.Serialize(payload);
        request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request);
        string result = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            // 例外メッセージは App.xaml.cs 経由で history.json に平文保存され、バルーン通知にも
            // 表示される。API レスポンス本文を無制限に含めないよう、先頭 500 文字程度に切り詰める。
            throw new HttpRequestException($"Groq Refine エラー ({(int)response.StatusCode}): {TruncateForError(result)}");
        }

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var msg = choices[0].GetProperty("message");
            if (msg.TryGetProperty("content", out var contentElem))
            {
                return contentElem.GetString()?.Trim() ?? string.Empty;
            }
        }

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
