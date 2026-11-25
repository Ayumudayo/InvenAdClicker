use anyhow::{Context, Result};
use serde::Deserialize;
use std::collections::HashSet;
use std::env;
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Deserialize)]
#[serde(untagged)]
pub enum PageTarget {
    Standard(String),
    InspectOnly { inspect_only: String },
}

impl PageTarget {
    pub fn url(&self) -> &str {
        match self {
            Self::Standard(url) => url,
            Self::InspectOnly { inspect_only } => inspect_only,
        }
    }
}

#[derive(Deserialize)]
pub struct PageList {
    pub pages: Vec<PageTarget>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(default)]
pub struct Config {
    pub id: String,
    pub pages: Vec<PageTarget>,
    pub passes: u32,
    pub concurrency: usize,
    pub click_concurrency: usize,
    pub click_delay_ms: u64,
    pub click_jitter_ms: u64,
    pub page_delay_ms: u64,
    pub page_jitter_ms: u64,
    /// 클릭 완료 후 최종 잔액·경험치 조회 전 대기 시간(ms).
    pub settle_delay_ms: u64,
    pub log_dir: PathBuf,
    pub log_retention_days: u32,
    /// 세션당 클릭 요청 상한. 없으면 제한하지 않는다.
    pub click_limit: Option<usize>,
}

impl Default for Config {
    fn default() -> Self {
        Config {
            id: String::new(),
            pages: serde_json::from_str::<PageList>(include_str!("../config.json.example"))
                .expect("the bundled config must declare its pages")
                .pages,
            passes: 2,
            concurrency: 3,
            click_concurrency: 3,
            click_delay_ms: 300,
            click_jitter_ms: 600,
            page_delay_ms: 300,
            page_jitter_ms: 500,
            settle_delay_ms: 4000,
            log_dir: "logs".into(),
            log_retention_days: 30,
            click_limit: None,
        }
    }
}

fn dedup_pages(pages: Vec<PageTarget>) -> Vec<PageTarget> {
    let mut seen = HashSet::new();
    pages
        .into_iter()
        .filter(|p| seen.insert(p.url().to_owned()))
        .collect()
}

pub(crate) fn parse_env_pages(raw: &str, existing: &[PageTarget]) -> Result<Vec<PageTarget>> {
    let mut pages = Vec::new();
    for url in raw.split(',').map(str::trim).filter(|url| !url.is_empty()) {
        let parsed = reqwest::Url::parse(url)
            .with_context(|| format!("잘못된 페이지 URL입니다: {url}"))?;
        anyhow::ensure!(
            matches!(parsed.scheme(), "http" | "https"),
            "잘못된 페이지 URL 스킴입니다: {url}"
        );
        let target = existing
            .iter()
            .find(|p| p.url() == url)
            .cloned()
            .unwrap_or_else(|| PageTarget::Standard(url.to_owned()));
        pages.push(target);
    }
    Ok(pages)
}

impl Config {
    pub fn page_urls(&self) -> Vec<String> {
        self.pages.iter().map(|p| p.url().to_owned()).collect()
    }

    pub fn load(app_dir: &Path) -> Result<Self> {
        let path = app_dir.join("config.json");
        let json = match std::fs::read_to_string(&path) {
            Ok(json) => json,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
                use std::io::Write;
                let mut file = tempfile::NamedTempFile::new_in(app_dir)
                    .with_context(|| format!("기본 설정 파일 생성 실패 ({})", path.display()))?;
                file.write_all(include_bytes!("../config.json.example"))?;
                match file.persist_noclobber(&path) {
                    Ok(_) => (),
                    Err(e) if e.error.kind() == std::io::ErrorKind::AlreadyExists => (),
                    Err(e) => {
                        return Err(e)
                            .with_context(|| format!("설정 파일 저장 실패 ({})", path.display()))
                    }
                }
                std::fs::read_to_string(&path)?
            }
            Err(e) => {
                return Err(e).with_context(|| format!("설정 파일 읽기 실패 ({})", path.display()))
            }
        };
        let mut cfg: Config = serde_json::from_str(json.trim_start_matches('\u{feff}'))
            .with_context(|| format!("설정 파일을 해석할 수 없습니다 ({})", path.display()))?;
        cfg.log_dir = app_dir.join(&cfg.log_dir);
        if let Ok(v) = env::var("INVEN_ID") {
            cfg.id = v;
        }
        if let Ok(v) = env::var("INVEN_PAGES") {
            cfg.pages = parse_env_pages(&v, &cfg.pages)?;
        }
        if let Ok(v) = env::var("INVEN_PASSES") {
            if let Ok(n) = v.parse::<u32>() {
                cfg.passes = n;
            }
        }
        if let Ok(v) = env::var("INVEN_CONCURRENCY") {
            if let Ok(n) = v.parse::<usize>() {
                cfg.concurrency = n.max(1usize);
            }
        }
        if let Ok(v) = env::var("INVEN_CLICK_CONCURRENCY") {
            if let Ok(n) = v.parse::<usize>() {
                cfg.click_concurrency = n.max(1usize);
            }
        }
        if let Ok(v) = env::var("INVEN_LOG_RETENTION_DAYS") {
            if let Ok(n) = v.parse::<u32>() {
                cfg.log_retention_days = n;
            }
        }
        if let Ok(v) = env::var("INVEN_CLICK_LIMIT") {
            if let Ok(n) = v.parse::<usize>() {
                cfg.click_limit = Some(n);
            }
        }
        if let Ok(v) = env::var("INVEN_SETTLE_DELAY_MS") {
            if let Ok(n) = v.parse::<u64>() {
                cfg.settle_delay_ms = n;
            }
        }
        cfg.pages = dedup_pages(cfg.pages);
        cfg.passes = cfg.passes.max(1);
        Ok(cfg)
    }
}
