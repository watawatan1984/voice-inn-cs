using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoiceIn.Core;

public class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_LMENU = 0xA4; // Left Alt
    private const int VK_RMENU = 0xA5; // Right Alt
    private const int VK_LCONTROL = 0xA2; // Left Ctrl
    private const int VK_RCONTROL = 0xA3; // Right Ctrl

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _isKeyPressed = false;

    // フックコールバックはシステム全体のあらゆるキーイベントごとに呼ばれるホットパスのため、
    // 毎回 SettingsManager から HoldKey を読んで文字列アロケーションするのを避け、
    // 対象 VK コードをキャッシュしておく。Start() 時と、設定保存時 (RefreshHoldKey 呼び出し) に
    // のみ再計算する。
    private int _targetVkCode = VK_LMENU;

    public event Action? KeyPressed;
    public event Action? KeyReleased;

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    public void Start()
    {
        RefreshHoldKey();
        if (_hookId == IntPtr.Zero)
        {
            _hookId = SetHook(_proc);
        }
    }

    /// <summary>
    /// キャッシュしている対象 VK コードを現在の設定 (Audio.HoldKey) から再計算する。
    /// ホットキー設定が変更されたとき (設定画面の保存完了時) に呼び出すこと。
    /// </summary>
    public void RefreshHoldKey()
    {
        _targetVkCode = ComputeTargetVkCode();
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            _isKeyPressed = false;
        }
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        IntPtr hookId = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule?.ModuleName), 0);
        if (hookId == IntPtr.Zero)
        {
            // SetLastError = true の P/Invoke 直後、他の呼び出しを挟まずに取得すること。
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error);
        }

        return hookId;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);

            if (vkCode == _targetVkCode)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    if (!_isKeyPressed)
                    {
                        _isKeyPressed = true;
                        KeyPressed?.Invoke();
                    }
                }
                else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    if (_isKeyPressed)
                    {
                        _isKeyPressed = false;
                        KeyReleased?.Invoke();
                    }
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static int ComputeTargetVkCode()
    {
        string holdKey = SettingsManager.Instance.Settings.Audio.HoldKey?.ToLowerInvariant() ?? "alt_l";
        return holdKey switch
        {
            "alt_r" => VK_RMENU,
            "ctrl_l" => VK_LCONTROL,
            "ctrl_r" => VK_RCONTROL,
            _ => VK_LMENU // デフォルト: alt_l
        };
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
