using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using VoiceIn.Core;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace VoiceIn.Ai;

/// <summary>
/// Whisper.net (whisper.cpp の .NET バインディング) を使ったローカル (オフライン) 音声認識プロバイダ。
/// 移植元 Python 版 (src/ai/providers/local.py, faster-whisper 使用) と同じく、既定では
/// 整形を一切行わず生の文字起こし結果をそのまま返す。設定 (Local.RefineWithCloud) が true の
/// 場合のみ、Ai/RefineProviderFactory が解決する整形バックエンド (既定 Gemini、設定で
/// NVIDIA にも切替可能) を使ってクラウド側で追加整形する (ハイブリッドモード)。
/// 以前は Groq の LLM で整形していたが、Groq 側で整形用チャットモデルの提供が終了する
/// 事故が起きたため、整形処理は Groq (Whisper による文字起こし専用になった) から
/// 切り離されている。
/// </summary>
public class LocalProvider : IAiProvider
{
    public string ProviderName => "local";

    // ---- ロード済みモデルの静的キャッシュ ----
    //
    // AiProviderFactory.CreateProvider() は発話 1 回ごとに呼ばれ LocalProvider インスタンスが
    // 都度生成される (App.xaml.cs の Task.Run 内)。Whisper のモデルロードは数百MB〜数GBの
    // 読み込みを伴い数秒かかるため、GeminiProvider / GroqProvider が HttpClient を static で
    // 使い回しているのと同じ考え方で、ロード済み WhisperFactory をプロセス全体で static に
    // キャッシュし、インスタンスが作り直されても再ロードしないようにする。
    //
    // キャッシュキーはモデルパスと UseGpu の組で判定し、どちらか (モデルサイズ変更による
    // パス変化を含む) が変わったときだけ古いモデルを破棄して再ロードする。
    // 複数スレッドから同時に呼ばれても二重ロードが起きないよう、_factoryLock で
    // チェック〜生成〜差し替えの一連の処理を排他制御する。
    private static readonly object _factoryLock = new();
    private static WhisperFactory? _cachedFactory;
    private static string? _cachedModelPath;
    private static bool _cachedUseGpu;

    /// <summary>
    /// GGML モデルファイルの既定の保存先ディレクトリ名 (EnvLoader.GetAppDataDirectory() 配下)。
    /// 後続作業のモデルダウンロード UI もこの場所にファイルを配置する想定。
    /// </summary>
    private const string ModelsSubDirectory = "models";

    public async Task<string> TranscribeAsync(string audioFilePath, string prompt)
    {
        var settings = SettingsManager.Instance.Settings;
        LocalSettings local = settings.Local;

        string modelPath = ResolveModelPath(local);
        if (!File.Exists(modelPath))
        {
            // 起動直後に数百MBの通信が勝手に始まるのはユーザーの意図に反するため、
            // ここでは絶対にダウンロードを開始しない。次に何をすればよいかが分かる
            // メッセージ (GeminiProvider の API キー未設定メッセージと同じ書き方) を投げる。
            throw new InvalidOperationException(
                $"ローカル文字起こしモデルが見つかりません ({modelPath})。設定画面からモデルをダウンロードしてください。");
        }

        WhisperFactory factory = GetOrCreateFactory(modelPath, local.UseGpu);

        WhisperProcessor processor;
        try
        {
            processor = factory.CreateBuilder()
                .WithLanguage("ja")
                .WithPrompt(settings.Prompts.GroqWhisperPrompt)
                .WithBeamSearchSamplingStrategy(b => b.WithBeamSize(5))
                .Build();
        }
        catch (WhisperModelLoadException ex)
        {
            // モデルファイルは存在するが壊れている等で読み込みに失敗したケース。
            // キャッシュを無効化し、次回呼び出し (ファイル差し替え後の再試行等) で
            // 改めて FromPath からやり直せるようにしておく。
            InvalidateCache();
            throw new InvalidOperationException(
                $"ローカル文字起こしモデルの読み込みに失敗しました ({modelPath})。ファイルが破損している可能性があります。設定画面から再ダウンロードしてください。",
                ex);
        }

        string rawText;
        using (processor)
        {
            var textBuilder = new StringBuilder();
            using var audioStream = File.OpenRead(audioFilePath);

            // 移植元 (faster-whisper) と同じく、セグメントを連結して 1 つの文字列として返す。
            await foreach (var segment in processor.ProcessAsync(audioStream))
            {
                textBuilder.Append(segment.Text);
            }

            rawText = textBuilder.ToString().Trim();
        }

        if (!local.RefineWithCloud || string.IsNullOrWhiteSpace(rawText))
        {
            // 既定 (RefineWithCloud=false): 移植元と同じく、整形は行わず生の文字起こし結果をそのまま返す。
            return rawText;
        }

        // ハイブリッドモード: ローカルの生テキストをクラウド LLM (Ai/RefineProviderFactory が
        // 解決する Gemini/NVIDIA) で整形する。
        // 整形はあくまで付加価値であり、失敗 (API キー未設定・通信エラー等) しても
        // 文字起こし結果そのものは絶対に失わせない。生テキストへフォールバックする。
        try
        {
            var refineProvider = RefineProviderFactory.CreateProvider();
            return await refineProvider.RefineAsync(rawText, prompt);
        }
        catch (Exception ex)
        {
            Logger.Warn($"LocalProvider: クラウド整形に失敗したため生の文字起こし結果を返します -- {ex.GetType().Name}: {ex.Message}");
            return rawText;
        }
    }

    /// <summary>
    /// GGML モデルの既定の保存先ディレクトリを返す (EnvLoader.GetAppDataDirectory() 配下)。
    /// Ai/ModelDownloader.cs (ダウンロード先の決定) や Ui/SettingsWindow (モデルの状態表示) からも
    /// 同じ場所を参照できるよう、パス構築ロジックをここに一元化する
    /// (重複させると、将来どちらか片方だけ変更されて食い違う恐れがあるため)。
    /// </summary>
    internal static string GetDefaultModelsDirectory() => Path.Combine(EnvLoader.GetAppDataDirectory(), ModelsSubDirectory);

    /// <summary>
    /// モデルサイズ名 (tiny/base/small/medium/large-v3 等) から GGML ファイル名を組み立てる。
    /// whisper.cpp / Hugging Face 上の実際の命名規則 (ggml-{size}.bin) に合わせる。
    /// </summary>
    internal static string GetDefaultModelFileName(string modelSize) => $"ggml-{modelSize}.bin";

    /// <summary>
    /// LocalSettings からモデルファイルの絶対パスを決定する。
    /// ModelPath が明示されていればそれを優先し、無ければ既定の保存先
    /// (EnvLoader.GetAppDataDirectory()\models\ggml-{ModelSize}.bin、ポータブルモードにも追従) から探す。
    /// Ui/SettingsWindow (モデルの状態表示) からも同じ解決ロジックを再利用できるよう internal 公開する。
    /// </summary>
    internal static string ResolveModelPath(LocalSettings local)
    {
        if (!string.IsNullOrWhiteSpace(local.ModelPath))
        {
            return local.ModelPath;
        }

        return Path.Combine(GetDefaultModelsDirectory(), GetDefaultModelFileName(local.ModelSize));
    }

    /// <summary>
    /// モデルパス・UseGpu が前回と同じであればキャッシュ済み WhisperFactory をそのまま返し、
    /// 異なる場合のみ古いモデルを破棄してから再ロードする。呼び出し全体を _factoryLock で
    /// 囲むことで、複数スレッドから同時に呼ばれても二重ロードが起きないようにする。
    /// </summary>
    private static WhisperFactory GetOrCreateFactory(string modelPath, bool useGpu)
    {
        lock (_factoryLock)
        {
            if (_cachedFactory != null &&
                string.Equals(_cachedModelPath, modelPath, StringComparison.Ordinal) &&
                _cachedUseGpu == useGpu)
            {
                return _cachedFactory;
            }

            _cachedFactory?.Dispose();
            _cachedFactory = null;
            _cachedModelPath = null;

            // RuntimeOptions はプロセス全体で共有される静的な設定であり、ドキュメント上
            // 「最初に WhisperFactory が作られる前」にのみ効果を持つ (一度ネイティブライブラリが
            // ロードされると、以後この値を変更しても既にロード済みのライブラリは切り替わらない)。
            // そのため UseGpu の切り替えが確実に反映されるのはプロセス起動後 最初の 1 回のみだが、
            // 呼び出す分には無害なので、ロードのたびに現在の設定へ合わせて設定し直す。
            //
            // UseGpu=true: 既定の優先順位 (Cuda -> Cuda12 -> Vulkan -> CoreML -> OpenVino -> Cpu ->
            //   CpuNoAvx) をそのまま使う。CUDA が使えない環境では Whisper.net 自身のローダーが
            //   cudaRuntimeGetVersion 等で利用可否を検証し、例外を出さずに Cpu へ自動フォールバックする
            //   (本実装時に CUDA 無し環境で実機確認済み)。
            // UseGpu=false: Cpu (と、AVX 非対応 CPU 向けの CpuNoAvx) のみに限定し、CUDA を試みさせない。
            RuntimeOptions.RuntimeLibraryOrder = useGpu
                ? new List<RuntimeLibrary>
                {
                    RuntimeLibrary.Cuda,
                    RuntimeLibrary.Cuda12,
                    RuntimeLibrary.Vulkan,
                    RuntimeLibrary.CoreML,
                    RuntimeLibrary.OpenVino,
                    RuntimeLibrary.Cpu,
                    RuntimeLibrary.CpuNoAvx,
                }
                : new List<RuntimeLibrary> { RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx };

            // WhisperFactoryOptions は struct (値型) であり、Default は毎回独立したコピーを返すため、
            // ここで UseGpu だけを上書きしても他の既定値 (共有インスタンス) には影響しない。
            WhisperFactoryOptions options = WhisperFactoryOptions.Default;
            options.UseGpu = useGpu;

            WhisperFactory factory = WhisperFactory.FromPath(modelPath, options);

            // 実際に GPU (CUDA/Vulkan) が使われているかどうかを記録する。
            // 【プライバシー上の注意】ここでは実行環境・ランタイム種別のみを記録し、
            // 文字起こし本文は絶対にログへ渡さない (Core/Logger.cs のクラスコメント参照)。
            bool gpuLoaded = RuntimeOptions.LoadedLibrary is RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12 or RuntimeLibrary.Vulkan;
            Logger.Info($"LocalProvider: モデルをロードしました (runtime={RuntimeOptions.LoadedLibrary}, gpu_active={gpuLoaded}, use_gpu_setting={useGpu})");

            _cachedFactory = factory;
            _cachedModelPath = modelPath;
            _cachedUseGpu = useGpu;

            return factory;
        }
    }

    /// <summary>
    /// キャッシュ済みモデルを破棄し、次回呼び出し時に再ロードを強制する。
    /// モデルファイルが壊れていた場合など、キャッシュした失敗状態を引きずらないために使う。
    /// </summary>
    private static void InvalidateCache()
    {
        lock (_factoryLock)
        {
            _cachedFactory?.Dispose();
            _cachedFactory = null;
            _cachedModelPath = null;
        }
    }
}
