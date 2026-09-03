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
    {
        string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceIn");
        Directory.CreateDirectory(baseDir);
        _historyPath = Path.Combine(baseDir, "history.json");
    }

    public List<HistoryItem> LoadItems()
    {
        try
        {
            if (!File.Exists(_historyPath))
            {
                return [];
            }

            string json = File.ReadAllText(_historyPath);
            var payload = JsonSerializer.Deserialize<HistoryPayload>(json);
            return payload?.Items ?? [];
        }
        catch
        {
            return [];
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

        try
        {
            var payload = new HistoryPayload { Version = 1, Items = items };
            string json = JsonSerializer.Serialize(payload, _jsonOptions);
            File.WriteAllText(_historyPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save history: {ex.Message}");
        }
    }
}
