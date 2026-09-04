using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace VoiceIn.Core;

public static class TextPaster
{
    private const int KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;
    private const byte VK_MENU = 0x12; // Alt

    // Ctrl+V 送出後、貼り付け先アプリがクリップボードを読み取り終えるのを待ってから
    // 元のクリップボード内容へ復元するための遅延。
    // keybd_event は入力キューへ非同期に積むだけで、実際の読み取りは貼り付け先アプリが
    // WM_PASTE を処理した時点になる。Electron / Java 系など応答の遅いアプリでは 100ms 程度だと
    // 復元が先行し「復元後の古い内容が貼り付けられる」競合が起きうるため、余裕を持たせている。
    private const int ClipboardRestoreDelayMs = 600;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// 変換後のテキストをクリップボード経由で対象ウィンドウに貼り付ける。
    /// ・クリップボードへの書き込みに失敗した場合は Ctrl+V を送らずに中止する
    ///   (直前にユーザーがコピーしていた内容が誤って貼り付けられるのを防ぐ)。
    /// ・貼り付け前に元のクリップボード内容 (テキストの場合のみ) を退避し、
    ///   Ctrl+V 送出後に復元する。
    /// ・対象ウィンドウのフォアグラウンド化に失敗した場合、または送出直前に
    ///   フォアグラウンドウィンドウが対象と一致しない場合も中止する
    ///   (録音開始時と別のウィンドウに貼り付けてしまうのを防ぐ)。
    /// </summary>
    /// <returns>Ctrl+V を送出できた場合は true。途中で中止した場合は false。</returns>
    public static async Task<bool> PasteTextAsync(string text, IntPtr targetHwnd, int delayMs = 60)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            Console.WriteLine("Paste aborted: Application.Current is null.");
            return false;
        }

        // 元のクリップボード内容を退避 (テキスト以外 (画像等) の場合は復元対象外)
        string? originalText = dispatcher.Invoke(() =>
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Clipboard backup failed: {ex.Message}");
                return null;
            }
        });

        // クリップボードに変換後テキストを格納 (STAスレッドで実行)
        bool clipboardSet = dispatcher.Invoke(() =>
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Clipboard SetText failed: {ex.Message}");
                return false;
            }
        });

        if (!clipboardSet)
        {
            // クリップボードを書き換えられていないので、Ctrl+V は送らずそのまま中止する。
            return false;
        }

        bool pasted = false;
        try
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs);
            }

            // 対象ウィンドウが指定されている場合はフォーカスを戻す
            if (targetHwnd != IntPtr.Zero)
            {
                if (!SetForegroundWindow(targetHwnd))
                {
                    Console.WriteLine("Paste aborted: SetForegroundWindow failed.");
                    return false;
                }

                await Task.Delay(30);

                // 録音開始時と別のウィンドウに貼り付けてしまわないよう、送出直前に再確認する
                if (GetForegroundWindow() != targetHwnd)
                {
                    Console.WriteLine("Paste aborted: foreground window changed before paste.");
                    return false;
                }
            }

            // Altキー等の修飾キーを確実に解除
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(10);

            // Ctrl + V 送信
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(VK_V, 0, 0, UIntPtr.Zero);
            keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

            pasted = true;
            return true;
        }
        finally
        {
            // 元がテキストだった場合のみ復元する。取得・復元は STA (UIスレッド) 上で行い、
            // 復元自体の失敗は握り潰す (本処理を落とさない)。
            if (originalText != null)
            {
                if (pasted)
                {
                    await Task.Delay(ClipboardRestoreDelayMs);
                }

                dispatcher.Invoke(() =>
                {
                    try
                    {
                        Clipboard.SetDataObject(originalText, true);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Clipboard restore failed: {ex.Message}");
                    }
                });
            }
        }
    }
}
