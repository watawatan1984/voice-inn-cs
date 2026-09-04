using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using GdiColor = System.Drawing.Color;

namespace VoiceIn.Ui;

/// <summary>
/// タスクトレイアイコンを状態 (idle/recording/processing/success/error) ごとに
/// 色分けして生成・キャッシュするクラス。
///
/// 配色は <see cref="OverlayWindow.SetState"/> が使用している色 (RGB) に揃えてある。
/// オーバーレイとトレイで色が食い違うと混乱するため。idle はプロバイダ (Gemini/Groq)
/// によって色が変わるため、状態名とは別に "idle-gemini" / "idle-groq" のキーを持つ。
///
/// 色の型について:
/// GlobalUsings.cs で `global using Color = System.Windows.Media.Color;` が
/// プロジェクト全体に定義されているため、本ファイルでは System.Drawing.Color を
/// "GdiColor" というエイリアスで参照する (`using Color = System.Drawing.Color;` は
/// 同名エイリアスの二重定義になり CS1537 でビルドエラーになるため使えない)。
///
/// GDI ハンドルリークについて:
/// Bitmap.GetHicon() は呼び出すたびに新しい GDI HICON を生成するが、Icon.FromHandle() は
/// そのハンドルを包むだけで所有権を持たない (Icon.Dispose() は自分が所有していない
/// ハンドルを破棄しない)。そのため状態変化のたびに素朴に GetHicon() すると、
/// 常駐アプリでは確実にハンドルリークになる。
///
/// 本クラスは起動時 (Initialize) に全状態ぶんのアイコンを一度だけ生成してキャッシュし、
/// 以後は状態変化のたびに同じ Icon インスタンスを使い回す。生成時に使った一時 HICON は
/// その場で DestroyIcon して確実に破棄し、代わりに Save→MemoryStream 経由で読み直した
/// (GDI ハンドルを自前で所有する) 独立した Icon をキャッシュに入れる。これにより
/// アイコン生成そのものが起動時の有限回で完結し、実行中にハンドルが増え続けることはない。
/// アプリ終了時は <see cref="DisposeAll"/> でキャッシュ済みアイコンをまとめて破棄すること。
/// </summary>
internal static class TrayIcons
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const int IconSize = 32;

    private static readonly object Lock = new();
    private static readonly Dictionary<string, Icon> Cache = new();
    private static bool _initialized;

    /// <summary>
    /// 全状態ぶんのアイコンを一度だけ生成してキャッシュする。
    /// App.OnStartup (UI スレッド) から呼び出すこと。二度目以降の呼び出しは何もしない。
    /// </summary>
    public static void Initialize()
    {
        lock (Lock)
        {
            if (_initialized)
            {
                return;
            }

            foreach (string key in new[] { "recording", "processing", "success", "error", "idle-gemini", "idle-groq" })
            {
                Cache[key] = CreateIcon(key);
            }

            _initialized = true;
        }
    }

    /// <summary>
    /// 状態 (state) とプロバイダ名 (provider) に対応するキャッシュ済みアイコンを取得する。
    /// state が "idle" の場合のみ provider を見て色を切り替える。
    /// Initialize が未実行でもここで初期化するため (呼び出し漏れに対する保険)、
    /// 単独で呼び出しても安全。
    /// </summary>
    public static Icon GetIcon(string state, string provider)
    {
        Initialize();

        string key = NormalizeKey(state, provider);
        lock (Lock)
        {
            return Cache.TryGetValue(key, out Icon? icon) ? icon : Cache["idle-gemini"];
        }
    }

    /// <summary>
    /// キャッシュ済みアイコンをすべて破棄する。App.OnExit から呼び出すこと。
    /// </summary>
    public static void DisposeAll()
    {
        lock (Lock)
        {
            foreach (Icon icon in Cache.Values)
            {
                icon.Dispose();
            }
            Cache.Clear();
            _initialized = false;
        }
    }

    private static string NormalizeKey(string state, string provider)
    {
        string normalizedState = state.ToLowerInvariant();
        if (normalizedState != "idle")
        {
            return normalizedState;
        }

        // OverlayWindow.SetState の idle 分岐と同じ判定 (groq ならオレンジ、それ以外は gemini の青)。
        return provider.ToLowerInvariant() == "groq" ? "idle-groq" : "idle-gemini";
    }

    private static Icon CreateIcon(string key)
    {
        (GdiColor top, GdiColor bottom, GdiColor border) = GetColors(key);

        using var bitmap = new Bitmap(IconSize, IconSize);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(GdiColor.Transparent);

            var circleRect = new Rectangle(2, 2, IconSize - 4, IconSize - 4);
            using (var fillBrush = new LinearGradientBrush(circleRect, top, bottom, LinearGradientMode.Vertical))
            {
                g.FillEllipse(fillBrush, circleRect);
            }
            using (var borderPen = new Pen(border, 3f))
            {
                g.DrawEllipse(borderPen, circleRect);
            }
        }

        // GetHicon() が返す一時ハンドル (hIcon) は、Save() で ICO 形式のバイト列として
        // MemoryStream に書き出したあと、その場で DestroyIcon して破棄する。
        // 戻り値の Icon は stream から生成しており GDI ハンドルを自前で所有するため、
        // 通常どおり Dispose() すれば正しく解放される。
        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using Icon tempIcon = Icon.FromHandle(hIcon);
            using var stream = new MemoryStream();
            tempIcon.Save(stream);
            stream.Seek(0, SeekOrigin.Begin);
            return new Icon(stream);
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static (GdiColor top, GdiColor bottom, GdiColor border) GetColors(string key)
    {
        // 各色は Ui/OverlayWindow.xaml.cs の SetState と同じ RGB 値を使用する。
        // アルファ値だけは、32x32 の小さなトレイアイコンでは半透明だと視認性が落ちるため
        // 255 (不透明) に統一している。
        return key switch
        {
            // recording: 深紅
            "recording" => (GdiColor.FromArgb(220, 20, 60), GdiColor.FromArgb(180, 10, 40), GdiColor.FromArgb(255, 107, 107)),
            // processing: 琥珀/黄
            "processing" => (GdiColor.FromArgb(255, 193, 7), GdiColor.FromArgb(230, 170, 0), GdiColor.FromArgb(255, 217, 61)),
            // success: 緑
            "success" => (GdiColor.FromArgb(46, 204, 113), GdiColor.FromArgb(35, 160, 90), GdiColor.FromArgb(74, 222, 128)),
            // error: 暗赤
            "error" => (GdiColor.FromArgb(176, 0, 32), GdiColor.FromArgb(140, 0, 20), GdiColor.FromArgb(239, 68, 68)),
            // idle (Groq): オレンジ
            "idle-groq" => (GdiColor.FromArgb(60, 60, 60), GdiColor.FromArgb(40, 40, 40), GdiColor.FromArgb(245, 80, 54)),
            // idle (Gemini, デフォルト): 青
            _ => (GdiColor.FromArgb(60, 60, 60), GdiColor.FromArgb(40, 40, 40), GdiColor.FromArgb(66, 133, 244)),
        };
    }
}
