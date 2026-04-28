/// Image generation five-step pipeline: Route → Upload → Build → Execute → Land
/// Aligned with C# ImagePipelineRunner
pub mod pipeline;
pub mod catalog;
/// File upload deduplication cache
pub mod file_cache;
