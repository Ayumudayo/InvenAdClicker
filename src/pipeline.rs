use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, LazyLock};
use std::time::Duration;

use anyhow::{Context, Result};
use rand::Rng;
use regex::Regex;
use tokio::sync::mpsc;

use crate::client::{AdLink, CollectedPage, Inven};
use crate::config::{Config, PageTarget};
use crate::log::SessionLog;
use crate::placements;
use crate::progress::{Event, Outcome, ProgressState};

static COMBO_RE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"/click_lx\.ads/[^/]+/([^/]+)/[^/]+/[^/]+/([^/]+)/INVEN/([^/]+)/").unwrap()
});

pub(crate) async fn jitter(base: u64, spread: u64) {
    let extra = if spread == 0 {
        0
    } else {
        rand::thread_rng().gen_range(0..spread)
    };
    tokio::time::sleep(Duration::from_millis(base.saturating_add(extra))).await;
}

pub(crate) struct Ctx {
    pub inven: Inven,
    pub state: Arc<ProgressState>,
    pub log: Arc<SessionLog>,
    pub cfg: Config,
    pub scripts: placements::ScriptCache,
    /// 클릭하지 않고 수집한 링크만 기록한다.
    pub collect_only: bool,
    pub collected: AtomicUsize,
}

// 원본 GenericPipelineRunner 구조: URL 채널 → 수집 워커들 → 클릭 채널 → 클릭 워커들.
// 수집과 클릭이 채널로 분리되어 URL과 무관하게 병렬 처리된다.
async fn collector_worker(
    url_rx: Arc<tokio::sync::Mutex<mpsc::Receiver<PageTarget>>>,
    public_http: reqwest::Client,
    click_tx: mpsc::Sender<AdLink>,
    ctx: Arc<Ctx>,
) -> Result<()> {
    loop {
        let page = {
            let mut rx = url_rx.lock().await;
            rx.recv().await
        };
        let Some(target) = page else { break };
        let page = target.url();
        let inspect_only = matches!(target, PageTarget::InspectOnly { .. });
        let pass_no = ctx
            .state
            .update(page, Event::StartCollection { inspect_only });
        let result = if inspect_only {
            placements::read_page(&public_http, &ctx.scripts, page)
                .await
                .map(|placements| CollectedPage {
                    placements,
                    ..Default::default()
                })
        } else {
            ctx.inven.collect_page(&ctx.scripts, page).await
        };
        let found = match result {
            Ok(v) => v,
            Err(e) => {
                ctx.state.update(page, Event::EndCollection { errors: 1 });
                ctx.state.update(
                    page,
                    Event::Problem(format!("수집 실패 · {}", first_line(&format!("{e:#}")))),
                );
                ctx.log.log(&format!("  수집 실패 {page}: {e:#}"));
                jitter(ctx.cfg.page_delay_ms, ctx.cfg.page_jitter_ms).await;
                continue;
            }
        };
        for error in &found.errors {
            ctx.log.log(&format!("  광고 프레임 요청 실패 {page}: {error:#}"));
        }
        if let Some(error) = found.errors.last() {
            let reason = if error
                .downcast_ref::<reqwest::Error>()
                .is_some_and(reqwest::Error::is_timeout)
            {
                "시간 초과".to_owned()
            } else {
                first_line(&format!("{error:#}"))
            };
            ctx.state.update(
                page,
                Event::Problem(format!("광고 프레임 {}개 실패 · {reason}", found.errors.len())),
            );
        }
        ctx.state.update(
            page,
            Event::Observed {
                pass: pass_no,
                frames: found.placements.frames.len(),
            },
        );
        let evidence = found.placements.description();
        let errors = found.errors.len();
        let detected = found.links.len();
        let mut queued = 0usize;
        // 원본 앱처럼 같은 광고라도 페이지와 회차마다 다시 클릭한다.
        for ad in found.links {
            let key = combo_of(&ad.url).unwrap_or_else(|| ad.url.clone());
            let ordinal = ctx.collected.fetch_add(1, Ordering::Relaxed) + 1;
            let over_limit = ctx.cfg.click_limit.is_some_and(|limit| ordinal > limit);
            if ctx.collect_only || over_limit {
                ctx.log.log(&format!("  [수집만] {key}"));
                continue;
            }
            let permit = click_tx.reserve().await.context("클릭 채널이 닫혔습니다")?;
            ctx.state.update(page, Event::LinkQueued);
            permit.send(ad);
            queued += 1;
        }
        ctx.state.update(page, Event::EndCollection { errors });
        let outcome = if inspect_only {
            "점검만 함".to_owned()
        } else if ctx.collect_only {
            format!("광고 링크 {detected}건 · 수집 전용이라 클릭 안 함")
        } else if queued < detected {
            format!(
                "광고 링크 {detected}건 (클릭 {queued}건, 클릭 상한으로 건너뜀 {}건)",
                detected - queued
            )
        } else {
            format!("광고 링크 {detected}건")
        };
        ctx.log
            .log(&format!("  수집 {pass_no}회차 {page}: {evidence} · {outcome}"));
        jitter(ctx.cfg.page_delay_ms, ctx.cfg.page_jitter_ms).await;
    }
    Ok(())
}

async fn clicker_worker(
    click_rx: Arc<tokio::sync::Mutex<mpsc::Receiver<AdLink>>>,
    ctx: Arc<Ctx>,
) {
    loop {
        let ad = {
            let mut rx = click_rx.lock().await;
            rx.recv().await
        };
        let Some(ad) = ad else { break };
        let key = combo_of(&ad.url).unwrap_or_else(|| "?".into());
        ctx.state.update(&ad.page, Event::StartRequest);
        let (outcome, message) = match ctx.inven.click(&ad).await {
            Ok(reply) => {
                let label = match reply.outcome {
                    Outcome::Redirected => "클릭 성공",
                    Outcome::Unconfirmed => "결과 불명",
                    Outcome::Failed => "클릭 실패",
                };
                (
                    reply.outcome,
                    format!(
                        "{label} · HTTP {} · {} · {}ms",
                        reply.status.as_u16(),
                        reply.detail,
                        reply.elapsed.as_millis()
                    ),
                )
            }
            Err(e) => (Outcome::Failed, format!("클릭 실패 · 요청 오류 {e:#}")),
        };
        if outcome != Outcome::Redirected {
            let slot = key.split('/').next().unwrap_or(&key).to_owned();
            let reason = message.split(" · ").skip(1).collect::<Vec<_>>().join(" · ");
            ctx.state
                .update(&ad.page, Event::Problem(format!("{slot} · {reason}")));
        }
        ctx.state.update(&ad.page, Event::EndRequest(outcome));
        ctx.log.log(&format!("  [{message}] {key}"));
        jitter(ctx.cfg.click_delay_ms, ctx.cfg.click_jitter_ms).await;
    }
}

pub(crate) async fn run_workers(ctx: &Arc<Ctx>) -> Result<()> {
    let cfg = &ctx.cfg;
    let public_http = placements::public_client()?;
    let (url_tx, url_rx) = mpsc::channel(64);
    let (click_tx, click_rx) = mpsc::channel(256);
    let url_rx = Arc::new(tokio::sync::Mutex::new(url_rx));
    let click_rx = Arc::new(tokio::sync::Mutex::new(click_rx));
    let mut tasks = tokio::task::JoinSet::new();
    let pages = cfg.pages.clone();
    let passes = cfg.passes;
    tasks.spawn(async move {
        for _ in 0..passes {
            for page in &pages {
                url_tx
                    .send(page.clone())
                    .await
                    .context("수집 채널이 닫혔습니다")?;
            }
        }
        Ok(())
    });
    for _ in 0..cfg.concurrency.max(1) {
        let (rx, tx, ctx) = (Arc::clone(&url_rx), click_tx.clone(), Arc::clone(ctx));
        let public_http = public_http.clone();
        tasks.spawn(async move { collector_worker(rx, public_http, tx, ctx).await });
    }
    drop(url_rx);
    drop(click_tx);
    for _ in 0..cfg.click_concurrency.max(1) {
        let (rx, ctx) = (Arc::clone(&click_rx), Arc::clone(ctx));
        tasks.spawn(async move {
            clicker_worker(rx, ctx).await;
            Ok(())
        });
    }
    drop(click_rx);
    while let Some(result) = tasks.join_next().await {
        result.context("작업 스레드가 비정상 종료되었습니다")??;
    }
    Ok(())
}

/// 화면 한 줄에 넣을 수 있도록 오류 문장의 첫 줄만 남긴다.
fn first_line(text: &str) -> String {
    text.lines().next().unwrap_or("").chars().take(80).collect()
}

fn combo_of(link: &str) -> Option<String> {
    COMBO_RE
        .captures(link)
        .map(|c| format!("{}@{}/{}", &c[1], &c[2], &c[3]))
}
