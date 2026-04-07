# IconToolUI

Windows 文件夹图标设置工具，基于 .NET 10 + Avalonia 11.3 构建。

## 功能

- 扫描指定目录及其子目录中的 EXE 文件，提取内嵌图标
- 可视化展示所有可用图标，双击或点击"确定"一键设置为文件夹图标
- 权限不足时自动请求 UAC 提权
- 操作结果通过 Toast 浮窗即时反馈

## 项目结构

```
IconToolUI/          ← Avalonia 桌面应用（WinExe）
IconTool.Core/       ← 共享核心库
  ├ PeIconExtractor  ← PE 文件图标提取
  ├ DirectoryIconService ← 文件夹图标写入 + Shell 缓存刷新
  ├ IcoHelper        ← ICO 格式转换
  └ Models           ← 数据模型
```

---

## 技术要点：文件夹图标缓存刷新

Windows 设置文件夹自定义图标后，Explorer 不一定立即显示新图标。本项目采用 **Shell 官方 API + 多层通知** 策略，确保图标变更尽可能即时生效。

### 常规做法（不够可靠）

```
手动写 desktop.ini → SHChangeNotify(SHCNE_UPDATEDIR) → 等 Shell 异步响应 → 刷新图标
```

这条路径的问题：

| 环节 | 风险 |
|------|------|
| 手动写 desktop.ini | 编码、BOM、节名大小写任何一个细节不符合 Shell 预期，Shell 就会忽略 |
| SHChangeNotify 异步 | 即使加 `SHCNF_FLUSH`，Shell 也只是"排队处理"，不保证立即刷新 |
| 缓存层级多 | 内存缓存、iconcache\_\*.db、thumbcache\_\*.db，单靠一条通知难以穿透所有层 |

### 本项目的做法（更可靠）

核心思路：**让 Shell 自己写入并刷新**，而不是我们写完文件再请求 Shell 来看。

#### Step 1 — `SHGetSetFolderCustomSettings(FCS_FORCEWRITE)`

这是 Explorer **"属性 → 自定义 → 更改图标"** 内部使用的同一 API。它不仅写入 desktop.ini，还同时更新 Shell 内部缓存结构。`FCS_FORCEWRITE` 标志强制写入，即使 Shell 认为值没变也会刷新。

```csharp
[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
private static extern int SHGetSetFolderCustomSettings(
    ref SHFOLDERCUSTOMSETTINGS pfcs, string pszPath, uint dwReadWrite);
```

#### Step 2 — `WritePrivateProfileString` 补充 IconResource

Shell API 只写旧格式 `IconFile`/`IconIndex`。我们用 Windows 原生 INI API 补写 Vista+ 的 `IconResource` 条目，保证编码和格式完全符合 Shell 预期：

```csharp
[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
private static extern bool WritePrivateProfileString(
    string lpAppName, string lpKeyName, string lpString, string lpFileName);
```

#### Step 3 — `PathMakeSystemFolderW`

替代手动设置 ReadOnly/System 属性。此 API 确保文件夹被 Shell 识别为"自定义文件夹"：

```csharp
[LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
[return: MarshalAs(UnmanagedType.Bool)]
private static partial bool PathMakeSystemFolderW(string pszPath);
```

#### Step 4 — 6 层 Shell 通知

覆盖所有可能的缓存失效路径：

| 层 | API | 作用 |
|----|-----|------|
| 1 | `SHChangeNotify(SHCNE_UPDATEITEM)` + `SHCNF_FLUSH` | 通知 desktop.ini 文件本身变更 |
| 2 | `SHChangeNotify(SHCNE_UPDATEDIR)` + `SHCNF_FLUSH` | 通知目标目录更新 |
| 3 | `SHChangeNotify(SHCNE_UPDATEDIR)` | 通知父目录刷新（Explorer 树视图） |
| 4 | `SHChangeNotify(SHCNE_ASSOCCHANGED)` + `SHCNF_FLUSHNOWAIT` | 全局关联变更，强制图标缓存失效（用 FLUSHNOWAIT 避免卡顿） |
| 5 | `FileIconInit(false)` + `FileIconInit(true)` | 重初始化系统图像列表（shell32.dll ordinal 660） |
| 6 | `SendMessageTimeout(WM_SETTINGCHANGE)` | 广播系统设置变更 |

### 为什么更稳定

| 改进点 | 原因 |
|--------|------|
| `SHGetSetFolderCustomSettings` | Explorer 自身的 API，写入同时更新内部缓存，"从内部修改"而非"从外部通知" |
| `FCS_FORCEWRITE` | 强制写入 + 刷新，不依赖 Shell 判断值是否变化 |
| `WritePrivateProfileString` | Windows 原生 INI API，格式/编码保证正确，不存在手动拼字符串的偏差 |
| `PathMakeSystemFolderW` | 正确设置系统文件夹标志，而非手动操作文件属性位 |
| 6 层通知 | 覆盖从文件级到系统级的所有缓存层，确保至少一层能命中 |

**一句话**：之前是"我们自己写文件，然后请求 Shell 来看一眼"—— Shell 可能不理你；现在是"让 Shell 自己动手写 + 刷新"—— 它自己改的东西，自己肯定认。
