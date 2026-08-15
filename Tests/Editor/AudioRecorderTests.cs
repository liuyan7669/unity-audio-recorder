using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cowart.AudioRecorder.Tests
{
    public sealed class AudioRecorderTests
    {
        [Test]
        public void RecordedAudio_ExposesCompleteRawResult()
        {
            byte[] data = { 1, 2, 3, 4 };
            RecordedAudio recording = new RecordedAudio(
                "recording.wav",
                "audio/wav",
                data,
                1.25f,
                16000,
                1,
                16);

            Assert.That(recording.Name, Is.EqualTo("recording.wav"));
            Assert.That(recording.MimeType, Is.EqualTo("audio/wav"));
            Assert.That(recording.Data, Is.SameAs(data));
            Assert.That(recording.Size, Is.EqualTo(4));
            Assert.That(recording.DurationSeconds, Is.EqualTo(1.25f));
            Assert.That(recording.SampleRate, Is.EqualTo(16000));
            Assert.That(recording.Channels, Is.EqualTo(1));
            Assert.That(recording.BitsPerSample, Is.EqualTo(16));
        }

        [Test]
        public void NormalizeAndEncode_ConvertsStereoToMonoPcm16Wav()
        {
            float[] stereo =
            {
                1f, -1f,
                0.5f, 0.5f,
                -0.5f, -0.5f,
                0f, 0f
            };

            NormalizedPcmAudio result = Pcm16WavUtility.NormalizeAndEncode(
                stereo,
                4,
                2,
                16000,
                16000);

            Assert.That(result.Samples, Is.EqualTo(new[] { 0f, 0.5f, -0.5f, 0f }));
            Assert.That(result.WavBytes.Length, Is.EqualTo(44 + 8));
            Assert.That(ReadAscii(result.WavBytes, 0, 4), Is.EqualTo("RIFF"));
            Assert.That(ReadAscii(result.WavBytes, 8, 4), Is.EqualTo("WAVE"));
            Assert.That(ReadInt32(result.WavBytes, 24), Is.EqualTo(16000));
            Assert.That(ReadInt16(result.WavBytes, 22), Is.EqualTo(1));
            Assert.That(ReadInt16(result.WavBytes, 34), Is.EqualTo(16));
        }

        [Test]
        public void NormalizeToMono_ResamplesToRequestedRate()
        {
            float[] source = { 0f, 0.25f, 0.5f, 0.75f };
            float[] result = Pcm16WavUtility.NormalizeToMono(source, 4, 1, 8000, 16000);

            Assert.That(result.Length, Is.EqualTo(8));
            Assert.That(result[0], Is.EqualTo(0f));
            Assert.That(result[2], Is.EqualTo(0.25f).Within(0.0001f));
            Assert.That(result[6], Is.EqualTo(0.75f).Within(0.0001f));
        }

        [Test]
        public void AudioStreamChunk_CalculatesDurationFromPcmFormat()
        {
            AudioStreamChunk chunk = new AudioStreamChunk(
                new byte[3200],
                0,
                0,
                16000,
                1,
                16,
                true,
                false);

            Assert.That(chunk.DurationMilliseconds, Is.EqualTo(100));
        }

        [Test]
        public void NotifyRecordingCompleted_CachesResultBeforePublishingEvent()
        {
            byte[] wavBytes = new byte[48];
            RecordedAudio received = null;
            Action<RecordedAudio> handler = recording =>
            {
                received = recording;
                Assert.That(AudioRecorder.LastRecording, Is.SameAs(recording));
            };

            AudioRecorder.RecordingCompleted += handler;
            try
            {
                AudioRecorder.NotifyRecordingCompleted(wavBytes, "voice.wav", 0.25f);

                Assert.That(received, Is.Not.Null);
                Assert.That(received.Data, Is.SameAs(wavBytes));
                Assert.That(received.Name, Is.EqualTo("voice.wav"));
                Assert.That(received.MimeType, Is.EqualTo("audio/wav"));
            }
            finally
            {
                AudioRecorder.RecordingCompleted -= handler;
                AudioRecorder.ClearRecording();
            }
        }

        [Test]
        public void InvokeSafely_ContinuesAfterSubscriberThrows()
        {
            int callCount = 0;
            Action callback = () => throw new InvalidOperationException("expected-test-error");
            callback += () => callCount++;
            LogAssert.Expect(LogType.Exception, "InvalidOperationException: expected-test-error");

            AudioRecorder.InvokeSafely(callback);

            Assert.That(callCount, Is.EqualTo(1));
        }

        [Test]
        public void RuntimeAssembly_ExposesNoPublicMonoBehaviourEntryPoint()
        {
            Type[] exportedTypes = typeof(AudioRecorder).Assembly.GetExportedTypes();
            for (int i = 0; i < exportedTypes.Length; i++)
            {
                Assert.That(
                    typeof(MonoBehaviour).IsAssignableFrom(exportedTypes[i]),
                    Is.False,
                    exportedTypes[i].FullName + " must not become a second scene entry point.");
            }
        }

        [Test]
        public void RuntimeAssembly_DoesNotReferenceFileBridge()
        {
            System.Reflection.AssemblyName[] references =
                typeof(AudioRecorder).Assembly.GetReferencedAssemblies();
            for (int i = 0; i < references.Length; i++)
            {
                Assert.That(references[i].Name, Is.Not.EqualTo("Cowart.FileBridge"));
            }

            Assert.That(typeof(AudioRecorder).GetMethod("SaveLastRecording"), Is.Null);
        }

        [Test]
        public void DurationContract_SupportsOneHourWithoutChangingTheDefault()
        {
            Assert.That(AudioRecorder.MinimumDurationSeconds, Is.EqualTo(5));
            Assert.That(AudioRecorder.DefaultMaximumDurationSeconds, Is.EqualTo(300));
            Assert.That(AudioRecorder.MaximumDurationSeconds, Is.EqualTo(3600));

            System.Reflection.ParameterInfo durationParameter = typeof(AudioRecorder)
                .GetMethod(nameof(AudioRecorder.StartRecording))
                .GetParameters()[0];
            Assert.That(durationParameter.DefaultValue, Is.EqualTo(300));
        }

        [Test]
        public void RecordingSessionGate_RejectsConcurrentBegin()
        {
            RecordingSessionGate gate = new RecordingSessionGate();
            int generation = gate.Begin();

            Assert.That(generation, Is.Not.EqualTo(0));
            Assert.That(gate.Begin(), Is.EqualTo(0));
            Assert.That(gate.ActiveGeneration, Is.EqualTo(generation));
        }

        [Test]
        public void RecordingSessionGate_FinalizingOnlyAcceptsCurrentGeneration()
        {
            RecordingSessionGate gate = new RecordingSessionGate();
            int generation = gate.Begin();

            Assert.That(gate.TryBeginFinalizing(generation + 1), Is.False);
            Assert.That(gate.IsFinalizing, Is.False);
            Assert.That(gate.TryBeginFinalizing(generation), Is.True);
            Assert.That(gate.IsFinalizing, Is.True);
            Assert.That(gate.Begin(), Is.EqualTo(0));
        }

        [Test]
        public void RecordingSessionGate_PublishesOneTerminalPerGeneration()
        {
            RecordingSessionGate gate = new RecordingSessionGate();
            int generation = gate.Begin();

            Assert.That(gate.TryPublishTerminal(generation), Is.True);
            Assert.That(gate.TryPublishTerminal(generation), Is.False);
            Assert.That(gate.IsActive, Is.False);
        }

        [Test]
        public void RecordingSessionGate_NewGenerationRejectsOldCallbacks()
        {
            RecordingSessionGate gate = new RecordingSessionGate();
            int oldGeneration = gate.Begin();
            Assert.That(gate.TryPublishTerminal(oldGeneration), Is.True);

            int newGeneration = gate.Begin();

            Assert.That(newGeneration, Is.Not.EqualTo(oldGeneration));
            Assert.That(gate.TryBeginFinalizing(oldGeneration), Is.False);
            Assert.That(gate.TryPublishTerminal(oldGeneration), Is.False);
            Assert.That(gate.IsCurrent(newGeneration), Is.True);
        }

        [Test]
        public void RecordingSessionGate_AbortInvalidatesCallbacksAndAllowsRestart()
        {
            RecordingSessionGate gate = new RecordingSessionGate();
            int abortedGeneration = gate.Begin();

            gate.AbortActive();
            int restartedGeneration = gate.Begin();

            Assert.That(gate.TryBeginFinalizing(abortedGeneration), Is.False);
            Assert.That(restartedGeneration, Is.Not.EqualTo(abortedGeneration));
            Assert.That(gate.IsCurrent(restartedGeneration), Is.True);
        }

        [TestCase(4, 40)]
        [TestCase(3601, 40)]
        [TestCase(60, 19)]
        [TestCase(60, 1001)]
        public void StartRecording_RejectsOutOfRangeConfigurationBeforePlatformAccess(
            int durationSeconds,
            int chunkMilliseconds)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                AudioRecorder.StartRecording(durationSeconds, false, chunkMilliseconds));
        }

        private static string ReadAscii(byte[] data, int offset, int count)
        {
            return System.Text.Encoding.ASCII.GetString(data, offset, count);
        }

        private static int ReadInt16(byte[] data, int offset)
        {
            return data[offset] | data[offset + 1] << 8;
        }

        private static int ReadInt32(byte[] data, int offset)
        {
            return data[offset] |
                data[offset + 1] << 8 |
                data[offset + 2] << 16 |
                data[offset + 3] << 24;
        }
    }
}
