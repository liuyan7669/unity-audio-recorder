using System;

namespace Cowart.AudioRecorder
{
    internal sealed class NormalizedPcmAudio
    {
        public NormalizedPcmAudio(float[] samples, int sampleRate, byte[] wavBytes)
        {
            Samples = samples;
            SampleRate = sampleRate;
            WavBytes = wavBytes;
        }

        public float[] Samples { get; }

        public int SampleRate { get; }

        public byte[] WavBytes { get; }
    }

    internal static class Pcm16WavUtility
    {
        private const int WavHeaderSize = 44;

        public static NormalizedPcmAudio NormalizeAndEncode(
            float[] interleavedSamples,
            int frameCount,
            int channels,
            int sourceSampleRate,
            int targetSampleRate)
        {
            if (interleavedSamples == null)
            {
                throw new ArgumentNullException(nameof(interleavedSamples));
            }

            if (frameCount <= 0 || channels <= 0 || sourceSampleRate <= 0 || targetSampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frameCount), "Audio dimensions and sample rates must be positive.");
            }

            if ((long)frameCount * channels > interleavedSamples.Length)
            {
                throw new ArgumentException("The sample array is shorter than the declared audio frame count.", nameof(interleavedSamples));
            }

            float[] monoSamples = NormalizeToMono(
                interleavedSamples,
                frameCount,
                channels,
                sourceSampleRate,
                targetSampleRate);

            return new NormalizedPcmAudio(
                monoSamples,
                targetSampleRate,
                EncodeMonoPcm16(monoSamples, targetSampleRate));
        }

        public static float[] NormalizeToMono(
            float[] interleavedSamples,
            int frameCount,
            int channels,
            int sourceSampleRate,
            int targetSampleRate)
        {
            if (interleavedSamples == null)
            {
                throw new ArgumentNullException(nameof(interleavedSamples));
            }

            if (frameCount <= 0 || channels <= 0 || sourceSampleRate <= 0 || targetSampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frameCount), "Audio dimensions and sample rates must be positive.");
            }

            if ((long)frameCount * channels > interleavedSamples.Length)
            {
                throw new ArgumentException("The sample array is shorter than the declared audio frame count.", nameof(interleavedSamples));
            }

            int outputFrameCount = Math.Max(
                1,
                (int)Math.Round(frameCount * (double)targetSampleRate / sourceSampleRate));
            float[] monoSamples = new float[outputFrameCount];
            double sourceFramesPerOutputFrame = sourceSampleRate / (double)targetSampleRate;

            for (int outputFrame = 0; outputFrame < outputFrameCount; outputFrame++)
            {
                double sourcePosition = outputFrame * sourceFramesPerOutputFrame;
                int firstFrame = Math.Min((int)sourcePosition, frameCount - 1);
                int secondFrame = Math.Min(firstFrame + 1, frameCount - 1);
                float interpolation = (float)(sourcePosition - firstFrame);
                float firstSample = ReadMonoFrame(interleavedSamples, firstFrame, channels);
                float secondSample = ReadMonoFrame(interleavedSamples, secondFrame, channels);
                monoSamples[outputFrame] = Clamp(firstSample + ((secondSample - firstSample) * interpolation));
            }

            return monoSamples;
        }

        public static byte[] EncodePcm16Data(float[] samples)
        {
            if (samples == null)
            {
                throw new ArgumentNullException(nameof(samples));
            }

            byte[] pcmBytes = new byte[checked(samples.Length * sizeof(short))];
            int destinationIndex = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                float sample = Clamp(samples[i]);
                short pcmValue = sample < 0f
                    ? (short)Math.Round(sample * 32768f)
                    : (short)Math.Round(sample * 32767f);
                WriteInt16(pcmBytes, destinationIndex, pcmValue);
                destinationIndex += sizeof(short);
            }

            return pcmBytes;
        }

        private static byte[] EncodeMonoPcm16(float[] samples, int sampleRate)
        {
            byte[] pcmBytes = EncodePcm16Data(samples);
            int dataSize = pcmBytes.Length;
            byte[] wavBytes = new byte[checked(WavHeaderSize + dataSize)];

            WriteAscii(wavBytes, 0, "RIFF");
            WriteInt32(wavBytes, 4, 36 + dataSize);
            WriteAscii(wavBytes, 8, "WAVE");
            WriteAscii(wavBytes, 12, "fmt ");
            WriteInt32(wavBytes, 16, 16);
            WriteInt16(wavBytes, 20, 1);
            WriteInt16(wavBytes, 22, 1);
            WriteInt32(wavBytes, 24, sampleRate);
            WriteInt32(wavBytes, 28, sampleRate * sizeof(short));
            WriteInt16(wavBytes, 32, sizeof(short));
            WriteInt16(wavBytes, 34, 16);
            WriteAscii(wavBytes, 36, "data");
            WriteInt32(wavBytes, 40, dataSize);

            Buffer.BlockCopy(pcmBytes, 0, wavBytes, WavHeaderSize, dataSize);

            return wavBytes;
        }

        private static float ReadMonoFrame(float[] samples, int frameIndex, int channels)
        {
            int sampleIndex = frameIndex * channels;
            float sum = 0f;
            for (int channel = 0; channel < channels; channel++)
            {
                sum += samples[sampleIndex + channel];
            }

            return sum / channels;
        }

        private static float Clamp(float value)
        {
            return Math.Max(-1f, Math.Min(1f, value));
        }

        private static void WriteAscii(byte[] destination, int offset, string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                destination[offset + i] = (byte)value[i];
            }
        }

        private static void WriteInt16(byte[] destination, int offset, int value)
        {
            destination[offset] = (byte)value;
            destination[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteInt32(byte[] destination, int offset, int value)
        {
            destination[offset] = (byte)value;
            destination[offset + 1] = (byte)(value >> 8);
            destination[offset + 2] = (byte)(value >> 16);
            destination[offset + 3] = (byte)(value >> 24);
        }
    }
}
