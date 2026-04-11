# 分析 DocumentTranslation.Desktop 的翻译与文本转语音功能

> 基于 `C:\Users\kukisama\Downloads\Project\DocumentTranslation.Desktop` 项目源码分析
> 目标：提取其翻译服务和 TTS/SSML 能力，搬运到 TrueFluentPro 项目中

---

## 第一部分：翻译服务架构分析

### 1.1 核心设计模式：策略模式 + 统一服务接口

DocumentTranslation.Desktop 的翻译模块最值得借鉴的设计是 **认证策略模式**。翻译业务逻辑完全不关心当前是什么认证方式，所有认证差异由策略层消化。

```
IDesktopTranslationService (统一翻译接口)
    └── DesktopTranslationService (实现)
            ├── IAuthStrategy (策略接口)
            │     ├── AadAuthStrategy      (Azure Entra ID)
            │     ├── ApiKeyAuthStrategy   (API Key)
            │     └── KeyVaultAuthStrategy (Key Vault → 从 Vault 取 Key)
            ├── IAuthContext (只读认证状态)
            └── IConfigurationService (配置读写)
```

**关键源码**:
- 策略接口: `Services/IAuthStrategy.cs`
- AAD 策略: `Services/AadAuthStrategy.cs`
- Key 策略: `Services/ApiKeyAuthStrategy.cs`
- KV 策略: `Services/KeyVaultAuthStrategy.cs`
- 翻译服务: `Services/DesktopTranslationService.cs`

### 1.2 IAuthStrategy 接口定义

每个策略只需实现 5 个方法：

```csharp
public interface IAuthStrategy
{
    AuthMode Mode { get; }
    Task<ResolvedTranslationSettings> ResolveSettingsAsync(AppConfig config, CancellationToken ct);
    DocumentTranslationClient CreateDocumentTranslationClient(ResolvedTranslationSettings resolved);
    BlobServiceClient CreateBlobServiceClient(ResolvedTranslationSettings resolved);
    Task SetAuthHeadersAsync(HttpRequestMessage request, ResolvedTranslationSettings resolved, CancellationToken ct);
    string? GetStorageConnectionString(ResolvedTranslationSettings resolved);
}
```

`ResolveSettingsAsync` 会根据当前配置产出一个 **不可变的统一设置对象** `ResolvedTranslationSettings`，后续所有操作都基于这个对象，不再回查配置。

### 1.3 ResolvedTranslationSettings — 一次解析全局通用

```csharp
public sealed record ResolvedTranslationSettings(
    AuthMode Mode,
    Uri DocumentEndpoint,
    string TextEndpoint,
    string? Region,
    string? StorageConnectionString,
    string? StorageEndpoint,
    string? StorageAccountName,
    string? DocumentKey,
    string? TextKey,
    TokenCredential? TokenCredential,
    string? TranslatorResourceId,
    AzureCloudEnvironment CloudEnvironment)
```

它还有两个派生属性，决定请求路径和头部行为：
- `UseGlobalTextEndpointForAad` — AAD 模式 + 全局端点时为 true，此时需要加 `Ocp-Apim-ResourceId` 头
- `UseCustomTextEndpointPath` — 自定义端点（*.cognitiveservices.azure.com）时为 true，路径需带 `/translator/text/v3.0/` 前缀

**源码**: `Services/ResolvedTranslationSettings.cs`

### 1.4 21Vianet（世纪互联/中国区）支持

`AzureCloudEnvironment` 枚举定义 Global 和 China，`AzureCloudEndpoints` 静态类包含所有云环境差异化配置。

| 用途 | Global | China (21V) |
|------|--------|-------------|
| 登录 Authority | `login.microsoftonline.com` | `login.chinacloudapi.cn` |
| 管理端点 | `management.azure.com` | `management.chinacloudapi.cn` |
| 认知服务 Scope | `cognitiveservices.azure.com/.default` | `cognitiveservices.azure.cn/.default` |
| 认知服务后缀 | `.cognitiveservices.azure.com` | `.cognitiveservices.azure.cn` |
| 文本翻译端点 | `api.cognitive.microsofttranslator.com` | `api.translator.azure.cn` |
| 存储端点后缀 | `blob.core.windows.net` | `blob.core.chinacloudapi.cn` |
| TTS 域名 | `{region}.tts.speech.microsoft.com` | `{region}.tts.speech.azure.cn` |

**源码**: `Models/AzureCloudEnvironment.cs`

### 1.5 三种认证模式实际工作方式

#### API Key 模式
- 请求直接携带 `Ocp-Apim-Subscription-Key` 头
- 如果 Region 不是 "global"，还携带 `Ocp-Apim-Subscription-Region` 头
- Key 可以来自手动输入或 Key Vault

```csharp
// ApiKeyAuthStrategy.SetAuthHeadersAsync
SettingsHelper.ApplyApiKeyHeaders(request, resolved.TextKey, resolved.Region);
```

**源码**: `Services/ApiKeyAuthStrategy.cs:62-68`

#### AAD 模式
- 通过 `ITokenProvider.GetTokenAsync(scope)` 获取 Bearer 令牌
- 当使用全局文本端点时（`api.cognitive.microsofttranslator.com`），需要额外加 `Ocp-Apim-ResourceId` 头指定目标资源
- 当使用自定义端点时（`*.cognitiveservices.azure.com`），只需 Bearer 令牌

```csharp
// AadAuthStrategy.SetAuthHeadersAsync
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
if (resolved.UseGlobalTextEndpointForAad)
{
    request.Headers.Add("Ocp-Apim-ResourceId", ...);
    SettingsHelper.ApplyRegionHeader(request, resolved.Region);
}
```

**源码**: `Services/AadAuthStrategy.cs:79-98`

#### Key Vault 模式
- 先从 Azure Key Vault 拉取 Key，然后行为与 API Key 模式一致
- Key Vault 自身的访问使用 AAD Token

**源码**: `Services/KeyVaultAuthStrategy.cs`

### 1.6 翻译 API 调用流程

`DesktopTranslationService` 中所有操作走统一路径：

```
1. ResolveSettingsAsync() → 拿到 (IAuthStrategy, ResolvedTranslationSettings)
2. 构造 URL（根据 UseCustomTextEndpointPath 选路径格式）
3. SetAuthHeadersAsync() → 策略注入认证头
4. SendTextRequestAsync() → 发送请求并处理响应
```

URL 构造逻辑：

| 端点类型 | 路径格式 |
|---------|---------|
| 全局端点 | `https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=...` |
| 自定义端点 | `https://xxx.cognitiveservices.azure.com/translator/text/v3.0/translate?api-version=3.0&to=...` |

**源码**: `Services/DesktopTranslationService.cs:310-330`

### 1.7 DesktopTranslationService 提供的能力

| 方法 | 功能 |
|------|------|
| `LoadLanguagesAsync()` | 加载支持的语言列表（含自动检测） |
| `TranslateTextAsync()` | 文本翻译（支持指定来源语言、目标语言、Category） |
| `DetectLanguageAsync()` | 语言自动检测 |
| `CreateDocumentTranslationClientAsync()` | 创建文档翻译 SDK 客户端 |
| `CreateBlobServiceClientAsync()` | 创建存储客户端（文档翻译需要） |

---

## 第二部分：文本转语音 (TTS) 与 SSML 架构分析

### 2.1 SpeechSynthesisService — 纯 REST 实现

该项目 **不使用** `Microsoft.CognitiveServices.Speech` NuGet SDK，全程用 HttpClient 调用 REST API。这对跨平台 Avalonia 项目非常友好。

核心 API：
- 语音列表: `GET {host}/cognitiveservices/voices/list`
- 语音合成: `POST {host}/cognitiveservices/v1` (Content-Type: application/ssml+xml)

**源码**: `Services/SpeechSynthesisService.cs`

### 2.2 TTS 的认证方式

TTS 认证与翻译不同，它不走 IAuthStrategy，而是在 `SpeechSynthesisService` 内部自行处理：

#### AAD 模式的特殊处理

Speech REST API 的 AAD Bearer 格式与其他服务不同，格式为：
```
Authorization: Bearer aad#{resourceId}#{entraAccessToken}
```

这是 Speech Service 特有的认证格式，需要三段拼接。

**源码**: `Services/SpeechSynthesisService.cs:250-260`

```csharp
var speechBearer = $"aad#{speechResourceId}#{tokenResult.Token}";
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", speechBearer);
```

#### API Key 模式
直接携带 `Ocp-Apim-Subscription-Key` 头。

#### TTS 域名选择逻辑

| 条件 | 域名 |
|------|------|
| AAD + 自定义域名端点 | `https://xxx.cognitiveservices.azure.com` |
| API Key + 全球区 | `https://{region}.tts.speech.microsoft.com` |
| API Key + 中国区 | `https://{region}.tts.speech.azure.cn` |

自定义域名端点在调用路径前需加 `/tts/` 前缀：
```
自定义域名: {endpoint}/tts/cognitiveservices/v1
区域端点:   {endpoint}/cognitiveservices/v1
```

**源码**: `Services/SpeechSynthesisService.cs:110-120`、`Services/SpeechSynthesisService.cs:200-225`

### 2.3 SSML 构建能力（核心能力）

`SpeechSynthesisService` 提供三种 SSML 构建方式：

#### 单人 SSML — `BuildSsml()`

```csharp
public static string BuildSsml(
    string text, VoiceInfo voice,
    string? style, double styleDegree, string? role,
    string? rate, string? pitch, string? volume,
    SpeechAdvancedOptions? advancedOptions)
```

构建包含完整嵌套的 SSML：
```xml
<speak>
  <voice name="..." effect="...">
    <mstts:silence .../>
    <lang xml:lang="...">
      <mstts:express-as style="..." styledegree="..." role="...">
        <prosody rate="..." pitch="..." volume="...">
          <emphasis level="...">
            <break .../>
            <say-as ...>文本</say-as>
          </emphasis>
        </prosody>
      </mstts:express-as>
    </lang>
  </voice>
</speak>
```

支持的高级选项（`SpeechAdvancedOptions`）：
- Effect（音效）
- LanguageOverride（跨语言切换，仅多语言/Dragon HD 语音支持）
- Volume / Range / Contour（韵律控制）
- Break strength / time（停顿）
- Silence type / value（静音）
- Emphasis level（强调）
- Phoneme alphabet / value（发音标注）
- SayAs interpret-as / format / detail（文本解读方式）
- Sub alias（文本替换）

**源码**: `Services/SpeechSynthesisService.cs:440-640`

#### 多人简单 SSML — `BuildMultiVoiceSsml()`

简化版多人 SSML，仅支持语音和文本，无风格/韵律控制：

```csharp
public static string BuildMultiVoiceSsml(
    IEnumerable<(string Text, string VoiceShortName, string Locale)> segments)
```

**源码**: `Services/SpeechSynthesisService.cs:640-670`

#### 多人完整 SSML — Script 模式 / Advanced 模式的 ViewModel 构建

在 `SpeechSynthesisViewModel` 中，Script 模式和 Advanced 模式会为每个 segment 调用 `BuildVoiceBlock()`，各 segment 可以有独立的 voice/style/prosody/advanced 配置。

**源码**: `ViewModels/SpeechSynthesisViewModel.cs:600-630`

### 2.4 VoiceInfo 数据模型

直接映射 REST API 返回的 JSON：

```csharp
public class VoiceInfo
{
    public string Name { get; set; }           // 完整标识
    public string ShortName { get; set; }      // 如 "zh-CN-XiaoxiaoNeural"
    public string DisplayName { get; set; }    // 显示名
    public string Locale { get; set; }         // 语言代码
    public string Gender { get; set; }         // 性别
    public string VoiceType { get; set; }      // Neural/Standard
    public List<string>? StyleList { get; set; }     // 支持的风格列表
    public List<string>? RolePlayList { get; set; }  // 支持的角色列表
    public List<string>? SecondaryLocaleList { get; set; } // 多语言支持
    // ... 能力标志位
}
```

关键能力检测：
- `IsHD` / `IsDragonHdVoice` / `IsDragonHdOmni` / `IsDragonHdFlash` — Dragon HD 系列识别
- `SupportsExpressAs` — 是否支持情感/风格
- `SupportsProsodyControl` — 是否支持韵律（Dragon HD 不支持）
- `SupportsHighQuality48K` — 是否支持 48kHz

**源码**: `Models/VoiceInfo.cs`

### 2.5 语音列表缓存机制

两级缓存：
1. **内存缓存**: `_cachedVoices` + `_cachedVoicesRegion`（会话内复用）
2. **磁盘缓存**: JSON 文件，按 `cloud-region-endpoint` 组合键，24 小时 TTL

```
AppData/cache/speech-voices-{cloud}-{region}-{endpoint}.json
```

**源码**: `Services/SpeechSynthesisService.cs:300-390`

### 2.6 输出格式

预定义 9 种格式：

| 标签 | Header 值 | 要求 48kHz |
|------|-----------|-----------|
| MP3 24kHz | audio-24khz-96kbitrate-mono-mp3 | 否 |
| MP3 48kHz | audio-48khz-192kbitrate-mono-mp3 | 是 |
| MP3 16kHz | audio-16khz-64kbitrate-mono-mp3 | 否 |
| Opus 24kHz | audio-24khz-48kbitrate-mono-opus | 否 |
| WAV 16kHz | riff-16khz-16bit-mono-pcm | 否 |
| WAV 24kHz | riff-24khz-16bit-mono-pcm | 否 |
| WAV 48kHz | riff-48khz-16bit-mono-pcm | 是 |
| OGG 24kHz | ogg-24khz-16bit-mono-opus | 否 |
| OGG 48kHz | ogg-48khz-16bit-mono-opus | 是 |

**源码**: `Services/SpeechSynthesisService.cs:400-420`

---

## 第三部分：播客生成工作流分析

### 3.1 DocumentTranslation.Desktop 的多人台本系统

#### 四种输入模式

`SpeechSynthesisViewModel` 支持 4 种模式：

| Index | 模式 | 说明 |
|-------|------|------|
| 0 | 文本模式 | 单人 + 单段文本 + 完整声音配置 |
| 1 | 手动 SSML | 用户直接编写 SSML XML |
| 2 | 台本模式 (Script) | 多人对话脚本 → 自动生成 SSML |
| 3 | 高级模式 (Advanced) | 多段 segment 编辑器，每段独立配置 |

#### 台本模式的脚本格式

用正则匹配发言人标签：
```
发言人 A：大家好，欢迎收听今天的播客。
发言人 B：今天我们来聊聊人工智能。
发言人 C：没错，最近变化很大。
```

正则: `^\s*发言人\s*([ABCabc])\s*[：:]\s*(.+?)\s*$`

支持发言人 A/B/C 三个角色，每个角色有独立的 `SpeakerProfile` 配置。

**源码**: `ViewModels/SpeechSynthesisViewModel.cs:16-17`、`ViewModels/SpeechSynthesisViewModel.cs:1416-1540`

#### SpeakerProfile 数据结构

```csharp
public partial class SpeakerProfile : ObservableObject
{
    public string Tag { get; }          // "A", "B", "C"
    public string DisplayName { get; }  // 本地化名称
    public VoiceInfo? Voice { get; }    // 绑定的语音
    
    // 完整声音配置
    public SpeechOptionItem? SelectedStyle { get; }
    public double StyleDegree { get; }
    public SpeechOptionItem? SelectedRole { get; }
    public string? Rate { get; }
    public string? Pitch { get; }
    public SpeechAdvancedOptions AdvancedOptions { get; }
}
```

**源码**: `Models/SpeakerProfile.cs`

#### SpeakerProfile 持久化

配置按 `cloud|region|endpoint` 组合键存档到 `AppConfig.SpeechSpeakerConfigs` 字典中，以 `SavedSpeakerConfig` 列表形式序列化，切换资源后自动恢复。

**源码**: `ViewModels/SpeechSynthesisViewModel.cs:640-730`、`Models/SavedSpeakerConfig.cs`

#### 台本 → SSML 转换流程

```
1. 逐行解析脚本 → (Speaker, Text) 列表
2. 通过 FindSpeakerProfile(speaker) 查找对应的 SpeakerProfile
3. 构建 SpeakerPersona（Voice + Style + Rate + Pitch + Advanced）
4. 逐条调用 SpeechSynthesisService.BuildVoiceBlock() 生成 <voice> 块
5. 包裹在 <speak> 根节点中生成完整 SSML
```

**源码**: `ViewModels/SpeechSynthesisViewModel.cs:1416-1540`

### 3.2 高级模式 (Advanced) 的 Segment 编辑器

每个 `SsmlSegment` 是独立的编辑单元：
- 独立的文本、语音、风格、韵律配置
- 带色彩标识（8 种颜色轮换）
- 支持拖拽排序、复制、删除
- 实时预览合并后的 SSML

**源码**: `Models/SsmlSegment.cs`、`ViewModels/SpeechSynthesisViewModel.cs:430-600`

### 3.3 合成执行流程

```
TryResolveSsmlForSynthesis() → 根据当前模式生成 SSML
    ↓
ResolveOutputFormatHeader() → 确定输出格式
    ↓
SpeechSynthesisService.SynthesizeAsync(ssml, format) → REST API 调用
    ↓
SaveGeneratedAudioAsync(audioBytes, format) → 自动保存到文件
    ↓
AudioPreviewService.Play(path) → 可选播放预览
```

**关键约束**: REST API 同步合成有 **10 分钟音频长度限制**。超长播客需要用 Batch Synthesis API。

**源码**: `ViewModels/SpeechSynthesisViewModel.cs:1329-1377`

### 3.4 SSML 50 条交互记录限制

Azure Speech Service 对单个 SSML 文档有以下限制：
- 实时 TTS REST API 最多 **50 个 `<voice>` 元素**
- SSML 文档最大 **64KB**
- 音频输出最长 **10 分钟**

对于播客场景的影响：
- **二人播客 50 轮对话**（25 轮交互）实际可以覆盖大多数播客长度
- **三人播客**每人约 16-17 轮发言
- 超出限制时，需要分批合成再拼接

### 3.5 TrueFluentPro 当前的播客生成状态

TrueFluentPro 的 `AudioLabViewModel.GeneratePodcastAsync()` 当前只做了第一步：

```
音频 → 转录 → AI 生成播客脚本（Markdown 格式）
```

Prompt 要求：
> 你是一个播客脚本编写专家。根据音频转录内容，生成一段适合播客的内容改写。
> 1. 用对话体、口语化风格重新组织内容
> 2. 添加适当的过渡语和解说
> ...

**缺失部分**: 生成脚本后，没有进入 TTS 合成环节。"脚本 → 音频" 这段链路完全空白。

**源码**: `ViewModels/AudioLabViewModel.cs:966-975`

---

## 第四部分：搬运到 TrueFluentPro 的建议

### 4.1 任务一：翻译 + TTS 模块化搬运

#### 需要引入的核心类型

| 层次 | 要引入的类型 | 用途 |
|------|------------|------|
| 模型层 | `AzureCloudEnvironment` + `AzureCloudEndpoints` | 21V/Global 云环境全量端点表 |
| 模型层 | `ResolvedTranslationSettings` | 不可变的已解析翻译配置 |
| 模型层 | `VoiceInfo` | 语音元数据 DTO |
| 模型层 | `SpeakerProfile` + `SavedSpeakerConfig` | 发言人配置 |
| 模型层 | `SsmlSegment` + `SpeechAdvancedOptions` | SSML 段落编辑 |
| 服务层 | `IAuthStrategy` + 三个策略实现 | 认证策略 |
| 服务层 | `SettingsHelper` | 端点 / 头部 / ResourceId 工具方法 |
| 服务层 | `SpeechSynthesisService` | TTS REST 调用 + SSML 构建 |
| 服务层 | 翻译 API 调用逻辑（`TranslateTextAsync`/`DetectLanguageAsync`） | 文本翻译 |

#### 与 TrueFluentPro 现有体系的对接点

TrueFluentPro 已有 `AzureTokenProvider` / `SpeechResource` / `AzureSpeechConfig` 等模型，需要做适配而非直接覆盖：

| DocumentTranslation.Desktop 概念 | TrueFluentPro 对应概念 | 对接方式 |
|-----------------------------------|----------------------|---------|
| `AppConfig.CloudEnvironment` | `AzureSpeechConfig` 中的端点判断 | 统一到 `AzureCloudEnvironment` 枚举 |
| `AppConfig.AuthMode` (AAD/Key/KV) | `SpeechResource.Vendor` + Key/AAD | 需要扩展 SpeechResource 支持翻译资源 |
| `ITokenProvider` | `AzureTokenProvider` | 可以做适配器包装 |
| `IAuthContext` | 不存在 | 新增，或搬运 |
| TTS REST 调用 | 不存在 | 直接搬运 `SpeechSynthesisService` |
| 翻译 REST 调用 | 不存在 | 直接搬运核心逻辑 |

#### 建议的模块边界

```
Services/Translation/
    ├── ITranslationAuthStrategy.cs        ← 改名以避免和现有冲突
    ├── TranslationAadAuthStrategy.cs
    ├── TranslationApiKeyAuthStrategy.cs
    ├── TranslationSettings.cs             ← 即 ResolvedTranslationSettings
    ├── TranslationSettingsHelper.cs       ← 即 SettingsHelper
    ├── TextTranslationService.cs          ← 从 DesktopTranslationService 提取
    └── CloudEndpoints.cs                  ← 即 AzureCloudEndpoints

Services/Speech/
    ├── SpeechSynthesisService.cs          ← 可直接搬运
    └── SsmlBuilder.cs                     ← 可选：从 Service 中拆出静态方法
```

### 4.2 任务二：播客文本 → 音频完整链路

#### 当前缺口

```
[已有] 音频 → 转录 → AI 生成播客脚本 (Markdown)
                                        ↓ ← 断裂点
[缺失] 脚本解析 → 发言人配置 → SSML 生成 → TTS 合成 → 音频文件
```

#### 需要实现的链路

```
1. 约束 AI 生成脚本格式（必须使用 "发言人A：" 格式）
2. 控制总轮次不超过 50 条 <voice> 限制
3. 解析脚本为 (Speaker, Text) 列表
4. 为每个 Speaker 绑定 SpeakerProfile (Voice + Style + Prosody)
5. 调用 SpeechSynthesisService.BuildVoiceBlock() 构建 SSML
6. 调用 SpeechSynthesisService.SynthesizeAsync() 合成音频
7. 保存音频文件
```

#### Prompt 改进建议

当前 Prompt 没有约束输出格式。需要明确：

```
请以两人对话的形式重新组织内容，严格使用以下格式：

发言人 A：[主持人台词]
发言人 B：[嘉宾台词]

要求：
1. 对话总轮次控制在 40 轮以内（即 A 和 B 各约 20 轮）
2. 每轮发言控制在 200 字以内，避免冗长
3. 口语化、自然过渡
4. 不要加 Markdown 格式、括号注释或舞台指导
5. 第一行必须是发言人 A 的开场白
```

三人播客对应修改上限为 45 轮（每人约 15 轮），留 5 条余量。

#### 分批合成策略

如果脚本超过 50 条 `<voice>` 限制，需要：

```
1. 将 segments 按 50 条一组分批
2. 每批独立生成 SSML 并合成
3. 将多个音频文件拼接（可用 FFmpeg 或 NAudio）
4. 或者让 AI 先把脚本控制在限制内
```

推荐优先用 Prompt 控制长度，避免引入音频拼接复杂度。

---

## 附录：关键常量与限制

| 限制项 | 值 | 来源 |
|--------|-----|------|
| 实时 TTS 最大音频长度 | 10 分钟 | Azure Speech REST API |
| 单 SSML 最大尺寸 | 64 KB | Azure Speech REST API |
| 单 SSML 最大 `<voice>` 数 | 50 个 | Azure Speech REST API |
| Batch Synthesis 音频长度 | 无限制 | Azure Batch Synthesis API |
| Voice 列表磁盘缓存 TTL | 24 小时 | `SpeechSynthesisService` 实现 |
| SpeakerProfile 最大数 | 3 (A/B/C) | 当前 ViewModel 硬编码 |
| 输出格式 | 9 种 (MP3/WAV/OGG) | `SpeechSynthesisService.OutputFormats` |

---

## 附录：源码索引

以下所有路径相对于 `C:\Users\kukisama\Downloads\Project\DocumentTranslation.Desktop\`

### 认证与配置
| 文件 | 角色 |
|------|------|
| `Models/AppConfig.cs` | 全局配置模型（含 AuthMode / CloudEnvironment / 各种 ResourceId） |
| `Models/AzureCloudEnvironment.cs` | 云环境枚举 + 所有端点映射表 |
| `Services/IAuthStrategy.cs` | 认证策略接口 |
| `Services/AadAuthStrategy.cs` | AAD 策略实现 |
| `Services/ApiKeyAuthStrategy.cs` | API Key 策略实现 |
| `Services/KeyVaultAuthStrategy.cs` | Key Vault 策略实现 |
| `Services/IAuthContext.cs` | 只读认证状态接口 |
| `Services/IAuthSession.cs` | 认证会话管理接口（登录/登出/切换租户） |
| `Services/ITokenProvider.cs` | Token 获取接口 |
| `Services/AzureTokenProvider.cs` | Token Provider 实现（InteractiveBrowserCredential） |
| `Services/ResolvedTranslationSettings.cs` | 已解析的翻译设置（不可变 record） |
| `Services/SettingsHelper.cs` | 端点解析 / 头部注入 / ResourceId 处理 |

### 翻译服务
| 文件 | 角色 |
|------|------|
| `Services/IDesktopTranslationService.cs` | 翻译服务接口 |
| `Services/DesktopTranslationService.cs` | 翻译服务实现（语言列表 / 文本翻译 / 语言检测 / 文档翻译客户端） |
| `ViewModels/TextTranslationViewModel.cs` | 文本翻译页面 ViewModel |

### TTS / SSML
| 文件 | 角色 |
|------|------|
| `Services/SpeechSynthesisService.cs` | TTS 核心：语音列表、SSML 构建、语音合成 REST调用、认证头处理 |
| `Models/VoiceInfo.cs` | REST API 语音信息 DTO（含能力标志位） |
| `Models/SpeakerProfile.cs` | 发言人配置模型 |
| `Models/SavedSpeakerConfig.cs` | 发言人配置持久化模型 |
| `Models/SsmlSegment.cs` | SSML 段落编辑模型（高级模式） |
| `Models/SpeechAdvancedOptions.cs` | SSML 高级选项（effect/silence/emphasis/phoneme 等） |
| `Models/SpeechOptionItem.cs` | 下拉选项模型 |
| `ViewModels/SpeechSynthesisViewModel.cs` | TTS 页面 ViewModel（4 种模式 / 台本解析 / 合成流程） |

### 文档
| 文件 | 角色 |
|------|------|
| `Docs/ssml.md` | SSML TTS 页面设计文档 |
| `Docs/speech计划.md` | 播客制作完整计划（SSML 语法参考 + Batch API 方案） |
| `Docs/多人台本页面改造.md` | 多人台本角色标签 → 语音配置模板映射设计 |
