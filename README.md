# Audio Recorder

Audio Recorder 是一个 Unity UPM 语音录制包。业务代码只通过
`Cowart.AudioRecorder.AudioRecorder` 静态门面控制录音，不需要在场景中放置 Recorder、
Prefab 或全局管理器。

包同时提供两条音频输出通道：

- 完整录音：录音结束后通过 `RecordingCompleted(RecordedAudio)` 返回完整 WAV `byte[]`，
  适合保存、上传、一次性语音识别和离线处理。
- 实时音频流：录音过程中通过 `AudioChunkReceived(AudioStreamChunk)` 持续返回无 WAV
  文件头的 PCM16 音频块，适合实时语音识别、网络传输、波形、VAD 和实时播放适配器。

所有输出统一为 **16 kHz、16-bit、单声道**。完整结果是 PCM16 WAV；实时块是
PCM16 Little Endian 原始字节。

包还提供独立的 PCM16 `byte[]` → Unity `AudioClip` 工具：可异步生成普通 Clip、从完整
WAV 创建按需解码 Clip，也可把连续实时 PCM 块写入一个有界环形缓冲 Clip。这些工具只消费
音频数据，不会启动麦克风；录音入口仍然只有 `AudioRecorder.StartRecording(...)`。

## 功能与边界

| 能力 | 当前包是否提供 | 使用方式 |
|---|---:|---|
| 开始、停止和最长时长自动停止 | 是 | `StartRecording(...)`、`StopRecording()` |
| 完整 WAV 输出 | 是 | `RecordingCompleted`、`LastRecording` |
| 实时 PCM16 分块输出 | 是 | `AudioChunkReceived`，启动时设置 `streamAudio: true` |
| 完整 WAV `byte[]` 转普通 `AudioClip` | 是 | `CreateAudioClipFromPcm16WavAsync(...)` |
| 完整 WAV 按需解码 `AudioClip` | 原生平台 | `CreateStreamingAudioClipFromPcm16Wav(...)` |
| 实时 PCM 环形缓冲 `AudioClip` | 原生平台 | `CreateRealtimeAudioClipStream(...)` |
| 最近录音播放 | 是 | `PlayLastRecording()`、`StopPlayback()` |
| 保存 WAV 到用户设备 | 可选组合 | 安装 File Bridge 后，把 `RecordedAudio.Data/Name/MimeType` 交给 `FileBridge.SaveFile(...)` |
| 录音时长、进度和输入电平 | 是 | 轮询静态状态属性 |
| Android 麦克风运行时权限 | 是 | 首次启动时请求 `RECORD_AUDIO` |
| WebGL 浏览器麦克风 | 是 | `getUserMedia`，要求 HTTPS/localhost 和真实用户手势 |
| 第三方语音识别 | 不直接提供 | 独立消费者订阅完整 WAV/实时 PCM 出口 |
| 实时录音监听播放 | 提供数据到 Clip 的底层会话 | 业务仍负责 `AudioSource`、生命周期和防回授 |
| MP3、AAC、Opus 压缩 | 否 | 交给独立编码包处理 |
| 降噪、回声消除、VAD | 否 | 交给独立音频处理包处理 |
| 网络上传或 WebSocket | 否 | 交给业务网络层或第三方包处理 |

推荐依赖方向：

```text
UGUI / 业务代码
        │
        ▼
AudioRecorder 唯一静态入口
        │
        ├─ RecordingCompleted(RecordedAudio) ──► 一次性识别 / 上传 / 后处理
        ├─ AudioChunkReceived(AudioStreamChunk) ► 实时识别 / 实时播放 / VAD
        ├─ PCM16 WAV byte[] ────────────────────► 普通或按需解码 AudioClip
        ├─ PCM16 实时块 ────────────────────────► Pcm16AudioClipStream
        └─ PCM16 WAV byte[] ────────────────────► 可选 File Bridge 保存出口
```

录音包只负责采集、标准化和输出音频，不引用 File Bridge 或任何语音服务 SDK。第三方包只依赖
公开 DTO 和静态事件，不应访问内部 Driver 或 WebGL `.jslib`。

## 支持范围与依赖

- UPM 清单最低 Unity 版本：`2021.3`；当前项目使用 `2021.3.30f1`。更高版本仍需在目标项目
  中重新编译和验收，不能仅凭版本号认定已实测。
- 运行平台：Unity Editor、Windows Standalone、Android 原生 APK、桌面浏览器 WebGL。
- 当前不提供 iOS、macOS Standalone 或 Linux Standalone 后端。
- 包可以独立安装和录音，不依赖 `com.cowart.file-bridge`。需要系统保存/浏览器下载时，再由业务
  可选安装 File Bridge `1.1.1` 或更高兼容版本并组合调用。
- 录音与实时 PCM 本身不依赖任何语音识别 SDK。

`package.json` 还显式声明以下 Unity 内置模块：

- `com.unity.modules.audio`：`Microphone`、`AudioClip` 和 `AudioSource`。
- `com.unity.modules.androidjni`：Android 权限与原生桥接支持。
- `com.unity.modules.imgui`：可导入的 `Basic Usage` 示例界面。
- `com.unity.ugui`：可导入的 `UGUI Usage` 示例场景。

`AudioRecorder.IsAvailable == true` 只表示当前编译目标有录音后端，不表示设备一定存在
麦克风、用户已经授权，也不表示 WebGL 页面满足安全上下文要求。真正结果必须看事件。

## 安装

Audio Recorder 可单独安装；File Bridge 只是可选保存能力。

### 通过 OpenUPM 安装

本包首次 Git 发布完成后，需要先在 OpenUPM 完成登记。登记并同步成功后，在 Unity 的
`Edit > Project Settings > Package Manager` 中添加：

```text
Name: package.openupm.com
URL: https://package.openupm.com
Scope: com.cowart.audio-recorder
```

然后通过 `Add package by name` 安装：

```text
com.cowart.audio-recorder
```

### 通过 Git URL 安装

```text
https://github.com/liuyan7669/unity-audio-recorder.git#1.0.0
```

### Package Manager 从磁盘安装

1. 打开 `Window > Package Manager`。
2. 点击左上角 `+`，选择 `Add package from disk...`。
3. 选择 `com.cowart.audio-recorder\package.json`。
4. 只有需要系统保存或浏览器下载时，才另外选择 `com.cowart.file-bridge\package.json`。

### 当前仓库的 Embedded/file 安装方式

只安装 Audio Recorder：

```json
{
  "dependencies": {
    "com.cowart.audio-recorder": "file:com.cowart.audio-recorder"
  }
}
```

需要保存时，再单独加入：

```json
"com.cowart.file-bridge": "file:com.cowart.file-bridge"
```

如果包放在项目外部，按实际相对路径调整 `file:` 地址。

## 30 秒快速接入

```csharp
using Cowart.AudioRecorder;
using UnityEngine;

public sealed class VoiceRecordingExample : MonoBehaviour
{
    private void OnEnable()
    {
        AudioRecorder.RecordingStarted += HandleStarted;
        AudioRecorder.RecordingCompleted += HandleCompleted;
        AudioRecorder.RecordingCanceled += HandleCanceled;
        AudioRecorder.RecordingFailed += HandleFailed;
    }

    private void OnDisable()
    {
        AudioRecorder.RecordingStarted -= HandleStarted;
        AudioRecorder.RecordingCompleted -= HandleCompleted;
        AudioRecorder.RecordingCanceled -= HandleCanceled;
        AudioRecorder.RecordingFailed -= HandleFailed;
    }

    public void StartFromButton()
    {
        bool requestAccepted = AudioRecorder.StartRecording(maxDurationSeconds: 60);
        if (!requestAccepted)
        {
            Debug.Log("录音请求没有启动，或启动失败事件已经同步交付。");
        }
    }

    public void StopFromButton()
    {
        if (!AudioRecorder.StopRecording())
        {
            Debug.Log("当前没有可停止的录音，或录音已经在结束处理中。");
        }
    }

    private static void HandleStarted()
    {
        Debug.Log("麦克风已经开始采样。");
    }

    private static void HandleCompleted(RecordedAudio recording)
    {
        byte[] completeWavBytes = recording.Data;
        Debug.Log($"完整 WAV：{recording.Name}，{recording.DurationSeconds:0.00} 秒，{recording.Size} 字节");
    }

    private static void HandleCanceled()
    {
        Debug.Log("录音在开始采样前被取消，没有生成 WAV。");
    }

    private static void HandleFailed(string message)
    {
        Debug.LogError("录音失败：" + message);
    }
}
```

静态事件必须先订阅再调用入口，并在 `OnDisable` 中对称退订。事件是全局的；遗漏退订会让
对象反复启用后收到重复回调。

## 唯一入口与会话流程

新业务代码只使用：

```csharp
using Cowart.AudioRecorder;
```

包会在第一次录音时自动创建隐藏的内部 Driver，并跨场景保留。不要手动创建 Driver，
也不要把旧的 `CrossPlatformAudioRecorder` 组件作为新入口。

关闭或切换某个 UI 不会自动停止这场全局录音；`OnDisable` 退订后，该 UI 也不会再收到终态。
业务应明确决定“界面关闭后继续录音”还是先调用 `StopRecording()` 并等待终态。应用退出或内部
Driver 被销毁时会直接释放采集资源，不应依赖退出阶段再收到业务终态。

一次正常会话的流程：

```text
先订阅事件
→ StartRecording(...) 返回请求是否被接受
→ 等待权限或设备启动
→ RecordingStarted
→ 持续录音，可选 AudioChunkReceived
→ 用户 StopRecording() / 到达时长上限 / 应用暂停
→ 停止采集并生成 WAV
→ RecordingCompleted(recording) 或 RecordingFailed(message)
```

在等待 Android/WebGL 权限时调用 `StopRecording()`：

```text
StartRecording(...)
→ IsStartPending == true
→ StopRecording()
→ RecordingCanceled
```

同一时间全局只允许一个录音会话。一个进入录音 Driver 的会话最多发布一个终态：

- `RecordingCompleted`
- `RecordingCanceled`
- `RecordingFailed`

包会先完成内部状态提交，再触发终态事件，因此可以在终态事件回调中安全启动下一次录音。
不要在流式 `IsLast` 块回调中立即重启；此时上一会话仍处于结束阶段，应等待它的终态事件。

## 开始录音

```csharp
bool AudioRecorder.StartRecording(
    int maxDurationSeconds = 300,
    bool streamAudio = false,
    int streamChunkDurationMilliseconds = 40);
```

### 参数

| 参数 | 默认值 | 有效范围 | 说明 |
|---|---:|---:|---|
| `maxDurationSeconds` | `300` | `5`～`3600` 秒 | 本次录音自动停止上限，整数秒 |
| `streamAudio` | `false` | `true` / `false` | 是否允许本次录音产生实时 PCM 块 |
| `streamChunkDurationMilliseconds` | `40` | `20`～`1000` 毫秒 | 实时块目标时长，整数毫秒 |

即使 `streamAudio == false`，传入的分块时长仍必须位于有效范围内。

超出参数范围会在访问麦克风前抛出 `ArgumentOutOfRangeException`。所有公开方法只能从
Unity 主线程调用；从后台线程调用会抛出 `InvalidOperationException`。

### 返回值

- `true`：后端接受了启动请求。它不表示用户已经授权，也不表示最终一定生成 WAV。
- `false`：请求没有保持为已接受状态，例如已有录音、平台无后端、没有麦克风或后端启动失败。

部分启动失败会在 `StartRecording()` 返回 `false` 之前同步触发 `RecordingFailed`；已有会话
等直接拒绝情况则可能只返回 `false`，不会再发终态。不能用返回值代替事件，也不要在方法
返回后无条件覆盖事件回调已经写入的 UI 状态。

`RecordingStarted` 表示麦克风已经真正开始采样。在 Editor、Windows 以及已经取得权限的
Android 中，它可能在 `StartRecording()` 返回前同步触发；需要弹出 Android 权限窗口或
WebGL 浏览器授权时通常稍后触发。

## 录音时长与自动停止

- 最长录音可设为 `5`～`3600` 秒，包含 5 秒和 3600 秒。
- 参数类型是 `int`，范围内任意整数秒都可以，不支持小数秒。
- 默认上限是 `300` 秒，也就是 5 分钟。
- 到达本次上限后，包会自动停止采集并尝试生成 WAV。
- 可以在到时前手动停止，因此实际有效录音可以短于 5 秒。
- 自动停止由 Unity 帧更新或浏览器计时器触发，可能有少量调度误差，不是毫秒级硬实时计时器。
- 应用在实际录音期间进入暂停状态时，包会请求停止并生成当前录音。

```csharp
// 最多录制 90 秒，也可以提前调用 StopRecording()。
bool requestAccepted = AudioRecorder.StartRecording(maxDurationSeconds: 90);

// 最多录制 30 分钟。
bool thirtyMinutesAccepted = AudioRecorder.StartRecording(maxDurationSeconds: 1800);

// 最多录制 1 小时。
bool oneHourAccepted = AudioRecorder.StartRecording(maxDurationSeconds: 3600);
```

权限拒绝、设备异常或没有采集到有效音频时不会伪造空 WAV，最终走取消或失败出口。

### 重要：30～60 分钟长录音的存储、内存与上线注意事项

> [!IMPORTANT]
> 1 小时录音生成的 WAV 约为 **109.9 MiB**。WebGL 在结束录音并生成完整结果时，单次录音的
> 增量内存峰值按当前代码路径静态估算可能达到约 **440 MiB**；该数字还不包含 Unity Player、
> 项目资源和浏览器本身的基础内存。低内存浏览器可能分配失败、卡顿甚至终止 Player，因此
> **不能把“允许设置 60 分钟”理解为所有 WebGL 设备都能稳定录满 60 分钟**。

完整输出固定为 16 kHz、16-bit、单声道 PCM16 WAV，因此数据量可以直接估算：

| 时长 | 完整 WAV 大小 |
|---:|---:|
| 5 分钟 | 约 9.6 MB |
| 30 分钟 | `57,600,044` 字节，约 54.9 MiB |
| 60 分钟 | `115,200,044` 字节，约 109.9 MiB |

Editor、Windows 和 Android 不会再让 `Microphone.Start` 预分配整段 30/60 分钟 Float Clip。
包使用固定 10 秒循环采集 Clip，持续把 PCM16 写入 `Application.temporaryCachePath` 下的临时 WAV；
停止时回填 WAV 头，并在后台读取最终 `byte[]`。因此录制期内存保持有界，但设备必须有足够临时
磁盘空间，结束时仍必须分配与完整 WAV 同等大小的连续托管数组。

原生播放 Clip 使用按需解码，不在停止时展开整段 float 数据。停止后的临时文件读取期间
`IsFinalizing == true`，`IsRecording` 仍为 `true`；最终只会进入一次 `RecordingCompleted` 或
`RecordingFailed`。不要在这个阶段开始新录音或清理结果。

WebGL 使用约 1 MiB 的 PCM pages，停止时直接以 `WAV header + pages` 创建浏览器 Blob，并只为
Unity 回调申请一次连续 Wasm WAV 区域，不再生成 `chunks → merged → WAV` 两份额外整段副本。
不过 1 小时结束时仍可能同时存在 pages、Blob、Wasm 临时区和托管 `byte[]`，桌面浏览器增量峰值
可能达到约 440 MiB。成功后浏览器 Blob 与 Unity 持有的完整 WAV 仍可能合计占用约 220 MiB，
直到相关引用和播放资源被释放。连续录制、保留上一段结果或业务层继续持有旧数组时，实际占用
还会继续增加。

业务收到长录音后应尽快上传、保存或处理，并在不再需要时调用 `ClearRecording()`。实时块订阅者
应即时消费或使用有界队列，不要同时把全部 `AudioStreamChunk.Data` 再累计一份。

#### 长录音启用前必须确认

- `3600` 秒是硬上限，不是“无限录音”；无参调用仍默认 `300` 秒。根据目标设备验收结果决定是否
  在业务 UI 中开放 30 或 60 分钟。
- 原生平台必须为 `Application.temporaryCachePath` 中的临时 WAV 预留足够磁盘空间；停止时还需要
  为最终完整 `byte[]` 分配一块连续内存。
- WebGL 必须在 HTTPS 或 localhost 的目标桌面浏览器测试。约 440 MiB 是当前实现的静态增量估算，
  不是浏览器总内存，也不是任何设备上的稳定性保证。
- WebGL 收尾（手动停止或达到时长上限）会同步生成 Blob、复制 Wasm 数据并交付托管 WAV；长录音
  结束时可能出现明显主线程停顿。手动停止时，`RecordingCompleted` 也可能在
  `StopRecording()` 返回前触发。
- 如果开启 `streamAudio`，消费方必须快速转交每个 PCM 块并使用有界队列。把所有实时块再保存一份，
  会在完整 WAV 之外额外占用接近同等规模的内存。
- 上一段结果不再需要时，在空闲状态调用 `ClearRecording()`；它只能释放包持有的资源，业务或
  第三方仍保存的 `RecordedAudio.Data`、AudioClip 和队列必须由各自持有者释放。
- 发布前至少分别完成 Windows、Android 真机和目标桌面浏览器的 30/60 分钟手动停止、自动停止、
  WAV 时长、内存峰值、临时磁盘、结束耗时、后台/暂停以及连续录制验收。构建成功不能代替这些测试。

## 停止录音与终态

```csharp
bool stopAccepted = AudioRecorder.StopRecording();
```

- 返回 `true`：当前会话接受了停止请求。
- 返回 `false`：通常表示没有录音、录音已经结束，或已经进入 WAV 生成阶段；WebGL 后端停止
  失败时也可能先同步触发 `RecordingFailed` 再返回 `false`。
- 重复停止不会产生第二个终态。
- 等待 Android/WebGL 权限时停止，走 `RecordingCanceled`，不生成 WAV。
- 已经采集音频后停止，会尝试生成 WAV；成功走 `RecordingCompleted`，编码或设备错误走
  `RecordingFailed`。

原生后端会先停止采集、关闭临时 WAV，再由后台任务读取完整结果，因此正常完成事件会在
`StopRecording()` 返回后触发。当前 WebGL 手动停止会在同一调用链内生成并复制 WAV，成功的
`RecordingCompleted` 可能在 `StopRecording()` 返回前同步触发，`IsFinalizing` 也可能只在这次
调用内短暂为 `true`；部分取消或失败回调同样可能同步发生。业务必须始终提前订阅终态事件，
不依赖同步或异步的具体先后顺序。

## 五个静态事件出口

| 事件 | 类型 | 含义 |
|---|---|---|
| `RecordingStarted` | `Action` | 权限和设备已就绪，麦克风真正开始采样 |
| `AudioChunkReceived` | `Action<AudioStreamChunk>` | 流式模式下交付一个 PCM16 块 |
| `RecordingCompleted` | `Action<RecordedAudio>` | 完整 WAV 已生成并缓存 |
| `RecordingCanceled` | `Action` | 在开始采样前主动取消，没有 WAV |
| `RecordingFailed` | `Action<string>` | 权限、设备、采样或 WAV 生成失败 |

所有事件都在 Unity 主线程交付。包会逐个保护订阅者；某个订阅者抛异常时会记录异常，
但不会阻止其他订阅者接收同一事件。

`RecordingCompleted`、`RecordingCanceled`、`RecordingFailed` 是录音会话的三个互斥终态。
`RecordingStarted` 和 `AudioChunkReceived` 不是终态。

## 完整录音输出 `RecordedAudio`

`RecordingCompleted` 的回调参数是只读结果对象：

| 属性 | 类型 | 内容 |
|---|---|---|
| `Name` | `string` | 建议 WAV 文件名，例如 `recording_yyyyMMdd_HHmmss.wav` |
| `MimeType` | `string` | 当前为 `audio/wav` |
| `Data` | `byte[]` | 带 WAV 文件头的完整 PCM16 WAV 原始字节，不是路径或 Base64 |
| `Size` | `int` | `Data.Length` |
| `DurationSeconds` | `float` | 实际有效录音时长 |
| `SampleRate` | `int` | `16000` |
| `Channels` | `int` | `1` |
| `BitsPerSample` | `int` | `16` |

```csharp
private void HandleCompleted(RecordedAudio recording)
{
    UploadWav(recording.Data, recording.Name, recording.MimeType);
}
```

`RecordedAudio.Data` 与 `LastRecording.Data` 复用同一个数组引用。
业务应把它视为只读；如果第三方 API 可能修改传入数组，应先调用
`(byte[])recording.Data.Clone()`。

## PCM16 `byte[]` 转 Unity `AudioClip`

以下转换 API 都放在唯一静态门面 `AudioRecorder` 上，不新增另一个静态工具入口，也不会改变
`LastRecording`、包内最近录音播放资源或当前录音状态。它们只接受本包输出的 PCM16
Little Endian WAV/PCM，不支持 MP3、AAC、Ogg、Opus、浮点 WAV、24/32-bit WAV、RIFX 或
`WAVE_FORMAT_EXTENSIBLE`。

### 方式一：异步生成普通 `AudioClip`

需要把 Clip 交给第三方 Unity API、调用 `GetData()`、随机访问或长期复用时，使用：

```csharp
using System.Threading;
using System.Threading.Tasks;
using Cowart.AudioRecorder;
using UnityEngine;

public sealed class WavClipConsumer : MonoBehaviour
{
    [SerializeField] private AudioSource audioSource;
    private AudioClip ownedClip;
    private CancellationTokenSource cancellation;

    public async Task LoadAsync(byte[] pcm16WavBytes)
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();

        AudioClip newClip = await AudioRecorder.CreateAudioClipFromPcm16WavAsync(
            pcm16WavBytes,
            clipName: "Voice Clip",
            decodeFramesPerUpdate: AudioRecorder.DefaultAudioClipDecodeFramesPerUpdate,
            cancellationToken: cancellation.Token);

        if (ownedClip != null)
        {
            Destroy(ownedClip);
        }

        ownedClip = newClip;
        audioSource.clip = ownedClip;
    }

    private void OnDestroy()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        if (ownedClip != null)
        {
            Destroy(ownedClip);
        }
    }
}
```

执行模型：

- Editor、Windows 和 Android 原生：RIFF 解析和 PCM16 → `float[]` 解码在工作线程执行；
  `AudioClip.Create` 与 `SetData` 只在 Unity 主线程执行。
- `SetData` 按 `decodeFramesPerUpdate` 每帧写一批，默认 `8192` 帧。减小该值会降低单帧工作量，
  但完成总帧数会增加；范围为 `256`～`65536`。
- 同时发起多个转换时，共用内部调度器轮询；每个 Unity 帧最多让一个请求执行一次主线程
  创建/解码/写入步骤，避免并发请求把多批重活堆到同一帧。
- WebGL Player 没有可用的托管工作线程，因此保持同一个 `Task<AudioClip>` 契约，但在连续 Unity
  帧中协作式解析、分批解码到完整 float 数组，最后只调用一次 `SetData`。WebGL 的这次完整
  写入无法拆小，必须在真实浏览器测量；显式 `CancellationToken.Cancel()` 仍可取消。
- `AudioClip.Create` 本身必须在 Unity 主线程分配 Unity 对象。实现避免整段同步解码和一次性
  `SetData`，但不能承诺任意尺寸、任意设备上绝对零耗时尖峰。

调用约束：

- 方法必须从 Unity 主线程调用；不要在主线程使用 `.Wait()` 或 `.Result`，应使用 `await`。
- 任务完成前必须把输入 `byte[]` 当作只读，不能修改或复用其内容；实现不会在入口同步克隆
  整个大数组，以免克隆本身造成主线程卡顿。
- 预取消或处理中取消会返回 canceled Task；原生后台解码会先真正停止，再完成公开 Task，因此
  任务结束后才可安全复用输入数组。格式无效会 fault；创建失败时临时 Clip 会被清理。
- 成功返回的 `AudioClip` 归调用方所有。替换或不再使用时由调用方 `Destroy`；
  `ClearRecording()` 不会代替业务销毁这个 Clip。

### 方式二：从完整 WAV 创建按需解码 `AudioClip`

原生平台上，如果已经有完整 PCM16 WAV，只想播放且希望避免先分配整段 `float[]`：

```csharp
AudioClip clip = AudioRecorder.CreateStreamingAudioClipFromPcm16Wav(
    recording.Data,
    "Long Voice Stream");

audioSource.clip = clip;
audioSource.Play();
```

此方法同步解析很小的 RIFF 元数据并创建 Unity 对象，实际 PCM16 → float 在 Unity 请求音频数据
时按需完成。返回的 Clip 在使用期间继续读取传入的 `byte[]`，所以该数组不能被修改。它适合
播放；要求完整 `float[]`、`GetData()` 或离线算法的第三方应使用上面的异步普通 Clip。

Unity 2021.3 WebGL Player 不支持动态流式 `AudioClip.Create`，所以此方法在 WebGL Player 抛出
`PlatformNotSupportedException`。WebGL 需要 Unity Clip 时使用异步普通 Clip。

### 格式、异常与内存

支持的完整文件格式为：RIFF/WAVE、format tag `1`、PCM16 Little Endian、1～8 声道、有效
采样率。解析器支持 `fmt ` 不紧邻 RIFF 头、未知块、奇数字节块填充和大于 16 字节的标准 PCM
`fmt ` 块；截断、块越界、错误 `blockAlign`/`byteRate` 和不完整采样帧会被拒绝。

普通 Clip 需要同时持有输入 WAV、解码后的 float 数据和 Unity Clip 缓冲，长音频应评估内存。
按需解码 Clip 避免整段 float 展开，但会持续持有原 WAV 数组。若第三方只接受文件字节，直接
传 `RecordedAudio.Data`，不必先转 Clip。

### 最近一次成功结果

```csharp
RecordedAudio recording = AudioRecorder.LastRecording;
bool hasRecording = AudioRecorder.HasRecording;
```

失败或取消新会话不会自动清掉上一份成功的 `LastRecording`。如果业务必须确保结果属于当前
操作，应在本次 `RecordingCompleted` 参数中直接处理，或开始前主动清空旧结果。

## 实时 PCM 输出

实时输出必须同时满足两个条件：

1. 在开始录音前已经订阅 `AudioRecorder.AudioChunkReceived`。
2. 本次 `StartRecording` 传入 `streamAudio: true`。

```csharp
private void OnEnable()
{
    AudioRecorder.AudioChunkReceived += HandleAudioChunk;
    AudioRecorder.RecordingCanceled += HandleRecordingCanceled;
    AudioRecorder.RecordingFailed += HandleRecordingFailed;
}

private void OnDisable()
{
    AudioRecorder.AudioChunkReceived -= HandleAudioChunk;
    AudioRecorder.RecordingCanceled -= HandleRecordingCanceled;
    AudioRecorder.RecordingFailed -= HandleRecordingFailed;
}

public void StartRealtimeRecording()
{
    AudioRecorder.StartRecording(
        maxDurationSeconds: 60,
        streamAudio: true,
        streamChunkDurationMilliseconds: 40);
}

private static void HandleAudioChunk(AudioStreamChunk chunk)
{
    // 这里只做快速转交或入队，不执行耗时网络请求。
    if (chunk.Data.Length > 0)
    {
        RealtimeConsumer.EnqueuePcm(chunk.Data, chunk.Sequence, chunk.TimestampMilliseconds);
    }

    if (chunk.IsLast)
    {
        RealtimeConsumer.CompleteInput();
    }
}
```

上例中的 `RealtimeConsumer` 是业务或第三方包自己的适配器，不是 Audio Recorder 提供的类。
Audio Recorder 提供的正式输出接口是 `AudioChunkReceived(AudioStreamChunk)`。

### `AudioStreamChunk` 数据契约

| 属性 | 类型 | 内容 |
|---|---|---|
| `Data` | `byte[]` | 无 WAV 文件头的 PCM16 Little Endian；最终结束块允许为空 |
| `Sequence` | `int` | 从 0 开始递增的块序号 |
| `TimestampMilliseconds` | `int` | 此块第一帧相对本次录音开始的时间 |
| `SampleRate` | `int` | 当前固定为 `16000` |
| `Channels` | `int` | 当前固定为 `1` |
| `BitsPerSample` | `int` | 当前固定为 `16` |
| `IsFirst` | `bool` | 是否是此流交付的第一个块 |
| `IsLast` | `bool` | 是否是此流的最终结束块 |
| `DurationMilliseconds` | `int` | 根据数据长度和音频格式计算出的本块时长 |

`AudioStreamChunk` 构造函数也是公开的，方便第三方适配包和测试代码构造同一数据契约：

```csharp
new AudioStreamChunk(
    byte[] data,
    int sequence,
    int timestampMilliseconds,
    int sampleRate,
    int channels,
    int bitsPerSample,
    bool isFirst,
    bool isLast);
```

正常业务不需要自己构造录音块；只需消费 `AudioChunkReceived` 的参数。构造时 `data == null`
会被转换为空数组。

`streamChunkDurationMilliseconds` 是目标块时长，不保证每块精确相等。设备缓冲、Unity 帧、
浏览器音频调度和最后剩余数据都会造成差异。最后一块可能短于目标值，也可能是
`Data.Length == 0 && IsLast == true` 的纯结束标记。

`IsLast` 只表示实时 PCM 通道已经结束，不等同于完整录音成功。仍须等待
`RecordingCompleted`、`RecordingCanceled` 或 `RecordingFailed` 判断会话结果。如果录音在
真正开始采样前取消或失败，可能一个实时块都不会产生。

如果实时读取本身发生异常，包会记录错误并结束实时块通道；完整录音后端仍可能继续并最终
生成 WAV。因此第三方实时业务还应监听录音取消和失败事件，并为网络层设置自己的超时与错误
处理。

`AudioStreamChunk.Data` 也应视为只读。回调在 Unity 主线程执行，应快速入队或复制后返回；
不要在回调里等待 WebSocket、执行大块编码、磁盘 I/O 或复杂识别计算。

### 分块大小参考

当前格式每秒占用：

```text
16000 samples × 1 channel × 16 bit ÷ 8 = 32000 bytes/second
```

因此 40 ms 块通常约为：

```text
32000 × 0.04 = 1280 bytes
```

该值适合许多低延迟 PCM 消费场景，但第三方仍应以每个块实际 `Data.Length` 和格式字段为准，
不能假设每块永远恰好是 1280 字节。

### `LastStreamChunk` 与流式统计

```csharp
AudioStreamChunk lastChunk = AudioRecorder.LastStreamChunk;
int deliveredChunkCount = AudioRecorder.StreamChunkCount;
int deliveredPcmBytes = AudioRecorder.StreamedPcmByteCount;
bool isStreaming = AudioRecorder.IsStreamingActive;
```

- `StreamChunkCount` 包含可能为空的最终结束块。
- `StreamedPcmByteCount` 只统计 PCM 数据，不包含 WAV 文件头。
- `LastStreamChunk` 只保存最近一个块，不保存完整块列表。
- 新会话第一个块到来前，静态 `LastStreamChunk` 可能仍指向上一次流的最后一块；不要把它
  当成本次会话的开始通知，应使用事件和 `Sequence`。
- 如果业务要重组整段原始 PCM，必须在自己的消费者中按 `Sequence` 收集；注意长录音内存。

## 第三方包接入方式

### 在另一个 UPM 包中引用

如果消费包本身就是 Audio Recorder 的专用扩展，可以声明单向依赖：

消费包的 `package.json` 声明 Audio Recorder 依赖：

```json
{
  "dependencies": {
    "com.cowart.audio-recorder": "1.0.0"
  }
}
```

消费包的 Runtime `.asmdef` 引用公开程序集 `Cowart.AudioRecorder`：

```json
{
  "name": "YourCompany.YourAudioConsumer",
  "references": [
    "Cowart.AudioRecorder"
  ]
}
```

然后在 C# 中使用：

```csharp
using Cowart.AudioRecorder;
```

如果两个 UPM 包都必须能够单独安装、单独运行，则它们不应互相写入 `package.json` 或 asmdef
依赖。由最终项目的 `Assets` 组合层同时引用两个包，把 `RecordedAudio.Data`、
`AudioStreamChunk.Data` 或外部 `AudioClip` 传给消费包。这种方式不会把录音实现绑定进识别、
上传或音频处理包。

无论采用哪种方式，消费代码都只能使用公开 DTO 和静态门面，不能访问录音内部 Driver。需要
保存文件时，由最终项目另外安装 File Bridge 并在业务层组合。

### 一次性语音识别

适用于“录完一段，再整体生成文字”：

```csharp
private void HandleCompleted(RecordedAudio recording)
{
    thirdPartyConsumer.SubmitPcm16Wav(recording.Data);
}
```

第三方适配包应明确自己接受完整 WAV 还是裸 PCM。Audio Recorder 的完整出口是 WAV；不要把
WAV 文件头当成语音 PCM 发送给只接受裸 PCM 的接口。

消费方可以直接把 `recording.Data` 交给接受 PCM16 WAV 的第三方接口。第三方服务可能另有
时长、文件大小、采样率或调用频率限制；这些限制属于消费方，不能据此改变或误解本包的
`5`～`3600` 秒录音上限。

### 实时语音识别

适用于“边录边出文字”：

```text
AudioChunkReceived(chunk)
→ 校验 16000 Hz / mono / 16 bit
→ 将 chunk.Data 放入发送队列
→ WebSocket 就绪后按 Sequence 发送
→ chunk.IsLast 时发送厂商协议的最终帧
→ 同时监听 RecordingCanceled / RecordingFailed 清理识别会话
```

典型实时消费者会把 40 ms 左右的 `AudioStreamChunk` 快速入队，待网络连接就绪后按顺序发送，
并在 `IsLast` 时结束音频输入。连接、鉴权、协议帧、重试和结果拼接都属于消费包，不属于
Audio Recorder。

### 实时录制、实时播放

`PlayLastRecording()` 只能播放已经完成的 WAV。原生平台可用
`CreateRealtimeAudioClipStream()` 创建一个独立的 `Pcm16AudioClipStream`，把录音块持续写入
预分配环形缓冲，再由一个 Unity 流式 `AudioClip` 读取：

```text
AudioChunkReceived
→ PCM16 Little Endian 转 float [-1, 1]
→ 写入有容量上限的环形缓冲
→ AudioClip PCMReaderCallback 持续读取
→ AudioSource 播放
```

不要为每个 40 ms 块创建一个 `AudioClip` 并立刻 `AudioSource.Play`，否则容易出现点击声、
间隙和大量分配。一个 `Pcm16AudioClipStream` 只对应一次流，新一次录音必须创建新实例。

```csharp
using System;
using Cowart.AudioRecorder;
using UnityEngine;

public sealed class RealtimeVoiceMonitor : MonoBehaviour
{
    [SerializeField] private AudioSource audioSource;

    private Pcm16AudioClipStream liveStream;
    private bool playbackStarted;
    private float releaseAt = -1f;

    private void OnEnable()
    {
        AudioRecorder.AudioChunkReceived += HandleChunk;
        AudioRecorder.RecordingCompleted += HandleCompleted;
        AudioRecorder.RecordingCanceled += HandleCanceled;
        AudioRecorder.RecordingFailed += HandleFailed;
    }

    private void OnDisable()
    {
        AudioRecorder.AudioChunkReceived -= HandleChunk;
        AudioRecorder.RecordingCompleted -= HandleCompleted;
        AudioRecorder.RecordingCanceled -= HandleCanceled;
        AudioRecorder.RecordingFailed -= HandleFailed;
        AbortAndRelease();
    }

    public void StartMonitoring()
    {
        AbortAndRelease();

        if (!AudioRecorder.IsRealtimeAudioClipStreamingSupported)
        {
            Debug.LogWarning("当前平台不支持 Unity 实时流式 AudioClip。");
            return;
        }

        liveStream = AudioRecorder.CreateRealtimeAudioClipStream(
            bufferCapacityMilliseconds: 2000,
            prebufferMilliseconds: 120);
        audioSource.clip = liveStream.Clip;
        audioSource.loop = true;

        bool accepted = AudioRecorder.StartRecording(
            maxDurationSeconds: 60,
            streamAudio: true,
            streamChunkDurationMilliseconds: 40);
        if (!accepted && liveStream != null)
        {
            AbortAndRelease();
        }
    }

    private void Update()
    {
        if (liveStream == null)
        {
            return;
        }

        if (!playbackStarted && liveStream.IsReadyToPlay)
        {
            audioSource.Play();
            playbackStarted = true;
        }

        if (liveStream.IsDrained && releaseAt < 0f)
        {
            // IsDrained 表示数据已交给 Unity 音频回调，给 DSP 队列留出尾音时间。
            AudioSettings.GetDSPBufferSize(out int bufferFrames, out int bufferCount);
            float dspTail = bufferFrames * bufferCount /
                (float)AudioSettings.outputSampleRate;
            releaseAt = Time.unscaledTime + dspTail;
        }

        if (releaseAt >= 0f && Time.unscaledTime >= releaseAt)
        {
            Release();
        }
    }

    private void HandleChunk(AudioStreamChunk chunk)
    {
        if (liveStream != null && !liveStream.TryWrite(chunk))
        {
            Debug.LogWarning($"实时播放缓冲溢出，累计丢帧：{liveStream.DroppedFrames}");
        }
    }

    private void HandleCompleted(RecordedAudio recording)
    {
        _ = recording;
        liveStream?.CompleteInput();
    }

    private void HandleCanceled()
    {
        AbortAndRelease();
    }

    private void HandleFailed(string message)
    {
        Debug.LogError(message);
        AbortAndRelease();
    }

    private void AbortAndRelease()
    {
        audioSource.Stop();
        audioSource.clip = null;
        liveStream?.Abort();
        liveStream?.Dispose();
        liveStream = null;
        playbackStarted = false;
        releaseAt = -1f;
    }

    private void Release()
    {
        audioSource.Stop();
        audioSource.clip = null;
        liveStream?.Dispose();
        liveStream = null;
        playbackStarted = false;
        releaseAt = -1f;
    }
}
```

必须在 `StartRecording(streamAudio: true)` 前订阅 `AudioChunkReceived`。`IsLast` 块只结束
PCM 输入，不代表录音成功；取消或启动失败可能没有任何块，所以仍要监听
`RecordingCanceled`/`RecordingFailed` 并调用 `Abort()`。成功时也可幂等调用
`CompleteInput()`，让缓冲播放完后进入 `IsDrained`。

会话规则：

- 创建、`TryWrite`、`CompleteInput`、`Abort` 和 `Dispose` 都从 Unity 主线程调用。
- 环形缓冲在创建时一次性分配；音频线程回调不加锁、不记日志、不发业务事件、不分配数组。
- 默认容量 `2000 ms`、启动预缓冲 `120 ms`。预缓冲越小延迟越低，但更容易欠载。
- 缓冲不足时 `TryWrite` 整块拒绝并返回 `false`，`DroppedFrames` 累计丢帧；不会悄悄覆盖
  尚未播放的数据。
- 单次写入最多 `16384` 个采样帧；更大的第三方包必须在多个 Unity 帧中切片投递，避免一次
  主线程解码过多数据。对本包 16 kHz 输出，该上限覆盖最大 1000 ms 录音块。
- 环形缓冲最多分配 `2000000` 个 float sample values（约 8 MB）；高采样率、多声道组合可能
  在达到 10 秒时间上限前就被拒绝。
- 音频线程来不及拿到新数据时补静音，并累计 `UnderflowCount`/`UnderflowFrames`。
- `IsDrained` 表示最后 PCM 已从环形缓冲交给 Unity 音频回调，不保证扬声器已经播放完最后一个
  DSP 缓冲；立即 `Stop()` 可能截掉尾音。
- 一个会话只交给一个 `AudioSource`，必须设置 `loop = true`，不能 `PlayOneShot`、不能寻址，
  也不能把 `AudioSource.time/timeSamples` 当作真实流进度。
- 中止或释放前先停止并从 `AudioSource` 解绑 Clip，再 `Abort()`/`Dispose()`。会话销毁返回的 Clip；不要再次
  手动 `Destroy(liveStream.Clip)`。
- 最终数据块若因溢出返回 `false`，会话不会自动完成。块本身不大于 `CapacityFrames` 时可等
  空间释放后整块重试；块大于总容量时必须切片。内置录音监听应保证
  `bufferCapacityMilliseconds >= streamChunkDurationMilliseconds`，否则单块可能永远放不进去。
  不重试时可明确调用 `CompleteInput()`，接受丢失尾音后结束。

第三方实时 PCM 数据不必构造 `AudioStreamChunk`，格式与创建参数一致时可直接写：

```csharp
Pcm16AudioClipStream stream = AudioRecorder.CreateRealtimeAudioClipStream(
    sampleRate: 16000,
    channels: 1);

bool accepted = stream.TryWritePcm16(pcmPacket);
bool finalAccepted = stream.TryWritePcm16(lastPacket, isFinal: true);
```

`TryWritePcm16(byte[], int offset, int count, bool isFinal)` 还支持写数组切片。网络回调位于后台线程
时，应先把数据放入线程安全队列，再由 Unity 主线程取出并写入会话。

| `Pcm16AudioClipStream` 成员 | 说明 |
|---|---|
| `Clip` | 交给 `AudioSource.clip` 的 Unity 流式 Clip |
| `TryWrite(AudioStreamChunk)` | 直接消费本包实时块，校验采样率/声道/16-bit |
| `TryWritePcm16(...)` | 消费第三方无 WAV 头 PCM16 Little Endian 数据 |
| `CompleteInput()` | 正常结束输入，保留缓冲中的尾音 |
| `Abort()` | 异常/取消时标记中止并丢弃缓冲；调用方应先停止 AudioSource |
| `IsReadyToPlay` | 已达到预缓冲，或短流已结束且仍有数据 |
| `IsInputCompleted` / `IsDrained` | 输入已结束 / 缓冲已交给音频回调 |
| `BufferedFrames` / `BufferedMilliseconds` | 当前尚未读取的音频量 |
| `DroppedFrames` | 因容量不足整块拒绝的累计帧数 |
| `UnderflowCount` / `UnderflowFrames` | 播放期间数据不足的次数和补静音帧数 |

Unity 2021.3 WebGL Player 明确不支持动态流式 `AudioClip.Create`。该平台
`IsRealtimeAudioClipStreamingSupported == false`，两个流式 Clip 工厂抛
`PlatformNotSupportedException`；WebGL 的实时播放若后续实现，必须走 Web Audio/
AudioWorklet，得到的也不是 Unity `AudioClip`。WebGL 仍可使用实时 PCM 数据出口做识别/上传，
也可用异步普通 Clip 工厂在录音结束后生成非流式 Clip。

PCM16 转 Unity float 的核心换算是：

```csharp
short sample = (short)(pcm[index] | (pcm[index + 1] << 8));
float value = sample / 32768f;
```

麦克风与扬声器同时开启可能产生啸叫或让扬声器声音重新进入麦克风。包不做回声消除；真机
调试建议先使用耳机。

### 波形、电平和 VAD

- UI 电平条可直接轮询 `CurrentInputLevel` 和 `CurrentPeakLevel`，不必解析 PCM。
- 精确波形、VAD 或自定义音频算法应订阅 `AudioChunkReceived`。
- 算法耗时较长时，把块交给受控队列；不要阻塞 Unity 主线程。

## 播放最近录音

```csharp
bool playAccepted = AudioRecorder.PlayLastRecording();
bool stopAccepted = AudioRecorder.StopPlayback();
```

- `PlayLastRecording()` 从头播放最近一次完整录音；没有完整结果或平台拒绝播放时返回
  `false`。
- 开始新录音会先停止当前播放。
- WebGL 的 `PlayLastRecording()` 使用浏览器音频元素，不返回 Unity `AudioClip`，并应从真实
  按钮点击直接调用；如业务需要 Clip，可另行等待异步普通 Clip 工厂完成。
- WebGL 返回 `true` 只表示同步调用 `HTMLMediaElement.play()` 成功发起；浏览器稍后拒绝
  Promise 时只会写浏览器控制台，当前没有对应的 C# 播放失败事件。
- 当前没有播放完成/失败事件；播放 UI 可轮询 `IsPlaybackActive` 和播放进度属性。

## 通过 File Bridge 保存 WAV

Audio Recorder 本身不依赖 File Bridge，也不包含文件窗口。两个包都安装时，由项目 UI 在真实
“下载录音”按钮中显式组合：

```csharp
using Cowart.AudioRecorder;
using CrossPlatformFileBridge = Cowart.FileBridge.FileBridge;

private void OnEnable()
{
    CrossPlatformFileBridge.SaveCompleted += HandleSaveCompleted;
    CrossPlatformFileBridge.SaveCanceled += HandleSaveCanceled;
    CrossPlatformFileBridge.SaveFailed += HandleSaveFailed;
}

private void OnDisable()
{
    CrossPlatformFileBridge.SaveCompleted -= HandleSaveCompleted;
    CrossPlatformFileBridge.SaveCanceled -= HandleSaveCanceled;
    CrossPlatformFileBridge.SaveFailed -= HandleSaveFailed;
}

public void SaveFromButton()
{
    RecordedAudio recording = AudioRecorder.LastRecording;
    if (recording == null)
    {
        return;
    }

    bool requestAccepted = CrossPlatformFileBridge.SaveFile(
        recording.Data,
        recording.Name,
        recording.MimeType);
}
```

`FileBridge.SaveFile(...)` 的 `bool` 只表示保存请求是否被接受。最终保存结果不从 Audio Recorder
返回，而是：

- `FileBridge.SaveCompleted(string destination)`
- `FileBridge.SaveCanceled`
- `FileBridge.SaveFailed(string message)`

必须先订阅这三个事件再调用保存。某些平台的成功或失败事件可能在 `FileBridge.SaveFile(...)`
返回前同步触发；不要在返回后无条件覆盖事件回调已经设置的 UI。没有 `LastRecording` 时，项目
代码不应发起 File Bridge 请求。

完整录音和流式录音使用同一组合方式：开启 `streamAudio` 只增加实时 PCM 块出口，不会取消
最终 `RecordingCompleted` 的完整 WAV。应保存 `RecordedAudio.Data`，不要把单个
`AudioStreamChunk.Data` 当成 WAV。

`SaveCompleted(destination)` 的参数随平台变化：

| 平台 | `destination` 含义 |
|---|---|
| Windows / Editor | 实际绝对保存路径 |
| Android | Storage Access Framework 返回的 `content:` URI |
| WebGL | 建议下载文件名，不是用户磁盘上的最终路径 |

WebGL 下载必须由真实用户点击直接触发，浏览器不会把最终磁盘路径暴露给 Unity。正确做法是：
在 `RecordingCompleted` 中缓存 `RecordedAudio` 并启用下载按钮，再由下一次真实按钮点击调用
`FileBridge.SaveFile(...)`。更完整的 byte[]、filePath、URL、保存窗口、取消、错误和资源限制
说明请查看 File Bridge README。

## 清理资源

```csharp
bool cleared = AudioRecorder.ClearRecording();
```

- 录音进行中或结束处理中返回 `false`，不会破坏当前会话。
- 其他状态返回 `true`，停止播放并释放包持有的完整 WAV、播放资源、最近流式块和统计。
- `LastRecording` 会变为 `null`，`HasRecording` 会变为 `false`。
- 包无法清理业务或第三方已经保存的数组引用；业务仍需释放自己的缓存和队列。

长录音会同时涉及采集缓冲、完整 WAV、播放资源和托管数组。完成上传、识别或保存后，如果
不再需要播放最近录音，应及时调用 `ClearRecording()`。

## 状态与监控属性

### 会话状态

| 属性 | 类型 | 说明 |
|---|---|---|
| `IsAvailable` | `bool` | 当前编译平台是否有录音后端 |
| `IsRealtimeAudioClipStreamingSupported` | `bool` | 当前运行环境是否支持动态流式 Unity Clip；WebGL Player 为 `false` |
| `IsRecording` | `bool` | 等待开始、正在采样或正在生成 WAV 时都为 `true` |
| `IsFinalizing` | `bool` | 已停止采集，正在生成或读取最终 WAV |
| `IsStartPending` | `bool` | 正在等待 Android/WebGL 权限或后端开始 |
| `IsRecordingActive` | `bool` | 麦克风已经真正采样 |
| `IsStreamingActive` | `bool` | 当前会话正在产生实时 PCM 块 |
| `HasRecording` | `bool` | 是否持有最近一次成功的完整 WAV |
| `IsPlaybackActive` | `bool` | 最近录音是否正在播放 |

不要只用 `IsRecordingActive` 控制“停止”按钮；等待权限时它为 `false`，但
`IsRecording == true`，此时仍可调用 `StopRecording()` 取消请求。

### 录音与输入电平

| 属性 | 类型 | 说明 |
|---|---|---|
| `RecordingElapsedSeconds` | `float` | 当前已录时长；成功停止后保留最近完成时长，清理后归零 |
| `RecordingRemainingSeconds` | `float` | 距离本次上限的剩余秒数 |
| `RecordingProgress` | `float` | 相对本次上限的 `0`～`1` 进度 |
| `CurrentInputLevel` | `float` | 麦克风 RMS 电平映射到 `0`～`1` |
| `CurrentPeakLevel` | `float` | 带短暂保持和衰减的峰值，范围 `0`～`1` |

### 播放与流式统计

| 属性 | 类型 | 说明 |
|---|---|---|
| `PlaybackElapsedSeconds` | `float` | 当前实际播放位置 |
| `PlaybackDurationSeconds` | `float` | 当前播放资源实际总时长 |
| `PlaybackRemainingSeconds` | `float` | 当前播放剩余时长 |
| `PlaybackProgress` | `float` | 当前播放进度 `0`～`1` |
| `StreamChunkCount` | `int` | 本次已交付块数，包含可能为空的结束块 |
| `StreamedPcmByteCount` | `int` | 本次已交付 PCM 字节数，不含 WAV 文件头 |
| `LastRecording` | `RecordedAudio` | 最近一次成功的完整结果，尚无结果时为 `null` |
| `LastStreamChunk` | `AudioStreamChunk` | 最近一次交付的流式块，尚无块时为 `null` |

这些属性用于 UI 轮询，不是硬实时计量接口。建议在 `Update()` 中读取，不要在后台线程读取并
据此调用录音方法。

## 公开常量

| 常量 | 值 | 说明 |
|---|---:|---|
| `MinimumDurationSeconds` | `5` | 最小最长时长参数 |
| `DefaultMaximumDurationSeconds` | `300` | 无参调用使用的默认上限，即 5 分钟 |
| `MaximumDurationSeconds` | `3600` | 最大最长时长参数，即 60 分钟 |
| `MinimumStreamChunkMilliseconds` | `20` | 最小目标块时长 |
| `MaximumStreamChunkMilliseconds` | `1000` | 最大目标块时长 |
| `DefaultStreamChunkMilliseconds` | `40` | 默认目标块时长 |
| `OutputSampleRate` | `16000` | 输出采样率 |
| `OutputChannels` | `1` | 输出声道数 |
| `OutputBitsPerSample` | `16` | 输出位深 |
| `MinimumAudioClipDecodeFramesPerUpdate` | `256` | 普通 Clip 每帧最小写入帧数 |
| `MaximumAudioClipDecodeFramesPerUpdate` | `65536` | 普通 Clip 每帧最大写入帧数 |
| `DefaultAudioClipDecodeFramesPerUpdate` | `8192` | 普通 Clip 默认每帧写入帧数 |
| `MinimumRealtimeBufferMilliseconds` | `100` | 实时 Clip 最小环形缓冲容量 |
| `MaximumRealtimeBufferMilliseconds` | `10000` | 实时 Clip 最大环形缓冲容量 |
| `DefaultRealtimeBufferMilliseconds` | `2000` | 实时 Clip 默认环形缓冲容量 |
| `DefaultRealtimePrebufferMilliseconds` | `120` | 实时 Clip 默认启动预缓冲 |
| `MaximumRealtimeBufferSampleValues` | `2000000` | 环形缓冲最多约 8 MB float 数据 |
| `MaximumRealtimeWriteFrames` | `16384` | 单次实时 PCM 写入最大采样帧数 |

## 公开方法总览

| 方法 | 返回值含义 |
|---|---|
| `StartRecording(int, bool, int)` | 录音启动请求是否被接受 |
| `StopRecording()` | 当前录音是否接受停止请求 |
| `PlayLastRecording()` | 最近录音是否接受播放请求 |
| `StopPlayback()` | 是否存在并停止了当前播放 |
| `ClearRecording()` | 是否允许并完成包内录音资源清理 |
| `CreateAudioClipFromPcm16WavAsync(...)` | 异步返回由调用方持有的普通 `AudioClip` |
| `CreateStreamingAudioClipFromPcm16Wav(...)` | 原生平台返回按需解码的完整 WAV Clip |
| `CreateRealtimeAudioClipStream(...)` | 原生平台返回可持续写入 PCM16 的会话 |

这些 `bool` 都是“请求是否被接受/操作是否执行”的同步结果，不是对应异步业务的最终成功证明。
录音必须等待自己的终态事件；可选文件保存必须等待 File Bridge 的保存终态事件。

## UGUI 接入

静态方法不能直接拖到 Unity Inspector 的 `Button.onClick`。UGUI 场景只需要一个很薄的
界面适配脚本；它调用静态门面，不创建第二套录音 API：

```csharp
using Cowart.AudioRecorder;
using UnityEngine;

public sealed class AudioRecorderPanelAdapter : MonoBehaviour
{
    [SerializeField] private int maxDurationSeconds = 60;
    [SerializeField] private bool streamAudio = true;

    public void StartRecordingFromButton()
    {
        AudioRecorder.StartRecording(
            maxDurationSeconds,
            streamAudio,
            AudioRecorder.DefaultStreamChunkMilliseconds);
    }

    public void StopRecordingFromButton()
    {
        AudioRecorder.StopRecording();
    }

    public void PlayFromButton()
    {
        AudioRecorder.PlayLastRecording();
    }

    public void StopPlaybackFromButton()
    {
        AudioRecorder.StopPlayback();
    }

    public void ClearFromButton()
    {
        AudioRecorder.ClearRecording();
    }
}
```

按钮绑定的是 UI 适配器，录音的唯一业务入口仍然是 `AudioRecorder` 静态类。保存能力需要时，
按上一节在业务层另行组合 File Bridge。包内已经提供自包含的 `UGUI Usage` Sample；它默认不会
出现在项目 `Assets` 中，只有在 Package Manager 点击 Import 后才会生成。Sample 控制器也按前文
在 `OnEnable` / `OnDisable` 中订阅和退订静态结果事件，按钮适配方法本身不能代替结果出口。

## 可导入的 Samples

两个 Sample 都不会在安装包时自动复制到 `Assets`，也不会自动加入 Build Settings。只导入实际
需要的 Sample；需要构建时再把对应场景加入构建列表。

### Basic Usage

在 Package Manager 选择 **Audio Recorder > Samples > Basic Usage > Import**。Unity 默认导入到
`Assets\Samples\Audio Recorder\1.0.0\Basic Usage`，然后打开其中的 `Basic Usage.unity`。

该 Sample 使用零 Prefab、零 Inspector 引用的 IMGUI 界面，演示：

- 使用预设或滑杆设置 5～3600 秒录音上限，包括 5、30、60 分钟。
- 开始、停止和终态事件。
- 可选 40 ms 实时 PCM 输出及统计。
- 播放、停止播放和清空最近录音。
- 直接查看完整 WAV `RecordedAudio.Data`；Sample 本身不依赖 File Bridge。
- 异步把完整 WAV `byte[]` 转成普通 `AudioClip` 并播放。
- 原生平台从完整 WAV 创建按需解码 Clip，或把实时 PCM 写入一个流式 Clip。

它是 API 入门示例，不是产品 UGUI 样式模板，也不包含第三方服务凭据或配置。

### UGUI Usage

在 Package Manager 选择 **Audio Recorder > Samples > UGUI Usage > Import**。Unity 默认导入到
`Assets\Samples\Audio Recorder\1.0.0\UGUI Usage`，然后打开 `Audio Recorder UGUI.unity`。

该 Sample 是纯录音 UGUI 界面，使用 `UnityEngine.UI` 和内置动态字体，不依赖项目字体、TMP、
File Bridge 或语音识别包。它演示：

- 5、30、60 分钟预设，以及 5～3600 秒可调上限。
- 开始、手动停止、自动到时停止、WAV 生成中状态和单一终态出口。
- 输入电平、进度、已录/剩余时间和最近一次完整 WAV 信息。
- 可选实时 PCM16 块、录音播放、停止播放和清理包内结果。
- `RecordingCompleted(recording)` / `AudioRecorder.LastRecording` 提供完整 WAV `byte[]`。

`Inspect WAV` 只查看结果信息，不写文件。需要保存时，由业务代码把
`RecordedAudio.Data/Name/MimeType` 交给可选的文件包；这样两个 UPM 包仍可单独安装，也可组合使用。

## 平台说明

### Unity Editor / Windows

- 使用 Unity `Microphone` API 采集。
- 当前公开 API 使用系统默认麦克风，没有公开设备枚举或设备选择参数。
- 系统没有麦克风，或 Windows 隐私设置禁止应用访问麦克风时会失败。
- Editor Play Mode 使用原生 Unity 后端，不会执行 WebGL `.jslib`；WebGL 行为必须构建后在
  浏览器验证。

### Android 原生 APK

- 首次调用会请求 `RECORD_AUDIO` 运行时权限。
- 用户拒绝或选择“不再询问”时走 `RecordingFailed`；需要用户在系统设置中重新授权。
- 等待权限时调用 `StopRecording()` 会取消本次请求，迟到的授权结果不会重新启动旧会话。
- “Android”只指 Unity 原生 APK；Android WebGL 不在当前支持范围。

### WebGL

- 浏览器必须支持 `navigator.mediaDevices.getUserMedia`。
- 页面必须位于 HTTPS 或 localhost 安全上下文。
- `StartRecording()` 必须从真实按钮点击等用户手势中直接调用，不要先等待协程、Task 或
  延迟回调。
- `PlayLastRecording()` 应从真实按钮点击直接调用，以满足浏览器有声播放策略。若另外安装
  File Bridge，保存 WAV 也应由独立真实下载按钮直接调用 `FileBridge.SaveFile(...)`。
- WebGL 的包内最近录音播放使用浏览器音频元素；非流式 `byte[] → AudioClip` 由异步工厂
  分帧创建，但动态流式 Unity Clip 不受支持。
- 实时块时长和自动停止时间会受浏览器主线程及音频调度影响，不是硬实时音频线程回调。
- 采集阶段缓存目标采样率 PCM16 分块；停止时生成 WAV、浏览器播放 Blob 和 Unity
  `byte[]` 仍会产生短时内存副本。
- 当前 WebGL 支持范围是桌面浏览器。移动浏览器与 Android WebGL 不在当前承诺范围；不同桌面
  浏览器版本仍必须分别实测。Editor Play Mode 和单纯 WebGL 构建成功都不等于浏览器麦克风
  验收通过。

## 常见问题

### 为什么调用 `StartRecording()` 返回 `true`，还没有 `RecordingStarted`？

`true` 只表示后端接受请求。Android/WebGL 可能还在等待权限，检查 `IsStartPending` 并等待
`RecordingStarted` 或终态事件。

### 调用 `StopRecording()` 后会不会立刻触发 `RecordingCompleted`？

时序取决于后端。原生平台正常停止后会异步读取临时 WAV，`RecordingCompleted` 在停止方法返回后
触发；当前 WebGL 手动停止在同一调用链内生成并复制 WAV，成功时可能在停止方法返回前就触发
`RecordingCompleted`。等待权限时停止走 `RecordingCanceled`，失败走 `RecordingFailed`，这些
WebGL 终态也可能同步发生。始终在开始录音前订阅终态事件，不要依赖“返回前”或“返回后”；
原生收尾阶段可用 `IsFinalizing` 显示状态，而 WebGL 的该状态可能非常短暂。

### 为什么没有收到实时音频块？

检查以下条件：

1. 是否在开始前订阅 `AudioChunkReceived`。
2. 是否传入 `streamAudio: true`。
3. 是否已经收到 `RecordingStarted`，而不是仍在等待权限。
4. 是否在事件回调中抛异常或阻塞 Unity 主线程。

### 每个 `AudioStreamChunk` 是一个 WAV 文件吗？

不是。每个块都是无文件头的 PCM16 Little Endian。只有 `RecordedAudio.Data` 是完整 WAV。

### 能不能边录边播放？

原生平台可以。先创建 `Pcm16AudioClipStream`，再把 `AudioChunkReceived` 写入它，并在
`IsReadyToPlay` 后启动自己的 `AudioSource`。包提供 PCM 到流式 Clip 的有界环形缓冲，但不
替业务管理扬声器、防回授、UI 和 AudioSource 生命周期。WebGL Player 不支持这种 Unity
动态 Clip。

### `byte[]` 怎么转成 Unity `AudioClip`？

本包生成的完整 WAV 使用 `CreateAudioClipFromPcm16WavAsync(...)`。需要原生平台低内存按需
播放时可用 `CreateStreamingAudioClipFromPcm16Wav(...)`。前者适合第三方 API、`GetData()`
和随机访问；后者主要适合播放。两者只接受 PCM16 WAV，不解码 MP3/AAC。

### 异步转 Clip 会完全不卡主线程吗？

整段解析/解码不会在原生平台主线程执行，样本写入也按帧分批；WebGL 在多帧中协作执行。
但 Unity 对象只能在主线程创建，`AudioClip.Create` 的分配无法移到工作线程，所以不能对任意
设备和任意大文件承诺数学意义上的零尖峰。不要在主线程 `.Wait()`/`.Result`，并根据设备调小
`decodeFramesPerUpdate` 做实测。

### 能不能接第三方语音服务？

可以。一次性识别使用完整 WAV；实时识别使用 PCM 块。服务鉴权、连接、协议帧、重试和文字
结果属于独立适配包，不属于 Audio Recorder。

### 为什么新录音失败后 `LastRecording` 还存在？

它表示“最近一次成功结果”，失败和取消不会删除旧结果。处理本次结果应使用
`RecordingCompleted` 的参数；不再需要旧结果时调用 `ClearRecording()`。

### 可以从后台线程调用吗？

录音控制、播放、Clip 工厂以及实时 Clip 写入/控制方法必须从 Unity 主线程调用；所有事件也在
Unity 主线程交付。`RecordedAudio`、`AudioStreamChunk` 等结果对象的只读数据可以交给后台任务
处理，但调用方必须遵守其数组只读和所有权约定，并通过线程安全队列把 Unity API 操作切回主线程。

## 参数错误与返回 `false` 的常见情况

| 情况 | 结果 |
|---|---|
| `maxDurationSeconds < 5` 或 `> 3600` | `ArgumentOutOfRangeException` |
| 分块时长 `< 20` 或 `> 1000` | `ArgumentOutOfRangeException` |
| 从非 Unity 主线程调用公开方法 | `InvalidOperationException` |
| 空或非 PCM16 WAV 传给 Clip 工厂 | 参数异常、`InvalidDataException` 或 `NotSupportedException` |
| WebGL Player 创建动态流式 Clip | `PlatformNotSupportedException` |
| 实时 Clip 缓冲不足 | `TryWrite` 返回 `false` 并累计 `DroppedFrames` |
| 已有录音或正在生成 WAV 时再次开始 | 返回 `false` |
| 没有可停止录音或已经在结束时再次停止 | 返回 `false` |
| 没有完整录音时播放或保存 | 返回 `false` |
| 没有正在播放的录音时停止播放 | 返回 `false` |
| 录音进行中调用 `ClearRecording()` | 返回 `false` |

## 旧场景兼容

包内仍保留内部的 `[Obsolete]` `Cowart.WebGLBridge.CrossPlatformAudioRecorder` 兼容组件，
仅用于尚未迁移场景中的旧序列化 GUID 和 UnityEvent。它不是新的公开入口，也不应被新代码
引用。

兼容层程序集名仍是 `Cowart.WebGLBridge`。目标项目如果已经有另一个同名 asmdef，会发生
程序集重名冲突；安装前应先迁移旧场景，并决定保留哪一个兼容来源。后续正式发布时可考虑把
兼容层拆成可选包，核心录音 API 不依赖它。

新代码只使用：

```csharp
Cowart.AudioRecorder.AudioRecorder
```

如果旧场景仍绑定 `StartRecording`、`StopRecording`、`DownloadLastRecording` 等实例方法，
应逐步迁移到 UGUI 适配器，再由适配器调用静态门面。

## 包目录结构

```text
com.cowart.audio-recorder\
├─ Runtime\
│  ├─ AudioRecorder.cs                  唯一公开静态门面
│  ├─ RecordedAudio.cs                  完整 WAV 结果 DTO
│  ├─ AudioStreamChunk.cs               实时 PCM 块 DTO
│  ├─ Pcm16WavData.cs                   PCM16 WAV 校验与分块解码
│  ├─ Pcm16RecordingFile.cs             原生长录音临时 WAV 与环形游标
│  ├─ AudioClipCreationScheduler.cs      普通 Clip 异步/分帧创建器
│  ├─ Pcm16AudioClipStream.cs            实时 PCM 环形缓冲 Clip 会话
│  ├─ AudioRecorderDriver.cs            内部跨平台驱动
│  ├─ Compatibility\                   旧场景序列化兼容层
│  └─ Plugins\WebGL\                   浏览器录音后端
├─ Tests\Editor\                       EditMode 测试
├─ Samples~\Basic Usage\               可导入的 IMGUI API 示例
├─ Samples~\UGUI Usage\                可导入的纯录音 UGUI 场景和适配器
├─ package.json                         UPM 清单、依赖和 Sample 注册
├─ README.md                            本文档
├─ CHANGELOG.md                         版本变更
└─ LICENSE.md                           MIT 许可证
```

## 测试与真实环境门禁

在 Unity Test Runner 的 EditMode 中运行 `Cowart.AudioRecorder.Editor.Tests`，可验证结果 DTO、
PCM/WAV 编码与解析、重采样、普通 Clip 异步创建、按需解码、实时环形缓冲、临时 WAV 增量写入、
麦克风环形游标回绕与覆盖保护、流式块计算、事件隔离、参数范围和录音会话单终态状态机。

本地验证记录（2026-08-15，Unity 2021.3.30f1）：

- 当前工程完整 EditMode 测试 `143/143` 通过；Audio Recorder 专项测试 `42/42` 通过。
- 在系统临时目录创建了只安装 `com.cowart.audio-recorder` 的空白工程；包解析、运行时程序集和
  调用公开 API 的 `Assembly-CSharp.dll` 均编译成功，依赖锁中没有 File Bridge。`Basic Usage` 和
  `UGUI Usage` 都通过 Package Manager Sample API 实际导入到 `Assets\Samples`。
- 导入后的 UGUI 场景已检查 9 个 Button、3 个 Slider、1 个 Toggle、全部控制器引用和 0 个
  Missing Script；场景依赖中没有项目 `_Utility`、File Bridge、语音识别包或 TMP。
- 只安装 Audio Recorder 的空白工程使用导入后的 `Audio Recorder UGUI.unity` 完成 WebGL Player
  构建：2,962,183 bytes、131.6 秒、0 error。

这些是源码、包解析、Sample 导入、EditMode 和构建证据；默认 Build Settings、真实麦克风与设备
行为仍是不同门禁。

EditMode 测试通过不等于以下真实环境已经通过：

- 真实 Windows 麦克风及系统隐私权限。
- Android 真机权限允许、拒绝和“不再询问”。
- HTTPS WebGL 页面上的浏览器授权、录音、播放和下载。
- 30/60 分钟录音在目标设备上的磁盘空间、结束耗时、内存和性能。
- 长 WAV 转普通/按需解码 Clip 的目标设备卡顿、内存和销毁时机。
- Windows/Android 实时 Clip 的扬声器延迟、欠载、溢出、尾音和麦克风回授。
- 第三方服务的账号、网络、鉴权、配额和协议行为。

发布前应分别完成源码/测试、Unity 构建、真实麦克风、Android 真机和目标浏览器验收，不要把
其中一项通过描述成全部平台已经通过。

`package.json` 当前发布版本为 `1.0.0`。空白项目单包安装编译已经通过；真实设备和目标浏览器
门禁仍需在对应环境中独立完成。
