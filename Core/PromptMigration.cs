using System;
using System.Collections.Generic;

namespace VoiceIn.Core;

/// <summary>
/// 保存済み settings.json に残った「過去の既定プロンプト」を、ユーザーが行った独自編集を
/// 壊さずに現在の既定値へ追いつかせるための移行ロジック。
///
/// 【背景】Core/Settings.cs の既定プロンプト (例: マークダウン禁止ルールの追加) を改善しても、
/// SettingsManager.Load() は JSON にある値をそのままデシリアライズして上書きするだけであり、
/// Dictionary 系プロパティ向けの SettingsManager.FillMissingDictionaryDefaults のような
/// マージ処理は文字列プロパティには効かないため、既に settings.json を保存したことがある
/// ユーザーにはその改善が自動的には届かない。
///
/// 【判定方針】保存されている値が過去のいずれかの既定値と完全一致する場合のみ、
/// ユーザーは編集していないと判断して現在の既定値へ更新する。currentDefaults ともどの
/// 過去既定値とも一致しない場合は、ユーザー独自の編集とみなし絶対に上書きしない。
/// バージョン番号ではなく内容の完全一致で判定するため、settings.json 側にスキーマ変更
/// (バージョンフィールドの追加等) を加える必要がない。
///
/// 【改行コードの正規化について】比較は string.ReplaceLineEndings("\n") で改行コードを
/// \n に揃えた上で行う。理由: PromptSettings.GroqRefineSystemPrompt は C# の生文字列
/// リテラル ("""..."""、複数行) で保持しており、その値はソースファイル上の改行バイトを
/// そのまま引き継ぐ。このリポジトリの .cs ファイルは core.autocrlf=true の Windows 環境で
/// CRLF (\r\n) としてチェックアウトされるため、実際にビルド・配布されたアプリでは
/// Core/Settings.cs 側の既定値は CRLF 入りの値になっている可能性が高い。そのため
/// 実ユーザーの settings.json に保存されている値の改行コードが \r\n か \n かを断定できず、
/// 改行コードだけの違いで「ユーザー独自の編集」と誤判定してしまうと、この移行の目的
/// (既存ユーザーにも新しい既定値を届ける) を果たせない。改行コードの違いだけを許容し、
/// それ以外の文字は一切正規化しないため、内容面での判定の厳密さ (完全一致) は保たれる。
///
/// このクラスは SettingsManager.Instance / HistoryManager.Instance / Core.Logger など
/// 外部の状態には一切触れない (AppSettings を引数に取り AppSettings を書き換えるだけの
/// 静的メソッドのみで構成する)。これにより、実ユーザーの %AppData%\VoiceIn を一切作らずに
/// 単体テストできる。
///
/// 【将来また既定値を変えるときの手順】
/// 1. Core/Settings.cs 側のプロパティ初期値 (現在の既定値) を新しい内容に書き換える。
/// 2. 書き換える"前"に既定値として使われていた文字列を、このファイル内の対応する
///    Legacy*** 配列の末尾に追記する。既存の要素は絶対に削除しないこと
///    (既定値をこれまでに複数回変更している場合、ユーザーの settings.json には
///    そのどれか古い版がそのまま残っている可能性があるため)。
/// 3. 追記する文字列は必ず実際の Git 履歴からそのまま取得すること
///    (例: `git show <直前のコミット>:Core/Settings.cs` で変更前のファイル全体を表示し、
///    該当するプロパティ/辞書要素の値をそのままコピーする)。目視で書き写さないこと。
///    1 文字でも違うと (改行コードの違いを除き) 下記 MigrateValue の完全一致判定が
///    働かず、そのユーザーには新しい既定値が永久に届かない。改行コードの違いは
///    NormalizeLineEndings が吸収するため、生文字列リテラルを追記する際に LF/CRLF の
///    どちらで保存するかは気にしなくてよい。
/// 4. `dotnet test` を実行し、tests/VoiceIn.Tests/Core/PromptMigrationTests.cs を含む
///    既存テストがすべて通ることを確認する。
///
/// 【この一覧の由来】現時点で記録されている値は、整形プロンプトへマークダウン禁止ルールを
/// 追加したコミット 8c8e16e (refactor: 整形を Groq から切り離し、Gemini / NVIDIA に
/// 差し替え可能にする) の直前の版、すなわち `git show 8c8e16e^:Core/Settings.cs`
/// (= コミット f4f361d 時点の Core/Settings.cs) から取得した。
/// </summary>
public static class PromptMigration
{
    /// <summary>
    /// PromptSettings.GroqRefineSystemPrompt の過去の既定値一覧 (古い順)。
    /// 要素 [0] はマークダウン禁止ルール (「6. プレーンテキスト厳守」) を追加する前の版で、
    /// git show 8c8e16e^:Core/Settings.cs (= コミット f4f361d 時点) から取得した。
    /// 比較は NormalizeLineEndings 経由で改行コードを無視して行うため、この値自体の
    /// 改行コードが LF か CRLF かは問わない。
    /// </summary>
    private static readonly string[] LegacyGroqRefineSystemPrompts =
    [
        """
        あなたは優秀なテクニカルライターAIです。
        入力は音声認識テキストであり、「発音の曖昧さによる誤字」や「過剰なカタカナ表記」が含まれます。
        文脈を読み取り、以下の【絶対ルール】に従ってテキストを再構築してください。

        【絶対ルール】
        1. **脱カタカナ・英単語化**: IT用語、ソフトウェア名、コマンド名、ビジネス用語は、カタカナではなく**「本来の英単語（アルファベット）」**に変換してください。
           - (例: 「パイソン」→「Python」、「リナックス」→「Linux」、「ギットハブ」→「GitHub」、「ユーブイ」→「uv」、「アジュール」→「Azure」)
        2. **文脈補正**: 発音が悪くても、前後の文脈から推測して正しい専門用語に直してください。（例: 「スクリプト」と聞こえても文脈がPythonなら「script」と書く）
        3. **フィラー完全除去**: 「えー」「あー」「そのー」などの無意味な言葉は跡形もなく消してください。
        4. **自然な日本語**: 助詞（てにをは）を整え、です・ます調で統一した読みやすい文章にしてください。
        5. **出力のみ**: 修正後のテキストだけを出力すること。返事や挨拶は不要。
        """,
    ];

    /// <summary>
    /// AppSettings.CategoryPrompts の過去の既定値一覧 (カテゴリキーごと、古い順)。
    /// 各配列の要素 [0] は、マークダウン禁止ルールを追加する前の版 (由来は上と同じ)。
    /// このプロパティは通常の (\n エスケープを使う) 文字列リテラルであり、生文字列
    /// リテラルのような改行コードの問題は無い (ソースファイルの改行コードに影響されない)。
    /// </summary>
    private static readonly Dictionary<string, string[]> LegacyCategoryPrompts = new()
    {
        ["DEV"] =
        [
            "あなたは熟練のプログラマです。\n\n【指示】\n- 入力テキストをコードコメント、コミットメッセージ、または変数名として適切な形式に変換\n- 変数名は snake_case または camelCase を適用\n- ライブラリ名・コマンド名・専門用語は正しい英単語スペルに修正\n- 出力は極めて簡潔に",
        ],
        ["BIZ"] =
        [
            "あなたは優秀なビジネス秘書です。\n\n【指示】\n- 口語体を丁寧な「ビジネス敬語（です・ます調）」に変換\n- メールやチャットとして適切な形式に整形\n- 文脈に応じて適切な改行を挿入",
        ],
        ["DOC"] =
        [
            "あなたはプロのライター・編集者です。\n\n【指示】\n- 論理構成を整え、読みやすい「書き言葉」に変換\n- 必要であればMarkdown形式（箇条書き等）を使用\n- 文体を入力の雰囲気に合わせて統一",
        ],
        ["STD"] =
        [
            "あなたは優秀なテクニカルライターAIです。\n\n【指示】\n- フィラー（えー、あー）を完全に除去\n- IT用語・固有名詞は英単語化（カタカナ禁止）\n- 誤字脱字を修正\n- 自然な日本語の文章に整形"
        ],
    };

    /// <summary>
    /// 1回の移行実行結果。UpdatedKeys にはプロンプト本文を絶対に含めないこと
    /// (Core/Logger へそのまま渡せるよう、更新したキー名の一覧のみを保持する)。
    /// プロンプトにはユーザーが業務上の固有名詞等を書き込んでいる可能性があるため。
    /// </summary>
    public readonly struct MigrationResult
    {
        public MigrationResult(bool changed, IReadOnlyList<string> updatedKeys)
        {
            Changed = changed;
            UpdatedKeys = updatedKeys;
        }

        /// <summary>1件以上のプロンプトを移行した場合 true。false ならば settings.json への書き戻しは不要。</summary>
        public bool Changed { get; }

        /// <summary>
        /// 移行したプロンプトのキー名一覧 (例: "prompts.groq_refine_system_prompt",
        /// "category_prompts.DEV")。プロンプト本文は含まない。
        /// </summary>
        public IReadOnlyList<string> UpdatedKeys { get; }
    }

    /// <summary>
    /// SettingsManager.Load() がデシリアライズした直後の設定 (loaded) のうち、
    /// 「過去の既定値から一切編集されていない」プロンプトだけを currentDefaults の値へ
    /// 書き換える。ユーザーが独自に編集した値 (currentDefaults ともどの過去既定値とも
    /// 一致しない値) は絶対に変更しない。
    ///
    /// 純粋関数: 引数として渡された loaded / currentDefaults 以外の状態を一切参照・変更
    /// しない (SettingsManager.Instance や Core.Logger など外部の状態には触れない)。
    /// そのためシングルトンを一切経由せずに単体テストできる。
    ///
    /// loaded 自体は破壊的に書き換える (SettingsManager.FillMissingDictionaryDefaults と
    /// 同じ流儀。呼び出し側である SettingsManager.Load() がデシリアライズ直後のまだ
    /// どこにも公開していない一時オブジェクトに対して呼ぶことを想定している)。
    /// </summary>
    public static MigrationResult ApplyLegacyDefaults(AppSettings loaded, AppSettings currentDefaults)
    {
        var updatedKeys = new List<string>();

        loaded.Prompts ??= new PromptSettings();

        var (newGroqPrompt, groqUpdated) = MigrateValue(
            loaded.Prompts.GroqRefineSystemPrompt,
            currentDefaults.Prompts.GroqRefineSystemPrompt,
            LegacyGroqRefineSystemPrompts);
        loaded.Prompts.GroqRefineSystemPrompt = newGroqPrompt;
        if (groqUpdated)
        {
            updatedKeys.Add("prompts.groq_refine_system_prompt");
        }

        loaded.CategoryPrompts ??= [];
        foreach (var (category, legacyValues) in LegacyCategoryPrompts)
        {
            // 保存データ側にそのカテゴリ自体が無い場合、SettingsManager.FillMissingDictionaryDefaults
            // が既に現在の既定値で補完済みのはず (Load() 内で本メソッドより先に呼ばれる想定) だが、
            // 呼び出し順に依存しないよう念のためガードしておく。
            if (!loaded.CategoryPrompts.TryGetValue(category, out var savedValue))
            {
                continue;
            }

            if (!currentDefaults.CategoryPrompts.TryGetValue(category, out var currentDefaultValue))
            {
                continue;
            }

            var (newValue, updated) = MigrateValue(savedValue, currentDefaultValue, legacyValues);
            loaded.CategoryPrompts[category] = newValue;
            if (updated)
            {
                updatedKeys.Add($"category_prompts.{category}");
            }
        }

        return new MigrationResult(updatedKeys.Count > 0, updatedKeys);
    }

    /// <summary>
    /// 1件のプロンプト値について、更新すべきかどうかを判定して結果を返す純粋関数。
    ///   - saved が既に currentDefault と同じ (改行コード違いのみの場合を含む)
    ///       → 変更なし (Updated=false)
    ///   - saved が過去の既定値のいずれかと完全一致 (改行コード違いのみの場合を含む)
    ///       → ユーザー未編集と判断し currentDefault (元の改行コードのまま) へ更新
    ///   - それ以外 (ユーザー独自の編集、または未知の値)
    ///       → 絶対に上書きせず saved を維持
    ///
    /// internal (AssemblyInfo.cs の InternalsVisibleTo により VoiceIn.Tests から参照可能) に
    /// しているのは、この判定ロジック単体を AppSettings を介さず直接テストできるようにするため。
    /// 副作用・外部状態への依存が一切ない純粋関数。
    /// </summary>
    internal static (string Value, bool Updated) MigrateValue(
        string saved,
        string currentDefault,
        IReadOnlyList<string> legacyDefaults)
    {
        if (NormalizeLineEndings(saved) == NormalizeLineEndings(currentDefault))
        {
            return (saved, false);
        }

        foreach (var legacy in legacyDefaults)
        {
            if (NormalizeLineEndings(saved) == NormalizeLineEndings(legacy))
            {
                return (currentDefault, true);
            }
        }

        return (saved, false);
    }

    /// <summary>
    /// 改行コードを \n に揃える。PromptSettings.GroqRefineSystemPrompt が C# の生文字列
    /// リテラルで保持されているため、その値の改行コードはソースファイル (Core/Settings.cs や
    /// このファイル) の改行コードにそのまま左右される (このリポジトリは core.autocrlf=true の
    /// Windows 環境で CRLF としてチェックアウトされる)。実ユーザーの settings.json が
    /// どちらの改行コードで保存されているか断定できないため、比較の直前でこの正規化を
    /// 挟むことで、改行コードの違いだけでは「ユーザー独自の編集」と誤判定しないようにする。
    /// 改行コード以外の文字は一切変更しないため、内容面での完全一致判定の厳密さは保たれる。
    /// </summary>
    private static string NormalizeLineEndings(string value) => value.ReplaceLineEndings("\n");
}
