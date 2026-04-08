# Rust Icon Tools

Windows 目录图标管理工具集的 Rust 实现，包含 4 个 crate：

| Crate | 类型 | 说明 |
|-------|------|------|
| `icon-core` | 库 | 核心逻辑：PE 图标提取、ICO 解析/生成、目录图标服务 |
| `icon-tool` | CLI | 命令行工具，16+ 子命令（提取、合成、裁剪、圆角、阴影等） |
| `icon-gen` | CLI | PNG → ICO 生成器 |
| `icon-ui` | GUI | egui 桌面界面，浏览 EXE 图标并设为目录图标 |

## 编译

### Debug 构建

```powershell
cargo build
```

产出位于 `target/debug/`，包含调试符号，体积较大，适合开发调试。

### Release 构建

```powershell
cargo build --release
```

产出位于 `target/release/`，已启用优化。`Cargo.toml` 中配置了 `strip = "symbols"`，Release 构建**不会生成 `.pdb` 文件**。

### 清理旧 PDB 文件

如果之前在没有 `strip` 配置时做过 Release 构建，旧的 `.pdb` 会残留在输出目录中，需要手动清理：

```powershell
# 清理 Release 目录的 PDB
Remove-Item target/release/*.pdb -Force

# 清理 Debug 目录的 PDB（可选）
Remove-Item target/debug/*.pdb -Force
```

### 完整重建

```powershell
cargo clean
cargo build --release
```

## 产出文件

Release 构建后在 `target/release/` 下：

- `icon-tool.exe` — CLI 工具
- `icon-gen.exe` — PNG→ICO 生成器
- `icon-ui.exe` — GUI 应用（Release 模式下无控制台窗口）

## 关于 PDB

`.pdb`（Program Database）是 MSVC 链接器生成的调试符号文件。对于最终分发的程序**完全不需要**，删除不影响运行。

当前 `Cargo.toml` 已配置：

```toml
[profile.release]
strip = "symbols"
```

此配置会在 Release 链接阶段剥离符号，不再生成 `.pdb`，同时也会缩小 `.exe` 体积。

如果需要临时保留 PDB（例如排查崩溃），注释掉该行后重新构建即可。
