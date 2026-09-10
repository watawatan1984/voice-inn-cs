using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace VoiceIn.Core;

/// <summary>
/// PasteTextAsync の結果。「クリップボードへの格納」と「実際の Ctrl+V 送出」を分けて表す。
///
/// 【設計方針】参照アプリ (Aqua Voice / Typeless) は「まずクリップボードに確実に置き、それは
/// 消さない。貼り付けは best-effort」という思想で、未選択の状態でもクリップボードに文字起こし
/// 結果が届くため、貼り付け自体が失敗してもユーザーは任意の場所で手動 Ctrl+V すれば結果を
/// 得られる。本アプリもこれに合わせる。
///
/// <see cref="ClipboardSet"/> が true であれば、たとえ <see cref="Pasted"/> が false (自動貼り付け
/// が失敗) であっても、クリップボードには文字起こし結果が残っている。呼び出し元
/// (App.xaml.cs) はこの2つを区別してユーザーへの通知文言を出し分けること。
/// </summary>
public readonly record struct PasteResult(bool ClipboardSet, bool Pasted);

public static class TextPaster
{
    private const uint ASFW_ANY = 0xFFFFFFFF;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, TextPasterKeyInput.INPUT[] pInputs, int cbSize);

    /// <summary>
    /// 変換後のテキストをクリップボードへ格納するだけの処理 (Ctrl+V は送らない)。
    ///
    /// AutoPaste 設定を無効にしているユーザー向け: 自動貼り付けは行わないが、参照アプリ
    /// (Aqua Voice / Typeless) と同様に文字起こし結果は必ずクリップボードに置き、
    /// 利用者が任意のタイミング・任意の場所で手動 Ctrl+V できる状態を保証する
    /// (呼び出し箇所は App.xaml.cs の OnKeyReleased 参照)。PasteTextAsync 内部でも
    /// クリップボードへの格納にはこのメソッドを使う。
    /// </summary>
    /// <returns>格納に成功した場合は true。</returns>
    public static Task<bool> CopyToClipboardAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Task.FromResult(false);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            Logger.Error("Clipboard copy aborted: Application.Current is null.");
            return Task.FromResult(false);
        }

        // WPF の Clipboard は STA スレッドでのみ操作できるため、UI スレッドの Dispatcher 経由で
        // 実行する (本メソッドはバックグラウンドスレッド (Task.Run 内) から呼ばれる想定)。
        bool clipboardSet = dispatcher.Invoke(() =>
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Clipboard SetText failed", ex);
                return false;
            }
        });

        // 【ログ方針】ここで記録してよいのは成否のみ。text (文字起こし本文) やクリップボードの
        // 内容自体は Core/Logger.cs 冒頭の【プライバシー上の厳守事項】により絶対に書かない。
        if (clipboardSet)
        {
            Logger.Info("Clipboard: text stored successfully.");
        }
        else
        {
            Logger.Warn("Clipboard: failed to store text.");
        }

        return Task.FromResult(clipboardSet);
    }

    /// <summary>
    /// 変換後のテキストをクリップボードへ格納した上で、対象ウィンドウへの Ctrl+V 送出を試みる。
    ///
    /// 【重要な設計変更】旧実装は「貼り付け前の元のクリップボード内容を退避し、処理の成否に
    /// 関わらず finally で復元する」実装だったため、SetForegroundWindow 失敗などで早期
    /// return しても finally が実行され、直前に格納したばかりの文字起こし結果が
    /// 元の内容で上書きされて消えてしまっていた
    /// (「貼り付けもされず、クリップボードにも残らない」という実際の不具合報告の原因)。
    ///
    /// 新実装はクリップボードの復元を一切行わない。文字起こし結果をクリップボードから
    /// 消してはならない、というのが本メソッドの一貫した方針であり、ユーザーの元の
    /// クリップボード内容は上書きされるが、これは参照アプリ (Aqua Voice / Typeless) と同じ
    /// 挙動であり、利用者が明示的に求めている挙動でもある。「元の内容を守る」ことより
    /// 「文字起こし結果を確実に残す」ことを優先する。
    ///
    /// また、対象ウィンドウのフォアグラウンド化に失敗しても、またはフォアグラウンドウィンドウが
    /// 送出直前に対象と一致しなくても、処理を中断しない。フォアグラウンド化は
    /// TryActivateForeground 内で best-effort として試みるだけで、その成否に関わらず
    /// Ctrl+V の送出まで進む。対象ウィンドウが既にフォアグラウンドにあれば送出は成功しうるし、
    /// たとえ本当に送出が届かなくても、クリップボードには文字起こし結果が残るため
    /// 利用者は手動の Ctrl+V で結果を得られる (<see cref="PasteResult.ClipboardSet"/> 参照)。
    /// </summary>
    /// <returns>
    /// クリップボードへの格納可否 (ClipboardSet) と、Ctrl+V を送出できたか (Pasted) の組。
    /// クリップボードへ格納できなかった場合は Ctrl+V 自体を送らず、両方 false を返す
    /// (直前にユーザーがコピーしていた無関係な内容を誤って貼り付けてしまうのを防ぐため)。
    /// </returns>
    public static async Task<PasteResult> PasteTextAsync(string text, IntPtr targetHwnd, int delayMs = 60)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new PasteResult(false, false);
        }

        bool clipboardSet = await CopyToClipboardAsync(text);
        if (!clipboardSet)
        {
            // クリップボードに置けていないので、Ctrl+V を送っても無意味 (かつ直前にユーザーが
            // コピーしていた無関係な内容を誤って貼り付けかねない) なので送出しない。
            return new PasteResult(false, false);
        }

        if (delayMs > 0)
        {
            await Task.Delay(delayMs);
        }

        // 対象ウィンドウのフォアグラウンド化を試みる (失敗しても中止しない)。
        // targetHwnd が不明 (IntPtr.Zero) な場合は、そもそもどのウィンドウも
        // フォアグラウンドに引き上げようがないため、現在フォーカスのある場所へ
        // そのまま Ctrl+V を送る。
        if (targetHwnd != IntPtr.Zero)
        {
            TryActivateForeground(targetHwnd);

            // AttachThreadInput 直後、入力キューが安定するまで一瞬待つ (旧実装の待機を踏襲)。
            await Task.Delay(30);
        }
        else
        {
            Logger.Info("Paste: no target window handle recorded; skipping foreground activation.");
        }

        bool pasted = SendCtrlV();
        Logger.Info($"Paste: completed. clipboardSet=true, pasted={pasted}.");

        return new PasteResult(true, pasted);
    }

    /// <summary>
    /// 対象ウィンドウをフォアグラウンドへ引き上げることを試みる (best-effort)。
    ///
    /// 【背景】本アプリは常駐トレイアプリであり、ホットキー押下から本メソッド呼び出しまでの間に
    /// 文字起こしのネットワーク I/O (数秒かかりうる) を挟む。Windows の SetForegroundWindow には
    /// 「直近でユーザー入力を受け取ったプロセスでなければ拒否する」という制約があり、
    /// バックグラウンドで数秒待った後の本プロセスはこの資格を高確率で失っている。
    /// AttachThreadInput で対象ウィンドウのスレッドと自スレッドの入力キューを一時的に結合すると、
    /// Windows 上は両者が同格 (同じ入力キューを共有する関係) とみなされ、この制約を回避できる
    /// (Win32 の定石)。AllowSetForegroundWindow(ASFW_ANY) も合わせて試みるが、
    /// これ単体で確実に効くとは限らないため、あくまで補助として扱う。
    ///
    /// 失敗しても例外は外に投げない。AttachThreadInput / SetForegroundWindow 等の失敗で
    /// 中断すると、クリップボードに残った文字起こし結果を活かす最後の手段である Ctrl+V の
    /// 送出まで巻き込んで失敗させてしまうため、本メソッドの戻り値に関わらず
    /// 呼び出し元 (PasteTextAsync) は Ctrl+V の送出へ進むこと。
    ///
    /// AttachThreadInput で結合した入力キューは、デタッチ漏れがあると他アプリの操作に
    /// 影響しうる (キー入力やフォーカス切り替えが結合されたままになる) ため、
    /// アタッチに成功した場合は必ず finally でデタッチする。
    /// </summary>
    /// <returns>SetForegroundWindow が成功を報告した場合は true (参考情報。失敗しても中断しない)。</returns>
    private static bool TryActivateForeground(IntPtr targetHwnd)
    {
        if (targetHwnd == IntPtr.Zero)
        {
            return false;
        }

        uint currentThreadId = GetCurrentThreadId();
        uint targetThreadId = 0;
        bool attached = false;
        bool activated = false;

        try
        {
            targetThreadId = GetWindowThreadProcessId(targetHwnd, out _);
            if (targetThreadId != 0 && targetThreadId != currentThreadId)
            {
                attached = AttachThreadInput(currentThreadId, targetThreadId, true);
            }

            // ASFW_ANY: 特定プロセスに限らず SetForegroundWindow を許可する。
            AllowSetForegroundWindow(ASFW_ANY);

            activated = SetForegroundWindow(targetHwnd);
            BringWindowToTop(targetHwnd);
            SetFocus(targetHwnd);
        }
        catch (Exception ex)
        {
            // AttachThreadInput 系の user32 API が例外を投げることは通常ないが、
            // 万一に備えて握りつぶす (アタッチ・デタッチの失敗で例外を投げないこと、という
            // 要件を「呼び出し元に絶対に伝播しない」形で満たす)。
            Logger.Warn($"Paste: foreground activation threw and was suppressed ({ex.GetType().Name}).");
            activated = false;
        }
        finally
        {
            // attached が true (= AttachThreadInput 自体が成功した) の場合のみデタッチする。
            // try ブロックの後半 (AllowSetForegroundWindow 以降) で例外が起きて catch に
            // 落ちた場合でも、finally は必ず実行されるためデタッチ漏れは起きない。
            if (attached)
            {
                try
                {
                    AttachThreadInput(currentThreadId, targetThreadId, false);
                }
                catch
                {
                    // デタッチ自体の失敗も best-effort。ここで例外を投げると、せっかく
                    // クリップボードに残した文字起こし結果を活かす Ctrl+V 送出まで
                    // 失敗させてしまうため、握りつぶして処理を続行させる。
                }
            }
        }

        Logger.Info($"Paste: foreground activation attached={attached}, activated={activated}, targetHwnd=0x{targetHwnd.ToInt64():X8}.");
        return activated;
    }

    /// <summary>
    /// 押されたままになっている可能性のある Alt / Ctrl を解除した上で、Ctrl+V を
    /// 1回の SendInput 呼び出しで送出する。
    ///
    /// ホットキー (Audio.HoldKey 設定。Core/KeyboardHook.ComputeTargetVkCode 参照) は
    /// alt_l / alt_r / ctrl_l / ctrl_r のいずれかであり、ユーザーがホットキーを押したまま
    /// 本メソッドの呼び出しに至る (＝離す前に文字起こしが完了する) 可能性がある。
    /// GetAsyncKeyState で実際に押下中の修飾キーだけを判定し、無関係のキーには触れない。
    ///
    /// 実際のキーボード状態を読み書きする本メソッド自体は (開発機の実キーボード状態を
    /// 変えてしまうため) テストしない。イベント配列の組み立てと修飾キー解除対象の判定は
    /// 純粋関数として TextPasterKeyInput に切り出してあり、そちらをテストする。
    /// </summary>
    /// <returns>組み立てた全イベントを SendInput が送出できた場合は true。</returns>
    private static bool SendCtrlV()
    {
        byte[] stuckModifiers = TextPasterKeyInput.DetermineModifiersToRelease(
            altDown: IsKeyDown(TextPasterKeyInput.VK_MENU),
            ctrlDown: IsKeyDown(TextPasterKeyInput.VK_CONTROL));

        TextPasterKeyInput.INPUT[] sequence = TextPasterKeyInput.BuildPasteSequence(stuckModifiers);

        // cbSize は「送っている配列 1 要素のサイズ」ではなく、Windows が期待する INPUT 構造体の
        // 実サイズと一致していなければならない。ここを誤る (例: KEYBDINPUT だけのサイズを渡す、
        // 共用体を省略したサイズを渡す等) と SendInput は例外もエラーも出さずに 0 を返して
        // 無言で失敗する。TextPasterKeyInput.INPUT は MOUSEINPUT / KEYBDINPUT / HARDWAREINPUT の
        // 共用体を正しく再現しているため、Marshal.SizeOf がそのまま Windows 側の期待値と
        // 一致するサイズ (x64 では 40 バイト) になる。
        int cbSize = Marshal.SizeOf(typeof(TextPasterKeyInput.INPUT));
        uint sentCount = SendInput((uint)sequence.Length, sequence, cbSize);

        bool allSent = TextPasterKeyInput.DidSendAllEvents(sentCount, sequence.Length);
        if (allSent)
        {
            Logger.Info($"Paste: SendInput dispatched {sentCount}/{sequence.Length} event(s).");
        }
        else
        {
            Logger.Warn($"Paste: SendInput dispatched only {sentCount}/{sequence.Length} event(s) (Win32Error={Marshal.GetLastWin32Error()}).");
        }

        return allSent;
    }

    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;
}

/// <summary>
/// TextPaster.PasteTextAsync が SendInput へ渡す入力イベント列を組み立てる、実際のキー入力
/// 送出やクリップボード操作を一切行わない純粋ロジック部分。
///
/// Audio/AudioRecorder.cs の AudioSampleProcessor (NAudio やマイクデバイスに依存しない
/// ゲイン計算・RMS 集計を切り出したクラス) と同じ方針で、実機のキーボード状態・
/// クリップボードに触れない部分だけを internal として切り出し、
/// AssemblyInfo.cs の [assembly: InternalsVisibleTo("VoiceIn.Tests")] 経由で単体テストする。
/// これにより「開発機の実際のキー入力・クリップボードを操作するテストは書かない」制約を
/// 守りつつ、戻り値の判定や修飾キー解除対象の決定といったロジックだけを検証できる。
/// </summary>
internal static class TextPasterKeyInput
{
    internal const int INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const byte VK_CONTROL = 0x11;
    internal const byte VK_MENU = 0x12; // Alt
    internal const byte VK_V = 0x56;

    // SendInput (user32.dll) の LPINPUT が指す共用体を C# 側で再現するための構造体群。
    // フィールドの型・並び順は winuser.h の定義と一致させてあり、これが崩れると
    // Marshal.SizeOf(INPUT) が Windows 側の期待するサイズと食い違い、SendInput が
    // (エラーも出さず) 送出に失敗する。実際に使うのは KEYBDINPUT だけだが、
    // MOUSEINPUT / HARDWAREINPUT も含めた共用体全体を定義しないと、共用体としての
    // サイズ (= 最大のメンバーのサイズ) が本来より小さくなってしまう点に注意。

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        internal ushort wVk;
        internal ushort wScan;
        internal uint dwFlags;
        internal uint time;
        internal IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        internal int dx;
        internal int dy;
        internal uint mouseData;
        internal uint dwFlags;
        internal uint time;
        internal IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        internal uint uMsg;
        internal ushort wParamL;
        internal ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] internal MOUSEINPUT Mi;
        [FieldOffset(0)] internal KEYBDINPUT Ki;
        [FieldOffset(0)] internal HARDWAREINPUT Hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        internal uint type;
        internal InputUnion U;
    }

    private static INPUT KeyInput(byte vk, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            Ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    /// <summary>
    /// 現在実際に押下中の修飾キー (Alt / Ctrl) のうち、Ctrl+V 送出前に解除すべきものを判定する。
    /// 押されていないキーには触れない (無関係な KEYUP を送らない) 。
    /// GetAsyncKeyState の呼び出し自体は TextPaster 側の責務とし、ここでは判定結果の bool
    /// だけを受け取ることで、実キーボード状態なしにテストできる純粋関数にしている。
    /// 戻り値の順序は Alt → Ctrl で固定 (どちらも該当する場合)。
    /// </summary>
    internal static byte[] DetermineModifiersToRelease(bool altDown, bool ctrlDown)
    {
        if (altDown && ctrlDown)
        {
            return [VK_MENU, VK_CONTROL];
        }
        if (altDown)
        {
            return [VK_MENU];
        }
        if (ctrlDown)
        {
            return [VK_CONTROL];
        }
        return [];
    }

    /// <summary>指定した VK コードそれぞれについて、KEYUP (キーを離す) イベントを組み立てる。</summary>
    internal static INPUT[] BuildKeyUpInputs(byte[] vkCodes)
    {
        var inputs = new INPUT[vkCodes.Length];
        for (int i = 0; i < vkCodes.Length; i++)
        {
            inputs[i] = KeyInput(vkCodes[i], KEYEVENTF_KEYUP);
        }
        return inputs;
    }

    /// <summary>Ctrl+V (Ctrl↓ → V↓ → V↑ → Ctrl↑) の 4 イベントを組み立てる。</summary>
    internal static INPUT[] BuildCtrlVCombo() =>
    [
        KeyInput(VK_CONTROL, 0),
        KeyInput(VK_V, 0),
        KeyInput(VK_V, KEYEVENTF_KEYUP),
        KeyInput(VK_CONTROL, KEYEVENTF_KEYUP)
    ];

    /// <summary>
    /// 実際に SendInput へ渡す入力イベント列全体 (解除すべき修飾キーの KEYUP 群 → Ctrl+V の
    /// 4 イベント) を組み立てる。呼び出し元 (TextPaster.SendCtrlV) はこれを丸ごと 1 回の
    /// SendInput 呼び出しで送出することで、途中に他プロセスの入力が割り込むのを防ぐ。
    /// </summary>
    internal static INPUT[] BuildPasteSequence(byte[] modifiersToRelease)
    {
        INPUT[] releases = BuildKeyUpInputs(modifiersToRelease);
        INPUT[] combo = BuildCtrlVCombo();

        var sequence = new INPUT[releases.Length + combo.Length];
        releases.CopyTo(sequence, 0);
        combo.CopyTo(sequence, releases.Length);
        return sequence;
    }

    /// <summary>SendInput の戻り値 (実際に送出できたイベント数) が全数送出できたかを判定する。</summary>
    internal static bool DidSendAllEvents(uint sentCount, int expectedCount) =>
        expectedCount > 0 && sentCount == (uint)expectedCount;
}
