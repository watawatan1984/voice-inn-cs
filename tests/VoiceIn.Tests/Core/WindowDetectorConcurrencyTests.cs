using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VoiceIn.Core;
using Xunit;

namespace VoiceIn.Tests.Core;

/// <summary>
/// WindowDetector.DetectCategory の並行アクセス安全性のテスト。
///
/// settings.AppCategories は、設定画面での保存処理 (Ui/SettingsWindow.OnSaveAndApply) から
/// バックグラウンドスレッドで参照ごと差し替えられる可能性がある。DetectCategory 側は
/// SettingsLock.Gate の下でスナップショットを取ってから列挙するようになった (Core/WindowDetector.cs)。
/// ここでは、
/// ・DetectCategory を複数スレッドから同時に呼んでも例外が出ないこと
/// ・その最中に AppCategories の参照を差し替えても例外が出ないこと
/// ・判定結果自体はロック導入前と変わらないこと
/// を検証する。DetectCategory のシグネチャ・戻り値の意味自体は変えていないため、
/// 既存の WindowDetectorTests / WindowDetectorKeywordCaseTests は変更なしでそのまま通る
/// (このテストファイルでは重複しない観点のみを追加する)。
/// </summary>
public class WindowDetectorConcurrencyTests
{
    [Fact]
    public async Task DetectCategory_CalledFromManyThreadsConcurrently_DoesNotThrow_AndReturnsExpectedCategory()
    {
        // AppCategories 自体は変更しない (純粋な同時読み取りのみ) パターン。
        var settings = new AppSettings();
        var info = new WindowInfo { ProcessName = "Code", Title = "main.cs - MyProject - Visual Studio Code" };

        const int taskCount = 16;
        const int iterationsPerTask = 200;

        var tasks = new Task<string[]>[taskCount];
        for (int t = 0; t < taskCount; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                var localResults = new string[iterationsPerTask];
                for (int i = 0; i < iterationsPerTask; i++)
                {
                    localResults[i] = WindowDetector.DetectCategory(info, settings);
                }
                return localResults;
            });
        }

        // いずれかのタスクが例外を投げていれば、await の時点でそのまま送出される
        // (= このテストは例外発生時に自動的に失敗する)。
        string[][] allResults = await Task.WhenAll(tasks);

        foreach (var localResults in allResults)
        {
            foreach (var result in localResults)
            {
                Assert.Equal("DEV", result);
            }
        }
    }

    [Fact]
    public async Task DetectCategory_WhileAppCategoriesIsReplacedConcurrently_DoesNotThrow_AndReturnsValidCategory()
    {
        var settings = new AppSettings();
        var info = new WindowInfo { ProcessName = "myapp", Title = "some window" };

        // "myapp" は bizCategories では BIZ、docCategories では DOC にのみ含まれる。
        // 書き込み側 (Ui/SettingsWindow.OnSaveAndApply) と同じく、ロックの外で組み立て済みの
        // Dictionary/List をロック内では参照差し替えのみに使う方式で、書き込みスレッドが
        // settings.AppCategories を反復して差し替え続ける。
        var bizCategories = new Dictionary<string, List<string>>
        {
            ["DEV"] = [],
            ["BIZ"] = ["myapp"],
            ["DOC"] = [],
            ["STD"] = []
        };
        var docCategories = new Dictionary<string, List<string>>
        {
            ["DEV"] = [],
            ["BIZ"] = [],
            ["DOC"] = ["myapp"],
            ["STD"] = []
        };

        // settings は既定コンストラクタの時点では "myapp" を含まない既定の AppCategories
        // (DEV/BIZ/DOC/STD いずれも既定キーワードのみ) を持っている。書き込みタスク
        // (Task.Run) の初回スイッチが実際に実行されるまでの間、読み取りタスク側が
        // (差し替えられる前の) この既定値を読んでしまうと "STD" が返る一瞬が生じ、
        // 「結果は必ず BIZ か DOC のどちらか」という以下の前提が崩れてフレーキーになる。
        // これを避けるため、タスクを一切起動していないこの時点 (単一スレッドのみで
        // 実行中、他スレッドと競合しえない) で bizCategories へ同期的に差し替えておき、
        // 以降は「bizCategories と docCategories のどちらか」の状態のみを行き来させる。
        settings.AppCategories = bizCategories;

        using var cts = new CancellationTokenSource();

        Task writerTask = Task.Run(() =>
        {
            bool useDoc = false;
            while (!cts.IsCancellationRequested)
            {
                lock (SettingsLock.Gate)
                {
                    settings.AppCategories = useDoc ? docCategories : bizCategories;
                }
                useDoc = !useDoc;
            }
        });

        const int readerCount = 8;
        const int iterationsPerReader = 300;

        var readerTasks = new Task<string[]>[readerCount];
        for (int r = 0; r < readerCount; r++)
        {
            readerTasks[r] = Task.Run(() =>
            {
                var localResults = new string[iterationsPerReader];
                for (int i = 0; i < iterationsPerReader; i++)
                {
                    localResults[i] = WindowDetector.DetectCategory(info, settings);
                }
                return localResults;
            });
        }

        // 読み取り側がすべて完了してから書き込みスレッドを止める。writerTask の await により、
        // 書き込み側で例外が起きていた場合もここで検出される。
        string[][] allReaderResults = await Task.WhenAll(readerTasks);
        cts.Cancel();
        await writerTask;

        foreach (var localResults in allReaderResults)
        {
            foreach (var result in localResults)
            {
                // 参照差し替え方式のため、読み取り側は常に「差し替え前」か「差し替え後」の
                // 一方のみを見る。BIZ/DOC が混在した中間状態や例外にはならない。
                Assert.True(result == "BIZ" || result == "DOC", $"Unexpected category: {result}");
            }
        }
    }

    [Fact]
    public void DetectCategory_StillReturnsSameResult_AsBeforeLockWasIntroduced()
    {
        // SettingsLock.Gate 導入 (ロックの追加) が判定ロジック自体に影響していないことの
        // 回帰確認。既存の WindowDetectorTests.DetectCategory_BizProcessName_ReturnsBiz と
        // 同じ入力・期待値を、このファイル単体でも確認する。
        var settings = new AppSettings();
        var info = new WindowInfo { ProcessName = "slack", Title = "general | MyWorkspace - Slack" };

        string result = WindowDetector.DetectCategory(info, settings);

        Assert.Equal("BIZ", result);
    }
}
