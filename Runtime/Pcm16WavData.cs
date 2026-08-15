using System;
using System.IO;
using System.Threading;

namespace Cowart.AudioRecorder
{
    internal sealed class Pcm16WavData
    {
        private const int MinimumHeaderSize = 12;
        private const int FormatChunkMinimumSize = 16;
        private const int PcmFormatTag = 1;
        private const int BitsPerSample = 16;
        private const int MaximumUnityChannelCount = 8;
        private const int CancellationCheckSampleInterval = 16384;

        private readonly byte[] bytes;

        private Pcm16WavData(
            byte[] bytes,
            int dataOffset,
            int dataLength,
            int sampleRate,
            int channels,
            int blockAlign)
        {
            this.bytes = bytes;
            DataOffset = dataOffset;
            DataLength = dataLength;
            SampleRate = sampleRate;
            Channels = channels;
            BlockAlign = blockAlign;
            FrameCount = dataLength / blockAlign;
            SampleValueCount = checked(FrameCount * channels);
        }

        public int DataOffset { get; }

        public int DataLength { get; }

        public int SampleRate { get; }

        public int Channels { get; }

        public int BlockAlign { get; }

        public int FrameCount { get; }

        public int SampleValueCount { get; }

        public static Pcm16WavData Parse(byte[] wavBytes)
        {
            if (wavBytes == null)
            {
                throw new ArgumentNullException(nameof(wavBytes));
            }

            if (wavBytes.Length == 0)
            {
                throw new ArgumentException("The PCM16 WAV byte array is empty.", nameof(wavBytes));
            }

            if (wavBytes.Length < MinimumHeaderSize ||
                !MatchesFourCc(wavBytes, 0, "RIFF") ||
                !MatchesFourCc(wavBytes, 8, "WAVE"))
            {
                throw new InvalidDataException("The audio data is not a valid little-endian RIFF/WAVE file.");
            }

            long declaredEnd = 8L + ReadUInt32(wavBytes, 4);
            if (declaredEnd < MinimumHeaderSize || declaredEnd > wavBytes.Length)
            {
                throw new InvalidDataException("The RIFF/WAVE container is truncated or has an invalid size.");
            }

            int containerEnd = (int)declaredEnd;
            bool hasFormat = false;
            bool hasData = false;
            int sampleRate = 0;
            int channels = 0;
            int blockAlign = 0;
            int dataOffset = 0;
            int dataLength = 0;
            int chunkOffset = MinimumHeaderSize;

            while (chunkOffset <= containerEnd - 8)
            {
                uint unsignedChunkSize = ReadUInt32(wavBytes, chunkOffset + 4);
                if (unsignedChunkSize > int.MaxValue)
                {
                    throw new InvalidDataException("A RIFF/WAVE chunk is too large for a managed byte array.");
                }

                int chunkSize = (int)unsignedChunkSize;
                int chunkDataOffset = chunkOffset + 8;
                long chunkDataEndLong = (long)chunkDataOffset + chunkSize;
                if (chunkDataEndLong > containerEnd)
                {
                    throw new InvalidDataException("A RIFF/WAVE chunk extends beyond the declared container.");
                }

                int chunkDataEnd = (int)chunkDataEndLong;

                if (MatchesFourCc(wavBytes, chunkOffset, "fmt "))
                {
                    if (chunkSize < FormatChunkMinimumSize)
                    {
                        throw new InvalidDataException("The WAV format chunk is shorter than 16 bytes.");
                    }

                    int formatTag = ReadUInt16(wavBytes, chunkDataOffset);
                    int parsedChannels = ReadUInt16(wavBytes, chunkDataOffset + 2);
                    uint parsedSampleRate = ReadUInt32(wavBytes, chunkDataOffset + 4);
                    uint parsedByteRate = ReadUInt32(wavBytes, chunkDataOffset + 8);
                    int parsedBlockAlign = ReadUInt16(wavBytes, chunkDataOffset + 12);
                    int parsedBitsPerSample = ReadUInt16(wavBytes, chunkDataOffset + 14);

                    if (formatTag != PcmFormatTag)
                    {
                        throw new NotSupportedException(
                            "Only uncompressed PCM WAV format tag 1 is supported. Compressed and WAVE_FORMAT_EXTENSIBLE files are not supported.");
                    }

                    if (parsedBitsPerSample != BitsPerSample)
                    {
                        throw new NotSupportedException("Only 16-bit PCM WAV audio is supported.");
                    }

                    if (parsedChannels <= 0 || parsedChannels > MaximumUnityChannelCount)
                    {
                        throw new NotSupportedException("The WAV channel count must be between 1 and 8 for Unity AudioClip.");
                    }

                    if (parsedSampleRate == 0 || parsedSampleRate > int.MaxValue)
                    {
                        throw new InvalidDataException("The WAV sample rate is invalid.");
                    }

                    int expectedBlockAlign = parsedChannels * sizeof(short);
                    long expectedByteRate = (long)parsedSampleRate * expectedBlockAlign;
                    if (expectedByteRate > uint.MaxValue ||
                        parsedBlockAlign != expectedBlockAlign ||
                        parsedByteRate != expectedByteRate)
                    {
                        throw new InvalidDataException("The WAV block alignment or byte rate does not match PCM16 audio dimensions.");
                    }

                    sampleRate = (int)parsedSampleRate;
                    channels = parsedChannels;
                    blockAlign = parsedBlockAlign;
                    hasFormat = true;
                }
                else if (!hasData && MatchesFourCc(wavBytes, chunkOffset, "data"))
                {
                    dataOffset = chunkDataOffset;
                    dataLength = chunkSize;
                    hasData = true;
                }

                int padding = chunkSize & 1;
                long nextChunkOffsetLong = (long)chunkDataEnd + padding;
                if (nextChunkOffsetLong > containerEnd)
                {
                    throw new InvalidDataException("A RIFF/WAVE chunk is missing its required padding byte.");
                }

                chunkOffset = (int)nextChunkOffsetLong;
            }

            if (chunkOffset != containerEnd)
            {
                throw new InvalidDataException("The RIFF/WAVE container ends with an incomplete chunk header.");
            }

            if (!hasFormat)
            {
                throw new InvalidDataException("The WAV file does not contain a format chunk.");
            }

            if (!hasData || dataLength <= 0)
            {
                throw new InvalidDataException("The WAV file does not contain PCM sample data.");
            }

            if (dataLength % blockAlign != 0)
            {
                throw new InvalidDataException("The WAV PCM data length is not aligned to complete sample frames.");
            }

            return new Pcm16WavData(
                wavBytes,
                dataOffset,
                dataLength,
                sampleRate,
                channels,
                blockAlign);
        }

        public float[] DecodeAll(CancellationToken cancellationToken)
        {
            float[] samples = new float[SampleValueCount];
            int byteOffset = DataOffset;
            for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
            {
                if ((sampleIndex & (CancellationCheckSampleInterval - 1)) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                samples[sampleIndex] = DecodeSample(bytes, byteOffset);
                byteOffset += sizeof(short);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return samples;
        }

        public void DecodeFrames(
            int sourceFrameOffset,
            int frameCount,
            float[] destination,
            int destinationSampleOffset = 0)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            if (sourceFrameOffset < 0 || frameCount < 0 ||
                sourceFrameOffset > FrameCount - frameCount)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceFrameOffset));
            }

            int sampleValueCount = checked(frameCount * Channels);
            if (destinationSampleOffset < 0 ||
                destinationSampleOffset > destination.Length - sampleValueCount)
            {
                throw new ArgumentOutOfRangeException(nameof(destinationSampleOffset));
            }

            int sourceByteOffset = checked(DataOffset + sourceFrameOffset * BlockAlign);
            DecodeSampleValuesCore(
                sampleValueCount,
                destination,
                destinationSampleOffset,
                sourceByteOffset);
        }

        public void DecodeSampleValues(
            int sourceSampleOffset,
            int sampleValueCount,
            float[] destination,
            int destinationSampleOffset = 0)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            if (sourceSampleOffset < 0 || sampleValueCount < 0 ||
                sourceSampleOffset > SampleValueCount - sampleValueCount)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceSampleOffset));
            }

            if (destinationSampleOffset < 0 ||
                destinationSampleOffset > destination.Length - sampleValueCount)
            {
                throw new ArgumentOutOfRangeException(nameof(destinationSampleOffset));
            }

            int sourceByteOffset = checked(DataOffset + sourceSampleOffset * sizeof(short));
            DecodeSampleValuesCore(
                sampleValueCount,
                destination,
                destinationSampleOffset,
                sourceByteOffset);
        }

        private void DecodeSampleValuesCore(
            int sampleValueCount,
            float[] destination,
            int destinationSampleOffset,
            int sourceByteOffset)
        {
            for (int i = 0; i < sampleValueCount; i++)
            {
                destination[destinationSampleOffset + i] = DecodeSample(bytes, sourceByteOffset);
                sourceByteOffset += sizeof(short);
            }
        }

        private static float DecodeSample(byte[] bytes, int byteOffset)
        {
            short value = (short)(bytes[byteOffset] | bytes[byteOffset + 1] << 8);
            return value / 32768f;
        }

        private static bool MatchesFourCc(byte[] data, int offset, string value)
        {
            return offset >= 0 && offset <= data.Length - 4 &&
                data[offset] == value[0] &&
                data[offset + 1] == value[1] &&
                data[offset + 2] == value[2] &&
                data[offset + 3] == value[3];
        }

        private static int ReadUInt16(byte[] data, int offset)
        {
            return data[offset] | data[offset + 1] << 8;
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)(data[offset] |
                data[offset + 1] << 8 |
                data[offset + 2] << 16 |
                data[offset + 3] << 24);
        }
    }

    internal sealed class Pcm16WavClipReader
    {
        private readonly Pcm16WavData wavData;
        private long samplePosition;

        public Pcm16WavClipReader(Pcm16WavData wavData)
        {
            this.wavData = wavData ?? throw new ArgumentNullException(nameof(wavData));
        }

        public void Read(float[] destination)
        {
            if (destination == null)
            {
                return;
            }

            long currentPosition = Interlocked.Read(ref samplePosition);
            int available = (int)Math.Max(
                0L,
                Math.Min(destination.Length, wavData.SampleValueCount - currentPosition));
            if (available > 0)
            {
                wavData.DecodeSampleValues((int)currentPosition, available, destination);
            }

            if (available < destination.Length)
            {
                Array.Clear(destination, available, destination.Length - available);
            }

            Interlocked.Exchange(ref samplePosition, currentPosition + available);
        }

        public void SetPosition(int framePosition)
        {
            int clampedFrame = Math.Max(0, Math.Min(framePosition, wavData.FrameCount));
            Interlocked.Exchange(ref samplePosition, (long)clampedFrame * wavData.Channels);
        }
    }
}
