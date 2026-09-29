# 各平台图片与图标资源入口

仅作定位清单，路径和字段须以项目当前版本配置及实际引用确认；替换步骤见[资源替换](./project-resource-replacement.md)。

## Tauri

- 配置：`src-tauri/tauri.conf.json`、`src-tauri/tauri.conf.json5` 或 `Tauri.toml`，结合 `Cargo.toml`、前端清单核对 `bundle.icon`、窗口标题、产品名和前端构建目录。
- Web / 程序内：`public/app-icon.png`、`public/favicon.*`、`src/assets/*`，另查 manifest、标题栏、启动页和关于页。
- 原生：`src-tauri/icons/icon.ico`、`icon.icns`、`*.png`；移动端另查 Android `mipmap-*` 和 iOS AppIcon。
- 复用已安装 CLI 或现有脚本；确认本地参数后可用 `npx tauri icon <master> --output <temporary-dir>` 或 `cargo tauri icon`，不为换图升级依赖。
- 仅复制 `bundle.icon` 和目标平台实际引用的输出，不把生成器附带的所有平台资源纳入项目；临时目录放项目内。
- 前端构建不更新原生 EXE：重建目标原生产物并核对实际图标；占用时只结束可执行路径匹配该产物的进程。

## 非 Tauri Rust

查 `Cargo.toml`、`build.rs`、`.rc`、`assets/`、打包脚本及实际使用的 `winres`、`winresource`、`embed-resource`、`include_bytes!`、`set_icon`、`Icon::from_*`。
Windows ICO、Linux desktop/icon、macOS bundle 按各自打包链处理，不套用 Tauri 目录。

## 其他技术栈

| 技术栈 | 配置与资源入口 |
| --- | --- |
| React / Vite / Vue / Web | `index.html` favicon、`public/`、`src/assets/`、Web Manifest、branding 常量；`dist/` 仅构建验证 |
| Next.js | `app/icon.*`、`app/apple-icon.*`、metadata、`public/`、页面 Logo 引用 |
| Electron | `package.json`、electron-builder / Forge 配置、窗口 `icon`、Web favicon；目标平台 ICO/ICNS/PNG |
| .NET / WPF / WinUI | `.csproj` 的 `ApplicationIcon` / `Win32Resource`、Resource、`Package.appxmanifest`、窗口/关于页图片 |
| Android | `AndroidManifest.xml` 的 `android:icon` / `roundIcon`、`res/mipmap-*`、adaptive icon XML foreground/background、Gradle 变体 |
| iOS / macOS | `Assets.xcassets` 的 AppIcon、`Contents.json`、target 配置、`Info.plist`；保留尺寸槽位 |
| Python 桌面 | PyInstaller `.spec` / CLI 的 `icon`、Qt `.qrc`、Tk/窗口图标调用、包内资源声明 |
| Flutter | `pubspec.yaml`、平台 Runner 资源、已有 `flutter_launcher_icons` 配置 |

未知技术栈按清单/配置 → 资产 → 代码引用 → 平台打包 → 产物核对，不补造字段；复用现有生成器与定向验证。