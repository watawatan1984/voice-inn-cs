using System.Collections.Generic;
using VoiceIn.Core;
using Xunit;

namespace VoiceIn.Tests.Core;

/// <summary>
/// WindowDetector.DetectCategory のキーワード側 (AppSettings.AppCategories) に
/// 大文字を含むキーワードが設定された場合のテスト。
///
/// DetectCategory は判定対象文字列 (ProcessName + Title) を ToLowerInvariant() した上で、
/// キーワード側も kw.ToLowerInvariant() してから Contains 比較する契約になっている
/// (WindowDetector.cs の DetectCategory 参照)。
///
/// 既存の WindowDetectorTests.DetectCategory_IsCaseInsensitive はウィンドウ側
/// (ProcessName / Title) だけを大文字化しており、既定の AppCategories のキーワードは
/// すべて元から小文字であるため、キーワード側の ToLowerInvariant() を消しても
/// 検出できてしまう (カバレッジの穴)。ユーザーが settings.json の app_categories に
/// "Slack" や "VSCode" のような大文字混じりのキーワードを追加するケースを想定し、
/// キーワード側の小文字化が効いていることを直接検証する。
/// </summary>
public class WindowDetectorKeywordCaseTests
{
    [Fact]
    public void DetectCategory_UppercaseKeywordInSettings_MatchesLowercaseWindowText()
    {
        var settings = new AppSettings
        {
            AppCategories = new Dictionary<string, List<string>>
            {
                ["DEV"] = [],
                ["BIZ"] = ["Slack"],
                ["DOC"] = [],
                ["STD"] = []
            }
        };
        // 実際の Win32 プロセス名は小文字であることが多い (例: slack.exe)。
        var info = new WindowInfo { ProcessName = "slack", Title = "general | myworkspace" };

        string result = WindowDetector.DetectCategory(info, settings);

        Assert.Equal("BIZ", result);
    }

    [Fact]
    public void DetectCategory_UppercaseKeywordInSettings_MatchesUppercaseWindowText()
    {
        var settings = new AppSettings
        {
            AppCategories = new Dictionary<string, List<string>>
            {
                ["DEV"] = ["VSCode"],
                ["BIZ"] = [],
                ["DOC"] = [],
                ["STD"] = []
            }
        };
        var info = new WindowInfo { ProcessName = "CODE", Title = "UNTITLED - VSCODE" };

        string result = WindowDetector.DetectCategory(info, settings);

        Assert.Equal("DEV", result);
    }
}
