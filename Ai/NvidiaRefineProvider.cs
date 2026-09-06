using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VoiceIn.Ai;

/// <summary>
/// NVIDIA API (OpenAI 互換の Chat Completions エンドポイント) を使ったテキスト整形バックエンド。
/// Ai/RefineProviderFactory で "nvidia" を選んだ場合に使われる、Gemini 以外の選択肢。
///
/// 【実測 (管理側で確認済み)】
/// ベース URL: https://integrate.api.nvidia.com/v1、POST /chat/completions。
/// 既定モデル nvidia/nemotron-3.5-lightning-30b-a3b で 914ms。
/// 【注意】nvidia/nemotron-nano-3-30b-a3b はモデル一覧には出るが、このアカウントでは
/// 404 になることを実測済みのため、既定モデルには絶対に採用しないこと。
///
/// 【最重要: thinking (思考モード) は必ず無効にする】
/// 実測結果 (同一リクエストで enable_thinking のみを変えた比較):
///
///   enable_thinking | 所要時間   | content の中身
///   ----------------|-----------|--------------------------------------------
///   false           | 914 ms    | 正しい整形結果
///   true            | 44,640 ms | "Here's a thinking process:" から始まる思考過程そのもの
///
/// true にすると reasoning_content だけでなく content 自体にも思考過程が流れ込む。
/// つまり「content だけを読む」という防御だけでは不十分で、応答自体が壊れる。
/// 有効にすると、モデルの思考過程がそのままユーザーのアクティブウィンドウへ貼り付けられる
/// 重大な不具合になるため、chat_template_kwargs.enable_thinking はコード側で false に
/// ハードコードし、設定や環境変数からは一切変更できないようにする
/// (下記 RefineAsync 内の匿名型リテラルを参照。値を外から注入できる作りに絶対に変更しないこと)。
/// </summary>
public class NvidiaRefineProvider : IRefineProvider
{
    public string ProviderName => "nvidia";

    // 発話のたびに RefineProviderFactory.CreateProvider() が呼ばれインスタンスが都度生成される
    // ため、HttpClient はインスタンスフィールドではなく static でプロセス全体で使い回す
    // (他の Ai/*Provider.cs と同じ方針。毎回 new すると常駐アプリでソケット枯渇に至るため)。
    // タイムアウトも既存プロバイダに揃えて 60 秒とする
    // (thinking を無効化した通常の応答は 1 秒未満だが、既存プロバイダとの一貫性を優先する)。
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

    private const string BaseUrl = "https://integrate.api.nvidia.com/v1";

    public async Task<string> RefineAsync(string rawText, string systemPrompt)
    {
        string? apiKey = Environment.GetEnvironmentVariable("NVIDIA_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("NVIDIA_API_KEY が設定されていません。.env ファイルを確認してください。");
        }

        // モデル名は環境変数で上書き可能にする (他の Ai/*Provider.cs の *_MODEL 環境変数と同じ方式)。
        // 既定値 nvidia/nemotron-3.5-lightning-30b-a3b は実測 914ms (thinking 無効時)。
        string refineModel = Environment.GetEnvironmentVariable("NVIDIA_REFINE_MODEL") ?? "nvidia/nemotron-3.5-lightning-30b-a3b";

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var payload = new
        {
            model = refineModel,
            temperature = 0.0,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = rawText }
            },
            // 【厳守】thinking (思考モード) は必ず無効。クラスコメントの実測結果を参照。
            // 有効にすると content 自体に思考過程の文章が流れ込み、44 秒以上かかったうえで
            // 意味不明なテキストがユーザーのアクティブウィンドウへ貼り付けられる重大な不具合になる。
            // このオブジェクトはハードコードのリテラルであり、設定値・環境変数のいずれからも
            // 値を注入できない作りにしている。true にできる経路を絶対に作らないこと。
            chat_template_kwargs = new { enable_thinking = false }
        };

        string jsonPayload = JsonSerializer.Serialize(payload);
        request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request);
        string result = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            // 例外メッセージは App.xaml.cs 経由で history.json に平文保存され、バルーン通知にも
            // 表示される。API レスポンス本文を無制限に含めないよう、先頭 500 文字程度に切り詰める。
            // 【実測】NVIDIA のエラー応答にはアカウント識別子が含まれることを確認済みのため、
            // 他プロバイダ以上にこの切り詰めが重要。
            throw new HttpRequestException($"NVIDIA 整形 API エラー ({(int)response.StatusCode}): {TruncateForError(result)}");
        }

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var msg = choices[0].GetProperty("message");

            // 【厳守】reasoning_content は絶対に読まない。content のみを読む。
            // クラスコメントのとおり、thinking を無効化していれば content には正しい整形結果
            // のみが入る。reasoning_content を読む・フォールバックするコードを絶対に追加しないこと。
            if (msg.TryGetProperty("content", out var contentElem))
            {
                return contentElem.GetString()?.Trim() ?? rawText;
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
