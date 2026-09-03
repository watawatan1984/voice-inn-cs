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

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public static async Task PasteTextAsync(string text, IntPtr targetHwnd, int delayMs = 60)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // クリップボードに格納 (STAスレッドで実行)
        Application.Current?.Dispatcher.Invoke(() =>
        {
            try
            {
                Clipboard.SetDataObject(text, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Clipboard SetText failed: {ex.Message}");
            }
        });

        if (delayMs > 0)
        {
            await Task.Delay(delayMs);
        }

        // 対象ウィンドウが指定されている場合はフォーカスを戻す
        if (targetHwnd != IntPtr.Zero)
        {
            SetForegroundWindow(targetHwnd);
            await Task.Delay(30);
        }

        // Altキー等の修飾キーを確実に解除
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(10);

        // Ctrl + V 送信
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
