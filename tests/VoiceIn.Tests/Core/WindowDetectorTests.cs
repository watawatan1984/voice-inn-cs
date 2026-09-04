using System.Collections.Generic;
using VoiceIn.Core;
using Xunit;

namespace VoiceIn.Tests.Core;

/// <summary>
/// WindowDetector.DetectCategory の純粋ロジック部分のテスト。
/// GetActiveWindow (Win32 呼び出し) はテスト対象外。WindowInfo を直接組み立てて検証する。
/// </summary>
public class WindowDetectorTests
{
    [Fact]
    public void DetectCategory_ContextAwareDisabled_AlwaysReturnsStd()
    {
        var settings = new AppSettings { ContextAwareEnabled = false };
        var info = new WindowInfo { ProcessName = "Code", Title = "Visual Studio Code" };

        string result = WindowDetector.DetectCategory(info, settings);

        Assert.Equal("STD", result);
    }

    [Fact]
    public void DetectCategory_DevProcessName_ReturnsDev()
    {
        var settings = new AppSettings();
        var info = new WindowInfo { ProcessName = "Code", Title = "main.cs - MyProject - Visual Studio Code" };

        string result = WindowDetector.DetectCategory(info, settings);

        Assert.Equal("DEV", result);
    }

    [Fact]
    public void DetectCategory_BizProcessName_ReturnsBiz()
    {
        var settings = new AppSettings();
        var info = new WindowInfo { ProcessName = "slack", Title = "general | MyWorkspace - Slack" };

        string result = WindowDetector.DetectCategory(info, settings);

        Assert.Equal("BIZ", result);
    }

    [Fact]
    public void DetectCategory_DocProcessName_ReturnsDoc()
    {
        var settings = new AppSettings();
        var info = new WindowInfo { ProcessName = "WINWORD", Title = "Document1 - Word" };

        string result = WindowDetector.DetectCategory(info, settings);

        Assert.Equal("DOC", result);
    }

    [Fact]
    public void DetectCategory_UnknownApp_FallsBackToStd()
    {
        var settings = new AppSettings();
        var info = new WindowInfo { ProcessName = "chrome", Title = "Google - Google Chrome" };

        string result = WindowDetector.DetectCategory(info, settings);

        Assert.Equal("STD", result);
    }

    [Fact]
    public void DetectCategory_IsCaseInsensitive()
    {
        var settings = new AppSettings();
        var info = new WindowInfo { ProcessName = "CODE", Title = "SOME FILE - VISUAL STUDIO CODE" };

        string result = WindowDetector.DetectCategory(info, settings);

        Assert.Equal("DEV", result);
    }

    [Fact]
    public void DetectCategory_MatchesOnTitleAlone_WhenProcessNameIsUnrelated()
    {
        var settings = new AppSettings();
        // プロセス名はどのキーワードにも合致しないが、タイトルに "slack" を含む
        var info = new WindowInfo { ProcessName = "electron", Title = "My Team - Slack" };

        string result = WindowDetector.DetectCategory(info, settings);

        Assert.Equal("BIZ", result);
    }

    [Fact]
    public void DetectCategory_CustomAppCategories_AreRespected()
    {
        var settings = new AppSettings
        {
            AppCategories = new Dictionary<string, List<string>>
            {
                ["DEV"] = ["mycustomide"],
                ["BIZ"] = [],
                ["DOC"] = [],
                ["STD"] = []
            }
        };
        var info = new WindowInfo { ProcessName = "MyCustomIde", Title = "untitled" };

        string result = WindowDetector.DetectCategory(info, settings);

        Assert.Equal("DEV", result);
    }

    [Fact]
    public void DetectCategory_EmptyOrWhitespaceKeywordsInList_AreIgnored()
    {
        // 空文字/空白のみのキーワードが Contains("") で常にマッチしてしまわないことのガード確認
        var settings = new AppSettings
        {
            AppCategories = new Dictionary<string, List<string>>
            {
                ["DEV"] = ["", "   ", "realkeyword"],
                ["BIZ"] = [],
                ["DOC"] = [],
                ["STD"] = []
            }
        };
        var info = new WindowInfo { ProcessName = "unrelated", Title = "nothing matches here" };

        string result = WindowDetector.DetectCategory(info, settings);

        Assert.Equal("STD", result);
    }

    [Fact]
    public void DetectCategory_NoAppCategoriesMatch_ReturnsStdEvenWhenStdListIsEmpty()
    {
        var settings = new AppSettings();
        Assert.Empty(settings.AppCategories["STD"]); // 前提確認: STD のキーワードリストは既定で空
        var info = new WindowInfo { ProcessName = "totally_unknown_app_xyz", Title = "nothing" };

        string result = WindowDetector.DetectCategory(info, settings);

        Assert.Equal("STD", result);
    }
}
