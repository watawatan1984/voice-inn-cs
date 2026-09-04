using System;
using VoiceIn.Core;

namespace VoiceIn.Ai;

public static class AiProviderFactory
{
    public static IAiProvider CreateProvider(string? providerName = null)
    {
        string name = providerName?.ToLowerInvariant() 
                      ?? SettingsManager.Instance.CurrentProvider.ToLowerInvariant();

        return name switch
        {
            "groq" => new GroqProvider(),
            "local" => new LocalProvider(),
            _ => new GeminiProvider()
        };
    }
}
