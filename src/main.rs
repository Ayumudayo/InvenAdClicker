use std::io::{IsTerminal, Write};
use std::path::Path;
use std::process::ExitCode;
use std::sync::Arc;
use std::time::{Duration, Instant};

use anyhow::{Context, Result};
use tokio::sync::watch;

mod adscript;
mod auth;
mod client;
mod config;
mod dashboard;
mod log;
mod pipeline;
mod placements;
mod progress;
mod secrets;

use client::{LOGIN_SURL, OAX_WARMUP_URL, SKILL_POINT_URL};
use config::{Config, PageTarget};
use log::SessionLog;
use pipeline::{run_workers, Ctx};
use progress::{ProgressState, Totals};

#[tokio::main]
async fn main() -> ExitCode {
    let args: Vec<_> = std::env::args_os().skip(1).collect();
    let result = match args.as_slice() {
        [] => start(false).await,
        [flag] if flag == "--collect-only" => start(true).await,
        [flag] if flag == "--inspect-placements" => inspect_configured_pages().await,
        [flag, path] if flag == "--inspect-placements" => {
            placements::inspect_targets(Path::new(path)).await
        }
        _ => Err(anyhow::anyhow!(
            "사용법: InvenAdClicker [--collect-only | --inspect-placements [설정 파일]]"
        )),
    };
    match result {
        Ok(code) => {
            if let Err(e) = wait_for_exit() {
                eprintln!("종료 입력을 읽지 못했습니다. {e}");
            }
            code
        }
        Err(e) => {
            eprintln!("{e:#}");
            ExitCode::FAILURE
        }
    }
}

fn wait_for_exit() -> std::io::Result<()> {
    use crossterm::{
        event::{self, Event, KeyCode, KeyEventKind, KeyModifiers},
        terminal,
    };
    let stdin = std::io::stdin();
    let stdout = std::io::stdout();
    if !(stdin.is_terminal() && stdout.is_terminal()) {
        return Ok(());
    }

    let prompt = "Enter 키를 누르면 종료합니다.";
    if terminal::enable_raw_mode().is_err() {
        println!("\n{prompt}");
        stdout.lock().flush()?;
        stdin.read_line(&mut String::new())?;
        return Ok(());
    }
    struct RestoreMode;
    impl Drop for RestoreMode {
        fn drop(&mut self) {
            let _ = terminal::disable_raw_mode();
        }
    }
    let mode = RestoreMode;
    // 작업 중에 쌓인 Enter로 결과 화면이 바로 닫히지 않게 한다.
    while event::poll(Duration::ZERO)? {
        event::read()?;
    }
    write!(stdout.lock(), "\r\n{prompt}")?;
    stdout.lock().flush()?;
    loop {
        if let Event::Key(key) = event::read()? {
            if key.kind == KeyEventKind::Press
                && (key.code == KeyCode::Enter
                    || (key.code == KeyCode::Char('c')
                        && key.modifiers.contains(KeyModifiers::CONTROL)))
            {
                break;
            }
        }
    }
    drop(mode);
    println!();
    Ok(())
}

fn app_directory() -> Result<std::path::PathBuf> {
    let mut path = std::env::current_exe()?;
    anyhow::ensure!(path.pop(), "실행 파일의 폴더를 확인할 수 없습니다");
    Ok(path)
}

async fn inspect_configured_pages() -> Result<ExitCode> {
    let cfg = Config::load(&app_directory()?)?;
    placements::inspect_pages(&cfg.page_urls()).await
}

async fn start(collect_only: bool) -> Result<ExitCode> {
    let app_dir = app_directory()?;
    let dir = app_dir.as_path();
    let tty = std::io::stdout().is_terminal();
    let cfg = match Config::load(dir) {
        Ok(cfg) => cfg,
        Err(e) => {
            if let Ok(log) = SessionLog::new(&dir.join("logs"), None, false) {
                log.record(&format!("실행 중단: {e:#}"));
            }
            return Err(e);
        }
    };
    let log = Arc::new(SessionLog::new(
        &cfg.log_dir,
        Some(cfg.log_retention_days),
        !tty,
    )?);
    let result = run(dir, cfg, Arc::clone(&log), tty, collect_only).await;
    if let Err(e) = &result {
        log.record(&format!("실행 중단: {e:#}"));
    }
    result
}

async fn run(
    dir: &Path,
    cfg: Config,
    log: Arc<SessionLog>,
    tty: bool,
    collect_only: bool,
) -> Result<ExitCode> {
    log.log(&format!(
        "세션 시작: 페이지 {}개, 반복 {}회{}",
        cfg.pages.len(),
        cfg.passes,
        if collect_only {
            " (수집 전용, 클릭 안 함)"
        } else {
            ""
        }
    ));
    let inven = auth::login(dir, &cfg.id, &log).await?;
    let before = inven.points().await?;
    match before.exp {
        Some(exp) => log.log(&format!(
            "BEFORE: ini={} beni={} exp={exp}",
            before.ini, before.beni
        )),
        None => log.log(&format!("BEFORE: ini={} beni={}", before.ini, before.beni)),
    }
    match inven.complete_login(SKILL_POINT_URL, LOGIN_SURL).await {
        Ok(notes) if !notes.is_empty() => {
            log.notice(&format!("로그인 보상: {}", notes.join(" · ")));
        }
        Ok(_) => {
            log.record("로그인 보상 확인 완료 (추가 적립 없음)");
        }
        Err(e) => {
            log.log(&format!("로그인 보상 확인 실패 (계속 진행): {e:#}"));
        }
    }

    let state = Arc::new(ProgressState::new(&cfg.page_urls(), cfg.passes.max(1)));
    let ctx = Arc::new(Ctx {
        inven: inven.clone(),
        state: Arc::clone(&state),
        log: Arc::clone(&log),
        cfg: cfg.clone(),
        scripts: placements::ScriptCache::new(placements::public_client()?),
        collect_only,
        collected: Default::default(),
    });

    let start = Instant::now();
    let (phase, phase_rx) = watch::channel(dashboard::Phase::Working);
    let dashboard = tty.then(|| {
        let state = Arc::clone(&state);
        tokio::task::spawn_blocking(move || dashboard::render(&state, phase_rx, start))
    });
    let work = async {
        if cfg.pages.iter().any(|p| matches!(p, PageTarget::Standard(_))) {
            if let Err(e) = inven.warm_oax(OAX_WARMUP_URL).await {
                log.log(&format!("광고 세션 준비 실패 (계속 진행): {e:#}"));
            }
        }
        run_workers(&ctx).await.context("페이지 작업 중단")?;
        let clicked = Totals::from_rows(&state.snapshot()).clicked;
        if !collect_only && clicked > 0 && cfg.settle_delay_ms > 0 {
            phase.send_replace(dashboard::Phase::Settling);
            log.log(&format!(
                "적립 반영 대기 중 ({:.1}초)...",
                cfg.settle_delay_ms as f64 / 1000.0
            ));
            tokio::time::sleep(Duration::from_millis(cfg.settle_delay_ms)).await;
        }
        phase.send_replace(dashboard::Phase::ReadingBalance);
        log.log("최종 잔액을 조회합니다.");
        inven.points().await.context("최종 잔액 조회 실패")
    };
    let outcome = tokio::select! {
        result = work => Ok(result),
        signal = tokio::signal::ctrl_c() => signal
            .context("종료 신호 확인 실패")
            .and_then(|()| anyhow::bail!("사용자가 작업을 중단했습니다.")),
    };
    drop(phase);
    if let Some(handle) = dashboard {
        if let Err(e) = handle
            .await
            .context("대시보드 종료 실패")
            .and_then(|result| result)
        {
            log.notice(&format!("진행 표 표시 오류: {e:#}"));
        }
    }
    let after = outcome?;
    let rows = state.snapshot();
    let totals = Totals::from_rows(&rows);
    log.log(&format!(
        "클릭 성공 {}건, 실패 {}건, 결과 불명 {}건 (요청 {}건) · 수집 실패 {}건",
        totals.redirects, totals.failed, totals.unconfirmed, totals.clicked, totals.errors
    ));
    if collect_only {
        log.log(&format!(
            "수집 전용: 클릭 대상 링크 {}건 (클릭하지 않음)",
            ctx.collected.load(std::sync::atomic::Ordering::Relaxed)
        ));
    }
    match &after {
        Ok(points) => {
            if let (Some(b_exp), Some(a_exp)) = (before.exp, points.exp) {
                log.log(&format!(
                    "AFTER: ini={} beni={} exp={a_exp} (delta ini={} beni={} exp={})",
                    points.ini,
                    points.beni,
                    i128::from(points.ini) - i128::from(before.ini),
                    i128::from(points.beni) - i128::from(before.beni),
                    i128::from(a_exp) - i128::from(b_exp)
                ));
            } else {
                log.log(&format!(
                    "AFTER: ini={} beni={} (delta ini={} beni={})",
                    points.ini,
                    points.beni,
                    i128::from(points.ini) - i128::from(before.ini),
                    i128::from(points.beni) - i128::from(before.beni)
                ));
            }
        }
        Err(e) => log.log(&format!("최종 잔액 미확인: {e:#}")),
    }
    if tty {
        let width = usize::from(
            crossterm::terminal::size()
                .map_or(80, |(w, _)| w)
                .saturating_sub(1),
        );
        dashboard::final_report(
            &mut std::io::stdout().lock(),
            &rows,
            before,
            &after,
            start.elapsed(),
            width,
            dashboard::use_colors(),
        )?;
        println!("상세 로그  {}", log.path.display());
    }
    Ok(if after.is_err() || totals.errors + totals.failed > 0 {
        ExitCode::FAILURE
    } else {
        ExitCode::SUCCESS
    })
}

#[cfg(test)]
mod tests;
