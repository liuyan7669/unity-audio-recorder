# Audio Recorder UGUI Usage

这是可从 Unity Package Manager 按需导入的纯录音 UGUI 示例。安装包后，它不会自动出现在
`Assets`；点击 **Audio Recorder > Samples > UGUI Usage > Import** 后，Unity 默认导入到：

```text
Assets\Samples\Audio Recorder\1.0.0\UGUI Usage
```

打开 `Audio Recorder UGUI.unity` 并进入 Play Mode。示例使用 `UnityEngine.UI` 和 Unity 内置
动态字体，不依赖 TMP、File Bridge、任何语音识别包或原项目资源。

## 界面功能

- 使用 5、30、60 分钟预设，或在 5～3600 秒之间设置本次录音上限。
- `Start Recording` 调用唯一入口 `AudioRecorder.StartRecording(...)`。
- `Stop Recording` 手动结束采集；到达本次上限时也会自动停止。
- 停止后生成完整 PCM16 WAV。原生长录音会先进入 `IsFinalizing`，完成时间取决于录音长度和设备。
- `Play Recording`、`Stop Playback` 和 `Clear Recording` 操作最近一次完整结果。
- `Inspect WAV` 显示文件名、时长、字节数、采样率、声道和位深。
- 打开 `Realtime PCM16 output` 后，通过 `AudioChunkReceived` 实时显示 PCM16 块信息。

完整结果从下面两个出口取得：

```csharp
AudioRecorder.RecordingCompleted += recording =>
{
    byte[] pcm16Wav = recording.Data;
};

RecordedAudio last = AudioRecorder.LastRecording;
```

实时块 `AudioStreamChunk.Data` 是无 WAV 文件头的 PCM16 Little Endian 数据；最后一个结束块允许
为空。第三方实时播放、识别或传输代码应即时消费这些块，不要在订阅者里长期累计全部块。

## 保存与其他包组合

本 Sample 不提供系统保存按钮，避免让 Audio Recorder 对 File Bridge 形成硬依赖。需要保存时，
由业务层把 `RecordedAudio.Data`、`Name`、`MimeType` 交给自己的文件实现或可选 File Bridge。
录音包和文件包仍可各自单独安装。

## 长录音注意事项

- 无参开始仍默认 5 分钟；显式选择 30/60 分钟才会扩大本次上限。
- 1 小时 16 kHz、单声道、PCM16 WAV 约为 109.9 MiB。
- WebGL 结束瞬间的录音增量内存峰值仍可能约 440 MiB；低内存浏览器不能保证 60 分钟稳定。
- 真实 Windows、Android 和桌面 WebGL 麦克风权限、30/60 分钟录制、结束耗时与内存峰值必须在
  目标设备验收。场景能打开或 Player 能构建不等于真实设备录音已经通过。
- WebGL 必须由按钮等真实用户手势直接开始录音，并部署在 HTTPS 或 localhost 安全上下文。

Sample 不会自动加入 Build Settings。如需构建此界面，请自行添加导入后的场景。
