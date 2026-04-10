# 译见 Pro v2.0.4

## 更新
- 解决更新程序无法从 v2.0.0 升级到 v2.0.4 的问题（更新程序误将 IconTool 的 zip 识别为更新包）
- 更新程序备份和恢复逻辑改为递归，确保 Assets/ 等子目录内容完整处理

### 运行前提

本版本为 **FDD（Framework-dependent）** 发布，需要预先安装： [**.NET 10 Desktop Runtime**](https://dotnet.microsoft.com/download/dotnet/10.0)

### 使用方式

1. 下载对应架构的 zip 文件
2. 解压到任意目录
3. 运行 `TrueFluentPro.exe`
