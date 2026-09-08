using System.Collections.Generic;
using VoiceIn.Ai;
using Xunit;

namespace VoiceIn.Tests.Ai;

public class ModelCatalogTests
{
    [Fact]
    public void FallbackModels_AreNotEmptyAndContainExpectedDefaults()
    {
        Assert.NotEmpty(ModelCatalog.GeminiFallbackModels);
        Assert.Contains("gemini-flash-lite-latest", ModelCatalog.GeminiFallbackModels);

        Assert.NotEmpty(ModelCatalog.GroqWhisperFallbackModels);
        Assert.Contains("whisper-large-v3", ModelCatalog.GroqWhisperFallbackModels);

        Assert.NotEmpty(ModelCatalog.NvidiaFallbackModels);
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
}
