using System;
using Cowart.AudioRecorder;
using Recorder = Cowart.AudioRecorder.AudioRecorder;
using UnityEngine;
using UnityEngine.Events;

namespace Cowart.WebGLBridge
{
    /// <summary>
    /// 仅用于尚未迁移的旧场景序列化引用。新代码只能使用
    /// <see cref="Cowart.AudioRecorder.AudioRecorder"/> 静态门面。
    /// </summary>
    [Obsolete("Use Cowart.AudioRecorder.AudioRecorder instead.", false)]
    [RequireComponent(typeof(AudioSource))]
    internal sealed class CrossPlatformAudioRecorder : MonoBehaviour
    {
        public const int MinimumDurationSeconds = Recorder.MinimumDurationSeconds;
        public const int DefaultMaximumDurationSeconds = Recorder.DefaultMaximumDurationSeconds;
        public const int MaximumDurationSeconds = Recorder.MaximumDurationSeconds;
        public const int MinimumStreamChunkMilliseconds = Recorder.MinimumStreamChunkMilliseconds;
        public const int MaximumStreamChunkMilliseconds = Recorder.MaximumStreamChunkMilliseconds;

        [SerializeField, Range(MinimumDurationSeconds, MaximumDurationSeconds)]
        private int maxDurationSeconds = DefaultMaximumDurationSeconds;

        [SerializeField, Range(8000, 48000)]
        private int targetSampleRate = Recorder.OutputSampleRate;

        [SerializeField]
        private string microphoneDeviceName = string.Empty;

        [SerializeField]
        private bool streamingEnabled = true;

        [SerializeField, Range(MinimumStreamChunkMilliseconds, MaximumStreamChunkMilliseconds)]
        private int streamChunkDurationMilliseconds = Recorder.DefaultStreamChunkMilliseconds;

        [SerializeField]
        private AudioSource playbackSource;

        [SerializeField]
        private UnityEvent onRecordingStarted = new UnityEvent();

        [SerializeField]
        private UnityEvent onRecordingCompleted = new UnityEvent();

        [SerializeField]
        private UnityEvent<string> onRecordingFailed = new UnityEvent<string>();

        [SerializeField]
        private UnityEvent<string> onStatusChanged = new UnityEvent<string>();

        public bool IsRecording => Recorder.IsRecording;
        public bool IsFinalizing => Recorder.IsFinalizing;
        public bool IsRecordingActive => Recorder.IsRecordingActive;
        public bool IsStartPending => Recorder.IsStartPending;
        public bool HasRecording => Recorder.HasRecording;
        public bool IsPlaybackActive => Recorder.IsPlaybackActive;
        public bool IsStreamingActive => Recorder.IsStreamingActive;
        public int MaxDurationSeconds => maxDurationSeconds;
        public int TargetSampleRate => targetSampleRate;
        public int StreamChunkDurationMilliseconds => streamChunkDurationMilliseconds;
        public int StreamChunkCount => Recorder.StreamChunkCount;
        public int StreamedPcmByteCount => Recorder.StreamedPcmByteCount;
        public float RecordingElapsedSeconds => Recorder.RecordingElapsedSeconds;
        public float RecordingRemainingSeconds => Recorder.RecordingRemainingSeconds;
        public float RecordingProgress => Recorder.RecordingProgress;
        public float CurrentInputLevel => Recorder.CurrentInputLevel;
        public float CurrentPeakLevel => Recorder.CurrentPeakLevel;
        public float PlaybackElapsedSeconds => Recorder.PlaybackElapsedSeconds;
        public float PlaybackDurationSeconds => Recorder.PlaybackDurationSeconds;
        public float PlaybackRemainingSeconds => Recorder.PlaybackRemainingSeconds;
        public float PlaybackProgress => Recorder.PlaybackProgress;
        public byte[] LastRecordingBytes => Recorder.LastRecording?.Data;
        public string LastRecordingFileName => Recorder.LastRecording?.Name;
        public float LastRecordingDurationSeconds => Recorder.LastRecording?.DurationSeconds ?? 0f;
        public AudioStreamChunk LastStreamChunk => Recorder.LastStreamChunk;

        public event Action<AudioStreamChunk> AudioChunkReceived
        {
            add => Recorder.AudioChunkReceived += value;
            remove => Recorder.AudioChunkReceived -= value;
        }

        private void OnEnable()
        {
            Recorder.RecordingStarted += HandleRecordingStarted;
            Recorder.RecordingCompleted += HandleRecordingCompleted;
            Recorder.RecordingCanceled += HandleRecordingCanceled;
            Recorder.RecordingFailed += HandleRecordingFailed;
        }

        private void OnDisable()
        {
            Recorder.RecordingStarted -= HandleRecordingStarted;
            Recorder.RecordingCompleted -= HandleRecordingCompleted;
            Recorder.RecordingCanceled -= HandleRecordingCanceled;
            Recorder.RecordingFailed -= HandleRecordingFailed;
        }

        public void SetMaxDurationSeconds(int seconds)
        {
            if (Recorder.IsRecording)
            {
                ReportStatus("Stop the active recording before changing its duration limit.");
                return;
            }

            maxDurationSeconds = Mathf.Clamp(seconds, MinimumDurationSeconds, MaximumDurationSeconds);
        }

        public void SetMaxDurationSecondsFromSlider(float seconds)
        {
            SetMaxDurationSeconds(Mathf.RoundToInt(seconds));
        }

        public void SetStreamingEnabled(bool enabled)
        {
            if (!Recorder.IsRecording)
            {
                streamingEnabled = enabled;
            }
        }

        public void SetStreamChunkDurationMilliseconds(int milliseconds)
        {
            if (!Recorder.IsRecording)
            {
                streamChunkDurationMilliseconds = Mathf.Clamp(
                    milliseconds,
                    MinimumStreamChunkMilliseconds,
                    MaximumStreamChunkMilliseconds);
            }
        }

        public void StartRecording()
        {
            bool accepted = Recorder.StartRecording(
                maxDurationSeconds,
                streamingEnabled,
                streamChunkDurationMilliseconds);
            if (!accepted)
            {
                ReportStatus("The microphone recording request was not accepted.");
            }
        }

        public void StopRecording()
        {
            if (!Recorder.StopRecording())
            {
                ReportStatus("No microphone recording is active.");
            }
        }

        public void PlayLastRecording()
        {
            if (!Recorder.PlayLastRecording())
            {
                ReportStatus("No completed recording is available for playback.");
            }
        }

        public void StopPlayback()
        {
            Recorder.StopPlayback();
        }

        public void DownloadLastRecording()
        {
            if (!TrySaveLastRecordingWithOptionalFileBridge())
            {
                ReportStatus(
                    "The recording save request was not accepted. Install File Bridge and use its SaveFile API.");
            }
        }

        public void ClearRecording()
        {
            if (Recorder.ClearRecording())
            {
                ReportStatus("The last microphone recording was cleared.");
            }
        }

        private static bool TrySaveLastRecordingWithOptionalFileBridge()
        {
            RecordedAudio recording = Recorder.LastRecording;
            if (recording == null)
            {
                return false;
            }

            Type bridgeType = Type.GetType("Cowart.FileBridge.FileBridge, Cowart.FileBridge", false);
            if (bridgeType == null)
            {
                return false;
            }

            System.Reflection.MethodInfo saveMethod = bridgeType.GetMethod(
                "SaveFile",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                null,
                new[] { typeof(byte[]), typeof(string), typeof(string) },
                null);
            if (saveMethod == null)
            {
                return false;
            }

            try
            {
                return (bool)saveMethod.Invoke(
                    null,
                    new object[] { recording.Data, recording.Name, recording.MimeType });
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return false;
            }
        }

        private void HandleRecordingStarted()
        {
            ReportStatus("Microphone recording started.");
            onRecordingStarted.Invoke();
        }

        private void HandleRecordingCompleted(RecordedAudio recording)
        {
            ReportStatus(string.Format(
                "Microphone recording completed: {0:F2} seconds, {1:N0} bytes.",
                recording.DurationSeconds,
                recording.Size));
            onRecordingCompleted.Invoke();
        }

        private void HandleRecordingCanceled()
        {
            ReportStatus("Microphone recording was canceled.");
        }

        private void HandleRecordingFailed(string message)
        {
            ReportStatus(message);
            onRecordingFailed.Invoke(message);
        }

        private void ReportStatus(string message)
        {
            onStatusChanged.Invoke(message ?? string.Empty);
        }
    }
}
