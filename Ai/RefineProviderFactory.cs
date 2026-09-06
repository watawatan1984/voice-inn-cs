using VoiceIn.Core;

namespace VoiceIn.Ai;

/// <summary>
/// 整形バックエンド (IRefineProvider) の解決ロジック。Ai/AiProviderFactory.cs (文字起こし
/// プロバイダの解決) と同じ書き方 (大文字小文字を区別しない switch 式、未知の値は既定へ
/// フォールバック) に揃えている。
///
/// 既定 (providerName 省略時) は Core/Settings.cs (AppSettings.RefineProvider) の設定値を
/// 使う。AiProviderFactory.CreateProvider が SettingsManager.Instance.CurrentProvider
/// (環境変数 AI_PROVIDER 経由) を見るのに対し、整形バックエンドの選択は settings.json に
/// 保存される設定値である点が異なる (要求仕様どおり、整形バックエンドは Core/Settings.cs 側の
/// 設定として追加されているため)。
/// </summary>
public static class RefineProviderFactory
{
    public static IRefineProvider CreateProvider(string? providerName = null)
    {
        string name = providerName?.ToLowerInvariant()
                      ?? SettingsManager.Instance.Settings.RefineProvider.ToLowerInvariant();

        return name switch
        {
            "nvidia" => new NvidiaRefineProvider(),
            // 未知の値・空文字列も含め、既定は Gemini (実測でレイテンシ・専門用語変換の
            // 精度ともに良好だったため。Core/Settings.cs の AppSettings.RefineProvider 参照)。
            _ => new GeminiRefineProvider()
        };
    }
}
