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
        _gainMultiplier = gainDb == 0.0 ? 1.0 : Math.Pow(10.0, gainDb / 20.0);

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
        byte[] outBuffer = e.Buffer;

        // 0dB (倍率 1.0) のときは無駄な計算を避けるため早期分岐し、元のバッファをそのまま使う。
        if (_gainMultiplier != 1.0)
        {
            outBuffer = new byte[bytesRecorded];
            for (int i = 0; i < bytesRecorded; i += 2)
            {
                short sample = (short)((e.Buffer[i + 1] << 8) | e.Buffer[i]);
                double amplified = Math.Round(sample * _gainMultiplier);

                // 16bit PCM の範囲外に飛ばないようクリッピングする。
                short clipped = (short)Math.Clamp(amplified, short.MinValue, short.MaxValue);

                outBuffer[i] = (byte)(clipped & 0xFF);
                outBuffer[i + 1] = (byte)((clipped >> 8) & 0xFF);
            }
        }

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

        // ゲイン適用後の値で RMS / Peak を集計する。
        for (int i = 0; i < bytesRecorded; i += 2)
        {
            short sample = (short)((outBuffer[i + 1] << 8) | outBuffer[i]);
            float sample32 = sample / 32768f;

            float abs = Math.Abs(sample32);
            if (abs > _peak) _peak = abs;

            _sumSquared += sample32 * sample32;
            _totalSamples++;
        }
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
