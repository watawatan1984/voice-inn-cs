using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VoiceIn.Core;
using Whisper.net.Ggml;

namespace VoiceIn.Ai;

/// <summary>
/// GGML モデルファイル (Whisper.net / whisper.cpp が読み込むモデル本体) のダウンロードを行うヘルパー。
///
/// 【入手方法の選定 (実装前にリフレクションで実機確認済み)】
/// モデルの入手方法には主に 2 通りが考えられた:
///   (a) Whisper.net パッケージに含まれるダウンローダ (Whisper.net.Ggml 名前空間の
///       WhisperGgmlDownloader) を使う
///   (b) Hugging Face の GGML モデル URL から HttpClient で直接取得する
///
/// 本実装では (a) を採用した。理由:
///   ・.NET のリフレクション (Assembly.LoadFile + GetType) で実際に確認した結果、
///     WhisperGgmlDownloader は「別パッケージ Whisper.net.Ggml」ではなく、
///     VoiceIn.csproj に既に参照済みの Whisper.net 1.9.1 パッケージ本体
///     (lib/net10.0/Whisper.net.dll) に直接同梱された public クラスであることが判明した
///     (obj/project.assets.json 上でも、本プロジェクトの実際のターゲット net10.0-windows7.0 で
///     "Whisper.net/1.9.1" が解決するアセンブリは lib/net10.0/Whisper.net.dll の 1 つのみであり、
///     "Whisper.net.Ggml" という別パッケージはローカル NuGet キャッシュ (~/.nuget/packages) にも
///     存在しない)。
///   ・そのため VoiceIn.csproj への PackageReference 追加 (本タスクでは編集禁止) が一切不要で、
///     既存の参照だけで完結する。
///   ・URL 組み立て・Hugging Face 側のリダイレクト対応・リポジトリ改編への追従を自前実装せずに
///     済み、ライブラリ側のメンテナンスに乗れる。
///   ・WhisperGgmlDownloader.GetGgmlModelAsync は CancellationToken を受け取れるため、
///     キャンセル要件も自然に満たせる。
///
/// 保存先は Core/EnvLoader.GetAppDataDirectory() 配下 (LocalProvider.GetDefaultModelsDirectory()
/// が返す場所であり、ポータブルモードにも追従する) に固定する。ダウンロード自体は一時ファイルへ
/// 書き込み、完了後にのみ File.Move でアトミックに正規のファイル名へ配置する。こうすることで、
/// ダウンロードの途中でアプリ終了・キャンセル・通信断が起きても、半端なファイルが正規のモデルと
/// して残って次回起動時に「破損モデルの読み込み失敗」という分かりにくい形で表面化することを防ぐ。
/// </summary>
public static class ModelDownloader
{
    /// <summary>
    /// ダウンロード進捗の通知に使う値。
    /// TotalBytesApprox は SupportedModelSizes に載っているおおよそのサイズ (バイト) であり、
    /// サーバー応答に Content-Length が無い場合でも UI 側で概算の割合を表示できるようにするための
    /// 目安に過ぎない (実ファイルサイズと厳密には一致しない)。
    /// </summary>
    public readonly record struct ModelDownloadProgress(long BytesDownloaded, long TotalBytesApprox);

    /// <summary>サポートするモデルサイズ 1 件分のメタデータ。</summary>
    public sealed record ModelSizeInfo(string Id, long ApproxBytes, string ApproxSizeLabel);

    /// <summary>
    /// 選択可能なモデルサイズの一覧 (この順序のまま Ui/SettingsWindow のコンボボックスに使う)。
    /// おおよそのファイルサイズは実装依頼の指示にある目安をそのまま使用する:
    /// tiny 約75MB / base 約142MB / small 約466MB / medium 約1.5GB / large-v3 約3GB。
    /// この一覧が唯一の情報源であり、UI 側 (XAML) にサイズを別途ハードコードしない。
    /// </summary>
    public static readonly IReadOnlyList<ModelSizeInfo> SupportedModelSizes =
    [
        new ModelSizeInfo("tiny", 75L * 1024 * 1024, "約75MB"),
        new ModelSizeInfo("base", 142L * 1024 * 1024, "約142MB"),
        new ModelSizeInfo("small", 466L * 1024 * 1024, "約466MB"),
        new ModelSizeInfo("medium", (long)(1.5 * 1024 * 1024 * 1024), "約1.5GB"),
        new ModelSizeInfo("large-v3", 3L * 1024 * 1024 * 1024, "約3GB"),
    ];

    // GGML モデルは数百MB〜数GBの通信になりうるため、HttpClient 自体のタイムアウトで途中で
    // 打ち切られてしまわないよう無期限にする。実際の打ち切りはあくまで呼び出し側が渡す
    // CancellationToken (UI のキャンセルボタン) の責務とする。
    // GeminiProvider/GroqProvider の 60 秒固定タイムアウトの HttpClient とは用途が異なるため、
    // それらとは共用せず本クラス専用のインスタンスを static で保持する。
    private static readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// 指定したモデルサイズ名のメタデータを返す。大文字小文字は区別しない。
    /// 未知のサイズ名の場合は null (ネットワーク I/O なし)。
    /// </summary>
    public static ModelSizeInfo? FindModelSizeInfo(string modelSize)
    {
        foreach (var info in SupportedModelSizes)
        {
            if (string.Equals(info.Id, modelSize, StringComparison.OrdinalIgnoreCase))
            {
                return info;
            }
        }

        return null;
    }

    /// <summary>GGML モデルファイルの既定の保存先ディレクトリ (ネットワーク I/O なし)。</summary>
    public static string GetModelsDirectory() => LocalProvider.GetDefaultModelsDirectory();

    /// <summary>モデルサイズ名から GGML ファイル名を組み立てる (ネットワーク I/O なし)。</summary>
    public static string GetModelFileName(string modelSize) => LocalProvider.GetDefaultModelFileName(modelSize);

    /// <summary>モデルサイズ名から、既定の保存先での絶対パスを組み立てる (ネットワーク I/O なし)。</summary>
    public static string GetModelFilePath(string modelSize) => Path.Combine(GetModelsDirectory(), GetModelFileName(modelSize));

    /// <summary>
    /// 既定の保存先に、指定したモデルサイズのファイルが既に存在するかどうか (ネットワーク I/O なし)。
    /// LocalSettings.ModelPath で明示パスが指定されているケースは考慮しない
    /// (呼び出し側が別途 LocalProvider.ResolveModelPath で判定すること)。
    /// </summary>
    public static bool ModelExists(string modelSize) => File.Exists(GetModelFilePath(modelSize));

    /// <summary>
    /// モデルサイズ名を Whisper.net の GgmlType へ変換する。
    /// 英語専用モデル (TinyEn 等) やより古いバージョン (LargeV1/V2) は選択肢に含めないため、
    /// ここで変換できるのは SupportedModelSizes に載っている 5 種類のみ。
    /// </summary>
    private static GgmlType ToGgmlType(string modelSize) => modelSize.ToLowerInvariant() switch
    {
        "tiny" => GgmlType.Tiny,
        "base" => GgmlType.Base,
        "small" => GgmlType.Small,
        "medium" => GgmlType.Medium,
        "large-v3" => GgmlType.LargeV3,
        _ => throw new ArgumentException($"未対応のモデルサイズです: {modelSize}", nameof(modelSize))
    };

    /// <summary>
    /// 指定したモデルサイズの GGML モデルファイルをダウンロードし、既定の保存先へ配置する。
    /// ・progress (IProgress&lt;ModelDownloadProgress&gt;) 経由でダウンロード済みバイト数を報告する。
    ///   IProgress&lt;T&gt;.Report は生成時にキャプチャした SynchronizationContext 上で実行されるため、
    ///   呼び出し側 (UI スレッド) で new Progress&lt;T&gt;(...) を作って渡せば、追加の
    ///   Dispatcher.Invoke なしで安全に UI を更新できる。
    /// ・cancellationToken 経由でいつでもキャンセルできる。
    /// ・ダウンロード中は最終ファイル名とは別の一時ファイル (destPath + ランダムサフィックス) へ
    ///   書き込み、正常終了時にのみ File.Move でアトミックに正規の場所へ配置する。
    ///   失敗・キャンセル時は一時ファイルを削除し、正規のファイル (既存の古いモデルを含む) には
    ///   一切手を触れない。
    /// ・「既に別のモデルファイルが存在する場合に上書きするかどうか確認する」UX は呼び出し側
    ///   (Ui/SettingsWindow) の責務とする。本メソッド自体は呼ばれたら常に配置 (上書き) する。
    /// ・UI スレッドをブロックしないよう全体を非同期 (async/await) で実装しており、
    ///   呼び出し側も同期的に待ち合わせ (.Wait()/.Result) せず必ず await すること。
    /// </summary>
    public static async Task DownloadModelAsync(
        string modelSize,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        GgmlType ggmlType = ToGgmlType(modelSize);
        ModelSizeInfo sizeInfo = FindModelSizeInfo(modelSize)
            ?? throw new ArgumentException($"未対応のモデルサイズです: {modelSize}", nameof(modelSize));

        string modelsDir = GetModelsDirectory();
        Directory.CreateDirectory(modelsDir);

        string destPath = GetModelFilePath(modelSize);
        string tempPath = Path.Combine(modelsDir, $"{GetModelFileName(modelSize)}.download-{Guid.NewGuid():N}.tmp");

        try
        {
            var downloader = new WhisperGgmlDownloader(_httpClient);
            using Stream sourceStream = await downloader
                .GetGgmlModelAsync(ggmlType, QuantizationType.NoQuantization, cancellationToken)
                .ConfigureAwait(false);

            const int BufferSize = 81920;
            var buffer = new byte[BufferSize];
            long totalRead = 0;

            await using (var fileStream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                int read;
                while ((read = await sourceStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    totalRead += read;
                    progress?.Report(new ModelDownloadProgress(totalRead, sizeInfo.ApproxBytes));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // アトミックに正規の場所へ配置する (同名の古いモデルがあれば上書きする)。
            File.Move(tempPath, destPath, overwrite: true);
            Logger.Info($"ModelDownloader: モデルのダウンロードが完了しました (size={modelSize}, bytes={totalRead})");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 文字起こし本文・API キーと違い、モデルサイズ名や例外の型・メッセージには
            // 機微情報を含まないため、Core/Logger.cs の禁止事項には抵触しない。
            Logger.Warn($"ModelDownloader: モデルのダウンロードに失敗しました (size={modelSize}) -- {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            // 成功時は File.Move 済みで tempPath は既に存在しないため、この呼び出しは無害な no-op になる。
            TryDeleteTempFile(tempPath);
        }
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // ベストエフォート: 一時ファイル削除の失敗でダウンロード処理自体の成否を変えない
            // (EnvLoader.TryWriteKey の一時ファイル後始末と同じ方針)。
        }
    }
}
