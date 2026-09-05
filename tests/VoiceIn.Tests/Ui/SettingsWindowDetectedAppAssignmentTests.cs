using System.Collections.Generic;
using System.Linq;
using VoiceIn.Core;
using VoiceIn.Ui;
using Xunit;

namespace VoiceIn.Tests.Ui;

/// <summary>
/// Ui/SettingsWindow.ApplyDetectedAppCategoryAssignment (「検出済みアプリ履歴」からカテゴリを
/// 割り当てる際の中核ロジック) のテスト。
///
/// Ui/SettingsWindow 自体は WPF の Window であり、テストホスト上でインスタンス化できない
/// (グローバルフックやディスパッチャを前提とするため、AppSettingsCategoryTests.cs 等の既存の
/// コメント参照)。ApplyDetectedAppCategoryAssignment は internal static であり、
/// SettingsManager や WPF に一切依存しない純粋な処理として実装されているため、
/// インスタンス化せずに静的メソッドとして直接呼び出して検証できる。
///
/// SettingsManager.Instance / HistoryManager.Instance / Logger には一切触れない。
/// </summary>
public class SettingsWindowDetectedAppAssignmentTests
{
    [Fact]
    public void ApplyDetectedAppCategoryAssignment_SetsUserCategory_PreservesOtherFields()
    {
        var settings = new AppSettings();
        settings.DetectedApps["notion"] = new DetectedAppInfo
        {
            TitleSample = "議事録 - Notion",
            AutoCategory = "STD",
            UserCategory = null
        };

        SettingsWindow.ApplyDetectedAppCategoryAssignment(settings, "notion", "DOC");

        var entry = settings.DetectedApps["notion"];
        Assert.Equal("DOC", entry.UserCategory);
        // title_sample / auto_category (割り当て対象ではない項目) はそのまま保たれること。
        Assert.Equal("議事録 - Notion", entry.TitleSample);
        Assert.Equal("STD", entry.AutoCategory);
    }

    [Fact]
    public void ApplyDetectedAppCategoryAssignment_AddsLowercasedAppNameToTargetCategoryKeywords()
    {
        var settings = new AppSettings();
        settings.DetectedApps["Notion"] = new DetectedAppInfo { TitleSample = "t", AutoCategory = "STD", UserCategory = null };

        SettingsWindow.ApplyDetectedAppCategoryAssignment(settings, "Notion", "DOC");

        Assert.Contains("notion", settings.AppCategories["DOC"]);
        // 元の大文字混じりの文字列そのままでは追加されていないこと (移植元と同じく小文字化する契約)。
        Assert.DoesNotContain("Notion", settings.AppCategories["DOC"]);
    }

    [Fact]
    public void ApplyDetectedAppCategoryAssignment_CaseInsensitiveDuplicate_DoesNotAddTwice()
    {
        var settings = new AppSettings();
        settings.AppCategories["DEV"] = new List<string> { "MyIDE" };
        settings.DetectedApps["MYIDE"] = new DetectedAppInfo { TitleSample = "t", AutoCategory = "STD", UserCategory = null };

        SettingsWindow.ApplyDetectedAppCategoryAssignment(settings, "MYIDE", "DEV");

        // "MyIDE" (既存) と "myide" (小文字化した新規追加分) が大文字小文字を無視して
        // 重複しているとみなされ、2件にならず1件のみであること。
        int matchCount = settings.AppCategories["DEV"]
            .Count(k => string.Equals(k, "myide", System.StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, matchCount);
        Assert.Single(settings.AppCategories["DEV"]);
    }

    [Fact]
    public void ApplyDetectedAppCategoryAssignment_CalledTwiceForSameApp_IsIdempotentForKeywordList()
    {
        var settings = new AppSettings();
        settings.DetectedApps["slack"] = new DetectedAppInfo { TitleSample = "t", AutoCategory = "BIZ", UserCategory = null };

        SettingsWindow.ApplyDetectedAppCategoryAssignment(settings, "slack", "BIZ");
        SettingsWindow.ApplyDetectedAppCategoryAssignment(settings, "slack", "BIZ");

        int matchCount = settings.AppCategories["BIZ"]
            .Count(k => string.Equals(k, "slack", System.StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, matchCount);
    }

    [Fact]
    public void ApplyDetectedAppCategoryAssignment_TargetCategoryDoesNotExistYet_CreatesNewCategoryWithKeyword()
    {
        var settings = new AppSettings();
        settings.DetectedApps["steam"] = new DetectedAppInfo { TitleSample = "t", AutoCategory = "STD", UserCategory = null };

        SettingsWindow.ApplyDetectedAppCategoryAssignment(settings, "steam", "GAME");

        Assert.True(settings.AppCategories.ContainsKey("GAME"));
        Assert.Contains("steam", settings.AppCategories["GAME"]);
        Assert.Equal("GAME", settings.DetectedApps["steam"].UserCategory);
    }

    [Fact]
    public void ApplyDetectedAppCategoryAssignment_AppNameNotYetInDetectedApps_StillAddsKeyword()
    {
        // DetectedApps にまだ存在しないアプリ名に対して呼ばれた場合でも
        // (通常の UI フローでは起きないはずだが、防御的に) キーワード追加自体は行われる。
        var settings = new AppSettings();

        SettingsWindow.ApplyDetectedAppCategoryAssignment(settings, "unknownapp", "DEV");

        Assert.Contains("unknownapp", settings.AppCategories["DEV"]);
        Assert.False(settings.DetectedApps.ContainsKey("unknownapp"));
    }

    [Fact]
    public void ApplyDetectedAppCategoryAssignment_DoesNotAffectOtherCategoriesOrOtherApps()
    {
        var settings = new AppSettings();
        settings.DetectedApps["notion"] = new DetectedAppInfo { TitleSample = "t1", AutoCategory = "STD", UserCategory = null };
        settings.DetectedApps["slack"] = new DetectedAppInfo { TitleSample = "t2", AutoCategory = "BIZ", UserCategory = "BIZ" };
        int devKeywordCountBefore = settings.AppCategories["DEV"].Count;

        SettingsWindow.ApplyDetectedAppCategoryAssignment(settings, "notion", "DOC");

        // 割り当て対象外の "slack" エントリや DEV カテゴリのキーワードは変化しないこと。
        Assert.Equal("BIZ", settings.DetectedApps["slack"].UserCategory);
        Assert.Equal(devKeywordCountBefore, settings.AppCategories["DEV"].Count);
    }
}
