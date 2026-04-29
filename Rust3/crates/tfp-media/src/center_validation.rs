//! Center workspace reference image validation.

/// Maximum reference images for image workspaces.
pub const MAX_IMAGE_REFS: usize = 8;

/// Maximum reference images for video workspaces.
pub const MAX_VIDEO_REFS: usize = 1;

/// Validate that adding `adding` reference images won't exceed limits.
///
/// `kind` should be "canvas_image" or "canvas_video" (session_type), or "image"/"video" (media_kind).
pub fn validate_reference_count(kind: &str, existing_count: usize, adding: usize) -> Result<(), String> {
    let max = match kind {
        "canvas_video" | "video" => MAX_VIDEO_REFS,
        _ => MAX_IMAGE_REFS,
    };
    let total = existing_count.saturating_add(adding);
    if total > max {
        Err(format!(
            "Reference image limit exceeded: max {} for {}, already have {}, trying to add {}",
            max, kind, existing_count, adding
        ))
    } else {
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_validate_reference_count_image_ok() {
        assert!(validate_reference_count("canvas_image", 5, 3).is_ok());
        assert!(validate_reference_count("image", 7, 1).is_ok());
        assert!(validate_reference_count("canvas_image", 0, 8).is_ok());
    }

    #[test]
    fn test_validate_reference_count_image_overflow() {
        assert!(validate_reference_count("canvas_image", 8, 1).is_err());
        assert!(validate_reference_count("image", 7, 2).is_err());
        assert!(validate_reference_count("canvas_image", 0, 9).is_err());
    }

    #[test]
    fn test_validate_reference_count_video_ok() {
        assert!(validate_reference_count("canvas_video", 0, 1).is_ok());
        assert!(validate_reference_count("video", 0, 1).is_ok());
    }

    #[test]
    fn test_validate_reference_count_video_overflow() {
        assert!(validate_reference_count("canvas_video", 1, 1).is_err());
        assert!(validate_reference_count("video", 0, 2).is_err());
        assert!(validate_reference_count("canvas_video", 1, 0).is_ok());
    }
}
