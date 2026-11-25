use std::sync::{Arc, LazyLock};
use std::time::{Duration, Instant};

use anyhow::{Context, Result};
use regex::Regex;
use tokio::sync::Semaphore;

use crate::auth;
use crate::placements;
use crate::progress::Outcome;

pub(crate) const UA_PC: &str = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
pub(crate) const UA_MOBILE: &str = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 Version/17.0 Mobile/15E148 Safari/604.1";

pub(crate) const MLOGIN_URL: &str = "https://member.inven.co.kr/user/scorpio/mlogin";
pub(crate) const DISPATCH_URL: &str = "https://member.inven.co.kr/m/login/dispatch";
pub(crate) const SKILL_POINT_URL: &str = "https://member.inven.co.kr/user/scorpio/chk/skill/point";
pub(crate) const LOGIN_SURL: &str = "https://m.inven.co.kr/";
pub(crate) const INVENTORY_URL: &str = "https://www.inven.co.kr/member/inventory/";
pub(crate) const IMART_URL: &str = "https://m.inven.co.kr/imart/";
pub(crate) const OAX_WARMUP_URL: &str =
    "https://zicf.inven.co.kr/RealMedia/ads/adstream_sx.ads/inven/webzine@x83?a1_ppo1=0";

const BODY_LIMIT: usize = 8192;
const MAX_INFLIGHT_FRAMES: usize = 12;

static INV_POINTS_RE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"class="(inni-value|veni-value|exp-value)"[^>]*>\s*([\d,]+)\s*<"#).unwrap()
});
static IMART_POINTS_RE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"<span>(이니|베니)</span>\s*<span class="point-red">([\d,]+)</span>"#).unwrap()
});
static HTML_TAG_RE: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"<[^>]+>").unwrap());
static STOKEN_RE: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r#"value="([^"]+)"\s+id="stoken""#).unwrap());
static CLICK_LINK_RE: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r#"(?:https?:)?//[^"'\s>]*click_lx[^"'\s>]*"#).unwrap());
static HTML_TITLE_RE: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?is)<title[^>]*>(.*?)</title>").unwrap());

pub(crate) fn ua_for(page: &str) -> &'static str {
    if page.contains("://m.") {
        UA_MOBILE
    } else {
        UA_PC
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct Points {
    pub ini: u64,
    pub beni: u64,
    pub exp: Option<u64>,
}

impl Points {
    pub(crate) fn parse(html: &str) -> Result<Self> {
        let mut ini = None;
        let mut beni = None;
        let mut exp = None;
        for cap in INV_POINTS_RE.captures_iter(html) {
            let value = cap[2].replace(',', "").trim().parse::<u64>().ok();
            match &cap[1] {
                "inni-value" if ini.is_none() => ini = value,
                "veni-value" if beni.is_none() => beni = value,
                "exp-value" if exp.is_none() => exp = value,
                _ => {}
            }
        }
        if let (Some(ini), Some(beni)) = (ini, beni) {
            return Ok(Points { ini, beni, exp });
        }
        let mut ini = None;
        let mut beni = None;
        for cap in IMART_POINTS_RE.captures_iter(html) {
            let label = &cap[1];
            let value: u64 = cap[2].replace(',', "").parse().unwrap_or(0);
            match label {
                "이니" => ini = Some(value),
                "베니" => beni = Some(value),
                _ => {}
            }
        }
        Ok(Points {
            ini: ini.context("이니 포인트를 찾지 못함 (로그인 만료?)")?,
            beni: beni.context("베니 포인트를 찾지 못함 (로그인 만료?)")?,
            exp: None,
        })
    }
}

pub(crate) struct LoginReward {
    pub notes: Vec<String>,
    pub redirect_url: Option<String>,
}

fn reward_text(value: Option<&serde_json::Value>) -> Option<String> {
    match value {
        Some(serde_json::Value::String(s)) => {
            let stripped = HTML_TAG_RE.replace_all(s, " ");
            let flat = stripped.split_whitespace().collect::<Vec<_>>().join(" ");
            let trimmed = flat.trim();
            (!trimmed.is_empty() && !matches!(trimmed, "0" | "false")).then(|| trimmed.to_owned())
        }
        Some(serde_json::Value::Number(n)) => {
            (n.as_i64() != Some(0)).then(|| n.to_string())
        }
        _ => None,
    }
}

pub(crate) fn parse_login_reward(body: &str) -> Result<LoginReward> {
    #[derive(serde::Deserialize, Default)]
    struct SkillPointReply {
        result: Option<String>,
        error: Option<String>,
        url: Option<String>,
        login: Option<serde_json::Value>,
        skill: Option<serde_json::Value>,
        sevent: Option<serde_json::Value>,
        #[serde(rename = "doubleEventMsg")]
        double_event_msg: Option<serde_json::Value>,
    }

    let reply: SkillPointReply = serde_json::from_str(body.trim_start_matches('\u{feff}'))
        .context("로그인 보상 응답 형식을 확인할 수 없습니다")?;
    if let Some(err) = reply.error.as_deref().map(str::trim).filter(|s| !s.is_empty()) {
        anyhow::bail!("로그인 보상 응답 오류: {err}");
    }
    let mut notes = Vec::new();
    if reply.result.as_deref() == Some("success") {
        if let Some(v) = reward_text(reply.login.as_ref()) {
            notes.push(format!("불로소득 {v}"));
        }
        if let Some(v) = reward_text(reply.skill.as_ref()) {
            notes.push(format!("인증보너스 {v}"));
        }
        if let Some(v) = reward_text(reply.sevent.as_ref()) {
            notes.push(format!("이벤트 {v}"));
        }
        if let Some(v) = reward_text(reply.double_event_msg.as_ref()) {
            notes.push(v);
        }
    }
    Ok(LoginReward {
        notes,
        redirect_url: reply.url.filter(|u| !u.trim().is_empty()),
    })
}

#[derive(Default)]
pub(crate) struct CollectedPage {
    pub placements: placements::Placements,
    pub links: Vec<AdLink>,
    pub errors: Vec<anyhow::Error>,
}

pub(crate) struct AdLink {
    pub url: String,
    pub page: String,
}

#[derive(Clone)]
pub(crate) struct Inven {
    pub(crate) http: reqwest::Client,
    pub(crate) click_http: reqwest::Client,
    frame_limit: Arc<Semaphore>,
}

impl Inven {
    pub(crate) fn new() -> Result<Self> {
        let jar = Arc::new(reqwest::cookie::Jar::default());
        let client = || {
            reqwest::Client::builder()
                .user_agent(UA_PC)
                .connect_timeout(Duration::from_secs(5))
                .timeout(Duration::from_secs(15))
                .cookie_provider(Arc::clone(&jar))
                // 광고 서버는 소문자 헤더 이름(hyper 기본값)으로 온 클릭을 약 3초 뒤
                // HTTP 200 {"error":"1","cause":-1}로 거절한다. 브라우저처럼 Title-Case로 보낸다.
                .http1_title_case_headers()
        };
        Ok(Self::with_clients(
            client().build()?,
            client().redirect(reqwest::redirect::Policy::none()).build()?,
        ))
    }

    pub(crate) fn with_clients(http: reqwest::Client, click_http: reqwest::Client) -> Self {
        Self {
            http,
            click_http,
            frame_limit: Arc::new(Semaphore::new(MAX_INFLIGHT_FRAMES)),
        }
    }

    pub(crate) async fn login(
        &self,
        id: &str,
        pw: &str,
        (form_url, submit_url): (&str, &str),
    ) -> Result<bool> {
        let html = self
            .http
            .get(form_url)
            .header(reqwest::header::USER_AGENT, UA_MOBILE)
            .send()
            .await
            .context("로그인 페이지 연결 실패")?
            .error_for_status()?
            .text()
            .await?;
        let st = STOKEN_RE
            .captures(&html)
            .context("stoken을 찾지 못함")?
            .get(1)
            .unwrap()
            .as_str()
            .to_string();
        let hex_pw = hex::encode(pw.as_bytes());
        let resp = self
            .http
            .post(submit_url)
            .header(reqwest::header::USER_AGENT, UA_MOBILE)
            .header(reqwest::header::REFERER, form_url)
            .form(&[
                ("user_id", id),
                ("password", hex_pw.as_str()),
                ("kp", "on"),
                ("st", st.as_str()),
                ("wsip", ""),
                ("surl", "https://m.inven.co.kr/"),
            ])
            .send()
            .await
            .context("로그인 요청 실패")?;
        if resp.status() == reqwest::StatusCode::UNAUTHORIZED {
            return Ok(false);
        }
        let body = resp
            .error_for_status()
            .context("로그인 서버 오류")?
            .text()
            .await?;
        auth::accepted(&body)
    }

    pub(crate) async fn complete_login(
        &self,
        skill_point_url: &str,
        surl: &str,
    ) -> Result<Vec<String>> {
        let body = self
            .http
            .post(skill_point_url)
            .header(reqwest::header::USER_AGENT, UA_MOBILE)
            .header(reqwest::header::REFERER, MLOGIN_URL)
            .header(reqwest::header::ORIGIN, "https://member.inven.co.kr")
            .header("X-Requested-With", "XMLHttpRequest")
            .header(
                reqwest::header::ACCEPT,
                "application/json, text/javascript, */*; q=0.01",
            )
            .header(reqwest::header::ACCEPT_LANGUAGE, "ko-KR,ko;q=0.9")
            .form(&[("surl", surl)])
            .send()
            .await
            .context("로그인 보상 요청 실패")?
            .error_for_status()
            .context("로그인 보상 서버 오류")?
            .text()
            .await?;
        let reward = parse_login_reward(&body)?;
        let skill_host = reqwest::Url::parse(skill_point_url)
            .ok()
            .and_then(|u| u.host_str().map(str::to_owned));
        let allowed_host = |url: &reqwest::Url| -> bool {
            let Some(host) = url.host_str() else {
                return false;
            };
            host.eq_ignore_ascii_case("inven.co.kr")
                || host.to_ascii_lowercase().ends_with(".inven.co.kr")
                || skill_host
                    .as_deref()
                    .is_some_and(|h| host.eq_ignore_ascii_case(h))
        };
        let follow_url = reqwest::Url::parse(surl)
            .ok()
            .and_then(|base| {
                reward.redirect_url.as_deref().and_then(|raw| {
                    reqwest::Url::parse(raw)
                        .ok()
                        .or_else(|| base.join(raw).ok())
                })
            })
            .filter(|u| matches!(u.scheme(), "http" | "https") && allowed_host(u))
            .map_or_else(|| surl.to_owned(), String::from);
        if let Ok(resp) = self
            .http
            .get(&follow_url)
            .header(reqwest::header::USER_AGENT, ua_for(&follow_url))
            .header(reqwest::header::REFERER, MLOGIN_URL)
            .header(reqwest::header::ACCEPT_LANGUAGE, "ko-KR,ko;q=0.9")
            .send()
            .await
        {
            let _ = resp.bytes().await;
        }
        Ok(reward.notes)
    }

    pub(crate) async fn warm_oax(&self, url: &str) -> Result<()> {
        let resp = self
            .http
            .get(url)
            .header(reqwest::header::USER_AGENT, UA_PC)
            .header(reqwest::header::REFERER, "https://www.inven.co.kr/")
            .header(reqwest::header::ACCEPT_LANGUAGE, "ko-KR,ko;q=0.9")
            .send()
            .await?
            .error_for_status()?;
        let _ = resp.bytes().await?;
        Ok(())
    }

    pub(crate) async fn points_from(&self, primary_url: &str, fallback_url: &str) -> Result<Points> {
        let primary = async {
            let html = self
                .http
                .get(primary_url)
                .header(reqwest::header::USER_AGENT, UA_PC)
                .header(reqwest::header::ACCEPT_LANGUAGE, "ko-KR,ko;q=0.9")
                .send()
                .await?
                .error_for_status()?
                .text()
                .await?;
            Points::parse(&html)
        }
        .await;
        if let Ok(points) = primary {
            return Ok(points);
        }
        let html = self
            .http
            .get(fallback_url)
            .header(reqwest::header::USER_AGENT, UA_MOBILE)
            .header(reqwest::header::ACCEPT_LANGUAGE, "ko-KR,ko;q=0.9")
            .send()
            .await?
            .error_for_status()?
            .text()
            .await?;
        Points::parse(&html)
    }

    pub(crate) async fn points(&self) -> Result<Points> {
        self.points_from(INVENTORY_URL, IMART_URL).await
    }

    pub(crate) async fn collect_page(
        &self,
        scripts: &placements::ScriptCache,
        page: &str,
    ) -> Result<CollectedPage> {
        let observed = placements::read_page(&self.http, scripts, page).await?;
        let mut collected = CollectedPage {
            placements: observed,
            ..Default::default()
        };
        // 브라우저처럼 한 페이지의 광고 프레임을 동시에 받되, 전체 동시 요청 수는 세마포어로 제한한다.
        let mut targets: Vec<reqwest::Url> = Vec::new();
        for target in &collected.placements.frames {
            if !targets.contains(target) {
                targets.push(target.clone());
            }
        }
        let mut tasks = tokio::task::JoinSet::new();
        for (index, target) in targets.into_iter().enumerate() {
            let http = self.http.clone();
            let frame_limit = Arc::clone(&self.frame_limit);
            let page = page.to_owned();
            tasks.spawn(async move {
                let body = async {
                    let _permit = frame_limit
                        .acquire_owned()
                        .await
                        .context("프레임 동시성 제한 오류")?;
                    let text = http
                        .get(target)
                        .header(reqwest::header::USER_AGENT, ua_for(&page))
                        .header(reqwest::header::REFERER, &page)
                        .header(reqwest::header::ACCEPT_LANGUAGE, "ko-KR,ko;q=0.9")
                        .send()
                        .await
                        .map_err(reqwest::Error::without_url)?
                        .error_for_status()
                        .map_err(reqwest::Error::without_url)?
                        .text()
                        .await
                        .map_err(reqwest::Error::without_url)?;
                    Ok::<_, anyhow::Error>(text)
                }
                .await;
                (index, body)
            });
        }
        let mut bodies = Vec::new();
        while let Some(joined) = tasks.join_next().await {
            bodies.push(joined.context("광고 프레임 작업이 비정상 종료되었습니다")?);
        }
        bodies.sort_by_key(|(index, _)| *index);
        for (_, response) in bodies {
            let body = match response {
                Ok(body) => body,
                Err(e) => {
                    collected.errors.push(e);
                    continue;
                }
            };
            for lm in CLICK_LINK_RE.find_iter(&body) {
                let mut link = lm.as_str().to_string();
                if link.starts_with("//") {
                    link = format!("https:{link}");
                }
                // 같은 페이지 안에서 똑같은 주소만 한 번으로 친다(원본 앱과 같음).
                if !link.contains("empty.gif") && !collected.links.iter().any(|l| l.url == link) {
                    collected.links.push(AdLink {
                        url: link,
                        page: page.to_string(),
                    });
                }
            }
        }
        Ok(collected)
    }

    pub(crate) async fn click(&self, ad: &AdLink) -> Result<ClickReply> {
        let started = Instant::now();
        // 광고 클릭은 브라우저의 페이지 이동 요청과 같은 헤더로 보낸다.
        let response = self
            .click_http
            .get(&ad.url)
            .header(reqwest::header::USER_AGENT, ua_for(&ad.page))
            .header(reqwest::header::REFERER, &ad.page)
            .header(
                reqwest::header::ACCEPT,
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
            )
            .header(reqwest::header::ACCEPT_LANGUAGE, "ko-KR,ko;q=0.9")
            .header(reqwest::header::UPGRADE_INSECURE_REQUESTS, "1")
            .send()
            .await
            .map_err(reqwest::Error::without_url)?;
        let status = response.status();
        let location = response
            .headers()
            .get(reqwest::header::LOCATION)
            .and_then(|v| v.to_str().ok())
            .map(str::to_owned);
        let content_type = response
            .headers()
            .get(reqwest::header::CONTENT_TYPE)
            .and_then(|v| v.to_str().ok())
            .unwrap_or("")
            .to_owned();
        let (outcome, detail) = if status.is_redirection() {
            let target = location
                .as_deref()
                .and_then(|l| reqwest::Url::parse(l).ok())
                .and_then(|u| u.host_str().map(str::to_owned))
                .unwrap_or_else(|| "이동 주소 없음".into());
            let outcome = if status == reqwest::StatusCode::FOUND {
                Outcome::Redirected
            } else {
                Outcome::Unconfirmed
            };
            (outcome, format!("→ {target}"))
        } else {
            let body = read_limited(response).await?;
            let summary = summarize_body(&body, &content_type);
            let failed = status.is_client_error()
                || status.is_server_error()
                || (status.is_success() && is_error_reply(&body));
            (
                if failed {
                    Outcome::Failed
                } else {
                    Outcome::Unconfirmed
                },
                summary,
            )
        };
        Ok(ClickReply {
            status,
            outcome,
            detail,
            elapsed: started.elapsed(),
        })
    }
}

#[derive(Debug)]
pub(crate) struct ClickReply {
    pub status: reqwest::StatusCode,
    pub outcome: Outcome,
    /// 이동한 호스트 또는 응답 본문의 요약.
    pub detail: String,
    pub elapsed: Duration,
}

async fn read_limited(mut response: reqwest::Response) -> Result<Vec<u8>> {
    let mut bytes = Vec::new();
    while let Some(chunk) = response
        .chunk()
        .await
        .map_err(reqwest::Error::without_url)?
    {
        let room = BODY_LIMIT - bytes.len();
        bytes.extend_from_slice(&chunk[..chunk.len().min(room)]);
        if bytes.len() >= BODY_LIMIT {
            break;
        }
    }
    Ok(bytes)
}

fn reply_json(body: &[u8]) -> Option<serde_json::Value> {
    let body = body.strip_prefix(b"\xef\xbb\xbf").unwrap_or(body);
    serde_json::from_slice(body).ok()
}

/// 실패 사유를 알 수 있도록 응답을 한 줄로 줄인다. 계정 정보가 들어갈 만한 필드는 쓰지 않는다.
fn summarize_body(body: &[u8], content_type: &str) -> String {
    let shorten = |s: &str| -> String {
        let flat = s.split_whitespace().collect::<Vec<_>>().join(" ");
        let mut out: String = flat.chars().take(120).collect();
        if flat.chars().count() > 120 {
            out.push('…');
        }
        out
    };
    if let Some(json) = reply_json(body) {
        let fields: Vec<String> = ["error", "message", "msg", "result", "status", "code", "success"]
            .iter()
            .filter_map(|k| match &json[*k] {
                serde_json::Value::Null => None,
                serde_json::Value::String(s) => Some(format!("{k}={}", shorten(s))),
                other => Some(format!("{k}={}", shorten(&other.to_string()))),
            })
            .collect();
        return if fields.is_empty() {
            format!("JSON {}", shorten(&json.to_string()))
        } else {
            format!("JSON {}", fields.join(", "))
        };
    }
    let text = String::from_utf8_lossy(body);
    let title = HTML_TITLE_RE
        .captures(&text)
        .map(|c| shorten(&c[1]));
    match title {
        Some(t) => format!("HTML \"{t}\" ({}바이트)", body.len()),
        None if body.is_empty() => "빈 응답".into(),
        None => format!("{} {}바이트: {}", content_type, body.len(), shorten(&text)),
    }
}

// 일부 서버는 HTTP 200과 함께 JSON 오류를 돌려준다.
fn is_error_reply(body: &[u8]) -> bool {
    let Some(json) = reply_json(body) else {
        return false;
    };
    let error = match &json["error"] {
        serde_json::Value::Bool(value) => *value,
        serde_json::Value::String(text) => !matches!(text.trim(), "" | "0" | "false"),
        serde_json::Value::Number(code) => code.as_i64() != Some(0),
        serde_json::Value::Object(details) => !details.is_empty(),
        _ => false,
    };
    error
        || json["success"] == false
        || ["status", "result"].iter().any(|k| {
            json[*k].as_str().is_some_and(|v| {
                matches!(v.to_ascii_lowercase().as_str(), "error" | "fail" | "failed")
            })
        })
}
