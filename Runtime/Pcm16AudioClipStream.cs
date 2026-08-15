using System;
using System.Threading;
using UnityEngine;

namespace Cowart.AudioRecorder
{
    /// <summary>
    /// 将连续 PCM16 Little Endian 数据写入 Unity 流式 AudioClip 的单生产者会话。
    /// 一个实例只对应一条音频流；创建和所有写入/控制方法必须在 Unity 主线程调用。
    /// </summary>
    public sealed class Pcm16AudioClipStream : IDisposable
    {
        private readonly float[] ringSamples;
        private readonly int capacityFrames;
        private readonly int prebufferFrames;

        private long publishedReadFrame;
        private long publishedWriteFrame;
        private long droppedFrames;
        private long underflowFrames;
        private long underflowCount;
        private int playbackStarted;
        private int inputCompleted;
        private int drained;
        private int aborted;
        private int disposed;

        internal Pcm16AudioClipStream(
            string clipName,
            int sampleRate,
            int channels,
            int bufferCapacityMilliseconds,
            int prebufferMilliseconds)
        {
            SampleRate = sampleRate;
            Channels = channels;
            capacityFrames = Math.Max(
                1,
                checked((int)Math.Ceiling(sampleRate * bufferCapacityMilliseconds / 1000d)));
            prebufferFrames = Math.Min(
                capacityFrames,
                Math.Max(1, checked((int)Math.Ceiling(sampleRate * prebufferMilliseconds / 1000d))));
            ringSamples = new float[checked(capacityFrames * channels)];

            Clip = AudioClip.Create(
                clipName,
                sampleRate,
                channels,
                sampleRate,
                true,
                ReadPcmData,
                SetPlaybackPosition);
            if (Clip == null)
            {
                throw new InvalidOperationException("Unity failed to create the real-time streaming AudioClip.");
            }
        }

        /// <summary>
        /// 把此 Clip 交给单个 AudioSource，在 IsReadyToPlay 后设置 loop=true 并开始播放。
        /// 该实时 Clip 不支持寻址，不能依赖 time/timeSamples，也不要交给多个 AudioSource。
        /// </summary>
        public AudioClip Clip { get; private set; }

        public int SampleRate { get; }

        public int Channels { get; }

        public int CapacityFrames => capacityFrames;

        public int BufferedFrames
        {
            get
            {
                long available = Interlocked.Read(ref publishedWriteFrame) -
                    Interlocked.Read(ref publishedReadFrame);
                return (int)Math.Max(0L, Math.Min(capacityFrames, available));
            }
        }

        public int BufferedMilliseconds =>
            (int)Math.Round(BufferedFrames * 1000d / SampleRate);

        public bool IsReadyToPlay
        {
            get
            {
                if (IsDisposed || IsAborted)
                {
                    return false;
                }

                int bufferedFrames = BufferedFrames;
                return Volatile.Read(ref playbackStarted) != 0 ||
                    bufferedFrames >= prebufferFrames ||
                    (IsInputCompleted && bufferedFrames > 0);
            }
        }

        public bool IsInputCompleted => Volatile.Read(ref inputCompleted) != 0;

        public bool IsDrained => Volatile.Read(ref drained) != 0;

        public bool IsAborted => Volatile.Read(ref aborted) != 0;

        public bool IsDisposed => Volatile.Read(ref disposed) != 0;

        public long DroppedFrames => Interlocked.Read(ref droppedFrames);

        public long UnderflowFrames => Interlocked.Read(ref underflowFrames);

        public long UnderflowCount => Interlocked.Read(ref underflowCount);

        /// <summary>
        /// 写入 AudioRecorder.AudioChunkReceived 返回的 PCM16 块。
        /// 缓冲区不足时整块丢弃并返回 false；只有写入成功的 IsLast 才会结束输入。
        /// 写入失败时应在空间可用后重试，或由调用方显式调用 CompleteInput。
        /// </summary>
        public bool TryWrite(AudioStreamChunk chunk)
        {
            AudioRecorderDriver.EnsureMainThread();
            if (chunk == null)
            {
                throw new ArgumentNullException(nameof(chunk));
            }

            if (chunk.SampleRate != SampleRate ||
                chunk.Channels != Channels ||
                chunk.BitsPerSample != AudioRecorder.OutputBitsPerSample)
            {
                throw new ArgumentException(
                    "The stream chunk format does not match this PCM16 AudioClip stream.",
                    nameof(chunk));
            }

            return TryWritePcm16Core(
                chunk.Data,
                0,
                chunk.Data.Length,
                chunk.IsLast);
        }

        /// <summary>
        /// 写入第三方提供的无 WAV 头 PCM16 Little Endian 数据。
        /// 数据格式必须与创建会话时的采样率和声道数一致。
        /// </summary>
        public bool TryWritePcm16(byte[] pcm16Data, bool isFinal = false)
        {
            AudioRecorderDriver.EnsureMainThread();
            if (pcm16Data == null)
            {
                throw new ArgumentNullException(nameof(pcm16Data));
            }

            return TryWritePcm16Core(
                pcm16Data,
                0,
                pcm16Data.Length,
                isFinal);
        }

        /// <summary>写入 PCM16 数组的一段切片。</summary>
        public bool TryWritePcm16(
            byte[] pcm16Data,
            int offset,
            int count,
            bool isFinal = false)
        {
            AudioRecorderDriver.EnsureMainThread();
            return TryWritePcm16Core(pcm16Data, offset, count, isFinal);
        }

        public void CompleteInput()
        {
            AudioRecorderDriver.EnsureMainThread();
            CompleteInputCore();
        }

        public void Abort()
        {
            AudioRecorderDriver.EnsureMainThread();
            if (IsDisposed)
            {
                return;
            }

            Interlocked.Exchange(ref aborted, 1);
            Interlocked.Exchange(ref inputCompleted, 1);
            Interlocked.Exchange(ref drained, 1);
            PublishReadFrame(Interlocked.Read(ref publishedWriteFrame));
        }

        public void Dispose()
        {
            AudioRecorderDriver.EnsureMainThread();
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref aborted, 1);
            Interlocked.Exchange(ref inputCompleted, 1);
            Interlocked.Exchange(ref drained, 1);
            PublishReadFrame(Interlocked.Read(ref publishedWriteFrame));

            AudioClip clip = Clip;
            Clip = null;
            AudioClipCreationScheduler.DestroyAudioClip(clip);
        }

        internal void ReadPcmData(float[] destination)
        {
            if (destination == null)
            {
                return;
            }

            if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref aborted) != 0)
            {
                Array.Clear(destination, 0, destination.Length);
                return;
            }

            int requestedFrames = destination.Length / Channels;
            if (requestedFrames <= 0 || requestedFrames * Channels != destination.Length)
            {
                Array.Clear(destination, 0, destination.Length);
                return;
            }

            long readFrame = Interlocked.Read(ref publishedReadFrame);
            long writeFrame = Interlocked.Read(ref publishedWriteFrame);
            long availableFrames = Math.Max(0L, writeFrame - readFrame);
            bool completed = Volatile.Read(ref inputCompleted) != 0;

            if (Volatile.Read(ref playbackStarted) == 0)
            {
                if (availableFrames < prebufferFrames && !(completed && availableFrames > 0))
                {
                    if (completed && availableFrames == 0)
                    {
                        Interlocked.Exchange(ref drained, 1);
                    }

                    Array.Clear(destination, 0, destination.Length);
                    return;
                }

                Interlocked.Exchange(ref playbackStarted, 1);
            }

            int framesToRead = (int)Math.Min(requestedFrames, availableFrames);
            if (framesToRead > 0)
            {
                CopyFramesFromRing(readFrame, framesToRead, destination);
                readFrame += framesToRead;
            }

            if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref aborted) != 0)
            {
                Array.Clear(destination, 0, destination.Length);
                PublishReadFrame(Interlocked.Read(ref publishedWriteFrame));
                return;
            }

            if (framesToRead > 0)
            {
                PublishReadFrame(readFrame);
            }

            int copiedSampleValues = framesToRead * Channels;
            if (copiedSampleValues < destination.Length)
            {
                Array.Clear(
                    destination,
                    copiedSampleValues,
                    destination.Length - copiedSampleValues);

                if (!completed)
                {
                    Interlocked.Increment(ref underflowCount);
                    Interlocked.Add(ref underflowFrames, requestedFrames - framesToRead);
                }
            }

            if (completed && readFrame >= writeFrame)
            {
                Interlocked.Exchange(ref drained, 1);
            }
        }

        private bool TryWritePcm16Core(
            byte[] pcm16Data,
            int offset,
            int count,
            bool isFinal)
        {
            if (pcm16Data == null)
            {
                throw new ArgumentNullException(nameof(pcm16Data));
            }

            if (offset < 0 || count < 0 || offset > pcm16Data.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            int bytesPerFrame = checked(Channels * sizeof(short));
            if (count % bytesPerFrame != 0)
            {
                throw new ArgumentException(
                    "PCM16 data must contain complete interleaved sample frames.",
                    nameof(count));
            }

            if (IsDisposed || IsAborted || IsInputCompleted)
            {
                return false;
            }

            int frameCount = count / bytesPerFrame;
            if (frameCount > AudioRecorder.MaximumRealtimeWriteFrames)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    "A single real-time PCM write exceeds the frame limit. Split it across Unity frames.");
            }

            long writeFrame = Interlocked.Read(ref publishedWriteFrame);
            long readFrame = Interlocked.Read(ref publishedReadFrame);
            long bufferedFrames = Math.Max(0L, writeFrame - readFrame);
            if (frameCount > capacityFrames - bufferedFrames)
            {
                Interlocked.Add(ref droppedFrames, frameCount);
                return false;
            }

            if (frameCount > 0)
            {
                WriteFramesToRing(pcm16Data, offset, frameCount, writeFrame);
                Interlocked.Exchange(ref publishedWriteFrame, writeFrame + frameCount);
            }

            if (isFinal)
            {
                CompleteInputCore();
            }

            return true;
        }

        private void CompleteInputCore()
        {
            if (IsDisposed || IsAborted)
            {
                return;
            }

            Interlocked.Exchange(ref inputCompleted, 1);
            if (Interlocked.Read(ref publishedReadFrame) >=
                Interlocked.Read(ref publishedWriteFrame))
            {
                Interlocked.Exchange(ref drained, 1);
            }
        }

        private void WriteFramesToRing(
            byte[] source,
            int sourceByteOffset,
            int frameCount,
            long writeFrame)
        {
            int ringFrameOffset = (int)(writeFrame % capacityFrames);
            int firstFrameCount = Math.Min(frameCount, capacityFrames - ringFrameOffset);
            int firstSampleValueCount = firstFrameCount * Channels;
            DecodePcm16(
                source,
                sourceByteOffset,
                firstSampleValueCount,
                ringFrameOffset * Channels);

            int remainingFrames = frameCount - firstFrameCount;
            if (remainingFrames > 0)
            {
                DecodePcm16(
                    source,
                    sourceByteOffset + firstSampleValueCount * sizeof(short),
                    remainingFrames * Channels,
                    0);
            }
        }

        private void DecodePcm16(
            byte[] source,
            int sourceByteOffset,
            int sampleValueCount,
            int ringSampleOffset)
        {
            for (int i = 0; i < sampleValueCount; i++)
            {
                short value = (short)(source[sourceByteOffset] |
                    source[sourceByteOffset + 1] << 8);
                ringSamples[ringSampleOffset + i] = value / 32768f;
                sourceByteOffset += sizeof(short);
            }
        }

        private void CopyFramesFromRing(
            long readFrame,
            int frameCount,
            float[] destination)
        {
            int ringFrameOffset = (int)(readFrame % capacityFrames);
            int firstFrameCount = Math.Min(frameCount, capacityFrames - ringFrameOffset);
            int firstSampleValueCount = firstFrameCount * Channels;
            Array.Copy(
                ringSamples,
                ringFrameOffset * Channels,
                destination,
                0,
                firstSampleValueCount);

            int remainingFrames = frameCount - firstFrameCount;
            if (remainingFrames > 0)
            {
                Array.Copy(
                    ringSamples,
                    0,
                    destination,
                    firstSampleValueCount,
                    remainingFrames * Channels);
            }
        }

        private void SetPlaybackPosition(int position)
        {
            _ = position;
        }

        private void PublishReadFrame(long readFrame)
        {
            while (true)
            {
                long current = Interlocked.Read(ref publishedReadFrame);
                if (current >= readFrame)
                {
                    return;
                }

                if (Interlocked.CompareExchange(
                    ref publishedReadFrame,
                    readFrame,
                    current) == current)
                {
                    return;
                }
            }
        }
    }
}
