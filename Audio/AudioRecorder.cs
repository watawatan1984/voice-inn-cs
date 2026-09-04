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

    public bool IsRecording { get; private set; }
    public event Action? AutoStopRequested;

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

    public string? Stop()
    {
        if (!IsRecording)
        {
            return null;
        }

        _autoStopTimer.Change(Timeout.Infinite, Timeout.Infinite);
        IsRecording = false;

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

    public void Dispose()
    {
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
}
