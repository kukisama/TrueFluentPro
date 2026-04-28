use tauri::State;
use tfp_core::VendorProfile;
use tfp_providers::ProviderInfo;

use crate::state::AppState;

#[tauri::command]
pub async fn list_providers(state: State<'_, AppState>) -> Result<Vec<ProviderInfo>, String> {
    let providers = state.providers.read().await;
    Ok(providers.list_providers())
}

#[tauri::command]
pub async fn refresh_providers(state: State<'_, AppState>) -> Result<(), String> {
    let config = state.config.read().await;
    let endpoints = config.endpoints.clone();
    drop(config);

    crate::register_providers_async(&*state, &endpoints).await;
    Ok(())
}

#[tauri::command]
pub async fn get_vendor_profiles() -> Result<Vec<VendorProfile>, String> {
    Ok(tfp_providers::load_profiles())
}
