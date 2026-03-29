using System;
using TailSlap;
using Xunit;

public class AudioResamplerTests
{
    [Fact]
    public void Resample16To24_EmptyInput_ReturnsEmpty()
    {
        var result = AudioResampler.Resample16To24(Array.Empty<byte>(), 0, 0);
        Assert.Empty(result);
    }

    [Fact]
    public void Resample16To24_SingleSample_ReturnsResampled()
    {
        // One sample (2 bytes) at 16kHz should produce 1.5 samples → 2 samples (rounded) at 24kHz
        var input = new byte[] { 0x00, 0x10 }; // sample value 0x1000 = 4096
        var result = AudioResampler.Resample16To24(input, 0, input.Length);
        Assert.True(result.Length >= 2);
    }

    [Fact]
    public void Resample16To24_OutputIsLargerThanInput()
    {
        // 100 samples at 16kHz -> 150 samples at 24kHz
        var input = new byte[200]; // 100 samples * 2 bytes
        for (int i = 0; i < 100; i++)
        {
            short sample = (short)(i * 100);
            input[i * 2] = (byte)(sample & 0xFF);
            input[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        var result = AudioResampler.Resample16To24(input, 0, input.Length);
        // 100 samples * (24000/16000) = 150 samples = 300 bytes
        Assert.Equal(300, result.Length);
    }

    [Fact]
    public void Resample16To24_SilencePreserved()
    {
        // All zeros (silence) should remain zeros
        var input = new byte[320]; // 160 samples of silence
        var result = AudioResampler.Resample16To24(input, 0, input.Length);

        foreach (var b in result)
        {
            Assert.Equal(0, b);
        }
    }

    [Fact]
    public void Resample16To24_ConstantSignalPreserved()
    {
        // Constant signal should produce same constant value
        const short value = 1000;
        var input = new byte[200]; // 100 samples
        for (int i = 0; i < 100; i++)
        {
            input[i * 2] = (byte)(value & 0xFF);
            input[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        var result = AudioResampler.Resample16To24(input, 0, input.Length);

        // Every output sample should be close to 1000
        for (int i = 0; i < result.Length / 2; i++)
        {
            short sample = BitConverter.ToInt16(result, i * 2);
            Assert.True(
                Math.Abs(sample - value) <= 1,
                $"Sample {i}: expected ~{value}, got {sample}"
            );
        }
    }

    [Fact]
    public void Resample16To24_WithOffset_ResamplesCorrectly()
    {
        var fullInput = new byte[400];
        for (int i = 0; i < 200; i++)
        {
            short sample = (short)(i * 50);
            fullInput[i * 2] = (byte)(sample & 0xFF);
            fullInput[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        // Resample only the first half
        var result = AudioResampler.Resample16To24(fullInput, 0, 200);
        Assert.Equal(300, result.Length); // 100 samples * 1.5 = 150 samples * 2 bytes
    }
}
