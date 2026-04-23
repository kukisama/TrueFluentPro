//! Configuration loader using figment.

use domain::config::GatewayConfig;
use figment::{Figment, providers::{Env, Toml, Format}};

pub fn load_config() -> anyhow::Result<GatewayConfig> {
    let config = Figment::new()
        .merge(Toml::file("config/default.toml"))
        .merge(Toml::file("config/local.toml"))
        .merge(Env::prefixed("GATEWAY_").split("_"))
        .extract::<GatewayConfig>()?;

    Ok(config)
}
