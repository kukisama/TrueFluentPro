# 批次 0 施工单 — Rust3 工程骨架
> 日期：2026-04-28

## 目标
创建 Rust3 目录，搭建 Cargo workspace（9 个空 crate）+ React 前端项目骨架。**不写任何业务代码**，只建目录结构和配置文件。

## 前置条件
无（第一批次）

## 参考文件
- 架构设计：`docs/4.28架构迁移新设计.md` 第三节（项目结构）
- Rust2 前端配置：`Rust2/package.json`, `Rust2/tsconfig.json`, `Rust2/tailwind.config.js`, `Rust2/vite.config.ts`, `Rust2/postcss.config.js`
- Rust2 Tauri 配置：`Rust2/src-tauri/Cargo.toml`, `Rust2/src-tauri/tauri.conf.json`
- Rust2 组件库：`Rust2/src/components/ui.tsx`（仅作参考，批次 0 不复制业务组件）

## 任务清单

### T-001: 创建 Cargo workspace 根
- 产出: `Rust3/Cargo.toml`（workspace 定义）
- 契约: members 列出 9 个 crate + src-tauri。resolver = "2"
- 测试: 文件存在且 TOML 语法正确

### T-002: 创建 9 个空 crate
- 产出: 每个 crate 有 `Cargo.toml` + `src/lib.rs`
- crate 清单（全部在 `Rust3/crates/` 下）:
  1. `tfp-core` — 核心模型和配置
  2. `tfp-providers` — Provider trait 定义
  3. `tfp-storage` — 数据持久化
  4. `tfp-engine` — 任务引擎
  5. `tfp-chat` — 聊天功能域
  6. `tfp-media` — 媒体生成域
  7. `tfp-audiolab` — 听析中心域
  8. `tfp-search` — 网络搜索域
  9. `tfp-speech` — Speech SDK FFI（仅声明，不引入 FFI 依赖）
- 契约: 每个 `lib.rs` 内容为 `//! {crate 描述}` 注释 + 一个空的 `#[cfg(test)] mod tests { #[test] fn it_works() { assert!(true); } }`
- 测试: `cargo check --workspace` 通过

### T-003: 创建 src-tauri 壳层
- 产出:
  - `Rust3/src-tauri/Cargo.toml` — 依赖 tauri 2 + 9 个本地 crate（path 依赖）
  - `Rust3/src-tauri/src/main.rs` — Tauri 标准入口（3 行）
  - `Rust3/src-tauri/src/lib.rs` — 空 `pub fn run() { tauri::Builder::default().run(tauri::generate_context!()).expect("error"); }`
  - `Rust3/src-tauri/tauri.conf.json` — 从 Rust2 复制，修改 identifier 为 `com.truefluent.rust3`
  - `Rust3/src-tauri/build.rs` — 标准 `tauri_build::build()`
- 契约: 对 9 个 crate 使用 `path = "../crates/tfp-xxx"` 依赖。**暂不加 speech-sdk FFI 依赖**（批次 2 再加）
- 依赖版本参考 `Rust2/src-tauri/Cargo.toml`，保持一致：tauri 2, serde 1, tokio 1, reqwest 0.12, rusqlite 0.32（bundled）, async-trait 0.1, thiserror 2, tracing 0.1
- 测试: `cargo check -p truefluent-pro-r3`（package name 用 truefluent-pro-r3 避免和 Rust2 冲突）

### T-004: 创建 React 前端项目
- 读取: `Rust2/package.json`, `Rust2/tsconfig.json`, `Rust2/tsconfig.node.json`, `Rust2/vite.config.ts`, `Rust2/tailwind.config.js`, `Rust2/postcss.config.js`
- 产出:
  - `Rust3/package.json` — 从 Rust2 复制，去掉 react-virtuoso/react-markdown 等业务依赖，**保留**: react 19, radix, tailwind, zustand, i18next, lucide-react, clsx, tailwind-merge, framer-motion, @tauri-apps/api
  - `Rust3/tsconfig.json` — 从 Rust2 复制
  - `Rust3/tsconfig.node.json` — 从 Rust2 复制
  - `Rust3/vite.config.ts` — 从 Rust2 复制，去掉多页面入口（floating-*.html）
  - `Rust3/tailwind.config.js` — 从 Rust2 复制
  - `Rust3/postcss.config.js` — 从 Rust2 复制（如存在）
  - `Rust3/index.html` — 从 Rust2 复制
- 契约: 不运行 `npm install`（用户自行执行）。文件语法正确即可。
- 测试: 无（npm install 在批次 0 不做）

### T-005: 创建前端目录骨架
- 产出:
  - `Rust3/src/main.tsx` — React 入口，挂载 `<App />`
  - `Rust3/src/App.tsx` — 空壳，只渲染 `<div>Rust3 Shell</div>`
  - `Rust3/src/index.css` — 从 Rust2 复制（Tailwind 基础样式）
  - `Rust3/src/vite-env.d.ts` — 从 Rust2 复制
  - `Rust3/src/lib/i18n.ts` — 从 Rust2 复制
  - `Rust3/src/lib/locales/zh-CN.json` — 仅保留 `{"app":{"name":"译见 Pro"}}`
  - `Rust3/src/lib/locales/en.json` — 仅保留 `{"app":{"name":"TrueFluentPro"}}`
  - `Rust3/src/lib/tauri-api.ts` — 空文件，顶部注释 `// IPC 契约层 — 批次 1 填充`
  - `Rust3/src/stores/` — 空目录
  - `Rust3/src/views/` — 空目录
  - `Rust3/src/components/` — 空目录
- 契约: 所有 TSX 中**零中文硬编码**（用 i18n t() 或英文占位符）
- 测试: 无（npm install 未做，tsc 暂不跑）

### T-006: 创建端点加载器测试辅助
- 产出: `Rust3/tests/common/mod.rs`
- 契约:
  `ust
  use rusqlite::{Connection, OpenFlags};
  use serde::Deserialize;

  #[derive(Debug, Deserialize)]
  pub struct TestAppConfig {
      pub endpoints: Vec<TestEndpoint>,
  }
  #[derive(Debug, Deserialize)]
  pub struct TestEndpoint {
      pub id: String,
      pub name: String,
      pub endpoint_type: String,
      pub url: String,
      pub api_key: String,
      pub enabled: bool,
      // 其余字段用 #[serde(flatten)] extra: serde_json::Value
  }

  pub fn load_rust2_endpoints() -> Vec<TestEndpoint> {
      let db_path = dirs::data_dir().unwrap().join("com.truefluent.pro/truefluent.db");
      // ... 从 kv_store 读取 app_config JSON → 反序列化 → 过滤 enabled
  }
  `
- 注意: 需要在 workspace Cargo.toml 中添加 dev-dependencies: rusqlite, serde, serde_json, dirs
- 测试: `cargo test --test common` 能编译（但不需要真的连 DB，test 函数标 `#[ignore]` 因为需要 Rust2 DB 存在）

### T-007: 创建 .gitignore
- 产出: `Rust3/.gitignore`
- 内容: target/, node_modules/, dist/, *.db, .env

## 禁止事项
- ❌ 不写任何业务逻辑代码（模型定义、Provider 实现、命令处理等全部留到后续批次）
- ❌ 不从 Rust2 复制任何 .rs 业务文件
- ❌ 不从 Rust2 复制任何视图组件（views/, components/ui.tsx 等）
- ❌ 不运行 `npm install`（用户环境未知，留给用户）
- ❌ crate 之间暂不声明依赖关系（批次 1 再加）— 每个 crate 的 Cargo.toml 的 [dependencies] 为空
- ❌ 前端 TSX 文件中不允许出现中文字面量

## 退出标准
1. `cd Rust3 && cargo check --workspace` — 0 errors
2. `Rust3/` 目录结构与上述 T-001 ~ T-007 完全匹配
3. 9 个 crate 各有 lib.rs + 空测试
4. `cargo test --workspace` — 9 个 test passed（每个 crate 的 it_works）
