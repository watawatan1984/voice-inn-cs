using System;
using System.Net.Http;
using System.Text.Json;
using VoiceIn.Ui;
using Xunit;

namespace VoiceIn.Tests.Ui;

/// <summary>
/// Ui/SettingsWindow.SummarizeFetchError (モデル一覧「更新」失敗時のステータス表示を
/// 1行に要約するロジック) のテスト。
///
/// 【背景】この関数の TaskCanceledException 分岐は、以前は RefreshModelListAsync 側の
/// catch (OperationCanceledException) が (when 句が無かったため) HttpClient のタイムアウトも
/// 含めて先に握り潰してしまい、到達不能なデッドコードになっていた。RefreshModelListAsync 側の
/// 修正 (catch に when (_modelCatalogCts.IsCancellationRequested) を追加) により、
/// タイムアウト時はこの関数まで例外が届くようになる。
///
/// SettingsWindowResetToDefaultTests.cs 等と同じ理由 (Ui/SettingsWindow は WPF の Window で
/// あり、テストホスト上でインスタンス化できない) により、internal static なメソッドのみを
/// 直接呼び出して検証する ([assembly: InternalsVisibleTo("VoiceIn.Tests")] が AssemblyInfo.cs に
/// あるため参照可能)。例外を実際に投げる (throw/catch) 必要は無く、対象の例外型のインスタンスを
/// 生成して渡すだけでよい。SettingsManager.Instance / HistoryManager.Instance / Logger には
/// 一切触れず、実ネットワークにも一切出ない。
/// </summary>
public class SettingsWindowSummarizeFetchErrorTests
{
    [Fact]
    public void SummarizeFetchError_TaskCanceledException_ReturnsTimeoutMessage()
    {
        // HttpClient.Timeout 経過時に実際に投げられるのはこの型 (OperationCanceledException の派生)。
        var ex = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.");

        Assert.Equal("タイムアウトしました", SettingsWindow.SummarizeFetchError(ex));
    }

    [Fact]
    public void SummarizeFetchError_HttpRequestException_ReturnsCommunicationErrorMessage()
    {
        var ex = new HttpRequestException("NVIDIA モデル一覧取得エラー (500): ...");

        Assert.Equal("通信エラー", SettingsWindow.SummarizeFetchError(ex));
    }

    [Fact]
    public void SummarizeFetchError_JsonException_ReturnsParseFailureMessage()
    {
        var ex = new JsonException("'<' is an invalid start of a value.");

        Assert.Equal("応答の解析に失敗しました", SettingsWindow.SummarizeFetchError(ex));
    }

    [Fact]
    public void SummarizeFetchError_InvalidOperationException_ReturnsMessageAsIs()
    {
        // API キー未設定など、ユーザーに直接見せてよい分かりやすいメッセージのみ
        // Ai/ModelCatalog.cs 側で InvalidOperationException として投げられる。
        var ex = new InvalidOperationException("Gemini の API キーが指定されていません。");

        Assert.Equal("Gemini の API キーが指定されていません。", SettingsWindow.SummarizeFetchError(ex));
    }

    [Fact]
    public void SummarizeFetchError_OtherException_ReturnsGenericFailureMessage()
    {
        // 上記いずれの型にも該当しない、想定外の例外。
        var ex = new InvalidCastException("unexpected");

        Assert.Equal("取得できませんでした", SettingsWindow.SummarizeFetchError(ex));
    }
}
