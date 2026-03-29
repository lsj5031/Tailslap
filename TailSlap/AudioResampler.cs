using System;

namespace TailSlap;

public static class AudioResampler
{
    public static byte[] Resample16To24(byte[] pcm16, int offset, int count)
    {
        if (count < 2)
            return Array.Empty<byte>();

        int sourceSamples = count / 2;
        int targetSamples = (int)((long)sourceSamples * 24000 / 16000);
        byte[] result = new byte[targetSamples * 2];

        for (int i = 0; i < targetSamples; i++)
        {
            double srcIndex = (double)i * sourceSamples / targetSamples;
            int srcIndexFloor = (int)srcIndex;

            if (srcIndexFloor >= sourceSamples - 1)
            {
                short lastSample = BitConverter.ToInt16(pcm16, offset + (sourceSamples - 1) * 2);
                result[i * 2] = (byte)(lastSample & 0xFF);
                result[i * 2 + 1] = (byte)((lastSample >> 8) & 0xFF);
                continue;
            }

            double frac = srcIndex - srcIndexFloor;
            int srcOffset = offset + srcIndexFloor * 2;
            short s0 = BitConverter.ToInt16(pcm16, srcOffset);
            short s1 = BitConverter.ToInt16(pcm16, srcOffset + 2);
            double interpolated = s0 + (s1 - s0) * frac;
            short outSample = (short)Math.Round(interpolated);
            result[i * 2] = (byte)(outSample & 0xFF);
            result[i * 2 + 1] = (byte)((outSample >> 8) & 0xFF);
        }

        return result;
    }
}
