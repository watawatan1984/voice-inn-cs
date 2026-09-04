using System.IO;
using System.Linq;
using VoiceIn.Core;
using Xunit;

namespace VoiceIn.Tests.Core;

/// <summary>
/// HistoryManager (一時ディレクトリ上のインスタンス) のテスト。
///
/// 【重要】HistoryManager.Instance は絶対に使用しないこと。実ユーザーの %AppData%\VoiceIn を
/// 直接組み立てて触ってしまう。本テストはすべて TempHistoryManager フィクスチャ経由で、
/// OS の一時フォルダ配下に作った使い捨てディレクトリ上のインスタンスに対して行う。
///
/// 【重要】AppendItem を呼ぶときは provider 引数を必ず明示的に渡すこと。省略 (null) すると
/// HistoryManager.AppendItem が SettingsManager.Instance.CurrentProvider にフォールバックし、
/// SettingsManager のシングルトンが %AppData%\VoiceIn\settings.json を作ってしまう。
/// </summary>
public class HistoryManagerTests
{
    private const string TestProvider = "test-provider";

    [Fact]
    public void AppendItem_ValidText_CanBeReadBackViaLoadItems()
    {
        using var temp = new TempHistoryManager();

        temp.Manager.AppendItem("こんにちは、音声入力のテストです。", provider: TestProvider);

        var items = temp.Manager.LoadItems();

        Assert.Single(items);
        Assert.Equal("こんにちは、音声入力のテストです。", items[0].Text);
        Assert.Equal(TestProvider, items[0].Provider);
        Assert.Null(items[0].Error);
        Assert.False(string.IsNullOrEmpty(items[0].Id));
    }

    [Fact]
    public void AppendItem_MultipleItems_NewestItemIsFirst()
    {
        using var temp = new TempHistoryManager();

        temp.Manager.AppendItem("1件目", provider: TestProvider);
        temp.Manager.AppendItem("2件目", provider: TestProvider);
        temp.Manager.AppendItem("3件目", provider: TestProvider);

        var items = temp.Manager.LoadItems();

        Assert.Equal(3, items.Count);
        Assert.Equal("3件目", items[0].Text);
        Assert.Equal("2件目", items[1].Text);
        Assert.Equal("1件目", items[2].Text);
    }

    [Fact]
    public void AppendItem_ExceedsMaxItems_TruncatesOldestAndKeepsNewestFirst()
    {
        using var temp = new TempHistoryManager();

        // HistoryManager.MaxItems (private const) は現状 50。上限を1件超える 51 件を追加し、
        // 最も古い1件だけが切り捨てられ、直近追加分が先頭に来ることを確認する。
        const int maxItems = 50;
        for (int i = 1; i <= maxItems + 1; i++)
        {
            temp.Manager.AppendItem($"item-{i}", provider: TestProvider);
        }

        var items = temp.Manager.LoadItems();

        Assert.Equal(maxItems, items.Count);
        Assert.Equal("item-51", items[0].Text);
        Assert.Equal("item-2", items[^1].Text);
        Assert.DoesNotContain(items, i => i.Text == "item-1");
    }

    [Fact]
    public void LoadItems_CorruptJson_QuarantinesFileAndReturnsEmptyList()
    {
        using var temp = new TempHistoryManager();
        const string corruptContent = "{ this is not valid json !!!";
        File.WriteAllText(temp.HistoryFilePath, corruptContent);

        var items = temp.Manager.LoadItems();

        Assert.Empty(items);

        // 壊れたファイルが元の場所から取り除かれている (Move であって Copy ではない)
        Assert.False(File.Exists(temp.HistoryFilePath));

        // 退避 (quarantine) ファイルが実際に作られ、壊れた内容がそのまま保全されていること
        var quarantineFiles = Directory.GetFiles(temp.DirectoryPath, "history.corrupt-*.json");
        Assert.Single(quarantineFiles);
        Assert.Equal(corruptContent, File.ReadAllText(quarantineFiles[0]));
    }

    [Fact]
    public void LoadItems_CorruptFileAlreadyQuarantined_DoesNotQuarantineRepeatedly()
    {
        using var temp = new TempHistoryManager();
        File.WriteAllText(temp.HistoryFilePath, "not json");

        // 1回目の LoadItems() で退避される
        temp.Manager.LoadItems();
        int countAfterFirst = Directory.GetFiles(temp.DirectoryPath, "history.corrupt-*.json").Length;
        Assert.Equal(1, countAfterFirst);

        // history.json は退避により既に存在しないため、以降の LoadItems() は
        // 「ファイルが無い」early-return となり、同じ壊れたファイルを何度も退避することはない
        temp.Manager.LoadItems();
        var itemsAfterThirdCall = temp.Manager.LoadItems();

        int countAfterMore = Directory.GetFiles(temp.DirectoryPath, "history.corrupt-*.json").Length;
        Assert.Equal(1, countAfterMore);
        Assert.Empty(itemsAfterThirdCall);
    }

    [Fact]
    public void DeleteItem_ExistingId_RemovesOnlyThatItem()
    {
        using var temp = new TempHistoryManager();
        temp.Manager.AppendItem("残す1", provider: TestProvider);
        temp.Manager.AppendItem("消す", provider: TestProvider);
        temp.Manager.AppendItem("残す2", provider: TestProvider);

        string targetId = temp.Manager.LoadItems().Single(i => i.Text == "消す").Id;
        temp.Manager.DeleteItem(targetId);

        var afterDelete = temp.Manager.LoadItems();
        Assert.Equal(2, afterDelete.Count);
        Assert.DoesNotContain(afterDelete, i => i.Text == "消す");
        Assert.Contains(afterDelete, i => i.Text == "残す1");
        Assert.Contains(afterDelete, i => i.Text == "残す2");
    }

    [Fact]
    public void DeleteItem_NonExistentId_DoesNotThrowAndLeavesItemsUnchanged()
    {
        using var temp = new TempHistoryManager();
        temp.Manager.AppendItem("そのまま残る", provider: TestProvider);

        var exception = Record.Exception(() => temp.Manager.DeleteItem("id-that-does-not-exist"));

        Assert.Null(exception);
        var items = temp.Manager.LoadItems();
        Assert.Single(items);
        Assert.Equal("そのまま残る", items[0].Text);
    }

    [Fact]
    public void DeleteItem_EmptyOrNullId_DoesNotThrowAndLeavesItemsUnchanged()
    {
        using var temp = new TempHistoryManager();
        temp.Manager.AppendItem("残る", provider: TestProvider);

        var exception = Record.Exception(() =>
        {
            temp.Manager.DeleteItem("");
            temp.Manager.DeleteItem(null!);
        });

        Assert.Null(exception);
        Assert.Single(temp.Manager.LoadItems());
    }

    [Fact]
    public void ClearAll_RemovesAllItems()
    {
        using var temp = new TempHistoryManager();
        temp.Manager.AppendItem("1", provider: TestProvider);
        temp.Manager.AppendItem("2", provider: TestProvider);
        temp.Manager.AppendItem("3", provider: TestProvider);

        temp.Manager.ClearAll();

        Assert.Empty(temp.Manager.LoadItems());
    }

    [Fact]
    public void AppendDeleteClearAll_EachWriteOperation_LeavesNoLeftoverTempFiles()
    {
        using var temp = new TempHistoryManager();

        // AppendItem / DeleteItem / ClearAll はすべて SaveItems (一時ファイル経由のアトミック置換)
        // を通る共通経路。3種類とも実行後に .tmp* ファイルが残っていないことを確認する。
        temp.Manager.AppendItem("1", provider: TestProvider);
        temp.Manager.AppendItem("2", provider: TestProvider);
        string idToDelete = temp.Manager.LoadItems()[0].Id;
        temp.Manager.DeleteItem(idToDelete);
        temp.Manager.ClearAll();

        var leftoverTempFiles = Directory.GetFiles(temp.DirectoryPath, "history.json.tmp*");
        Assert.Empty(leftoverTempFiles);

        // history.json 自体は最終状態 (ClearAll による空配列の保存結果) として残っている
        Assert.True(File.Exists(temp.HistoryFilePath));
    }

    [Fact]
    public void AppendItem_EmptyTextAndEmptyError_IsNotAdded()
    {
        using var temp = new TempHistoryManager();

        temp.Manager.AppendItem("", null, provider: TestProvider);
        temp.Manager.AppendItem("   ", "   ", provider: TestProvider); // 空白のみも空扱い (Trim/IsNullOrWhiteSpace)

        Assert.Empty(temp.Manager.LoadItems());
    }

    [Fact]
    public void AppendItem_EmptyTextWithError_IsAddedAsErrorItem()
    {
        using var temp = new TempHistoryManager();

        temp.Manager.AppendItem("", "文字起こしに失敗しました", provider: TestProvider);

        var items = temp.Manager.LoadItems();
        Assert.Single(items);
        Assert.Equal(string.Empty, items[0].Text);
        Assert.Equal("文字起こしに失敗しました", items[0].Error);
        Assert.True(items[0].IsError);
    }
}
