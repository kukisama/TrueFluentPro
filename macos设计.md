# 译见 Pro macOS 版设计方案

## 一、可行性总结

**结论：macOS 版完全可行，且有明确的落地路径。**

项目基于 **Avalonia 11 + .NET 10** 构建，Avalonia 本身就是跨平台 UI 框架，原生支持 macOS（x64 + Apple Silicon ARM64）。经过代码审计，项目中约 **65% 的代码**（UI 层、ViewModel 层、AI 服务、配置管理）无需修改即可在 macOS 上运行。需要重点处理的是 **音频采集层**（NAudio/WASAPI → macOS CoreAudio）和少量 **平台 P/Invoke**（剪贴板、文件管理器集成）。

---

## 二、现有架构兼容性评估

### 2.1 各模块 macOS 兼容性一览

| 模块 | 文件 | 现状 | macOS 兼容 | 工作量 |
|------|------|------|-----------|--------|
| **UI 框架** | `*.axaml` / `*.axaml.cs` | Avalonia 11.3.11 | ✅ 无需修改 | — |
| **MVVM 层** | `ViewModels/` 全部 | CommunityToolkit.MVVM | ✅ 无需修改 | — |
| **AI 图像生成** | `AiImageGenService.cs` | HTTP API 调用 | ✅ 无需修改 | — |
| **AI 视频生成** | `AiVideoGenService.cs` | HTTP API 调用 | ✅ 无需修改 | — |
| **AI 洞察分析** | `AiInsightService.cs` | HTTP API 调用 | ✅ 无需修改 | — |
| **Azure Blob 存储** | `BlobStorageService.cs` | Azure SDK | ✅ 无需修改 | — |
| **配置管理** | `ConfigurationService.cs` | JSON 本地文件 | ✅ 无需修改 | — |
| **路径管理** | `PathManager.cs` | 已含 macOS 分支 | ✅ 已支持 | — |
| **崩溃日志** | `CrashLogger.cs` | 标准 .NET API | ✅ 无需修改 | — |
| **悬浮字幕窗** | `FloatingSubtitleWindow` | Avalonia Window | ✅ 基本兼容 | 小 |
| **悬浮洞察窗** | `FloatingInsightWindow` | Avalonia Window | ✅ 基本兼容 | 小 |
| **Markdown 渲染** | Markdown.Avalonia | 纯 .NET | ✅ 无需修改 | — |
| **音频设备枚举** | `AudioDeviceEnumerator.cs` | NAudio WASAPI | ❌ 需重写 | 中 |
| **麦克风采集** | `WasapiPcm16AudioSource.cs` | WASAPI Capture | ❌ 需重写 | 高 |
| **系统声音回环** | `HighQualityRecorder.cs` | WASAPI Loopback | ❌ 需重写 | 高 |
| **音频重采样** | `AudioFormatConverter.cs` | Media Foundation | ❌ 需替换 | 中 |
| **WAV→MP3 转码** | `WavToMp3Transcoder.cs` | Media Foundation | ❌ 需替换 | 中 |
| **视频帧提取** | `VideoFrameExtractorService.cs` | WinRT Media API | ❌ 需替换 | 中 |
| **剪贴板图片** | `MediaStudioView.axaml.cs` | GDI+ P/Invoke | ❌ 需替换 | 中 |
| **剪贴板图片** | `ImagePreviewWindow.axaml.cs` | GDI+ P/Invoke | ❌ 需替换 | 中 |
| **自动更新** | `UpdateService.cs` | Updater.exe | ⚠️ 需适配 | 小 |
| **语音翻译服务** | `SpeechTranslationService.cs` | Azure Speech SDK | ⚠️ SDK 跨平台，音频源需适配 | 中 |
| **批量转录** | `RealtimeSpeechTranscriber.cs` | Media Foundation 重采样 | ⚠️ 需替换重采样器 | 中 |

### 2.2 已有的跨平台基础

项目已经具备一定的跨平台意识：

1. **PathManager.cs** 已包含 `OperatingSystem.IsMacOS()` 分支，macOS 下使用 `~/Library/Application Support/TrueFluentPro/`
2. **SpeechTranslationService.cs** 已有非 Windows 回退路径：`AudioConfig.FromDefaultMicrophoneInput()`（仅使用默认麦克风）
3. **AudioDeviceEnumerator.cs** 在非 Windows 平台返回空列表而非崩溃
4. **Azure Speech SDK 1.48.1** 官方支持 macOS（x64 + ARM64，macOS 10.14+ / 11.0+）

---

## 三、核心技术挑战与解决方案

### 3.1 音频采集（最大挑战）

#### 现状分析

当前音频管线完全依赖 Windows WASAPI：

```
[Windows 当前架构]
用户声音 → WasapiCapture (麦克风) ─┐
                                    ├→ MixingSampleProvider → PushAudioInputStream → Azure Speech SDK
系统声音 → WasapiLoopbackCapture ──┘
                                    └→ HighQualityRecorder (MP3 录音)
```

#### macOS 解决方案：三层策略

**方案 A：麦克风采集 — 使用 PortAudio 跨平台绑定**

推荐使用 **PortAudioSharp**（PortAudio 的 .NET 绑定），原因：
- PortAudio 是经过 20+ 年验证的跨平台音频 I/O 库
- 在 macOS 上使用 CoreAudio 后端，性能优秀
- 支持设备枚举、多声道、自定义采样率
- 已有成熟的 NuGet 包 `PortAudioSharp2`

```
[macOS 架构 - 麦克风]
用户声音 → PortAudio (CoreAudio 后端) → PCM16 Buffer → PushAudioInputStream → Azure Speech SDK
```

**方案 B：系统声音回环 — macOS 14.2+ CoreAudio Process Tap**

macOS 14.2（Sonoma）起，Apple 提供了官方的系统音频捕获 API：
- `AudioHardwareCreateProcessTap` — 无需虚拟音频驱动
- 可通过 Swift/ObjC 原生库封装，从 .NET 通过 P/Invoke 调用

对于 macOS 12.3-14.1，使用 **ScreenCaptureKit** 的纯音频模式作为后备：
- `SCStream` 支持仅捕获音频（不含视频）
- 可指定捕获特定应用或整个系统的音频

```
[macOS 架构 - 系统声音]
macOS 14.2+: CoreAudio Process Tap → PCM Buffer → PushAudioInputStream
macOS 12.3+: ScreenCaptureKit (audio-only) → CMSampleBuffer → PCM → Push
macOS <12.3: 提示用户安装 BlackHole 虚拟音频驱动
```

**方案 C：降级方案 — 仅默认麦克风**

最低可行版本可以直接使用 Azure Speech SDK 内置的 `AudioConfig.FromDefaultMicrophoneInput()`，这在当前代码中已经作为非 Windows 的回退路径存在（`SpeechTranslationService.cs` 第 732-740 行）。

#### 推荐的音频抽象接口

```csharp
// 新增跨平台音频接口
public interface IAudioCaptureSource : IDisposable
{
    event EventHandler<AudioDataEventArgs> DataAvailable;
    event EventHandler<float> PeakLevelChanged;
    
    void Start();
    void Stop();
    
    WaveFormat OutputFormat { get; }
    bool IsCapturing { get; }
}

public interface IAudioDeviceEnumerator
{
    IReadOnlyList<AudioDeviceInfo> GetInputDevices();
    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();
    AudioDeviceInfo? GetDefaultDevice(bool isInput);
}

// 平台实现
// Windows: WasapiAudioCaptureSource (已有代码重构)
// macOS:   PortAudioCaptureSource (新增)
//          CoreAudioLoopbackSource (新增, macOS 14.2+)
//          ScreenCaptureKitAudioSource (新增, macOS 12.3+)
```

### 3.2 音频格式转换

#### 现状

- `AudioFormatConverter.cs` 使用 `MediaFoundationResampler`（Windows Media Foundation）
- `WavToMp3Transcoder.cs` 使用 `MediaFoundationEncoder`

#### macOS 解决方案

| 功能 | Windows | macOS 替代 | NuGet 包 |
|------|---------|------------|----------|
| PCM 重采样 | MediaFoundationResampler | 纯 .NET 实现或 FFmpeg | `NWaves` 或 `FFMpegCore` |
| WAV→MP3 | MediaFoundationEncoder | LAME 编码器 | `NAudio.Lame`（需验证 macOS 兼容性）或 `FFMpegCore` |

**推荐方案：使用 FFmpeg 作为统一后端**

```csharp
// 跨平台音频转换接口
public interface IAudioTranscoder
{
    Task<string> ConvertToMp3Async(string inputPath, int bitrate = 128);
    Task<byte[]> ResampleToPcm16kMonoAsync(byte[] input, int inputSampleRate, int inputChannels);
}

// Windows 实现: MediaFoundationTranscoder (保持现有代码)
// macOS 实现: FFmpegTranscoder (调用 ffmpeg CLI 或 FFMpegCore 库)
```

macOS 上 FFmpeg 可通过 Homebrew 安装，或随应用捆绑。`FFMpegCore` NuGet 包提供了跨平台的 .NET 封装。

### 3.3 视频帧提取

#### 现状

`VideoFrameExtractorService.cs` 使用 `Windows.Media.Editing.MediaClip`（WinRT API）。

#### macOS 解决方案

使用 **FFmpeg** 提取关键帧，保持跨平台一致性：

```csharp
// macOS 实现
public class FFmpegFrameExtractor : IVideoFrameExtractor
{
    public async Task<Bitmap?> ExtractFirstFrameAsync(string videoPath)
    {
        // ffmpeg -i input.mp4 -vframes 1 -f image2pipe -vcodec png pipe:1
        var result = await FFMpeg.SnapshotAsync(videoPath, TimeSpan.Zero);
        return new Bitmap(new MemoryStream(result));
    }
}
```

### 3.4 剪贴板图片操作

#### 现状

`MediaStudioView.axaml.cs` 和 `ImagePreviewWindow.axaml.cs` 使用大量 Windows P/Invoke：
- `user32.dll`: OpenClipboard, CloseClipboard, GetClipboardData
- `gdiplus.dll`: GdiplusStartup, GdipCreateBitmapFromHBITMAP, GdipSaveImageToFile
- 处理 `CF_DIB`（Windows 设备无关位图）格式

#### macOS 解决方案

**使用 Avalonia 内置剪贴板 API**（推荐，最简方案）：

Avalonia 11 的 `IClipboard` 接口已经支持跨平台的图片剪贴板操作：

```csharp
// 跨平台剪贴板读取
var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
var formats = await clipboard.GetFormatsAsync();
if (formats.Contains("image/png"))
{
    var data = await clipboard.GetDataAsync("image/png");
    // 转换为 Avalonia Bitmap
}

// 跨平台剪贴板写入
var dataObject = new DataObject();
dataObject.Set("image/png", pngBytes);
await clipboard.SetDataObjectAsync(dataObject);
```

如果 Avalonia 内置 API 不满足需求（如特定格式支持），macOS 端可以通过少量 ObjC interop 使用 `NSPasteboard`：

```csharp
// macOS 原生剪贴板 (备选方案)
[DllImport("/System/Library/Frameworks/AppKit.framework/AppKit")]
static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
// 通过 NSPasteboard 读取 NSPasteboardTypePNG / NSPasteboardTypeTIFF
```

### 3.5 自动更新机制

#### 现状

`UpdateService.cs` 从 GitHub Releases 下载更新包，调用 `Updater.exe` 完成文件替换。

#### macOS 解决方案

| 方面 | Windows | macOS |
|------|---------|-------|
| 更新包格式 | `.zip` (win-x64/win-arm64) | `.zip` 或 `.dmg` (osx-x64/osx-arm64) |
| 更新器 | `Updater.exe` | Shell 脚本或独立 `Updater` 可执行文件 |
| 安装位置 | 用户自选 | `/Applications/译见 Pro.app` |
| RID | win-x64 / win-arm64 | osx-x64 / osx-arm64 |

```csharp
// UpdateService.cs 修改
private string GetRuntimeIdentifier()
{
    if (OperatingSystem.IsMacOS())
    {
        return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "osx-arm64" : "osx-x64";
    }
    // 现有 Windows 逻辑...
}
```

### 3.6 文件管理器集成

#### 现状

`ImagePreviewWindow.axaml.cs` 使用 `explorer.exe /select,{path}` 打开文件位置。

#### macOS 解决方案

```csharp
if (OperatingSystem.IsMacOS())
{
    Process.Start("open", $"-R \"{filePath}\"");  // Finder 中显示文件
}
else if (OperatingSystem.IsWindows())
{
    Process.Start("explorer.exe", $"/select,\"{filePath}\"");
}
```

---

## 四、项目结构调整

### 4.1 推荐的平台抽象层结构

```
TrueFluentPro/
├── Services/
│   ├── Audio/
│   │   ├── IAudioCaptureSource.cs          // 新增：跨平台接口
│   │   ├── IAudioDeviceEnumerator.cs       // 新增：跨平台接口
│   │   ├── IAudioTranscoder.cs             // 新增：跨平台接口
│   │   ├── AutoGainProcessor.cs            // 不变：纯算法
│   │   ├── WavChunkRecorder.cs             // 不变：已跨平台
│   │   ├── Windows/                        // 新增目录
│   │   │   ├── WasapiCaptureSource.cs      // 重构自 WasapiPcm16AudioSource.cs
│   │   │   ├── WasapiLoopbackSource.cs     // 重构自 HighQualityRecorder.cs
│   │   │   ├── WinAudioDeviceEnumerator.cs // 重构自 AudioDeviceEnumerator.cs
│   │   │   └── MediaFoundationTranscoder.cs// 重构自 WavToMp3Transcoder.cs
│   │   └── Mac/                            // 新增目录
│   │       ├── PortAudioCaptureSource.cs   // 新增：PortAudio 麦克风
│   │       ├── CoreAudioLoopbackSource.cs  // 新增：系统声音回环
│   │       ├── MacAudioDeviceEnumerator.cs // 新增：设备枚举
│   │       └── FFmpegTranscoder.cs         // 新增：FFmpeg 转码
│   ├── Platform/
│   │   ├── IPlatformServices.cs            // 新增：平台服务接口
│   │   ├── IClipboardImageService.cs       // 新增：剪贴板图片接口
│   │   ├── IVideoFrameExtractor.cs         // 新增：视频帧提取接口
│   │   ├── Windows/
│   │   │   ├── WindowsPlatformServices.cs
│   │   │   ├── WindowsClipboardImage.cs
│   │   │   └── WinRTFrameExtractor.cs
│   │   └── Mac/
│   │       ├── MacPlatformServices.cs
│   │       ├── MacClipboardImage.cs
│   │       └── FFmpegFrameExtractor.cs
```

### 4.2 依赖注入注册修改

```csharp
// App.axaml.cs — 平台感知的服务注册
private void RegisterPlatformServices(IServiceCollection services)
{
    if (OperatingSystem.IsWindows())
    {
        services.AddSingleton<IAudioDeviceEnumerator, WinAudioDeviceEnumerator>();
        services.AddSingleton<IAudioTranscoder, MediaFoundationTranscoder>();
        services.AddSingleton<IClipboardImageService, WindowsClipboardImage>();
        services.AddSingleton<IVideoFrameExtractor, WinRTFrameExtractor>();
    }
    else if (OperatingSystem.IsMacOS())
    {
        services.AddSingleton<IAudioDeviceEnumerator, MacAudioDeviceEnumerator>();
        services.AddSingleton<IAudioTranscoder, FFmpegTranscoder>();
        services.AddSingleton<IClipboardImageService, MacClipboardImage>();
        services.AddSingleton<IVideoFrameExtractor, FFmpegFrameExtractor>();
    }
}
```

### 4.3 .csproj 条件编译调整

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <!-- 移除 Windows 专属清单 -->
    <ApplicationManifest Condition="$([MSBuild]::IsOSPlatform('Windows'))">app.manifest</ApplicationManifest>
  </PropertyGroup>

  <!-- 跨平台依赖 -->
  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.3.11" />
    <PackageReference Include="Avalonia.Desktop" Version="11.3.11" />
    <!-- ... 其他跨平台包 ... -->
    <PackageReference Include="Microsoft.CognitiveServices.Speech" Version="1.48.1" />
    <PackageReference Include="FFMpegCore" Version="5.*" />
    <PackageReference Include="PortAudioSharp2" Version="*" />
  </ItemGroup>

  <!-- Windows 专属依赖 -->
  <ItemGroup Condition="$([MSBuild]::IsOSPlatform('Windows'))">
    <PackageReference Include="NAudio" Version="2.2.1" />
    <PackageReference Include="NAudio.Lame" Version="2.1.0" />
    <PackageReference Include="Microsoft.Windows.SDK.NET" Version="10.0.18362.6-preview" />
  </ItemGroup>

  <!-- macOS 专属依赖 -->
  <ItemGroup Condition="$([MSBuild]::IsOSPlatform('OSX'))">
    <!-- macOS 原生绑定库（如需要） -->
  </ItemGroup>
</Project>
```

---

## 五、macOS 特有适配项

### 5.1 应用打包与分发

| 项目 | 说明 |
|------|------|
| **App Bundle** | 需创建标准 `.app` 目录结构（`Contents/MacOS/`, `Contents/Resources/`, `Info.plist`） |
| **图标** | 需从 `AppIcon.png` 生成 `.icns` 格式（使用 `iconutil` 或项目已有的 `IconTool`） |
| **代码签名** | 需要 Apple Developer ID Application 证书 |
| **公证** | macOS 10.15+ 强制要求 notarization（通过 `notarytool` 或 Avalonia Parcel 工具） |
| **分发格式** | `.dmg` 磁盘映像（拖拽安装） 或 `.zip` 直接下载 |
| **工具链** | 推荐使用 **Avalonia Parcel**（支持从 Windows/Linux/macOS 交叉打包、签名、公证） |

#### 打包命令示例

```bash
# 发布 macOS 版本
dotnet publish -c Release -r osx-arm64 --self-contained true

# 使用 Parcel 创建 .app 并签名
parcel pack --input bin/Release/net10.0/osx-arm64/publish \
  --output TrueFluentPro.app \
  --bundle-id com.kukisama.truefluentpro \
  --icon Assets/AppIcon.icns

parcel sign --identity "Developer ID Application: ..." --input TrueFluentPro.app
parcel notarize --apple-id "..." --team-id "..." --input TrueFluentPro.app
```

### 5.2 macOS 权限声明 (entitlements)

macOS 应用需要在 `Info.plist` 和 entitlements 文件中声明以下权限：

```xml
<!-- Info.plist 必要条目 -->
<key>NSMicrophoneUsageDescription</key>
<string>译见 Pro 需要访问麦克风以进行实时语音识别和翻译</string>

<key>NSScreenCaptureUsageDescription</key>
<string>译见 Pro 需要屏幕录制权限以捕获系统音频（用于翻译系统播放的音频）</string>
```

```xml
<!-- entitlements.plist -->
<key>com.apple.security.device.audio-input</key>
<true/>
<key>com.apple.security.cs.allow-jit</key>
<true/>  <!-- .NET JIT 编译需要 -->
<key>com.apple.security.cs.allow-unsigned-executable-memory</key>
<true/>  <!-- .NET 运行时需要 -->
```

### 5.3 macOS 系统集成

| 功能 | 实现方式 |
|------|----------|
| **菜单栏** | Avalonia 的 `NativeMenu` 自动适配 macOS 菜单栏 |
| **Dock 图标** | .app Bundle 自动显示 |
| **暗色模式** | Avalonia `FluentTheme` 自动跟随系统主题 |
| **触控板手势** | Avalonia 自动处理双指滚动、缩放 |
| **全屏** | Avalonia Window 支持 macOS 原生全屏 |
| **键盘快捷键** | 需将 Ctrl 映射为 Cmd（使用 `ControlOrMeta` 修饰符） |

#### 快捷键适配

```csharp
// MainWindow.axaml.cs — 修改 OnKeyDown
// 将 Ctrl+1/2/3/4 改为使用 ControlOrMeta 判断
protected override void OnKeyDown(KeyEventArgs e)
{
    var mod = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
    
    if (e.KeyModifiers.HasFlag(mod))
    {
        switch (e.Key)
        {
            case Key.D1: ShowPage(NavTagLive); break;
            case Key.D2: ShowPage(NavTagReview); break;
            // ...
        }
    }
}
```

---

## 六、分阶段实施计划

### Phase 0：基础验证（1 周）

> **目标**：验证项目在 macOS 上编译运行，确认 UI 渲染正常

- [ ] 在 macOS 上安装 .NET 10 SDK
- [ ] 修改 `.csproj` 使条件编译通过（移除 Windows 强制依赖）
- [ ] 解决编译错误（预计 20-30 个平台相关编译错误）
- [ ] 验证 UI 层渲染（Avalonia 窗口、主题、导航）
- [ ] 验证 Azure AI 服务调用（图像生成、视频生成、洞察分析）
- [ ] 验证配置管理和文件存储
- [ ] **产出**：macOS 上可运行的空壳版本（音频和媒体功能禁用）

**交付标准**：应用启动 → 导航切换 → 设置页可用 → AI 服务调通

### Phase 1：核心音频能力（3-4 周）

> **目标**：实现 macOS 麦克风采集 + 语音识别翻译

- [ ] 定义 `IAudioCaptureSource` / `IAudioDeviceEnumerator` 跨平台接口
- [ ] 重构 Windows 现有代码实现新接口（不改变现有功能）
- [ ] 实现 macOS 麦克风采集（PortAudio 或 Azure SDK 默认麦克风模式）
- [ ] 实现 macOS 音频设备枚举
- [ ] 实现跨平台音频重采样（替换 MediaFoundationResampler）
- [ ] 对接 Azure Speech SDK 的 `PushAudioInputStream`
- [ ] 测试实时语音翻译基本流程
- [ ] **产出**：macOS 版支持麦克风实时翻译

**交付标准**：选择麦克风 → 开始翻译 → 实时显示字幕 → 停止翻译

### Phase 2：系统音频与录音（2-3 周）

> **目标**：实现系统声音回环采集和本地录音

- [ ] 实现 CoreAudio Process Tap 回环采集（macOS 14.2+）
- [ ] 实现 ScreenCaptureKit 回环采集（macOS 12.3+ 后备方案）
- [ ] 实现混合模式（麦克风 + 系统声音）
- [ ] 实现 MP3 录音功能（FFmpeg 或跨平台 LAME 编码）
- [ ] 实现音频电平实时监测
- [ ] 添加 macOS 音频权限请求流程
- [ ] **产出**：macOS 版完整音频功能

**交付标准**：系统声音回环 → 混合录音 → MP3 导出

### Phase 3：媒体与平台集成（1-2 周）

> **目标**：补全媒体功能和平台特有集成

- [ ] 替换剪贴板图片操作（GDI+ → Avalonia API 或 NSPasteboard）
- [ ] 实现视频帧提取（FFmpeg 替换 WinRT MediaClip）
- [ ] 适配文件管理器集成（`open -R` 替换 `explorer.exe /select`）
- [ ] 适配快捷键（Ctrl → Cmd）
- [ ] 适配自动更新机制
- [ ] **产出**：macOS 版功能齐全

### Phase 4：打包发布（1 周）

> **目标**：完成 macOS 应用的打包、签名、分发流程

- [ ] 创建 `.app` Bundle 和 `Info.plist`
- [ ] 生成 `.icns` 图标
- [ ] 配置代码签名和公证
- [ ] 创建 `.dmg` 安装镜像
- [ ] 配置 GitHub Actions CI/CD（macOS 构建、签名、发布）
- [ ] 在 GitHub Releases 中添加 macOS 下载链接
- [ ] **产出**：可分发的 macOS 安装包

### Phase 5：测试与优化（1-2 周）

> **目标**：macOS 版质量保证

- [ ] 在 Intel Mac 和 Apple Silicon Mac 上测试
- [ ] 测试 macOS 14、15 的系统音频捕获
- [ ] 测试暗色/亮色模式切换
- [ ] 测试系统权限请求流程
- [ ] 性能优化和内存泄漏检查
- [ ] 修复 macOS 特有的 UI 问题（字体渲染、窗口行为差异）
- [ ] **产出**：稳定的 macOS 正式版

---

## 七、工作量估算

| 阶段 | 工作量 | 累计 |
|------|--------|------|
| Phase 0 - 基础验证 | 5-7 人天 | 1 周 |
| Phase 1 - 核心音频 | 15-20 人天 | 4-5 周 |
| Phase 2 - 系统音频与录音 | 10-15 人天 | 6-8 周 |
| Phase 3 - 媒体与平台集成 | 5-10 人天 | 7-10 周 |
| Phase 4 - 打包发布 | 3-5 人天 | 8-11 周 |
| Phase 5 - 测试与优化 | 5-10 人天 | 9-13 周 |
| **总计** | **43-67 人天** | **约 2-3 个月** |

---

## 八、风险评估与缓解

| 风险 | 影响 | 概率 | 缓解措施 |
|------|------|------|----------|
| macOS 系统音频回环受限 | 功能降级 | 中 | 提供 ScreenCaptureKit 后备 + BlackHole 虚拟驱动引导 |
| PortAudio .NET 绑定不稳定 | 音频功能不可用 | 低 | 可回退到 Azure SDK 默认麦克风模式 |
| Apple 公证流程复杂 | 延迟发布 | 低 | 使用 Avalonia Parcel 自动化工具链 |
| Azure Speech SDK macOS 表现差异 | 识别质量差异 | 低 | SDK 官方支持 macOS，有完善文档 |
| FFmpeg 依赖分发 | 安装体验差 | 中 | 随 .app 捆绑 FFmpeg 二进制或使用 Homebrew 引导 |
| .NET 10 在 macOS 上有 Bug | 运行时问题 | 低 | .NET 10 已进入稳定期，macOS 是一级支持平台 |

---

## 九、MVP 定义（最小可行产品）

如果要以最快速度发布 macOS 版，MVP 应包含：

1. ✅ 完整 UI（Avalonia 原生跨平台）
2. ✅ AI 图像/视频生成（纯 HTTP API，已跨平台）
3. ✅ AI 洞察分析（纯 HTTP API，已跨平台）
4. ✅ 配置管理（已跨平台）
5. ⚡ 默认麦克风实时翻译（Azure SDK `FromDefaultMicrophoneInput`，已有回退代码）
6. ❌ 系统声音回环（MVP 不含）
7. ❌ 高级录音功能（MVP 不含）
8. ❌ 视频帧提取（MVP 不含）

**MVP 工作量估计：2-3 周**

这意味着 Phase 0 + 简化的 Phase 1 即可产出一个可用的 macOS 版本，核心翻译功能已经可用，只是不支持系统声音回环和录音。

---

## 十、CI/CD 参考配置

```yaml
# .github/workflows/build-macos.yml
name: Build macOS

on:
  push:
    branches: [main]
  release:
    types: [published]

jobs:
  build-macos:
    runs-on: macos-14  # Apple Silicon runner
    strategy:
      matrix:
        rid: [osx-x64, osx-arm64]
    steps:
      - uses: actions/checkout@v4
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      
      - name: Publish
        run: dotnet publish -c Release -r ${{ matrix.rid }} --self-contained true
      
      - name: Create App Bundle
        run: |
          # 使用 Parcel 或脚本创建 .app 结构
          mkdir -p "TrueFluentPro.app/Contents/MacOS"
          mkdir -p "TrueFluentPro.app/Contents/Resources"
          cp -R bin/Release/net10.0/${{ matrix.rid }}/publish/* "TrueFluentPro.app/Contents/MacOS/"
          cp Info.plist "TrueFluentPro.app/Contents/"
          cp Assets/AppIcon.icns "TrueFluentPro.app/Contents/Resources/"
      
      - name: Code Sign
        if: github.event_name == 'release'
        run: |
          codesign --deep --force --verify --verbose \
            --sign "${{ secrets.APPLE_SIGNING_IDENTITY }}" \
            --options runtime \
            --entitlements entitlements.plist \
            TrueFluentPro.app
      
      - name: Notarize
        if: github.event_name == 'release'
        run: |
          ditto -c -k --keepParent TrueFluentPro.app TrueFluentPro.zip
          xcrun notarytool submit TrueFluentPro.zip \
            --apple-id "${{ secrets.APPLE_ID }}" \
            --team-id "${{ secrets.APPLE_TEAM_ID }}" \
            --password "${{ secrets.APPLE_APP_PASSWORD }}" \
            --wait
          xcrun stapler staple TrueFluentPro.app
      
      - name: Create DMG
        run: |
          hdiutil create -volname "译见 Pro" \
            -srcfolder TrueFluentPro.app \
            -ov -format UDZO \
            TrueFluentPro-${{ matrix.rid }}.dmg
      
      - name: Upload artifact
        uses: actions/upload-artifact@v4
        with:
          name: macos-${{ matrix.rid }}
          path: TrueFluentPro-${{ matrix.rid }}.dmg
```

---

## 十一、结论

TrueFluentPro 做成 macOS 版是**完全可行且成本可控**的：

1. **Avalonia 框架保证了 UI 层零成本迁移** — 所有 AXAML 视图和主题直接可用
2. **AI 服务层已经是跨平台的** — HTTP API 调用无需任何修改
3. **Azure Speech SDK 官方支持 macOS** — 核心语音识别能力有保障
4. **音频采集是唯一重大工程** — 但有成熟的 macOS 音频 API 可用（CoreAudio、ScreenCaptureKit、PortAudio）
5. **MVP 可在 2-3 周内交付** — 使用默认麦克风模式即可实现基本翻译功能

建议的推进策略：**先出 MVP（默认麦克风翻译 + AI 功能），快速验证市场需求，再逐步补齐高级音频功能。**
