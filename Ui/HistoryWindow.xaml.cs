using System;
using System.Windows;
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
    }

    private void OnCopyText(object sender, RoutedEventArgs e)
    {
        if (GridHistory.SelectedItem is HistoryItem item && !string.IsNullOrEmpty(item.Text))
        {
            Clipboard.SetDataObject(item.Text, true);
            MessageBox.Show("テキストをクリップボードにコピーしました。", "Voice In", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("コピーする項目を選択してください。", "Voice In", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
