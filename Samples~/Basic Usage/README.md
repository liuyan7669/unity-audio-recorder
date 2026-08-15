# Basic Usage

导入 Sample 后打开 `Basic Usage.unity`，进入 Play Mode 即可使用，无需绑定 Inspector 引用。

- 开始按钮最终只调用 `AudioRecorder.StartRecording(...)`。
- 可用 5、30、60 分钟预设或滑杆设置 5～3600 秒；默认仍为 5 分钟。
- 完整 WAV 从 `AudioRecorder.RecordingCompleted(RecordedAudio recording)` 取得。
- 实时模式从 `AudioRecorder.AudioChunkReceived(AudioStreamChunk chunk)` 取得 PCM16 分块。
- “普通 AudioClip”按钮演示异步 `CreateAudioClipFromPcm16WavAsync(...)`。
- 原生平台的“按需解码 AudioClip”按钮演示 `CreateStreamingAudioClipFromPcm16Wav(...)`。
- 原生平台可把实时 PCM 持续写入一个 `Pcm16AudioClipStream`；监听时注意扬声器回授。
- `StopRecording()` 只提交停止请求；`IsFinalizing` 为 `true` 时正在生成 WAV，完成后才触发
  `RecordingCompleted`。
- Sample 不依赖 File Bridge；需要保存时由项目把完整 `RecordedAudio.Data` 交给可选 File Bridge。
- WebGL 开始和播放按钮必须由真实用户点击直接触发。
