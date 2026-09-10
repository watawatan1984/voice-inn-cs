using System;
using VoiceIn.Core;
using Xunit;

namespace VoiceIn.Tests.Core;

/// <summary>
/// Core/WindowDetector.DetectCategory による「検出済みアプリ履歴」(AppSettings.DetectedApps)
/// への記録と、user_category (ユーザーによる上書き) の優先順位のテスト。
///
/// 【重要】SettingsManager.Instance には絶対に触れない (触れた時点で %AppData%\VoiceIn が
/// 作成されてしまう)。DetectCategory は新規アプリ検出時に SettingsManager.Instance.Save() を
/// 呼びうる設計になっているが、WindowDetector.SaveSettingsCallback (internal, テスト専用
/// フック) を差し替えることで、実ファイルへ一切書き込まずにこの経路を検証する。
/// 各テストは using var で SaveOverrideScope を確保し、テスト終了時に必ずフックを null へ
/// 戻す (このアセンブリはテストの並列実行を無効化している (AssemblyInfo.cs) ため、
/// 他のテストと同時に競合することはないが、後続のテストへ影響を残さないための後始末)。
/// </summary>
public class WindowDetectorDetectedAppsTests
{
    /// <summary>
    /// WindowDetector.SaveSettingsCallback を一時的に差し替え、呼び出し回数を記録し、
    /// Dispose で必ず null に戻すスコープ。
    /// </summary>
    private sealed class SaveOverrideScope : IDisposable
    {
        public int CallCount { get; private set; }

        public SaveOverrideScope()
        {
            WindowDetector.SaveSettingsCallback = _ => CallCount++;
        }

        public void Dispose()
        {
            WindowDetector.SaveSettingsCallback = null;
        }
    }

    [Fact]
    public void DetectCategory_NewApp_IsRecordedWithTitleSampleAndAutoCategory()
    {
        using var scope = new SaveOverrideScope();
        var settings = new AppSettings();
        var info = new WindowInfo { ProcessName = "Code", Title = "main.cs - MyProject - Visual Studio Code" };

        string result = WindowDetector.DetectCategory(info, settings);

        Assert.Equal("DEV", result);
        Assert.True(settings.DetectedApps.ContainsKey("Code"));
        var entry = settings.DetectedApps["Code"];
        Assert.Equal("main.cs - MyProject - Visual Studio Code", entry.TitleSample);
        Assert.Equal("DEV", entry.AutoCategory);
        Assert.Null(entry.UserCategory);
    }

    [Fact]
    public void DetectCategory_NewApp_TriggersSaveExactlyOnce()
    {
        using var scope = new SaveOverrideScope();
        var settings = new AppSettings();
        var info = new WindowInfo { ProcessName = "chrome", Title = "Google - Google Chrome" };

        WindowDetector.DetectCategory(info, settings);

        Assert.Equal(1, scope.CallCount);
    }

    [Fact]
    public void DetectCategory_KnownApp_DoesNotTriggerSave()
    {
        using var scope = new SaveOverrideScope();
        var settings = new AppSettings();
        var info = new WindowInfo { ProcessName = "chrome", Title = "Google - Google Chrome" };

        // 1回目: 新規アプリとして記録・保存される
        WindowDetector.DetectCategory(info, settings);
        Assert.Equal(1, scope.CallCount);

        // 2〜10回目: 既知のアプリなので保存は一切発生しない
        // (「発話のたびにファイル書き込みが走るのは論外」という性能要件の直接的な確認)
        for (int i = 0; i < 9; i++)
        {
            WindowDetector.DetectCategory(info, settings);
        }

        Assert.Equal(1, scope.CallCount);
    }

    [Fact]
    public void DetectCategory_KnownApp_DoesNotOverwriteExistingEntry()
    {
        using var scope = new SaveOverrideScope();
        var settings = new AppSettings();
        settings.DetectedApps["chrome"] = new DetectedAppInfo
        {
            TitleSample = "最初に検出したタイトル",
            AutoCategory = "STD",
            UserCategory = null
        };

        // 同じアプリ名 (chrome) だが、タイトルが変わった状態で再度検出させる。
        var info = new WindowInfo { ProcessName = "chrome", Title = "別のタブ - Google Chrome" };
        WindowDetector.DetectCategory(info, settings);

        // 既存エントリの title_sample / auto_category は上書きされていないこと。
        Assert.Equal("最初に検出したタイトル", settings.DetectedApps["chrome"].TitleSample);
        Assert.Equal("STD", settings.DetectedApps["chrome"].AutoCategory);
        Assert.Single(settings.DetectedApps);
        // 既知のアプリなので保存も発生しない。
        Assert.Equal(0, scope.CallCount);
    }

    [Fact]
    public void DetectCategory_UserCategorySet_OverridesKeywordAutoDetection()
    {
        using var scope = new SaveOverrideScope();
        var settings = new AppSettings();
        // "slack" は既定のキーワードでは BIZ に自動判定される。
        settings.DetectedApps["slack"] = new DetectedAppInfo
        {
            TitleSample = "general | MyWorkspace - Slack",
            AutoCategory = "BIZ",
            UserCategory = "DEV" // ユーザーが明示的に DEV へ上書き
        };

        var info = new WindowInfo { ProcessName = "slack", Title = "general | MyWorkspace - Slack" };
        string result = WindowDetector.DetectCategory(info, settings);

        // キーワードによる自動判定 (BIZ) より user_category (DEV) が優先される。
        Assert.Equal("DEV", result);
        // 既知のアプリなので既存エントリは変更されず、保存も発生しない。
        Assert.Equal("BIZ", settings.DetectedApps["slack"].AutoCategory);
        Assert.Equal(0, scope.CallCount);
    }

    [Fact]
    public void DetectCategory_UserCategoryNull_FallsBackToKeywordAutoDetection()
    {
        using var scope = new SaveOverrideScope();
        var settings = new AppSettings();
        settings.DetectedApps["slack"] = new DetectedAppInfo
        {
            TitleSample = "general | MyWorkspace - Slack",
            AutoCategory = "BIZ",
            UserCategory = null
        };

        var info = new WindowInfo { ProcessName = "slack", Title = "general | MyWorkspace - Slack" };
        string result = WindowDetector.DetectCategory(info, settings);

        Assert.Equal("BIZ", result);
    }

    [Fact]
    public void DetectCategory_ContextAwareDisabled_DoesNotRecordAnything()
    {
        using var scope = new SaveOverrideScope();
        var settings = new AppSettings { ContextAwareEnabled = false };
        var info = new WindowInfo { ProcessName = "Code", Title = "main.cs - Visual Studio Code" };

        string result = WindowDetector.DetectCategory(info, settings);

        Assert.Equal("STD", result);
        Assert.Empty(settings.DetectedApps);
        Assert.Equal(0, scope.CallCount);
    }

    [Fact]
    public void DetectCategory_EmptyProcessName_DoesNotRecordOrThrow()
    {
        using var scope = new SaveOverrideScope();
        var settings = new AppSettings();
        var info = new WindowInfo { ProcessName = string.Empty, Title = "some title with no process name" };

        var exception = Record.Exception(() => WindowDetector.DetectCategory(info, settings));

        Assert.Null(exception);
        Assert.Empty(settings.DetectedApps);
        Assert.Equal(0, scope.CallCount);
    }

    [Fact]
    public void DetectCategory_WithoutSaveOverride_DoesNotTouchRealSettingsManager()
    {
        // SaveSettingsCallback を一切設定しない、既定の挙動を確認する。
        // このテストプロセス内で SettingsManager.Instance に触れた形跡 (=作成済み扱いになって
        // いること) が無い限り、既定の保存フックは安全側に倒れて何も書き込まない。
        // (実ファイルの有無そのものはこのテストからは検証できない -- それを検証しようとする
        // こと自体が %AppData%\VoiceIn に触れる操作になってしまうため。実ファイルが作られて
        // いないことは、テストプロジェクト全体を通した検証手順 (dotnet test 実行後に
        // %AppData%\VoiceIn の不存在を確認する) で担保する。)
        WindowDetector.SaveSettingsCallback = null;

        var settings = new AppSettings();
        var info = new WindowInfo { ProcessName = "totally_new_app_xyz", Title = "some window" };

        var exception = Record.Exception(() => WindowDetector.DetectCategory(info, settings));

        Assert.Null(exception);
        Assert.True(settings.DetectedApps.ContainsKey("totally_new_app_xyz"));
    }
}
