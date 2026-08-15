using System;

namespace Cowart.AudioRecorder
{
    /// <summary>
    /// 一次完整录音的只读结果。Data 是完整的 PCM16 WAV 原始字节，不是路径或 Base64。
    /// </summary>
    [Serializable]
    public sealed class RecordedAudio
    {
        internal RecordedAudio(
            string name,
            string mimeType,
            byte[] data,
            float durationSeconds,
            int sampleRate,
            int channels,
            int bitsPerSample)
        {
            Name = name ?? string.Empty;
            MimeType = string.IsNullOrEmpty(mimeType) ? "audio/wav" : mimeType;
            Data = data ?? Array.Empty<byte>();
            DurationSeconds = Math.Max(0f, durationSeconds);
            SampleRate = sampleRate;
            Channels = channels;
            BitsPerSample = bitsPerSample;
        }

        public string Name { get; }

        public string MimeType { get; }

        public byte[] Data { get; }

        public int Size => Data.Length;

        public float DurationSeconds { get; }

        public int SampleRate { get; }

        public int Channels { get; }

        public int BitsPerSample { get; }
    }
}
