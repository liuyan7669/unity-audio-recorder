using System;

namespace Cowart.AudioRecorder
{
    /// <summary>
    /// 录音过程中返回的一段厂商无关 PCM16 音频。
    /// Data 为无 WAV 文件头的 Little Endian PCM，可直接交给实时语音识别适配器。
    /// </summary>
    [Serializable]
    public sealed class AudioStreamChunk
    {
        public AudioStreamChunk(
            byte[] data,
            int sequence,
            int timestampMilliseconds,
            int sampleRate,
            int channels,
            int bitsPerSample,
            bool isFirst,
            bool isLast)
        {
            Data = data ?? Array.Empty<byte>();
            Sequence = sequence;
            TimestampMilliseconds = timestampMilliseconds;
            SampleRate = sampleRate;
            Channels = channels;
            BitsPerSample = bitsPerSample;
            IsFirst = isFirst;
            IsLast = isLast;
        }

        /// <summary>无 WAV 文件头的 PCM16 Little Endian 字节；最后一个结束块允许为空。</summary>
        public byte[] Data { get; }

        /// <summary>从 0 开始递增的块序号。</summary>
        public int Sequence { get; }

        /// <summary>该块第一帧相对本次录音开始的时间。</summary>
        public int TimestampMilliseconds { get; }

        public int SampleRate { get; }

        public int Channels { get; }

        public int BitsPerSample { get; }

        public bool IsFirst { get; }

        public bool IsLast { get; }

        /// <summary>当前块包含的音频时长。</summary>
        public int DurationMilliseconds
        {
            get
            {
                int bytesPerFrame = Channels * BitsPerSample / 8;
                if (SampleRate <= 0 || bytesPerFrame <= 0)
                {
                    return 0;
                }

                return (int)Math.Round(
                    Data.Length * 1000d / bytesPerFrame / SampleRate);
            }
        }
    }
}
