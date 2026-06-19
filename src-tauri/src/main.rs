#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::Mutex;
use std::time::Instant;
use tauri::menu::{Menu, MenuItem};
use tauri::tray::TrayIconBuilder;
use tauri::webview::WebviewBuilder;
use tauri::window::{Color, WindowBuilder};
use tauri::{AppHandle, LogicalPosition, LogicalSize, Manager, WebviewUrl};

const SIDEBAR_WIDTH: f64 = 64.0;
const SETTINGS_LABEL: &str = "__settings";
const CTXMENU_LABEL: &str = "__ctxmenu";

// Google はWebView系UAのログインを弾くことがあるため素のChrome UAを名乗る
const CHROME_UA: &str = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36";

#[derive(Clone, Serialize, Deserialize)]
struct InstanceCfg {
    id: String,
    recipe: String,
    name: String,
    color: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    url: Option<String>,
    #[serde(default)]
    keep_awake: bool,
    // スリープ中の未読取得用(Chatworkレシピのみ)。平文保存なので注意
    #[serde(default, skip_serializing_if = "Option::is_none")]
    chatwork_token: Option<String>,
    // ユーザーがローカル画像を設定した場合のアイコン(data URL)
    #[serde(default, skip_serializing_if = "Option::is_none")]
    custom_icon: Option<String>,
    // UA偽装の上書き設定。None=レシピ既定に従う、Some(true/false)で明示指定
    #[serde(default, skip_serializing_if = "Option::is_none")]
    chrome_ua: Option<bool>,
    // 初回ナビゲーションがabout:blankで固まる(YouTube Music等)サービス向けの
    // 再ナビゲートキックを有効化する。既定はオフ(他サービスへの影響を避けるため)
    #[serde(default)]
    force_renavigate: bool,
}

#[derive(Clone, Serialize, Deserialize)]
struct Settings {
    #[serde(default = "default_sleep_minutes")]
    sleep_after_minutes: u64,
    #[serde(default = "default_true")]
    close_to_tray: bool,
    #[serde(default)]
    start_minimized: bool,
    #[serde(default)]
    autostart: bool,
    #[serde(default = "default_true")]
    notifications: bool,
    #[serde(default = "default_language")]
    language: String,
    #[serde(default = "default_true")]
    notification_sound: bool,
    #[serde(default)]
    notification_sound_path: String,
}

fn default_sleep_minutes() -> u64 {
    15
}

fn default_true() -> bool {
    true
}

fn default_language() -> String {
    "ja".to_string()
}

#[derive(Clone, Serialize, Deserialize)]
struct Config {
    settings: Settings,
    instances: Vec<InstanceCfg>,
}

impl Default for Config {
    fn default() -> Self {
        let inst = |id: &str, recipe: &str, name: &str, color: &str| InstanceCfg {
            id: id.into(),
            recipe: recipe.into(),
            name: name.into(),
            color: color.into(),
            url: None,
            keep_awake: false,
            chatwork_token: None,
            custom_icon: None,
            chrome_ua: None,
            force_renavigate: false,
        };
        Config {
            settings: Settings {
                sleep_after_minutes: default_sleep_minutes(),
                close_to_tray: true,
                start_minimized: false,
                autostart: false,
                notifications: true,
                language: default_language(),
                notification_sound: true,
                notification_sound_path: String::new(),
            },
            instances: vec![
                inst("gmail-work", "gmail", "Gmail (仕事)", "#5b8def"),
                inst("gmail-personal", "gmail", "Gmail (個人)", "#41b883"),
                inst("chatwork-a", "chatwork", "Chatwork (A)", "#5b8def"),
                inst("chatwork-b", "chatwork", "Chatwork (B)", "#e8a33d"),
            ],
        }
    }
}

#[derive(Default)]
struct RtState {
    active: Option<String>,
    // スリープ中サービスの「選択中(待機画面でスリープ解除ボタンを出す対象)」。
    // activateで他のWebViewが表示されてもhide_others()によるactive書き換えの
    // 影響を受けないよう、activeとは別のフィールドで管理する。
    pending_wake: Option<String>,
    hidden_since: HashMap<String, Instant>,
    unread: HashMap<String, u32>,
    mem_mb: HashMap<String, u64>,
    host_mem_mb: u64,
}

struct AppState {
    config: Mutex<Config>,
    rt: Mutex<RtState>,
}

fn config_path(app: &AppHandle) -> Result<PathBuf, String> {
    Ok(app
        .path()
        .app_config_dir()
        .map_err(|e| e.to_string())?
        .join("config.json"))
}

fn load_config(app: &AppHandle) -> Config {
    let Ok(path) = config_path(app) else {
        return Config::default();
    };
    std::fs::read_to_string(&path)
        .ok()
        .and_then(|s| serde_json::from_str(&s).ok())
        .unwrap_or_default()
}

fn save_config(app: &AppHandle, cfg: &Config) -> Result<(), String> {
    let path = config_path(app)?;
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    }
    std::fs::write(&path, serde_json::to_string_pretty(cfg).unwrap()).map_err(|e| e.to_string())
}

fn recipe_url(cfg: &InstanceCfg) -> Result<String, String> {
    match cfg.recipe.as_str() {
        "gmail" => Ok("https://mail.google.com".into()),
        "googlecalendar" => Ok("https://calendar.google.com".into()),
        "googlechat" => Ok("https://chat.google.com/".into()),
        "youtube" => Ok("https://www.youtube.com".into()),
        "messenger" => Ok("https://www.messenger.com".into()),
        "notion" => Ok("https://www.notion.so".into()),
        "chatwork" => Ok("https://www.chatwork.com".into()),
        "slack" => Ok("https://app.slack.com/client".into()),
        "discord" => Ok("https://discord.com/app".into()),
        "deckblue" => Ok("https://deck.blue".into()),
        "misskey" => Ok("https://misskey.io".into()),
        "x" => Ok("https://x.com".into()),
        "teams" => Ok("https://teams.microsoft.com".into()),
        "whatsapp" => Ok("https://web.whatsapp.com".into()),
        "telegram" => Ok("https://web.telegram.org/a/".into()),
        "linkedin" => Ok("https://www.linkedin.com".into()),
        "instagram" => Ok("https://www.instagram.com".into()),
        "reddit" => Ok("https://www.reddit.com".into()),
        "trello" => Ok("https://trello.com".into()),
        "github" => Ok("https://github.com".into()),
        "twitch" => Ok("https://www.twitch.tv".into()),
        "zoom" => Ok("https://app.zoom.us/wc/home".into()),
        "googledrive" => Ok("https://drive.google.com".into()),
        "googlekeep" => Ok("https://keep.google.com".into()),
        "threads" => Ok("https://www.threads.net".into()),
        "linear" => Ok("https://linear.app".into()),
        "generic" => cfg
            .url
            .clone()
            .filter(|u| !u.is_empty())
            .ok_or_else(|| "generic レシピには url が必要です".into()),
        other => Err(format!("不明なレシピ: {other}")),
    }
}

// Google系・Meta系・LinkedIn・NotionはWebView系UAだとログインを弾かれることがあるため、
// 既定でChrome UAを名乗る(個別インスタンスのchrome_uaで上書き可能)
fn recipe_default_chrome_ua(recipe: &str) -> bool {
    matches!(
        recipe,
        "gmail"
            | "googlecalendar"
            | "googlechat"
            | "youtube"
            | "messenger"
            | "notion"
            | "whatsapp"
            | "linkedin"
            | "instagram"
            | "googledrive"
            | "googlekeep"
            | "threads"
    )
}

fn extensions_dir(app: &AppHandle) -> Result<PathBuf, String> {
    let dir = app
        .path()
        .app_data_dir()
        .map_err(|e| e.to_string())?
        .join("extensions");
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    Ok(dir)
}

#[derive(Serialize)]
struct ExtensionInfo {
    path: String,
    name: String,
    version: String,
    description: String,
    manifest_version: u64,
}

// manifest.json内の "__MSG_key__" 形式の文字列を
// _locales/<default_locale>/messages.json で解決する(i18n未解決のまま表示される問題の対策)
fn resolve_extension_i18n(ext_dir: &std::path::Path, json: &serde_json::Value, raw: &str) -> String {
    let Some(key) = raw.strip_prefix("__MSG_").and_then(|s| s.strip_suffix("__")) else {
        return raw.to_string();
    };
    let default_locale = json["default_locale"].as_str().unwrap_or("en");
    let messages_path = ext_dir.join("_locales").join(default_locale).join("messages.json");
    let Ok(content) = std::fs::read_to_string(&messages_path) else {
        return raw.to_string();
    };
    let Ok(messages) = serde_json::from_str::<serde_json::Value>(&content) else {
        return raw.to_string();
    };
    // キー名はmessages.json内では大小文字を区別しないため、まず完全一致を試して
    // 見つからなければ小文字化して再検索する
    let entry = messages.get(key).or_else(|| {
        let lower = key.to_lowercase();
        messages.as_object()?.iter().find(|(k, _)| k.to_lowercase() == lower).map(|(_, v)| v)
    });
    let Some(message) = entry.and_then(|m| m["message"].as_str()) else {
        return raw.to_string();
    };
    // メッセージ内の "$PLACEHOLDER$" を二次解決する
    let mut result = message.to_string();
    if let Some(placeholders) = entry.and_then(|m| m["placeholders"].as_object()) {
        for (name, ph) in placeholders {
            if let Some(content) = ph["content"].as_str() {
                let needle = format!("${}$", name.to_uppercase());
                result = result.replace(&needle, content);
            }
        }
    }
    result
}

// extensions/ 直下の「manifest.jsonを含むフォルダ」を展開済み拡張として列挙する
fn scan_extensions(app: &AppHandle) -> Vec<ExtensionInfo> {
    let Ok(dir) = extensions_dir(app) else {
        return vec![];
    };
    let Ok(entries) = std::fs::read_dir(&dir) else {
        return vec![];
    };
    entries
        .flatten()
        .filter_map(|e| {
            let path = e.path();
            let manifest = path.join("manifest.json");
            if !manifest.is_file() {
                return None;
            }
            let json: serde_json::Value =
                serde_json::from_str(&std::fs::read_to_string(&manifest).ok()?).ok()?;
            let fallback = path.file_name()?.to_string_lossy().to_string();
            let name = json["name"].as_str().unwrap_or(&fallback);
            let description = json["description"].as_str().unwrap_or("");
            Some(ExtensionInfo {
                name: resolve_extension_i18n(&path, &json, name),
                description: resolve_extension_i18n(&path, &json, description),
                path: path.to_string_lossy().to_string(),
                version: json["version"].as_str().unwrap_or("?").to_string(),
                manifest_version: json["manifest_version"].as_u64().unwrap_or(2),
            })
        })
        .collect()
}

// 生成済みWebViewのWebView2プロファイルに展開済み拡張を読み込む
fn load_extensions_into(webview: &tauri::Webview, ext_paths: Vec<PathBuf>) {
    if ext_paths.is_empty() {
        return;
    }
    let label = webview.label().to_string();
    let _ = webview.with_webview(move |pw| {
        use webview2_com::Microsoft::Web::WebView2::Win32::{
            ICoreWebView2Profile7, ICoreWebView2_13,
        };
        use webview2_com::ProfileAddBrowserExtensionCompletedHandler;
        use windows::core::{Interface, HSTRING};
        unsafe {
            let result: windows::core::Result<()> = (|| {
                let core = pw.controller().CoreWebView2()?;
                let core13: ICoreWebView2_13 = core.cast()?;
                let profile = core13.Profile()?;
                let profile7: ICoreWebView2Profile7 = profile.cast()?;
                for path in &ext_paths {
                    let label = label.clone();
                    let shown = path.display().to_string();
                    let handler =
                        ProfileAddBrowserExtensionCompletedHandler::create(Box::new(
                            move |hr, _ext| {
                                match hr {
                                    Ok(_) => eprintln!("[ext] {label}: loaded {shown}"),
                                    Err(e) => eprintln!("[ext] {label}: failed {shown}: {e}"),
                                }
                                Ok(())
                            },
                        ));
                    profile7
                        .AddBrowserExtension(&HSTRING::from(path.as_os_str()), &handler)?;
                }
                Ok(())
            })();
            if let Err(e) = result {
                eprintln!("[ext] COM error: {e}");
            }
        }
    });
}

// 未読数を更新し、増えていれば(設定ONのとき)デスクトップ通知を出す
fn set_unread(app: &AppHandle, id: &str, count: u32) {
    use tauri_plugin_notification::NotificationExt;
    let state = app.state::<AppState>();
    let (prev, notify_enabled, name) = {
        let rt = state.rt.lock().unwrap();
        let config = state.config.lock().unwrap();
        (
            rt.unread.get(id).copied().unwrap_or(0),
            config.settings.notifications,
            config
                .instances
                .iter()
                .find(|i| i.id == id)
                .map(|i| i.name.clone())
                .unwrap_or_else(|| id.to_string()),
        )
    };
    state
        .rt
        .lock()
        .unwrap()
        .unread
        .insert(id.to_string(), count);
    update_overlay_badge(app);
    if notify_enabled && count > prev {
        let _ = app
            .notification()
            .builder()
            .title(&name)
            .body(format!("未読 {count} 件"))
            .show();
        play_notification_sound(app);
    }
}

// デスクトップ通知に合わせて通知音を再生する(設定でON、かつ未読が増えたときのみ)。
// notification_sound_path が空ならWindows標準の通知音、指定があればそのファイルを再生する
fn play_notification_sound(app: &AppHandle) {
    let (enabled, path) = {
        let state = app.state::<AppState>();
        let config = state.config.lock().unwrap();
        (
            config.settings.notification_sound,
            config.settings.notification_sound_path.clone(),
        )
    };
    if !enabled {
        return;
    }
    std::thread::spawn(move || unsafe {
        use windows::core::HSTRING;
        use windows::Win32::Media::Audio::{PlaySoundW, SND_ALIAS, SND_ASYNC, SND_FILENAME, SND_NODEFAULT};
        let flags = SND_ASYNC | SND_NODEFAULT;
        if path.is_empty() {
            let sound = HSTRING::from("Notification.Default");
            let _ = PlaySoundW(&sound, None, flags | SND_ALIAS);
        } else {
            let sound = HSTRING::from(path.as_str());
            let _ = PlaySoundW(&sound, None, flags | SND_FILENAME);
        }
    });
}

// CWSのURLまたは生IDから拡張ID(32文字のa-p)を抜き出す
fn parse_extension_id(input: &str) -> Option<String> {
    let input = input.trim();
    for token in input.split(|c: char| !c.is_ascii_alphabetic()) {
        if token.len() == 32 && token.chars().all(|c| ('a'..='p').contains(&c)) {
            return Some(token.to_string());
        }
    }
    None
}

// CRX3バイナリからZIP部分を取り出す(マジック"Cr24" + ヘッダ長はオフセット8のu32LE)
fn crx_to_zip(buf: &[u8]) -> Result<&[u8], String> {
    if buf.len() < 12 || &buf[0..4] != b"Cr24" {
        return Err("CRXファイルではありません".into());
    }
    let header_size = u32::from_le_bytes([buf[8], buf[9], buf[10], buf[11]]) as usize;
    buf.get(12 + header_size..)
        .ok_or_else(|| "CRXヘッダが壊れています".into())
}

// タイトル内の "(N)" 件数を拾う(先頭・中間どちらでも)。
// "(3) Chatwork" も "Inbox (3) - ... - Gmail" も対応する。
fn parse_unread(title: &str) -> u32 {
    let bytes = title.as_bytes();
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'(' {
            if let Some(rel_end) = title[i + 1..].find(')') {
                let inner = title[i + 1..i + 1 + rel_end].trim();
                // "(3)" や "(12)" は拾い、"(1.2 GB)" 等の混在は拒否
                if !inner.is_empty() && inner.bytes().all(|b| b.is_ascii_digit()) {
                    if let Ok(n) = inner.parse::<u32>() {
                        return n;
                    }
                }
            }
        }
        i += 1;
    }
    0
}

// ページ読み込み完了時に注入するJS。
// ・"unread"系のクラス名/aria-labelを持つ要素から数字バッジを拾い、
//   タイトル先頭に"(N)"を付与する(既存のparse_unreadが汎用的に検知する)
fn build_injected_script() -> String {
    format!(
        r#"(function(){{
  if (window.__ymbUnreadScanStarted) return;
  window.__ymbUnreadScanStarted = true;
  function scan() {{
    try {{
      var els = document.querySelectorAll('[class*="unread" i], [aria-label*="unread" i], [aria-label*="未読" i]');
      var best = 0;
      for (var i = 0; i < els.length; i++) {{
        var t = (els[i].textContent || '').trim();
        if (/^\d{{1,3}}$/.test(t)) {{
          var n = parseInt(t, 10);
          if (n > best) best = n;
        }}
      }}
      var base = (window.__ymbBaseTitle || document.title).replace(/^\(\d+\)\s*/, '');
      window.__ymbBaseTitle = base;
      var wanted = best > 0 ? '(' + best + ') ' + base : base;
      if (document.title !== wanted) document.title = wanted;
    }} catch (e) {{}}
  }}
  setInterval(scan, 5000);
  scan();
}})();"#
    )
}

// タスクバーの未読バッジ(赤い丸)用アイコンをその場で生成する
fn badge_icon() -> tauri::image::Image<'static> {
    let size: u32 = 32;
    let mut rgba = vec![0u8; (size * size * 4) as usize];
    let center = size as f32 / 2.0;
    let radius = center - 1.0;
    for y in 0..size {
        for x in 0..size {
            let dx = x as f32 + 0.5 - center;
            let dy = y as f32 + 0.5 - center;
            if (dx * dx + dy * dy).sqrt() <= radius {
                let idx = ((y * size + x) * 4) as usize;
                rgba[idx] = 0xe0;
                rgba[idx + 1] = 0x5d;
                rgba[idx + 2] = 0x5d;
                rgba[idx + 3] = 0xff;
            }
        }
    }
    tauri::image::Image::new_owned(rgba, size, size)
}

// 全インスタンスの未読数を合計し、タスクバーに赤丸バッジを表示/非表示する
fn update_overlay_badge(app: &AppHandle) {
    let Some(window) = app.get_window("main") else {
        return;
    };
    let total: u32 = app
        .state::<AppState>()
        .rt
        .lock()
        .unwrap()
        .unread
        .values()
        .sum();
    let icon = if total > 0 { Some(badge_icon()) } else { None };
    let _ = window.set_overlay_icon(icon);
}

fn relayout(window: &tauri::Window) {
    let Ok(scale) = window.scale_factor() else {
        return;
    };
    let Ok(size) = window.inner_size() else {
        return;
    };
    let size = size.to_logical::<f64>(scale);
    for wv in window.webviews() {
        let (pos, sz) = if wv.label() == "sidebar" {
            (
                LogicalPosition::new(0.0, 0.0),
                LogicalSize::new(SIDEBAR_WIDTH, size.height),
            )
        } else {
            (
                LogicalPosition::new(SIDEBAR_WIDTH, 0.0),
                LogicalSize::new(size.width - SIDEBAR_WIDTH, size.height),
            )
        };
        let _ = wv.set_position(pos);
        let _ = wv.set_size(sz);
    }
}

fn service_area(window: &tauri::Window) -> Result<(LogicalPosition<f64>, LogicalSize<f64>), String> {
    let scale = window.scale_factor().map_err(|e| e.to_string())?;
    let size = window
        .inner_size()
        .map_err(|e| e.to_string())?
        .to_logical::<f64>(scale);
    Ok((
        LogicalPosition::new(SIDEBAR_WIDTH, 0.0),
        LogicalSize::new(size.width - SIDEBAR_WIDTH, size.height),
    ))
}

// 表示中以外を隠して hidden_since を記録する
// 注意: Window::webviews()はワーカースレッドから呼ぶと空を返すため
// Manager::webviews()(アプリ全体)を使うこと
fn hide_others(app: &AppHandle, keep: &str) {
    let state = app.state::<AppState>();
    let mut rt = state.rt.lock().unwrap();
    for (label, wv) in app.webviews() {
        if label != "sidebar" && label != keep {
            let _ = wv.hide();
            rt.hidden_since.entry(label).or_insert_with(Instant::now);
        }
    }
    rt.hidden_since.remove(keep);
    rt.active = Some(keep.to_string());
}

// WebViewを生成するコマンドは必ず async fn にすること。
// 同期コマンドはメインスレッドで実行され、add_child(メインスレッドへの
// ディスパッチ待ち)がデッドロックする。
#[tauri::command]
async fn activate(app: AppHandle, id: String) -> Result<(), String> {
    {
        let state = app.state::<AppState>();
        let mut rt = state.rt.lock().unwrap();
        if rt.pending_wake.as_deref() == Some(id.as_str()) {
            rt.pending_wake = None;
        }
    }
    let window = app.get_window("main").ok_or("main window not found")?;

    if let Some(wv) = app.get_webview(&id) {
        wv.show().map_err(|e| e.to_string())?;
        hide_others(&app, &id);
        return Ok(());
    }

    let cfg = {
        let state = app.state::<AppState>();
        let config = state.config.lock().unwrap();
        config
            .instances
            .iter()
            .find(|i| i.id == id)
            .cloned()
            .ok_or("unknown instance")?
    };
    let url = recipe_url(&cfg)?;
    let use_chrome_ua = cfg
        .chrome_ua
        .unwrap_or_else(|| recipe_default_chrome_ua(&cfg.recipe));
    let ua = use_chrome_ua.then_some(CHROME_UA);

    // インスタンスごとに別のユーザーデータフォルダ = セッション完全分離
    let profile = app
        .path()
        .app_data_dir()
        .map_err(|e| e.to_string())?
        .join("profiles")
        .join(&id);
    std::fs::create_dir_all(&profile).map_err(|e| e.to_string())?;

    let parsed = url.parse::<tauri::Url>().map_err(|e| e.to_string())?;
    let app2 = app.clone();
    let app3 = app.clone();
    let script = build_injected_script();
    let target_url = parsed.clone();
    let blank_retries = std::sync::Arc::new(std::sync::atomic::AtomicU32::new(0));
    let mut builder = WebviewBuilder::new(&id, WebviewUrl::External(parsed))
        .data_directory(profile)
        .on_document_title_changed(move |wv, title| {
            set_unread(&app2, wv.label(), parse_unread(&title));
        })
        .on_page_load(move |wv, payload| {
            if payload.event() == tauri::webview::PageLoadEvent::Finished {
                // 初回ナビゲーションがリダイレクトを経由するURL(googlecalendar/youtube music等)では
                // about:blankに着地してしまうことがあり、再度about:blankになるケースもあるため
                // 上限回数までnavigateで再ナビゲートする
                if payload.url().as_str() == "about:blank"
                    && blank_retries.fetch_add(1, std::sync::atomic::Ordering::SeqCst) < 5
                {
                    let _ = wv.navigate(target_url.clone());
                    return;
                }
                let _ = wv.eval(&script);
            }
        })
        // OAuthポップアップやtarget="_blank"等の別ウインドウ遷移を
        // 通常のタイトルバー付きウインドウとして開く(既定の枠なしポップアップより扱いやすい)
        .on_new_window(move |url, features| {
            let label = format!(
                "popup-{}",
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap()
                    .as_nanos()
            );
            let title = url.host_str().unwrap_or("popup").to_string();
            let result = tauri::WebviewWindowBuilder::new(&app3, label, WebviewUrl::External(url))
                .window_features(features)
                .title(title)
                .build();
            match result {
                Ok(window) => tauri::webview::NewWindowResponse::Create { window },
                Err(_) => tauri::webview::NewWindowResponse::Allow,
            }
        });
    if let Some(ua) = ua {
        builder = builder.user_agent(ua);
    }
    // クラッシュレポート用crashpad-handlerプロセス(インスタンスごとに2個生成される)を
    // 無効化してメモリ/プロセス数を削減する。既定の--disable-features指定を維持しつつ追加。
    builder = builder.additional_browser_args(
        "--disable-features=msWebOOUI,msPdfOOUI,msSmartScreenProtection --disable-breakpad",
    );
    // 拡張機能を使うにはWebView2環境オプションで有効化が必要
    let ext_paths: Vec<PathBuf> = scan_extensions(&app)
        .into_iter()
        .map(|e| PathBuf::from(e.path))
        .collect();
    if !ext_paths.is_empty() {
        builder = builder.browser_extensions_enabled(true);
    }

    let (pos, size) = service_area(&window)?;
    let wv = window.add_child(builder, pos, size).map_err(|e| e.to_string())?;
    load_extensions_into(&wv, ext_paths);
    hide_others(&app, &id);

    // WebviewBuilderの初回ナビゲーションがPageLoadEventを発火させず
    // about:blankのまま固まることがある(youtube music等)ため、
    // 設定で有効化されている場合は少し待って明示的にnavigateし直す。
    // これによりon_page_load以降のabout:blank再試行ロジックが正常に動き出す。
    if cfg.force_renavigate {
        let wv2 = wv.clone();
        let fallback_url = url.clone();
        std::thread::spawn(move || {
            std::thread::sleep(std::time::Duration::from_millis(2000));
            if let Ok(u) = fallback_url.parse::<tauri::Url>() {
                let _ = wv2.navigate(u);
            }
        });
    }
    Ok(())
}

#[tauri::command]
async fn get_extensions(app: AppHandle) -> Vec<ExtensionInfo> {
    scan_extensions(&app)
}

// CWSから指定IDのCRXをダウンロードしてdestに展開する
// (curl.exe / tar.exe はWindows 10以降標準搭載)
fn fetch_and_extract_crx(id: &str, dest: &std::path::Path) -> Result<(), String> {
    let url = format!(
        "https://clients2.google.com/service/update2/crx?response=redirect&prodversion=137.0.0.0&acceptformat=crx3&x=id%3D{id}%26installsource%3Dondemand%26uc"
    );
    let tmp = std::env::temp_dir();
    let pid = std::process::id();
    let crx_path = tmp.join(format!("ymb-{id}-{pid}.crx"));
    let zip_path = tmp.join(format!("ymb-{id}-{pid}.zip"));

    let out = std::process::Command::new("curl.exe")
        .args(["-L", "--fail", "-sS", "-o"])
        .arg(&crx_path)
        .arg(&url)
        .output()
        .map_err(|e| format!("curl実行失敗: {e}"))?;
    if !out.status.success() {
        return Err(format!(
            "ダウンロード失敗(IDが正しいか確認してください): {}",
            String::from_utf8_lossy(&out.stderr)
        ));
    }

    let crx = std::fs::read(&crx_path).map_err(|e| e.to_string())?;
    let zip = crx_to_zip(&crx)?;
    std::fs::write(&zip_path, zip).map_err(|e| e.to_string())?;

    std::fs::create_dir_all(dest).map_err(|e| e.to_string())?;
    let out = std::process::Command::new("tar.exe")
        .arg("-xf")
        .arg(&zip_path)
        .arg("-C")
        .arg(dest)
        .output()
        .map_err(|e| format!("tar実行失敗: {e}"))?;
    let _ = std::fs::remove_file(&crx_path);
    let _ = std::fs::remove_file(&zip_path);
    if !out.status.success() {
        return Err(format!(
            "展開失敗: {}",
            String::from_utf8_lossy(&out.stderr)
        ));
    }
    if !dest.join("manifest.json").is_file() {
        return Err("展開結果にmanifest.jsonがありません".into());
    }
    Ok(())
}

#[tauri::command]
async fn install_extension_from_cws(app: AppHandle, id_or_url: String) -> Result<String, String> {
    let id = parse_extension_id(&id_or_url)
        .ok_or("拡張IDが見つかりません。CWSのURLか32文字のIDを入力してください")?;
    let dest = extensions_dir(&app)?.join(&id);
    let _ = std::fs::remove_dir_all(&dest);
    if let Err(e) = fetch_and_extract_crx(&id, &dest) {
        let _ = std::fs::remove_dir_all(&dest);
        return Err(e);
    }
    Ok(id)
}

// "1.2.3" 形式のバージョン文字列を比較可能な数値列に変換する
fn version_tuple(v: &str) -> Vec<u64> {
    v.split('.').map(|p| p.parse().unwrap_or(0)).collect()
}

// CWSからインストール済みの拡張(フォルダ名=32文字のCWS拡張ID)について、
// 最新版をダウンロードしバージョンが上がっていれば差し替える
#[tauri::command]
async fn update_extensions(app: AppHandle) -> Result<Vec<String>, String> {
    let dir = extensions_dir(&app)?;
    let mut updated = Vec::new();
    let entries = std::fs::read_dir(&dir).map_err(|e| e.to_string())?;
    for entry in entries.flatten() {
        let path = entry.path();
        let Some(name) = path.file_name().and_then(|n| n.to_str()) else {
            continue;
        };
        // unpacked拡張(手動配置)はCWS更新対象外
        if name.len() != 32 || !name.chars().all(|c| ('a'..='p').contains(&c)) {
            continue;
        }
        let id = name.to_string();
        let manifest_path = path.join("manifest.json");
        let Ok(content) = std::fs::read_to_string(&manifest_path) else {
            continue;
        };
        let Ok(json) = serde_json::from_str::<serde_json::Value>(&content) else {
            continue;
        };
        let current_version = json["version"].as_str().unwrap_or("0").to_string();
        let ext_name = resolve_extension_i18n(
            &path,
            &json,
            json["name"].as_str().unwrap_or(&id),
        );

        let tmp_dest = dir.join(format!("{id}.update-tmp"));
        let _ = std::fs::remove_dir_all(&tmp_dest);
        if fetch_and_extract_crx(&id, &tmp_dest).is_err() {
            let _ = std::fs::remove_dir_all(&tmp_dest);
            continue;
        }
        let new_version = std::fs::read_to_string(tmp_dest.join("manifest.json"))
            .ok()
            .and_then(|s| serde_json::from_str::<serde_json::Value>(&s).ok())
            .and_then(|j| j["version"].as_str().map(|s| s.to_string()))
            .unwrap_or_default();

        if version_tuple(&new_version) > version_tuple(&current_version) {
            let _ = std::fs::remove_dir_all(&path);
            let _ = std::fs::rename(&tmp_dest, &path);
            updated.push(format!("{ext_name} ({current_version} → {new_version})"));
        } else {
            let _ = std::fs::remove_dir_all(&tmp_dest);
        }
    }
    Ok(updated)
}

#[tauri::command]
async fn remove_extension(app: AppHandle, path: String) -> Result<(), String> {
    // extensions/ 配下のみ削除を許可する
    let dir = extensions_dir(&app)?;
    let target = PathBuf::from(&path);
    if !target.starts_with(&dir) {
        return Err("拡張機能フォルダ外は削除できません".into());
    }
    std::fs::remove_dir_all(&target).map_err(|e| e.to_string())
}

#[tauri::command]
async fn open_extensions_dir(app: AppHandle) -> Result<(), String> {
    let dir = extensions_dir(&app)?;
    std::process::Command::new("explorer")
        .arg(&dir)
        .spawn()
        .map_err(|e| e.to_string())?;
    Ok(())
}

#[tauri::command]
async fn open_settings(app: AppHandle, edit_id: Option<String>) -> Result<(), String> {
    let window = app.get_window("main").ok_or("main window not found")?;
    if let Some(wv) = app.get_webview(SETTINGS_LABEL) {
        wv.show().map_err(|e| e.to_string())?;
        if let Some(id) = &edit_id {
            let _ = wv.eval(&format!(
                "window.startEditById && window.startEditById({id:?})"
            ));
        }
    } else {
        let (pos, size) = service_area(&window)?;
        let url = match &edit_id {
            Some(id) => format!("settings.html?edit={id}"),
            None => "settings.html".into(),
        };
        window
            .add_child(
                WebviewBuilder::new(SETTINGS_LABEL, WebviewUrl::App(url.into())),
                pos,
                size,
            )
            .map_err(|e| e.to_string())?;
    }
    hide_others(&app, SETTINGS_LABEL);
    Ok(())
}

// サイドバーアイコンのクリック用。WebViewが生存していれば表示切替(activateと同じ)、
// スリープ中なら起動はせず「選択中」として記録し、待機画面にスリープ解除ボタンを出す。
#[tauri::command]
async fn select_instance(app: AppHandle, id: String) -> Result<(), String> {
    if app.get_webview(&id).is_some() {
        {
            let state = app.state::<AppState>();
            let mut rt = state.rt.lock().unwrap();
            rt.pending_wake = None;
        }
        return activate(app, id).await;
    }
    hide_others(&app, "welcome");
    {
        let state = app.state::<AppState>();
        let mut rt = state.rt.lock().unwrap();
        rt.pending_wake = Some(id);
    }
    if let Some(wv) = app.get_webview("welcome") {
        let _ = wv.show();
    }
    Ok(())
}

#[tauri::command]
async fn sleep_service(app: AppHandle, id: String) -> Result<(), String> {
    if let Some(wv) = app.get_webview(&id) {
        wv.close().map_err(|e| e.to_string())?;
    }
    let state = app.state::<AppState>();
    let mut rt = state.rt.lock().unwrap();
    rt.hidden_since.remove(&id);
    if rt.pending_wake.as_deref() == Some(id.as_str()) {
        rt.pending_wake = None;
    }
    if rt.active.as_deref() == Some(id.as_str()) {
        rt.active = None;
        drop(rt);
        if let Some(wv) = app.get_webview("welcome") {
            let _ = wv.show();
        }
    }
    Ok(())
}

// 拡張機能のオンボーディング/初期設定ページは初回読み込み時(chrome.runtime.onInstalled)
// にしか開かれないため、対象サービスのプロファイル内にある拡張機能の保存状態を削除して
// 次回起動時に「インストール直後」と同じ状態(初期設定が再度開く)に戻す。
// 1Password等の拡張に限らず、全拡張を対象とする汎用機能。
#[tauri::command]
async fn reset_extension_state(app: AppHandle, id: String) -> Result<(), String> {
    // ファイルロックを避けるため、生存中なら先にスリープする
    if app.get_webview(&id).is_some() {
        sleep_service(app.clone(), id.clone()).await?;
    }
    let profile = app
        .path()
        .app_data_dir()
        .map_err(|e| e.to_string())?
        .join("profiles")
        .join(&id)
        .join("EBWebView")
        .join("Default");
    if !profile.is_dir() {
        return Ok(());
    }
    for name in [
        "Extension State",
        "Local Extension Settings",
        "Sync Extension Settings",
        "Extension Rules",
        "Extension Scripts",
        "Managed Extension Settings",
    ] {
        let p = profile.join(name);
        if p.is_dir() {
            std::fs::remove_dir_all(&p).map_err(|e| e.to_string())?;
        }
    }
    // IndexedDB内の拡張機能オリジン(chrome-extension_*)のみ削除し、サイト側のデータは残す
    let idb = profile.join("IndexedDB");
    if let Ok(entries) = std::fs::read_dir(&idb) {
        for entry in entries.flatten() {
            if entry.file_name().to_string_lossy().starts_with("chrome-extension_") {
                let _ = std::fs::remove_dir_all(entry.path());
            }
        }
    }
    Ok(())
}

const LANG_JA: &str = include_str!("../../ui/lang/ja.json");
const LANG_EN: &str = include_str!("../../ui/lang/en.json");

// 翻訳辞書を返す。バンドル済みの既定値に対し、<app_data_dir>/lang/<language>.json
// が存在する場合はそのキーで上書きする(外部から言語ファイルを追加・修正可能にする)
#[tauri::command]
async fn get_translations(
    app: AppHandle,
    language: String,
) -> Result<HashMap<String, String>, String> {
    let base = match language.as_str() {
        "en" => LANG_EN,
        _ => LANG_JA,
    };
    let mut dict: HashMap<String, String> =
        serde_json::from_str(base).map_err(|e| e.to_string())?;
    if let Ok(dir) = app.path().app_data_dir() {
        let override_path = dir.join("lang").join(format!("{language}.json"));
        if let Ok(s) = std::fs::read_to_string(&override_path) {
            if let Ok(extra) = serde_json::from_str::<HashMap<String, String>>(&s) {
                dict.extend(extra);
            }
        }
    }
    Ok(dict)
}

// アクティブ/スリープ中に関わらず、生存しているWebViewがあればページを再読み込みする
#[tauri::command]
async fn reload_service(app: AppHandle, id: String) -> Result<(), String> {
    let wv = app
        .get_webview(&id)
        .ok_or("このサービスは現在スリープ中です(再読み込み対象がありません)")?;
    wv.eval("location.reload()").map_err(|e| e.to_string())?;
    Ok(())
}

// サイドバーのアイコン右クリックメニューを子WebViewとして表示する
// (サイドバーWebView自体は64px幅しかなく、メニューがその幅でクリップされてしまうため)
#[tauri::command]
async fn show_context_menu(app: AppHandle, id: String, alive: bool, x: f64, y: f64) -> Result<(), String> {
    let window = app.get_window("main").ok_or("main window not found")?;
    if let Some(wv) = app.get_webview(CTXMENU_LABEL) {
        let _ = wv.close();
    }
    let item_count = if alive { 3 } else { 2 };
    let height = 32.0 * item_count as f64;
    let scale = window.scale_factor().map_err(|e| e.to_string())?;
    let win_size = window
        .inner_size()
        .map_err(|e| e.to_string())?
        .to_logical::<f64>(scale);
    let y = y.min((win_size.height - height).max(0.0));
    let url = format!("ctxmenu.html?id={id}&alive={alive}");
    window
        .add_child(
            WebviewBuilder::new(CTXMENU_LABEL, WebviewUrl::App(url.into())),
            LogicalPosition::new(x, y),
            LogicalSize::new(120.0, height),
        )
        .map_err(|e| e.to_string())?;
    Ok(())
}

#[tauri::command]
async fn close_context_menu(app: AppHandle) -> Result<(), String> {
    if let Some(wv) = app.get_webview(CTXMENU_LABEL) {
        wv.close().map_err(|e| e.to_string())?;
    }
    Ok(())
}

// アプリ本体を再起動する
#[tauri::command]
fn restart_app(app: AppHandle) {
    app.restart();
}

#[derive(Serialize)]
struct UiInstance {
    id: String,
    name: String,
    color: String,
    letter: String,
    recipe: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    custom_icon: Option<String>,
    alive: bool,
    active: bool,
    unread: u32,
    mem_mb: u64,
}

#[tauri::command]
async fn ui_state(app: AppHandle) -> Result<Vec<UiInstance>, String> {
    let alive: Vec<String> = app.webviews().keys().cloned().collect();
    let state = app.state::<AppState>();
    let config = state.config.lock().unwrap();
    let rt = state.rt.lock().unwrap();
    Ok(config
        .instances
        .iter()
        .map(|i| {
            let letter = match i.recipe.as_str() {
                "gmail" => "G".into(),
                "chatwork" => "C".into(),
                "slack" => "S".into(),
                "discord" => "D".into(),
                "messenger" => "M".into(),
                "googlecalendar" => "Ca".into(),
                "notion" => "N".into(),
                "youtube" => "Y".into(),
                "deckblue" => "B".into(),
                "misskey" => "Mk".into(),
                "x" => "X".into(),
                "teams" => "T".into(),
                "googlechat" => "Gc".into(),
                "whatsapp" => "Wa".into(),
                "telegram" => "Tg".into(),
                "linkedin" => "Li".into(),
                "instagram" => "Ig".into(),
                "reddit" => "R".into(),
                "trello" => "Tr".into(),
                "github" => "Gh".into(),
                "twitch" => "Tw".into(),
                "zoom" => "Z".into(),
                "googledrive" => "Gd".into(),
                "googlekeep" => "Gk".into(),
                "threads" => "@".into(),
                "linear" => "Ln".into(),
                _ => i.name.chars().next().map(|c| c.to_string()).unwrap_or("?".into()),
            };
            UiInstance {
                id: i.id.clone(),
                name: i.name.clone(),
                color: i.color.clone(),
                letter,
                recipe: i.recipe.clone(),
                custom_icon: i.custom_icon.clone(),
                alive: alive.contains(&i.id),
                active: if alive.contains(&i.id) {
                    rt.active.as_deref() == Some(i.id.as_str())
                } else {
                    rt.pending_wake.as_deref() == Some(i.id.as_str())
                },
                unread: rt.unread.get(&i.id).copied().unwrap_or(0),
                mem_mb: rt.mem_mb.get(&i.id).copied().unwrap_or(0),
            }
        })
        .collect())
}

#[tauri::command]
async fn refresh_memory_now(app: AppHandle) -> Result<(), String> {
    let mut sys = sysinfo::System::new();
    refresh_memory(&app, &mut sys);
    Ok(())
}

#[tauri::command]
async fn host_memory_mb(app: AppHandle) -> u64 {
    app.state::<AppState>().rt.lock().unwrap().host_mem_mb
}

#[tauri::command]
async fn get_config(app: AppHandle) -> Config {
    app.state::<AppState>().config.lock().unwrap().clone()
}

// 設定(サービス一覧・各種設定)をJSONファイルへ書き出す。
// 別PCでこのファイルをインポートすれば同じサービス構成を復元できる。
// ※ログイン情報(WebView2プロファイル)は対象外で、サービス側で再ログインが必要。
#[tauri::command]
async fn export_settings(app: AppHandle) -> Result<bool, String> {
    use tauri_plugin_dialog::DialogExt;
    let snapshot = app.state::<AppState>().config.lock().unwrap().clone();
    let json = serde_json::to_string_pretty(&snapshot).map_err(|e| e.to_string())?;
    let Some(path) = app
        .dialog()
        .file()
        .add_filter("JSON", &["json"])
        .set_file_name("ymb-thatuation-settings.json")
        .blocking_save_file()
    else {
        return Ok(false);
    };
    let path = path.into_path().map_err(|e| e.to_string())?;
    std::fs::write(&path, json).map_err(|e| e.to_string())?;
    Ok(true)
}

// JSONファイルから設定を読み込み、現在の設定を置き換える。
// 反映には本体の再起動が必要(呼び出し側で案内する)。
#[tauri::command]
async fn import_settings(app: AppHandle) -> Result<bool, String> {
    use tauri_plugin_dialog::DialogExt;
    let Some(path) = app
        .dialog()
        .file()
        .add_filter("JSON", &["json"])
        .blocking_pick_file()
    else {
        return Ok(false);
    };
    let path = path.into_path().map_err(|e| e.to_string())?;
    let json = std::fs::read_to_string(&path).map_err(|e| e.to_string())?;
    let imported: Config = serde_json::from_str(&json).map_err(|e| e.to_string())?;
    {
        let state = app.state::<AppState>();
        let mut config = state.config.lock().unwrap();
        *config = imported.clone();
    }
    save_config(&app, &imported)?;
    Ok(true)
}

// 通知音用のカスタム音声ファイル(wav/mp3)を選択し、設定に保存する
#[tauri::command]
async fn select_notification_sound(app: AppHandle) -> Result<Option<String>, String> {
    use tauri_plugin_dialog::DialogExt;
    let Some(path) = app
        .dialog()
        .file()
        .add_filter("Audio", &["wav", "mp3"])
        .blocking_pick_file()
    else {
        return Ok(None);
    };
    let path = path.into_path().map_err(|e| e.to_string())?;
    let path = path.to_string_lossy().to_string();
    let snapshot = {
        let state = app.state::<AppState>();
        let mut config = state.config.lock().unwrap();
        config.settings.notification_sound_path = path.clone();
        config.clone()
    };
    save_config(&app, &snapshot)?;
    Ok(Some(path))
}

// 通知音をWindows標準に戻す
#[tauri::command]
async fn reset_notification_sound(app: AppHandle) -> Result<(), String> {
    let snapshot = {
        let state = app.state::<AppState>();
        let mut config = state.config.lock().unwrap();
        config.settings.notification_sound_path = String::new();
        config.clone()
    };
    save_config(&app, &snapshot)
}

// 通知音をテスト再生する
#[tauri::command]
async fn test_notification_sound(app: AppHandle) {
    play_notification_sound(&app);
}

#[tauri::command]
async fn add_instance(
    app: AppHandle,
    recipe: String,
    name: String,
    color: String,
    url: Option<String>,
) -> Result<(), String> {
    let id = format!(
        "{}-{}",
        recipe,
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_millis()
    );
    let cfg = InstanceCfg {
        id,
        recipe,
        name,
        color,
        url,
        keep_awake: false,
        chatwork_token: None,
        custom_icon: None,
        chrome_ua: None,
        force_renavigate: false,
    };
    recipe_url(&cfg)?; // レシピ妥当性チェック
    let state = app.state::<AppState>();
    let snapshot = {
        let mut config = state.config.lock().unwrap();
        config.instances.push(cfg);
        config.clone()
    };
    save_config(&app, &snapshot)
}

#[tauri::command]
async fn remove_instance(app: AppHandle, id: String) -> Result<(), String> {
    if let Some(wv) = app.get_webview(&id) {
        let _ = wv.close();
    }
    let state = app.state::<AppState>();
    let snapshot = {
        let mut config = state.config.lock().unwrap();
        config.instances.retain(|i| i.id != id);
        config.clone()
    };
    save_config(&app, &snapshot)?;
    // ログイン情報ごと削除(明示的な削除操作のため)
    if let Ok(dir) = app.path().app_data_dir() {
        let _ = std::fs::remove_dir_all(dir.join("profiles").join(&id));
    }
    Ok(())
}

#[tauri::command]
async fn update_instance(
    app: AppHandle,
    id: String,
    name: String,
    color: String,
    url: Option<String>,
    keep_awake: bool,
    chatwork_token: Option<String>,
    custom_icon: Option<String>,
    chrome_ua: Option<bool>,
    force_renavigate: bool,
) -> Result<(), String> {
    let state = app.state::<AppState>();
    let snapshot = {
        let mut config = state.config.lock().unwrap();
        let inst = config
            .instances
            .iter_mut()
            .find(|i| i.id == id)
            .ok_or("unknown instance")?;
        inst.name = name;
        inst.color = color;
        inst.keep_awake = keep_awake;
        if inst.recipe == "generic" {
            inst.url = url.filter(|u| !u.is_empty());
        }
        if inst.recipe == "chatwork" {
            inst.chatwork_token = chatwork_token.filter(|t| !t.is_empty());
        }
        inst.custom_icon = custom_icon.filter(|s| !s.is_empty());
        inst.chrome_ua = chrome_ua;
        inst.force_renavigate = force_renavigate;
        config.clone()
    };
    save_config(&app, &snapshot)
}

#[tauri::command]
async fn move_instance(app: AppHandle, id: String, delta: i32) -> Result<(), String> {
    let state = app.state::<AppState>();
    let snapshot = {
        let mut config = state.config.lock().unwrap();
        let pos = config
            .instances
            .iter()
            .position(|i| i.id == id)
            .ok_or("unknown instance")?;
        let new_pos = pos as i64 + delta as i64;
        if new_pos < 0 || new_pos >= config.instances.len() as i64 {
            return Ok(());
        }
        config.instances.swap(pos, new_pos as usize);
        config.clone()
    };
    save_config(&app, &snapshot)
}

#[tauri::command]
async fn update_settings(
    app: AppHandle,
    sleep_after_minutes: u64,
    close_to_tray: bool,
    start_minimized: bool,
    autostart: bool,
    notifications: bool,
    language: String,
    notification_sound: bool,
) -> Result<(), String> {
    use tauri_plugin_autostart::ManagerExt;
    let autolaunch = app.autolaunch();
    if autostart {
        autolaunch.enable().map_err(|e| e.to_string())?;
    } else {
        let _ = autolaunch.disable();
    }
    let state = app.state::<AppState>();
    let snapshot = {
        let mut config = state.config.lock().unwrap();
        config.settings.sleep_after_minutes = sleep_after_minutes;
        config.settings.close_to_tray = close_to_tray;
        config.settings.start_minimized = start_minimized;
        config.settings.autostart = autostart;
        config.settings.notifications = notifications;
        config.settings.language = language;
        config.settings.notification_sound = notification_sound;
        config.clone()
    };
    save_config(&app, &snapshot)
}

// 旧identifier(jp.yumebi.ferdium-next-poc)からのプロファイル移行
fn migrate_old_appdata(app: &AppHandle) {
    let Ok(new_dir) = app.path().app_data_dir() else {
        return;
    };
    let Some(parent) = new_dir.parent() else {
        return;
    };
    let old_dir = parent.join("jp.yumebi.ferdium-next-poc");
    if old_dir.exists() && !new_dir.exists() {
        match std::fs::rename(&old_dir, &new_dir) {
            Ok(_) => eprintln!("[migrate] {} -> {}", old_dir.display(), new_dir.display()),
            Err(e) => eprintln!("[migrate] failed: {e}"),
        }
    }
}

// インスタンスごとのWebView2プロセス群のメモリ使用量(MB)を集計する
fn refresh_memory(app: &AppHandle, sys: &mut sysinfo::System) {
    // cmd は明示的に要求しないと取得されない(プロファイルパス判定に必須)
    let kind = sysinfo::ProcessRefreshKind::nothing()
        .with_cmd(sysinfo::UpdateKind::Always)
        .with_memory();
    sys.refresh_processes_specifics(sysinfo::ProcessesToUpdate::All, true, kind);
    let state = app.state::<AppState>();
    let ids: Vec<String> = {
        let config = state.config.lock().unwrap();
        config.instances.iter().map(|i| i.id.clone()).collect()
    };
    // 自プロセスの子孫かどうかを親PIDチェーンをたどって判定する
    // (Windowsの共有WebView2ランタイムは他アプリのプロセスも"msedgewebview2"という
    // 名前で動いているため、名前だけでは自アプリ分と区別できない)
    let own_pid = sysinfo::get_current_pid().ok();
    let is_own_descendant = |mut pid: sysinfo::Pid| -> bool {
        for _ in 0..16 {
            if Some(pid) == own_pid {
                return true;
            }
            match sys.process(pid).and_then(|p| p.parent()) {
                Some(parent) => pid = parent,
                None => return false,
            }
        }
        false
    };
    let mut result: HashMap<String, u64> = HashMap::new();
    // 各インスタンスのプロファイルに紐づかないプロセス(本体exe・サイドバー/設定/待機画面の
    // WebView2ホストプロセス等)はアプリ本体のメモリ使用量として集計する
    let mut host_mb: u64 = 0;
    for p in sys.processes().values() {
        let name = p.name().to_string_lossy();
        if name.contains("ymb-thatuation") {
            host_mb += p.memory() / (1024 * 1024);
            continue;
        }
        if !name.contains("msedgewebview2") {
            continue;
        }
        let cmd = p
            .cmd()
            .iter()
            .map(|s| s.to_string_lossy())
            .collect::<Vec<_>>()
            .join(" ");
        let mut matched = false;
        for id in &ids {
            if cmd.contains(&format!("profiles\\{id}")) {
                *result.entry(id.clone()).or_insert(0) += p.memory() / (1024 * 1024);
                matched = true;
                break;
            }
        }
        if !matched && is_own_descendant(p.pid()) {
            host_mb += p.memory() / (1024 * 1024);
        }
    }
    let mut rt = state.rt.lock().unwrap();
    rt.mem_mb = result;
    rt.host_mem_mb = host_mb;
}

// スリープ中のChatworkインスタンスの未読をAPIで取得する
fn poll_chatwork(app: &AppHandle) {
    let state = app.state::<AppState>();
    let targets: Vec<(String, String)> = {
        let config = state.config.lock().unwrap();
        config
            .instances
            .iter()
            .filter(|i| i.recipe == "chatwork")
            .filter_map(|i| i.chatwork_token.clone().map(|t| (i.id.clone(), t)))
            .collect()
    };
    for (id, token) in targets {
        // WebViewが生きている間はタイトル監視に任せる
        if app.get_webview(&id).is_some() {
            continue;
        }
        let out = std::process::Command::new("curl.exe")
            .args(["-sS", "--fail", "-H"])
            .arg(format!("x-chatworktoken: {token}"))
            .arg("https://api.chatwork.com/v2/my/status")
            .output();
        let Ok(out) = out else { continue };
        if !out.status.success() {
            eprintln!("[chatwork] {id}: API失敗");
            continue;
        }
        if let Ok(json) = serde_json::from_slice::<serde_json::Value>(&out.stdout) {
            let n = json["unread_room_num"].as_u64().unwrap_or(0) as u32;
            set_unread(app, &id, n);
        }
    }
}

// 起動時、「スリープさせない」設定のサービスをスリープ状態のままにせず順次起動する。
// 一斉に立ち上げるとWebView2プロセスが同時生成されて負荷が高いため、間隔を空けて1つずつ起動する。
fn spawn_keep_awake_startup(app: AppHandle) {
    std::thread::spawn(move || {
        let ids: Vec<String> = {
            let state = app.state::<AppState>();
            let config = state.config.lock().unwrap();
            config
                .instances
                .iter()
                .filter(|i| i.keep_awake)
                .map(|i| i.id.clone())
                .collect()
        };
        for (idx, id) in ids.into_iter().enumerate() {
            if idx > 0 {
                std::thread::sleep(std::time::Duration::from_secs(8));
            }
            if let Err(e) = tauri::async_runtime::block_on(activate(app.clone(), id.clone())) {
                eprintln!("[keep-awake-startup] {id}: {e}");
            }
        }
    });
}

// 起動時にCWS版拡張機能の更新を確認し、更新があればデスクトップ通知で知らせる
fn spawn_extension_update_check(app: AppHandle) {
    std::thread::spawn(move || {
        std::thread::sleep(std::time::Duration::from_secs(5));
        match tauri::async_runtime::block_on(update_extensions(app.clone())) {
            Ok(updated) if !updated.is_empty() => {
                eprintln!("[ext-update] updated: {updated:?}");
                let notify_enabled = {
                    let state = app.state::<AppState>();
                    let config = state.config.lock().unwrap();
                    config.settings.notifications
                };
                if notify_enabled {
                    use tauri_plugin_notification::NotificationExt;
                    let _ = app
                        .notification()
                        .builder()
                        .title("拡張機能を更新しました")
                        .body(updated.join("\n"))
                        .show();
                    play_notification_sound(&app);
                }
            }
            Ok(_) => {}
            Err(e) => eprintln!("[ext-update] failed: {e}"),
        }
    });
}

// 10秒ごと: メモリ集計 / 60秒ごと: 自動スリープ判定 + Chatworkポーリング
fn spawn_background(app: AppHandle) {
    std::thread::spawn(move || {
        let mut sys = sysinfo::System::new();
        let mut tick: u64 = 0;
        loop {
            std::thread::sleep(std::time::Duration::from_secs(10));
            refresh_memory(&app, &mut sys);
            tick += 1;
            if tick % 6 != 0 {
                continue;
            }
            poll_chatwork(&app);
            let state = app.state::<AppState>();
            let limit_min = state.config.lock().unwrap().settings.sleep_after_minutes;
            if limit_min == 0 {
                continue;
            }
            // keep_awake指定のインスタンスはスリープ対象外
            let keep: Vec<String> = state
                .config
                .lock()
                .unwrap()
                .instances
                .iter()
                .filter(|i| i.keep_awake)
                .map(|i| i.id.clone())
                .collect();
            let expired: Vec<String> = {
                let rt = state.rt.lock().unwrap();
                rt.hidden_since
                    .iter()
                    .filter(|(id, t)| {
                        !keep.contains(id) && t.elapsed().as_secs() >= limit_min * 60
                    })
                    .map(|(id, _)| id.clone())
                    .collect()
            };
            for id in expired {
                if let Some(wv) = app.get_webview(&id) {
                    eprintln!("[auto-sleep] {}", id);
                    let _ = wv.close();
                }
                state.rt.lock().unwrap().hidden_since.remove(&id);
            }
        }
    });
}

fn main() {
    tauri::Builder::default()
        // 二重起動時は既存のウインドウを前面に表示して終了する
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            if let Some(window) = app.get_window("main") {
                let _ = window.show();
                let _ = window.unminimize();
                let _ = window.set_focus();
            }
        }))
        .plugin(tauri_plugin_autostart::init(
            tauri_plugin_autostart::MacosLauncher::LaunchAgent,
            None,
        ))
        .plugin(tauri_plugin_notification::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_window_state::Builder::new().build())
        .invoke_handler(tauri::generate_handler![
            activate,
            select_instance,
            open_settings,
            sleep_service,
            reload_service,
            reset_extension_state,
            get_translations,
            show_context_menu,
            close_context_menu,
            restart_app,
            ui_state,
            get_config,
            export_settings,
            import_settings,
            select_notification_sound,
            reset_notification_sound,
            test_notification_sound,
            add_instance,
            remove_instance,
            update_instance,
            move_instance,
            update_settings,
            refresh_memory_now,
            host_memory_mb,
            get_extensions,
            open_extensions_dir,
            install_extension_from_cws,
            update_extensions,
            remove_extension
        ])
        .setup(|app| {
            let handle = app.handle().clone();
            migrate_old_appdata(&handle);
            app.manage(AppState {
                config: Mutex::new(load_config(&handle)),
                rt: Mutex::new(RtState::default()),
            });
            let start_minimized = handle
                .state::<AppState>()
                .config
                .lock()
                .unwrap()
                .settings
                .start_minimized;

            let window = WindowBuilder::new(app, "main")
                .title("YMB Thatuation")
                .inner_size(1200.0, 800.0)
                .visible(!start_minimized)
                .background_color(Color(0x0d, 0x0e, 0x12, 255))
                .build()?;
            window.add_child(
                WebviewBuilder::new("sidebar", WebviewUrl::App("index.html".into())),
                LogicalPosition::new(0.0, 0.0),
                LogicalSize::new(SIDEBAR_WIDTH, 800.0),
            )?;
            // 全サービスがスリープ中/初期起動時に表示する待機画面
            {
                let (pos, size) = service_area(&window)?;
                window.add_child(
                    WebviewBuilder::new("welcome", WebviewUrl::App("welcome.html".into())),
                    pos,
                    size,
                )?;
            }

            let win = window.clone();
            window.on_window_event(move |event| match event {
                tauri::WindowEvent::Resized(_) => relayout(&win),
                tauri::WindowEvent::CloseRequested { api, .. } => {
                    // ×で閉じてもトレイ常駐(設定でOFFにすると終了)
                    let to_tray = win
                        .app_handle()
                        .state::<AppState>()
                        .config
                        .lock()
                        .unwrap()
                        .settings
                        .close_to_tray;
                    if to_tray {
                        api.prevent_close();
                        let _ = win.hide();
                    }
                }
                _ => {}
            });

            let show = MenuItem::with_id(app, "show", "表示", true, None::<&str>)?;
            let quit = MenuItem::with_id(app, "quit", "終了", true, None::<&str>)?;
            let menu = Menu::with_items(app, &[&show, &quit])?;
            TrayIconBuilder::new()
                .icon(app.default_window_icon().unwrap().clone())
                .tooltip("YMB Thatuation")
                .menu(&menu)
                .show_menu_on_left_click(true)
                .on_menu_event(|app, event| match event.id.as_ref() {
                    "show" => {
                        if let Some(w) = app.get_window("main") {
                            let _ = w.show();
                            let _ = w.set_focus();
                        }
                    }
                    "quit" => app.exit(0),
                    _ => {}
                })
                .build(app)?;

            spawn_background(handle.clone());
            spawn_keep_awake_startup(handle.clone());
            spawn_extension_update_check(handle);
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
