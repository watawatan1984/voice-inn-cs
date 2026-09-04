using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VoiceIn.Core;

public class WindowInfo
{
    public string Title { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public IntPtr Hwnd { get; set; } = IntPtr.Zero;
}

public static class WindowDetector
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public static WindowInfo GetActiveWindow()
    {
        var info = new WindowInfo();
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return info;
        }

        info.Hwnd = hwnd;

        const int nChars = 256;
        var buff = new StringBuilder(nChars);
        if (GetWindowText(hwnd, buff, nChars) > 0)
        {
            info.Title = buff.ToString();
        }

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != 0)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                info.ProcessName = proc.ProcessName;
            }
            catch
            {
                // Access denied or exited
            }
        }

        return info;
    }

    public static string DetectCategory(WindowInfo info, AppSettings settings)
    {
        if (!settings.ContextAwareEnabled)
        {
            return "STD";
        }

        string text = $"{info.ProcessName} {info.Title}".ToLowerInvariant();

        // settings.AppCategories は設定画面での保存処理 (Ui/SettingsWindow.OnSaveAndApply) から
        // バックグラウンドスレッドで参照ごと差し替えられうるため、SettingsLock.Gate の下で
        // 列挙用のスナップショット (KeyValuePair の配列) を取ってから即座にロックを抜ける。
        // ロック内で行うのは列挙とその場でのコピーのみで、キーワード照合 (ロック不要な
        // 純粋な文字列比較) はロックの外で行う。これにより、保存処理の前後で新旧の
        // AppCategories が混在して読まれることを防ぎつつ、ロックの保持時間も最短にする。
        KeyValuePair<string, List<string>>[] categoriesSnapshot;
        lock (SettingsLock.Gate)
        {
            categoriesSnapshot = settings.AppCategories.ToArray();
        }

        foreach (var (cat, keywords) in categoriesSnapshot)
        {
            if (cat == "STD") continue;

            foreach (var kw in keywords)
            {
                if (!string.IsNullOrWhiteSpace(kw) && text.Contains(kw.ToLowerInvariant()))
                {
                    return cat;
                }
            }
        }

        return "STD";
    }
}
