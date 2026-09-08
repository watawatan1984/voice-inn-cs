using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceIn.Ai;

/// <summary>
/// 設定画面 (Ui/SettingsWindow.xaml) のモデル選択 ComboBox 3つ (Groq Whisper モデル /
/// Gemini 整形モデル / NVIDIA 整形モデル) 向けに、各社の実際のモデル一覧を取得する。
///
/// 【背景】ComboBox 自体は IsEditable="True" で自由入力が技術的には効いていたが、
/// 候補が各社1件ずつしかハードコードされていなかったため、ユーザーには
/// 「モデルが固定されていて追加できない」と映っていた。本クラスは各社の一覧 API を
/// 叩いて候補を増やすためのものであり、既存の自由入力自体はそのまま活かす
/// (一覧に無いモデル名を入力した場合も ComboBox.Text はそのまま尊重される)。
///
/// 【実測 (2026-09 時点、確認済み)】
///   ・Gemini:  GET https://generativelanguage.googleapis.com/v1beta/models?key=&lt;KEY&gt;&amp;pageSize=1000
///              -&gt; models[] (各要素に name ("models/xxx" 形式) と supportedGenerationMethods[])。40件。
///   ・Groq:    GET https://api.groq.com/openai/v1/models (Authorization: Bearer &lt;KEY&gt;) -&gt; data[].id。
///              全14件中 whisper 系は whisper-large-v3 / whisper-large-v3-turbo の2件。
///   ・NVIDIA:  GET https://integrate.api.nvidia.com/v1/models (Authorization: Bearer &lt;KEY&gt;) -&gt; data[].id。81件。
/// </summary>
public static class ModelCatalog
{
    // Ai/GroqProvider.cs 冒頭のコメントと同じ方針: 設定画面の「更新」ボタンを押すたびに
    // 新しい HttpClient を new すると、常駐アプリでソケット枯渇に至るおそれがある。
    // そのため HttpClient はインスタンスフィールドではなく static でプロセス全体で使い回す。
    // モデル一覧の取得は文字起こし・整形の本処理より軽い操作のため、タイムアウトは
    // 他プロバイダ (60秒) より短い10秒とする。
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// API キー未設定・オフライン・API 障害時に使う既定候補 (Gemini 整形用)。
    /// 1件だけだと今回ユーザーから指摘された「モデルが固定されていて追加できない」という
    /// 問題が再発するため、必ず複数件を保持する。
    /// </summary>
    public static readonly IReadOnlyList<string> GeminiFallbackModels = new[]
    {
        "gemini-flash-lite-latest",
        "gemini-flash-latest",
        "gemini-2.5-flash",
        "gemini-2.5-pro",
        "gemini-pro-latest",
    };

    /// <summary>API キー未設定・オフライン・API 障害時に使う既定候補 (Groq Whisper 用)。</summary>
    public static readonly IReadOnlyList<string> GroqWhisperFallbackModels = new[]
    {
        "whisper-large-v3",
        "whisper-large-v3-turbo",
    };

    /// <summary>API キー未設定・オフライン・API 障害時に使う既定候補 (NVIDIA 整形用)。</summary>
    public static readonly IReadOnlyList<string> NvidiaFallbackModels = new[]
    {
        "nvidia/nemotron-3.5-lightning-30b-a3b",
        "meta/llama-3.3-70b-instruct",
        "deepseek-ai/deepseek-v4-flash-0731",
    };

    // Gemini の ListModels は generateContent 対応でも、テキスト整形用途には使えない/
    // 使うべきでない系統を多数含む (画像・動画・音声生成、embedding、実験的なライブ音声など)。
    // 名前 (先頭の "models/" を除いた部分) にこれらの文字列を含むものは候補から除外する。
    private static readonly string[] GeminiExcludedNamePatterns =
    [
        "embedding", "imagen", "veo", "-tts", "aqa", "image-generation",
        "robotics", "native-audio", "live-", "learnlm", "gemma", "lyria", "nano-banana",
    ];

    /// <summary>
    /// Gemini の ListModels API からテキスト整形に使えるモデル名一覧を取得する
    /// (generateContent 対応かつ GeminiExcludedNamePatterns に該当しないもの)。
    /// 戻り値はソート済み・重複なし。
    /// </summary>
    /// <param name="apiKey">Gemini の API キー。空・null の場合は例外を投げる。</param>
    /// <param name="ct">ウィンドウが閉じられた等での中断用。</param>
    /// <exception cref="InvalidOperationException">apiKey が空または空白のみの場合。</exception>
    /// <exception cref="HttpRequestException">API がエラーステータスを返した場合。</exception>
    /// <exception cref="JsonException">応答が JSON として解釈できない場合。</exception>
    public static async Task<IReadOnlyList<string>> FetchGeminiModelsAsync(string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Gemini の API キーが指定されていません。");
        }

        // 【実測済み】Gemini の ListModels は API キーをヘッダではなく URL クエリ文字列で
        // 受け取る仕様 (Ai/GeminiProvider.cs 等の generateContent 呼び出しが使う
        // x-goog-api-key ヘッダとは異なる)。この URL 自体にキーが含まれるため、
        // 例外メッセージ・ログの類には url 変数の値を絶対に含めないこと。
        string url = $"https://generativelanguage.googleapis.com/v1beta/models?key={Uri.EscapeDataString(apiKey)}&pageSize=1000";

        string body;
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        using (var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false))
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // ステータスコード・レスポンス本文のみを含める (URL・API キーは含めない)。
                throw new HttpRequestException($"Gemini モデル一覧取得エラー ({(int)response.StatusCode}): {TruncateForError(body)}");
            }
        }

        return ParseGeminiModels(body);
    }

    /// <summary>
    /// Gemini ListModels の応答 JSON から、テキスト整形に使えるモデル名一覧を抽出する純粋関数。
    /// API キーを一切扱わないため、ネットワークに出ずに単体テストできる。
    /// </summary>
    internal static IReadOnlyList<string> ParseGeminiModels(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var results = new SortedSet<string>(StringComparer.Ordinal);

        if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in models.EnumerateArray())
            {
                if (!model.TryGetProperty("name", out var nameElem))
                {
                    continue;
                }

                string? rawName = nameElem.GetString();
                if (string.IsNullOrWhiteSpace(rawName))
                {
                    continue;
                }

                // "models/gemini-2.5-flash" -> "gemini-2.5-flash"
                string name = rawName.StartsWith("models/", StringComparison.Ordinal)
                    ? rawName["models/".Length..]
                    : rawName;

                if (!SupportsGenerateContent(model))
                {
                    continue;
                }

                if (GeminiExcludedNamePatterns.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                results.Add(name);
            }
        }

        return results.ToList();
    }

    private static bool SupportsGenerateContent(JsonElement model)
    {
        if (!model.TryGetProperty("supportedGenerationMethods", out var methods) || methods.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var m in methods.EnumerateArray())
        {
            if (string.Equals(m.GetString(), "generateContent", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Groq の /v1/models からモデル一覧を取得し、id に "whisper" を含むものだけを返す
    /// (文字起こし用 ComboBox 向けのため、チャット系モデルは除外する)。
    /// 戻り値はソート済み・重複なし。
    /// </summary>
    /// <exception cref="InvalidOperationException">apiKey が空または空白のみの場合。</exception>
    public static async Task<IReadOnlyList<string>> FetchGroqWhisperModelsAsync(string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Groq の API キーが指定されていません。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.groq.com/openai/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Groq モデル一覧取得エラー ({(int)response.StatusCode}): {TruncateForError(body)}");
        }

        return ParseGroqWhisperModels(body);
    }

    /// <summary>
    /// Groq /v1/models の応答 JSON から、id に "whisper" を含むものだけを抽出する純粋関数。
    /// API キーを一切扱わないため、ネットワークに出ずに単体テストできる。
    /// </summary>
    internal static IReadOnlyList<string> ParseGroqWhisperModels(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var results = new SortedSet<string>(StringComparer.Ordinal);

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idElem))
                {
                    continue;
                }

                string? id = idElem.GetString();
                if (!string.IsNullOrWhiteSpace(id) && id.Contains("whisper", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(id);
                }
            }
        }

        return results.ToList();
    }

    /// <summary>
    /// NVIDIA の /v1/models からモデル一覧を取得する。modality (音声/画像/テキスト等) を
    /// 判別できるフィールドが応答に無いため、絞り込みは行わず id でソートして全件返す
    /// (戻り値は重複なし)。
    /// </summary>
    /// <exception cref="InvalidOperationException">apiKey が空または空白のみの場合。</exception>
    public static async Task<IReadOnlyList<string>> FetchNvidiaModelsAsync(string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("NVIDIA の API キーが指定されていません。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://integrate.api.nvidia.com/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"NVIDIA モデル一覧取得エラー ({(int)response.StatusCode}): {TruncateForError(body)}");
        }

        return ParseNvidiaModels(body);
    }

    /// <summary>
    /// NVIDIA /v1/models の応答 JSON から id 一覧を抽出する純粋関数。
    /// API キーを一切扱わないため、ネットワークに出ずに単体テストできる。
    /// </summary>
    internal static IReadOnlyList<string> ParseNvidiaModels(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var results = new SortedSet<string>(StringComparer.Ordinal);

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idElem))
                {
                    continue;
                }

                string? id = idElem.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    results.Add(id);
                }
            }
        }

        return results.ToList();
    }

    /// <summary>
    /// 例外メッセージに載せる API レスポンス本文を先頭 maxLength 文字に切り詰める。
    /// Ai/GroqProvider.cs の TruncateForError と同じ方針 (このクラス専用に複製したもの。
    /// private のため直接の共有はできず、同じロジックを保つことを優先した)。
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
