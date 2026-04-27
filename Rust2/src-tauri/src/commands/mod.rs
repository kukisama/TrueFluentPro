//! N-07: commands.rs 拆分为按职责域的子模块
//!
//! 原 commands.rs 1,792 行 → 7 个子模块
//! - config: 端点 CRUD + 导入/导出 + 存储验证 + Provider 热重载
//! - translate: 翻译 + 实时翻译 + 语言列表 + Provider 查询
//! - media: AI 补全 + 流式补全 + 图片 + 视频 + 提示词优化 + 图片管道
//! - session: 会话 + 消息 CRUD + 翻译历史
//! - audio: 音频库 + 生命周期 + 任务引擎
//! - test: 端点测试 + 模型发现 + 厂商资料包
//! - system: 系统信息 + 文件读写 + 沙箱 + 计费

pub mod config;
pub mod translate;
pub mod media;
pub mod session;
pub mod audio;
pub mod test;
pub mod system;
pub mod auth;

// Re-export all public items so callers can use commands::xxx
pub use config::*;
pub use translate::*;
pub use media::*;
pub use session::*;
pub use audio::*;
pub use test::*;
pub use system::*;
pub use auth::*;
