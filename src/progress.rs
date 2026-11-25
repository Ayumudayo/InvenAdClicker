use std::collections::HashMap;
use std::sync::Mutex;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Outcome {
    Redirected,
    Unconfirmed,
    Failed,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PageState {
    Waiting,
    Collecting,
    Clicking,
    Failed,
    PartialFailure,
    NoAds,
    Inspected,
    Done,
}

pub enum Event {
    StartCollection {
        inspect_only: bool,
    },
    EndCollection {
        errors: usize,
    },
    Observed {
        pass: u32,
        frames: usize,
    },
    LinkQueued,
    StartRequest,
    EndRequest(Outcome),
    /// 화면에 보여줄 마지막 실패 사유.
    Problem(String),
}

#[derive(Default, Debug, Clone)]
pub struct ProgressInfo {
    pub passes: u32,
    pub finished_passes: u32,
    pub observed_pass: u32,
    pub inspect_only: bool,
    pub frames: usize,
    pub total_ads: usize,
    pub clicked: usize,
    pub pending: usize,
    /// 수집(페이지·광고 프레임) 실패 횟수.
    pub errors: usize,
    /// 클릭 요청 실패 횟수.
    pub failed: usize,
    pub redirects: usize,
    pub unconfirmed: usize,
    pub collectors: usize,
    pub requests: usize,
    /// 완료 순서(먼저 완료될수록 작은 값). 아직 완료되지 않았으면 None.
    pub completed_seq: Option<u64>,
    /// 마지막 실패 사유(수집 또는 클릭).
    pub problem: Option<String>,
}

impl ProgressInfo {
    /// 진행 중이거나 클릭을 기다리는 페이지.
    pub fn active(&self) -> bool {
        self.collectors + self.requests + self.pending > 0
    }

    /// 수집이나 클릭이 실패한 페이지. 광고가 없는 것은 문제로 보지 않는다.
    pub fn has_problem(&self) -> bool {
        self.failed + self.errors + self.unconfirmed > 0
    }

    /// 정상적으로 모두 열었지만 광고 링크가 하나도 없었던 페이지.
    pub fn no_ads(&self) -> bool {
        !self.inspect_only && self.finished() && self.total_ads == 0 && self.errors == 0
    }

    pub fn finished(&self) -> bool {
        self.finished_passes >= self.passes
            && self.collectors == 0
            && self.requests == 0
            && self.pending == 0
    }

    pub fn state(&self) -> PageState {
        if self.collectors > 0 {
            PageState::Collecting
        } else if self.requests > 0 || self.pending > 0 {
            PageState::Clicking
        } else if self.has_problem() {
            let total_fail = (self.clicked > 0 && self.redirects == 0 && self.failed > 0)
                || (self.clicked == 0
                    && self.errors > 0
                    && self.errors >= self.finished_passes as usize);
            if total_fail {
                PageState::Failed
            } else {
                PageState::PartialFailure
            }
        } else if self.no_ads() {
            PageState::NoAds
        } else if self.finished() {
            if self.inspect_only {
                PageState::Inspected
            } else {
                PageState::Done
            }
        } else {
            PageState::Waiting
        }
    }
}

struct ProgressInner {
    map: HashMap<String, ProgressInfo>,
    completed_seq: u64,
}

pub struct ProgressState {
    order: Vec<String>,
    inner: Mutex<ProgressInner>,
}

impl ProgressState {
    pub fn new(urls: &[String], passes: u32) -> Self {
        let mut map = HashMap::<String, ProgressInfo>::new();
        let mut order = Vec::new();
        for url in urls {
            let info = map.entry(url.clone()).or_insert_with(|| {
                order.push(url.clone());
                ProgressInfo::default()
            });
            info.passes = info.passes.saturating_add(passes);
        }
        Self {
            order,
            inner: Mutex::new(ProgressInner {
                map,
                completed_seq: 0,
            }),
        }
    }

    pub fn update(&self, url: &str, event: Event) -> u32 {
        let mut inner = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        let ProgressInner { map, completed_seq } = &mut *inner;
        let Some(row) = map.get_mut(url) else {
            return 0;
        };
        match event {
            Event::StartCollection { inspect_only } => {
                row.inspect_only = inspect_only;
                row.collectors += 1;
            }
            Event::EndCollection { errors } => {
                row.collectors -= 1;
                row.finished_passes += 1;
                row.errors += errors;
            }
            Event::Observed { pass, frames } => {
                if pass >= row.observed_pass {
                    row.observed_pass = pass;
                    row.frames = frames;
                }
            }
            Event::LinkQueued => {
                row.total_ads += 1;
                row.pending += 1;
            }
            Event::StartRequest => {
                row.requests += 1;
            }
            Event::EndRequest(outcome) => {
                row.requests -= 1;
                row.pending -= 1;
                row.clicked += 1;
                match outcome {
                    Outcome::Redirected => row.redirects += 1,
                    Outcome::Unconfirmed => row.unconfirmed += 1,
                    Outcome::Failed => row.failed += 1,
                }
            }
            Event::Problem(reason) => row.problem = Some(reason),
        }
        if row.finished() {
            if row.completed_seq.is_none() {
                *completed_seq += 1;
                row.completed_seq = Some(*completed_seq);
            }
        } else {
            row.completed_seq = None;
        }
        row.finished_passes + row.collectors as u32
    }

    pub fn snapshot(&self) -> Vec<(String, ProgressInfo)> {
        let inner = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        self.order
            .iter()
            .map(|url| (url.clone(), inner.map[url].clone()))
            .collect()
    }
}

#[derive(Default)]
pub struct Totals {
    pub ads: usize,
    pub clicked: usize,
    pub pending: usize,
    pub errors: usize,
    pub failed: usize,
    pub redirects: usize,
    pub unconfirmed: usize,
}

impl Totals {
    pub fn from_rows(rows: &[(String, ProgressInfo)]) -> Self {
        let mut total = Self::default();
        for (_, row) in rows {
            total.ads += row.total_ads;
            total.clicked += row.clicked;
            total.pending += row.pending;
            total.errors += row.errors;
            total.failed += row.failed;
            total.redirects += row.redirects;
            total.unconfirmed += row.unconfirmed;
        }
        total
    }
}
