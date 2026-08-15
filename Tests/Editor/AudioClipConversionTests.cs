using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cowart.AudioRecorder.Tests
{
    public sealed class AudioClipConversionTests
    {
        [Test]
        public void Pcm16WavData_DecodesMonoBoundaryValues()
        {
            byte[] wavBytes = BuildPcm16Wav(
                16000,
                1,
                new short[] { short.MinValue, 0, short.MaxValue });

            Pcm16WavData wavData = Pcm16WavData.Parse(wavBytes);
            float[] samples = wavData.DecodeAll(CancellationToken.None);

            Assert.That(wavData.FrameCount, Is.EqualTo(3));
            Assert.That(wavData.Channels, Is.EqualTo(1));
            Assert.That(wavData.SampleRate, Is.EqualTo(16000));
            Assert.That(samples[0], Is.EqualTo(-1f));
            Assert.That(samples[1], Is.EqualTo(0f));
            Assert.That(samples[2], Is.EqualTo(32767f / 32768f).Within(0.000001f));
        }

        [Test]
        public void Pcm16WavData_PreservesInterleavedStereoAndSkipsOddChunk()
        {
            byte[] wavBytes = BuildPcm16Wav(
                44100,
                2,
                new short[] { short.MinValue, short.MaxValue, 16384, -16384 },
                true,
                18);

            Pcm16WavData wavData = Pcm16WavData.Parse(wavBytes);
            float[] samples = wavData.DecodeAll(CancellationToken.None);

            Assert.That(wavData.FrameCount, Is.EqualTo(2));
            Assert.That(wavData.Channels, Is.EqualTo(2));
            Assert.That(samples, Is.EqualTo(new[]
            {
                -1f,
                32767f / 32768f,
                0.5f,
                -0.5f
            }).Within(0.000001f));
        }

        [Test]
        public void Pcm16WavData_RejectsTruncatedAndUnsupportedFiles()
        {
            byte[] truncated = BuildPcm16Wav(16000, 1, new short[] { 1, 2 });
            Array.Resize(ref truncated, truncated.Length - 1);
            Assert.Throws<InvalidDataException>(() => Pcm16WavData.Parse(truncated));

            byte[] compressed = BuildPcm16Wav(16000, 1, new short[] { 1, 2 });
            compressed[20] = 3;
            Assert.Throws<NotSupportedException>(() => Pcm16WavData.Parse(compressed));

            byte[] badBlockAlignment = BuildPcm16Wav(16000, 1, new short[] { 1, 2 });
            badBlockAlignment[32] = 4;
            Assert.Throws<InvalidDataException>(() => Pcm16WavData.Parse(badBlockAlignment));
        }

        [Test]
        public void Pcm16WavClipReader_StreamsAndPadsEndWithSilence()
        {
            Pcm16WavData wavData = Pcm16WavData.Parse(BuildPcm16Wav(
                16000,
                1,
                new short[] { short.MinValue, 0, short.MaxValue }));
            Pcm16WavClipReader reader = new Pcm16WavClipReader(wavData);
            float[] destination = new float[5];

            reader.Read(destination);

            Assert.That(destination[0], Is.EqualTo(-1f));
            Assert.That(destination[1], Is.EqualTo(0f));
            Assert.That(destination[2], Is.EqualTo(32767f / 32768f).Within(0.000001f));
            Assert.That(destination[3], Is.EqualTo(0f));
            Assert.That(destination[4], Is.EqualTo(0f));
        }

        [Test]
        public void RealtimeAudioClipStream_WritesReadsAndDrainsPcm16()
        {
            Pcm16AudioClipStream stream = AudioRecorder.CreateRealtimeAudioClipStream(
                sampleRate: 8000,
                channels: 1,
                bufferCapacityMilliseconds: 100,
                prebufferMilliseconds: 20);
            try
            {
                byte[] pcm = ToPcm16Bytes(short.MinValue, 0, short.MaxValue);
                Assert.That(stream.TryWritePcm16(pcm, true), Is.True);
                Assert.That(stream.IsReadyToPlay, Is.True);
                Assert.That(stream.IsInputCompleted, Is.True);

                float[] destination = new float[4];
                stream.ReadPcmData(destination);

                Assert.That(destination[0], Is.EqualTo(-1f));
                Assert.That(destination[1], Is.EqualTo(0f));
                Assert.That(destination[2], Is.EqualTo(32767f / 32768f).Within(0.000001f));
                Assert.That(destination[3], Is.EqualTo(0f));
                Assert.That(stream.BufferedFrames, Is.EqualTo(0));
                Assert.That(stream.IsDrained, Is.True);
            }
            finally
            {
                stream.Dispose();
            }
        }

        [Test]
        public void RealtimeAudioClipStream_DropsWholeChunkOnOverflow()
        {
            Pcm16AudioClipStream stream = AudioRecorder.CreateRealtimeAudioClipStream(
                sampleRate: 8000,
                channels: 1,
                bufferCapacityMilliseconds: 100,
                prebufferMilliseconds: 20);
            try
            {
                byte[] tooLarge = new byte[(stream.CapacityFrames + 1) * sizeof(short)];

                Assert.That(stream.TryWritePcm16(tooLarge), Is.False);
                Assert.That(stream.BufferedFrames, Is.EqualTo(0));
                Assert.That(stream.DroppedFrames, Is.EqualTo(stream.CapacityFrames + 1));
            }
            finally
            {
                stream.Dispose();
            }
        }

        [Test]
        public void RealtimeAudioClipStream_FinalOverflowCanBeRetriedOrCompletedExplicitly()
        {
            Pcm16AudioClipStream stream = AudioRecorder.CreateRealtimeAudioClipStream(
                sampleRate: 8000,
                channels: 1,
                bufferCapacityMilliseconds: 100,
                prebufferMilliseconds: 20);
            try
            {
                byte[] tooLarge = new byte[(stream.CapacityFrames + 1) * sizeof(short)];

                Assert.That(stream.TryWritePcm16(tooLarge, true), Is.False);
                Assert.That(stream.IsInputCompleted, Is.False);

                stream.CompleteInput();
                Assert.That(stream.IsInputCompleted, Is.True);
                Assert.That(stream.IsDrained, Is.True);
            }
            finally
            {
                stream.Dispose();
            }
        }

        [Test]
        public void RealtimeAudioClipStream_RejectsOversizedSingleWrite()
        {
            Pcm16AudioClipStream stream = AudioRecorder.CreateRealtimeAudioClipStream(
                sampleRate: 16000,
                channels: 1,
                bufferCapacityMilliseconds: 2000,
                prebufferMilliseconds: 20);
            try
            {
                byte[] tooLarge = new byte[
                    (AudioRecorder.MaximumRealtimeWriteFrames + 1) * sizeof(short)];

                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    stream.TryWritePcm16(tooLarge));
            }
            finally
            {
                stream.Dispose();
            }
        }

        [Test]
        public void CreateRealtimeAudioClipStream_RejectsOversizedRingAllocation()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                AudioRecorder.CreateRealtimeAudioClipStream(
                    sampleRate: 192000,
                    channels: 8,
                    bufferCapacityMilliseconds: 2000,
                    prebufferMilliseconds: 120));
        }

        [Test]
        public void RealtimeAudioClipStream_RejectsMismatchedChunkFormat()
        {
            Pcm16AudioClipStream stream = AudioRecorder.CreateRealtimeAudioClipStream();
            try
            {
                AudioStreamChunk chunk = new AudioStreamChunk(
                    Array.Empty<byte>(),
                    0,
                    0,
                    8000,
                    1,
                    16,
                    true,
                    true);

                Assert.Throws<ArgumentException>(() => stream.TryWrite(chunk));
            }
            finally
            {
                stream.Dispose();
            }
        }

        [Test]
        public void CreateStreamingAudioClipFromPcm16Wav_UsesWavDimensions()
        {
            AudioClip clip = null;
            try
            {
                clip = AudioRecorder.CreateStreamingAudioClipFromPcm16Wav(
                    BuildPcm16Wav(
                        22050,
                        2,
                        new short[] { 1, -1, 2, -2 }),
                    "Streaming PCM16 Test");

                Assert.That(clip.samples, Is.EqualTo(2));
                Assert.That(clip.channels, Is.EqualTo(2));
                Assert.That(clip.frequency, Is.EqualTo(22050));
            }
            finally
            {
                AudioClipCreationScheduler.DestroyAudioClip(clip);
            }
        }

        [UnityTest]
        public IEnumerator CreateAudioClipFromPcm16WavAsync_CreatesMaterializedClip()
        {
            byte[] wavBytes = BuildPcm16Wav(
                16000,
                1,
                new short[] { short.MinValue, 0, short.MaxValue, 16384 });
            Task<AudioClip> task = AudioRecorder.CreateAudioClipFromPcm16WavAsync(
                wavBytes,
                "Async PCM16 Test",
                AudioRecorder.MinimumAudioClipDecodeFramesPerUpdate);
            AudioClip clip = null;

            try
            {
                int frameBudget = 300;
                while (!task.IsCompleted && frameBudget-- > 0)
                {
                    AudioClipCreationScheduler.ProcessPendingRequestsForTests();
                    yield return null;
                }

                Assert.That(task.IsCompleted, Is.True, "AudioClip creation did not finish in the test frame budget.");
                Assert.That(task.IsFaulted, Is.False, task.Exception?.ToString());
                Assert.That(task.IsCanceled, Is.False);

                clip = task.GetAwaiter().GetResult();
                Assert.That(clip.samples, Is.EqualTo(4));
                Assert.That(clip.channels, Is.EqualTo(1));
                Assert.That(clip.frequency, Is.EqualTo(16000));

                float[] samples = new float[4];
                Assert.That(clip.GetData(samples, 0), Is.True);
                Assert.That(samples[0], Is.EqualTo(-1f));
                Assert.That(samples[1], Is.EqualTo(0f));
                Assert.That(samples[2], Is.EqualTo(32767f / 32768f).Within(0.000001f));
                Assert.That(samples[3], Is.EqualTo(0.5f).Within(0.0001f));
            }
            finally
            {
                AudioClipCreationScheduler.DestroyAudioClip(clip);
                AudioClipCreationScheduler.DestroyCurrentForTests();
            }
        }

        [UnityTest]
        public IEnumerator CreateAudioClipFromPcm16WavAsync_WritesMultipleStereoBatchesAtFrameOffsets()
        {
            short[] source = new short[600];
            for (int i = 0; i < source.Length; i++)
            {
                source[i] = (short)(i - 300);
            }

            Task<AudioClip> task = AudioRecorder.CreateAudioClipFromPcm16WavAsync(
                BuildPcm16Wav(16000, 2, source),
                "Multi Batch Stereo Test",
                AudioRecorder.MinimumAudioClipDecodeFramesPerUpdate);
            AudioClip clip = null;
            try
            {
                int frameBudget = 300;
                while (!task.IsCompleted && frameBudget-- > 0)
                {
                    AudioClipCreationScheduler.ProcessPendingRequestsForTests();
                    yield return null;
                }

                Assert.That(task.IsCompleted, Is.True);
                Assert.That(task.IsFaulted, Is.False, task.Exception?.ToString());
                Assert.That(task.IsCanceled, Is.False);
                clip = task.GetAwaiter().GetResult();
                float[] actual = new float[source.Length];
                Assert.That(clip.GetData(actual, 0), Is.True);
                Assert.That(actual[0], Is.EqualTo(source[0] / 32768f).Within(0.000001f));
                Assert.That(actual[511], Is.EqualTo(source[511] / 32768f).Within(0.000001f));
                Assert.That(actual[512], Is.EqualTo(source[512] / 32768f).Within(0.000001f));
                Assert.That(actual[599], Is.EqualTo(source[599] / 32768f).Within(0.000001f));
            }
            finally
            {
                AudioClipCreationScheduler.DestroyAudioClip(clip);
                AudioClipCreationScheduler.DestroyCurrentForTests();
            }
        }

        [Test]
        public void CreateAudioClipFromPcm16WavAsync_HonorsPreCanceledToken()
        {
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                Task<AudioClip> task = AudioRecorder.CreateAudioClipFromPcm16WavAsync(
                    BuildPcm16Wav(16000, 1, new short[] { 0 }),
                    cancellationToken: cancellation.Token);

                Assert.That(task.IsCanceled, Is.True);
            }
        }

        [UnityTest]
        public IEnumerator CreateAudioClipFromPcm16WavAsync_CanCancelBeforeMainThreadCreation()
        {
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                Task<AudioClip> task = AudioRecorder.CreateAudioClipFromPcm16WavAsync(
                    BuildPcm16Wav(16000, 1, new short[] { 0, 1, 2 }),
                    cancellationToken: cancellation.Token);

                cancellation.Cancel();
                int frameBudget = 300;
                while (!task.IsCompleted && frameBudget-- > 0)
                {
                    AudioClipCreationScheduler.ProcessPendingRequestsForTests();
                    yield return null;
                }

                Assert.That(task.IsCompleted, Is.True);
                Assert.That(task.IsCanceled, Is.True);
                AudioClipCreationScheduler.DestroyCurrentForTests();
            }
        }

        private static byte[] BuildPcm16Wav(
            int sampleRate,
            int channels,
            short[] interleavedSamples,
            bool includeOddJunkChunk = false,
            int formatChunkSize = 16)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                writer.Write(0);
                writer.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });

                if (includeOddJunkChunk)
                {
                    writer.Write(new[] { (byte)'J', (byte)'U', (byte)'N', (byte)'K' });
                    writer.Write(1);
                    writer.Write((byte)0x5A);
                    writer.Write((byte)0);
                }

                int blockAlign = channels * sizeof(short);
                writer.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                writer.Write(formatChunkSize);
                writer.Write((short)1);
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * blockAlign);
                writer.Write((short)blockAlign);
                writer.Write((short)16);
                for (int i = 16; i < formatChunkSize; i++)
                {
                    writer.Write((byte)0);
                }

                writer.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                writer.Write(interleavedSamples.Length * sizeof(short));
                for (int i = 0; i < interleavedSamples.Length; i++)
                {
                    writer.Write(interleavedSamples[i]);
                }

                writer.Flush();
                byte[] result = stream.ToArray();
                int riffSize = result.Length - 8;
                result[4] = (byte)riffSize;
                result[5] = (byte)(riffSize >> 8);
                result[6] = (byte)(riffSize >> 16);
                result[7] = (byte)(riffSize >> 24);
                return result;
            }
        }

        private static byte[] ToPcm16Bytes(params short[] samples)
        {
            byte[] data = new byte[samples.Length * sizeof(short)];
            for (int i = 0; i < samples.Length; i++)
            {
                data[i * 2] = (byte)samples[i];
                data[i * 2 + 1] = (byte)(samples[i] >> 8);
            }

            return data;
        }
    }
}
