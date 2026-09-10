using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using VoiceIn.Ai;
using Xunit;

namespace VoiceIn.Tests.Ai;

public class ModelCatalogTests
{
    [Fact]
    public void FallbackModels_AreNotEmptyAndContainExpectedDefaults()
    {
        // 【欠陥修正】NotEmpty だけでは1件でも通ってしまい、今回ユーザーが怒った
        // 「モデルが固定されていて追加できない」問題 (候補が実質1件しかない) を再発検知できない。
        // 3つのフォールバック一覧それぞれについて、必ず複数件保持していることを確認する。
        Assert.True(ModelCatalog.GeminiFallbackModels.Count >= 2,
            $"GeminiFallbackModels は複数件必要 (実際: {ModelCatalog.GeminiFallbackModels.Count}件)");
        Assert.Contains("gemini-flash-lite-latest", ModelCatalog.GeminiFallbackModels);

        Assert.True(ModelCatalog.GroqWhisperFallbackModels.Count >= 2,
            $"GroqWhisperFallbackModels は複数件必要 (実際: {ModelCatalog.GroqWhisperFallbackModels.Count}件)");
        Assert.Contains("whisper-large-v3", ModelCatalog.GroqWhisperFallbackModels);

        Assert.True(ModelCatalog.NvidiaFallbackModels.Count >= 2,
            $"NvidiaFallbackModels は複数件必要 (実際: {ModelCatalog.NvidiaFallbackModels.Count}件)");
        Assert.Contains("nvidia/nemotron-3.5-lightning-30b-a3b", ModelCatalog.NvidiaFallbackModels);
    }

    [Fact]
    public void ParseGeminiModels_FiltersAndSortsCorrectly()
    {
        string json = """
        {
            "models": [
                {
                    "name": "models/gemini-2.5-flash",
                    "supportedGenerationMethods": ["generateContent"]
                },
                {
                    "name": "models/gemini-2.5-pro",
                    "supportedGenerationMethods": ["generateContent"]
                },
                {
                    "name": "models/text-embedding-004",
                    "supportedGenerationMethods": ["embedContent"]
                },
                {
                    "name": "models/imagen-3.0-generate-002",
                    "supportedGenerationMethods": ["generateContent"]
                },
                {
                    "name": "models/gemini-flash-experimental",
                    "supportedGenerationMethods": ["generateContent"]
                }
            ]
        }
        """;

        IReadOnlyList<string> result = ModelCatalog.ParseGeminiModels(json);

        // "models/" prefix stripped, embedding / imagen excluded, sorted
        Assert.Equal(3, result.Count);
        Assert.Equal("gemini-2.5-flash", result[0]);
        Assert.Equal("gemini-2.5-pro", result[1]);
        Assert.Equal("gemini-flash-experimental", result[2]);
    }

    [Fact]
    public void ParseGeminiModels_NewlyAddedExclusionPatterns_AreFilteredOut()
    {
        // 【A2】実データ30件中12件が整形に使えなかったため追加した除外パターン
        // (transcribe / -image / computer-use / deep-research / antigravity) が効くこと。
        // gemini-flash-lite-latest / gemini-3.5-flash はどのパターンにも該当しないため残ること。
        string json = """
        {
            "models": [
                { "name": "models/gemini-flash-lite-latest", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/gemini-3.5-flash", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/gemini-3.5-transcribe", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/gemini-3.5-flash-image", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/gemini-computer-use-preview", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/gemini-deep-research", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/antigravity-agent", "supportedGenerationMethods": ["generateContent"] }
            ]
        }
        """;

        IReadOnlyList<string> result = ModelCatalog.ParseGeminiModels(json);

        Assert.Equal(2, result.Count);
        Assert.Contains("gemini-flash-lite-latest", result);
        Assert.Contains("gemini-3.5-flash", result);
        Assert.DoesNotContain("gemini-3.5-transcribe", result);
        Assert.DoesNotContain("gemini-3.5-flash-image", result);
        Assert.DoesNotContain("gemini-computer-use-preview", result);
        Assert.DoesNotContain("gemini-deep-research", result);
        Assert.DoesNotContain("antigravity-agent", result);
    }

    [Fact]
    public void ParseGeminiModels_MissingSupportedGenerationMethods_IsExcluded()
    {
        // 【D4】supportedGenerationMethods フィールド自体が無いモデルは
        // (generateContent 対応と確認できないため) 除外されること。
        string json = """
        {
            "models": [
                { "name": "models/gemini-2.5-flash", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/gemini-no-methods-field" }
            ]
        }
        """;

        IReadOnlyList<string> result = ModelCatalog.ParseGeminiModels(json);

        Assert.Single(result);
        Assert.Equal("gemini-2.5-flash", result[0]);
    }

    [Fact]
    public void ParseGeminiModels_InvalidJson_ThrowsJsonException()
    {
        // JsonDocument.Parse は実際には派生型の JsonReaderException を投げるため、
        // (基底型と派生型の両方を許容する) ThrowsAny を使う。呼び出し側の
        // Ui/SettingsWindow.SummarizeFetchError の `JsonException => ...` という型パターンは
        // C# の型パターンマッチにより派生型 (JsonReaderException) にも一致するため、
        // 本番コードの動作には影響しない。
        Assert.ThrowsAny<JsonException>(() => ModelCatalog.ParseGeminiModels("this is not json"));
    }

    [Fact]
    public void ParseGroqWhisperModels_FiltersWhisperOnlyAndSorts()
    {
        string json = """
        {
            "data": [
                { "id": "llama-3.3-70b-versatile" },
                { "id": "whisper-large-v3-turbo" },
                { "id": "whisper-large-v3" },
                { "id": "mixtral-8x7b-32768" }
            ]
        }
        """;

        IReadOnlyList<string> result = ModelCatalog.ParseGroqWhisperModels(json);

        Assert.Equal(2, result.Count);
        Assert.Equal("whisper-large-v3", result[0]);
        Assert.Equal("whisper-large-v3-turbo", result[1]);
    }

    [Fact]
    public void ParseGroqWhisperModels_InvalidJson_ThrowsJsonException()
    {
        Assert.ThrowsAny<JsonException>(() => ModelCatalog.ParseGroqWhisperModels("{ not json"));
    }

    [Fact]
    public void ParseNvidiaModels_ReturnsSortedIds()
    {
        string json = """
        {
            "data": [
                { "id": "nvidia/nemotron-3.5-lightning-30b-a3b" },
                { "id": "meta/llama-3.3-70b-instruct" },
                { "id": "deepseek-ai/deepseek-v4-flash-0731" }
            ]
        }
        """;

        IReadOnlyList<string> result = ModelCatalog.ParseNvidiaModels(json);

        Assert.Equal(3, result.Count);
        Assert.Equal("deepseek-ai/deepseek-v4-flash-0731", result[0]);
        Assert.Equal("meta/llama-3.3-70b-instruct", result[1]);
        Assert.Equal("nvidia/nemotron-3.5-lightning-30b-a3b", result[2]);
    }

    [Fact]
    public void ParseNvidiaModels_InvalidJson_ThrowsJsonException()
    {
        Assert.ThrowsAny<JsonException>(() => ModelCatalog.ParseNvidiaModels("[[["));
    }

    [Fact]
    public void ParseNvidiaModels_ExcludesModelsThatBreakRefinement_AndKeepsVerifiedSurvivors()
    {
        // 【A3】実データ80件中約25件が整形に使えなかったため新設した除外パターンが効くこと。
        // 実際に正常に整形した4件 (nvidia/nemotron-3.5-lightning-30b-a3b,
        // nvidia/ising-calibration-1.5-31b, google/diffusiongemma-26b-a4b-it,
        // meta/muse-glimmer-30b) は絶対に除外されず残ること。
        string json = """
        {
            "data": [
                { "id": "nvidia/nemotron-3.5-lightning-30b-a3b" },
                { "id": "nvidia/ising-calibration-1.5-31b" },
                { "id": "google/diffusiongemma-26b-a4b-it" },
                { "id": "meta/muse-glimmer-30b" },
                { "id": "nvidia/nemotron-3.5-content-safety" },
                { "id": "nvidia/riva-translate-4b-instruct-v2" },
                { "id": "nvidia/nemotron-3-embed-1b" },
                { "id": "nvidia/nemotron-4-340b-reward" },
                { "id": "nvidia/nemotron-parse" },
                { "id": "meta/llama-guard-4-12b" },
                { "id": "adept/fuyu-8b" },
                { "id": "microsoft/kosmos-2" },
                { "id": "nvidia/vila" },
                { "id": "nvidia/neva-22b" },
                { "id": "nvidia/nvclip" },
                { "id": "google/deplot" },
                { "id": "nvidia/ai-synthetic-video-detector" }
            ]
        }
        """;

        IReadOnlyList<string> result = ModelCatalog.ParseNvidiaModels(json);

        // 生存確認 (実際に正常に整形した4件)
        Assert.Contains("nvidia/nemotron-3.5-lightning-30b-a3b", result);
        Assert.Contains("nvidia/ising-calibration-1.5-31b", result);
        Assert.Contains("google/diffusiongemma-26b-a4b-it", result);
        Assert.Contains("meta/muse-glimmer-30b", result);

        // 除外確認 (実際に整形が壊れた・使えなかった13件)
        Assert.DoesNotContain("nvidia/nemotron-3.5-content-safety", result);
        Assert.DoesNotContain("nvidia/riva-translate-4b-instruct-v2", result);
        Assert.DoesNotContain("nvidia/nemotron-3-embed-1b", result);
        Assert.DoesNotContain("nvidia/nemotron-4-340b-reward", result);
        Assert.DoesNotContain("nvidia/nemotron-parse", result);
        Assert.DoesNotContain("meta/llama-guard-4-12b", result);
        Assert.DoesNotContain("adept/fuyu-8b", result);
        Assert.DoesNotContain("microsoft/kosmos-2", result);
        Assert.DoesNotContain("nvidia/vila", result);
        Assert.DoesNotContain("nvidia/neva-22b", result);
        Assert.DoesNotContain("nvidia/nvclip", result);
        Assert.DoesNotContain("google/deplot", result);
        Assert.DoesNotContain("nvidia/ai-synthetic-video-detector", result);

        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void ParseNvidiaModels_ShortPatternsRequireSlash_DoesNotFalsePositiveMatchUnrelatedIds()
    {
        // "vila" / "neva" は短く他のモデル名に部分一致しやすいため、スラッシュ付き ("/vila" /
        // "/neva") で照合する。素の "vila" / "neva" で照合していたら誤って除外されていたはずの
        // 例 ("avila" は "vila" を、"geneva" は "neva" をそれぞれ部分文字列として含むが、
        // どちらも直前が "/" ではない) が、除外されずに残ることを確認する。
        string json = """
        {
            "data": [
                { "id": "meta/avila-7b" },
                { "id": "acme/geneva-13b" }
            ]
        }
        """;

        IReadOnlyList<string> result = ModelCatalog.ParseNvidiaModels(json);

        Assert.Equal(2, result.Count);
        Assert.Contains("meta/avila-7b", result);
        Assert.Contains("acme/geneva-13b", result);
    }

    [Fact]
    public void BuildGeminiListModelsRequest_PutsApiKeyInHeaderNotInUrl()
    {
        // 【A1】Gemini ListModels は URL クエリではなく x-goog-api-key ヘッダでキーを送ること。
        // RequestUri にキーが一切含まれないこと・ヘッダにキーが入っていることの両方を固定する。
        const string apiKey = "secret-test-key-12345";

        using HttpRequestMessage request = ModelCatalog.BuildGeminiListModelsRequest(apiKey);

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.DoesNotContain(apiKey, request.RequestUri!.ToString());
        Assert.DoesNotContain("key=", request.RequestUri!.ToString());

        Assert.True(request.Headers.TryGetValues("x-goog-api-key", out var headerValues));
        Assert.Equal(apiKey, headerValues!.Single());
    }

    [Fact]
    public void BuildGeminiListModelsRequest_UrlPointsAtListModelsEndpointWithPageSize()
    {
        using HttpRequestMessage request = ModelCatalog.BuildGeminiListModelsRequest("any-key");

        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000",
            request.RequestUri!.ToString());
    }
}
