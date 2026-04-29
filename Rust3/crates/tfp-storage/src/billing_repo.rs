use rusqlite::params;

use crate::db::{Database, map_db_err};
use tfp_core::{BillingRecord, BillingSummary, BillingByModel};

impl Database {
    pub async fn insert_billing_record(&self, record: &BillingRecord) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO billing_records (id, task_id, endpoint_id, model_id, prompt_tokens, completion_tokens, cost_usd, status, created_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)",
            params![
                record.id,
                record.task_id,
                record.endpoint_id,
                record.model_id,
                record.prompt_tokens,
                record.completion_tokens,
                record.cost_usd,
                record.status,
                record.created_at,
            ],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn get_billing_ledger_summary(&self) -> tfp_core::Result<BillingSummary> {
        let conn = self.conn().lock().await;

        let (total_prompt, total_completion, total_cost, count): (i64, i64, f64, i64) = conn.query_row(
            "SELECT COALESCE(SUM(prompt_tokens), 0), COALESCE(SUM(completion_tokens), 0),
                    COALESCE(SUM(cost_usd), 0.0), COUNT(*)
             FROM billing_records WHERE status = 'Committed'",
            [],
            |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?, row.get(3)?)),
        ).map_err(map_db_err)?;

        let mut stmt = conn.prepare(
            "SELECT model_id, SUM(prompt_tokens), SUM(completion_tokens),
                    COALESCE(SUM(cost_usd), 0.0), COUNT(*)
             FROM billing_records WHERE status = 'Committed'
             GROUP BY model_id ORDER BY COUNT(*) DESC"
        ).map_err(map_db_err)?;

        let by_model: Vec<BillingByModel> = stmt.query_map([], |row| {
            Ok(BillingByModel {
                model_id: row.get(0)?,
                prompt_tokens: row.get(1)?,
                completion_tokens: row.get(2)?,
                cost_usd: row.get(3)?,
                count: row.get(4)?,
            })
        }).map_err(map_db_err)?
          .filter_map(|r| r.ok())
          .collect();

        Ok(BillingSummary {
            total_prompt_tokens: total_prompt,
            total_completion_tokens: total_completion,
            total_cost_usd: total_cost,
            record_count: count,
            by_model,
        })
    }

    pub async fn get_billing_records_by_range(
        &self,
        from: &str,
        to: &str,
    ) -> tfp_core::Result<Vec<BillingRecord>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, task_id, endpoint_id, model_id, prompt_tokens, completion_tokens,
                    cost_usd, status, created_at
             FROM billing_records
             WHERE created_at >= ?1 AND created_at <= ?2
             ORDER BY created_at DESC LIMIT 1000"
        ).map_err(map_db_err)?;

        let records: Vec<BillingRecord> = stmt.query_map(params![from, to], |row| {
            Ok(BillingRecord {
                id: row.get(0)?,
                task_id: row.get(1)?,
                endpoint_id: row.get(2)?,
                model_id: row.get(3)?,
                prompt_tokens: row.get(4)?,
                completion_tokens: row.get(5)?,
                cost_usd: row.get(6)?,
                status: row.get(7)?,
                created_at: row.get(8)?,
            })
        }).map_err(map_db_err)?
          .filter_map(|r| r.ok())
          .collect();

        Ok(records)
    }

    pub async fn get_billing_by_endpoint(&self, endpoint_id: &str) -> tfp_core::Result<BillingSummary> {
        let conn = self.conn().lock().await;

        let (total_prompt, total_completion, total_cost, count): (i64, i64, f64, i64) = conn.query_row(
            "SELECT COALESCE(SUM(prompt_tokens), 0), COALESCE(SUM(completion_tokens), 0),
                    COALESCE(SUM(cost_usd), 0.0), COUNT(*)
             FROM billing_records WHERE status = 'Committed' AND endpoint_id = ?1",
            params![endpoint_id],
            |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?, row.get(3)?)),
        ).map_err(map_db_err)?;

        let mut stmt = conn.prepare(
            "SELECT model_id, SUM(prompt_tokens), SUM(completion_tokens),
                    COALESCE(SUM(cost_usd), 0.0), COUNT(*)
             FROM billing_records WHERE status = 'Committed' AND endpoint_id = ?1
             GROUP BY model_id ORDER BY COUNT(*) DESC"
        ).map_err(map_db_err)?;

        let by_model: Vec<BillingByModel> = stmt.query_map(params![endpoint_id], |row| {
            Ok(BillingByModel {
                model_id: row.get(0)?,
                prompt_tokens: row.get(1)?,
                completion_tokens: row.get(2)?,
                cost_usd: row.get(3)?,
                count: row.get(4)?,
            })
        }).map_err(map_db_err)?
          .filter_map(|r| r.ok())
          .collect();

        Ok(BillingSummary {
            total_prompt_tokens: total_prompt,
            total_completion_tokens: total_completion,
            total_cost_usd: total_cost,
            record_count: count,
            by_model,
        })
    }
}

#[cfg(test)]
mod tests {
    use crate::Database;
    use tfp_core::BillingRecord;

    fn make_record(id: &str, model: &str, ep: &str, prompt: i64, completion: i64, cost: f64) -> BillingRecord {
        BillingRecord {
            id: id.into(),
            task_id: None,
            endpoint_id: ep.into(),
            model_id: model.into(),
            prompt_tokens: prompt,
            completion_tokens: completion,
            cost_usd: Some(cost),
            created_at: "2026-04-29T10:00:00Z".into(),
            status: "Committed".into(),
        }
    }

    #[tokio::test]
    async fn test_billing_insert_and_query() {
        let db = Database::open_in_memory().unwrap();
        let record = make_record("br-1", "gpt-image-2", "ep-1", 100, 1767, 0.053);
        db.insert_billing_record(&record).await.unwrap();

        let summary = db.get_billing_ledger_summary().await.unwrap();
        assert_eq!(summary.record_count, 1);
        assert_eq!(summary.total_prompt_tokens, 100);
        assert_eq!(summary.total_completion_tokens, 1767);
        assert!((summary.total_cost_usd - 0.053).abs() < 0.001);
    }

    #[tokio::test]
    async fn test_billing_summary_empty() {
        let db = Database::open_in_memory().unwrap();
        let summary = db.get_billing_ledger_summary().await.unwrap();
        assert_eq!(summary.record_count, 0);
        assert_eq!(summary.total_prompt_tokens, 0);
        assert_eq!(summary.total_cost_usd, 0.0);
        assert!(summary.by_model.is_empty());
    }

    #[tokio::test]
    async fn test_billing_summary_multi_model() {
        let db = Database::open_in_memory().unwrap();
        db.insert_billing_record(&make_record("br-1", "gpt-image-2", "ep-1", 100, 1767, 0.053)).await.unwrap();
        db.insert_billing_record(&make_record("br-2", "gpt-image-1.5", "ep-1", 50, 272, 0.009)).await.unwrap();
        db.insert_billing_record(&make_record("br-3", "gpt-image-2", "ep-2", 200, 7033, 0.211)).await.unwrap();

        let summary = db.get_billing_ledger_summary().await.unwrap();
        assert_eq!(summary.record_count, 3);
        assert_eq!(summary.by_model.len(), 2);
        // gpt-image-2 has 2 records, should be first (ordered by count desc)
        assert_eq!(summary.by_model[0].model_id, "gpt-image-2");
        assert_eq!(summary.by_model[0].count, 2);
    }

    #[tokio::test]
    async fn test_billing_by_endpoint() {
        let db = Database::open_in_memory().unwrap();
        db.insert_billing_record(&make_record("br-1", "gpt-image-2", "ep-1", 100, 1767, 0.053)).await.unwrap();
        db.insert_billing_record(&make_record("br-2", "gpt-image-2", "ep-2", 200, 7033, 0.211)).await.unwrap();

        let summary = db.get_billing_by_endpoint("ep-1").await.unwrap();
        assert_eq!(summary.record_count, 1);
        assert_eq!(summary.total_prompt_tokens, 100);
    }

    #[tokio::test]
    async fn test_billing_records_by_range() {
        let db = Database::open_in_memory().unwrap();
        db.insert_billing_record(&make_record("br-1", "gpt-image-2", "ep-1", 100, 1767, 0.053)).await.unwrap();

        let records = db.get_billing_records_by_range("2026-04-01", "2026-04-30").await.unwrap();
        assert_eq!(records.len(), 1);
        assert_eq!(records[0].id, "br-1");

        let empty = db.get_billing_records_by_range("2025-01-01", "2025-01-31").await.unwrap();
        assert!(empty.is_empty());
    }
}
