using System.Threading.Tasks;

namespace VoiceIn.Ai;

public interface IAiProvider
{
    string ProviderName { get; }
    Task<string> TranscribeAsync(string audioFilePath, string prompt);
}
