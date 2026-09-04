using VoiceIn.Ai;
using Xunit;

namespace VoiceIn.Tests.Ai;

/// <summary>
/// AiProviderFactory.CreateProvider のプロバイダ名解決ロジックのテスト。
///
/// 注意: providerName に null を渡す（または引数省略する）オーバーロードは
/// SettingsManager.Instance にフォールバックし、その静的コンストラクタが実ユーザーの
/// %APPDATA%\VoiceIn ディレクトリを作成する副作用を持つ。実ユーザー環境を汚さないため、
/// このテストでは常に非 null の providerName を明示的に渡し、その経路には一切触れない。
/// </summary>
public class AiProviderFactoryTests
{
    [Fact]
    public void CreateProvider_Groq_ReturnsGroqProvider()
    {
        IAiProvider provider = AiProviderFactory.CreateProvider("groq");

        Assert.IsType<GroqProvider>(provider);
        Assert.Equal("groq", provider.ProviderName);
    }

    [Theory]
    [InlineData("GROQ")]
    [InlineData("Groq")]
    [InlineData("gRoQ")]
    public void CreateProvider_GroqNameIsCaseInsensitive(string providerName)
    {
        IAiProvider provider = AiProviderFactory.CreateProvider(providerName);

        Assert.IsType<GroqProvider>(provider);
    }

    [Fact]
    public void CreateProvider_Gemini_ReturnsGeminiProvider()
    {
        IAiProvider provider = AiProviderFactory.CreateProvider("gemini");

        Assert.IsType<GeminiProvider>(provider);
        Assert.Equal("gemini", provider.ProviderName);
    }

    [Theory]
    [InlineData("GEMINI")]
    [InlineData("Gemini")]
    public void CreateProvider_GeminiNameIsCaseInsensitive(string providerName)
    {
        IAiProvider provider = AiProviderFactory.CreateProvider(providerName);

        Assert.IsType<GeminiProvider>(provider);
    }

    [Theory]
    [InlineData("unknown-provider")]
    [InlineData("chatgpt")]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateProvider_UnknownOrEmptyName_FallsBackToGeminiProvider(string providerName)
    {
        // switch 式の既定分岐 (_ => new GeminiProvider()) により、
        // 未知の名前は例外を投げず Gemini にフォールバックする（実装上の既定動作）。
        IAiProvider provider = AiProviderFactory.CreateProvider(providerName);

        Assert.IsType<GeminiProvider>(provider);
    }
}
