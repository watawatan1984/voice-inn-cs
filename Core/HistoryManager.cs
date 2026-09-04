using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceIn.Core;

public class HistoryItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>
    /// Text が空 (＝文字起こし失敗) の行かどうか。UI 表示専用の導出プロパティであり、
    /// history.json のスキーマを変えないよう JSON へは出力しない。
    /// </summary>
    [JsonIgnore]
    public bool IsError => string.IsNullOrEmpty(Text);

    /// <summary>
    /// 履歴一覧の「種別」列に表示する文字列。Text があれば "Text"、無ければ "Error"。
    /// </summary>
    [JsonIgnore]
    public string Kind => IsError ? "Error" : "Text";

    /// <summary>
    /// 履歴一覧の本文列・コピー時に使う表示用テキスト。
    /// Text が空の場合は Error の内容を代わりに表示する (空白行のまま失敗理由が
    /// わからなくなるのを防ぐ)。
    /// </summary>
    [JsonIgnore]
    public string DisplayText => IsError ? (Error ?? string.Empty) : Text;
}

public class HistoryPayload
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("items")]
    public List<HistoryItem> Items { get; set; } = [];
}

public class HistoryManager
{
    private static readonly Lazy<HistoryManager> _instance = new(() => new HistoryManager());
    public static HistoryManager Instance => _instance.Value;

    private const int MaxItems = 50;
    private readonly string _historyPath;
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private HistoryManager()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceIn", "history.json"))
    {
    }

    /// <summary>
    /// テスト用: 履歴ファイルの保存先パスを指定してインスタンスを作成する。
    /// Instance (シングルトン) はこのコンストラクタを private の既定コンストラクタ経由でのみ呼び出し、
    /// %AppData%\VoiceIn\history.json を渡す。挙動は本コンストラクタへの委譲前とまったく同じ
    /// (baseDir の作成 → history.json への結合) であり、Instance の動作は変えていない。
    ///
    /// このコンストラクタを internal で公開しているのは、テストから任意の一時ディレクトリを
    /// 指定してインスタンスを作れるようにするため。テストは HistoryManager.Instance
    /// (実ユーザーの %AppData%\VoiceIn を組み立ててしまう) には絶対に触れないこと。
    /// </summary>
    internal HistoryManager(string historyPath)
    {
        string? baseDir = Path.GetDirectoryName(historyPath);
        if (!string.IsNullOrEmpty(baseDir))
        {
            Directory.CreateDirectory(baseDir);
        }
        _historyPath = historyPath;
    }

    public List<HistoryItem> LoadItems()
    {
        if (!File.Exists(_historyPath))
        {
            return [];
        }

        try
        {
            string json = File.ReadAllText(_historyPath);
            var payload = JsonSerializer.Deserialize<HistoryPayload>(json);
            return payload?.Items ?? [];
        }
        catch
        {
            // 壊れたファイルを黙って空リストで上書きすると過去の履歴が警告なく全消失するため、
            // 読み込みに失敗したファイルは退避 (リネーム) してから空リストにフォールバックする。
            // 退避自体が失敗しても、履歴読み込みの失敗を外へ伝播させない (ベストエフォート)。
            QuarantineCorruptFile();
            return [];
        }
    }

    private void QuarantineCorruptFile()
    {
        try
        {
            if (!File.Exists(_historyPath))
            {
                return;
            }

            string? dir = Path.GetDirectoryName(_historyPath);
            string quarantinePath = Path.Combine(
                string.IsNullOrEmpty(dir) ? Path.GetTempPath() : dir,
                $"history.corrupt-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.json");

            // Move (Copy ではない) することで、壊れたファイルを元の場所から確実に取り除く。
            // これにより、以後 LoadItems() が呼ばれるたびに同じ壊れたファイルを何度も
            // 退避してしまう (quarantine ファイルが積み重なる) のを防ぐ。
            File.Move(_historyPath, quarantinePath);
        }
        catch
        {
            // ベストエフォート: 退避の失敗はテスト結果や本処理に影響させない。
        }
    }

    public void AppendItem(string text, string? error = null, string? provider = null)
    {
        string txt = text?.Trim() ?? string.Empty;
        string? err = string.IsNullOrWhiteSpace(error) ? null : error.Trim();

        if (string.IsNullOrEmpty(txt) && string.IsNullOrEmpty(err))
        {
            return;
        }

        var item = new HistoryItem
        {
            Id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            CreatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            Provider = provider ?? SettingsManager.Instance.CurrentProvider,
            Text = txt,
            Error = err
        };

        var items = LoadItems();
        items.Insert(0, item);

        if (items.Count > MaxItems)
        {
            items = items.GetRange(0, MaxItems);
        }

        SaveItems(items);
    }

    /// <summary>
    /// 指定した Id の履歴項目を1件削除する。該当する項目が無い場合は何もしない。
    /// </summary>
    public void DeleteItem(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        var items = LoadItems();
        int removed = items.RemoveAll(i => i.Id == id);
        if (removed > 0)
        {
            SaveItems(items);
        }
    }

    /// <summary>
    /// 履歴をすべて削除する。取り返しがつかない操作のため、呼び出し側 (UI) で
    /// 実行前に必ず確認を取ること。
    /// </summary>
    public void ClearAll()
    {
        SaveItems([]);
    }

    /// <summary>
    /// 履歴一覧を history.json へ書き込む共通経路。AppendItem / DeleteItem / ClearAll は
    /// すべてここを通ることで、一時ファイル経由のアトミック置換 (File.Move による overwrite)
    /// を共有する。書き込み中のクラッシュや電源断で history.json 自体が壊れないようにするため、
    /// File.WriteAllText で直接上書きすることは絶対にしない。
    /// </summary>
    private void SaveItems(List<HistoryItem> items)
    {
        string tempPath = _historyPath + $".tmp{Guid.NewGuid():N}";
        try
        {
            var payload = new HistoryPayload { Version = 1, Items = items };
            string json = JsonSerializer.Serialize(payload, _jsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _historyPath, overwrite: true);
        }
        catch (Exception ex)
        {
            // 例外メッセージ (ex.Message) はファイル I/O 失敗の理由 (権限不足・パス不正等) であり、
            // 履歴の本文 (items の Text/Error) は一切含まれない。プライバシー上安全にログ可能。
            Logger.Error("Failed to save history", ex);
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }
}
