use rusqlite::params;
use serde::{Deserialize, Serialize};

use crate::db::{map_db_err, Database};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SavedImage {
    pub id: String,
    pub prompt: String,
    pub revised_prompt: Option<String>,
    pub file_path: String,
    pub file_size: i64,
    pub width: Option<u32>,
    pub height: Option<u32>,
    pub model_id: Option<String>,
    pub endpoint_id: Option<String>,
    pub generate_seconds: Option<f64>,
    pub source: String,
    pub created_at: String,
}

impl Database {
    pub async fn add_saved_image(&self, img: &SavedImage) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO saved_images (id, prompt, revised_prompt, file_path, file_size,
             width, height, model_id, endpoint_id, generate_seconds, source, created_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)",
            params![
                img.id,
                img.prompt,
                img.revised_prompt,
                img.file_path,
                img.file_size,
                img.width,
                img.height,
                img.model_id,
                img.endpoint_id,
                img.generate_seconds,
                img.source,
                img.created_at,
            ],
        )
        .map_err(map_db_err)?;
        Ok(())
    }

    pub async fn list_saved_images(&self, limit: u32) -> tfp_core::Result<Vec<SavedImage>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn
            .prepare(
                "SELECT id, prompt, revised_prompt, file_path, file_size,
                 width, height, model_id, endpoint_id, generate_seconds, source, created_at
                 FROM saved_images ORDER BY created_at DESC LIMIT ?1",
            )
            .map_err(map_db_err)?;
        let rows = stmt
            .query_map(params![limit], |row| {
                Ok(SavedImage {
                    id: row.get(0)?,
                    prompt: row.get(1)?,
                    revised_prompt: row.get(2)?,
                    file_path: row.get(3)?,
                    file_size: row.get(4)?,
                    width: row.get(5)?,
                    height: row.get(6)?,
                    model_id: row.get(7)?,
                    endpoint_id: row.get(8)?,
                    generate_seconds: row.get(9)?,
                    source: row.get(10)?,
                    created_at: row.get(11)?,
                })
            })
            .map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }
}

#[cfg(test)]
mod tests {
    use crate::Database;

    use super::SavedImage;

    #[tokio::test]
    async fn test_saved_image_crud() {
        let db = Database::open_in_memory().unwrap();

        let img = SavedImage {
            id: "img-001".into(),
            prompt: "A sunset over mountains".into(),
            revised_prompt: Some("A beautiful sunset".into()),
            file_path: "/images/sunset.png".into(),
            file_size: 102400,
            width: Some(1024),
            height: Some(1024),
            model_id: Some("gpt-image-2".into()),
            endpoint_id: Some("ep-1".into()),
            generate_seconds: Some(12.5),
            source: "media_center".into(),
            created_at: "2026-04-28T12:00:00Z".into(),
        };
        db.add_saved_image(&img).await.unwrap();

        let img2 = SavedImage {
            id: "img-002".into(),
            prompt: "A cat playing".into(),
            revised_prompt: None,
            file_path: "/images/cat.png".into(),
            file_size: 51200,
            width: Some(512),
            height: Some(512),
            model_id: None,
            endpoint_id: None,
            generate_seconds: None,
            source: "media_center".into(),
            created_at: "2026-04-28T13:00:00Z".into(),
        };
        db.add_saved_image(&img2).await.unwrap();

        // List all
        let all = db.list_saved_images(50).await.unwrap();
        assert_eq!(all.len(), 2);
        // Ordered by created_at DESC
        assert_eq!(all[0].id, "img-002");
        assert_eq!(all[1].id, "img-001");

        // List with limit
        let limited = db.list_saved_images(1).await.unwrap();
        assert_eq!(limited.len(), 1);
        assert_eq!(limited[0].id, "img-002");

        // Verify fields
        assert_eq!(all[1].prompt, "A sunset over mountains");
        assert_eq!(
            all[1].revised_prompt.as_deref(),
            Some("A beautiful sunset")
        );
        assert_eq!(all[1].file_size, 102400);
        assert_eq!(all[1].width, Some(1024));
    }
}
