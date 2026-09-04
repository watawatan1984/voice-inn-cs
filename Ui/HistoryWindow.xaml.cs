using System;
using System.Windows;
using System.Windows.Controls;
using VoiceIn.Core;

namespace VoiceIn.Ui;

public partial class HistoryWindow : Window
{
    public HistoryWindow()
    {
        InitializeComponent();
        Loaded += (s, e) => LoadHistory();
    }

    private void LoadHistory()
    {
        var items = HistoryManager.Instance.LoadItems();
        GridHistory.ItemsSource = items;
        GridHistory.Items.Filter = FilterItem;
        UpdateEmptyState();
    }

    /// <summary>
    /// 検索ボックスの内容で履歴一覧を絞り込む。テキスト本文だけでなく、
    /// エラー内容・プロバイダ名も検索対象に含める (大文字小文字は区別しない)。
    /// </summary>
    private bool FilterItem(object obj)
    {
        if (obj is not HistoryItem item)
        {
            return false;
        }

        string query = TxtSearch.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        return ContainsIgnoreCase(item.Text, query)
            || ContainsIgnoreCase(item.Error, query)
            || ContainsIgnoreCase(item.Provider, query);
    }

    private static bool ContainsIgnoreCase(string? source, string query)
    {
        return !string.IsNullOrEmpty(source) && source.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        GridHistory.Items.Refresh();
        UpdateEmptyState();
    }

    /// <summary>
    /// 絞り込み結果が0件のときに「該当なし」のメッセージを表示する。
    /// </summary>
    private void UpdateEmptyState()
    {
        TxtEmpty.Visibility = GridHistory.Items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnCopyText(object sender, RoutedEventArgs e)
    {
        if (GridHistory.SelectedItem is HistoryItem item && !string.IsNullOrEmpty(item.DisplayText))
        {
            // Text が空 (エラー行) の場合は DisplayText が Error の内容にフォールバックする。
            Clipboard.SetDataObject(item.DisplayText, true);
            MessageBox.Show("テキストをクリップボードにコピーしました。", "Voice In", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("コピーする項目を選択してください。", "Voice In", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDeleteSelected(object sender, RoutedEventArgs e)
    {
        if (GridHistory.SelectedItem is not HistoryItem item)
        {
            MessageBox.Show("削除する項目を選択してください。", "Voice In", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        HistoryManager.Instance.DeleteItem(item.Id);
        LoadHistory();
    }

    private void OnDeleteAll(object sender, RoutedEventArgs e)
    {
        // 全削除は取り返しがつかないため、既定ボタンを「いいえ」にして誤操作を防ぐ。
        var result = MessageBox.Show(
            "履歴をすべて削除します。この操作は取り消せません。よろしいですか?",
            "Voice In",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        HistoryManager.Instance.ClearAll();
        LoadHistory();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
