//! 独立版 SQLite 用量与原文历史存储层（`usage.db`）。
//!
//! 职责分离：
//! - [`char_count`]：Unicode 扩展字素簇可见字数统计（与 C# `StringInfo` 完全一致）；
//! - [`schema`]：表结构创建与崩溃未结会话恢复；
//! - [`writer`]：守护进程持有的有界后台写入队列与隐私开关版本控制；
//! - [`report`]：工具窗报表专用的纯只读快照查询（绝不建库、绝不抢写锁）。

pub mod char_count;
pub mod report;
pub mod schema;
pub mod writer;

pub use char_count::count_visible_characters;
pub use report::{
    ActiveRun, PeriodComparison, ReportAvailability, ReportBucket, ReportDay, ReportHour,
    ReportSession, SessionTextAvailability, SessionTextEntry, SessionTextPage, UsageReport,
    UsageReportService,
};
pub use schema::default_usage_db_path;
pub use writer::{StopReason, UsageStatus, UsageWriterService};
