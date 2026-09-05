using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VoiceIn.Core;
using Xunit;

namespace VoiceIn.Tests.Core;

/// <summary>
/// Core/WindowDetector.DetectCategory による「検出済みアプリ履歴」記録 (AppSettings.DetectedApps
/// への書き込みと、新規アプリ検出時の保存フック呼び出し) の並行アクセス安全性のテスト。
/// 既存の WindowDetectorConcurrencyTests.cs (AppCategories の並行アクセス) と同じ方針・
/// スタイルに倣う。
///
/// ここで検証する内容:
/// ・同じ (未検出の) アプリ名に対して大量のスレッドから同時に DetectCategory を呼んでも
///   例外が出ないこと、かつ記録・保存フック呼び出しがちょうど1回だけ発生すること
///   (SettingsLock.Gate による排他が「新規アプリの記録」を二重に行わせないことの確認)。
/// ・異なる (未検出の) アプリ名を複数スレッドから同時に検出させた場合、それぞれが
///   正しく1回ずつ記録・保存されること。
///
/// 【重要】SettingsManager.Instance には一切触れない。WindowDetector.SaveSettingsCallback
/// (internal, テスト専用フック) を差し替えることで、実ファイルへ書き込まずに保存呼び出し
/// 回数を検証する。
/// </summary>
public class WindowDetectorDetectedAppsConcurrencyTests
{
    [Fact]
    public async Task DetectCategory_ManyThreadsDetectingSameNewApp_RecordsOnceAndSavesExactlyOnce()
    {
        var settings = new AppSettings();
        var info = new WindowInfo { ProcessName = "Code", Title = "main.cs - MyProject - Visual Studio Code" };

        int saveCallCount = 0;
        WindowDetector.SaveSettingsCallback = _ => Interlocked.Increment(ref saveCallCount);
        try
        {
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

            // 3200回 (16スレッド x 200回) 呼ばれても、記録されるのは1エントリのみ。
            Assert.Single(settings.DetectedApps);
            Assert.True(settings.DetectedApps.ContainsKey("Code"));
            // 保存フックが呼ばれるのも「最初の1回」だけ (性能要件: 既知のアプリでは書き込まない)。
            Assert.Equal(1, saveCallCount);
        }
        finally
        {
            WindowDetector.SaveSettingsCallback = null;
        }
    }

    [Fact]
    public async Task DetectCategory_ManyThreadsDetectingDifferentNewApps_RecordsEachExactlyOnce()
    {
        var settings = new AppSettings();
        const int appCount = 16;
        const int iterationsPerApp = 100;

        int saveCallCount = 0;
        WindowDetector.SaveSettingsCallback = _ => Interlocked.Increment(ref saveCallCount);
        try
        {
            var tasks = new Task[appCount];
            for (int a = 0; a < appCount; a++)
            {
                int appIndex = a;
                tasks[a] = Task.Run(() =>
                {
                    var info = new WindowInfo { ProcessName = $"concurrentapp{appIndex}", Title = $"window {appIndex}" };
                    for (int i = 0; i < iterationsPerApp; i++)
                    {
                        WindowDetector.DetectCategory(info, settings);
                    }
                });
            }

            await Task.WhenAll(tasks);

            Assert.Equal(appCount, settings.DetectedApps.Count);
            Assert.Equal(appCount, saveCallCount);
            for (int a = 0; a < appCount; a++)
            {
                Assert.True(settings.DetectedApps.ContainsKey($"concurrentapp{a}"), $"concurrentapp{a} が記録されていません。");
            }
        }
        finally
        {
            WindowDetector.SaveSettingsCallback = null;
        }
    }

    [Fact]
    public async Task DetectCategory_ConcurrentWithCategoryAssignment_DoesNotThrow_AndReturnsAssignedCategory()
    {
        // WindowDetector.DetectCategory (バックグラウンドスレッド想定) と
        // Ui.SettingsWindow.ApplyDetectedAppCategoryAssignment (設定画面からの割り当て、
        // 別スレッド想定) が同じ SettingsLock.Gate ・同じ DetectedApps/AppCategories を
        // 取り合っても例外が出ないことを確認する。
        var settings = new AppSettings();
        var info = new WindowInfo { ProcessName = "myide", Title = "untitled - MyIDE" };

        // 事前準備 (単一スレッドのみで実行中): エントリを作成し、DEV を割り当てておく。
        WindowDetector.SaveSettingsCallback = _ => { };
        try
        {
            WindowDetector.DetectCategory(info, settings);
        }
        finally
        {
            WindowDetector.SaveSettingsCallback = null;
        }

        VoiceIn.Ui.SettingsWindow.ApplyDetectedAppCategoryAssignment(settings, "myide", "DEV");
        Assert.Equal("DEV", settings.DetectedApps["myide"].UserCategory);

        using var cts = new CancellationTokenSource();

        // 割り当てスレッド: 同じ割り当て (DEV) を繰り返し適用し続ける。値自体は変わらないが、
        // DetectCategory 側の読み取りと同じロックを取り合う状況を作るのが目的。
        Task assignerTask = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                VoiceIn.Ui.SettingsWindow.ApplyDetectedAppCategoryAssignment(settings, "myide", "DEV");
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

        string[][] allReaderResults = await Task.WhenAll(readerTasks);
        cts.Cancel();
        await assignerTask;

        foreach (var localResults in allReaderResults)
        {
            foreach (var result in localResults)
            {
                // DEV は事前準備の時点で既に割り当て済みであり、並行実行中は誰も別の値へ
                // 変更しないため、常に DEV が返るはず。
                Assert.Equal("DEV", result);
            }
        }
    }
}
