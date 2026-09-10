using VoiceIn.Ai;
using Xunit;

namespace VoiceIn.Tests.Ai;

/// <summary>
/// RefineProviderFactory.CreateProvider の整形バックエンド名解決ロジックのテスト。
/// Ai/AiProviderFactoryTests.cs と同じ方針を踏襲する。
///
/// 注意: providerName に null を渡す（または引数省略する）オーバーロードは
/// SettingsManager.Instance にフォールバックし、その静的コンストラクタが実ユーザーの
/// %APPDATA%\VoiceIn ディレクトリを作成する副作用を持つ。実ユーザー環境を汚さないため、
/// このテストでは常に非 null の providerName を明示的に渡し、その経路には一切触れない。
/// </summary>
public class RefineProviderFactoryTests
{
    [Fact]
    public void CreateProvider_Gemini_ReturnsGeminiRefineProvider()
    {
        IRefineProvider provider = RefineProviderFactory.CreateProvider("gemini");

        Assert.IsType<GeminiRefineProvider>(provider);
        Assert.Equal("gemini", provider.ProviderName);
    }

    [Theory]
    [InlineData("GEMINI")]
    [InlineData("Gemini")]
    [InlineData("gEmInI")]
    public void CreateProvider_GeminiNameIsCaseInsensitive(string providerName)
    {
        IRefineProvider provider = RefineProviderFactory.CreateProvider(providerName);

        Assert.IsType<GeminiRefineProvider>(provider);
    }

    [Fact]
    public void CreateProvider_Nvidia_ReturnsNvidiaRefineProvider()
    {
        IRefineProvider provider = RefineProviderFactory.CreateProvider("nvidia");

        Assert.IsType<NvidiaRefineProvider>(provider);
        Assert.Equal("nvidia", provider.ProviderName);
    }

    [Theory]
    [InlineData("NVIDIA")]
    [InlineData("Nvidia")]
    [InlineData("nViDiA")]
    public void CreateProvider_NvidiaNameIsCaseInsensitive(string providerName)
    {
        IRefineProvider provider = RefineProviderFactory.CreateProvider(providerName);

        Assert.IsType<NvidiaRefineProvider>(provider);
    }

    [Theory]
    [InlineData("unknown-provider")]
    [InlineData("chatgpt")]
    [InlineData("groq")] // Groq はもう整形バックエンドの選択肢ではない (文字起こし専用になったため)
    [InlineData("")]
    [InlineData("   ")]
    public void CreateProvider_UnknownOrEmptyName_FallsBackToGeminiRefineProvider(string providerName)
    {
        // switch 式の既定分岐 (_ => new GeminiRefineProvider()) により、
        // 未知の名前は例外を投げず Gemini にフォールバックする（実装上の既定動作）。
        IRefineProvider provider = RefineProviderFactory.CreateProvider(providerName);

        Assert.IsType<GeminiRefineProvider>(provider);
    }
}
