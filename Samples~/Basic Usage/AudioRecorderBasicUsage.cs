using System;
using Cowart.AudioRecorder;
using Recorder = Cowart.AudioRecorder.AudioRecorder;
using UnityEngine;

namespace Cowart.AudioRecorder.Samples
{
    /// <summary>
    /// 导入示例场景后无需绑定 Inspector 引用即可运行，也不依赖 File Bridge。
    /// </summary>
    public sealed class AudioRecorderBasicUsage : MonoBehaviour
    {
        private int maximumDurationSeconds = Recorder.DefaultMaximumDurationSeconds;
        private bool streamAudio;
        private bool monitorRealtimeAudio;
        private bool isCreatingAudioClip;
        private string status = "点击“开始录音”。唯一入口是 AudioRecorder.StartRecording(...)。";
        private GUIStyle titleStyle;
        private GUIStyle sectionStyle;
        private GUIStyle wrappedLabelStyle;
        private AudioSource sampleAudioSource;
        private AudioClip ownedAudioClip;
        private Pcm16AudioClipStream realtimeClipStream;
        private float realtimeClipReleaseAt = -1f;

        private void OnEnable()
        {
            Recorder.RecordingStarted += HandleStarted;
            Recorder.RecordingCompleted += HandleCompleted;
            Recorder.RecordingCanceled += HandleCanceled;
            Recorder.RecordingFailed += HandleFailed;
            Recorder.AudioChunkReceived += HandleAudioChunk;
        }

        private void OnDisable()
        {
            Recorder.RecordingStarted -= HandleStarted;
            Recorder.RecordingCompleted -= HandleCompleted;
            Recorder.RecordingCanceled -= HandleCanceled;
            Recorder.RecordingFailed -= HandleFailed;
            Recorder.AudioChunkReceived -= HandleAudioChunk;
            AbortRealtimeClipStream();
        }

        private void OnDestroy()
        {
            DisposeRealtimeClipStream();
            if (ownedAudioClip != null)
            {
                Destroy(ownedAudioClip);
                ownedAudioClip = null;
            }
        }

        private void Update()
        {
            if (realtimeClipStream == null)
            {
                return;
            }

            if (realtimeClipReleaseAt < 0f &&
                sampleAudioSource != null &&
                !sampleAudioSource.isPlaying &&
                realtimeClipStream.IsReadyToPlay)
            {
                sampleAudioSource.Play();
            }

            if (realtimeClipStream.IsDrained && realtimeClipReleaseAt < 0f)
            {
                AudioSettings.GetDSPBufferSize(out int bufferFrames, out int bufferCount);
                float dspTail = bufferFrames * bufferCount /
                    (float)AudioSettings.outputSampleRate;
                realtimeClipReleaseAt = Time.unscaledTime + dspTail;
            }

            if (realtimeClipReleaseAt >= 0f && Time.unscaledTime >= realtimeClipReleaseAt)
            {
                DisposeRealtimeClipStream();
            }
        }

        private void OnGUI()
        {
            EnsureStyles();

            float panelWidth = Mathf.Min(760f, Screen.width - 32f);
            float panelHeight = Mathf.Max(100f, Screen.height - 32f);
            GUILayout.BeginArea(new Rect(16f, 16f, panelWidth, panelHeight), GUI.skin.box);

            GUILayout.Label("Audio Recorder - Basic Usage", titleStyle);
            GUILayout.Label(
                "调用顺序：StartRecording(...) → RecordingStarted → 用户停止或到达时长上限 → " +
                "RecordingCompleted(recording) → recording.Data 取得完整 WAV byte[]。",
                wrappedLabelStyle);

            GUILayout.Space(8f);
            GUILayout.Label("1. 录音设置", sectionStyle);
            GUI.enabled = !Recorder.IsRecording;
            GUILayout.Label("最长录音：" + FormatSeconds(maximumDurationSeconds), wrappedLabelStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("5 分钟")) maximumDurationSeconds = 300;
            if (GUILayout.Button("30 分钟")) maximumDurationSeconds = 1800;
            if (GUILayout.Button("60 分钟")) maximumDurationSeconds = 3600;
            GUILayout.EndHorizontal();
            maximumDurationSeconds = Mathf.RoundToInt(GUILayout.HorizontalSlider(
                maximumDurationSeconds,
                Recorder.MinimumDurationSeconds,
                Recorder.MaximumDurationSeconds));
            streamAudio = GUILayout.Toggle(streamAudio, "同时输出 40 ms PCM16 实时音频块");
            GUI.enabled = !Recorder.IsRecording && Recorder.IsRealtimeAudioClipStreamingSupported;
            monitorRealtimeAudio = GUILayout.Toggle(
                monitorRealtimeAudio,
                "把实时 PCM 写入一个流式 AudioClip 并监听（注意麦克风回授）");

            GUILayout.Space(8f);
            GUILayout.Label("2. 录制与结果", sectionStyle);
            GUI.enabled = Recorder.IsAvailable && !Recorder.IsRecording;
            if (GUILayout.Button("开始录音", GUILayout.Height(36f)))
            {
                StartRecording();
            }

            GUI.enabled = Recorder.IsRecording && !Recorder.IsFinalizing;
            if (GUILayout.Button("停止录音并生成 WAV", GUILayout.Height(36f)))
            {
                StopRecording();
            }

            GUI.enabled = Recorder.HasRecording && !Recorder.IsRecording;
            if (GUILayout.Button("播放最近录音", GUILayout.Height(34f)))
            {
                PlayRecording();
            }

            GUI.enabled = Recorder.HasRecording && !Recorder.IsRecording && !isCreatingAudioClip;
            if (GUILayout.Button("异步 WAV byte[] → 普通 AudioClip 并播放", GUILayout.Height(34f)))
            {
                CreateAndPlayRegularAudioClip();
            }

            GUI.enabled = Recorder.HasRecording &&
                !Recorder.IsRecording &&
                Recorder.IsRealtimeAudioClipStreamingSupported;
            if (GUILayout.Button("WAV byte[] → 按需解码 AudioClip 并播放", GUILayout.Height(34f)))
            {
                CreateAndPlayStreamingAudioClip();
            }

            GUI.enabled = Recorder.IsPlaybackActive ||
                (sampleAudioSource != null && sampleAudioSource.isPlaying);
            if (GUILayout.Button("停止播放", GUILayout.Height(34f)))
            {
                StopPlayback();
            }

            GUI.enabled = Recorder.HasRecording && !Recorder.IsRecording;
            if (GUILayout.Button("清空最近录音", GUILayout.Height(34f)))
            {
                ClearRecording();
            }

            GUI.enabled = true;
            GUILayout.Space(8f);
            GUILayout.Label("3. 当前状态", sectionStyle);
            GUILayout.Label(status, wrappedLabelStyle);
            GUILayout.Label(
                "IsRecording：" + Recorder.IsRecording +
                "    IsFinalizing：" + Recorder.IsFinalizing +
                "    HasRecording：" + Recorder.HasRecording +
                "    IsPlaybackActive：" + Recorder.IsPlaybackActive,
                wrappedLabelStyle);
            GUILayout.Label(
                "录音时间：" + FormatSeconds(Mathf.FloorToInt(Recorder.RecordingElapsedSeconds)) +
                "    流式块：" + Recorder.StreamChunkCount +
                "    PCM 字节：" + Recorder.StreamedPcmByteCount,
                wrappedLabelStyle);

            RecordedAudio recording = Recorder.LastRecording;
            if (recording != null)
            {
                byte[] wavBytes = recording.Data;
                GUILayout.Label(
                    "最近结果：" + recording.Name + "，" +
                    recording.DurationSeconds.ToString("0.00") + " 秒，" +
                    wavBytes.Length.ToString("N0") + " 个 WAV 字节。",
                    wrappedLabelStyle);
            }

            GUILayout.EndArea();
        }

        public void StartRecording()
        {
            DisposeRealtimeClipStream();
            if (monitorRealtimeAudio && !TryCreateRealtimeClipStream())
            {
                return;
            }

            const string startingStatus = "正在启动录音请求……";
            status = startingStatus;
            bool accepted = Recorder.StartRecording(
                    maximumDurationSeconds,
                    streamAudio || monitorRealtimeAudio,
                    Recorder.DefaultStreamChunkMilliseconds);
            if (!accepted && status == startingStatus)
            {
                status = "录音请求未被接受。";
                DisposeRealtimeClipStream();
            }
            else if (accepted && Recorder.IsStartPending && status == startingStatus)
            {
                status = "正在等待麦克风权限……";
            }
        }

        public void StopRecording()
        {
            const string stoppingStatus = "正在提交停止请求……";
            status = stoppingStatus;
            bool accepted = Recorder.StopRecording();
            if (!accepted && status == stoppingStatus)
            {
                status = Recorder.IsFinalizing
                    ? "采集已停止，正在生成 WAV……"
                    : "当前没有可停止的录音。";
            }
            else if (accepted && Recorder.IsFinalizing && status == stoppingStatus)
            {
                status = "采集已停止，正在生成 WAV……";
            }
        }

        public void StopPlayback()
        {
            bool stopped = Recorder.StopPlayback();
            if (realtimeClipStream != null)
            {
                AbortRealtimeClipStream();
                stopped = true;
            }

            if (sampleAudioSource != null && sampleAudioSource.isPlaying)
            {
                sampleAudioSource.Stop();
                stopped = true;
            }

            status = stopped ? "播放已停止。" : "当前没有正在播放的音频。";
        }

        public void PlayRecording()
        {
            if (!Recorder.PlayLastRecording())
            {
                status = "当前没有可播放的完整录音。";
            }
        }

        public void ClearRecording()
        {
            if (Recorder.ClearRecording())
            {
                status = "录音已清空。";
            }
        }

        public async void CreateAndPlayRegularAudioClip()
        {
            RecordedAudio recording = Recorder.LastRecording;
            if (recording == null || isCreatingAudioClip)
            {
                return;
            }

            isCreatingAudioClip = true;
            status = "正在分帧创建普通 AudioClip……";
            try
            {
                AudioClip clip = await Recorder.CreateAudioClipFromPcm16WavAsync(
                    recording.Data,
                    "Basic Usage WAV Clip");
                if (this == null)
                {
                    Destroy(clip);
                    return;
                }

                ReplaceOwnedAudioClip(clip);
                PlayOwnedAudioClip();
                status = "普通 AudioClip 已创建并开始播放。";
            }
            catch (Exception exception)
            {
                status = "创建普通 AudioClip 失败：" + exception.Message;
            }
            finally
            {
                isCreatingAudioClip = false;
            }
        }

        public void CreateAndPlayStreamingAudioClip()
        {
            RecordedAudio recording = Recorder.LastRecording;
            if (recording == null)
            {
                return;
            }

            try
            {
                AudioClip clip = Recorder.CreateStreamingAudioClipFromPcm16Wav(
                    recording.Data,
                    "Basic Usage Streaming WAV Clip");
                ReplaceOwnedAudioClip(clip);
                PlayOwnedAudioClip();
                status = "按需解码 AudioClip 已创建并开始播放。";
            }
            catch (Exception exception)
            {
                status = "创建按需解码 AudioClip 失败：" + exception.Message;
            }
        }

        private void HandleStarted()
        {
            status = "录音已开始。";
        }

        private void HandleCompleted(RecordedAudio recording)
        {
            byte[] wavBytes = recording.Data;
            status = string.Format(
                "RecordingCompleted 已交付完整结果：{0:0.00} 秒，{1:N0} 字节。",
                recording.DurationSeconds,
                wavBytes.Length);
        }

        private void HandleCanceled()
        {
            status = "录音已取消。";
            AbortRealtimeClipStream();
        }

        private void HandleFailed(string message)
        {
            status = "录音失败：" + message;
            AbortRealtimeClipStream();
        }

        private void HandleAudioChunk(AudioStreamChunk chunk)
        {
            if (realtimeClipStream != null && !realtimeClipStream.TryWrite(chunk))
            {
                status = "实时 AudioClip 缓冲不足，监听流已中止；完整 WAV 录制不受影响。";
                AbortRealtimeClipStream();
                return;
            }

            if (chunk.IsLast)
            {
                status = "实时 PCM 已结束，共 " + Recorder.StreamChunkCount + " 个音频块。";
            }
        }

        private bool TryCreateRealtimeClipStream()
        {
            if (!Recorder.IsRealtimeAudioClipStreamingSupported)
            {
                status = "当前平台不支持 Unity 动态流式 AudioClip。";
                return false;
            }

            try
            {
                realtimeClipStream = Recorder.CreateRealtimeAudioClipStream(
                    "Basic Usage Realtime PCM",
                    Recorder.OutputSampleRate,
                    Recorder.OutputChannels,
                    4000,
                    Recorder.DefaultRealtimePrebufferMilliseconds);
                realtimeClipReleaseAt = -1f;
                AudioSource source = GetOrCreateSampleAudioSource();
                source.Stop();
                source.clip = realtimeClipStream.Clip;
                source.loop = true;
                return true;
            }
            catch (Exception exception)
            {
                status = "创建实时 AudioClip 失败：" + exception.Message;
                DisposeRealtimeClipStream();
                return false;
            }
        }

        private void AbortRealtimeClipStream()
        {
            if (realtimeClipStream != null)
            {
                GetOrCreateSampleAudioSource().Stop();
                realtimeClipStream.Abort();
                DisposeRealtimeClipStream();
            }
        }

        private void DisposeRealtimeClipStream()
        {
            if (realtimeClipStream == null)
            {
                return;
            }

            if (sampleAudioSource != null && sampleAudioSource.clip == realtimeClipStream.Clip)
            {
                sampleAudioSource.Stop();
                sampleAudioSource.clip = null;
            }

            realtimeClipStream.Dispose();
            realtimeClipStream = null;
            realtimeClipReleaseAt = -1f;
        }

        private void ReplaceOwnedAudioClip(AudioClip clip)
        {
            DisposeRealtimeClipStream();
            AudioSource source = GetOrCreateSampleAudioSource();
            source.Stop();
            source.clip = null;
            if (ownedAudioClip != null)
            {
                Destroy(ownedAudioClip);
            }

            ownedAudioClip = clip;
        }

        private void PlayOwnedAudioClip()
        {
            AudioSource source = GetOrCreateSampleAudioSource();
            source.clip = ownedAudioClip;
            source.loop = false;
            source.Play();
        }

        private AudioSource GetOrCreateSampleAudioSource()
        {
            if (sampleAudioSource == null)
            {
                sampleAudioSource = GetComponent<AudioSource>();
                if (sampleAudioSource == null)
                {
                    sampleAudioSource = gameObject.AddComponent<AudioSource>();
                }

                sampleAudioSource.playOnAwake = false;
            }

            return sampleAudioSource;
        }

        private void EnsureStyles()
        {
            if (titleStyle != null)
            {
                return;
            }

            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 24,
                fontStyle = FontStyle.Bold
            };
            sectionStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 17,
                fontStyle = FontStyle.Bold
            };
            wrappedLabelStyle = new GUIStyle(GUI.skin.label)
            {
                wordWrap = true
            };
        }

        private static string FormatSeconds(int seconds)
        {
            return string.Format("{0:00}:{1:00}", seconds / 60, seconds % 60);
        }
    }
}
