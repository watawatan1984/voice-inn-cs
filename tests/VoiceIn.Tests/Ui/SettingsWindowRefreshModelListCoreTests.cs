using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VoiceIn.Ui;
using Xunit;

namespace VoiceIn.Tests.Ui;

/// <summary>
/// Ui/SettingsWindow.RefreshModelListCoreAsync (モデル一覧「更新」ボタンの判定・状態遷移の
/// 中核ロジック) のテスト。
///
/// 【背景】この処理は元々 RefreshModelListAsync という private インスタンスメソッドで、
/// ComboBox/Button/TextBlock を直接操作していた。SettingsWindow は WPF の Window であり
/// テストホスト上でインスタンス化できないため (SettingsWindowResetToDefaultTests.cs 等と
/// 同じ理由)、判定・状態遷移の中核ロジックを WPF に依存しない internal static メソッドへ
/// 切り出し、実際の WPF コントロールの代わりに IModelListView (フェイク実装は本ファイル内の
/// FakeModelListView) を渡すことで検証できるようにした。
///
/// この切り出し自体は、次の2つの既存の欠陥修正 (Ui/SettingsWindow.xaml.cs) の回帰を防ぐために
/// 行った:
/// ・catch (OperationCanceledException) when (windowClosing.IsCancellationRequested):
///   ウィンドウを閉じたことによる本物のキャンセルのときだけ握り潰す。HttpClient のタイムアウト
///   (TaskCanceledException) は一般の catch (Exception) に流し「取得失敗: タイムアウトしました」
///   と表示する。
/// ・view.Text (ComboBox.Text 相当) の退避は await の後、view.Items (ItemsSource 相当) を
///   差し替える直前に行う。失敗時は view.Text に一切触れない。
///
/// SettingsManager.Instance / HistoryManager.Instance / Logger には一切触れず、WPF のコントロールも
/// 一切生成しない (IModelListView のフェイク実装のみを使う)。実ネットワークにも一切出ない
/// (fetchAsync はすべてテスト側でその場で用意したデリゲートであり、実際に HTTP 通信は行わない)。
/// </summary>
public class SettingsWindowRefreshModelListCoreTests
{
    /// <summary>
    /// IModelListView のテスト用フェイク実装。実際の ComboBox/Button/TextBlock の代わりに
    /// プレーンなプロパティへ読み書きするだけで、WPF には一切依存しない。
    ///
    /// Items は IModelListView 上は書き込み専用 (set のみ) のため、「一度も差し替えられて
    /// いないこと」を確認できるよう ItemsWasSet を別途持つ (Items 自体を null 許容にすると
    /// 「未設定」と「空配列が設定された」を区別しづらくなるため、非 null な既定値
    /// (空配列) + フラグという構成にしている)。ButtonEnabled についても、false→true の
    /// 遷移回数を確認できるよう呼び出し回数を記録する。
    /// </summary>
    private sealed class FakeModelListView : SettingsWindow.IModelListView
    {
        public string Text { get; set; } = string.Empty;

        public IReadOnlyList<string> Items { get; private set; } = Array.Empty<string>();

        public bool ItemsWasSet { get; private set; }

        public bool ButtonEnabled { get; private set; } = true;

        public string StatusText { get; private set; } = string.Empty;

        public bool StatusVisible { get; private set; }

        public int ButtonEnabledFalseCount { get; private set; }

        public int ButtonEnabledTrueCount { get; private set; }

        IReadOnlyList<string> SettingsWindow.IModelListView.Items
        {
            set
            {
                Items = value;
                ItemsWasSet = true;
            }
        }

        bool SettingsWindow.IModelListView.ButtonEnabled
        {
            set
            {
                ButtonEnabled = value;
                if (value)
                {
                    ButtonEnabledTrueCount++;
                }
                else
                {
                    ButtonEnabledFalseCount++;
                }
            }
        }

        string SettingsWindow.IModelListView.StatusText
        {
            set => StatusText = value;
        }

        bool SettingsWindow.IModelListView.StatusVisible
        {
            set => StatusVisible = value;
        }
    }

    [Fact]
    public async Task Timeout_NotWindowClosing_ShowsTimeoutStatus_AndReenablesButton()
    {
        // HttpClient.Timeout 経過時に実際に投げられる例外型 (OperationCanceledException の派生)。
        // windowClosing は未キャンセルのままなので、ウィンドウを閉じたことによる本物の
        // キャンセルとは区別され、一般の catch (Exception) 側で処理されるはずである。
        var view = new FakeModelListView();
        using var windowClosing = new CancellationTokenSource();
        Task<IReadOnlyList<string>> FetchAsync(CancellationToken ct) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.");

        await SettingsWindow.RefreshModelListCoreAsync(view, FetchAsync, windowClosing.Token);

        Assert.Equal("取得失敗: タイムアウトしました", view.StatusText);
        Assert.True(view.ButtonEnabled);
        Assert.False(view.ItemsWasSet);
    }

    [Fact]
    public async Task WindowClosing_OperationCanceled_LeavesStatusAsFetching_AndButtonStaysDisabled()
    {
        // ウィンドウを閉じたことによる本物のキャンセル。この場合は catch (OperationCanceledException)
        // when (windowClosing.IsCancellationRequested) にマッチし、ステータス表示もボタンの
        // 再有効化も一切行わない (閉じた後の Window を触っても意味が無いため)。
        var view = new FakeModelListView();
        using var windowClosing = new CancellationTokenSource();
        windowClosing.Cancel();
        Task<IReadOnlyList<string>> FetchAsync(CancellationToken ct) =>
            throw new OperationCanceledException();

        await SettingsWindow.RefreshModelListCoreAsync(view, FetchAsync, windowClosing.Token);

        Assert.Equal("取得中...", view.StatusText);
        Assert.False(view.ButtonEnabled);
        Assert.False(view.ItemsWasSet);
    }

    [Fact]
    public async Task TypedValueDuringFetch_IsPreserved_OnSuccess()
    {
        // 取得中 (await で保留中) にユーザーが Text を打ち替えたケース。成功時は
        // Items 差し替え直前に Text を退避するため、打ち替えた値がそのまま残るはずである。
        var view = new FakeModelListView { Text = "元の値" };
        var tcs = new TaskCompletionSource<IReadOnlyList<string>>();

        Task refreshTask = SettingsWindow.RefreshModelListCoreAsync(view, _ => tcs.Task, CancellationToken.None);

        // fetchAsync がまだ完了していない間にユーザーが入力し直した状況を再現する。
        view.Text = "取得中に打ち替えた値";
        tcs.SetResult(new[] { "model-a", "model-b" });
        await refreshTask;

        Assert.Equal("取得中に打ち替えた値", view.Text);
        Assert.True(view.ItemsWasSet);
        Assert.Equal(new[] { "model-a", "model-b" }, view.Items);
        Assert.Equal("2件取得しました。", view.StatusText);
    }

    [Fact]
    public async Task TypedValueDuringFetch_IsPreserved_OnFailure()
    {
        // 取得中に打ち替えた値は、失敗時も (Items に一切触れないため) そのまま残るはずである。
        var view = new FakeModelListView { Text = "元の値" };
        var tcs = new TaskCompletionSource<IReadOnlyList<string>>();

        Task refreshTask = SettingsWindow.RefreshModelListCoreAsync(view, _ => tcs.Task, CancellationToken.None);

        view.Text = "取得中に打ち替えた値";
        tcs.SetException(new InvalidCastException("unexpected"));
        await refreshTask;

        Assert.Equal("取得中に打ち替えた値", view.Text);
        Assert.False(view.ItemsWasSet);
        Assert.Equal("取得失敗: 取得できませんでした", view.StatusText);
    }

    [Fact]
    public async Task Success_ValueNotInList_TextIsKept_ItemsReplaced_StatusShowsCount()
    {
        var view = new FakeModelListView { Text = "一覧に無い自由入力値" };
        IReadOnlyList<string> models = new[] { "model-a", "model-b", "model-c" };

        await SettingsWindow.RefreshModelListCoreAsync(view, _ => Task.FromResult(models), CancellationToken.None);

        Assert.Equal("一覧に無い自由入力値", view.Text);
        Assert.Equal(models, view.Items);
        Assert.Equal("3件取得しました。", view.StatusText);
        Assert.True(view.ButtonEnabled);
    }

    [Fact]
    public async Task WhilePending_ButtonIsDisabled()
    {
        // 取得中 (fetchAsync が完了する前) はボタンが無効化されていることを確認する。
        var view = new FakeModelListView();
        var tcs = new TaskCompletionSource<IReadOnlyList<string>>();

        Task refreshTask = SettingsWindow.RefreshModelListCoreAsync(view, _ => tcs.Task, CancellationToken.None);

        Assert.False(view.ButtonEnabled);
        Assert.Equal("取得中...", view.StatusText);
        Assert.True(view.StatusVisible);

        tcs.SetResult(Array.Empty<string>());
        await refreshTask;

        Assert.True(view.ButtonEnabled);
    }

    [Fact]
    public async Task ButtonEnabled_IsSetFalseThenTrue_OnSuccess()
    {
        // ButtonEnabled への書き込み順序 (false → true) を明示的に確認する。
        var view = new FakeModelListView();

        await SettingsWindow.RefreshModelListCoreAsync(view, _ => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()), CancellationToken.None);

        Assert.Equal(1, view.ButtonEnabledFalseCount);
        Assert.Equal(1, view.ButtonEnabledTrueCount);
    }

    [Fact]
    public async Task WindowClosing_OperationCanceled_ButtonEnabledNeverSetTrue()
    {
        // ウィンドウを閉じた後の本物のキャンセルでは、finally でも再有効化しないこと
        // (ButtonEnabled=true への書き込みが一度も発生しないこと) を明示的に確認する。
        var view = new FakeModelListView();
        using var windowClosing = new CancellationTokenSource();
        windowClosing.Cancel();

        await SettingsWindow.RefreshModelListCoreAsync(
            view,
            ct => throw new OperationCanceledException(ct),
            windowClosing.Token);

        Assert.Equal(0, view.ButtonEnabledTrueCount);
        Assert.Equal(1, view.ButtonEnabledFalseCount);
    }
}
