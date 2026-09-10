using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.Wave;
using VoiceIn.Core;

namespace VoiceIn.Audio;

public class AudioRecorder : IDisposable
{
    private WaveIn? _waveIn;
    private WaveFileWriter? _writer;
    private string? _tempFilePath;
    private readonly System.Threading.Timer _autoStopTimer;

    // RecordingStopped (NAudio のキャプチャ専用スレッドが完全に終了した後に発火する) を
    // 待ち合わせるためのシグナル。Stop() がこれを待ってから _writer を破棄することで、
    // キャプチャスレッドが書き込み中の WaveFileWriter を破棄してしまう競合を防ぐ。
    private readonly ManualResetEventSlim _recordingStoppedSignal = new(false);

    private long _totalSamples = 0;
    private double _sumSquared = 0;
    private float _peak = 0;

    // input_gain_db (dB) を線形倍率に変換した値。1.0 (0dB) のときは無操作。
    private double _gainMultiplier = 1.0;

    // ==== マイク入力レベルの「モニタリング」(設定画面・セットアップウィザードのマイクテスト用) ====
    // 録音 (Start/Stop, _waveIn/_writer) とは完全に別系統の WaveIn を使う。ファイルへは
    // 一切書き込まず、ゲイン適用後の RMS だけをフィールドへ保持する。UI 側はタイマーで
    // CurrentMonitoringRms を読み取ってバー表示を更新する (キャプチャスレッドのコールバックから
    // 直接 UI を触らない)。
    private WaveIn? _monitorWaveIn;
    private readonly object _monitorLock = new();
    private double _monitorRms;
    private double _monitorGainMultiplier = 1.0;

    // ==== プロセス内の複数インスタンスをまたいだ「録音とモニタリングの排他制御」 ====
    // App.xaml.cs は録音用に AudioRecorder のインスタンスを1つだけ保持しており (private フィールド、
    // コンストラクタは引数なしで SettingsWindow/SetupWindow を生成する)、これらのウィンドウが
    // マイクテスト用に生成する AudioRecorder は必然的に「別インスタンス」になる
    // (App.xaml.cs 側の呼び出しを変更してインスタンスを共有させることができないため)。
    // そのため、インスタンスフィールドの IsRecording / IsMonitoring だけでは
    // 「設定画面でマイクテスト中でも、ホットキーによる録音 (App 側の別インスタンス) は必ず動く」
    // 「録音がプロセス内のどこかで始まったら、他インスタンスのモニタリングも道連れで停止する」
    // という要件を満たせない。AudioRecorder 型が持つ static な登録簿を介して、
    // 同一プロセス内のインスタンスをまたいだ排他制御を行う。
    // ・登録簿の出入りは Start/Stop/StartMonitoring/StopMonitoring からのみ行われ、
    //   いずれも s_crossInstanceLock を取っている間は参照の Add/Remove/件数確認のみで
    //   即座に終わる (デバイスのオープン/クローズ自体はロックの外で行う)。
    private static readonly object s_crossInstanceLock = new();
    private static readonly HashSet<AudioRecorder> s_recordingInstances = new();
    private static readonly HashSet<AudioRecorder> s_monitoringInstances = new();

    public bool IsRecording { get; private set; }
    public bool IsMonitoring { get; private set; }
    public event Action? AutoStopRequested;

    /// <summary>
    /// モニタリング中の現在の RMS 値 (概ね 0.0〜1.0 の範囲、入力ゲイン適用後)。
    /// モニタリングしていないときは 0.0。NAudio のキャプチャコールバック (別スレッド) から
    /// ロックの下で書き込まれる。UI 側はこれをタイマーでポーリングして表示を更新する想定であり、
    /// このプロパティ自体はイベントを発火しない (コールバックから直接 UI を更新させないため)。
    /// </summary>
    public double CurrentMonitoringRms
    {
        get { lock (_monitorLock) { return _monitorRms; } }
    }

    public AudioRecorder()
    {
        _autoStopTimer = new System.Threading.Timer(OnAutoStopTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    public static List<(int Index, string Name)> GetInputDevices()
    {
        var devices = new List<(int Index, string Name)>();
        int count = WaveIn.DeviceCount;
        for (int i = 0; i < count; i++)
        {
            var caps = WaveIn.GetCapabilities(i);
            devices.Add((i, caps.ProductName));
        }
        return devices;
    }

    public void Start(int? deviceIndex = null, int maxSeconds = 60, double? inputGainDb = null)
    {
        if (IsRecording)
        {
            return;
        }

        // モニタリング (マイクテスト) が動作中だと同じデバイスの二重オープンで録音の
        // StartRecording() が失敗しうる。録音はこのアプリの主機能であり失敗させてはならないため、
        // 録音開始時にモニタリングを自動的に止めてからデバイスを解放する。
        // SettingsWindow/SetupWindow はマイクテスト用に自分自身とは別の AudioRecorder インスタンスを
        // 持つため (クラス冒頭の static フィールド群のコメント参照)、自分自身だけでなく
        // プロセス内の他インスタンスのモニタリングも道連れで停止する。
        // StopMonitoring() 自体は例外を投げないベストエフォート実装。
        StopMonitoring();
        List<AudioRecorder> otherMonitors;
        lock (s_crossInstanceLock)
        {
            otherMonitors = new List<AudioRecorder>(s_monitoringInstances);
        }
        foreach (var monitor in otherMonitors)
        {
            monitor.StopMonitoring();
        }

        Cleanup();

        _tempFilePath = Path.Combine(Path.GetTempPath(), $"voicein_{Guid.NewGuid():N}.wav");
        _totalSamples = 0;
        _sumSquared = 0;
        _peak = 0;

        // inputGainDb が明示的に渡されなければ設定 (SettingsManager) から読む。
        // App.xaml.cs は Start(inputDevice, maxRecordSeconds) の2引数で呼ぶため、
        // 既存の呼び出しはそのまま動作する。
        double gainDb = inputGainDb ?? SettingsManager.Instance.Settings.Audio.InputGainDb;
        _gainMultiplier = AudioSampleProcessor.DbToLinearGain(gainDb);

        // NAudio 3.0.1 では WaveIn 自体が専用キャプチャスレッド (RecordThread) と
        // AutoResetEvent ベースのコールバックで実装されており、呼び出しスレッドの
        // メッセージポンプに依存しない（過去バージョンにあった「メッセージポンプ必須の
        // WaveIn」と「スレッドベースの WaveInEvent」は本バージョンで統合済み）。
        // 別名の WaveInEvent は [Obsolete("...renamed to WaveIn...")] な後方互換シムで
        // WaveIn を継承するだけの空実装のため、あえて使わずそのまま WaveIn を使う
        // （使うと警告が増えるだけで挙動は変わらない。実測で CS0618 警告16件を確認済み）。
        WaveIn? waveIn = null;
        WaveFileWriter? writer = null;
        try
        {
            waveIn = new WaveIn
            {
                WaveFormat = new WaveFormat(44100, 16, 1) // 44.1kHz, 16bit, Mono
            };

            if (deviceIndex.HasValue && deviceIndex.Value >= 0 && deviceIndex.Value < WaveIn.DeviceCount)
            {
                waveIn.DeviceNumber = deviceIndex.Value;
            }

            writer = new WaveFileWriter(_tempFilePath, waveIn.WaveFormat);

            _recordingStoppedSignal.Reset();
            waveIn.DataAvailable += OnDataAvailable;
            waveIn.RecordingStopped += OnRecordingStopped;

            _waveIn = waveIn;
            _writer = writer;

            waveIn.StartRecording();
            IsRecording = true;
            lock (s_crossInstanceLock) { s_recordingInstances.Add(this); }
        }
        catch
        {
            // new WaveIn() / new WaveFileWriter() / StartRecording() のいずれかで
            // 例外が発生した場合、確保済みのデバイス・ファイルをすべて解放してから
            // 呼び出し元 (App.xaml.cs) へ例外を再スローする。マイクが他アプリで
            // 使用中の場合などに繰り返し発生しうるため、リークさせない。
            _waveIn = null;
            _writer = null;

            if (waveIn != null)
            {
                waveIn.DataAvailable -= OnDataAvailable;
                waveIn.RecordingStopped -= OnRecordingStopped;
                waveIn.Dispose();
            }

            writer?.Dispose();

            if (_tempFilePath != null && File.Exists(_tempFilePath))
            {
                try { File.Delete(_tempFilePath); } catch { }
            }
            _tempFilePath = null;

            throw;
        }

        // settings.json を直接編集する等して極端な値が渡されても、非常に長い録音や
        // メモリ逼迫 (WAV は Stop 後に File.ReadAllBytesAsync で丸ごとメモリに読み込まれる)
        // を招かないよう、maxSeconds を妥当な範囲にクランプする。
        //   ・0 以下は従来「自動停止しない (無制限録音)」を意味していたが、これはメモリ逼迫の
        //     最悪ケースそのものなので廃止する。ただし単純に 1 秒へ丸めると「話し始めた直後に
        //     録音が切れる」壊れた挙動になってしまうため、既定値の 60 秒へフォールバックする。
        //   ・正の値は移植元 Python 版 (src/ui/settings.py の setRange(5, 600)) に合わせて
        //     5〜600 秒の範囲にクランプする。
        int clampedMaxSeconds = maxSeconds > 0 ? Math.Clamp(maxSeconds, 5, 600) : 60;
        _autoStopTimer.Change(TimeSpan.FromSeconds(clampedMaxSeconds), Timeout.InfiniteTimeSpan);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // ローカル変数に退避してから null チェックする。_writer フィールドは
        // Stop() 側から別スレッドで書き換えられうるため、チェックと使用の間で
        // フィールドが null に変わるレースを避ける。
        var writer = _writer;
        if (writer == null) return;

        int bytesRecorded = e.BytesRecorded;

        // ゲイン適用 (dB→倍率変換・16bit PCM クリッピング) は AudioSampleProcessor へ
        // 切り出し済み。倍率 1.0 (0dB) のときに無駄なコピーを避ける早期分岐も内部にある。
        byte[] outBuffer = AudioSampleProcessor.ApplyGain(e.Buffer, bytesRecorded, _gainMultiplier);

        try
        {
            writer.Write(outBuffer, 0, bytesRecorded);
        }
        catch (ObjectDisposedException)
        {
            // Stop() 側で writer が破棄された直後に、キャプチャスレッドからの
            // 最終コールバックが遅延して届いた場合のガード。データは破棄する。
            return;
        }

        // ゲイン適用後の値で RMS / Peak を集計する (集計ロジックも AudioSampleProcessor へ
        // 切り出し済み)。Peak は結合則を満たすため、チャンクごとの最大値を Math.Max していく形でも
        // 全サンプルを通しで見た場合と結果は変わらない。
        var (sampleCount, sumSquared, peak) = AudioSampleProcessor.Aggregate(outBuffer, bytesRecorded);
        _totalSamples += sampleCount;
        _sumSquared += sumSquared;
        if (peak > _peak) _peak = peak;
    }

    private void OnAutoStopTimer(object? state)
    {
        AutoStopRequested?.Invoke();
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // NAudio の WaveIn は専用キャプチャスレッドが完全に終了した後にこのイベントを
        // 発火する。Stop() 側はこのシグナルを待ってから _writer を破棄するため、
        // 書き込み中の WaveFileWriter を破棄してしまう競合 (WAV破損・ObjectDisposedException)
        // を防げる。
        _recordingStoppedSignal.Set();
    }

    /// <summary>
    /// ファイルへの書き込みを一切行わず、マイク入力レベル (RMS) だけを監視する
    /// 「モニタリング」を開始する。設定画面・セットアップウィザードの「マイクテスト」機能から使う。
    ///
    /// ・録音 (Start/Stop) とは完全に独立した別の WaveIn インスタンス (_monitorWaveIn) を使う。
    /// ・inputGainDb は録音時 (Start) と同じ AudioSampleProcessor.DbToLinearGain で線形倍率に変換し、
    ///   コールバック内で AudioSampleProcessor.ApplyGain 相当のゲインを適用してから RMS を計算する
    ///   (メーターは「実際に録音される音量」を示す必要があるため。詳細は ComputeRms 参照)。
    ///   明示指定が無ければ Start() と同じく設定 (SettingsManager) から読む。
    /// ・録音中に呼び出された場合は、同じデバイスの二重オープンによる失敗を避けるため、
    ///   録音を止めることはせず、モニタリング開始そのものを拒否する。呼び出し元が
    ///   「開始できなかったこと」に気付けるよう、黙って失敗せず例外を投げる。
    ///   ここでいう「録音中」は自分自身のインスタンスに限らない。SettingsWindow/SetupWindow は
    ///   マイクテスト用に App.xaml.cs の録音用インスタンスとは別の AudioRecorder を持つため、
    ///   プロセス内の他インスタンスが録音中かどうかも static な登録簿で確認する
    ///   (詳細はクラス冒頭の static フィールド群のコメント参照)。
    /// </summary>
    /// <exception cref="InvalidOperationException">プロセス内のいずれかのインスタンスが録音中に呼び出された場合。</exception>
    public void StartMonitoring(int? deviceIndex = null, double? inputGainDb = null)
    {
        if (IsRecording)
        {
            throw new InvalidOperationException("録音中はマイクレベルのモニタリングを開始できません。");
        }

        lock (s_crossInstanceLock)
        {
            if (s_recordingInstances.Count > 0)
            {
                throw new InvalidOperationException("録音中はマイクレベルのモニタリングを開始できません。");
            }
        }

        // 既にモニタリング中であれば、一旦止めてから (デバイス変更等に対応するため) 開始し直す。
        StopMonitoring();

        double gainDb = inputGainDb ?? SettingsManager.Instance.Settings.Audio.InputGainDb;
        double gainMultiplier = AudioSampleProcessor.DbToLinearGain(gainDb);

        WaveIn? waveIn = null;
        try
        {
            waveIn = new WaveIn
            {
                WaveFormat = new WaveFormat(44100, 16, 1) // 44.1kHz, 16bit, Mono (Start() と同じ)
            };

            if (deviceIndex.HasValue && deviceIndex.Value >= 0 && deviceIndex.Value < WaveIn.DeviceCount)
            {
                waveIn.DeviceNumber = deviceIndex.Value;
            }

            _monitorGainMultiplier = gainMultiplier;
            lock (_monitorLock) { _monitorRms = 0.0; }

            waveIn.DataAvailable += OnMonitorDataAvailable;

            waveIn.StartRecording();

            _monitorWaveIn = waveIn;
            IsMonitoring = true;
            lock (s_crossInstanceLock) { s_monitoringInstances.Add(this); }
        }
        catch
        {
            // マイクが他アプリで使用中・無効化されている等で失敗した場合、確保しかけた
            // WaveIn を必ず解放してから呼び出し元 (Ui) へ例外を再スローする
            // (Start() の失敗時ハンドリングと同じ方針)。呼び出し元 (SettingsWindow/SetupWindow) は
            // これを catch して「何が起きたか分かるメッセージ」を表示すること。
            if (waveIn != null)
            {
                waveIn.DataAvailable -= OnMonitorDataAvailable;
                waveIn.Dispose();
            }
            _monitorWaveIn = null;
            IsMonitoring = false;
            throw;
        }
    }

    private void OnMonitorDataAvailable(object? sender, WaveInEventArgs e)
    {
        // 【プライバシー・最重要】ここで受け取る音声データはレベル (RMS) の計算にのみ使う。
        // ファイルへの書き込み・ログ出力は一切行わない (このメソッドはそれ以外の副作用を持たない)。
        double rms = AudioSampleProcessor.ComputeRms(e.Buffer, e.BytesRecorded, _monitorGainMultiplier);
        lock (_monitorLock)
        {
            _monitorRms = rms;
        }
    }

    /// <summary>
    /// モニタリングを停止し、マイクデバイスを解放する。モニタリング中でなければ何もしない。
    /// Start() (録音開始時の自動停止) と Dispose() の両方から呼ばれるため、内部で例外を
    /// 握りつぶすベストエフォート実装にする (ここで例外が漏れると、録音開始やアプリ終了処理
    /// そのものを失敗させてしまいかねないため)。
    /// </summary>
    public void StopMonitoring()
    {
        // 登録簿からの除去は早期 return より前、無条件に行う (二重呼び出しでも Remove は
        // 何も無ければ何もしないだけなので安全)。これにより「モニタリング中でない」と
        // 自分では思っていても、万一登録簿に残っていた場合の取りこぼしを防ぐ。
        lock (s_crossInstanceLock) { s_monitoringInstances.Remove(this); }

        if (!IsMonitoring && _monitorWaveIn == null)
        {
            return;
        }

        IsMonitoring = false;
        var waveIn = _monitorWaveIn;
        _monitorWaveIn = null;

        if (waveIn != null)
        {
            try
            {
                waveIn.DataAvailable -= OnMonitorDataAvailable;
                waveIn.StopRecording();
            }
            catch
            {
                // ベストエフォート: 停止処理自体の失敗で例外を伝播させない。
            }
            finally
            {
                try { waveIn.Dispose(); } catch { }
            }
        }

        lock (_monitorLock) { _monitorRms = 0.0; }
    }

    public string? Stop()
    {
        if (!IsRecording)
        {
            return null;
        }

        _autoStopTimer.Change(Timeout.Infinite, Timeout.Infinite);
        IsRecording = false;

        // 後片付けの途中で例外が出ても、登録簿からの除去だけは必ず行う (finally)。
        // ここを取りこぼすと s_recordingInstances にインスタンスが残り続け、
        // StartMonitoring の「プロセス内のどこかで録音中か」判定が永久に真になって
        // マイクレベルのモニタリングが二度と開始できなくなる (しかもエラーは
        // 「録音中です」としか出ないため、原因にたどり着けない)。
        try
        {
            if (_waveIn != null)
            {
                _waveIn.StopRecording();

                // StopRecording() は非同期にキャプチャスレッドへ停止を要求するだけで、
                // 呼び出し時点ではスレッドがまだ動作中のことがある。RecordingStopped が
                // 発火する（= キャプチャスレッドが完全に終了する）まで待ってから後片付けする
                // ことで、書き込み中の _writer を破棄してしまう競合を防ぐ。万一イベントが
                // 発火しない場合に無限待機しないよう、タイムアウト付きでベストエフォートに
                // 後片付けを進める。
                _recordingStoppedSignal.Wait(TimeSpan.FromSeconds(5));

                _waveIn.DataAvailable -= OnDataAvailable;
                _waveIn.RecordingStopped -= OnRecordingStopped;
                _waveIn.Dispose();
                _waveIn = null;
            }

            if (_writer != null)
            {
                _writer.Flush();
                _writer.Dispose();
                _writer = null;
            }
        }
        finally
        {
            // デバイスを実際に解放し終えた後に登録簿から外す (StartMonitoring 側の
            // 「プロセス内のどこかで録音中か」の判定が、デバイスがまだ解放されていない
            // 途中の状態を「録音していない」と誤認しないようにするため)。
            lock (s_crossInstanceLock) { s_recordingInstances.Remove(this); }
        }

        return _tempFilePath;
    }

    public bool IsSilence(double minDuration = 0.2, double rmsThreshold = 0.005, double peakThreshold = 0.02)
    {
        if (_totalSamples == 0) return true;

        double durationSec = (double)_totalSamples / 44100;
        if (durationSec < minDuration)
        {
            return true;
        }

        double rms = Math.Sqrt(_sumSquared / _totalSamples);
        if (rms < rmsThreshold && _peak < peakThreshold)
        {
            return true;
        }

        return false;
    }

    public void Cleanup()
    {
        if (IsRecording)
        {
            Stop();
        }

        if (_tempFilePath != null && File.Exists(_tempFilePath))
        {
            try
            {
                File.Delete(_tempFilePath);
            }
            catch { }
            _tempFilePath = null;
        }
    }

    /// <summary>
    /// %TEMP% に残った古い一時 WAV ファイル (voicein_*.wav) を削除する。
    ///
    /// 通常は Start()/Cleanup() が録音のたびに自分の一時ファイルを削除するが、
    /// プロセスのクラッシュ・強制終了・電源断ではこの後片付けが実行されず、
    /// ユーザーの生の音声データを含む WAV が %TEMP% に残り続けてしまう。
    /// App.xaml.cs の起動処理から呼び出し、そうした残骸を掃除する。
    ///
    /// ・「古い」の判定は最終更新時刻で行う。実行中の自分自身がちょうど書き込み中の
    ///   ファイルを誤って消さないよう、minAge (既定 1 時間) 以上前に更新されたものだけを
    ///   対象にする。録音は長くても数分で終わるため、1 時間の猶予があれば実行中の
    ///   ファイルと衝突することはない。
    /// ・ファイル名は Start() が生成する "voicein_{GUID}.wav" と同じパターン
    ///   ("voicein_*.wav") のみを対象にする。無関係なファイルには触れない。
    /// ・1 件の削除に失敗しても (他プロセスが使用中、権限不足等)、残りのファイルの
    ///   削除は継続する。呼び出し元 (App.xaml.cs) はこのメソッド自体が例外を投げないことを
    ///   前提にできる。
    /// </summary>
    /// <param name="tempDirectory">
    /// 掃除対象のディレクトリ。既定 (null) では Path.GetTempPath() (%TEMP%) を使う。
    /// テストから実際の %TEMP% を汚染せずに検証できるよう、差し替え可能にしている。
    /// </param>
    /// <param name="minAge">これより新しいファイルは削除しない。既定は 1 時間。</param>
    internal static void CleanupStaleTempFiles(string? tempDirectory = null, TimeSpan? minAge = null)
    {
        string directory = tempDirectory ?? Path.GetTempPath();
        TimeSpan threshold = minAge ?? TimeSpan.FromHours(1);

        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(directory, "voicein_*.wav");
        }
        catch
        {
            // ディレクトリが存在しない・列挙に失敗した場合もベストエフォートで諦める
            // (起動処理を止めないことを優先する)。
            return;
        }

        DateTime cutoffUtc = DateTime.UtcNow - threshold;

        foreach (var path in candidates)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoffUtc)
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 他プロセスが使用中・権限不足などで 1 件の削除に失敗しても、
                // 残りのファイルの掃除を継続する。
            }
        }
    }

    public void Dispose()
    {
        // モニタリング (マイクテスト) 用のマイクが開きっぱなしにならないよう、
        // 録音の後片付け (Cleanup) より前に必ず停止する。
        StopMonitoring();
        Cleanup();
        _autoStopTimer.Dispose();
        _recordingStoppedSignal.Dispose();
        GC.SuppressFinalize(this);
    }
}

// AudioRecorder.OnDataAvailable (NAudio のイベントハンドラ) に埋め込まれていた
// 「入力ゲインの適用 (dB→線形倍率変換・16bit PCM クリッピング)」と「RMS/Peak の集計」を
// 切り出した純粋関数群。NAudio の型にもマイクデバイスにも依存しないため単体テストが可能。
// [assembly: InternalsVisibleTo("VoiceIn.Tests")] (AssemblyInfo.cs) によりテストプロジェクトから
// internal のまま参照できる。
//
// 【重要】ここを変更する場合、OnDataAvailable が元々インラインで行っていた計算
// (丸め方・クリッピング範囲・RMS/Peak の正規化式) を、有効な (偶数長の) 入力については
// 1バイトも結果を変えないこと。
internal static class AudioSampleProcessor
{
    // 16bit PCM の 1 サンプルあたりのバイト数。
    private const int BytesPerSample = 2;

    /// <summary>
    /// 入力ゲイン (dB) を線形倍率に変換する。AudioRecorder.Start() が設定値 (または明示指定の
    /// 引数) から _gainMultiplier を求めるのに使う。0dB は Math.Pow の丸め誤差を避けるため
    /// 特別扱いしてちょうど 1.0 を返す (元の実装と同じ分岐)。
    /// </summary>
    internal static double DbToLinearGain(double gainDb) =>
        gainDb == 0.0 ? 1.0 : Math.Pow(10.0, gainDb / 20.0);

    /// <summary>
    /// 16bit PCM (リトルエンディアン、1サンプル2バイト) のバイト列にゲインを適用する。
    /// ・gainMultiplier が 1.0 (0dB) のときは、元の OnDataAvailable の早期分岐と同じく
    ///   無駄なコピーを避けるため buffer をそのまま返す (新規配列は作らない)。
    /// ・それ以外は新しいバイト列へゲイン適用後の値を書き込んで返す (buffer 自体は変更しない)。
    /// ・Math.Round で丸めたのち short.MinValue〜short.MaxValue にクリッピングしてからキャストする
    ///   (クリッピングを経由せず単純に (short) キャストすると、範囲外の値は符号が反転して
    ///   逆方向に振り切れてしまうため、クリッピングは省略できない)。
    /// ・bytesRecorded が奇数の場合、対になるバイトが無い末尾の1バイトは処理対象から除外し
    ///   そのままコピーする (範囲外アクセスの例外を避けるため)。16bit モノラルの実録音では
    ///   NAudio からのコールバックの BytesRecorded は常に偶数になる想定のため、この分岐が
    ///   有効な録音の計算結果に影響することはない。
    /// </summary>
    internal static byte[] ApplyGain(byte[] buffer, int bytesRecorded, double gainMultiplier)
    {
        if (gainMultiplier == 1.0)
        {
            return buffer;
        }

        var outBuffer = new byte[bytesRecorded];
        int pairedLength = bytesRecorded - (bytesRecorded % BytesPerSample);

        for (int i = 0; i < pairedLength; i += BytesPerSample)
        {
            short sample = (short)((buffer[i + 1] << 8) | buffer[i]);
            double amplified = Math.Round(sample * gainMultiplier);

            // 16bit PCM の範囲外に飛ばないようクリッピングする。
            short clipped = (short)Math.Clamp(amplified, short.MinValue, short.MaxValue);

            outBuffer[i] = (byte)(clipped & 0xFF);
            outBuffer[i + 1] = (byte)((clipped >> 8) & 0xFF);
        }

        if (pairedLength < bytesRecorded)
        {
            // 対になるバイトが無い末尾の1バイトはゲインを適用しようがないのでそのままコピーする。
            outBuffer[pairedLength] = buffer[pairedLength];
        }

        return outBuffer;
    }

    /// <summary>
    /// 16bit PCM (リトルエンディアン) のバイト列から、RMS 計算用の二乗和・Peak・サンプル数を
    /// 集計する。AudioRecorder はチャンク (DataAvailable の1回分) ごとにこの戻り値を
    /// インスタンスフィールド (_totalSamples / _sumSquared / _peak) へ積算していく。
    /// Peak は結合則を満たすため、チャンクごとの最大値を後から Math.Max していく形でも、
    /// 全サンプルを通しで見た場合と結果は変わらない。
    /// ・bytesRecorded が奇数の場合、ApplyGain と同様に対になるバイトが無い末尾の1バイトは
    ///   集計対象から除外する (範囲外アクセスの例外を避けるため)。
    /// </summary>
    internal static (long SampleCount, double SumSquared, float Peak) Aggregate(byte[] buffer, int bytesRecorded)
    {
        long sampleCount = 0;
        double sumSquared = 0;
        float peak = 0;

        int pairedLength = bytesRecorded - (bytesRecorded % BytesPerSample);

        for (int i = 0; i < pairedLength; i += BytesPerSample)
        {
            short sample = (short)((buffer[i + 1] << 8) | buffer[i]);
            float sample32 = sample / 32768f;

            float abs = Math.Abs(sample32);
            if (abs > peak) peak = abs;

            sumSquared += sample32 * sample32;
            sampleCount++;
        }

        return (sampleCount, sumSquared, peak);
    }

    /// <summary>
    /// マイク入力レベルの「モニタリング」(AudioRecorder.OnMonitorDataAvailable) 用に、
    /// 16bit PCM のバイト列へゲインを適用したうえで RMS (sqrt(mean(square(samples)))) を
    /// 1回のコールバック分としてまとめて計算する。ApplyGain → Aggregate という、録音時の
    /// OnDataAvailable と全く同じ2段階の処理を合成しただけの純粋関数であり、NAudio の型にも
    /// マイクデバイスにも依存しないため単体テストが可能。
    /// ・sampleCount が 0 (空バッファ) の場合はゼロ除算を避けるため 0.0 を返す。
    /// </summary>
    internal static double ComputeRms(byte[] buffer, int bytesRecorded, double gainMultiplier)
    {
        byte[] gained = ApplyGain(buffer, bytesRecorded, gainMultiplier);
        var (sampleCount, sumSquared, _) = Aggregate(gained, bytesRecorded);
        return sampleCount > 0 ? Math.Sqrt(sumSquared / sampleCount) : 0.0;
    }

    /// <summary>
    /// RMS 値をマイクレベルメーター (0〜100 の ProgressBar) 表示用の整数値に変換する。
    /// 移植元 Python 版 (src/ui/settings.py の _update_mic_bar) と同じ式
    /// min(100, int(rms * 300)) を使う。300 倍しているのは、通常の発話音量の RMS が
    /// 概ね 0.0〜0.3 程度に収まることを踏まえた見た目上のスケーリングであり、
    /// 100 を超えた場合は 100 に丸める (ProgressBar の Maximum を超えないようにするため)。
    /// </summary>
    internal static int RmsToBarValue(double rms) => Math.Min(100, (int)(rms * 300));
}
