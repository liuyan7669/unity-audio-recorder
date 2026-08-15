using Cowart.AudioRecorder;
using Recorder = Cowart.AudioRecorder.AudioRecorder;
using UnityEngine;
using UnityEngine.UI;

namespace Cowart.AudioRecorder.Samples
{
    /// <summary>
    /// 可通过 Package Manager 导入的纯录音 UGUI 示例。
    /// 业务入口始终是 AudioRecorder 静态门面，不依赖 File Bridge 或语音识别包。
    /// </summary>
    public sealed class AudioRecorderUGUIUsage : MonoBehaviour
    {
        [Header("Buttons")]
        [SerializeField] private Button startRecordingButton;
        [SerializeField] private Button stopRecordingButton;
        [SerializeField] private Button playRecordingButton;
        [SerializeField] private Button stopPlaybackButton;
        [SerializeField] private Button inspectWavButton;
        [SerializeField] private Button clearRecordingButton;
        [SerializeField] private Button fiveMinuteButton;
        [SerializeField] private Button thirtyMinuteButton;
        [SerializeField] private Button sixtyMinuteButton;

        [Header("Recording Settings")]
        [SerializeField] private Slider recordingDurationSlider;
        [SerializeField] private Text recordingDurationText;
        [SerializeField] private Toggle streamAudioToggle;

        [Header("Recording State")]
        [SerializeField] private Slider inputLevelSlider;
        [SerializeField] private Image inputLevelFill;
        [SerializeField] private Slider recordingProgressSlider;
        [SerializeField] private Text recordingElapsedText;
        [SerializeField] private Text recordingRemainingText;
        [SerializeField] private Text statusText;
        [SerializeField] private Image recordingIndicator;
        [SerializeField] private Text recordingIndicatorText;
        [SerializeField] private Text resultText;
        [SerializeField] private Text streamText;

        private int selectedMaximumSeconds = Recorder.DefaultMaximumDurationSeconds;
        private string currentStatus = string.Empty;

        private void Awake()
        {
            BindControls();

            if (recordingDurationSlider != null)
            {
                recordingDurationSlider.minValue = Recorder.MinimumDurationSeconds;
                recordingDurationSlider.maxValue = Recorder.MaximumDurationSeconds;
                recordingDurationSlider.wholeNumbers = true;
                selectedMaximumSeconds = Mathf.Clamp(
                    Mathf.RoundToInt(recordingDurationSlider.value),
                    Recorder.MinimumDurationSeconds,
                    Recorder.MaximumDurationSeconds);
                recordingDurationSlider.SetValueWithoutNotify(selectedMaximumSeconds);
            }
        }

        private void OnEnable()
        {
            Recorder.RecordingStarted += HandleRecordingStarted;
            Recorder.RecordingCompleted += HandleRecordingCompleted;
            Recorder.RecordingCanceled += HandleRecordingCanceled;
            Recorder.RecordingFailed += HandleRecordingFailed;
            Recorder.AudioChunkReceived += HandleAudioChunk;

            SetStatus(Recorder.IsAvailable
                ? "Ready. Choose a duration and start recording."
                : "Audio recording is unavailable on the current platform.");
            UpdateRecordingUI();
        }

        private void OnDisable()
        {
            Recorder.RecordingStarted -= HandleRecordingStarted;
            Recorder.RecordingCompleted -= HandleRecordingCompleted;
            Recorder.RecordingCanceled -= HandleRecordingCanceled;
            Recorder.RecordingFailed -= HandleRecordingFailed;
            Recorder.AudioChunkReceived -= HandleAudioChunk;
        }

        private void OnDestroy()
        {
            UnbindControls();
        }

        private void Update()
        {
            UpdateRecordingUI();
        }

        public void StartRecording()
        {
            const string startingStatus = "Starting the recording request...";
            SetStatus(startingStatus);
            SetStreamText(streamAudioToggle != null && streamAudioToggle.isOn
                ? "Waiting for realtime PCM16 chunks..."
                : "Realtime PCM16 output is disabled for this session.");

            bool accepted = Recorder.StartRecording(
                selectedMaximumSeconds,
                streamAudioToggle != null && streamAudioToggle.isOn,
                Recorder.DefaultStreamChunkMilliseconds);
            if (!accepted && currentStatus == startingStatus)
            {
                SetStatus("The recording request was not accepted. Check platform and session state.");
            }
            else if (accepted && Recorder.IsStartPending && currentStatus == startingStatus)
            {
                SetStatus("Waiting for microphone permission...");
            }
        }

        public void StopRecording()
        {
            if (Recorder.IsFinalizing)
            {
                SetStatus("Capture has stopped. The final WAV is being generated...");
                return;
            }

            if (!Recorder.StopRecording())
            {
                SetStatus("There is no recording request that can be stopped.");
            }
        }

        public void PlayLastRecording()
        {
            if (Recorder.PlayLastRecording())
            {
                SetStatus("Playing the most recent completed recording.");
            }
            else
            {
                SetStatus("There is no completed recording to play.");
            }
        }

        public void StopPlayback()
        {
            if (Recorder.StopPlayback())
            {
                SetStatus("Recording playback stopped.");
            }
        }

        public void InspectLastRecording()
        {
            RecordedAudio recording = Recorder.LastRecording;
            if (recording == null)
            {
                SetStatus("There is no completed WAV result.");
                return;
            }

            SetResultText(string.Format(
                "WAV result: {0} | {1:0.00} s | {2:N0} bytes | {3} Hz | {4} ch | PCM{5}",
                recording.Name,
                recording.DurationSeconds,
                recording.Size,
                recording.SampleRate,
                recording.Channels,
                recording.BitsPerSample));
            SetStatus("The complete WAV byte[] is available through RecordingCompleted and LastRecording.");
        }

        public void ClearRecording()
        {
            if (!Recorder.ClearRecording())
            {
                SetStatus("The current recording is still active and cannot be cleared.");
                return;
            }

            SetResultText("No completed WAV is currently held by the package.");
            SetStreamText("Realtime PCM16 output has not started.");
            SetStatus("The most recent recording and package-owned playback resources were cleared.");
        }

        public void SetRecordingDuration(float seconds)
        {
            if (Recorder.IsRecording)
            {
                if (recordingDurationSlider != null)
                {
                    recordingDurationSlider.SetValueWithoutNotify(selectedMaximumSeconds);
                }
                return;
            }

            selectedMaximumSeconds = Mathf.Clamp(
                Mathf.RoundToInt(seconds),
                Recorder.MinimumDurationSeconds,
                Recorder.MaximumDurationSeconds);
            if (recordingDurationSlider != null)
            {
                recordingDurationSlider.SetValueWithoutNotify(selectedMaximumSeconds);
            }
            UpdateDurationText();
        }

        public void UseFiveMinuteLimit()
        {
            SetRecordingDuration(300f);
        }

        public void UseThirtyMinuteLimit()
        {
            SetRecordingDuration(1800f);
        }

        public void UseSixtyMinuteLimit()
        {
            SetRecordingDuration(3600f);
        }

        private void BindControls()
        {
            if (startRecordingButton != null) startRecordingButton.onClick.AddListener(StartRecording);
            if (stopRecordingButton != null) stopRecordingButton.onClick.AddListener(StopRecording);
            if (playRecordingButton != null) playRecordingButton.onClick.AddListener(PlayLastRecording);
            if (stopPlaybackButton != null) stopPlaybackButton.onClick.AddListener(StopPlayback);
            if (inspectWavButton != null) inspectWavButton.onClick.AddListener(InspectLastRecording);
            if (clearRecordingButton != null) clearRecordingButton.onClick.AddListener(ClearRecording);
            if (fiveMinuteButton != null) fiveMinuteButton.onClick.AddListener(UseFiveMinuteLimit);
            if (thirtyMinuteButton != null) thirtyMinuteButton.onClick.AddListener(UseThirtyMinuteLimit);
            if (sixtyMinuteButton != null) sixtyMinuteButton.onClick.AddListener(UseSixtyMinuteLimit);
            if (recordingDurationSlider != null) recordingDurationSlider.onValueChanged.AddListener(SetRecordingDuration);
        }

        private void UnbindControls()
        {
            if (startRecordingButton != null) startRecordingButton.onClick.RemoveListener(StartRecording);
            if (stopRecordingButton != null) stopRecordingButton.onClick.RemoveListener(StopRecording);
            if (playRecordingButton != null) playRecordingButton.onClick.RemoveListener(PlayLastRecording);
            if (stopPlaybackButton != null) stopPlaybackButton.onClick.RemoveListener(StopPlayback);
            if (inspectWavButton != null) inspectWavButton.onClick.RemoveListener(InspectLastRecording);
            if (clearRecordingButton != null) clearRecordingButton.onClick.RemoveListener(ClearRecording);
            if (fiveMinuteButton != null) fiveMinuteButton.onClick.RemoveListener(UseFiveMinuteLimit);
            if (thirtyMinuteButton != null) thirtyMinuteButton.onClick.RemoveListener(UseThirtyMinuteLimit);
            if (sixtyMinuteButton != null) sixtyMinuteButton.onClick.RemoveListener(UseSixtyMinuteLimit);
            if (recordingDurationSlider != null) recordingDurationSlider.onValueChanged.RemoveListener(SetRecordingDuration);
        }

        private void HandleRecordingStarted()
        {
            SetStatus("Recording started.");
        }

        private void HandleRecordingCompleted(RecordedAudio recording)
        {
            SetStatus("Recording completed. The final PCM16 WAV is ready.");
            SetResultText(string.Format(
                "WAV result: {0} | {1:0.00} s | {2:N0} bytes",
                recording.Name,
                recording.DurationSeconds,
                recording.Size));
        }

        private void HandleRecordingCanceled()
        {
            SetStatus("The recording request was canceled before a WAV was produced.");
        }

        private void HandleRecordingFailed(string message)
        {
            SetStatus("Recording failed: " + message);
        }

        private void HandleAudioChunk(AudioStreamChunk chunk)
        {
            SetStreamText(string.Format(
                "PCM16 stream: chunk #{0} | {1:N0} bytes | {2} ms | IsLast={3}",
                chunk.Sequence,
                chunk.Data.Length,
                chunk.TimestampMilliseconds,
                chunk.IsLast));
        }

        private void UpdateRecordingUI()
        {
            bool recordingOrPending = Recorder.IsRecording;
            bool finalizing = Recorder.IsFinalizing;

            if (recordingDurationSlider != null)
            {
                recordingDurationSlider.SetValueWithoutNotify(selectedMaximumSeconds);
                recordingDurationSlider.interactable = !recordingOrPending;
            }
            if (streamAudioToggle != null) streamAudioToggle.interactable = !recordingOrPending;
            if (fiveMinuteButton != null) fiveMinuteButton.interactable = !recordingOrPending;
            if (thirtyMinuteButton != null) thirtyMinuteButton.interactable = !recordingOrPending;
            if (sixtyMinuteButton != null) sixtyMinuteButton.interactable = !recordingOrPending;

            UpdateDurationText();

            if (inputLevelSlider != null)
            {
                inputLevelSlider.SetValueWithoutNotify(Recorder.CurrentInputLevel);
            }
            if (inputLevelFill != null)
            {
                inputLevelFill.color = Color.Lerp(
                    new Color32(50, 205, 160, 255),
                    new Color32(255, 91, 91, 255),
                    Recorder.CurrentPeakLevel);
            }
            if (recordingProgressSlider != null)
            {
                recordingProgressSlider.SetValueWithoutNotify(Recorder.IsRecording
                    ? Recorder.RecordingProgress
                    : Recorder.HasRecording ? 1f : 0f);
            }

            int elapsedSeconds = Mathf.FloorToInt(Recorder.RecordingElapsedSeconds);
            int remainingSeconds = Mathf.CeilToInt(Recorder.IsRecording
                ? Recorder.RecordingRemainingSeconds
                : selectedMaximumSeconds);
            if (recordingElapsedText != null)
            {
                recordingElapsedText.text = "Elapsed  " + FormatSeconds(elapsedSeconds);
            }
            if (recordingRemainingText != null)
            {
                recordingRemainingText.text = "Remaining  " + FormatSeconds(remainingSeconds);
            }

            if (startRecordingButton != null) startRecordingButton.interactable = Recorder.IsAvailable && !recordingOrPending;
            if (stopRecordingButton != null) stopRecordingButton.interactable = recordingOrPending && !finalizing;
            if (playRecordingButton != null) playRecordingButton.interactable = Recorder.HasRecording && !recordingOrPending;
            if (stopPlaybackButton != null) stopPlaybackButton.interactable = Recorder.IsPlaybackActive;
            if (inspectWavButton != null) inspectWavButton.interactable = Recorder.HasRecording && !recordingOrPending;
            if (clearRecordingButton != null) clearRecordingButton.interactable = Recorder.HasRecording && !recordingOrPending;

            UpdateIndicator(finalizing);
        }

        private void UpdateDurationText()
        {
            if (recordingDurationText != null)
            {
                recordingDurationText.text = string.Format(
                    "Maximum duration: {0}   (5 seconds to 60 minutes)",
                    FormatSeconds(selectedMaximumSeconds));
            }
        }

        private void UpdateIndicator(bool finalizing)
        {
            string label;
            Color color;
            if (finalizing)
            {
                label = "Generating WAV";
                color = new Color32(168, 112, 255, 255);
            }
            else if (Recorder.IsPlaybackActive)
            {
                label = "Playing";
                color = new Color32(74, 158, 255, 255);
            }
            else if (Recorder.IsRecordingActive)
            {
                label = "Recording";
                color = new Color32(255, 78, 78, 255);
            }
            else if (Recorder.IsStartPending)
            {
                label = "Waiting for permission";
                color = new Color32(255, 181, 70, 255);
            }
            else
            {
                label = "Idle";
                color = new Color32(92, 105, 128, 255);
            }

            if (recordingIndicator != null) recordingIndicator.color = color;
            if (recordingIndicatorText != null) recordingIndicatorText.text = label;
        }

        private void SetStatus(string message)
        {
            currentStatus = message ?? string.Empty;
            if (statusText != null) statusText.text = currentStatus;
        }

        private void SetResultText(string message)
        {
            if (resultText != null) resultText.text = message ?? string.Empty;
        }

        private void SetStreamText(string message)
        {
            if (streamText != null) streamText.text = message ?? string.Empty;
        }

        private static string FormatSeconds(int seconds)
        {
            seconds = Mathf.Max(0, seconds);
            return string.Format("{0:00}:{1:00}", seconds / 60, seconds % 60);
        }
    }
}
