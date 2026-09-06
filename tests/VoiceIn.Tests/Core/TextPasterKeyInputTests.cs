using System;
using System.Runtime.InteropServices;
using VoiceIn.Core;
using Xunit;

namespace VoiceIn.Tests.Core;

/// <summary>
/// TextPaster.PasteTextAsync から切り出した純粋ロジック部分 (TextPasterKeyInput) のテスト。
///
/// TextPasterKeyInput は internal だが、AssemblyInfo.cs の
/// [assembly: InternalsVisibleTo("VoiceIn.Tests")] によりこのテストプロジェクトから
/// 直接参照できる。AudioSampleProcessorTests と同じ方針で、実際のキーボード状態
/// (GetAsyncKeyState) や SendInput によるキー入力送出、クリップボードには一切触れない
/// 「配列の組み立て」「戻り値の判定」だけを検証する
/// (開発機の実際のキー入力・クリップボードを操作するテストは書かないため)。
/// </summary>
public class TextPasterKeyInputTests
{
    // ==== DetermineModifiersToRelease: 実際に押下中の修飾キーだけを解除対象にする ====

    [Fact]
    public void DetermineModifiersToRelease_NeitherDown_ReturnsEmpty()
    {
        byte[] result = TextPasterKeyInput.DetermineModifiersToRelease(altDown: false, ctrlDown: false);

        Assert.Empty(result);
    }

    [Fact]
    public void DetermineModifiersToRelease_OnlyAltDown_ReturnsOnlyAlt()
    {
        byte[] result = TextPasterKeyInput.DetermineModifiersToRelease(altDown: true, ctrlDown: false);

        Assert.Equal([TextPasterKeyInput.VK_MENU], result);
    }

    [Fact]
    public void DetermineModifiersToRelease_OnlyCtrlDown_ReturnsOnlyCtrl()
    {
        // ホットキーが ctrl_l / ctrl_r に設定されているユーザーが、ホットキーを押したまま
        // 文字起こし完了に至るケースを想定 (Core/KeyboardHook.ComputeTargetVkCode 参照)。
        byte[] result = TextPasterKeyInput.DetermineModifiersToRelease(altDown: false, ctrlDown: true);

        Assert.Equal([TextPasterKeyInput.VK_CONTROL], result);
    }

    [Fact]
    public void DetermineModifiersToRelease_BothDown_ReturnsAltThenCtrl()
    {
        byte[] result = TextPasterKeyInput.DetermineModifiersToRelease(altDown: true, ctrlDown: true);

        Assert.Equal([TextPasterKeyInput.VK_MENU, TextPasterKeyInput.VK_CONTROL], result);
    }

    // ==== BuildKeyUpInputs: 指定した VK コードそれぞれの KEYUP イベントを組み立てる ====

    [Fact]
    public void BuildKeyUpInputs_EmptyArray_ReturnsEmpty()
    {
        TextPasterKeyInput.INPUT[] result = TextPasterKeyInput.BuildKeyUpInputs([]);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildKeyUpInputs_SingleVk_BuildsKeyUpEvent()
    {
        TextPasterKeyInput.INPUT[] result = TextPasterKeyInput.BuildKeyUpInputs([TextPasterKeyInput.VK_MENU]);

        TextPasterKeyInput.INPUT input = Assert.Single(result);
        Assert.Equal((uint)TextPasterKeyInput.INPUT_KEYBOARD, input.type);
        Assert.Equal(TextPasterKeyInput.VK_MENU, input.U.Ki.wVk);
        Assert.Equal(TextPasterKeyInput.KEYEVENTF_KEYUP, input.U.Ki.dwFlags);
    }

    [Fact]
    public void BuildKeyUpInputs_MultipleVks_PreservesOrder()
    {
        TextPasterKeyInput.INPUT[] result = TextPasterKeyInput.BuildKeyUpInputs(
            [TextPasterKeyInput.VK_MENU, TextPasterKeyInput.VK_CONTROL]);

        Assert.Equal(2, result.Length);
        Assert.Equal(TextPasterKeyInput.VK_MENU, result[0].U.Ki.wVk);
        Assert.Equal(TextPasterKeyInput.VK_CONTROL, result[1].U.Ki.wVk);
        Assert.All(result, i => Assert.Equal(TextPasterKeyInput.KEYEVENTF_KEYUP, i.U.Ki.dwFlags));
    }

    // ==== BuildCtrlVCombo: Ctrl↓ → V↓ → V↑ → Ctrl↑ の 4 イベント ====

    [Fact]
    public void BuildCtrlVCombo_ReturnsFourEventsInOrder()
    {
        TextPasterKeyInput.INPUT[] combo = TextPasterKeyInput.BuildCtrlVCombo();

        Assert.Equal(4, combo.Length);
        Assert.All(combo, i => Assert.Equal((uint)TextPasterKeyInput.INPUT_KEYBOARD, i.type));

        // Ctrl 押下
        Assert.Equal(TextPasterKeyInput.VK_CONTROL, combo[0].U.Ki.wVk);
        Assert.Equal(0u, combo[0].U.Ki.dwFlags);
        // V 押下
        Assert.Equal(TextPasterKeyInput.VK_V, combo[1].U.Ki.wVk);
        Assert.Equal(0u, combo[1].U.Ki.dwFlags);
        // V 解放
        Assert.Equal(TextPasterKeyInput.VK_V, combo[2].U.Ki.wVk);
        Assert.Equal(TextPasterKeyInput.KEYEVENTF_KEYUP, combo[2].U.Ki.dwFlags);
        // Ctrl 解放
        Assert.Equal(TextPasterKeyInput.VK_CONTROL, combo[3].U.Ki.wVk);
        Assert.Equal(TextPasterKeyInput.KEYEVENTF_KEYUP, combo[3].U.Ki.dwFlags);
    }

    // ==== BuildPasteSequence: 修飾キー解除 + Ctrl+V を1つの配列にまとめる ====
    // (TextPaster.SendCtrlV はこの配列を 1 回の SendInput 呼び出しで送出することで、
    //  他プロセスの入力がシーケンス途中に割り込むのを防ぐ設計になっている)

    [Fact]
    public void BuildPasteSequence_NoModifiersToRelease_IsJustTheCombo()
    {
        TextPasterKeyInput.INPUT[] sequence = TextPasterKeyInput.BuildPasteSequence([]);
        TextPasterKeyInput.INPUT[] combo = TextPasterKeyInput.BuildCtrlVCombo();

        Assert.Equal(4, sequence.Length);
        for (int i = 0; i < combo.Length; i++)
        {
            Assert.Equal(combo[i].U.Ki.wVk, sequence[i].U.Ki.wVk);
            Assert.Equal(combo[i].U.Ki.dwFlags, sequence[i].U.Ki.dwFlags);
        }
    }

    [Fact]
    public void BuildPasteSequence_OneModifierToRelease_PrependsItBeforeCombo()
    {
        TextPasterKeyInput.INPUT[] sequence = TextPasterKeyInput.BuildPasteSequence([TextPasterKeyInput.VK_MENU]);

        Assert.Equal(5, sequence.Length);
        // 先頭: Alt の KEYUP
        Assert.Equal(TextPasterKeyInput.VK_MENU, sequence[0].U.Ki.wVk);
        Assert.Equal(TextPasterKeyInput.KEYEVENTF_KEYUP, sequence[0].U.Ki.dwFlags);
        // 続く4件: Ctrl+V の組み合わせ (Ctrl↓, V↓, V↑, Ctrl↑)
        Assert.Equal(TextPasterKeyInput.VK_CONTROL, sequence[1].U.Ki.wVk);
        Assert.Equal(TextPasterKeyInput.VK_V, sequence[2].U.Ki.wVk);
        Assert.Equal(TextPasterKeyInput.VK_V, sequence[3].U.Ki.wVk);
        Assert.Equal(TextPasterKeyInput.VK_CONTROL, sequence[4].U.Ki.wVk);
    }

    [Fact]
    public void BuildPasteSequence_BothModifiersToRelease_LengthIsSix()
    {
        TextPasterKeyInput.INPUT[] sequence = TextPasterKeyInput.BuildPasteSequence(
            [TextPasterKeyInput.VK_MENU, TextPasterKeyInput.VK_CONTROL]);

        Assert.Equal(6, sequence.Length);
        Assert.Equal(TextPasterKeyInput.VK_MENU, sequence[0].U.Ki.wVk);
        Assert.Equal(TextPasterKeyInput.VK_CONTROL, sequence[1].U.Ki.wVk);
    }

    // ==== DidSendAllEvents: SendInput の戻り値 (送出できたイベント数) から成否を判定する ====

    [Fact]
    public void DidSendAllEvents_SentCountMatchesExpected_ReturnsTrue()
    {
        bool result = TextPasterKeyInput.DidSendAllEvents(sentCount: 4, expectedCount: 4);

        Assert.True(result);
    }

    [Fact]
    public void DidSendAllEvents_SentCountLessThanExpected_ReturnsFalse()
    {
        // SendInput は UIPI 等でブロックされた場合、例外を出さず途中までのイベント数を返す。
        bool result = TextPasterKeyInput.DidSendAllEvents(sentCount: 2, expectedCount: 4);

        Assert.False(result);
    }

    [Fact]
    public void DidSendAllEvents_SentCountZero_ReturnsFalse()
    {
        // SendInput が完全にブロックされた場合 (戻り値 0)。
        bool result = TextPasterKeyInput.DidSendAllEvents(sentCount: 0, expectedCount: 4);

        Assert.False(result);
    }

    [Fact]
    public void DidSendAllEvents_ExpectedCountZero_ReturnsFalse()
    {
        // 送出すべきイベントが1件も無い (組み立てに失敗している) 状態を「成功」と誤判定しない。
        bool result = TextPasterKeyInput.DidSendAllEvents(sentCount: 0, expectedCount: 0);

        Assert.False(result);
    }

    // ==== INPUT 構造体のサイズ: SendInput の cbSize 引数と一致すべき値の検証 ====

    [Fact]
    public void INPUT_MarshalSizeOf_MatchesWin32NativeStructSize()
    {
        // TextPaster.SendCtrlV は cbSize として Marshal.SizeOf(typeof(INPUT)) を渡す。
        // これが Windows 側 (winuser.h) の実際の INPUT 構造体サイズ (x64: 40 バイト,
        // x86: 28 バイト) と一致していなければ、SendInput は例外もエラーも出さず
        // 無言で送出に失敗する。MOUSEINPUT / HARDWAREINPUT を含む共用体を正しく
        // 再現できているかどうかを、プラットフォームのポインタサイズに応じた
        // 既知の期待値と突き合わせて検証する。
        int expected = IntPtr.Size == 8 ? 40 : 28;

        int actual = Marshal.SizeOf(typeof(TextPasterKeyInput.INPUT));

        Assert.Equal(expected, actual);
    }

    // ==== PasteResult: クリップボード格納と貼り付け成否を独立に表現できること ====

    [Fact]
    public void PasteResult_ClipboardSetTrueAndPastedFalse_KeepsFieldsIndependent()
    {
        // 自動貼り付けには失敗したが、クリップボードには文字起こし結果が残っている
        // (呼び出し元が「Ctrl+V で貼り付けられる」と案内すべき) ケースを表現できること。
        var result = new PasteResult(ClipboardSet: true, Pasted: false);

        Assert.True(result.ClipboardSet);
        Assert.False(result.Pasted);
    }

    [Fact]
    public void PasteResult_ClipboardSetFalse_ImpliesPastedFalseByConstruction()
    {
        var result = new PasteResult(ClipboardSet: false, Pasted: false);

        Assert.False(result.ClipboardSet);
        Assert.False(result.Pasted);
    }
}
