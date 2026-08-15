using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AOT;
using UnityEngine;
using UnityEngine.Scripting;

#if UNITY_EDITOR
using UnityEditor;
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace Cowart.AudioRecorder
{
    /// <summary>
    /// 在 Unity Editor、Windows Player、Android APK 和桌面浏览器 WebGL 中录制麦克风。
    /// 输出统一为 16 kHz、单声道、16-bit PCM WAV。WebGL 必须运行在安全上下文
    /// （生产环境 HTTPS，开发环境可用 localhost），并且 StartRecording 必须直接由用户点击触发。
    /// Android WebGL 不在支持范围；Android 指 Unity 原生 APK。
    /// </summary>
    [Preserve]
    [RequireComponent(typeof(AudioSource))]
    internal sealed class AudioRecorderDriver : MonoBehaviour
    {
        private static AudioRecorderDriver instance;
        private static int mainThreadId;

        internal static AudioRecorderDriver Current => instance;

        internal static AudioRecorderDriver GetOrCreate()
        {
            EnsureMainThread();
            if (instance != null)
            {
                return instance;
            }

            GameObject host = new GameObject("[Cowart.AudioRecorder]");
            host.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(host);
            instance = host.AddComponent<AudioRecorderDriver>();
            return instance;
        }

        internal static void EnsureMainThread()
        {
            if (mainThreadId == 0 || Thread.CurrentThread.ManagedThreadId != mainThreadId)
            {
                throw new InvalidOperationException(
                    "Audio Recorder public APIs must be called on the Unity main thread.");
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            instance = null;
#if UNITY_WEBGL && !UNITY_EDITOR
            activeWebGLRecorder = null;
            webGLCallbacksRegistered = false;
#endif
        }

#if UNITY_EDITOR
        [InitializeOnLoadMethod]
        private static void InitializeEditorState()
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }
#endif

        public const int MinimumDurationSeconds = 5;
        public const int DefaultMaximumDurationSeconds = 300;
        public const int MaximumDurationSeconds = 3600;
        public const int MinimumStreamChunkMilliseconds = 20;
        public const int MaximumStreamChunkMilliseconds = 1000;

        private const int MinimumSampleRate = 8000;
        private const int MaximumSampleRate = 48000;
        private const int NativeMicrophoneRingBufferSeconds = 10;
        private const int NativeFileChunkDurationMilliseconds = 1000;
        private const int LevelSampleFrameCount = 512;
        private const float MinimumLevelDecibels = -60f;
        private const float LevelAttackSpeed = 8f;
        private const float LevelReleaseSpeed = 3f;
        private const float PeakReleaseSpeed = 0.65f;
        private const float PeakHoldSeconds = 0.3f;
        private const int WebGLStateStarted = 0;
        private const int WebGLStateFailed = 1;
        private const int WebGLStateCanceled = 2;
        private const int WebGLStateFinalizing = 3;

        [Header("Recording")]
        [Tooltip("单次录音时长上限，支持 5–3600 秒。必须在开始录音前设置。")]
        [SerializeField, Range(MinimumDurationSeconds, MaximumDurationSeconds)]
        private int maxDurationSeconds = DefaultMaximumDurationSeconds;

        [SerializeField, Range(MinimumSampleRate, MaximumSampleRate)]
        private int targetSampleRate = 16000;

        [Tooltip("留空时使用系统默认麦克风。仅 Editor、Windows Player 和 Android APK 使用。")]
        [SerializeField]
        private string microphoneDeviceName = string.Empty;

        [Header("Streaming")]
        [Tooltip("启用后，只有录音开始前已经订阅 AudioChunkReceived 时才会持续生成 PCM16 音频块。")]
        [SerializeField]
        private bool streamingEnabled = true;

        [Tooltip("每个流式 PCM 块的目标时长，支持 20–1000 毫秒。")]
        [SerializeField, Range(MinimumStreamChunkMilliseconds, MaximumStreamChunkMilliseconds)]
        private int streamChunkDurationMilliseconds = 40;

        [Header("Playback")]
        [SerializeField]
        private AudioSource playbackSource;

#if !UNITY_WEBGL || UNITY_EDITOR
        private AudioClip nativeRecordingClip;
        private Pcm16RecordingFile nativeRecordingFile;
        private NativeMicrophoneRingCursor nativeRingCursor;
        private Task<byte[]> nativeFinalizeTask;
        private int nativeFinalizeGeneration;
        private float nativeFinalizeDurationSeconds;
#endif
        private AudioClip playbackClip;
        private double recordingStartedAt;
        private float currentRecordingElapsedSeconds;
        private float currentInputLevel;
        private float currentPeakLevel;
        private float targetInputLevel;
        private float targetPeakLevel;
        private float peakHoldUntil;
        private float currentPlaybackTimeSeconds;
        private float currentPlaybackDurationSeconds;
#if !UNITY_WEBGL || UNITY_EDITOR
        private float[] nativeLevelSamples;
#endif
        private int streamSequence;
        private int streamedOutputFrameCount;
        private int streamChunkCount;
        private int streamedPcmByteCount;
        private readonly RecordingSessionGate recordingSession = new RecordingSessionGate();
        private bool isRecording;
        private bool isStartPending;
        private bool isPlaybackActive;
        private bool streamRequestedForSession;
        private bool isStreamingActive;
        private byte[] lastRecordingBytes;
        private AudioStreamChunk lastStreamChunk;

#if UNITY_ANDROID && !UNITY_EDITOR
        private PermissionCallbacks androidPermissionCallbacks;
        private int androidPermissionGeneration;
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void WebGLStateCallback(int state, IntPtr messageBytes, int messageLength);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void WebGLDataCallback(
            IntPtr dataBytes,
            int dataLength,
            int sampleRate,
            int channels,
            int durationMilliseconds);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void WebGLLevelCallback(float rms, float peak);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void WebGLStreamCallback(
            IntPtr dataBytes,
            int dataLength,
            int sequence,
            int timestampMilliseconds,
            int isFirst,
            int isLast);

        private static readonly WebGLStateCallback BrowserStateCallback = OnWebGLStateChanged;
        private static readonly WebGLDataCallback BrowserDataCallback = OnWebGLDataReceived;
        private static readonly WebGLLevelCallback BrowserLevelCallback = OnWebGLLevelChanged;
        private static readonly WebGLStreamCallback BrowserStreamCallback = OnWebGLStreamChunkReceived;
        private static AudioRecorderDriver activeWebGLRecorder;
        private static bool webGLCallbacksRegistered;

        [DllImport("__Internal")]
        private static extern void CowartWebGLAudio_RegisterCallbacks(
            WebGLStateCallback stateCallback,
            WebGLDataCallback dataCallback,
            WebGLLevelCallback levelCallback,
            WebGLStreamCallback streamCallback);

        [DllImport("__Internal")]
        private static extern void CowartWebGLAudio_UnregisterCallbacks();

        [DllImport("__Internal")]
        private static extern int CowartWebGLAudio_IsSupported();

        [DllImport("__Internal")]
        private static extern int CowartWebGLAudio_Start(
            int sampleRate,
            int maxDurationMilliseconds,
            int streamChunkMilliseconds);

        [DllImport("__Internal")]
        private static extern int CowartWebGLAudio_Stop();

        [DllImport("__Internal")]
        private static extern int CowartWebGLAudio_Play();

        [DllImport("__Internal")]
        private static extern void CowartWebGLAudio_StopPlayback();

        [DllImport("__Internal")]
        private static extern int CowartWebGLAudio_IsPlaying();

        [DllImport("__Internal")]
        private static extern int CowartWebGLAudio_GetPlaybackTimeMilliseconds();

        [DllImport("__Internal")]
        private static extern int CowartWebGLAudio_GetPlaybackDurationMilliseconds();

        [DllImport("__Internal")]
        private static extern void CowartWebGLAudio_Clear();
#endif

        /// <summary>麦克风正在录制，或正在等待浏览器/Android 的权限结果。</summary>
        public bool IsRecording => recordingSession.IsActive &&
                                   (isRecording || isStartPending || recordingSession.IsFinalizing);

        /// <summary>麦克风已经真正开始采样。</summary>
        public bool IsRecordingActive => isRecording;

        /// <summary>正在等待浏览器或 Android 麦克风权限结果。</summary>
        public bool IsStartPending => isStartPending;

        /// <summary>已停止采集，正在生成并读取最终 WAV。</summary>
        public bool IsFinalizing => recordingSession.IsFinalizing;

        /// <summary>单次录音时长上限，范围为 5–3600 秒。录音过程中设置会被忽略。</summary>
        public int MaxDurationSeconds
        {
            get => maxDurationSeconds;
            set => SetMaxDurationSeconds(value);
        }

        /// <summary>当前录音已经进行的秒数；停止后保留最近一次录音时长。</summary>
        public float RecordingElapsedSeconds => currentRecordingElapsedSeconds;

        /// <summary>距离当前录音上限的剩余秒数。</summary>
        public float RecordingRemainingSeconds => Mathf.Max(
            0f,
            maxDurationSeconds - currentRecordingElapsedSeconds);

        /// <summary>当前录音时间进度，范围 0–1。</summary>
        public float RecordingProgress => maxDurationSeconds > 0
            ? Mathf.Clamp01(currentRecordingElapsedSeconds / maxDurationSeconds)
            : 0f;

        /// <summary>实时麦克风 RMS 电平，已从 -60–0 dB 映射到 0–1。</summary>
        public float CurrentInputLevel => currentInputLevel;

        /// <summary>带短暂保持和衰减的峰值电平，范围 0–1。</summary>
        public float CurrentPeakLevel => currentPeakLevel;

        /// <summary>是否允许在有订阅者时为下一次录音生成流式 PCM 块。</summary>
        public bool StreamingEnabled
        {
            get => streamingEnabled;
            set => SetStreamingEnabled(value);
        }

        /// <summary>流式音频块的目标时长，范围为 20–1000 毫秒。</summary>
        public int StreamChunkDurationMilliseconds
        {
            get => streamChunkDurationMilliseconds;
            set => SetStreamChunkDurationMilliseconds(value);
        }

        /// <summary>当前录音是否正在产生流式 PCM 块。</summary>
        public bool IsStreamingActive => isStreamingActive;

        /// <summary>本次录音已经发出的块数量，包含可能为空的最终结束块。</summary>
        public int StreamChunkCount => streamChunkCount;

        /// <summary>本次录音已经发出的 PCM 数据字节数，不包含 WAV 文件头。</summary>
        public int StreamedPcmByteCount => streamedPcmByteCount;

        /// <summary>最近发出的流式块。</summary>
        public AudioStreamChunk LastStreamChunk => lastStreamChunk;

        /// <summary>当前语音片段是否正在播放。</summary>
        public bool IsPlaybackActive => isPlaybackActive;

        /// <summary>当前播放位置，直接读取实际播放源，单位为秒。</summary>
        public float PlaybackElapsedSeconds => currentPlaybackTimeSeconds;

        /// <summary>
        /// 当前播放语音片段的原始总时长，直接读取 AudioClip.length 或浏览器 Audio.duration，
        /// 不使用最近一次录音过程记录的时长。
        /// </summary>
        public float PlaybackDurationSeconds => currentPlaybackDurationSeconds;

        /// <summary>当前播放语音片段的剩余时间，单位为秒。</summary>
        public float PlaybackRemainingSeconds => Mathf.Max(
            0f,
            currentPlaybackDurationSeconds - currentPlaybackTimeSeconds);

        /// <summary>当前播放进度，范围 0–1。</summary>
        public float PlaybackProgress => currentPlaybackDurationSeconds > 0f
            ? Mathf.Clamp01(currentPlaybackTimeSeconds / currentPlaybackDurationSeconds)
            : 0f;

        /// <summary>是否已经得到一份完整的 WAV 录音。</summary>
        public bool HasRecording => lastRecordingBytes != null && lastRecordingBytes.Length > 44;

        /// <summary>最近录音的完整 PCM16 WAV 字节。没有录音时为 null。</summary>
        public byte[] LastRecordingBytes => lastRecordingBytes;

        /// <summary>最近录音在原生平台上的播放 AudioClip。WebGL 使用浏览器播放，因此该值为 null。</summary>
        public AudioClip LastRecordingClip => playbackClip;

        /// <summary>最近录音的建议文件名。</summary>
        public string LastRecordingFileName { get; private set; }

        /// <summary>最近录音的秒数。</summary>
        public float LastRecordingDurationSeconds { get; private set; }

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }

            instance = this;
            maxDurationSeconds = Mathf.Clamp(
                maxDurationSeconds,
                MinimumDurationSeconds,
                MaximumDurationSeconds);
            streamChunkDurationMilliseconds = Mathf.Clamp(
                streamChunkDurationMilliseconds,
                MinimumStreamChunkMilliseconds,
                MaximumStreamChunkMilliseconds);

            if (playbackSource == null)
            {
                playbackSource = GetComponent<AudioSource>();
            }

            playbackSource.playOnAwake = false;
        }

        private void Update()
        {
            UpdateRecordingMetrics();
            UpdatePlaybackMetrics();

#if !UNITY_WEBGL || UNITY_EDITOR
            UpdateNativeFinalization();
            if (isRecording && currentRecordingElapsedSeconds >= maxDurationSeconds)
            {
                StopRecording();
            }
#endif
        }

        private void OnValidate()
        {
            maxDurationSeconds = Mathf.Clamp(
                maxDurationSeconds,
                MinimumDurationSeconds,
                MaximumDurationSeconds);
            targetSampleRate = Mathf.Clamp(targetSampleRate, MinimumSampleRate, MaximumSampleRate);
            streamChunkDurationMilliseconds = Mathf.Clamp(
                streamChunkDurationMilliseconds,
                MinimumStreamChunkMilliseconds,
                MaximumStreamChunkMilliseconds);
        }

        private void UpdateRecordingMetrics()
        {
            if (isRecording)
            {
                currentRecordingElapsedSeconds = Mathf.Clamp(
                    (float)(Time.realtimeSinceStartupAsDouble - recordingStartedAt),
                    0f,
                    maxDurationSeconds);

#if !UNITY_WEBGL || UNITY_EDITOR
                UpdateNativeInputLevel();
                UpdateNativeAudioStream();
#endif
            }
            else
            {
                targetInputLevel = 0f;
                targetPeakLevel = 0f;
            }

            float levelSpeed = targetInputLevel > currentInputLevel
                ? LevelAttackSpeed
                : LevelReleaseSpeed;
            currentInputLevel = Mathf.MoveTowards(
                currentInputLevel,
                targetInputLevel,
                levelSpeed * Time.unscaledDeltaTime);

            if (targetPeakLevel >= currentPeakLevel)
            {
                currentPeakLevel = targetPeakLevel;
                peakHoldUntil = Time.unscaledTime + PeakHoldSeconds;
            }
            else if (Time.unscaledTime >= peakHoldUntil)
            {
                currentPeakLevel = Mathf.MoveTowards(
                    currentPeakLevel,
                    Mathf.Max(targetPeakLevel, currentInputLevel),
                    PeakReleaseSpeed * Time.unscaledDeltaTime);
            }
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        private void UpdateNativeInputLevel()
        {
            AudioClip clip = nativeRecordingClip;
            if (clip == null)
            {
                SetRawInputLevels(0f, 0f);
                return;
            }

            string deviceName = string.IsNullOrWhiteSpace(microphoneDeviceName)
                ? null
                : microphoneDeviceName;
            int frameCount = LevelSampleFrameCount;
            int sampleCount = frameCount * clip.channels;
            if (nativeLevelSamples == null || nativeLevelSamples.Length != sampleCount)
            {
                nativeLevelSamples = new float[sampleCount];
            }

            try
            {
                int currentFrame = Microphone.GetPosition(deviceName);
                if (currentFrame <= 0)
                {
                    SetRawInputLevels(0f, 0f);
                    return;
                }

                int offsetFrame = (currentFrame - frameCount + clip.samples) % clip.samples;
                if (!clip.GetData(nativeLevelSamples, offsetFrame))
                {
                    SetRawInputLevels(0f, 0f);
                    return;
                }

                double squareSum = 0d;
                float peak = 0f;
                for (int i = 0; i < nativeLevelSamples.Length; i++)
                {
                    float absolute = Mathf.Abs(nativeLevelSamples[i]);
                    squareSum += absolute * absolute;
                    peak = Mathf.Max(peak, absolute);
                }

                float rms = nativeLevelSamples.Length > 0
                    ? Mathf.Sqrt((float)(squareSum / nativeLevelSamples.Length))
                    : 0f;
                SetRawInputLevels(rms, peak);
            }
            catch (Exception)
            {
                SetRawInputLevels(0f, 0f);
            }
        }

        private void UpdateNativeAudioStream()
        {
            int generation = recordingSession.ActiveGeneration;
            AudioClip clip = nativeRecordingClip;
            Pcm16RecordingFile recordingFile = nativeRecordingFile;
            NativeMicrophoneRingCursor ringCursor = nativeRingCursor;
            if (!IsNativeCaptureCurrent(
                    generation,
                    clip,
                    recordingFile,
                    ringCursor))
            {
                return;
            }

            string deviceName = string.IsNullOrWhiteSpace(microphoneDeviceName)
                ? null
                : microphoneDeviceName;
            try
            {
                int framesPerChunk = GetNativeStreamFramesPerChunk(clip.frequency);
                while (true)
                {
                    if (!IsNativeCaptureCurrent(
                            generation,
                            clip,
                            recordingFile,
                            ringCursor))
                    {
                        return;
                    }

                    if (!Microphone.IsRecording(deviceName))
                    {
                        throw new InvalidOperationException(
                            "The native microphone stopped before the recording was requested to end.");
                    }

                    int currentFrame = Microphone.GetPosition(deviceName);
                    if (currentFrame < 0)
                    {
                        throw new InvalidOperationException(
                            "Unity returned an invalid native microphone write position.");
                    }

                    int availableFrames = GetAvailableNativeRingFrames(
                        clip,
                        ringCursor,
                        currentFrame);
                    if (availableFrames < framesPerChunk)
                    {
                        return;
                    }

                    ringCursor.EnsureReadSafetyMargin(framesPerChunk);
                    EmitNativeAudioFrames(
                        clip,
                        recordingFile,
                        ringCursor,
                        framesPerChunk,
                        false);

                    // AudioChunkReceived is synchronous and may stop or destroy this capture.
                    if (!IsNativeCaptureCurrent(
                            generation,
                            clip,
                            recordingFile,
                            ringCursor))
                    {
                        return;
                    }
                }
            }
            catch (Exception exception)
            {
                if (!IsNativeCaptureCurrent(
                        generation,
                        clip,
                        recordingFile,
                        ringCursor))
                {
                    return;
                }

                FailNativeRecordingDuringCapture(
                    generation,
                    clip,
                    recordingFile,
                    ringCursor,
                    "Failed to read the native microphone ring buffer: " + exception.Message);
            }
        }

        private bool FlushNativeAudioStream(
            int generation,
            AudioClip clip,
            Pcm16RecordingFile recordingFile,
            NativeMicrophoneRingCursor ringCursor,
            int currentFrame)
        {
            if (!IsNativeFinalizationDrainCurrent(
                    generation,
                    clip,
                    recordingFile,
                    ringCursor))
            {
                return false;
            }

            int framesPerChunk = GetNativeStreamFramesPerChunk(clip.frequency);
            try
            {
                int availableFrames = GetAvailableNativeRingFrames(
                    clip,
                    ringCursor,
                    currentFrame);
                while (availableFrames > framesPerChunk)
                {
                    EmitNativeAudioFrames(
                        clip,
                        recordingFile,
                        ringCursor,
                        framesPerChunk,
                        false);
                    if (!IsNativeFinalizationDrainCurrent(
                            generation,
                            clip,
                            recordingFile,
                            ringCursor))
                    {
                        return false;
                    }

                    availableFrames = ringCursor.AvailableFrameCount;
                }

                if (availableFrames > 0)
                {
                    EmitNativeAudioFrames(
                        clip,
                        recordingFile,
                        ringCursor,
                        availableFrames,
                        true);
                }
                else
                {
                    PublishEmptyAudioStreamEndIfNeeded();
                }

                return IsNativeFinalizationDrainCurrent(
                    generation,
                    clip,
                    recordingFile,
                    ringCursor);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "Failed to finish the native microphone ring buffer: " + exception.Message,
                    exception);
            }
        }

        private void EmitNativeAudioFrames(
            AudioClip clip,
            Pcm16RecordingFile recordingFile,
            NativeMicrophoneRingCursor ringCursor,
            int frameCount,
            bool isLast)
        {
            float[] interleavedSamples = new float[checked(frameCount * clip.channels)];
            ReadNativeAudioFrames(clip, ringCursor, frameCount, interleavedSamples);
            ringCursor.Advance(frameCount);

            float[] normalizedSamples = Pcm16WavUtility.NormalizeToMono(
                interleavedSamples,
                frameCount,
                clip.channels,
                clip.frequency,
                targetSampleRate);
            byte[] pcmBytes = Pcm16WavUtility.EncodePcm16Data(normalizedSamples);
            long maximumOutputFrames = (long)maxDurationSeconds * targetSampleRate;
            long recordedOutputFrames = recordingFile.PcmDataByteCount / sizeof(short);
            int outputFrameCount = (int)Math.Min(
                normalizedSamples.Length,
                Math.Max(0L, maximumOutputFrames - recordedOutputFrames));
            int pcmByteCount = checked(outputFrameCount * sizeof(short));
            if (pcmByteCount > 0)
            {
                recordingFile.AppendPcm16(pcmBytes, 0, pcmByteCount);
            }

            if (isStreamingActive)
            {
                if (pcmByteCount <= 0)
                {
                    if (isLast)
                    {
                        PublishEmptyAudioStreamEndIfNeeded();
                    }

                    return;
                }

                byte[] publishedBytes = pcmBytes;
                if (pcmByteCount != pcmBytes.Length)
                {
                    publishedBytes = new byte[pcmByteCount];
                    Buffer.BlockCopy(pcmBytes, 0, publishedBytes, 0, pcmByteCount);
                }

                PublishNativeAudioStreamChunk(publishedBytes, outputFrameCount, isLast);
            }
        }

        private void ReadNativeAudioFrames(
            AudioClip clip,
            NativeMicrophoneRingCursor ringCursor,
            int frameCount,
            float[] destination)
        {
            int readFrame = ringCursor.ReadFrame;
            int firstFrameCount = Math.Min(frameCount, clip.samples - readFrame);
            if (firstFrameCount == frameCount)
            {
                if (!clip.GetData(destination, readFrame))
                {
                    throw new InvalidOperationException(
                        "Unity could not read the next microphone stream frames.");
                }

                return;
            }

            int channels = clip.channels;
            float[] tailSamples = new float[checked(firstFrameCount * channels)];
            if (!clip.GetData(tailSamples, readFrame))
            {
                throw new InvalidOperationException(
                    "Unity could not read the end of the microphone ring buffer.");
            }

            int secondFrameCount = frameCount - firstFrameCount;
            float[] headSamples = new float[checked(secondFrameCount * channels)];
            if (!clip.GetData(headSamples, 0))
            {
                throw new InvalidOperationException(
                    "Unity could not read the beginning of the microphone ring buffer.");
            }

            Array.Copy(tailSamples, 0, destination, 0, tailSamples.Length);
            Array.Copy(headSamples, 0, destination, tailSamples.Length, headSamples.Length);
        }

        private int GetAvailableNativeRingFrames(
            AudioClip clip,
            NativeMicrophoneRingCursor ringCursor,
            int currentFrame)
        {
            double ringDurationSeconds = clip.samples / (double)clip.frequency;
            return ringCursor.GetAvailableFrames(
                currentFrame,
                Time.realtimeSinceStartupAsDouble,
                ringDurationSeconds);
        }

        private bool IsNativeCaptureCurrent(
            int generation,
            AudioClip clip,
            Pcm16RecordingFile recordingFile,
            NativeMicrophoneRingCursor ringCursor)
        {
            return IsNativeCaptureLeaseCurrent(
                generation != 0 && recordingSession.IsCurrent(generation),
                isRecording,
                clip,
                recordingFile,
                ringCursor,
                nativeRecordingClip,
                nativeRecordingFile,
                nativeRingCursor);
        }

        internal static bool IsNativeCaptureLeaseCurrent(
            bool sessionIsCurrent,
            bool captureIsRecording,
            AudioClip expectedClip,
            Pcm16RecordingFile expectedRecordingFile,
            NativeMicrophoneRingCursor expectedRingCursor,
            AudioClip currentClip,
            Pcm16RecordingFile currentRecordingFile,
            NativeMicrophoneRingCursor currentRingCursor)
        {
            return sessionIsCurrent &&
                   captureIsRecording &&
                   expectedClip != null &&
                   expectedRecordingFile != null &&
                   expectedRingCursor != null &&
                   currentClip == expectedClip &&
                   currentRecordingFile == expectedRecordingFile &&
                   currentRingCursor == expectedRingCursor;
        }

        private bool IsNativeFinalizationDrainCurrent(
            int generation,
            AudioClip clip,
            Pcm16RecordingFile recordingFile,
            NativeMicrophoneRingCursor ringCursor)
        {
            return generation != 0 &&
                   clip != null &&
                   recordingFile != null &&
                   ringCursor != null &&
                   recordingSession.IsCurrent(generation) &&
                   recordingSession.IsFinalizing &&
                   nativeRecordingClip == clip &&
                   nativeRecordingFile == recordingFile &&
                   nativeRingCursor == ringCursor;
        }

        private void FailNativeRecordingDuringCapture(
            int generation,
            AudioClip clip,
            Pcm16RecordingFile recordingFile,
            NativeMicrophoneRingCursor ringCursor,
            string message)
        {
            if (!IsNativeCaptureCurrent(
                    generation,
                    clip,
                    recordingFile,
                    ringCursor))
            {
                return;
            }

            StopNativeMicrophoneWithoutSaving();
            FailRecording(generation, message);
        }

        private int GetNativeStreamFramesPerChunk(int sampleRate)
        {
            int chunkDurationMilliseconds = isStreamingActive
                ? streamChunkDurationMilliseconds
                : NativeFileChunkDurationMilliseconds;
            return Mathf.Max(
                1,
                Mathf.RoundToInt(sampleRate * chunkDurationMilliseconds / 1000f));
        }
#endif

        private void PrepareRecordingMetrics()
        {
            currentRecordingElapsedSeconds = 0f;
            recordingStartedAt = Time.realtimeSinceStartupAsDouble;
            ResetInputLevels();
        }

        private void PrepareAudioStreamSession()
        {
            streamRequestedForSession = streamingEnabled && AudioRecorder.HasAudioChunkSubscribers;
            isStreamingActive = false;
#if !UNITY_WEBGL || UNITY_EDITOR
            nativeRingCursor = null;
#endif
            streamSequence = 0;
            streamedOutputFrameCount = 0;
            streamChunkCount = 0;
            streamedPcmByteCount = 0;
            lastStreamChunk = null;
        }

        private void BeginAudioStream()
        {
            isStreamingActive = streamRequestedForSession;
        }

        private void PublishNativeAudioStreamChunk(byte[] data, int outputFrameCount, bool isLast)
        {
            int timestampMilliseconds = targetSampleRate > 0
                ? Mathf.RoundToInt(streamedOutputFrameCount * 1000f / targetSampleRate)
                : 0;
            AudioStreamChunk chunk = new AudioStreamChunk(
                data,
                streamSequence,
                timestampMilliseconds,
                targetSampleRate,
                1,
                16,
                streamSequence == 0,
                isLast);
            streamSequence++;
            streamedOutputFrameCount += outputFrameCount;
            PublishAudioStreamChunk(chunk);
        }

        private void PublishAudioStreamChunk(AudioStreamChunk chunk)
        {
            lastStreamChunk = chunk;
            streamChunkCount++;
            streamedPcmByteCount = checked(streamedPcmByteCount + chunk.Data.Length);
            if (chunk.IsLast)
            {
                isStreamingActive = false;
            }

            AudioRecorder.NotifyAudioChunk(chunk);
        }

        private void PublishEmptyAudioStreamEndIfNeeded()
        {
            if (!isStreamingActive)
            {
                return;
            }

            PublishNativeAudioStreamChunk(Array.Empty<byte>(), 0, true);
        }

        private void FailAudioStream(string message)
        {
            Debug.LogError(message, this);
            PublishEmptyAudioStreamEndIfNeeded();
            isStreamingActive = false;
        }

        private void ResetInputLevels()
        {
            currentInputLevel = 0f;
            currentPeakLevel = 0f;
            targetInputLevel = 0f;
            targetPeakLevel = 0f;
            peakHoldUntil = 0f;
        }

        private void SetRawInputLevels(float rms, float peak)
        {
            targetInputLevel = ConvertAmplitudeToNormalizedLevel(rms);
            targetPeakLevel = ConvertAmplitudeToNormalizedLevel(peak);
        }

        private static float ConvertAmplitudeToNormalizedLevel(float amplitude)
        {
            float decibels = 20f * Mathf.Log10(Mathf.Max(amplitude, 0.0001f));
            return Mathf.InverseLerp(MinimumLevelDecibels, 0f, decibels);
        }

        private void UpdatePlaybackMetrics()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            bool wasPlaying = isPlaybackActive;
            isPlaybackActive = CowartWebGLAudio_IsPlaying() != 0;
            currentPlaybackDurationSeconds = Mathf.Max(
                0f,
                CowartWebGLAudio_GetPlaybackDurationMilliseconds() / 1000f);
            currentPlaybackTimeSeconds = Mathf.Clamp(
                CowartWebGLAudio_GetPlaybackTimeMilliseconds() / 1000f,
                0f,
                currentPlaybackDurationSeconds);

            if (wasPlaying && !isPlaybackActive &&
                currentPlaybackDurationSeconds > 0f &&
                currentPlaybackTimeSeconds >= currentPlaybackDurationSeconds - 0.05f)
            {
                currentPlaybackTimeSeconds = currentPlaybackDurationSeconds;
            }
#else
            AudioClip clip = playbackSource != null ? playbackSource.clip : null;
            if (clip == null)
            {
                isPlaybackActive = false;
                currentPlaybackTimeSeconds = 0f;
                currentPlaybackDurationSeconds = 0f;
                return;
            }

            bool wasPlaying = isPlaybackActive;
            float durationSeconds = Mathf.Max(0f, clip.length);
            float sourceTimeSeconds = Mathf.Clamp(playbackSource.time, 0f, durationSeconds);
            isPlaybackActive = playbackSource.isPlaying;
            currentPlaybackDurationSeconds = durationSeconds;

            if (!isPlaybackActive &&
                durationSeconds > 0f &&
                sourceTimeSeconds <= 0.05f &&
                (wasPlaying || currentPlaybackTimeSeconds >= durationSeconds - 0.05f))
            {
                currentPlaybackTimeSeconds = durationSeconds;
            }
            else
            {
                currentPlaybackTimeSeconds = sourceTimeSeconds;
            }
#endif
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus && isRecording)
            {
                StopRecording();
            }
        }

        private void OnDestroy()
        {
            StopPlayback();

#if UNITY_WEBGL && !UNITY_EDITOR
            if (activeWebGLRecorder == this)
            {
                activeWebGLRecorder = null;
                CowartWebGLAudio_Clear();
            }
#else
#if UNITY_ANDROID && !UNITY_EDITOR
            ReleaseAndroidPermissionCallbacks();
#endif
            StopNativeMicrophoneWithoutSaving();
            nativeFinalizeTask = null;
            nativeFinalizeGeneration = 0;
            nativeFinalizeDurationSeconds = 0f;
#endif
            recordingSession.AbortActive();

            if (playbackClip != null)
            {
                Destroy(playbackClip);
                playbackClip = null;
            }

            if (instance == this)
            {
                instance = null;
            }
        }

        /// <summary>设置下次录音的时长上限，输入会被限制到 5–3600 秒。</summary>
        public void SetMaxDurationSeconds(int seconds)
        {
            if (IsRecording)
            {
                SetStatus("Stop the active microphone recording before changing its duration limit.");
                return;
            }

            maxDurationSeconds = Mathf.Clamp(
                seconds,
                MinimumDurationSeconds,
                MaximumDurationSeconds);
            currentRecordingElapsedSeconds = Mathf.Min(
                currentRecordingElapsedSeconds,
                maxDurationSeconds);
        }

        /// <summary>
        /// 供 Slider.onValueChanged(float) 动态绑定使用，最终按整秒保存。
        /// </summary>
        public void SetMaxDurationSecondsFromSlider(float seconds)
        {
            SetMaxDurationSeconds(Mathf.RoundToInt(seconds));
        }

        /// <summary>设置下一次录音是否允许输出流式 PCM 块。</summary>
        public void SetStreamingEnabled(bool enabled)
        {
            if (IsRecording)
            {
                SetStatus("Stop the active microphone recording before changing audio streaming.");
                return;
            }

            streamingEnabled = enabled;
        }

        /// <summary>设置下一次录音的流式块目标时长，输入会被限制到 20–1000 毫秒。</summary>
        public void SetStreamChunkDurationMilliseconds(int milliseconds)
        {
            if (IsRecording)
            {
                SetStatus("Stop the active microphone recording before changing the stream chunk duration.");
                return;
            }

            streamChunkDurationMilliseconds = Mathf.Clamp(
                milliseconds,
                MinimumStreamChunkMilliseconds,
                MaximumStreamChunkMilliseconds);
        }

        /// <summary>
        /// 开始录音。WebGL 中必须直接绑定到 Button.onClick，不能先等待协程或异步回调。
        /// Android APK 首次调用时会请求 RECORD_AUDIO 运行时权限。
        /// </summary>
        public bool StartRecording()
        {
            if (IsRecording || recordingSession.IsActive)
            {
                SetStatus("A microphone recording is already active or waiting for permission.");
                return false;
            }

            int generation = recordingSession.Begin();
            if (generation == 0)
            {
                return false;
            }

            isStartPending = true;
            StopPlayback();
            maxDurationSeconds = Mathf.Clamp(
                maxDurationSeconds,
                MinimumDurationSeconds,
                MaximumDurationSeconds);
            targetSampleRate = Mathf.Clamp(targetSampleRate, MinimumSampleRate, MaximumSampleRate);
            PrepareRecordingMetrics();
            PrepareAudioStreamSession();

#if UNITY_WEBGL && !UNITY_EDITOR
            EnsureWebGLCallbacks();
            if (CowartWebGLAudio_IsSupported() == 0)
            {
                FailRecording(
                    generation,
                    "Browser microphone recording requires getUserMedia in an HTTPS or localhost secure context.");
                return false;
            }

            if (activeWebGLRecorder != null && activeWebGLRecorder != this && activeWebGLRecorder.IsRecording)
            {
                FailRecording(
                    generation,
                    "Another audio recording session is already using the browser microphone.");
                return false;
            }

            activeWebGLRecorder = this;
            SetStatus("Waiting for browser microphone permission...");
            int started = CowartWebGLAudio_Start(
                targetSampleRate,
                checked(maxDurationSeconds * 1000),
                streamRequestedForSession ? streamChunkDurationMilliseconds : 0);
            if (started == 0 && recordingSession.IsCurrent(generation) && isStartPending)
            {
                FailRecording(generation, "The browser rejected the microphone recording request.");
            }
            return started != 0;
#else
            return RequestPermissionAndStartNativeRecording(generation);
#endif
        }

        /// <summary>停止录音并生成完整的 PCM16 WAV。</summary>
        public bool StopRecording()
        {
            if (!IsRecording)
            {
                SetStatus("No microphone recording is active.");
                return false;
            }

            if (recordingSession.IsFinalizing)
            {
                SetStatus("The active microphone recording is already being finalized.");
                return false;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            if (isStartPending && !isRecording)
            {
                int generation = recordingSession.ActiveGeneration;
                ReleaseAndroidPermissionCallbacks();
                CancelRecording(
                    generation,
                    "Android microphone recording was canceled while waiting for permission.");
                return true;
            }
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
            int generation = recordingSession.ActiveGeneration;
            if (isRecording)
            {
                BeginFinalizing(generation);
            }

            if (CowartWebGLAudio_Stop() == 0)
            {
                if (!recordingSession.IsCurrent(generation))
                {
                    return true;
                }

                FailRecording(generation, "The browser could not stop the active microphone recording.");
                return false;
            }
#else
            StopNativeRecordingAndCreateWav();
#endif
            return true;
        }

        /// <summary>
        /// 播放最近录音。WebGL 中应直接由按钮点击调用，以满足浏览器的有声播放策略。
        /// </summary>
        public bool PlayLastRecording()
        {
            if (!HasRecording)
            {
                SetStatus("No completed recording is available for playback.");
                return false;
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            if (CowartWebGLAudio_Play() == 0)
            {
                SetStatus("The browser could not play the last recording.");
                return false;
            }
#else
            if (playbackSource == null || playbackClip == null)
            {
                SetStatus("The recorded AudioClip is not available for playback.");
                return false;
            }

            playbackSource.clip = playbackClip;
            playbackSource.Play();
#endif
            currentPlaybackTimeSeconds = 0f;
            isPlaybackActive = true;
            UpdatePlaybackMetrics();
            SetStatus("Recording playback started.");
            return true;
        }

        /// <summary>停止最近录音的播放。</summary>
        public void StopPlayback()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            CowartWebGLAudio_StopPlayback();
#else
            if (playbackSource != null)
            {
                playbackSource.Stop();
            }
#endif
            isPlaybackActive = false;
            currentPlaybackTimeSeconds = 0f;
        }

        /// <summary>释放最近录音的 WAV 字节和播放资源。</summary>
        public void ClearRecording()
        {
            if (IsRecording)
            {
                SetStatus("Stop the active microphone recording before clearing it.");
                return;
            }

            StopPlayback();
            lastRecordingBytes = null;
            LastRecordingFileName = null;
            LastRecordingDurationSeconds = 0f;
            currentRecordingElapsedSeconds = 0f;
            currentPlaybackTimeSeconds = 0f;
            currentPlaybackDurationSeconds = 0f;
            isPlaybackActive = false;
            streamChunkCount = 0;
            streamedPcmByteCount = 0;
            lastStreamChunk = null;
            ResetInputLevels();

            if (playbackClip != null)
            {
                Destroy(playbackClip);
                playbackClip = null;
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            CowartWebGLAudio_Clear();
#endif
            SetStatus("The last microphone recording was cleared.");
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        private bool RequestPermissionAndStartNativeRecording(int generation)
        {
            if (!recordingSession.IsCurrent(generation))
            {
                return false;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                isStartPending = true;
                androidPermissionGeneration = generation;
                SetStatus("Waiting for Android microphone permission...");
                androidPermissionCallbacks = new PermissionCallbacks();
                androidPermissionCallbacks.PermissionGranted += HandleAndroidPermissionGranted;
                androidPermissionCallbacks.PermissionDenied += HandleAndroidPermissionDenied;
                androidPermissionCallbacks.PermissionDeniedAndDontAskAgain += HandleAndroidPermissionDenied;
                Permission.RequestUserPermission(Permission.Microphone, androidPermissionCallbacks);
                return true;
            }
#endif
            return StartNativeRecording(generation);
        }

        private bool StartNativeRecording(int generation)
        {
            if (!recordingSession.IsCurrent(generation))
            {
                return false;
            }

            try
            {
                if (Microphone.devices == null || Microphone.devices.Length == 0)
                {
                    FailRecording(generation, "No microphone device is available.");
                    return false;
                }

                string deviceName = string.IsNullOrWhiteSpace(microphoneDeviceName)
                    ? null
                    : microphoneDeviceName;
                nativeRecordingClip = Microphone.Start(
                    deviceName,
                    true,
                    NativeMicrophoneRingBufferSeconds,
                    targetSampleRate);

                if (nativeRecordingClip == null)
                {
                    FailRecording(generation, "Unity Microphone.Start did not create an AudioClip.");
                    return false;
                }

                nativeRecordingFile = Pcm16RecordingFile.Create(
                    Application.temporaryCachePath,
                    targetSampleRate);
                nativeRingCursor = new NativeMicrophoneRingCursor(nativeRecordingClip.samples);

                if (!recordingSession.IsCurrent(generation))
                {
                    StopNativeMicrophoneWithoutSaving();
                    return false;
                }

                recordingStartedAt = Time.realtimeSinceStartupAsDouble;
                nativeRingCursor.Reset(recordingStartedAt);
                currentRecordingElapsedSeconds = 0f;
                isStartPending = false;
                isRecording = true;
                BeginAudioStream();
                SetStatus("Microphone recording started.");
                AudioRecorder.NotifyRecordingStarted();
                return true;
            }
            catch (Exception exception)
            {
                StopNativeMicrophoneWithoutSaving();
                FailRecording(generation, "Failed to start the microphone: " + exception.Message);
                return false;
            }
        }

        private void StopNativeRecordingAndCreateWav()
        {
            int generation = recordingSession.ActiveGeneration;
            AudioClip sourceClip = nativeRecordingClip;
            Pcm16RecordingFile recordingFile = nativeRecordingFile;
            NativeMicrophoneRingCursor ringCursor = nativeRingCursor;
            if (!BeginFinalizing(generation))
            {
                return;
            }

            if (sourceClip == null || recordingFile == null || ringCursor == null)
            {
                StopNativeMicrophoneWithoutSaving();
                FailRecording(generation, "The native microphone AudioClip is missing.");
                return;
            }

            string deviceName = string.IsNullOrWhiteSpace(microphoneDeviceName)
                ? null
                : microphoneDeviceName;
            string failureMessage = null;
            try
            {
                int currentFrame = Microphone.GetPosition(deviceName);
                if (currentFrame < 0)
                {
                    throw new InvalidOperationException(
                        "Unity returned an invalid native microphone write position while stopping.");
                }

                // Freeze the producer before draining the remaining ring-buffer frames.
                Microphone.End(deviceName);
                if (!FlushNativeAudioStream(
                        generation,
                        sourceClip,
                        recordingFile,
                        ringCursor,
                        currentFrame))
                {
                    return;
                }

                if (recordingFile.PcmDataByteCount <= 0)
                {
                    throw new InvalidOperationException(
                        "The microphone stopped before any audio samples were captured.");
                }

                float durationSeconds = (float)recordingFile.DurationSeconds;
                Pcm16RecordingFile finalizingFile = recordingFile;
                Task<byte[]> finalizationTask = Task.Run(
                    () => finalizingFile.CompleteAndReadAllBytes());
                if (nativeRecordingFile == recordingFile)
                {
                    nativeRecordingFile = null;
                }

                if (nativeRingCursor == ringCursor)
                {
                    nativeRingCursor = null;
                }

                nativeFinalizeGeneration = generation;
                nativeFinalizeDurationSeconds = durationSeconds;
                nativeFinalizeTask = finalizationTask;
                recordingFile = null;
            }
            catch (Exception exception)
            {
                failureMessage =
                    "Failed to stop the microphone and create the WAV recording: " + exception.Message;
            }
            finally
            {
                TryEndNativeMicrophone(deviceName);
                if (recordingSession.IsCurrent(generation) && nativeRecordingClip == sourceClip)
                {
                    nativeRecordingClip = null;
                }

                if (recordingFile != null)
                {
                    if (nativeRecordingFile == recordingFile)
                    {
                        nativeRecordingFile = null;
                    }

                    recordingFile.Dispose();
                }

                if (nativeRingCursor == ringCursor)
                {
                    nativeRingCursor = null;
                }

                Destroy(sourceClip);
            }

            if (!recordingSession.IsCurrent(generation))
            {
                return;
            }

            if (!string.IsNullOrEmpty(failureMessage))
            {
                FailRecording(generation, failureMessage);
            }
        }

        private void UpdateNativeFinalization()
        {
            Task<byte[]> task = nativeFinalizeTask;
            if (task == null || !task.IsCompleted)
            {
                return;
            }

            int generation = nativeFinalizeGeneration;
            float durationSeconds = nativeFinalizeDurationSeconds;
            nativeFinalizeTask = null;
            nativeFinalizeGeneration = 0;
            nativeFinalizeDurationSeconds = 0f;
            if (!recordingSession.IsCurrent(generation))
            {
                return;
            }

            byte[] wavBytes;
            Pcm16WavData wavData;
            try
            {
                wavBytes = task.GetAwaiter().GetResult();
                wavData = Pcm16WavData.Parse(wavBytes);
            }
            catch (Exception exception)
            {
                FailRecording(
                    generation,
                    "Failed to finalize the native WAV recording: " + exception.Message);
                return;
            }

            AudioClip createdPlaybackClip = TryCreateNativePlaybackClip(
                wavData,
                CreateNativePlaybackClip,
                message => Debug.LogWarning(message, this));
            if (!recordingSession.IsCurrent(generation))
            {
                if (createdPlaybackClip != null)
                {
                    Destroy(createdPlaybackClip);
                }

                return;
            }

            AudioClip previousPlaybackClip = playbackClip;
            playbackClip = createdPlaybackClip;
            if (playbackSource != null && playbackSource.clip == previousPlaybackClip)
            {
                playbackSource.clip = null;
            }

            if (previousPlaybackClip != null)
            {
                Destroy(previousPlaybackClip);
            }

            CompleteRecording(generation, wavBytes, durationSeconds);
        }

        internal static AudioClip TryCreateNativePlaybackClip(
            Pcm16WavData wavData,
            Func<Pcm16WavData, AudioClip> createClip,
            Action<string> reportWarning)
        {
            if (wavData == null)
            {
                throw new ArgumentNullException(nameof(wavData));
            }

            if (createClip == null)
            {
                throw new ArgumentNullException(nameof(createClip));
            }

            try
            {
                AudioClip clip = createClip(wavData);
                if (clip == null)
                {
                    throw new InvalidOperationException(
                        "Unity could not create the streaming playback AudioClip.");
                }

                return clip;
            }
            catch (Exception exception)
            {
                reportWarning?.Invoke(
                    "The WAV recording completed, but built-in playback is unavailable: " +
                    exception.Message);
                return null;
            }
        }

        private static AudioClip CreateNativePlaybackClip(Pcm16WavData wavData)
        {
            Pcm16WavClipReader reader = new Pcm16WavClipReader(wavData);
            return AudioClip.Create(
                "Microphone Recording",
                wavData.FrameCount,
                wavData.Channels,
                wavData.SampleRate,
                true,
                reader.Read,
                reader.SetPosition);
        }

        private void StopNativeMicrophoneWithoutSaving()
        {
            AudioClip clip = nativeRecordingClip;
            if (clip != null)
            {
                string deviceName = string.IsNullOrWhiteSpace(microphoneDeviceName)
                    ? null
                    : microphoneDeviceName;
                TryEndNativeMicrophone(deviceName);
                Destroy(clip);
                nativeRecordingClip = null;
            }

            AbortNativeRecordingFile();

            isRecording = false;
            isStartPending = false;
            isStreamingActive = false;
            streamRequestedForSession = false;
            targetInputLevel = 0f;
            targetPeakLevel = 0f;
        }

        private void AbortNativeRecordingFile()
        {
            Pcm16RecordingFile recordingFile = nativeRecordingFile;
            nativeRecordingFile = null;
            nativeRingCursor = null;
            recordingFile?.Dispose();
        }

        private void TryEndNativeMicrophone(string deviceName)
        {
            try
            {
                if (Microphone.IsRecording(deviceName))
                {
                    Microphone.End(deviceName);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "Failed to release the native microphone cleanly: " + exception.Message,
                    this);
            }
        }
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        private void HandleAndroidPermissionGranted(string permissionName)
        {
            int generation = androidPermissionGeneration;
            ReleaseAndroidPermissionCallbacks();
            if (!recordingSession.IsCurrent(generation) || !isStartPending)
            {
                return;
            }

            isStartPending = false;
            StartNativeRecording(generation);
        }

        private void HandleAndroidPermissionDenied(string permissionName)
        {
            int generation = androidPermissionGeneration;
            ReleaseAndroidPermissionCallbacks();
            if (!recordingSession.IsCurrent(generation) || !isStartPending)
            {
                return;
            }

            isStartPending = false;
            FailRecording(
                generation,
                "Android microphone permission was denied. Enable it in the app settings before recording.");
        }

        private void ReleaseAndroidPermissionCallbacks()
        {
            if (androidPermissionCallbacks == null)
            {
                androidPermissionGeneration = 0;
                return;
            }

            androidPermissionCallbacks.PermissionGranted -= HandleAndroidPermissionGranted;
            androidPermissionCallbacks.PermissionDenied -= HandleAndroidPermissionDenied;
            androidPermissionCallbacks.PermissionDeniedAndDontAskAgain -= HandleAndroidPermissionDenied;
            androidPermissionCallbacks = null;
            androidPermissionGeneration = 0;
        }
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
        private static void EnsureWebGLCallbacks()
        {
            if (webGLCallbacksRegistered)
            {
                return;
            }

            CowartWebGLAudio_RegisterCallbacks(
                BrowserStateCallback,
                BrowserDataCallback,
                BrowserLevelCallback,
                BrowserStreamCallback);
            webGLCallbacksRegistered = true;
            Application.quitting -= ShutdownWebGL;
            Application.quitting += ShutdownWebGL;
        }

        private static void ShutdownWebGL()
        {
            CowartWebGLAudio_Clear();
            CowartWebGLAudio_UnregisterCallbacks();
            activeWebGLRecorder = null;
            webGLCallbacksRegistered = false;
            Application.quitting -= ShutdownWebGL;
        }

        [MonoPInvokeCallback(typeof(WebGLStateCallback))]
        [Preserve]
        private static void OnWebGLStateChanged(int state, IntPtr messageBytes, int messageLength)
        {
            AudioRecorderDriver recorder = activeWebGLRecorder;
            if (recorder == null)
            {
                return;
            }

            string message = messageLength > 0
                ? Marshal.PtrToStringUTF8(messageBytes, messageLength)
                : string.Empty;
            int generation = recorder.recordingSession.ActiveGeneration;

            switch (state)
            {
                case WebGLStateStarted:
                    if (!recorder.recordingSession.IsCurrent(generation) ||
                        recorder.recordingSession.IsFinalizing)
                    {
                        return;
                    }

                    recorder.isStartPending = false;
                    recorder.isRecording = true;
                    recorder.recordingStartedAt = Time.realtimeSinceStartup;
                    recorder.currentRecordingElapsedSeconds = 0f;
                    recorder.BeginAudioStream();
                    recorder.SetStatus("Browser microphone recording started.");
                    AudioRecorder.NotifyRecordingStarted();
                    break;
                case WebGLStateFailed:
                    recorder.FailRecording(
                        generation,
                        string.IsNullOrEmpty(message)
                            ? "Browser microphone recording failed."
                            : message);
                    break;
                case WebGLStateCanceled:
                    recorder.CancelRecording(
                        generation,
                        "Browser microphone recording was canceled before audio was captured.");
                    break;
                case WebGLStateFinalizing:
                    recorder.BeginFinalizing(generation);
                    break;
            }
        }

        [MonoPInvokeCallback(typeof(WebGLLevelCallback))]
        [Preserve]
        private static void OnWebGLLevelChanged(float rms, float peak)
        {
            AudioRecorderDriver recorder = activeWebGLRecorder;
            if (recorder == null || !recorder.isRecording)
            {
                return;
            }

            recorder.SetRawInputLevels(rms, peak);
        }

        [MonoPInvokeCallback(typeof(WebGLStreamCallback))]
        [Preserve]
        private static void OnWebGLStreamChunkReceived(
            IntPtr dataBytes,
            int dataLength,
            int sequence,
            int timestampMilliseconds,
            int isFirst,
            int isLast)
        {
            AudioRecorderDriver recorder = activeWebGLRecorder;
            if (recorder == null || !recorder.isStreamingActive || dataLength < 0)
            {
                return;
            }

            if (isLast != 0 &&
                !recorder.BeginFinalizing(recorder.recordingSession.ActiveGeneration))
            {
                return;
            }

            byte[] pcmBytes = dataLength > 0 ? new byte[dataLength] : Array.Empty<byte>();
            if (dataLength > 0)
            {
                if (dataBytes == IntPtr.Zero)
                {
                    recorder.FailAudioStream("The browser returned an invalid streaming PCM block.");
                    return;
                }

                Marshal.Copy(dataBytes, pcmBytes, 0, dataLength);
            }

            recorder.streamSequence = Mathf.Max(recorder.streamSequence, sequence + 1);
            recorder.streamedOutputFrameCount = Mathf.Max(
                recorder.streamedOutputFrameCount,
                (int)((long)timestampMilliseconds * recorder.targetSampleRate / 1000) + dataLength / 2);
            recorder.PublishAudioStreamChunk(new AudioStreamChunk(
                pcmBytes,
                sequence,
                timestampMilliseconds,
                recorder.targetSampleRate,
                1,
                16,
                isFirst != 0,
                isLast != 0));
        }

        [MonoPInvokeCallback(typeof(WebGLDataCallback))]
        [Preserve]
        private static void OnWebGLDataReceived(
            IntPtr dataBytes,
            int dataLength,
            int sampleRate,
            int channels,
            int durationMilliseconds)
        {
            AudioRecorderDriver recorder = activeWebGLRecorder;
            if (recorder == null)
            {
                return;
            }

            int generation = recorder.recordingSession.ActiveGeneration;
            if (!recorder.BeginFinalizing(generation))
            {
                return;
            }

            if (dataBytes == IntPtr.Zero || dataLength <= 44 || sampleRate <= 0 || channels != 1)
            {
                recorder.FailRecording(
                    generation,
                    "The browser returned an invalid PCM16 WAV recording.");
                return;
            }

            byte[] wavBytes = new byte[dataLength];
            Marshal.Copy(dataBytes, wavBytes, 0, dataLength);
            recorder.CompleteRecording(
                generation,
                wavBytes,
                durationMilliseconds / 1000f);
        }
#endif

        private bool BeginFinalizing(int generation)
        {
            if (!recordingSession.TryBeginFinalizing(generation))
            {
                return false;
            }

            isRecording = false;
            isStartPending = false;
            return true;
        }

        private void CompleteRecording(int generation, byte[] wavBytes, float durationSeconds)
        {
            if (!BeginFinalizing(generation))
            {
                return;
            }

            PublishEmptyAudioStreamEndIfNeeded();
            streamRequestedForSession = false;
            isRecording = false;
            isStartPending = false;
            lastRecordingBytes = wavBytes;
            LastRecordingDurationSeconds = durationSeconds;
            currentRecordingElapsedSeconds = Mathf.Clamp(
                durationSeconds,
                0f,
                maxDurationSeconds);
            targetInputLevel = 0f;
            targetPeakLevel = 0f;
            LastRecordingFileName = "recording_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".wav";
            SetStatus(string.Format(
                "Microphone recording completed: {0:F2} seconds, {1:N0} bytes.",
                LastRecordingDurationSeconds,
                lastRecordingBytes.Length));
            if (!CommitTerminal(generation))
            {
                return;
            }

            AudioRecorder.NotifyRecordingCompleted(
                lastRecordingBytes,
                LastRecordingFileName,
                LastRecordingDurationSeconds);
        }

        private void CancelRecording(int generation, string message)
        {
            if (!BeginFinalizing(generation))
            {
                return;
            }

            PublishEmptyAudioStreamEndIfNeeded();
            isStreamingActive = false;
            streamRequestedForSession = false;
            ResetInputLevels();
            SetStatus(message);
            if (!CommitTerminal(generation))
            {
                return;
            }

            AudioRecorder.NotifyRecordingCanceled();
        }

        private void FailRecording(int generation, string message)
        {
            if (!BeginFinalizing(generation))
            {
                return;
            }

            PublishEmptyAudioStreamEndIfNeeded();
            isRecording = false;
            isStartPending = false;
            isStreamingActive = false;
            streamRequestedForSession = false;
            targetInputLevel = 0f;
            targetPeakLevel = 0f;
            Debug.LogError(message, this);
            if (!CommitTerminal(generation))
            {
                return;
            }

            AudioRecorder.NotifyRecordingFailed(message);
        }

        private bool CommitTerminal(int generation)
        {
            if (!recordingSession.TryPublishTerminal(generation))
            {
                return false;
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            if (activeWebGLRecorder == this)
            {
                activeWebGLRecorder = null;
            }
#endif
            return true;
        }

        private void SetStatus(string message)
        {
            Debug.Log(message, this);
        }
    }
}
