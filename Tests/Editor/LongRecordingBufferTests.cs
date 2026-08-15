using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Cowart.AudioRecorder.Tests
{
    public sealed class LongRecordingBufferTests
    {
        private string temporaryDirectory;

        [SetUp]
        public void SetUp()
        {
            temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "cowart_audio_recorder_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(temporaryDirectory) &&
                Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, true);
            }
        }

        [Test]
        public void CompleteAndReadAllBytes_WritesValidPcm16WavAndDeletesTemporaryFile()
        {
            Pcm16RecordingFile recording = Pcm16RecordingFile.Create(
                temporaryDirectory,
                16000);
            string filePath = recording.FilePath;
            byte[] firstChunk = { 0x00, 0x00, 0xFF, 0x7F };
            byte[] secondChunk = { 0x00, 0x80, 0x34, 0x12 };

            recording.AppendPcm16(firstChunk, 0, firstChunk.Length);
            recording.AppendPcm16(secondChunk, 0, secondChunk.Length);
            byte[] wavBytes = recording.CompleteAndReadAllBytes();

            Assert.That(File.Exists(filePath), Is.False);
            Assert.That(wavBytes.Length, Is.EqualTo(52));
            Pcm16WavData wavData = Pcm16WavData.Parse(wavBytes);
            Assert.That(wavData.SampleRate, Is.EqualTo(16000));
            Assert.That(wavData.Channels, Is.EqualTo(1));
            Assert.That(wavData.FrameCount, Is.EqualTo(4));
            CollectionAssert.AreEqual(
                new byte[] { 0x00, 0x00, 0xFF, 0x7F, 0x00, 0x80, 0x34, 0x12 },
                new ArraySegment<byte>(wavBytes, wavData.DataOffset, wavData.DataLength));
        }

        [Test]
        public void Dispose_DeletesIncompleteTemporaryFile()
        {
            Pcm16RecordingFile recording = Pcm16RecordingFile.Create(
                temporaryDirectory,
                16000);
            string filePath = recording.FilePath;
            recording.AppendPcm16(new byte[] { 0x00, 0x00 }, 0, 2);

            recording.Dispose();

            Assert.That(File.Exists(filePath), Is.False);
        }

        [Test]
        public void RingCursor_ReportsUnreadFramesAcrossWrap()
        {
            NativeMicrophoneRingCursor cursor = new NativeMicrophoneRingCursor(100);
            cursor.Reset(0d);

            Assert.That(cursor.GetAvailableFrames(80, 0.5d, 10d), Is.EqualTo(80));
            cursor.Advance(80);
            Assert.That(cursor.GetAvailableFrames(5, 1d, 10d), Is.EqualTo(25));
            cursor.Advance(25);
            Assert.That(cursor.ReadFrame, Is.EqualTo(5));
        }

        [Test]
        public void RingCursor_RejectsAccumulatedUnreadFramesThatReachCapacity()
        {
            NativeMicrophoneRingCursor cursor = new NativeMicrophoneRingCursor(100);
            cursor.Reset(0d);

            Assert.That(cursor.GetAvailableFrames(90, 9d, 10d), Is.EqualTo(90));
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                cursor.GetAvailableFrames(0, 10d, 10d));

            StringAssert.Contains("overwritten", exception.Message);
        }

        [Test]
        public void RingCursor_AdvanceDecrementsAccumulatedUnreadFrames()
        {
            NativeMicrophoneRingCursor cursor = new NativeMicrophoneRingCursor(100);
            cursor.Reset(0d);

            Assert.That(cursor.GetAvailableFrames(90, 9d, 10d), Is.EqualTo(90));
            cursor.Advance(30);

            Assert.That(cursor.AvailableFrameCount, Is.EqualTo(60));
            Assert.That(cursor.GetAvailableFrames(95, 9.5d, 10d), Is.EqualTo(65));
        }

        [Test]
        public void RingCursor_RejectsChunkWithoutWriteHeadroom()
        {
            NativeMicrophoneRingCursor cursor = new NativeMicrophoneRingCursor(100);
            cursor.Reset(0d);
            Assert.That(cursor.GetAvailableFrames(80, 8d, 10d), Is.EqualTo(80));

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                cursor.EnsureReadSafetyMargin(20));

            StringAssert.Contains("headroom", exception.Message);
        }

        [Test]
        public void RingCursor_RejectsPollGapThatCanOverwriteUnreadAudio()
        {
            NativeMicrophoneRingCursor cursor = new NativeMicrophoneRingCursor(160000);
            cursor.Reset(20d);

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                cursor.GetAvailableFrames(0, 30d, 10d));

            StringAssert.Contains("overwritten", exception.Message);
        }

        [Test]
        public void NativeCaptureLease_WhenSynchronousStopClearsRecordingFlag_IsRejected()
        {
            AudioClip clip = AudioClip.Create("native-capture-lease-test", 1, 1, 8000, false);
            Pcm16RecordingFile recordingFile = Pcm16RecordingFile.Create(
                temporaryDirectory,
                16000);
            NativeMicrophoneRingCursor cursor = new NativeMicrophoneRingCursor(100);
            try
            {
                Assert.That(AudioRecorderDriver.IsNativeCaptureLeaseCurrent(
                    true,
                    true,
                    clip,
                    recordingFile,
                    cursor,
                    clip,
                    recordingFile,
                    cursor), Is.True);

                Assert.That(AudioRecorderDriver.IsNativeCaptureLeaseCurrent(
                    true,
                    false,
                    clip,
                    recordingFile,
                    cursor,
                    clip,
                    recordingFile,
                    cursor), Is.False);
            }
            finally
            {
                recordingFile.Dispose();
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void TryCreateNativePlaybackClip_WhenFactoryThrows_ReturnsNullAndWarns()
        {
            Pcm16RecordingFile recording = Pcm16RecordingFile.Create(
                temporaryDirectory,
                16000);
            recording.AppendPcm16(new byte[] { 0x00, 0x00 }, 0, 2);
            Pcm16WavData wavData = Pcm16WavData.Parse(recording.CompleteAndReadAllBytes());
            string warning = null;

            AudioClip clip = AudioRecorderDriver.TryCreateNativePlaybackClip(
                wavData,
                _ => throw new InvalidOperationException("simulated playback failure"),
                message => warning = message);

            Assert.That(clip, Is.Null);
            StringAssert.Contains("completed", warning);
            StringAssert.Contains("simulated playback failure", warning);
        }
    }
}
