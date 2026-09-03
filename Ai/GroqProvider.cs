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
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

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

        multipart.Add(fileContent, "file", Path.GetFileName(audioFilePath));
        multipart.Add(new StringContent("whisper-large-v3"), "model");
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
            throw new HttpRequestException($"Groq Whisper エラー ({(int)response.StatusCode}): {result}");
        }

        return result.Trim();
    }

    private async Task<string> RefineTextAsync(string rawText, string apiKey, string systemPrompt)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var payload = new
        {
            model = "llama-3.3-70b-versatile",
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
            throw new HttpRequestException($"Groq Refine エラー ({(int)response.StatusCode}): {result}");
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
}
