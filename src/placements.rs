use anyhow::{Context, Result};
use reqwest::{Client, Url};
use scraper::{ElementRef, Html, Selector};
use serde::Deserialize;
use std::{
    collections::{HashMap, HashSet},
    path::Path,
    sync::{Arc, LazyLock, Mutex},
    time::Duration,
};
use tokio::sync::OnceCell;

use crate::adscript::{self, Tok};

static ELEMENTS: LazyLock<Selector> =
    LazyLock::new(|| Selector::parse("base[href], iframe[src], script, [id]").unwrap());

/// 페이지가 불러오는 인벤 광고 출력 스크립트. 이 파일들의 함수가 광고 iframe을 만든다.
const AD_SCRIPTS: &[&str] = &["/zicf.js", "/zicfm.js"];

#[derive(Default)]
pub(crate) struct Placements {
    /// 요청할 광고 프레임. HTML에 적힌 것과 스크립트가 만드는 것을 모두 담는다.
    pub frames: Vec<Url>,
    /// `frames` 가운데 페이지 스크립트를 해석해 얻은 것의 수.
    pub scripted: usize,
}

impl Placements {
    pub fn description(&self) -> String {
        format!("광고 프레임 {}개", self.frames.len())
    }
}

fn placement_url(base: &Url, reference: &str) -> Option<Url> {
    if reference.trim().is_empty() {
        return None;
    }
    let mut url = base.join(reference.trim()).ok()?;
    if !matches!(url.scheme(), "https" | "http")
        || url.host_str() != Some("zicf.inven.co.kr")
        || !url.username().is_empty()
        || url.password().is_some()
        || url.port().is_some()
        || !url.path().starts_with("/RealMedia/ads/adstream_sx.ads/")
    {
        return None;
    }
    url.set_fragment(None);
    Some(url)
}

fn executable(element: ElementRef<'_>) -> bool {
    let kind = element.value().attr("type").unwrap_or("").trim();
    kind.is_empty()
        || kind.eq_ignore_ascii_case("text/javascript")
        || kind.eq_ignore_ascii_case("application/javascript")
}

/// HTML에서 읽어 낸 정보. `scraper::Html`은 스레드 간에 옮길 수 없어 바로 이 형태로 바꾼다.
#[derive(Default)]
pub(crate) struct PageScan {
    base: Option<Url>,
    frames: Vec<Url>,
    scripts: Vec<Url>,
    inline: Vec<String>,
    ids: HashSet<String>,
}

pub(crate) fn scan(document_url: &Url, html: &str) -> PageScan {
    let document = Html::parse_document(html);
    let elements: Vec<_> = document
        .select(&ELEMENTS)
        .filter(|element| {
            !element
                .ancestors()
                .filter_map(ElementRef::wrap)
                .any(|p| matches!(p.value().name(), "template" | "noscript"))
        })
        .collect();
    let base = elements
        .iter()
        .find(|e| e.value().name() == "base" && e.attr("href").is_some())
        .and_then(|e| document_url.join(e.attr("href")?).ok())
        .unwrap_or_else(|| document_url.clone());
    let mut result = PageScan::default();
    for element in elements {
        if let Some(id) = element.attr("id") {
            result.ids.insert(id.to_owned());
        }
        match element.value().name() {
            "iframe" if element.attr("srcdoc").is_none() => {
                if let Some(url) = element
                    .attr("src")
                    .and_then(|src| placement_url(&base, src))
                {
                    result.frames.push(url);
                }
            }
            "script" if executable(element) => match element.attr("src") {
                Some(src) => {
                    if let Ok(url) = base.join(src.trim()) {
                        let ours = url
                            .host_str()
                            .is_some_and(|h| h == "inven.co.kr" || h.ends_with(".inven.co.kr"));
                        if ours && AD_SCRIPTS.iter().any(|name| url.path().ends_with(name)) {
                            result.scripts.push(url);
                        }
                    }
                }
                None => result.inline.push(element.text().collect()),
            },
            _ => (),
        }
    }
    result.base = Some(base);
    result
}

/// 광고 스크립트를 실행해 페이지의 광고 프레임을 계산한다.
pub(crate) fn resolve(document_url: &Url, page: PageScan, scripts: &[Arc<Vec<Tok>>]) -> Placements {
    let base = page.base.clone().unwrap_or_else(|| document_url.clone());
    let mut env = adscript::Env::default();
    for toks in scripts {
        env.load(toks);
    }
    let mut seen: HashSet<Url> = page.frames.iter().cloned().collect();
    let mut result = Placements {
        frames: page.frames,
        scripted: 0,
    };
    if scripts.is_empty() {
        return result;
    }
    for raw in adscript::page_slots(&env, &page.inline, page.ids, document_url) {
        if let Some(url) = placement_url(&base, &raw) {
            if seen.insert(url.clone()) {
                result.frames.push(url);
                result.scripted += 1;
            }
        }
    }
    result
}

/// 테스트용: 스크립트 원문을 직접 받아 계산한다.
#[cfg(test)]
pub(crate) fn parse(document_url: &Url, html: &str, scripts: &[&str]) -> Placements {
    let toks: Vec<_> = scripts.iter().map(|s| adscript::lex(s)).collect();
    resolve(document_url, scan(document_url, html), &toks)
}

type ScriptEntry = Arc<OnceCell<Arc<Vec<Tok>>>>;

/// 광고 출력 스크립트를 주소별로 한 번만 받아 둔다.
pub(crate) struct ScriptCache {
    client: Client,
    loaded: Mutex<HashMap<Url, ScriptEntry>>,
}

impl ScriptCache {
    pub fn new(client: Client) -> Self {
        Self {
            client,
            loaded: Default::default(),
        }
    }

    pub(crate) async fn get(&self, url: &Url) -> Result<Arc<Vec<Tok>>> {
        let cell = {
            let mut loaded = self.loaded.lock().unwrap_or_else(|e| e.into_inner());
            Arc::clone(
                loaded
                    .entry(url.clone())
                    .or_insert_with(|| Arc::new(OnceCell::new())),
            )
        };
        let toks = cell
            .get_or_try_init(|| async {
                let text = self
                    .client
                    .get(url.clone())
                    .header(reqwest::header::USER_AGENT, crate::client::UA_PC)
                    .send()
                    .await?
                    .error_for_status()?
                    .text()
                    .await?;
                Ok::<_, anyhow::Error>(adscript::lex(&text))
            })
            .await?;
        Ok(Arc::clone(toks))
    }
}

pub(crate) async fn read_page(client: &Client, scripts: &ScriptCache, page: &str) -> Result<Placements> {
    let response = client
        .get(page)
        .header(reqwest::header::USER_AGENT, crate::client::ua_for(page))
        .header(reqwest::header::ACCEPT_LANGUAGE, "ko-KR,ko;q=0.9")
        .send()
        .await?
        .error_for_status()?;
    let document_url = response.url().clone();
    let found = scan(&document_url, &response.text().await?);
    let mut loaded = Vec::new();
    for url in &found.scripts {
        loaded.push(
            scripts
                .get(url)
                .await
                .with_context(|| format!("광고 스크립트를 받지 못했습니다 ({url})"))?,
        );
    }
    Ok(resolve(&document_url, found, &loaded))
}

#[derive(Deserialize)]
#[serde(untagged)]
enum TargetFile {
    Original {
        #[serde(rename = "AppSettings")]
        settings: TargetSettings,
    },
    Native(crate::config::PageList),
}

#[derive(Deserialize)]
struct TargetSettings {
    #[serde(rename = "TargetUrls")]
    urls: Vec<String>,
}

pub(crate) fn public_client() -> Result<Client> {
    Ok(Client::builder()
        .http1_title_case_headers()
        .connect_timeout(Duration::from_secs(5))
        .timeout(Duration::from_secs(15))
        .build()?)
}

pub(crate) async fn inspect_targets(path: &Path) -> Result<std::process::ExitCode> {
    let text = std::fs::read_to_string(path)
        .with_context(|| format!("대상 파일 읽기 실패 ({})", path.display()))?;
    let input: TargetFile = serde_json::from_str(text.trim_start_matches('\u{feff}'))
        .context("설정의 pages 또는 AppSettings.TargetUrls를 읽지 못했습니다")?;
    let urls = match input {
        TargetFile::Original { settings } => settings.urls,
        TargetFile::Native(list) => list.pages.iter().map(|p| p.url().to_owned()).collect(),
    };
    inspect_pages(&urls).await
}

// 공개 문서와 광고 출력 스크립트만 읽는다. 계정, 광고 프레임과 클릭 워커는 사용하지 않는다.
pub(crate) async fn inspect_pages(urls: &[String]) -> Result<std::process::ExitCode> {
    let client = public_client()?;
    let scripts = ScriptCache::new(client.clone());
    let mut seen = HashSet::new();
    let mut failures = 0;
    let mut frames = 0;
    let mut scripted = 0;
    for page in urls {
        let mut url = Url::parse(page).context("대상 URL 해석 실패")?;
        url.set_fragment(None);
        if !seen.insert(url.clone()) {
            continue;
        }
        match read_page(&client, &scripts, url.as_str()).await {
            Ok(observed) => {
                println!("{}  {}", url, observed.description());
                for frame in &observed.frames {
                    println!("    {frame}");
                }
                frames += observed.frames.len();
                scripted += observed.scripted;
            }
            Err(e) => {
                eprintln!("{url}  페이지 읽기 실패: {e:#}");
                failures += 1;
            }
        }
    }
    println!("목록 {}개 · 고유 문서 {}개 · 광고 프레임 {frames}개 (스크립트 {scripted}개) · 오류 {failures}개", urls.len(), seen.len());
    Ok(if failures == 0 {
        std::process::ExitCode::SUCCESS
    } else {
        std::process::ExitCode::FAILURE
    })
}
