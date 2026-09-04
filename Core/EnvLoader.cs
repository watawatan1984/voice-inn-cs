using System;
using System.Collections.Generic;
using System.IO;

namespace VoiceIn.Core;

public static class EnvLoader
{
    /// <summary>
    /// Load() が実際に読み込んだ .env ファイルのフルパス。
    /// 候補パスのいずれにもファイルが存在せず、読み込みが行われなかった場合は null。
    /// </summary>
    public static string? LoadedFilePath { get; private set; }

    /// <summary>
    /// ポータブルモードかどうかを判定する。環境変数 VOICEIN_PORTABLE の値がちょうど "1" の
    /// ときのみ true を返す (移植元 Python 版 src/core/utils.py の get_config_dir/get_state_dir
    /// と同じ判定基準)。未設定・"0"・"true" 等それ以外の値はすべて false (=通常モード) となり、
    /// 既定の挙動は変わらない。
    /// </summary>
    internal static bool IsPortableMode() =>
        Environment.GetEnvironmentVariable("VOICEIN_PORTABLE") == "1";

    /// <summary>
    /// 設定 (settings.json) ・履歴 (history.json) ・ログ (app.log) ・.env の保存先ディレクトリを
    /// 返す共通ヘルパー。SettingsManager / HistoryManager / Logger / EnvLoader (本クラス自身の
    /// GetWritableFilePath) はすべてこのメソッド経由でディレクトリを決定すること。
    /// 同じ判定を複数箇所へ個別にコピーしないため、ここに一元化している。
    ///
    /// ・ポータブルモード (IsPortableMode() が true) のときは実行ファイルと同じディレクトリ
    ///   (AppDomain.CurrentDomain.BaseDirectory) を返す。
    /// ・それ以外 (既定・環境変数未設定時) は、従来と全く同じ %AppData%\VoiceIn を返す。
    ///   既存ユーザーのデータ (settings.json / history.json / .env) はそのまま読まれ続ける。
    ///
    /// ディレクトリの作成 (Directory.CreateDirectory) はしない。呼び出し側の責務とする。
    /// </summary>
    internal static string GetAppDataDirectory()
    {
        return IsPortableMode()
            ? AppDomain.CurrentDomain.BaseDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceIn");
    }

    public static void Load(string? customPath = null)
    {
        string[] candidatePaths = customPath != null
            ? [customPath]
            : [
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env"),
                Path.Combine(Directory.GetCurrentDirectory(), ".env"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceIn", ".env")
            ];

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                LoadFile(path);
                LoadedFilePath = path;
                return;
            }
        }
    }

    /// <summary>
    /// .env への書き戻し先パスを返す。Load() で実際に読み込んだファイルがあればそのパス、
    /// どの候補にも .env が存在せず読み込みが行われなかった場合は、新規作成先として
    /// GetAppDataDirectory()\.env を返す。
    /// ・通常モード (既定): %AppData%\VoiceIn\.env (Environment.SpecialFolder.ApplicationData 配下、
    ///   ユーザー単位で保護される場所)。
    /// ・ポータブルモード (VOICEIN_PORTABLE=1): 実行ファイルと同じディレクトリ\.env。
    /// </summary>
    public static string GetWritableFilePath()
    {
        return LoadedFilePath ?? Path.Combine(GetAppDataDirectory(), ".env");
    }

    /// <summary>
    /// 指定したキーの値を .env ファイルへ書き戻す。
    /// 既存の行・コメント・空行・順序はすべてそのまま保持し、対象キーの行だけを置き換える
    /// (対象キーの行が無い場合は末尾に追加する)。他のキーの行 (GEMINI_API_KEY /
    /// GROQ_API_KEY を含む) の内容は一切変更しない。
    ///
    /// GEMINI_API_KEY / GROQ_API_KEY のような API キー自体の書き込みにも使用できる
    /// (設定画面の API キー入力欄の保存処理から呼び出される)。このファイルには
    /// API キーが含まれうるため、以下を厳守する:
    ///   ・書き込みは一時ファイル経由のアトミック置換 (File.Move の overwrite) で行う
    ///   ・失敗しても例外は外へ投げない (設定切替自体を失敗させないため)
    ///   ・value (API キーを含みうる) や既存ファイルの内容を例外メッセージ・ログ・
    ///     コンソール出力へ一切出力しない
    ///   ・"export KEY=value" のように export 接頭辞が付いている行は、その接頭辞を
    ///     保持したまま値だけを置き換える
    /// </summary>
    public static bool TryWriteKey(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        string path = GetWritableFilePath();
        string tempPath = path + $".tmp{Guid.NewGuid():N}";

        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            List<string> lines = [];
            if (File.Exists(path))
            {
                lines.AddRange(File.ReadAllLines(path));
            }

            bool replaced = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                int eqIndex = trimmed.IndexOf('=');
                if (eqIndex <= 0)
                {
                    continue;
                }

                string existingKey = trimmed[..eqIndex].Trim();
                string exportPrefix = string.Empty;
                if (existingKey.StartsWith("export", StringComparison.Ordinal) &&
                    existingKey.Length > "export".Length &&
                    char.IsWhiteSpace(existingKey["export".Length]))
                {
                    exportPrefix = "export ";
                    existingKey = existingKey["export".Length..].TrimStart();
                }

                if (string.Equals(existingKey, key, StringComparison.Ordinal))
                {
                    // 対象キーの行だけを置き換える。他の行 (API キー行含む) には触れない。
                    lines[i] = $"{exportPrefix}{key}={value}";
                    replaced = true;
                    break;
                }
            }

            if (!replaced)
            {
                lines.Add($"{key}={value}");
            }

            File.WriteAllLines(tempPath, lines);
            File.Move(tempPath, path, overwrite: true);
            return true;
        }
        catch
        {
            // .env への書き戻しに失敗しても、設定切り替え自体は継続させる。
            // API キーを含みうるファイルのため、例外の詳細はログへ一切出力しない。
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            return false;
        }
    }

    private static void LoadFile(string filePath)
    {
        foreach (var rawLine in File.ReadAllLines(filePath))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            int eqIndex = line.IndexOf('=');
            if (eqIndex <= 0)
            {
                continue;
            }

            string key = line[..eqIndex].Trim();
            string val = line[(eqIndex + 1)..].Trim();

            // "export KEY=value" 形式に対応するため、キーの export 接頭辞を除去する。
            // "export" の直後が空白文字の場合のみ接頭辞とみなす (例: "exported_flag" のような
            // キー名を誤って "ed_flag" に切り詰めないため)。
            if (key.StartsWith("export", StringComparison.Ordinal) &&
                key.Length > "export".Length &&
                char.IsWhiteSpace(key["export".Length]))
            {
                key = key["export".Length..].TrimStart();
            }

            // クォーテーション除去
            bool isQuoted = false;
            if ((val.StartsWith('"') && val.EndsWith('"')) || (val.StartsWith('\'') && val.EndsWith('\'')))
            {
                if (val.Length >= 2)
                {
                    val = val[1..^1];
                    isQuoted = true;
                }
            }

            // クォートされていない値に限り、半角スペース+# 以降を行末コメントとして切り捨てる。
            // クォートで囲まれた値の中の # はそのまま保持する。
            if (!isQuoted)
            {
                int commentIndex = val.IndexOf(" #", StringComparison.Ordinal);
                if (commentIndex >= 0)
                {
                    val = val[..commentIndex].TrimEnd();
                }
            }

            Environment.SetEnvironmentVariable(key, val);
        }
    }
}
