# 批次 0 交付报告（修订版 — 修复 T-006 打回）
> 初次提交：2026-04-28
> 修订提交：2026-04-28

## 审查打回修复

### ❌ T-006 打回项修复
- **问题**: `Rust3/tests/common/mod.rs` 位于虚拟 workspace 根，Cargo 不扫描其 `tests/` 目录，导致 `cargo test --test common` 报 `no test target named common`
- **修复措施**:
  1. 删除 `Rust3/tests/` 整个目录 — 验证: `Test-Path` 返回 `False` ✅
  2. 创建 `Rust3/src-tauri/tests/common.rs` — 内容与原 `mod.rs` 相同，但改为扁平文件（Cargo 将其识别为 test target "common"）
  3. 去除 `#[cfg(test)] mod tests` 包裹，直接使用顶级 `#[test]` 函数（integration test 文件天然是 test 上下文）
  4. `src-tauri/Cargo.toml` 已有所需依赖：`rusqlite`/`serde`/`serde_json` 在 `[dependencies]`，`dirs` 在 `[dev-dependencies]`
- **验证**: `cargo test -p truefluent-pro-r3 --test common --no-run` 编译通过 ✅

## 任务完成状态
- [x] T-001: 创建 Cargo workspace 根 — 证据：`Rust3/Cargo.toml`:L1-L14
- [x] T-002: 创建 9 个空 crate — 证据：`Rust3/crates/tfp-*/Cargo.toml` + `src/lib.rs`（共 9 组）
- [x] T-003: 创建 src-tauri 壳层 — 证据：`Rust3/src-tauri/Cargo.toml`:L1-L39, `src/main.rs`, `src/lib.rs`, `tauri.conf.json`, `build.rs`
- [x] T-004: 创建 React 前端项目 — 证据：`Rust3/package.json`, `tsconfig.json`, `vite.config.ts`, `tailwind.config.js`, `postcss.config.js`, `index.html`
- [x] T-005: 创建前端目录骨架 — 证据：`Rust3/src/main.tsx`, `App.tsx`, `index.css`, `lib/i18n.ts`, `lib/locales/*.json`, `lib/tauri-api.ts`
- [x] T-006: 创建端点加载器测试辅助 — 证据：`Rust3/src-tauri/tests/common.rs`:L1-L61（**已修复：从 workspace 根移入 src-tauri**）
- [x] T-007: 创建 .gitignore — 证据：`Rust3/.gitignore`:L1-L5

## 编译状态
```
$ cargo check --workspace
Finished dev profile [unoptimized + debuginfo] target(s) in 0.90s
→ 0 errors ✅

$ cargo test --workspace
9 crate unit tests passed, 1 integration test ignored (common::can_load_rust2_endpoints)
→ 9 passed, 0 failed, 1 ignored ✅

$ cargo test -p truefluent-pro-r3 --test common --no-run
Finished test profile [unoptimized + debuginfo] target(s) in 14.44s
Executable tests\common.rs (target\debug\deps\common-168e95c78e0f7653.exe)
→ 编译通过 ✅
```

## 自检清单
- [x] 编译通过（0 errors）
- [x] 无死代码（骨架项目，所有 crate 仅含 it_works 测试；common.rs 中 load_rust2_endpoints 被 #[test] 函数调用）
- [x] 测试通过（9 passed, 0 failed, 1 ignored）
- [x] 无中文硬编码（App.tsx 使用 `t("app.name")`，locale JSON 不算硬编码）
- [x] 变更日志每条有证据
- [x] 旧文件已删除（`Rust3/tests/` 不存在，`Test-Path` = False）

## 已知局限
- `Rust3/dist/index.html` 是占位文件，用于满足 Tauri `generate_context!()` 宏在 check 时对 `frontendDist` 路径的验证。此文件在 `.gitignore` 中已排除。
- `icons/icon.ico` 从 Rust2 复制，满足 Tauri Windows 资源文件生成需求。
- `common.rs` 中的 `load_rust2_endpoints()` 函数标记 `#[ignore]` 测试，需 Rust2 数据库存在才能真正运行。

## 新增/修改的文件清单
- `Rust3/src-tauri/tests/common.rs` — 端点加载器测试辅助（**新位置**，从 `Rust3/tests/common/mod.rs` 迁移）
- ~~`Rust3/tests/common/mod.rs`~~ — **已删除**
