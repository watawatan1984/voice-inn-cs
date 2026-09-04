using System;
using VoiceIn.Audio;
using Xunit;

namespace VoiceIn.Tests.Audio;

/// <summary>
/// AudioRecorder.OnDataAvailable (NAudio のイベントハンドラ) から切り出した純粋計算部分
/// (AudioSampleProcessor) のテスト。
///
/// AudioSampleProcessor は internal だが、AssemblyInfo.cs の
/// [assembly: InternalsVisibleTo("VoiceIn.Tests")] によりこのテストプロジェクトから
/// 直接参照できる。NAudio の型やマイクデバイスには一切依存しないため、
/// マイク・ハードウェア・実際の録音なしで検証できる。
///
/// バイト列はすべて 16bit PCM のリトルエンディアン (下位バイトが先、上位バイトが後) を
/// 前提とする。ToPcmBytes / ReadPcmSample は AudioSampleProcessor 自身のビット演算
/// ((buffer[i+1] &lt;&lt; 8) | buffer[i]) と同じ組み立て方をするヘルパーで、
/// BitConverter のプラットフォーム依存動作には頼らない。
/// </summary>
public class AudioSampleProcessorTests
{
    /// <summary>
    /// short の配列を 16bit PCM リトルエンディアンのバイト列に変換する。
    /// AudioSampleProcessor 自身の書き込み処理 ((byte)(v &amp; 0xFF), (byte)((v &gt;&gt; 8) &amp; 0xFF)) と
    /// 対になるテスト用ヘルパー。
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

    /// <summary>16bit PCM リトルエンディアンのバイト列から、指定バイト位置の1サンプルを読む。</summary>
    private static short ReadPcmSample(byte[] buffer, int byteIndex) =>
        (short)((buffer[byteIndex + 1] << 8) | buffer[byteIndex]);

    // ==== DbToLinearGain: dB → 線形倍率変換 ====

    [Fact]
    public void DbToLinearGain_ZeroDb_ReturnsExactlyOne()
    {
        double result = AudioSampleProcessor.DbToLinearGain(0.0);

        // Math.Pow(10, 0/20) も理論上は 1.0 になるはずだが、実装は丸め誤差を避けるため
        // 0dB を特別扱いしている。ここでは誤差なくちょうど 1.0 になることを確認する。
        Assert.Equal(1.0, result);
    }

    [Theory]
    [InlineData(20.0, 10.0)]   // +20dB = 10倍
    [InlineData(40.0, 100.0)]  // +40dB = 100倍
    public void DbToLinearGain_PositiveDb_AmplifiesByExpectedFactor(double gainDb, double expectedMultiplier)
    {
        double result = AudioSampleProcessor.DbToLinearGain(gainDb);

        Assert.True(result > 1.0);
        Assert.Equal(expectedMultiplier, result, 9);
    }

    [Theory]
    [InlineData(-20.0, 0.1)]   // -20dB = 1/10
    [InlineData(-40.0, 0.01)]  // -40dB = 1/100
    public void DbToLinearGain_NegativeDb_AttenuatesByExpectedFactor(double gainDb, double expectedMultiplier)
    {
        double result = AudioSampleProcessor.DbToLinearGain(gainDb);

        Assert.True(result < 1.0);
        Assert.Equal(expectedMultiplier, result, 9);
    }

    // ==== ApplyGain: 0dB / 増幅 / 減衰 ====

    [Fact]
    public void ApplyGain_GainMultiplierIsOne_ReturnsSameBufferInstanceUnchanged()
    {
        byte[] buffer = ToPcmBytes(1000, -1000, short.MaxValue, short.MinValue);

        byte[] result = AudioSampleProcessor.ApplyGain(buffer, buffer.Length, gainMultiplier: 1.0);

        // OnDataAvailable の元の早期分岐 (倍率1.0のときは無駄なコピーをしない) を
        // 維持していることを、参照そのものが同一であることで確認する。
        Assert.Same(buffer, result);
    }

    [Fact]
    public void ApplyGain_PositiveGain_AmplifiesSample()
    {
        byte[] buffer = ToPcmBytes(1000);

        byte[] result = AudioSampleProcessor.ApplyGain(buffer, buffer.Length, gainMultiplier: 2.0);

        Assert.Equal(2000, (int)ReadPcmSample(result, 0));
    }

    [Fact]
    public void ApplyGain_GainBelowOne_AttenuatesSample()
    {
        byte[] buffer = ToPcmBytes(1000);

        byte[] result = AudioSampleProcessor.ApplyGain(buffer, buffer.Length, gainMultiplier: 0.5);

        Assert.Equal(500, (int)ReadPcmSample(result, 0));
    }

    [Fact]
    public void ApplyGain_DoesNotMutateInputBuffer()
    {
        byte[] buffer = ToPcmBytes(1000, -12345);
        byte[] original = (byte[])buffer.Clone();

        AudioSampleProcessor.ApplyGain(buffer, buffer.Length, gainMultiplier: 3.0);

        Assert.Equal(original, buffer);
    }

    // ==== ApplyGain: クリッピング (short の範囲を超える入力の扱い。最重要) ====

    [Fact]
    public void ApplyGain_PositiveOverflow_ClipsToShortMaxValue_DoesNotWrapToNegative()
    {
        // 20000 * 2.0 = 40000 は short の範囲 (最大 32767) を超える。
        // 単純に (short) キャストするだけだと符号が反転して負の値に「折り返って」しまうため、
        // クリッピングによって正しく short.MaxValue に丸められることを確認する。
        byte[] buffer = ToPcmBytes(20000);

        byte[] result = AudioSampleProcessor.ApplyGain(buffer, buffer.Length, gainMultiplier: 2.0);

        Assert.Equal(short.MaxValue, ReadPcmSample(result, 0));
    }

    [Fact]
    public void ApplyGain_NegativeOverflow_ClipsToShortMinValue_DoesNotWrapToPositive()
    {
        // -20000 * 2.0 = -40000 は short の範囲 (最小 -32768) を超える。
        // クリッピングなしだと符号が反転して正の値に「折り返って」しまう。
        byte[] buffer = ToPcmBytes(-20000);

        byte[] result = AudioSampleProcessor.ApplyGain(buffer, buffer.Length, gainMultiplier: 2.0);

        Assert.Equal(short.MinValue, ReadPcmSample(result, 0));
    }

    [Fact]
    public void ApplyGain_SampleAlreadyAtShortMaxValue_StaysClippedAtShortMaxValue()
    {
        byte[] buffer = ToPcmBytes(short.MaxValue);

        byte[] result = AudioSampleProcessor.ApplyGain(buffer, buffer.Length, gainMultiplier: 1.5);

        Assert.Equal(short.MaxValue, ReadPcmSample(result, 0));
    }

    [Fact]
    public void ApplyGain_SampleAlreadyAtShortMinValue_StaysClippedAtShortMinValue()
    {
        byte[] buffer = ToPcmBytes(short.MinValue);

        byte[] result = AudioSampleProcessor.ApplyGain(buffer, buffer.Length, gainMultiplier: 1.5);

        Assert.Equal(short.MinValue, ReadPcmSample(result, 0));
    }

    // ==== ApplyGain: 無音・境界入力 ====

    [Fact]
    public void ApplyGain_SilentInput_RemainsZeroRegardlessOfGain()
    {
        byte[] buffer = ToPcmBytes(0, 0, 0, 0);

        byte[] result = AudioSampleProcessor.ApplyGain(buffer, buffer.Length, gainMultiplier: 3.5);

        Assert.All(result, b => Assert.Equal(0, (int)b));
    }

    [Fact]
    public void ApplyGain_OddLengthBuffer_DoesNotThrow_AndCopiesTrailingByteUnchanged()
    {
        // NAudio からの実際のコールバックでは 16bit モノラルである以上 BytesRecorded は
        // 常に偶数になる想定だが、境界的な入力 (奇数長) を渡されても現在の実装は
        // 例外を投げてはならない。buffer の長さを bytesRecorded ぴったりにしているのは、
        // 対になるはずのバイトが存在しない末尾の1バイトへアクセスしたときに真っ先に
        // 範囲外例外になる、最も厳しい条件を再現するため
        // (実際、この境界対応を入れる前の実装ではここが IndexOutOfRangeException になっていた。
        // 詳細はレポート「検出した本体の不具合」を参照)。
        byte[] pairedBytes = ToPcmBytes(1000, 2000); // 4バイト
        byte[] oddBuffer = new byte[5];
        Array.Copy(pairedBytes, oddBuffer, 4);
        oddBuffer[4] = 0xAB; // 対になるバイトが無い末尾の1バイト

        byte[]? result = null;
        var exception = Record.Exception(() =>
            result = AudioSampleProcessor.ApplyGain(oddBuffer, oddBuffer.Length, gainMultiplier: 2.0));

        Assert.Null(exception);
        Assert.NotNull(result);
        Assert.Equal(5, result!.Length);
        Assert.Equal(2000, (int)ReadPcmSample(result, 0)); // 1000 * 2.0
        Assert.Equal(4000, (int)ReadPcmSample(result, 2)); // 2000 * 2.0
        Assert.Equal(0xAB, (int)result[4]);                // 末尾の余りバイトはそのままコピーされる
    }

    // ==== Aggregate: RMS / Peak 集計 ====

    [Fact]
    public void Aggregate_SilentInput_ReturnsZeroSumSquaredAndZeroPeak()
    {
        byte[] buffer = ToPcmBytes(0, 0, 0);

        var (sampleCount, sumSquared, peak) = AudioSampleProcessor.Aggregate(buffer, buffer.Length);

        Assert.Equal(3L, sampleCount);
        Assert.Equal(0.0, sumSquared);
        Assert.Equal(0f, peak);
    }

    [Fact]
    public void Aggregate_EmptyBuffer_ReturnsAllZero()
    {
        var (sampleCount, sumSquared, peak) = AudioSampleProcessor.Aggregate(Array.Empty<byte>(), 0);

        Assert.Equal(0L, sampleCount);
        Assert.Equal(0.0, sumSquared);
        Assert.Equal(0f, peak);
    }

    [Fact]
    public void Aggregate_KnownSamples_ComputesExpectedPeakAndSumSquared()
    {
        // 16384/32768 = 0.5、-8192/32768 = -0.25 (絶対値 0.25)。
        // Peak は絶対値の最大 (0.5)、SumSquared は 0.5^2 + 0.25^2 = 0.3125 になるはず。
        byte[] buffer = ToPcmBytes(16384, -8192);

        var (sampleCount, sumSquared, peak) = AudioSampleProcessor.Aggregate(buffer, buffer.Length);

        Assert.Equal(2L, sampleCount);
        Assert.Equal(0.5f, peak, 6);
        Assert.Equal(0.3125, sumSquared, 6);
    }

    [Fact]
    public void Aggregate_MinShortValue_PeakIsExactlyOne()
    {
        // short.MinValue (-32768) / 32768 = -1.0 ちょうど。絶対値の Peak は 1.0 になる
        // (short.MaxValue 側は 32767/32768 なので、ちょうど1.0になるのはこちらだけ)。
        byte[] buffer = ToPcmBytes(short.MinValue);

        var (_, _, peak) = AudioSampleProcessor.Aggregate(buffer, buffer.Length);

        Assert.Equal(1.0f, peak);
    }

    [Fact]
    public void Aggregate_MaxShortValue_PeakIsJustBelowOne()
    {
        // short.MaxValue (32767) / 32768 は 1.0 にわずかに届かない。
        byte[] buffer = ToPcmBytes(short.MaxValue);

        var (_, _, peak) = AudioSampleProcessor.Aggregate(buffer, buffer.Length);

        Assert.True(peak < 1.0f);
        Assert.Equal(32767f / 32768f, peak, 6);
    }

    [Fact]
    public void Aggregate_ComputesRmsMatchingExpectedFormula()
    {
        // AudioRecorder.IsSilence() と同じ式 (Math.Sqrt(sumSquared / totalSamples)) で
        // RMS を計算し、一定振幅のサンプル列に対して期待どおりの値になることを確認する。
        // 4サンプルすべて 16384 (0.5) なので RMS も 0.5 になるはず。
        byte[] buffer = ToPcmBytes(16384, 16384, 16384, 16384);

        var (sampleCount, sumSquared, _) = AudioSampleProcessor.Aggregate(buffer, buffer.Length);
        double rms = Math.Sqrt(sumSquared / sampleCount);

        Assert.Equal(0.5, rms, 6);
    }

    [Fact]
    public void Aggregate_OddLengthBuffer_DoesNotThrow_AndIgnoresTrailingByte()
    {
        byte[] pairedBytes = ToPcmBytes(16384, 16384); // 4バイト
        byte[] oddBuffer = new byte[5];
        Array.Copy(pairedBytes, oddBuffer, 4);
        oddBuffer[4] = 0xFF; // 対になるバイトが無い末尾の1バイト

        (long SampleCount, double SumSquared, float Peak)? result = null;
        var exception = Record.Exception(() =>
            result = AudioSampleProcessor.Aggregate(oddBuffer, oddBuffer.Length));

        Assert.Null(exception);
        Assert.NotNull(result);
        Assert.Equal(2L, result!.Value.SampleCount); // 末尾の1バイトはサンプルとして数えない
    }
}
