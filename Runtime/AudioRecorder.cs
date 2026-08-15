using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Cowart.AudioRecorder
{
    /// <summary>
    /// 跨平台语音录制的唯一业务入口。无需在场景中挂载组件。
    /// 启动方法的 bool 只表示请求是否被后端接受，最终结果通过静态事件交付。
    /// </summary>
    [Preserve]
    public static class AudioRecorder
    {
        public const int MinimumDurationSeconds = 5;
        public const int DefaultMaximumDurationSeconds = 300;
        public const int MaximumDurationSeconds = 3600;
        public const int MinimumStreamChunkMilliseconds = 20;
        public const int MaximumStreamChunkMilliseconds = 1000;
        public const int DefaultStreamChunkMilliseconds = 40;
        public const int OutputSampleRate = 16000;
        public const int OutputChannels = 1;
        public const int OutputBitsPerSample = 16;
        public const int MinimumAudioClipDecodeFramesPerUpdate = 256;
        public const int MaximumAudioClipDecodeFramesPerUpdate = 65536;
        public const int DefaultAudioClipDecodeFramesPerUpdate = 8192;
        public const int MinimumRealtimeBufferMilliseconds = 100;
        public const int MaximumRealtimeBufferMilliseconds = 10000;
        public const int DefaultRealtimeBufferMilliseconds = 2000;
        public const int DefaultRealtimePrebufferMilliseconds = 120;
        public const int MaximumRealtimeBufferSampleValues = 2000000;
        public const int MaximumRealtimeWriteFrames = 16384;

        public static event Action RecordingStarted;

        public static event Action<RecordedAudio> RecordingCompleted;

        public static event Action RecordingCanceled;

        public static event Action<string> RecordingFailed;

        public static event Action<AudioStreamChunk> AudioChunkReceived;

        public static RecordedAudio LastRecording { get; private set; }

        public static AudioStreamChunk LastStreamChunk { get; private set; }

        public static bool IsAvailable
        {
            get
            {
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_ANDROID || (UNITY_WEBGL && !UNITY_EDITOR)
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// 当前运行环境能否创建 PCMReaderCallback 驱动的动态流式 AudioClip。
        /// Unity 2021.3 WebGL Player 不支持；WebGL 目标下的 Editor Play Mode 仍是 Editor 后端。
        /// </summary>
        public static bool IsRealtimeAudioClipStreamingSupported
        {
            get
            {
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_ANDROID
                return true;
#else
                return false;
#endif
            }
        }

        public static bool IsRecording =>
            AudioRecorderDriver.Current != null && AudioRecorderDriver.Current.IsRecording;

        public static bool IsFinalizing =>
            AudioRecorderDriver.Current != null && AudioRecorderDriver.Current.IsFinalizing;

        public static bool IsRecordingActive =>
            AudioRecorderDriver.Current != null && AudioRecorderDriver.Current.IsRecordingActive;

        public static bool IsStartPending =>
            AudioRecorderDriver.Current != null && AudioRecorderDriver.Current.IsStartPending;

        public static bool IsStreamingActive =>
            AudioRecorderDriver.Current != null && AudioRecorderDriver.Current.IsStreamingActive;

        public static bool IsPlaybackActive =>
            AudioRecorderDriver.Current != null && AudioRecorderDriver.Current.IsPlaybackActive;

        public static bool HasRecording => LastRecording != null;

        public static float RecordingElapsedSeconds =>
            AudioRecorderDriver.Current != null
                ? AudioRecorderDriver.Current.RecordingElapsedSeconds
                : 0f;

        public static float RecordingRemainingSeconds =>
            AudioRecorderDriver.Current != null
                ? AudioRecorderDriver.Current.RecordingRemainingSeconds
                : 0f;

        public static float RecordingProgress =>
            AudioRecorderDriver.Current != null
                ? AudioRecorderDriver.Current.RecordingProgress
                : 0f;

        public static float CurrentInputLevel =>
            AudioRecorderDriver.Current != null
                ? AudioRecorderDriver.Current.CurrentInputLevel
                : 0f;

        public static float CurrentPeakLevel =>
            AudioRecorderDriver.Current != null
                ? AudioRecorderDriver.Current.CurrentPeakLevel
                : 0f;

        public static float PlaybackElapsedSeconds =>
            AudioRecorderDriver.Current != null
                ? AudioRecorderDriver.Current.PlaybackElapsedSeconds
                : 0f;

        public static float PlaybackDurationSeconds =>
            AudioRecorderDriver.Current != null
                ? AudioRecorderDriver.Current.PlaybackDurationSeconds
                : 0f;

        public static float PlaybackRemainingSeconds =>
            AudioRecorderDriver.Current != null
                ? AudioRecorderDriver.Current.PlaybackRemainingSeconds
                : 0f;

        public static float PlaybackProgress =>
            AudioRecorderDriver.Current != null
                ? AudioRecorderDriver.Current.PlaybackProgress
                : 0f;

        public static int StreamChunkCount =>
            AudioRecorderDriver.Current != null
                ? AudioRecorderDriver.Current.StreamChunkCount
                : 0;

        public static int StreamedPcmByteCount =>
            AudioRecorderDriver.Current != null
                ? AudioRecorderDriver.Current.StreamedPcmByteCount
                : 0;

        /// <summary>
        /// 把 PCM16 RIFF/WAVE 字节异步生成为普通 Unity AudioClip。
        /// 原生平台在工作线程解码，AudioClip 创建和 SetData 在主线程按帧分批执行；
        /// WebGL Player 无托管线程，改为每帧协作式解码。调用完成前不要修改 wavBytes。
        /// </summary>
        public static Task<AudioClip> CreateAudioClipFromPcm16WavAsync(
            byte[] wavBytes,
            string clipName = "PCM16 WAV Audio",
            int decodeFramesPerUpdate = DefaultAudioClipDecodeFramesPerUpdate,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            AudioRecorderDriver.EnsureMainThread();
            if (wavBytes == null)
            {
                throw new ArgumentNullException(nameof(wavBytes));
            }

            if (wavBytes.Length == 0)
            {
                throw new ArgumentException("The PCM16 WAV byte array is empty.", nameof(wavBytes));
            }

            if (decodeFramesPerUpdate < MinimumAudioClipDecodeFramesPerUpdate ||
                decodeFramesPerUpdate > MaximumAudioClipDecodeFramesPerUpdate)
            {
                throw new ArgumentOutOfRangeException(nameof(decodeFramesPerUpdate));
            }

            return AudioClipCreationScheduler.Schedule(
                wavBytes,
                NormalizeClipName(clipName),
                decodeFramesPerUpdate,
                cancellationToken);
        }

        /// <summary>
        /// 从完整 PCM16 WAV 创建按需解码的原生流式 AudioClip，不先展开整段 float[]。
        /// 返回的 AudioClip 会继续读取 wavBytes，因此使用期间不要修改该数组。
        /// </summary>
        public static AudioClip CreateStreamingAudioClipFromPcm16Wav(
            byte[] wavBytes,
            string clipName = "PCM16 WAV Stream")
        {
            AudioRecorderDriver.EnsureMainThread();
            if (!IsRealtimeAudioClipStreamingSupported)
            {
                throw new PlatformNotSupportedException(
                    "Unity 2021.3 WebGL Player does not support dynamic streaming AudioClip creation.");
            }

            Pcm16WavData wavData = Pcm16WavData.Parse(wavBytes);
            Pcm16WavClipReader reader = new Pcm16WavClipReader(wavData);
            AudioClip clip = AudioClip.Create(
                NormalizeClipName(clipName),
                wavData.FrameCount,
                wavData.Channels,
                wavData.SampleRate,
                true,
                reader.Read,
                reader.SetPosition);
            if (clip == null)
            {
                throw new InvalidOperationException("Unity failed to create the streaming AudioClip.");
            }

            return clip;
        }

        /// <summary>
        /// 创建可持续写入 PCM16 数据的实时 Unity AudioClip 会话。
        /// 录音入口仍只有 StartRecording；此对象只消费 AudioStreamChunk 或第三方 PCM16 数据。
        /// </summary>
        public static Pcm16AudioClipStream CreateRealtimeAudioClipStream(
            string clipName = "Realtime PCM16 Stream",
            int sampleRate = OutputSampleRate,
            int channels = OutputChannels,
            int bufferCapacityMilliseconds = DefaultRealtimeBufferMilliseconds,
            int prebufferMilliseconds = DefaultRealtimePrebufferMilliseconds)
        {
            AudioRecorderDriver.EnsureMainThread();
            if (!IsRealtimeAudioClipStreamingSupported)
            {
                throw new PlatformNotSupportedException(
                    "Unity 2021.3 WebGL Player does not support dynamic streaming AudioClip creation.");
            }

            if (sampleRate <= 0 || sampleRate > 192000)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            }

            if (channels <= 0 || channels > 8)
            {
                throw new ArgumentOutOfRangeException(nameof(channels));
            }

            if (bufferCapacityMilliseconds < MinimumRealtimeBufferMilliseconds ||
                bufferCapacityMilliseconds > MaximumRealtimeBufferMilliseconds)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferCapacityMilliseconds));
            }

            if (prebufferMilliseconds <= 0 ||
                prebufferMilliseconds > bufferCapacityMilliseconds)
            {
                throw new ArgumentOutOfRangeException(nameof(prebufferMilliseconds));
            }

            long capacityFrames =
                ((long)sampleRate * bufferCapacityMilliseconds + 999L) / 1000L;
            if (capacityFrames * channels > MaximumRealtimeBufferSampleValues)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bufferCapacityMilliseconds),
                    "The requested real-time ring buffer exceeds the managed sample-value limit.");
            }

            return new Pcm16AudioClipStream(
                NormalizeClipName(clipName),
                sampleRate,
                channels,
                bufferCapacityMilliseconds,
                prebufferMilliseconds);
        }

        /// <summary>
        /// 开始一次录音。WebGL 中必须从按钮点击等用户手势直接调用。
        /// </summary>
        /// <returns>后端接受启动请求时为 true；最终成功、取消或失败由静态事件报告。</returns>
        public static bool StartRecording(
            int maxDurationSeconds = DefaultMaximumDurationSeconds,
            bool streamAudio = false,
            int streamChunkDurationMilliseconds = DefaultStreamChunkMilliseconds)
        {
            ValidateConfiguration(maxDurationSeconds, streamChunkDurationMilliseconds);
            AudioRecorderDriver.EnsureMainThread();
            if (!IsAvailable || IsRecording)
            {
                return false;
            }

            AudioRecorderDriver driver = AudioRecorderDriver.GetOrCreate();
            driver.SetMaxDurationSeconds(maxDurationSeconds);
            driver.SetStreamingEnabled(streamAudio);
            driver.SetStreamChunkDurationMilliseconds(streamChunkDurationMilliseconds);
            return driver.StartRecording();
        }

        /// <summary>停止当前录音并生成完整 WAV。</summary>
        public static bool StopRecording()
        {
            AudioRecorderDriver.EnsureMainThread();
            AudioRecorderDriver driver = AudioRecorderDriver.Current;
            if (driver == null || !driver.IsRecording)
            {
                return false;
            }

            return driver.StopRecording();
        }

        /// <summary>从头播放最近一次完整录音。</summary>
        public static bool PlayLastRecording()
        {
            AudioRecorderDriver.EnsureMainThread();
            AudioRecorderDriver driver = AudioRecorderDriver.Current;
            return LastRecording != null && driver != null && driver.PlayLastRecording();
        }

        public static bool StopPlayback()
        {
            AudioRecorderDriver.EnsureMainThread();
            AudioRecorderDriver driver = AudioRecorderDriver.Current;
            if (driver == null || !driver.IsPlaybackActive)
            {
                return false;
            }

            driver.StopPlayback();
            return true;
        }

        public static bool ClearRecording()
        {
            AudioRecorderDriver.EnsureMainThread();
            if (IsRecording)
            {
                return false;
            }

            AudioRecorderDriver driver = AudioRecorderDriver.Current;
            if (driver != null)
            {
                driver.ClearRecording();
            }

            LastRecording = null;
            LastStreamChunk = null;
            return true;
        }

        internal static bool HasAudioChunkSubscribers => AudioChunkReceived != null;

        internal static void NotifyRecordingStarted()
        {
            InvokeSafely(RecordingStarted);
        }

        internal static void NotifyRecordingCompleted(
            byte[] wavBytes,
            string fileName,
            float durationSeconds)
        {
            LastRecording = new RecordedAudio(
                fileName,
                "audio/wav",
                wavBytes,
                durationSeconds,
                OutputSampleRate,
                OutputChannels,
                OutputBitsPerSample);
            InvokeSafely(RecordingCompleted, LastRecording);
        }

        internal static void NotifyRecordingCanceled()
        {
            InvokeSafely(RecordingCanceled);
        }

        internal static void NotifyRecordingFailed(string message)
        {
            InvokeSafely(RecordingFailed, message ?? string.Empty);
        }

        internal static void NotifyAudioChunk(AudioStreamChunk chunk)
        {
            LastStreamChunk = chunk;
            InvokeSafely(AudioChunkReceived, chunk);
        }

        internal static void InvokeSafely(Action callback)
        {
            if (callback == null)
            {
                return;
            }

            Delegate[] callbacks = callback.GetInvocationList();
            for (int i = 0; i < callbacks.Length; i++)
            {
                try
                {
                    ((Action)callbacks[i])();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        internal static void InvokeSafely<T>(Action<T> callback, T value)
        {
            if (callback == null)
            {
                return;
            }

            Delegate[] callbacks = callback.GetInvocationList();
            for (int i = 0; i < callbacks.Length; i++)
            {
                try
                {
                    ((Action<T>)callbacks[i])(value);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            RecordingStarted = null;
            RecordingCompleted = null;
            RecordingCanceled = null;
            RecordingFailed = null;
            AudioChunkReceived = null;
            LastRecording = null;
            LastStreamChunk = null;
        }

        private static void ValidateConfiguration(
            int maxDurationSeconds,
            int streamChunkDurationMilliseconds)
        {
            if (maxDurationSeconds < MinimumDurationSeconds ||
                maxDurationSeconds > MaximumDurationSeconds)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDurationSeconds));
            }

            if (streamChunkDurationMilliseconds < MinimumStreamChunkMilliseconds ||
                streamChunkDurationMilliseconds > MaximumStreamChunkMilliseconds)
            {
                throw new ArgumentOutOfRangeException(nameof(streamChunkDurationMilliseconds));
            }
        }

        private static string NormalizeClipName(string clipName)
        {
            return string.IsNullOrWhiteSpace(clipName)
                ? "PCM16 Audio"
                : clipName;
        }
    }
}
