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
    private long _totalSamples = 0;
    private double _sumSquared = 0;
    private float _peak = 0;

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

    public void Start(int? deviceIndex = null, int maxSeconds = 60)
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

        _waveIn = new WaveIn
        {
            WaveFormat = new WaveFormat(44100, 16, 1) // 44.1kHz, 16bit, Mono
        };

        if (deviceIndex.HasValue && deviceIndex.Value >= 0 && deviceIndex.Value < WaveIn.DeviceCount)
        {
            _waveIn.DeviceNumber = deviceIndex.Value;
        }

        _writer = new WaveFileWriter(_tempFilePath, _waveIn.WaveFormat);
        _waveIn.DataAvailable += OnDataAvailable;

        _waveIn.StartRecording();
        IsRecording = true;

        if (maxSeconds > 0)
        {
            _autoStopTimer.Change(TimeSpan.FromSeconds(maxSeconds), Timeout.InfiniteTimeSpan);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_writer == null) return;

        _writer.Write(e.Buffer, 0, e.BytesRecorded);

        // ゲインとRMS / Peak の集計
        for (int i = 0; i < e.BytesRecorded; i += 2)
        {
            short sample = (short)((e.Buffer[i + 1] << 8) | e.Buffer[i]);
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
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.StopRecording();
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
        GC.SuppressFinalize(this);
    }
}
