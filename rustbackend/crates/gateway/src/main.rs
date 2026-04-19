//! TrueFluentPro Gateway — main entry point.

mod config;
mod state;
mod error;
mod middleware;
mod routes;

use std::sync::Arc;
use tracing::info;

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    // Load .env if present
    let _ = dotenvy::dotenv();

    // Load configuration
    let cfg = config::load_config()?;

    // Initialize observability
    observability::init(&cfg.observability.log_level, &cfg.observability.log_format);

    info!("TrueFluentPro Gateway starting...");

    // Build application state
    let app_state = state::AppState::build(cfg.clone()).await?;
    let shared_state = Arc::new(app_state);

    // Build router
    let app = routes::build_router(shared_state.clone());

    // Bind and serve
    let listener = tokio::net::TcpListener::bind(&cfg.server.bind).await?;
    info!("Listening on {}", cfg.server.bind);

    axum::serve(listener, app)
        .with_graceful_shutdown(shutdown_signal())
        .await?;

    info!("Gateway shut down gracefully");
    Ok(())
}

async fn shutdown_signal() {
    tokio::signal::ctrl_c()
        .await
        .expect("failed to install CTRL+C handler");
    info!("Shutdown signal received");
}
