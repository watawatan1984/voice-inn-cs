using System;
using VoiceIn.Core;
using Xunit;

namespace VoiceIn.Tests.Core;

/// <summary>
/// Core/PromptMigration.cs (保存済み設定に残った過去の既定プロンプトを、ユーザーの編集を
/// 壊さずに現在の既定値へ追いつかせる移行ロジック) のテスト。
///
/// このテストファイルの Legacy*** フィールドは、PromptMigration.cs 内部の同名の配列とは
/// 別に、このファイル自身が独立して git show 8c8e16e^:Core/Settings.cs
/// (マークダウン禁止ルールを追加する直前のコミット時点の Core/Settings.cs) から取得し
/// 直したものである。どちらか一方でも取得を誤っていれば、旧既定値との完全一致を前提と
/// する下記テストが失敗するため、本テストは PromptMigration.cs 側の値が実際の Git 履歴
/// と一致していることの検証も兼ねている。
///
/// SettingsManager.Instance / HistoryManager.Instance / Core.Logger には一切触れない
/// (これらはシングルトンの初回アクセス時に実ユーザーの %AppData%\VoiceIn を作ってしまう
/// ため)。PromptMigration.ApplyLegacyDefaults と PromptMigration.MigrateValue はどちらも
/// AppSettings や文字列だけを引数に取る純粋関数であり、シングルトンを一切経由せずに
/// 直接呼び出してテストできる。
/// </summary>
public class PromptMigrationTests
{
    // マークダウン禁止ルールを追加する前 (コミット 8c8e16e の直前) の GroqRefineSystemPrompt。
    private static readonly string[] LegacyGroqRefineSystemPrompt =
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

    private static readonly string[] LegacyDevPrompt =
    [
        "あなたは熟練のプログラマです。\n\n【指示】\n- 入力テキストをコードコメント、コミットメッセージ、または変数名として適切な形式に変換\n- 変数名は snake_case または camelCase を適用\n- ライブラリ名・コマンド名・専門用語は正しい英単語スペルに修正\n- 出力は極めて簡潔に",
    ];

    private static readonly string[] LegacyBizPrompt =
    [
        "あなたは優秀なビジネス秘書です。\n\n【指示】\n- 口語体を丁寧な「ビジネス敬語（です・ます調）」に変換\n- メールやチャットとして適切な形式に整形\n- 文脈に応じて適切な改行を挿入",
    ];

    private static readonly string[] LegacyDocPrompt =
    [
        "あなたはプロのライター・編集者です。\n\n【指示】\n- 論理構成を整え、読みやすい「書き言葉」に変換\n- 必要であればMarkdown形式（箇条書き等）を使用\n- 文体を入力の雰囲気に合わせて統一",
    ];

    private static readonly string[] LegacyStdPrompt =
    [
        "あなたは優秀なテクニカルライターAIです。\n\n【指示】\n- フィラー（えー、あー）を完全に除去\n- IT用語・固有名詞は英単語化（カタカナ禁止）\n- 誤字脱字を修正\n- 自然な日本語の文章に整形"
    ];

    private static string GetLegacyCategoryPrompt(string category) => category switch
    {
        "DEV" => LegacyDevPrompt[0],
        "BIZ" => LegacyBizPrompt[0],
        "DOC" => LegacyDocPrompt[0],
        "STD" => LegacyStdPrompt[0],
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "未対応のカテゴリ"),
    };

    [Fact]
    public void ApplyLegacyDefaults_SavedMatchesLegacyGroqPrompt_UpdatesToCurrentDefault()
    {
        var currentDefaults = new AppSettings();
        var loaded = new AppSettings();
        loaded.Prompts.GroqRefineSystemPrompt = LegacyGroqRefineSystemPrompt[0];

        var result = PromptMigration.ApplyLegacyDefaults(loaded, currentDefaults);

        Assert.Equal(currentDefaults.Prompts.GroqRefineSystemPrompt, loaded.Prompts.GroqRefineSystemPrompt);
        Assert.True(result.Changed);
        Assert.Contains("prompts.groq_refine_system_prompt", result.UpdatedKeys);
    }

    [Fact]
    public void ApplyLegacyDefaults_SavedIsUserCustomGroqPrompt_DoesNotOverwrite()
    {
        var currentDefaults = new AppSettings();
        var loaded = new AppSettings();
        const string userPrompt = "私が独自に書いた整形プロンプトです。絶対に上書きされてはいけません。";
        loaded.Prompts.GroqRefineSystemPrompt = userPrompt;

        var result = PromptMigration.ApplyLegacyDefaults(loaded, currentDefaults);

        Assert.Equal(userPrompt, loaded.Prompts.GroqRefineSystemPrompt);
        Assert.False(result.Changed);
        Assert.DoesNotContain("prompts.groq_refine_system_prompt", result.UpdatedKeys);
    }

    [Fact]
    public void ApplyLegacyDefaults_SavedAlreadyCurrentGroqPrompt_NoOp()
    {
        var currentDefaults = new AppSettings();
        var loaded = new AppSettings();

        var result = PromptMigration.ApplyLegacyDefaults(loaded, currentDefaults);

        Assert.Equal(currentDefaults.Prompts.GroqRefineSystemPrompt, loaded.Prompts.GroqRefineSystemPrompt);
        Assert.False(result.Changed);
        Assert.Empty(result.UpdatedKeys);
    }

    // ================= 改行コード (CRLF/LF) の違いを吸収することの回帰テスト =================
    //
    // 【背景】PromptSettings.GroqRefineSystemPrompt は C# の生文字列リテラル ("""..."""、
    // 複数行) で保持されており、その値の改行コードはソースファイル上の改行バイトを
    // そのまま引き継ぐ。実際に Core/Settings.cs をこのリポジトリの動作 (core.autocrlf=true
    // の Windows 環境で .cs ファイルが CRLF としてチェックアウトされる) の下でビルドすると、
    // new AppSettings().Prompts.GroqRefineSystemPrompt は \r\n 入りの値になることを、
    // 実装時に一時的な診断テストで実際に確認している。つまり実ユーザーの settings.json
    // (過去のビルドが生成した既定値がそのまま保存されたもの) も \r\n 入りである可能性が高い。
    // 一方このテストファイル自身の LegacyGroqRefineSystemPrompt フィクスチャは git の
    // blob (常に LF) から取得しているため \n のみであり、素の完全一致では絶対に
    // マッチしない。PromptMigration.MigrateValue が NormalizeLineEndings で改行コードを
    // 揃えてから比較していることを、ここで直接検証する。

    [Fact]
    public void ApplyLegacyDefaults_SavedMatchesLegacyGroqPromptButWithCrlfLineEndings_StillUpdatesToCurrentDefault()
    {
        var currentDefaults = new AppSettings();
        var loaded = new AppSettings();
        // このテストファイルのフィクスチャは LF のみだが、実際に保存されている可能性が高い
        // CRLF 版を模して変換する (ファイルに直接 CRLF を書き込むと環境依存で壊れやすいため、
        // 実行時に文字列操作で作る)。
        loaded.Prompts.GroqRefineSystemPrompt = LegacyGroqRefineSystemPrompt[0].Replace("\n", "\r\n");

        var result = PromptMigration.ApplyLegacyDefaults(loaded, currentDefaults);

        Assert.Equal(currentDefaults.Prompts.GroqRefineSystemPrompt, loaded.Prompts.GroqRefineSystemPrompt);
        Assert.True(result.Changed);
        Assert.Contains("prompts.groq_refine_system_prompt", result.UpdatedKeys);
    }

    [Fact]
    public void ApplyLegacyDefaults_SavedEqualsCurrentDefaultExceptLineEndings_TreatedAsNoOpAndLeftUntouched()
    {
        var currentDefaults = new AppSettings();
        var loaded = new AppSettings();
        // 現在の既定値と「改行コードだけ」が違う値。内容としては編集されていないので
        // 上書き対象ではないが、そもそも currentDefault と実質同じなので Changed は false、
        // かつ saved 側の値もそのまま (無用な書き換えをしない) であるべき。
        string savedWithNormalizedLineEndings = currentDefaults.Prompts.GroqRefineSystemPrompt.ReplaceLineEndings("\n");
        loaded.Prompts.GroqRefineSystemPrompt = savedWithNormalizedLineEndings;

        var result = PromptMigration.ApplyLegacyDefaults(loaded, currentDefaults);

        Assert.False(result.Changed);
        Assert.Empty(result.UpdatedKeys);
        Assert.Equal(savedWithNormalizedLineEndings, loaded.Prompts.GroqRefineSystemPrompt);
    }

    [Fact]
    public void MigrateValue_SavedAndLegacyDifferOnlyByLineEndings_StillTreatedAsMatch()
    {
        var (value, updated) = PromptMigration.MigrateValue(
            "legacy\r\nvalue",
            "current",
            ["legacy\nvalue"]);

        Assert.Equal("current", value);
        Assert.True(updated);
    }

    [Theory]
    [InlineData("DEV")]
    [InlineData("BIZ")]
    [InlineData("DOC")]
    [InlineData("STD")]
    public void ApplyLegacyDefaults_SavedMatchesLegacyCategoryPrompt_UpdatesToCurrentDefault(string category)
    {
        var currentDefaults = new AppSettings();
        var loaded = new AppSettings();
        loaded.CategoryPrompts[category] = GetLegacyCategoryPrompt(category);

        var result = PromptMigration.ApplyLegacyDefaults(loaded, currentDefaults);

        Assert.Equal(currentDefaults.CategoryPrompts[category], loaded.CategoryPrompts[category]);
        Assert.True(result.Changed);
        Assert.Contains($"category_prompts.{category}", result.UpdatedKeys);
    }

    [Theory]
    [InlineData("DEV")]
    [InlineData("BIZ")]
    [InlineData("DOC")]
    [InlineData("STD")]
    public void ApplyLegacyDefaults_SavedIsUserCustomCategoryPrompt_DoesNotOverwrite(string category)
    {
        var currentDefaults = new AppSettings();
        var loaded = new AppSettings();
        string userPrompt = $"{category} 用に私が独自に書いたプロンプトです。書き換えられては困ります。";
        loaded.CategoryPrompts[category] = userPrompt;

        var result = PromptMigration.ApplyLegacyDefaults(loaded, currentDefaults);

        Assert.Equal(userPrompt, loaded.CategoryPrompts[category]);
        Assert.False(result.Changed);
        Assert.DoesNotContain($"category_prompts.{category}", result.UpdatedKeys);
    }

    [Theory]
    [InlineData("DEV")]
    [InlineData("BIZ")]
    [InlineData("DOC")]
    [InlineData("STD")]
    public void ApplyLegacyDefaults_SavedAlreadyCurrentCategoryPrompt_NoOp(string category)
    {
        var currentDefaults = new AppSettings();
        var loaded = new AppSettings();

        var result = PromptMigration.ApplyLegacyDefaults(loaded, currentDefaults);

        Assert.False(result.Changed);
        Assert.Empty(result.UpdatedKeys);
        Assert.Equal(currentDefaults.CategoryPrompts[category], loaded.CategoryPrompts[category]);
    }

    [Fact]
    public void ApplyLegacyDefaults_MixOfLegacyAndUserEditedFields_OnlyMigratesLegacyOnes()
    {
        var currentDefaults = new AppSettings();
        var loaded = new AppSettings();
        loaded.Prompts.GroqRefineSystemPrompt = LegacyGroqRefineSystemPrompt[0];
        loaded.CategoryPrompts["DEV"] = LegacyDevPrompt[0];
        loaded.CategoryPrompts["BIZ"] = "ユーザー独自の BIZ プロンプト";

        var result = PromptMigration.ApplyLegacyDefaults(loaded, currentDefaults);

        Assert.True(result.Changed);
        Assert.Contains("prompts.groq_refine_system_prompt", result.UpdatedKeys);
        Assert.Contains("category_prompts.DEV", result.UpdatedKeys);
        Assert.DoesNotContain("category_prompts.BIZ", result.UpdatedKeys);

        Assert.Equal(currentDefaults.Prompts.GroqRefineSystemPrompt, loaded.Prompts.GroqRefineSystemPrompt);
        Assert.Equal(currentDefaults.CategoryPrompts["DEV"], loaded.CategoryPrompts["DEV"]);
        Assert.Equal("ユーザー独自の BIZ プロンプト", loaded.CategoryPrompts["BIZ"]);
    }

    [Fact]
    public void ApplyLegacyDefaults_UpdatedKeys_AreShortKeyNamesNotPromptBodies()
    {
        var currentDefaults = new AppSettings();
        var loaded = new AppSettings();
        loaded.Prompts.GroqRefineSystemPrompt = LegacyGroqRefineSystemPrompt[0];
        loaded.CategoryPrompts["DEV"] = LegacyDevPrompt[0];
        loaded.CategoryPrompts["BIZ"] = LegacyBizPrompt[0];
        loaded.CategoryPrompts["DOC"] = LegacyDocPrompt[0];
        loaded.CategoryPrompts["STD"] = LegacyStdPrompt[0];

        var result = PromptMigration.ApplyLegacyDefaults(loaded, currentDefaults);

        Assert.Equal(
            new[]
            {
                "prompts.groq_refine_system_prompt",
                "category_prompts.DEV",
                "category_prompts.BIZ",
                "category_prompts.DOC",
                "category_prompts.STD",
            },
            result.UpdatedKeys);
        Assert.All(result.UpdatedKeys, key => Assert.True(key.Length < 40, $"key too long: {key}"));
    }

    [Fact]
    public void MigrateValue_SavedEqualsLegacyValue_ReturnsCurrentDefaultAndUpdatedTrue()
    {
        var (value, updated) = PromptMigration.MigrateValue("legacy", "current", ["legacy", "older"]);

        Assert.Equal("current", value);
        Assert.True(updated);
    }

    [Fact]
    public void MigrateValue_SavedEqualsCurrentDefault_ReturnsSavedAndUpdatedFalse()
    {
        var (value, updated) = PromptMigration.MigrateValue("current", "current", ["legacy"]);

        Assert.Equal("current", value);
        Assert.False(updated);
    }

    [Fact]
    public void MigrateValue_SavedIsUnknownUserValue_ReturnsSavedUnchangedAndUpdatedFalse()
    {
        var (value, updated) = PromptMigration.MigrateValue("user value", "current", ["legacy", "older"]);

        Assert.Equal("user value", value);
        Assert.False(updated);
    }

    [Fact]
    public void MigrateValue_NoLegacyValuesRegistered_UnknownSavedValueIsNeverOverwritten()
    {
        var (value, updated) = PromptMigration.MigrateValue("some saved value", "current", []);

        Assert.Equal("some saved value", value);
        Assert.False(updated);
    }

    [Fact]
    public void ApplyLegacyDefaults_LoadedPromptsIsNull_DoesNotThrowAndFillsInPromptSettings()
    {
        // settings.json に "prompts": null が明示的に書かれていた場合、System.Text.Json は
        // AppSettings.Prompts のプロパティ既定値 (= new PromptSettings()) を null で
        // 上書きしてしまう。ApplyLegacyDefaults 冒頭の loaded.Prompts ??= new PromptSettings()
        // というガードが効いて例外にならないことを確認する。
        var currentDefaults = new AppSettings();
        var loaded = new AppSettings { Prompts = null! };

        var exception = Record.Exception(() => PromptMigration.ApplyLegacyDefaults(loaded, currentDefaults));

        Assert.Null(exception);
        Assert.NotNull(loaded.Prompts);
        Assert.Equal(currentDefaults.Prompts.GroqRefineSystemPrompt, loaded.Prompts.GroqRefineSystemPrompt);
    }
}
