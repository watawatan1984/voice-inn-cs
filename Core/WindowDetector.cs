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

        // キーワードによる自動判定 (既存ロジック、変更なし)。新規アプリの記録時に
        // auto_category として書き込む値でもあり、user_category が未設定のアプリの
        // フォールバック結果でもある。
        string autoCategory = DetectCategoryByKeywords(info, settings);

        // 検出済みアプリ履歴への記録 (未記録のアプリ名の場合のみ) と、
        // user_category (ユーザーによる上書き) の取得。
        string? userCategory = RecordDetectionAndGetUserCategory(info, settings, autoCategory);

        // user_category が設定されていれば、キーワードによる自動判定より優先する
        // (移植元 Python 版 get_effective_category, context_prompt.py:233-240 相当)。
        return !string.IsNullOrWhiteSpace(userCategory) ? userCategory : autoCategory;
    }

    /// <summary>
    /// settings.AppCategories のキーワードに基づく自動判定のみを行う (DetectCategory から
    /// 抽出した既存ロジック、動作は一切変更していない)。
    /// </summary>
    private static string DetectCategoryByKeywords(WindowInfo info, AppSettings settings)
    {
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

    /// <summary>
    /// settings.DetectedApps (検出済みアプリ履歴) へ、まだ記録されていないアプリ名の場合のみ
    /// 新規エントリを追加する (既存エントリの title_sample / auto_category / user_category は
    /// 一切上書きしない)。戻り値はそのアプリの user_category (未割り当てなら null)。
    ///
    /// 【性能・安全性】書き込み (Dictionary への追加、および PersistNewDetection 経由のファイル
    /// 保存) が発生するのは「まだ見たことのないアプリ名」のときだけであり、既知のアプリでは
    /// TryGetValue のみで即座に返る (発話のたびにファイル I/O が走ることはない)。
    /// settings.DetectedApps への読み書きは SettingsLock.Gate の下で行い、ロック内では
    /// 辞書の参照・小さな値の代入のみを行う (ファイル I/O は PersistNewDetection 側で
    /// ロックの外に出してから行う)。
    /// </summary>
    private static string? RecordDetectionAndGetUserCategory(WindowInfo info, AppSettings settings, string autoCategory)
    {
        string appName = info.ProcessName;
        if (string.IsNullOrWhiteSpace(appName))
        {
            // プロセス名を取得できなかった (アクセス拒否・終了済みプロセス等) 場合は
            // 記録のしようがないため、何もせず抜ける。
            return null;
        }

        bool isNewApp;
        string? userCategory;

        lock (SettingsLock.Gate)
        {
            settings.DetectedApps ??= [];

            if (settings.DetectedApps.TryGetValue(appName, out var existing))
            {
                isNewApp = false;
                userCategory = existing.UserCategory;
            }
            else
            {
                isNewApp = true;
                userCategory = null;
                settings.DetectedApps[appName] = new DetectedAppInfo
                {
                    TitleSample = info.Title,
                    AutoCategory = autoCategory,
                    UserCategory = null
                };
            }
        }

        if (isNewApp)
        {
            PersistNewDetection(settings);
        }

        return userCategory;
    }

    /// <summary>
    /// 新規アプリを検出したときに設定を永続化するためのコールバック (既定は null)。
    ///
    /// 本番では App.xaml.cs が起動時に SettingsManager.Instance.Save() を呼ぶ形で設定する。
    /// **ここを設定しない限り保存は行われない**ので、配線を消すと履歴が残らなくなる点に注意。
    ///
    /// なぜ WindowDetector が直接 SettingsManager.Instance を呼ばないのか:
    /// DetectCategory はテスト (WindowDetectorTests 等) から素の AppSettings を渡されて
    /// 直接呼ばれる。無条件に SettingsManager.Instance.Save() を呼ぶ設計にすると、
    /// Instance への初回アクセスが発生し、その private コンストラクタが実ユーザーの
    /// %AppData%\VoiceIn を作成してしまう。注入にすることで、テストは何も設定せずに
    /// 「実ファイルへ書き込まない」状態を保てる。
    /// テストが保存の呼び出し回数を検証する場合は一時的に差し替えてよいが、使用後は
    /// 必ず null に戻すこと (このアセンブリはテストの並列実行を無効化しているが、
    /// 後続のテストへ影響を残さないため)。
    /// </summary>
    internal static Action<AppSettings>? SaveSettingsCallback { get; set; }

    /// <summary>
    /// 新規アプリ検出時の保存を行う。SaveSettingsCallback が設定されていればそれを使う
    /// (テスト用)。既定 (null) の場合は、SettingsManager.Instance が既に初期化済みのときに
    /// 限って実際に SettingsManager.Instance.Save() を呼ぶ。
    ///
    /// なぜ「初期化済みのときに限って」なのか: SettingsManager.Instance への最初のアクセスは
    /// それ自体が %AppData%\VoiceIn を作成する副作用を持つ (SettingsManager のコンストラクタ
    /// 参照)。実アプリでは App.xaml.cs が起動時点・キー押下時点で既に何度も
    /// SettingsManager.Instance を参照しているため、DetectCategory が呼ばれる時点
    /// (キーを離した後) には確実に初期化済みであり、ここで実際に保存できる。
    /// 一方、素の AppSettings を直接構築してテストする既存/新規のテストは
    /// SettingsManager.Instance に一切触れないため、IsValueCreated は常に false のままとなり、
    /// ここでの保存は安全にスキップされる (実ファイルへは絶対に書き込まれない)。
    /// リフレクションに失敗した場合も安全側 (未初期化扱い = 保存しない) に倒す。
    /// </summary>
    private static void PersistNewDetection(AppSettings settings)
    {
        // 保存手段は「明示的に注入されたコールバックがあるときだけ」使う。
        // 本番では App.xaml.cs が起動時に SaveSettingsCallback を設定する。
        // テストは何も設定しないため、実ユーザーの %AppData%\VoiceIn へは決して書き込まれない。
        //
        // 以前はここで SettingsManager の private フィールドを reflection で覗き、
        // シングルトンが初期化済みかどうかで保存の可否を決めていた。しかしそれは
        // private フィールド名の文字列に依存するため、リネームされた時点で reflection が
        // 失敗し「本番で保存が無言のまま効かなくなる」という、気づけない壊れ方をする。
        // 明示的な注入に変えることで、配線がコード上で追えるようにしている。
        var save = SaveSettingsCallback;
        if (save == null)
        {
            return;
        }

        try
        {
            save(settings);
        }
        catch (Exception ex)
        {
            // 保存の失敗が検出処理自体をクラッシュさせないようにする。
            // (title_sample 等の機微情報を含みうるため、settings の内容はログへ出さない)
            Logger.Error("検出済みアプリ履歴の自動保存に失敗しました", ex);
        }
    }
}
