use std::time::SystemTime;

/// Format current time as YYYYMMDD_HHMMSS for filenames (no chrono dependency)
pub fn format_timestamp_for_filename() -> String {
    let dur = SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)
        .unwrap_or_default();
    let secs = dur.as_secs();
    let days = secs / 86400;
    let time_of_day = secs % 86400;
    let hours = time_of_day / 3600;
    let minutes = (time_of_day % 3600) / 60;
    let seconds = time_of_day % 60;
    let (year, month, day) = days_to_ymd(days);
    format!("{year:04}{month:02}{day:02}_{hours:02}{minutes:02}{seconds:02}")
}

/// Convert days since epoch to (year, month, day)
fn days_to_ymd(days: u64) -> (u64, u64, u64) {
    // Civil calendar algorithm (Fliegel & Van Flandern)
    let z = days + 719468;
    let era = z / 146097;
    let doe = z - era * 146097;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let year = if m <= 2 { y + 1 } else { y };
    (year, m, d)
}

/// Save base64-encoded image to disk with atomic write
pub async fn save_image_to_disk(
    images_dir: &std::path::Path,
    base64_data: &str,
    format: &str,
) -> Result<(String, Vec<u8>), String> {
    use base64::Engine;

    tokio::fs::create_dir_all(images_dir)
        .await
        .map_err(|e| format!("Cannot create images dir: {e}"))?;

    let bytes = base64::engine::general_purpose::STANDARD
        .decode(base64_data)
        .map_err(|e| format!("Base64 decode error: {e}"))?;

    let ext = match format.to_lowercase().as_str() {
        "jpeg" | "jpg" => "jpg",
        "webp" => "webp",
        _ => "png",
    };

    let timestamp = format_timestamp_for_filename();
    let uuid8 = &uuid::Uuid::new_v4().to_string()[..8];
    let filename = format!("img_{timestamp}_{uuid8}.{ext}");
    let final_path = images_dir.join(&filename);
    let tmp_path = images_dir.join(format!("{filename}.tmp"));

    tokio::fs::write(&tmp_path, &bytes)
        .await
        .map_err(|e| format!("Write tmp file error: {e}"))?;
    tokio::fs::rename(&tmp_path, &final_path)
        .await
        .map_err(|e| format!("Rename error: {e}"))?;

    Ok((final_path.to_string_lossy().to_string(), bytes))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_format_timestamp_format() {
        let ts = format_timestamp_for_filename();
        assert_eq!(ts.len(), 15);
        assert_eq!(ts.as_bytes()[8], b'_');
        for (i, ch) in ts.chars().enumerate() {
            if i == 8 {
                assert_eq!(ch, '_');
            } else {
                assert!(ch.is_ascii_digit(), "char at {i} is not digit: {ch}");
            }
        }
    }

    #[test]
    fn test_format_timestamp_reasonable_year() {
        let ts = format_timestamp_for_filename();
        let year: u32 = ts[..4].parse().unwrap();
        assert!(year >= 2020 && year <= 2099, "year out of range: {year}");
    }

    #[test]
    fn test_days_to_ymd_epoch() {
        let (y, m, d) = days_to_ymd(0);
        assert_eq!((y, m, d), (1970, 1, 1));
    }

    #[test]
    fn test_days_to_ymd_2024() {
        // 2024-01-01 is day 19723
        let (y, m, d) = days_to_ymd(19723);
        assert_eq!((y, m, d), (2024, 1, 1));
    }
}
