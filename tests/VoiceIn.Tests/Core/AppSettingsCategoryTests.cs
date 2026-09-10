using System.Collections.Generic;
using System.Text.Json;
using VoiceIn.Core;
using Xunit;

namespace VoiceIn.Tests.Core;

/// <summary>
/// 設定画面「カテゴリ」タブが編集対象とする AppSettings.AppCategories /
/// AppSettings.CategoryPrompts の JSON シリアライズ/デシリアライズ契約のテスト。
///
/// Ui/SettingsWindow は WPF の Window であり、テストホスト上でインスタンス化できない
/// (グローバルフックやディスパッチャを前提とするため) ため、ここでは「カテゴリ」タブが
/// 最終的に書き戻す先である AppSettings 自体の往復保存性のみを検証する。
/// AppSettingsTests.cs と同じ方針 (SettingsManager が使う
/// JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true } を模す) に倣う。
///
/// SettingsManager.Instance / HistoryManager.Instance / Logger には一切触れない
/// (これらはシングルトン初回アクセス時に %AppData%\VoiceIn を作成してしまうため)。
/// </summary>
public class AppSettingsCategoryTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void RoundTrip_ModifiedCategoryKeywordsAndPrompts_PreservesValues()
    {
        var original = new AppSettings();

        // キーワード一覧を丸ごと差し替える (設定画面の保存処理も Clear せず新しい List を
        // 割り当てる方式のため、それと同じ操作を再現する)。
        original.AppCategories["DEV"] = new List<string> { "neovim", "warp", "自作エディタ" };
        original.AppCategories["BIZ"].Add("新しいツール");

        original.CategoryPrompts["DEV"] = "カスタムプロンプト:\n・簡潔に\n・敬語不要";
        original.CategoryPrompts["STD"] = "素のプロンプト";

        string json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal(new[] { "neovim", "warp", "自作エディタ" }, restored!.AppCategories["DEV"]);
        Assert.Contains("新しいツール", restored.AppCategories["BIZ"]);
        Assert.Equal("カスタムプロンプト:\n・簡潔に\n・敬語不要", restored.CategoryPrompts["DEV"]);
        Assert.Equal("素のプロンプト", restored.CategoryPrompts["STD"]);
    }

    [Fact]
    public void RoundTrip_AddedNewCategoryKey_PreservesItAlongsideDefaults()
    {
        var original = new AppSettings();

        // 設定画面には「新規カテゴリ追加」UI はないが、AppSettings 自体は Dictionary である
        // 以上、settings.json を直接編集する等で任意のキーが増える可能性がある。
        // そのようなデータでも壊れずに往復できることを保証する。
        original.AppCategories["GAME"] = new List<string> { "steam", "epic games", "ゲーム" };
        original.CategoryPrompts["GAME"] = "あなたはゲーム実況者です。";

        string json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal(5, restored!.AppCategories.Count);
        Assert.Equal(new[] { "steam", "epic games", "ゲーム" }, restored.AppCategories["GAME"]);
        Assert.Equal("あなたはゲーム実況者です。", restored.CategoryPrompts["GAME"]);

        // 既定の4カテゴリも無事に残っていること
        Assert.Contains("DEV", restored.AppCategories.Keys);
        Assert.Contains("BIZ", restored.AppCategories.Keys);
        Assert.Contains("DOC", restored.AppCategories.Keys);
        Assert.Contains("STD", restored.AppCategories.Keys);
    }

    [Fact]
    public void RoundTrip_RemovedCategoryKey_DoesNotReappearAfterRoundTrip()
    {
        var original = new AppSettings();

        Assert.True(original.AppCategories.Remove("STD"));
        Assert.True(original.CategoryPrompts.Remove("STD"));

        string json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(restored);
        // AppSettings / JsonSerializer 自身は既定値を補完しない (それは SettingsManager.Load の
        // 責務であり、SettingsManager.Instance に触れられない本テストの対象外) ため、
        // 削除したキーは往復後も存在しないままであるべき。
        Assert.DoesNotContain("STD", restored!.AppCategories.Keys);
        Assert.DoesNotContain("STD", restored.CategoryPrompts.Keys);
        Assert.Equal(3, restored.AppCategories.Count);
        Assert.Equal(3, restored.CategoryPrompts.Count);

        // 他のカテゴリには影響しないこと
        Assert.Contains("DEV", restored.AppCategories.Keys);
        Assert.Contains("BIZ", restored.AppCategories.Keys);
        Assert.Contains("DOC", restored.AppCategories.Keys);
    }

    [Fact]
    public void RoundTrip_AddedAndRemovedCategoriesSimultaneously_KeepsRemainingCategoriesIntact()
    {
        var original = new AppSettings();

        original.AppCategories.Remove("DOC");
        original.CategoryPrompts.Remove("DOC");

        original.AppCategories["GAME"] = new List<string> { "steam" };
        original.CategoryPrompts["GAME"] = "ゲーム用プロンプト";

        original.AppCategories["BIZ"].Add("追加キーワード");

        string json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal(
            new HashSet<string> { "DEV", "BIZ", "STD", "GAME" },
            new HashSet<string>(restored!.AppCategories.Keys));
        Assert.DoesNotContain("DOC", restored.AppCategories.Keys);
        Assert.DoesNotContain("DOC", restored.CategoryPrompts.Keys);
        Assert.Contains("追加キーワード", restored.AppCategories["BIZ"]);
        Assert.Equal(new[] { "steam" }, restored.AppCategories["GAME"]);
        Assert.Equal("ゲーム用プロンプト", restored.CategoryPrompts["GAME"]);

        // 変更していない DEV のキーワードは既定値のまま保持されていること
        Assert.Contains("code", restored.AppCategories["DEV"]);
    }

    [Fact]
    public void RoundTrip_EmptyKeywordList_PreservesEmptyNotNull()
    {
        var original = new AppSettings();
        original.AppCategories["DEV"] = new List<string>();

        string json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(restored);
        Assert.NotNull(restored!.AppCategories["DEV"]);
        Assert.Empty(restored.AppCategories["DEV"]);
    }
}
