# Changelog

## [1.0.1] - 2026-08-23

- 新增包专用图标，并将包内组件脚本的默认图标统一替换为该图标。

## [1.0.0] - 2026-08-15

- 录音上限从 300 秒扩展为 3600 秒；新增 `DefaultMaximumDurationSeconds = 300`，无参调用仍保持
  5 分钟，显式传入 `1800`/`3600` 可录制 30/60 分钟。
- 原生录音改为固定 10 秒循环麦克风缓冲并增量写入临时 WAV，停止后异步读取最终 `byte[]`；
  不再预分配整段长录音 Float Clip，也不在完成时展开整段播放 Clip。
- WebGL PCM 改为约 1 MiB pages，结束时直接以 WAV 头和 pages 创建 Blob/回调数据，移除
  `chunks → merged → WAV` 的额外整段副本。
- 新增 `IsFinalizing` 状态；UGUI 时长滑杆扩展到 60 分钟并显示结束处理状态，Basic Usage
  增加 5/30/60 分钟预设与结束处理提示。
- 新增唯一公开静态门面 `Cowart.AudioRecorder.AudioRecorder`。
- 录音完成、取消、失败与实时音频块统一改为静态 C# 事件出口。
- 新增只读 `RecordedAudio` 结果对象，直接交付完整 WAV `byte[]`。
- 平台驱动改为内部自动创建，业务场景不再挂载录音组件。
- 新增导入即可运行的 Basic Usage 与纯录音 UGUI Usage 示例场景，以及 Editor 测试程序集。
- 新增录音会话代次与单终态保护，允许终态事件中安全启动下一次录音。
- WebGL 权限等待可立即取消；迟到的权限 Promise、旧计时器和旧音频回调不再影响新会话。
- WebGL 采集期改为缓存目标采样率 PCM16 分块，降低长录音内存峰值。
- 销毁内部驱动时无回调中止采集，不再从销毁栈发布完成或取消事件。
- 新增 PCM16 WAV `byte[]` 异步生成普通 Unity `AudioClip` 的分帧 API；原生平台后台解码，
  WebGL Player 按帧协作解码。
- 新增完整 PCM16 WAV 的原生按需解码 Clip，以及可消费 `AudioStreamChunk`/第三方 PCM16
  数据的有界实时环形缓冲 `Pcm16AudioClipStream`。
- 明确 Unity 2021.3 WebGL Player 不支持动态流式 AudioClip，流式 Clip 工厂会报告平台不支持。
- 移除核心程序集与 UPM 清单对 File Bridge 的硬依赖，使 Audio Recorder 可单独安装。
- 移除 `AudioRecorder.SaveLastRecording()`；需要保存时由业务把
  `RecordedAudio.Data/Name/MimeType` 显式交给可选的 `FileBridge.SaveFile(...)`。
- Basic Usage 和 UGUI Usage 都不引用 File Bridge；完整 WAV 由 Sample 展示，保存能力由业务层可选组合。

## [0.1.0] - 2026-08-12

- 从项目内 WebGLBridge 拆出跨平台录音、播放、WAV 编码和实时 PCM 分块功能。
- 保留原程序集名、公开 API、命名空间和 Unity `.meta` GUID，兼容现有场景事件。
