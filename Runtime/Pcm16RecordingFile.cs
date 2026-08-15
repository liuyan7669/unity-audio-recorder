using System;
using System.IO;

namespace Cowart.AudioRecorder
{
    /// <summary>
    /// Incrementally writes mono PCM16 data into a temporary WAV file so recording memory stays bounded.
    /// Ownership transfers to CompleteAndReadAllBytes when finalization starts.
    /// </summary>
    internal sealed class Pcm16RecordingFile : IDisposable
    {
        private const int WavHeaderSize = 44;
        private const int FileBufferSize = 64 * 1024;

        private readonly string filePath;
        private readonly int sampleRate;
        private FileStream stream;
        private long pcmDataByteCount;
        private bool completionStarted;

        private Pcm16RecordingFile(string filePath, int sampleRate, FileStream stream)
        {
            this.filePath = filePath;
            this.sampleRate = sampleRate;
            this.stream = stream;
        }

        internal string FilePath => filePath;

        internal long PcmDataByteCount => pcmDataByteCount;

        internal double DurationSeconds => pcmDataByteCount / (sampleRate * (double)sizeof(short));

        internal static Pcm16RecordingFile Create(string directoryPath, int sampleRate)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                throw new ArgumentException("A temporary recording directory is required.", nameof(directoryPath));
            }

            if (sampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            }

            Directory.CreateDirectory(directoryPath);
            string path = Path.Combine(
                directoryPath,
                "cowart_audio_" + Guid.NewGuid().ToString("N") + ".wav.tmp");
            FileStream fileStream = null;
            try
            {
                fileStream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    FileBufferSize,
                    FileOptions.SequentialScan);
                byte[] header = CreateWavHeader(sampleRate, 0);
                fileStream.Write(header, 0, header.Length);
                return new Pcm16RecordingFile(path, sampleRate, fileStream);
            }
            catch
            {
                fileStream?.Dispose();
                TryDelete(path);
                throw;
            }
        }

        internal void AppendPcm16(byte[] pcmBytes, int offset, int count)
        {
            if (pcmBytes == null)
            {
                throw new ArgumentNullException(nameof(pcmBytes));
            }

            if (offset < 0 || count < 0 || offset > pcmBytes.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            if ((count & 1) != 0)
            {
                throw new ArgumentException("Mono PCM16 data must contain complete sample frames.", nameof(count));
            }

            if (completionStarted || stream == null)
            {
                throw new InvalidOperationException("The temporary WAV recording is already closed.");
            }

            long nextDataByteCount = checked(pcmDataByteCount + count);
            if (nextDataByteCount > int.MaxValue - WavHeaderSize)
            {
                throw new InvalidOperationException(
                    "The PCM16 WAV recording is too large for a managed byte array result.");
            }

            stream.Write(pcmBytes, offset, count);
            pcmDataByteCount = nextDataByteCount;
        }

        internal byte[] CompleteAndReadAllBytes()
        {
            if (completionStarted || stream == null)
            {
                throw new InvalidOperationException("The temporary WAV recording is already closed.");
            }

            completionStarted = true;
            FileStream activeStream = stream;
            stream = null;
            try
            {
                byte[] header = CreateWavHeader(sampleRate, checked((int)pcmDataByteCount));
                activeStream.Position = 0;
                activeStream.Write(header, 0, header.Length);
                activeStream.Flush();
                activeStream.Dispose();
                activeStream = null;

                byte[] wavBytes = File.ReadAllBytes(filePath);
                int expectedLength = checked(WavHeaderSize + (int)pcmDataByteCount);
                if (wavBytes.Length != expectedLength)
                {
                    throw new InvalidDataException(
                        "The temporary WAV recording length changed during finalization.");
                }

                return wavBytes;
            }
            finally
            {
                activeStream?.Dispose();
                TryDelete(filePath);
            }
        }

        public void Dispose()
        {
            FileStream activeStream = stream;
            stream = null;
            completionStarted = true;
            try
            {
                activeStream?.Dispose();
            }
            finally
            {
                TryDelete(filePath);
            }
        }

        private static byte[] CreateWavHeader(int sampleRate, int pcmDataLength)
        {
            byte[] header = new byte[WavHeaderSize];
            WriteAscii(header, 0, "RIFF");
            WriteInt32(header, 4, checked(36 + pcmDataLength));
            WriteAscii(header, 8, "WAVE");
            WriteAscii(header, 12, "fmt ");
            WriteInt32(header, 16, 16);
            WriteInt16(header, 20, 1);
            WriteInt16(header, 22, 1);
            WriteInt32(header, 24, sampleRate);
            WriteInt32(header, 28, checked(sampleRate * sizeof(short)));
            WriteInt16(header, 32, sizeof(short));
            WriteInt16(header, 34, 16);
            WriteAscii(header, 36, "data");
            WriteInt32(header, 40, pcmDataLength);
            return header;
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

        private static void TryDelete(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Temporary cleanup is best effort during failure and application shutdown.
            }
        }
    }

    internal sealed class NativeMicrophoneRingCursor
    {
        private readonly int capacityFrames;
        private int readFrame;
        private int lastWriteFrame;
        private long unreadFrames;
        private double lastPollTime;
        private bool hasPollTime;

        internal NativeMicrophoneRingCursor(int capacityFrames)
        {
            if (capacityFrames <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacityFrames));
            }

            this.capacityFrames = capacityFrames;
        }

        internal int ReadFrame => readFrame;

        internal int AvailableFrameCount => checked((int)unreadFrames);

        internal void Reset(double timestamp)
        {
            readFrame = 0;
            lastWriteFrame = 0;
            unreadFrames = 0L;
            lastPollTime = timestamp;
            hasPollTime = true;
        }

        internal int GetAvailableFrames(
            int writeFrame,
            double timestamp,
            double ringDurationSeconds)
        {
            if (writeFrame < 0 || writeFrame > capacityFrames)
            {
                throw new ArgumentOutOfRangeException(nameof(writeFrame));
            }

            if (ringDurationSeconds <= 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(ringDurationSeconds));
            }

            if (writeFrame == capacityFrames)
            {
                writeFrame = 0;
            }

            if (hasPollTime && timestamp - lastPollTime >= ringDurationSeconds)
            {
                throw new InvalidOperationException(
                    "The Unity main thread did not drain the microphone ring buffer before it wrapped and audio may have been overwritten.");
            }

            if (!hasPollTime)
            {
                lastWriteFrame = writeFrame;
                lastPollTime = timestamp;
                hasPollTime = true;
                return checked((int)unreadFrames);
            }

            int newlyWrittenFrames = writeFrame >= lastWriteFrame
                ? writeFrame - lastWriteFrame
                : capacityFrames - lastWriteFrame + writeFrame;
            long nextUnreadFrames = unreadFrames + newlyWrittenFrames;
            if (nextUnreadFrames >= capacityFrames)
            {
                throw new InvalidOperationException(
                    "The microphone write cursor caught the unread ring-buffer data and audio may have been overwritten.");
            }

            unreadFrames = nextUnreadFrames;
            lastWriteFrame = writeFrame;
            lastPollTime = timestamp;
            return checked((int)unreadFrames);
        }

        internal void EnsureReadSafetyMargin(int safetyFrameCount)
        {
            if (safetyFrameCount < 0 || safetyFrameCount >= capacityFrames)
            {
                throw new ArgumentOutOfRangeException(nameof(safetyFrameCount));
            }

            if (unreadFrames + safetyFrameCount >= capacityFrames)
            {
                throw new InvalidOperationException(
                    "The microphone ring buffer has too little write-headroom to read safely without overwritten audio.");
            }
        }

        internal void Advance(int frameCount)
        {
            if (frameCount < 0 || frameCount > unreadFrames)
            {
                throw new ArgumentOutOfRangeException(nameof(frameCount));
            }

            readFrame = (readFrame + frameCount) % capacityFrames;
            unreadFrames -= frameCount;
        }
    }
}
