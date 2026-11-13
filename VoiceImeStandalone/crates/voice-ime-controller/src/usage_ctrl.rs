//! 用量报表页签控制器（只读查询 1/7/30 天统计与会话原文游标分页）。

use voice_ime_storage::{SessionTextPage, UsageReport, UsageReportService};

pub fn load_usage_report(service: &UsageReportService, days: u32) -> Result<UsageReport, String> {
    service.read_report(days)
}

pub fn load_session_text_page(
    service: &UsageReportService,
    session_id: &str,
    after_sequence: i64,
) -> Result<SessionTextPage, String> {
    service.read_session_text(session_id, after_sequence, 100)
}
