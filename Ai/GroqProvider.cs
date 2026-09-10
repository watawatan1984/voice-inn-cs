using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
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

        // 1. Whisper Transcription (Groq はこの文字起こし専用。整形は行わない)
        string rawText = await TranscribeAudioAsync(audioFilePath, apiKey, whisperPrompt);
        if (string.IsNullOrWhiteSpace(rawText) || rawText.Trim() == whisperPrompt.Trim())
        {
            return string.Empty;
        }

        // 2. 整形 (Gemini/NVIDIA。Ai/RefineProviderFactory が Core/Settings.cs の設定に
        //    従って解決する。以前は Groq 自身のチャットモデルで整形していたが、Groq 側の
        //    整形用モデル提供終了で整形が丸ごと壊れる事故が起きたため切り離した)。
        //
        // 整形はあくまで付加価値であり、Whisper の文字起こし自体は既に成功している。
        // 整形バックエンド側の障害 (API キー未設定・通信エラー・モデル提供終了など) で
        // この関数全体を失敗させると、まさに今回切り離す原因となった事故
        // (整形が壊れて発話ごと失われる) を再発させてしまう。そのため
        // Ai/LocalProvider.cs のハイブリッド整形と同じ方針で、整形の失敗はログに
        // 残すのみに留め、生の文字起こし結果へフォールバックする。
        var refineProvider = RefineProviderFactory.CreateProvider();
        try
        {
            return await refineProvider.RefineAsync(rawText, refineSystemPrompt);
        }
        catch (Exception ex)
        {
            Logger.Warn($"GroqProvider: 整形に失敗したため生の文字起こし結果を返します -- {ex.GetType().Name}: {ex.Message}");
            return rawText;
        }
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
