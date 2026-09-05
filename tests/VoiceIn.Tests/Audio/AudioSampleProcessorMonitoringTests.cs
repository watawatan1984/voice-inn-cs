using VoiceIn.Audio;
using Xunit;

namespace VoiceIn.Tests.Audio;

/// <summary>
/// マイク入力レベルの「モニタリング」(マイクテスト機能, AudioRecorder.StartMonitoring /
/// Ui/SettingsWindow・Ui/SetupWindow のレベルメーター) のために追加した純粋計算部分
/// (AudioSampleProcessor.ComputeRms / AudioSampleProcessor.RmsToBarValue) のテスト。
///
/// AudioSampleProcessorTests.cs (既存) と同じ方針: これらのメソッドは NAudio の型にも
/// マイクデバイスにも一切依存しない純粋関数であり、[assembly: InternalsVisibleTo("VoiceIn.Tests")]
/// (AssemblyInfo.cs) によりこのテストプロジェクトから internal のまま直接呼び出せる。
///
/// 【重要】このファイルは実際にマイクデバイスを開くテストを一切含まない (CI・ローカルの
/// どちらでも安定させるため、マイクを開くテストは書かない方針)。SettingsManager.Instance /
/// HistoryManager.Instance / Logger にも一切触れない (%AppData%\VoiceIn を作らせないため)。
/// </summary>
public class AudioSampleProcessorMonitoringTests
{
    /// <summary>
    /// short の配列を 16bit PCM リトルエンディアンのバイト列に変換する。
    /// AudioSampleProcessorTests.ToPcmBytes と同じ組み立て方 (テストファイル間でヘルパーを
    /// 共有する仕組みが無いため、同じ内容をこのファイル内にも複製している)。
    /// </summary>
    private static byte[] ToPcmBytes(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            bytes[i * 2] = (byte)(samples[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xFF);
        }
        return bytes;
    }

    // ==== RmsToBarValue: RMS → レベルバー (0〜100) 変換 ====
    // 移植元 Python 版 (src/ui/settings.py の _update_mic_bar) と同じ式
    // min(100, int(rms * 300)) を検証する。

    [Fact]
    public void RmsToBarValue_ZeroRms_ReturnsZero()
    {
        int result = AudioSampleProcessor.RmsToBarValue(0.0);

        Assert.Equal(0, result);
    }

    [Fact]
    public void RmsToBarValue_SmallRms_ScalesByFactorOf300()
    {
        // 0.01 * 300 = 3 (無音に近い小さな入力でもバーがわずかに動くことを確認する)。
        int result = AudioSampleProcessor.RmsToBarValue(0.01);

        Assert.Equal(3, result);
    }

    [Fact]
    public void RmsToBarValue_RmsTimes300ExactlyReachesMaximum_ReturnsExactly100()
    {
        // 100.0/300.0 は rms*300 の計算結果がちょうど 100.0 になる (浮動小数点誤差で 99 側に
        // ぶれない) ことを事前に確認済みの境界値。クリッピングではなく計算そのものが
        // ちょうど上限に達するケースを、100 を超えるケース (次のテスト) と区別して検証する。
        double rms = 100.0 / 300.0;

        int result = AudioSampleProcessor.RmsToBarValue(rms);

        Assert.Equal(100, result);
    }

    [Fact]
    public void RmsToBarValue_JustBelowExactMaximum_ReturnsNinetyNine()
    {
        // 99.0/300.0 も同様に rms*300 がちょうど 99.0 になる境界値。ちょうど100になる
        // 直前の値でも、丸めや切り捨てで意図せず 100 や 98 にならないことを確認する。
        double rms = 99.0 / 300.0;

        int result = AudioSampleProcessor.RmsToBarValue(rms);

        Assert.Equal(99, result);
    }

    [Theory]
    [InlineData(0.5)] // 0.5 * 300 = 150 (上限を大きく超える)
    [InlineData(1.0)] // 1.0 * 300 = 300 (最大音量相当、さらに大きく超える)
    public void RmsToBarValue_RmsTimes300ExceedsMaximum_ClampsTo100(double rms)
    {
        int result = AudioSampleProcessor.RmsToBarValue(rms);

        Assert.Equal(100, result);
    }

    // ==== ComputeRms: ゲイン適用後の RMS 計算 (モニタリングのコールバックで使う) ====
    // メーターは「実際に録音される音量」を示す必要があるため、モニタリングの RMS 計算にも
    // 録音時 (AudioRecorder.OnDataAvailable) と同じ入力ゲインが反映されなければならない。
    // AudioSampleProcessorTests.cs 側の ApplyGain / Aggregate 個別のテストとは観点を変え、
    // ここでは「ComputeRms の戻り値そのものがゲイン倍率に応じて変化すること」を検証する。

    [Fact]
    public void ComputeRms_GainMultiplierOfOne_MatchesUngainedRms()
    {
        // 8000/32768 = 0.244140625 (2の冪で割り切れるため浮動小数点誤差が出ない値)。
        // 一定振幅のサンプル列の RMS はその絶対値に一致する。
        byte[] buffer = ToPcmBytes(8000, 8000);

        double rms = AudioSampleProcessor.ComputeRms(buffer, buffer.Length, gainMultiplier: 1.0);

        Assert.Equal(0.244140625, rms, 9);
    }

    [Fact]
    public void ComputeRms_DoublingGain_DoublesReportedRms()
    {
        // ゲインが2倍になれば (クリッピングに達しない範囲では) RMS も2倍になるはず。
        // 8000 * 2.0 = 16000 は short の範囲内でクリッピングされない。
        byte[] buffer = ToPcmBytes(8000, 8000);

        double rmsNoGain = AudioSampleProcessor.ComputeRms(buffer, buffer.Length, gainMultiplier: 1.0);
        double rmsDoubleGain = AudioSampleProcessor.ComputeRms(buffer, buffer.Length, gainMultiplier: 2.0);

        Assert.Equal(0.48828125, rmsDoubleGain, 9);
        Assert.Equal(rmsNoGain * 2.0, rmsDoubleGain, 9);
    }

    [Fact]
    public void ComputeRms_HalvingGain_HalvesReportedRms()
    {
        // 減衰方向 (ゲイン < 1.0) でも同様に反映されることを確認する。
        byte[] buffer = ToPcmBytes(8000, 8000);

        double rmsNoGain = AudioSampleProcessor.ComputeRms(buffer, buffer.Length, gainMultiplier: 1.0);
        double rmsHalfGain = AudioSampleProcessor.ComputeRms(buffer, buffer.Length, gainMultiplier: 0.5);

        Assert.Equal(0.1220703125, rmsHalfGain, 9);
        Assert.Equal(rmsNoGain * 0.5, rmsHalfGain, 9);
    }

    [Fact]
    public void ComputeRms_GainCausesClipping_RmsReflectsClippedValueNotRawMultiplication()
    {
        // short.MaxValue 付近のサンプルに大きなゲインをかけると、AudioSampleProcessor.ApplyGain の
        // クリッピングにより short.MaxValue で頭打ちになる。ComputeRms がこのクリッピング後の
        // 値を使って RMS を計算していること (単純に「無ゲインの RMS × 倍率」にはならないこと) を
        // 確認する。
        byte[] buffer = ToPcmBytes(30000, 30000); // 30000 * 2.0 = 60000 は short の範囲外

        double rmsHugeGain = AudioSampleProcessor.ComputeRms(buffer, buffer.Length, gainMultiplier: 2.0);
        double expectedClippedRms = (double)short.MaxValue / 32768.0;

        Assert.Equal(expectedClippedRms, rmsHugeGain, 9);
    }

    [Fact]
    public void ComputeRms_SilentInput_ReturnsZeroRegardlessOfGain()
    {
        byte[] buffer = ToPcmBytes(0, 0, 0, 0);

        double rms = AudioSampleProcessor.ComputeRms(buffer, buffer.Length, gainMultiplier: 5.0);

        Assert.Equal(0.0, rms);
    }

    [Fact]
    public void ComputeRms_EmptyBuffer_ReturnsZero_DoesNotDivideByZero()
    {
        // sampleCount が 0 になる境界。ComputeRms 内部で sumSquared/sampleCount のゼロ除算
        // (NaN) を避けるガードが効いていることを確認する。
        var exception = Record.Exception(() =>
            AudioSampleProcessor.ComputeRms([], 0, gainMultiplier: 1.0));

        Assert.Null(exception);
        Assert.Equal(0.0, AudioSampleProcessor.ComputeRms([], 0, gainMultiplier: 1.0));
    }
}
