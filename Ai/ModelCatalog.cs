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
///   ・Gemini:  GET https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000
///              (x-goog-api-key ヘッダで認証) -&gt; models[] (各要素に name ("models/xxx" 形式) と
///              supportedGenerationMethods[])。40件。2026-09-10 に x-goog-api-key ヘッダで
///              ListModels を呼び、55件を正常取得済み (URL クエリ文字列でのみ受け付けるという
///              以前の記載は誤りだった)。
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

        // 【実測 (2026-09-10)】実データ30件中12件が整形に使えなかったため追加した除外パターン。
        "transcribe",    // 音声認識専用 (gemini-3.5-transcribe)。アプリの整形リクエスト (systemInstruction 付き)
                         // には 400「Developer instruction is not enabled for this model」を実測。system 指示なしでも
                         // 出力トークン0の空応答 (finishReason=STOP) を返し、どちらにしても整形には使えない。
        "-image",        // 画像生成モデル。このアカウントでは 429 を実測。
        "computer-use",  // エージェント用のツール操作モデル。このアカウントでは 429 を実測。
        "deep-research", // エージェント製品。1回の処理が数分かかり、発話ごとの整形には使えない。
        "antigravity",   // エージェント製品。deep-research と同様、発話ごとの整形には使えない。
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

        string body;
        using (var request = BuildGeminiListModelsRequest(apiKey))
        using (var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false))
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // ステータスコード・レスポンス本文のみを含める (API キーは含めない)。
                throw new HttpRequestException($"Gemini モデル一覧取得エラー ({(int)response.StatusCode}): {TruncateForError(body)}");
            }
        }

        return ParseGeminiModels(body);
    }

    /// <summary>
    /// Gemini ListModels 用の HTTP リクエストを組み立てる純粋関数 (ネットワークに出ない)。
    ///
    /// 【実測 (2026-09-10)】x-goog-api-key ヘッダを付けて ListModels を呼び、55件を正常取得済み。
    /// Ai/GeminiProvider.cs:28,71 と Ai/GeminiRefineProvider.cs:47,79 の generateContent 呼び出しが
    /// 「API キーは URL クエリ文字列ではなくヘッダで送る (プロキシ・DLP 機器のログに平文で
    /// 残りうるため)」という方針を採っているのと同じ理由で、ListModels でもヘッダ送信に統一する
    /// (以前あった「ListModels はキーを URL クエリでしか受け取らない」という記載は誤りだった)。
    /// RequestUri には pageSize のみを含め、API キーは一切含めない。
    /// </summary>
    /// <param name="apiKey">Gemini の API キー。呼び出し前の空・null チェックは呼び出し元の責務。</param>
    internal static HttpRequestMessage BuildGeminiListModelsRequest(string apiKey)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000");
        request.Headers.Add("x-goog-api-key", apiKey);
        return request;
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

    // NVIDIA の /v1/models は modality (音声/画像/テキスト等) を判別できるフィールドを
    // 応答に含まないため、以前は絞り込みを一切行わず全件返していた。しかし実データ80件のうち
    // 約25件がテキスト整形用途には使えない (画像・埋め込み・安全判定・翻訳・パーサー等)
    // ことを実測したため、名前 (id) にこれらの文字列を含むものは候補から除外する。
    // "vila" と "neva" は短く他のモデル名に部分一致しやすいため、スラッシュを含めて照合する。
    private static readonly string[] NvidiaExcludedNamePatterns =
    [
        "embed",          // embedding 専用モデル (nemotron-3-embed-1b)。整形用途では 404 を実測。
        "reward",         // 報酬モデル (nemotron-4-340b-reward)。整形用途では 404 を実測。
        "guard",          // 安全判定器 (meta/llama-guard-4-12b)。整形依頼に60秒無応答を実測。
        "safety",         // 安全判定器 (nemotron-3.5-content-safety)。選ぶと発言の代わりに
                          // "User Safety: safe" が貼り付けられることを実測。
        "nemotron-parse", // ドキュメント解析専用。「テキスト入力非対応」の 400 を実測。
        "translate",      // 翻訳特化モデル (riva-translate-4b-instruct-v2)。指示文ごとオウム返しすることを実測。
        "nvclip",         // 画像・テキスト対照学習 (CLIP系) モデル (nvclip)。整形用途では 404 を実測。
        "detector",       // 動画検出モデル (ai-synthetic-video-detector)。整形用途では 500 を実測。
        "fuyu",           // マルチモーダル (画像) モデル (adept/fuyu-8b)。整形用途では 404 を実測。
        "kosmos",         // マルチモーダル (画像) モデル (microsoft/kosmos-2)。整形用途では 404 を実測。
        "/vila",          // マルチモーダル (画像) モデル (nvidia/vila)。整形用途では 404 を実測。
        "/neva",          // マルチモーダル (画像) モデル (nvidia/neva-22b)。整形用途では 404 を実測。
        "deplot",         // グラフ画像→テキストのモデル (google/deplot)。整形用途では 404 を実測。
    ];

    /// <summary>
    /// NVIDIA の /v1/models からモデル一覧を取得する。modality (音声/画像/テキスト等) を
    /// 判別できるフィールドが応答に無いため、NvidiaExcludedNamePatterns による名前ベースの
    /// 除外のみを行い、id でソートして返す (戻り値は重複なし)。
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
    /// NVIDIA /v1/models の応答 JSON から、テキスト整形に使えるモデル id 一覧を抽出する純粋関数
    /// (NvidiaExcludedNamePatterns に該当するものは除外する)。
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
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                if (NvidiaExcludedNamePatterns.Any(p => id.Contains(p, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                results.Add(id);
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
