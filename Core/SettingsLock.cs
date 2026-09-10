namespace VoiceIn.Core;

/// <summary>
/// <see cref="AppSettings"/> の <c>Dictionary</c> (単語置換辞書) / <c>AppCategories</c>
/// (アプリ判定カテゴリとキーワード) / <c>CategoryPrompts</c> (カテゴリ別プロンプト) への
/// 同時アクセスを保護するためのロックオブジェクト。
///
/// これら 3 つの辞書は、UI スレッド (Ui/SettingsWindow.OnSaveAndApply での保存処理) と
/// バックグラウンドスレッド (App.xaml.cs の Task.Run 内で行う文字起こし処理、および
/// Core/WindowDetector.DetectCategory によるカテゴリ判定) の両方から参照される。
///
/// 書き込み側・読み取り側の**両方**が、このロックを必ず取ってから対象の辞書へアクセスすること。
/// 片方でも欠けると、保存の前後で新旧の値が混在して読まれる可能性が残る。
///
/// 更新側は、ロックの外で新しい Dictionary/List を組み立ててから、ロック内では参照の
/// 差し替えのみを行う方式を取っている (詳細は Ui/SettingsWindow.OnSaveAndApply 参照)。
/// これにより、このロックの中で行う作業は常に短時間 (参照代入・小さな列挙やコピー) で済み、
/// ネットワーク I/O・ファイル I/O・await・Dispatcher.Invoke 等の時間のかかる処理を
/// ロックを保持したまま行うことは絶対にない。
///
/// もともと App.xaml.cs の internal static な DictionaryLock として定義されていたが、
/// Core 層 (WindowDetector) から UI 層 (App) を参照する逆転した依存関係になってしまうため、
/// 依存の向きが正しくなるよう Core 層であるこのクラスへ移動した。
/// </summary>
internal static class SettingsLock
{
    /// <summary>
    /// <see cref="AppSettings"/> の Dictionary / AppCategories / CategoryPrompts を
    /// 保護するロックオブジェクト。lock (SettingsLock.Gate) の形で使用する。
    /// </summary>
    internal static readonly object Gate = new();
}
