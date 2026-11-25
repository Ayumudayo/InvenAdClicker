//! 진행 화면과 종료 요약.
//!
//! 화면은 위에서부터 제목·진행 막대, 핵심 숫자 한 줄, 그리고 상태별 묶음(진행 중 → 문제 →
//! 완료 → 대기)으로 이루어진다. 모든 섹션에 전체 URL을 표시하며, 완료 목록은 완료 순서대로
//! 세로로 쌓는다. 창 크기가 바뀌면 다음 갱신(0.2초)에 다시 배치한다.

use crate::client::Points;
use crate::progress::{PageState, ProgressInfo, ProgressState, Totals};
use anyhow::Result;
use crossterm::{
    cursor::{Hide, MoveTo, Show},
    execute, queue,
    style::{Attribute, Color, Print, SetAttribute, SetForegroundColor},
    terminal::{self, EnterAlternateScreen, LeaveAlternateScreen},
};
use std::io::{self, IsTerminal, Write};
use std::ops::Range;
use std::time::{Duration, Instant};
use tokio::sync::watch;
use unicode_segmentation::UnicodeSegmentation;
use unicode_width::UnicodeWidthStr;

#[derive(Clone, Copy)]
pub enum Phase {
    Working,
    Settling,
    ReadingBalance,
}

// ---- 출력 줄 ----

#[derive(Debug, PartialEq, Eq, Clone)]
struct Line {
    text: String,
    bold: bool,
    /// (바이트 범위, 색). 겹치지 않고 앞에서부터 정렬되어 있다.
    spans: Vec<(Range<usize>, Color)>,
}

impl Line {
    fn new() -> Self {
        Self {
            text: String::new(),
            bold: false,
            spans: Vec::new(),
        }
    }
    fn plain(text: impl Into<String>) -> Self {
        let mut line = Self::new();
        line.text = text.into();
        line
    }
    fn bold(mut self) -> Self {
        self.bold = true;
        self
    }
    fn push(&mut self, text: &str) -> &mut Self {
        self.text.push_str(text);
        self
    }
    fn paint(&mut self, text: &str, color: Color) -> &mut Self {
        let start = self.text.len();
        self.text.push_str(text);
        if !text.is_empty() {
            self.spans.push((start..self.text.len(), color));
        }
        self
    }
    fn width(&self) -> usize {
        self.text.width()
    }
    /// 화면 너비에 맞게 자른다. 색 범위도 함께 줄인다.
    fn fit(mut self, width: usize) -> Self {
        if self.width() > width {
            self.text = prefix(&self.text, width);
            let end = self.text.len();
            self.spans.retain(|(r, _)| r.start < end);
            for (r, _) in &mut self.spans {
                r.end = r.end.min(end);
            }
        }
        self
    }

    fn write(&self, out: &mut impl Write, colors: bool) -> io::Result<()> {
        let bold = self.bold && colors;
        if bold {
            queue!(out, SetAttribute(Attribute::Bold))?;
        }
        if colors {
            let mut at = 0;
            for (range, color) in &self.spans {
                queue!(
                    out,
                    Print(&self.text[at..range.start]),
                    SetForegroundColor(*color),
                    Print(&self.text[range.clone()]),
                    SetForegroundColor(Color::Reset)
                )?;
                at = range.end;
            }
            queue!(out, Print(&self.text[at..]))?;
        } else {
            queue!(out, Print(&self.text))?;
        }
        if bold {
            queue!(out, SetAttribute(Attribute::Reset))?;
        }
        Ok(())
    }
}

pub(crate) fn use_colors() -> bool {
    io::stdout().is_terminal()
        && std::env::var_os("NO_COLOR").is_none_or(|v| v.is_empty())
        && std::env::var_os("TERM").is_none_or(|v| v != "dumb")
}

// ---- 기호 ----

/// 화면에 쓰는 기호. 일부 기호는 한국어 콘솔 글꼴에서 두 칸으로 그려지므로,
/// Windows Terminal이 아니면 ASCII 기호를 쓴다(`INVEN_ASCII`, `INVEN_UNICODE`로 강제할 수 있다).
#[derive(Clone, Copy, Debug)]
struct Glyphs {
    collecting: &'static str,
    clicking: &'static str,
    waiting: &'static str,
    done: &'static str,
    warn: &'static str,
    fail: &'static str,
    /// 정상적으로 열었지만 광고가 없었던 페이지.
    empty: &'static str,
    pass_done: &'static str,
    pass_todo: &'static str,
    bar_full: &'static str,
    bar_empty: &'static str,
    rule: &'static str,
}

const UNICODE: Glyphs = Glyphs {
    collecting: "●",
    clicking: "◐",
    waiting: "○",
    done: "✓",
    warn: "!",
    fail: "✗",
    empty: "-",
    pass_done: "●",
    pass_todo: "○",
    bar_full: "█",
    bar_empty: "░",
    rule: "─",
};

const ASCII: Glyphs = Glyphs {
    collecting: "*",
    clicking: "~",
    waiting: ".",
    done: "v",
    warn: "!",
    fail: "x",
    empty: "-",
    pass_done: "#",
    pass_todo: ".",
    bar_full: "#",
    bar_empty: ".",
    rule: "-",
};

fn glyphs() -> Glyphs {
    let set = |name: &str| std::env::var_os(name).is_some_and(|v| !v.is_empty() && v != "0");
    if set("INVEN_ASCII") {
        ASCII
    } else if set("INVEN_UNICODE") || !cfg!(windows) || std::env::var_os("WT_SESSION").is_some() {
        UNICODE
    } else {
        ASCII
    }
}

// ---- 문자열 도구 ----

fn clean(text: &str) -> String {
    text.chars()
        .map(|ch| if ch.is_control() { ' ' } else { ch })
        .collect()
}

fn prefix(text: &str, width: usize) -> String {
    let mut used = 0;
    text.graphemes(true)
        .take_while(|part| {
            used += part.width();
            used <= width
        })
        .collect()
}

/// 글자를 자르지 않고 너비에 맞춰 여러 줄로 나눈다. 첫 줄과 나머지 줄의 너비를 따로 줄 수 있다.
fn wrap_indent(text: &str, first: usize, rest: usize) -> Vec<String> {
    let mut out = vec![];
    let mut line = String::new();
    let mut limit = first.max(1);
    for part in text.graphemes(true) {
        if line.width() + part.width() > limit && !line.is_empty() {
            out.push(std::mem::take(&mut line));
            limit = rest.max(1);
        }
        line.push_str(part);
    }
    if !line.is_empty() || out.is_empty() {
        out.push(line);
    }
    out
}

fn wrap(text: &str, width: usize) -> Vec<String> {
    wrap_indent(text, width, width)
}
fn pad(text: &str, width: usize) -> String {
    format!("{text}{}", " ".repeat(width.saturating_sub(text.width())))
}

fn elapsed(time: Duration) -> String {
    let secs = time.as_secs();
    if secs >= 3600 {
        format!("{}:{:02}:{:02}", secs / 3600, secs / 60 % 60, secs % 60)
    } else {
        format!("{:02}:{:02}", secs / 60, secs % 60)
    }
}

fn duration_words(time: Duration) -> String {
    let secs = time.as_secs();
    match secs {
        0..=59 => format!("{secs}초"),
        60..=3599 => format!("{}분 {}초", secs / 60, secs % 60),
        _ => format!("{}시간 {}분", secs / 3600, secs / 60 % 60),
    }
}

// ---- 페이지 행 ----

#[derive(Clone, Copy)]
struct RowStyle {
    icon: &'static str,
    color: Color,
}

fn row_style(r: &ProgressInfo, g: Glyphs) -> RowStyle {
    let (icon, color) = match r.state() {
        PageState::Collecting => (g.collecting, Color::Cyan),
        PageState::Clicking => (g.clicking, Color::Cyan),
        PageState::Failed => (g.fail, Color::Red),
        PageState::PartialFailure => (g.warn, Color::Yellow),
        PageState::NoAds => (g.empty, Color::Yellow),
        PageState::Done | PageState::Inspected => (g.done, Color::Green),
        PageState::Waiting => (g.waiting, Color::DarkGrey),
    };
    RowStyle { icon, color }
}

fn passes_text(r: &ProgressInfo, g: Glyphs) -> String {
    let done = r.finished_passes.min(r.passes) as usize;
    let todo = r.passes as usize - done;
    if r.passes > 4 {
        return format!("{done}/{}", r.passes);
    }
    format!("{}{}", g.pass_done.repeat(done), g.pass_todo.repeat(todo))
}

/// 진행 중·문제 행의 오른쪽 설명.
fn detail(r: &ProgressInfo, g: Glyphs) -> (String, Option<(String, Color)>) {
    let mut text = format!("수집 {}", passes_text(r, g));
    if r.inspect_only {
        if r.frames > 0 {
            text.push_str(&format!("  점검만 · 프레임 {}", r.frames));
        } else {
            text.push_str("  점검만");
        }
    } else if r.total_ads > 0 {
        text.push_str(&format!("  클릭 {}/{}", r.redirects, r.total_ads));
    }
    let note = if r.active() {
        None
    } else if let Some(problem) = &r.problem {
        Some((problem.clone(), if r.failed + r.errors > 0 { Color::Red } else { Color::Yellow }))
    } else if r.total_ads == 0 && !r.inspect_only {
        Some(("광고 없음".to_owned(), Color::Yellow))
    } else {
        None
    };
    (text, note)
}

fn wrap_url_line(style: RowStyle, url: &str, width: usize) -> Vec<Line> {
    let url = clean(url);
    let prefix_w = 2 + style.icon.width();
    let first_w = width.saturating_sub(prefix_w).max(1);
    let rest_w = width.saturating_sub(3).max(1);
    let mut out = vec![];
    for (i, piece) in wrap_indent(&url, first_w, rest_w).into_iter().enumerate() {
        let line = if i == 0 {
            let mut l = Line::new();
            l.push(" ");
            l.paint(style.icon, style.color);
            l.push(" ");
            l.push(&piece);
            l
        } else {
            Line::plain(format!("   {piece}"))
        };
        out.push(line.fit(width));
    }
    out
}

/// 페이지 행의 열 너비. 전체 URL을 자르지 않고, 한 줄에 들어가지 않으면 설명을 다음 줄로 내린다.
struct Columns {
    url: usize,
    /// 주소와 설명을 두 줄로 나눠 쓴다.
    stacked: bool,
}

/// 설명(수집 ●●  클릭 12/15)에 필요한 최소 너비.
const DETAIL_MIN: usize = 22;

impl Columns {
    fn new<'a>(urls: impl IntoIterator<Item = &'a str>, width: usize) -> Self {
        let url = urls
            .into_iter()
            .map(|u| clean(u).width())
            .max()
            .unwrap_or(1);
        // " ● " + 전체 URL + "  " + 설명
        let stacked = 3 + url + 2 + DETAIL_MIN > width;
        Self { url, stacked }
    }

    fn lines(&self, url: &str, r: &ProgressInfo, width: usize, g: Glyphs) -> Vec<Line> {
        let style = row_style(r, g);
        let (text, note) = detail(r, g);
        let note = note.map(|(n, c)| (clean(&n), c));
        if self.stacked {
            let mut out = wrap_url_line(style, url, width);
            let mut body = Line::plain("   ");
            body.push(&text);
            if let Some((note, color)) = note {
                if body.width() + 3 + note.width() <= width {
                    body.push("   ");
                    body.paint(&note, color);
                    out.push(body.fit(width));
                } else {
                    out.push(body.fit(width));
                    for piece in wrap(&note, width.saturating_sub(3).max(1)) {
                        let mut extra = Line::plain("   ");
                        extra.paint(&piece, color);
                        out.push(extra.fit(width));
                    }
                }
            } else {
                out.push(body.fit(width));
            }
            return out;
        }

        let url = clean(url);
        let mut body = Line::new();
        body.push(" ");
        body.paint(style.icon, style.color);
        body.push(" ");
        body.push(&pad(&url, self.url));
        body.push("  ");
        body.push(&text);
        if let Some((note, color)) = note {
            if body.width() + 3 + note.width() <= width {
                body.push("   ");
                body.paint(&note, color);
                vec![body.fit(width)]
            } else {
                let mut out = vec![body.fit(width)];
                for piece in wrap(&note, width.saturating_sub(3).max(1)) {
                    let mut extra = Line::plain("   ");
                    extra.paint(&piece, color);
                    out.push(extra.fit(width));
                }
                out
            }
        } else {
            vec![body.fit(width)]
        }
    }
}

fn rule(title: &str, right: &str, width: usize, g: Glyphs) -> Line {
    let head = format!("{} {title} ", g.rule);
    let tail = if right.is_empty() {
        String::new()
    } else {
        format!(" {right} {}", g.rule)
    };
    let fill = width.saturating_sub(head.width() + tail.width());
    let mut line = Line::new();
    line.paint(&head, Color::DarkGrey);
    line.paint(&g.rule.repeat(fill), Color::DarkGrey);
    line.paint(&tail, Color::DarkGrey);
    line.fit(width)
}

// ---- 진행 화면 ----

fn bar(fraction: f64, width: usize, g: Glyphs) -> String {
    let full = ((fraction.clamp(0.0, 1.0) * width as f64).round() as usize).min(width);
    format!("{}{}", g.bar_full.repeat(full), g.bar_empty.repeat(width - full))
}

/// 페이지별 진행률(수집 회차와 클릭)의 평균. 막대 옆의 페이지 수와 어긋나지 않는다.
fn fraction(rows: &[(String, ProgressInfo)]) -> f64 {
    if rows.is_empty() {
        return 0.0;
    }
    let sum: f64 = rows
        .iter()
        .map(|(_, r)| {
            if r.finished() {
                return 1.0;
            }
            let done = r.finished_passes.min(r.passes) as f64 + r.clicked as f64;
            let total = r.passes as f64 + r.total_ads as f64;
            if total == 0.0 {
                0.0
            } else {
                (done / total).min(0.99)
            }
        })
        .sum();
    sum / rows.len() as f64
}

fn kpi(t: &Totals, no_ads: usize, width: usize, g: Glyphs) -> Line {
    let compact = width < 52;
    let mut line = Line::plain(" ");
    if !compact {
        line.push("클릭  ");
    }
    line.paint(&format!("{} {}", g.done, t.redirects), Color::Green);
    line.push("  ");
    let fail_color = if t.failed > 0 { Color::Red } else { Color::DarkGrey };
    line.paint(&format!("{} {}", g.fail, t.failed), fail_color);
    if t.unconfirmed > 0 {
        line.push("  ");
        line.paint(&format!("? {}", t.unconfirmed), Color::Yellow);
    }
    line.push(if compact { "  " } else { "   " });
    line.push(&format!("대기 {}", t.pending));
    let gap = if compact { "  " } else { "      " };
    if t.errors > 0 || !compact {
        line.push(gap);
        let color = if t.errors > 0 { Color::Red } else { Color::DarkGrey };
        line.paint(&format!("수집 실패 {}", t.errors), color);
    }
    if no_ads > 0 {
        line.push(gap);
        line.paint(&format!("광고 없음 {no_ads}"), Color::Yellow);
    }
    line.fit(width)
}

struct Section {
    title: String,
    right: String,
    /// 페이지(또는 줄) 묶음. 자를 때는 묶음 단위로 자른다.
    groups: Vec<Vec<Line>>,
    /// 다 들어가지 않을 때 대신 쓰는 한 줄. 없으면 묶음 단위로 자른다.
    folded: Option<Line>,
    /// 높이가 모자랄 때 앞에서부터 생략하고 최근 항목(뒤쪽)을 남긴다.
    keep_tail: bool,
    /// `keep_tail`에서 한 항목도 못 넣을 때 대신 쓰는 한 줄 요약.
    overflow_summary: Option<Line>,
}

impl Section {
    fn height(&self) -> usize {
        1 + self.groups.iter().map(Vec::len).sum::<usize>()
    }
}

fn live_frame(
    rows: &[(String, ProgressInfo)],
    phase: Phase,
    time: Duration,
    size: (u16, u16),
    g: Glyphs,
) -> Vec<Line> {
    let width = usize::from(size.0.saturating_sub(1));
    let height = usize::from(size.1);
    if width < 10 || height == 0 {
        return vec![];
    }
    let total = Totals::from_rows(rows);
    let done_pages = rows.iter().filter(|(_, r)| r.finished()).count();
    let no_ads = rows.iter().filter(|(_, r)| r.no_ads()).count();

    // 제목 줄: 왼쪽 이름, 오른쪽 경과 시간.
    let right = match phase {
        Phase::Working => format!("경과 {}", elapsed(time)),
        Phase::Settling => format!("적립 반영 대기 중 · {}", elapsed(time)),
        Phase::ReadingBalance => format!("최종 잔액 조회 중 · {}", elapsed(time)),
    };
    let mut title = Line::new();
    title.push(" ");
    title.paint("InvenAdClicker", Color::Cyan);
    let gap = width.saturating_sub(title.width() + right.width() + 1);
    title.push(&" ".repeat(gap.max(1)));
    title.push(&right);
    let title = title.bold().fit(width);

    // 진행 막대.
    let label = format!(" {done_pages}/{} 페이지", rows.len());
    let bar_width = width.saturating_sub(label.width() + 3).min(48);
    let mut progress = Line::plain(" ");
    if bar_width >= 6 {
        progress.paint(&bar(fraction(rows), bar_width, g), Color::Cyan);
    }
    progress.push(&label);
    let progress = progress.fit(width);

    let mut lines = vec![title];
    if height >= 2 {
        lines.push(progress);
    }
    if height >= 4 {
        if height >= 14 {
            lines.push(Line::new());
        }
        lines.push(kpi(&total, no_ads, width, g));
    }
    let mut room = height.saturating_sub(lines.len());
    if room < 2 {
        lines.truncate(height);
        return lines;
    }

    let active: Vec<_> = rows.iter().filter(|(_, r)| r.active()).collect();
    let problems: Vec<_> = rows
        .iter()
        .filter(|(_, r)| !r.active() && r.has_problem())
        .collect();
    let mut done_with_idx: Vec<(usize, &(String, ProgressInfo))> = rows
        .iter()
        .enumerate()
        .filter(|(_, (_, r))| !r.active() && !r.has_problem() && r.finished())
        .collect();
    done_with_idx.sort_by_key(|(idx, (_, r))| (r.completed_seq.unwrap_or(u64::MAX), *idx));
    let done: Vec<&(String, ProgressInfo)> = done_with_idx.into_iter().map(|(_, row)| row).collect();
    let waiting: Vec<_> = rows
        .iter()
        .filter(|(_, r)| !r.active() && !r.has_problem() && !r.finished())
        .collect();
    let columns = Columns::new(rows.iter().map(|(u, _)| u.as_str()), width);

    let mut sections = vec![];
    for (title, pages) in [("진행 중", &active), ("문제", &problems)] {
        if !pages.is_empty() {
            sections.push(Section {
                title: title.into(),
                right: pages.len().to_string(),
                groups: pages.iter().map(|(u, r)| columns.lines(u, r, width, g)).collect(),
                folded: None,
                keep_tail: false,
                overflow_summary: None,
            });
        }
    }
    if !done.is_empty() {
        let clicks: usize = done.iter().map(|(_, r)| r.redirects).sum();
        let empty = done.iter().filter(|(_, r)| r.no_ads()).count();
        let mut summary = Line::plain(" ");
        summary.paint(g.done, Color::Green);
        summary.push(&format!(" {}페이지", done.len() - empty));
        if empty > 0 {
            summary.push("   ");
            summary.paint(g.empty, Color::Yellow);
            summary.push(&format!(" 광고 없음 {empty}페이지"));
        }
        sections.push(Section {
            title: "완료".into(),
            right: format!("{}페이지 · {} {clicks}", done.len(), g.done),
            groups: done.iter().map(|(u, r)| columns.lines(u, r, width, g)).collect(),
            folded: None,
            keep_tail: true,
            overflow_summary: Some(summary.fit(width)),
        });
    }
    if !waiting.is_empty() {
        let waiting_style = RowStyle {
            icon: g.waiting,
            color: Color::DarkGrey,
        };
        let mut folded = Line::plain(" ");
        folded.paint(g.waiting, Color::DarkGrey);
        folded.push(&format!(" {}페이지", waiting.len()));
        sections.push(Section {
            title: "대기".into(),
            right: waiting.len().to_string(),
            groups: waiting
                .iter()
                .map(|(u, _)| wrap_url_line(waiting_style, u, width))
                .collect(),
            folded: Some(folded.fit(width)),
            keep_tail: false,
            overflow_summary: None,
        });
    }

    // 높이 배분: 모두 들어가면 그대로. 넘치면 대기를 한 줄 요약으로 접고, 완료는 최근 항목부터
    // 세로로 표시한다. 그래도 넘치면 진행 중·문제를 페이지 단위로 자른다.
    let total_height = |s: &[Section]| s.iter().map(Section::height).sum::<usize>();
    for i in (0..sections.len()).rev() {
        if total_height(&sections) <= room {
            break;
        }
        if let Some(folded) = sections[i].folded.take() {
            sections[i].groups = vec![vec![folded]];
        }
    }
    let gaps = height >= 20 && total_height(&sections) + sections.len() <= room;
    for (i, s) in sections.iter().enumerate() {
        if gaps && i > 0 {
            lines.push(Line::new());
            room -= 1;
        }
        if room < 2 {
            break;
        }
        lines.push(rule(&s.title, &s.right, width, g));
        room -= 1;
        // 우선순위는 진행 중 > 문제 > 완료 > 대기. 진행 중이 길면 문제 묶음에 몇 줄을 남기고,
        // 완료가 길면 대기 요약에 최소 자리를 남긴다.
        let reserve: usize = match sections.get(i + 1) {
            Some(next) if s.title == "진행 중" && next.title == "문제" => {
                next.height().min(4).min(room.saturating_sub(2))
            }
            Some(next) if s.title == "완료" && next.title == "대기" && room >= 6 => {
                next.height().min(2)
            }
            _ => 0,
        };
        let budget = room.saturating_sub(reserve).max(1);
        let mut used = 0;
        if s.keep_tail {
            let total_lines: usize = s.groups.iter().map(Vec::len).sum();
            if total_lines <= budget {
                for group in &s.groups {
                    lines.extend(group.iter().cloned());
                    used += group.len();
                }
            } else {
                let mut kept_lines = 0usize;
                let mut start = s.groups.len();
                for k in (0..s.groups.len()).rev() {
                    let g_len = s.groups[k].len();
                    let indicator = usize::from(k > 0);
                    if kept_lines + g_len + indicator > budget {
                        break;
                    }
                    kept_lines += g_len;
                    start = k;
                }
                if start == s.groups.len() {
                    let fallback = s.overflow_summary.clone().unwrap_or_else(|| {
                        let mut more = Line::plain("   ");
                        more.paint(&format!("이전 완료 {}페이지", s.groups.len()), Color::DarkGrey);
                        more.fit(width)
                    });
                    lines.push(fallback);
                    used += 1;
                } else {
                    if start > 0 {
                        let mut more = Line::plain("   ");
                        more.paint(&format!("이전 완료 {start}페이지"), Color::DarkGrey);
                        lines.push(more.fit(width));
                        used += 1;
                    }
                    for group in &s.groups[start..] {
                        lines.extend(group.iter().cloned());
                        used += group.len();
                    }
                }
            }
        } else {
            for (k, group) in s.groups.iter().enumerate() {
                let left = s.groups.len() - k;
                // 마지막이 아니면 "외 n페이지" 한 줄을 남겨 둔다.
                let tail = usize::from(left > 1);
                if used + group.len() + tail > budget && !(left == 1 && used + group.len() <= budget) {
                    let mut more = Line::plain("   ");
                    more.paint(&format!("외 {left}페이지"), Color::DarkGrey);
                    lines.push(more.fit(width));
                    used += 1;
                    break;
                }
                lines.extend(group.iter().cloned());
                used += group.len();
            }
        }
        room = room.saturating_sub(used);
    }
    lines.truncate(height);
    lines
}


fn paint(
    out: &mut impl Write,
    old: &[Line],
    new: &[Line],
    size: (u16, u16),
    resized: bool,
    colors: bool,
) -> io::Result<()> {
    let width = usize::from(size.0.saturating_sub(1));
    let height = usize::from(size.1);
    let count = if resized {
        height
    } else {
        old.len().max(new.len()).min(height)
    };
    for y in 0..count {
        let prev = old.get(y);
        let next = new.get(y);
        if !resized && prev == next {
            continue;
        }
        queue!(out, MoveTo(0, y as u16))?;
        if let Some(line) = next {
            line.write(out, colors)?;
        }
        if resized {
            queue!(out, terminal::Clear(terminal::ClearType::UntilNewLine))?;
        } else {
            let text = next.map_or(0, Line::width);
            let erase = prev
                .map_or(0, Line::width)
                .min(width)
                .saturating_sub(text);
            if erase > 0 {
                queue!(out, Print(" ".repeat(erase)))?;
            }
        }
    }
    out.flush()
}

struct Screen;
impl Drop for Screen {
    fn drop(&mut self) {
        let _ = execute!(io::stdout(), LeaveAlternateScreen, Show);
    }
}

pub(crate) fn render(
    state: &ProgressState,
    phase: watch::Receiver<Phase>,
    start: Instant,
) -> Result<()> {
    let stdout = io::stdout();
    execute!(stdout.lock(), EnterAlternateScreen)?;
    let _screen = Screen;
    let mut out = io::BufWriter::new(stdout.lock());
    execute!(out, Hide)?;
    let colors = use_colors();
    let g = glyphs();
    let mut previous = vec![];
    let mut last_size = None;
    while phase.has_changed().is_ok() {
        let size = terminal::size()?;
        let lines = live_frame(&state.snapshot(), *phase.borrow(), start.elapsed(), size, g);
        paint(
            &mut out,
            &previous,
            &lines,
            size,
            last_size.is_some_and(|old| old != size),
            colors,
        )?;
        previous = lines;
        last_size = Some(size);
        std::thread::sleep(Duration::from_millis(200));
    }
    Ok(())
}

// ---- 종료 요약 ----

fn grouped(n: u64) -> String {
    let digits = n.to_string();
    let mut result = String::new();
    for (i, ch) in digits.char_indices() {
        if i > 0 && (digits.len() - i).is_multiple_of(3) {
            result.push(',');
        }
        result.push(ch);
    }
    result
}

fn delta(name: &str, before: u64, after: u64) -> (String, Color) {
    let sign = if after < before { "-" } else { "+" };
    let color = match after.cmp(&before) {
        std::cmp::Ordering::Greater => Color::Green,
        std::cmp::Ordering::Less => Color::Red,
        std::cmp::Ordering::Equal => Color::DarkGrey,
    };
    (format!("{name} {sign}{}", grouped(before.abs_diff(after))), color)
}

pub(crate) fn final_report(
    out: &mut impl Write,
    rows: &[(String, ProgressInfo)],
    before: Points,
    after: &Result<Points>,
    time: Duration,
    width: usize,
    colors: bool,
) -> io::Result<()> {
    final_report_with(out, rows, before, after, time, width, colors, glyphs())
}

#[allow(clippy::too_many_arguments)]
fn final_report_with(
    out: &mut impl Write,
    rows: &[(String, ProgressInfo)],
    before: Points,
    after: &Result<Points>,
    time: Duration,
    width: usize,
    colors: bool,
    g: Glyphs,
) -> io::Result<()> {
    let width = width.max(20);
    let t = Totals::from_rows(rows);
    let done_pages = rows.iter().filter(|(_, r)| r.finished()).count();
    let no_ads = rows.iter().filter(|(_, r)| r.no_ads()).count();
    let (icon, status, color) = if t.clicked > 0 && t.redirects == 0 {
        (g.fail, "클릭이 모두 실패함", Color::Red)
    } else if after.is_err() {
        (g.fail, "최종 잔액 확인 실패", Color::Red)
    } else if t.failed + t.errors + t.unconfirmed > 0 {
        (g.warn, "일부 실패", Color::Yellow)
    } else if t.clicked == 0 {
        (g.warn, "클릭할 광고 없음", Color::Yellow)
    } else {
        (g.done, "완료", Color::Green)
    };

    let mut head = Line::plain(" ");
    head.paint(&format!("{icon} {status}"), color);
    head.push(&format!(" · {}", duration_words(time)));
    if let Ok(points) = after {
        let (ini, ini_color) = delta("이니", before.ini, points.ini);
        let (beni, beni_color) = delta("베니", before.beni, points.beni);
        let exp_item = before
            .exp
            .zip(points.exp)
            .map(|(b, a)| delta("경험치", b, a));
        let pts_width = ini.width() + 3 + beni.width();
        let full_width = exp_item
            .as_ref()
            .map_or(pts_width, |(e, _)| e.width() + 3 + pts_width);
        let show_exp = exp_item.is_some() && head.width() + 2 + full_width <= width;
        let right_width = if show_exp { full_width } else { pts_width };
        let gap = width.saturating_sub(head.width() + right_width + 1).max(3);
        head.push(&" ".repeat(gap));
        if show_exp {
            if let Some((exp, exp_color)) = exp_item {
                head.paint(&exp, exp_color);
                head.push("   ");
            }
        }
        head.paint(&ini, ini_color);
        head.push("   ");
        head.paint(&beni, beni_color);
    }
    writeln!(out)?;
    head.bold().fit(width).write(out, colors)?;
    writeln!(out)?;
    writeln!(out)?;

    let mut counts = Line::plain(" 클릭  ");
    counts.paint(&format!("{} {}", g.done, t.redirects), Color::Green);
    counts.push("  ");
    counts.paint(
        &format!("{} {}", g.fail, t.failed),
        if t.failed > 0 { Color::Red } else { Color::DarkGrey },
    );
    if t.unconfirmed > 0 {
        counts.push("  ");
        counts.paint(&format!("? {}", t.unconfirmed), Color::Yellow);
    }
    counts.push(&format!("      페이지 {done_pages}/{}", rows.len()));
    if no_ads > 0 {
        counts.push(" (");
        counts.paint(&format!("광고 없음 {no_ads}"), Color::Yellow);
        counts.push(")");
    }
    if t.errors > 0 {
        counts.push("   ");
        counts.paint(&format!("수집 실패 {}", t.errors), Color::Red);
    }
    counts.fit(width).write(out, colors)?;
    writeln!(out)?;

    match after {
        Ok(points) => {
            writeln!(
                out,
                " 잔액  이니 {} → {}   베니 {} → {}",
                grouped(before.ini),
                grouped(points.ini),
                grouped(before.beni),
                grouped(points.beni)
            )?;
            if let (Some(b_exp), Some(a_exp)) = (before.exp, points.exp) {
                let (exp_delta, exp_color) = delta("경험치", b_exp, a_exp);
                let exp_change = exp_delta.strip_prefix("경험치 ").unwrap_or(&exp_delta);
                let mut exp_line = Line::plain(format!(
                    " 경험치  {} → {}  (",
                    grouped(b_exp),
                    grouped(a_exp)
                ));
                exp_line.paint(exp_change, exp_color);
                exp_line.push(")");
                exp_line.fit(width).write(out, colors)?;
                writeln!(out)?;
            }
        }
        Err(e) => {
            if let Some(b_exp) = before.exp {
                writeln!(
                    out,
                    " 시작 잔액  이니 {}  베니 {}  경험치 {}",
                    grouped(before.ini),
                    grouped(before.beni),
                    grouped(b_exp)
                )?;
            } else {
                writeln!(
                    out,
                    " 시작 잔액  이니 {}  베니 {}",
                    grouped(before.ini),
                    grouped(before.beni)
                )?;
            }
            writeln!(out, " 최종 잔액 미확인 · {}", clean(&format!("{e:#}")))?;
        }
    }

    // 실패만 모두 보여준다. 광고 없는 페이지는 수만 앞에서 알린다.
    let problems: Vec<_> = rows
        .iter()
        .filter(|(_, r)| r.has_problem())
        .collect();
    if !problems.is_empty() {
        writeln!(out)?;
        Line::plain(format!(" 문제 {}건", problems.len()))
            .bold()
            .write(out, colors)?;
        writeln!(out)?;
        let columns = Columns::new(problems.iter().map(|(u, _)| u.as_str()), width);
        for (url, r) in problems {
            for line in columns.lines(url, r, width, g) {
                line.write(out, colors)?;
                writeln!(out)?;
            }
        }
    }
    out.flush()
}

pub(crate) fn balance_refresh(
    out: &mut impl Write,
    before: Points,
    previous: Option<Points>,
    current: &Result<Points>,
    timestamp: &str,
    width: usize,
    colors: bool,
) -> io::Result<()> {
    balance_refresh_with(out, before, previous, current, timestamp, width, colors, glyphs())
}

#[allow(clippy::too_many_arguments)]
fn balance_refresh_with(
    out: &mut impl Write,
    before: Points,
    previous: Option<Points>,
    current: &Result<Points>,
    timestamp: &str,
    width: usize,
    colors: bool,
    g: Glyphs,
) -> io::Result<()> {
    let width = width.max(20);
    match current {
        Ok(points) => {
            let (ini_delta, ini_color) = delta("이니", before.ini, points.ini);
            let (beni_delta, beni_color) = delta("베니", before.beni, points.beni);
            let ini_change = ini_delta.strip_prefix("이니 ").unwrap_or(&ini_delta);
            let beni_change = beni_delta.strip_prefix("베니 ").unwrap_or(&beni_delta);
            let mut line = Line::plain(format!(
                " [{timestamp}] 재조회  이니 {} → {} (",
                grouped(before.ini),
                grouped(points.ini),
            ));
            line.paint(ini_change, ini_color);
            line.push(&format!(
                ")   베니 {} → {} (",
                grouped(before.beni),
                grouped(points.beni),
            ));
            line.paint(beni_change, beni_color);
            line.push(")");
            if previous == Some(*points) {
                line.push("   ");
                line.paint("변동 없음", Color::DarkGrey);
            }
            line.fit(width).write(out, colors)?;
            writeln!(out)?;
            if let (Some(b_exp), Some(a_exp)) = (before.exp, points.exp) {
                let (exp_delta, exp_color) = delta("경험치", b_exp, a_exp);
                let exp_change = exp_delta.strip_prefix("경험치 ").unwrap_or(&exp_delta);
                let mut exp_line = Line::plain(format!(
                    "                    경험치 {} → {} (",
                    grouped(b_exp),
                    grouped(a_exp),
                ));
                exp_line.paint(exp_change, exp_color);
                exp_line.push(")");
                exp_line.fit(width).write(out, colors)?;
                writeln!(out)?;
            }
        }
        Err(e) => {
            let mut line = Line::plain(format!(" [{timestamp}] "));
            line.paint(&format!("{} 잔액 재조회 실패", g.fail), Color::Red);
            line.push(&format!(" · {}", clean(&format!("{e:#}"))));
            line.fit(width).write(out, colors)?;
            writeln!(out)?;
        }
    }
    out.flush()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn page(passes: u32) -> ProgressInfo {
        ProgressInfo {
            passes,
            ..Default::default()
        }
    }

    fn sample() -> Vec<(String, ProgressInfo)> {
        let mut rows: Vec<_> = (0..27)
            .map(|i| {
                let url = if i % 3 == 0 {
                    format!("https://m.inven.co.kr/board/game{i}/4336/{}", 1000 + i)
                } else {
                    format!("https://site{i}.inven.co.kr/")
                };
                (
                    url,
                    ProgressInfo {
                        finished_passes: 2,
                        total_ads: 3,
                        clicked: 3,
                        redirects: 3,
                        ..page(2)
                    },
                )
            })
            .collect();
        rows[1].1 = ProgressInfo {
            finished_passes: 1,
            collectors: 1,
            ..page(2)
        };
        rows[2].1 = ProgressInfo {
            finished_passes: 2,
            total_ads: 5,
            clicked: 2,
            redirects: 2,
            requests: 1,
            pending: 3,
            ..page(2)
        };
        rows[4].1 = ProgressInfo {
            finished_passes: 2,
            total_ads: 3,
            clicked: 3,
            failed: 3,
            problem: Some("webzine@x19 · HTTP 200 · JSON error=1 · 3021ms".into()),
            ..page(2)
        };
        rows[6].1 = ProgressInfo {
            finished_passes: 2,
            ..page(2)
        };
        rows[26].1 = page(2);
        rows
    }

    #[test]
    fn every_size_fits_without_wrapping_or_control_characters() {
        let rows = sample();
        for g in [UNICODE, ASCII] {
            for columns in [1u16, 10, 20, 32, 40, 52, 60, 80, 100, 140, 220] {
                for rows_count in [1u16, 2, 3, 4, 6, 8, 12, 16, 24, 40, 60] {
                    let lines = live_frame(
                        &rows,
                        Phase::Working,
                        Duration::from_secs(83),
                        (columns, rows_count),
                        g,
                    );
                    assert!(lines.len() <= usize::from(rows_count));
                    for l in &lines {
                        assert!(l.width() < usize::from(columns.max(1)), "{columns}x{rows_count}: {:?}", l.text);
                        assert!(!l.text.chars().any(char::is_control));
                        for (r, _) in &l.spans {
                            assert!(r.end <= l.text.len() && l.text.is_char_boundary(r.start));
                        }
                    }
                }
            }
        }
    }

    #[test]
    fn layout_puts_active_and_problem_pages_first_and_shows_full_urls() {
        let rows = sample();
        let text = |size| {
            live_frame(&rows, Phase::Working, Duration::ZERO, size, UNICODE)
                .iter()
                .map(|l| l.text.clone())
                .collect::<Vec<_>>()
                .join("\n")
        };
        let tall = text((100, 40));
        let active = tall.find("진행 중").unwrap();
        let problem = tall.find("문제").unwrap();
        let done = tall.find("완료").unwrap();
        assert!(active < problem && problem < done);
        assert!(tall.contains("https://site1.inven.co.kr/") && tall.contains("https://site2.inven.co.kr/"));
        assert!(tall.contains("https://site4.inven.co.kr/") && tall.contains("HTTP 200"));
        assert!(tall.contains("https://m.inven.co.kr/board/game0/4336/1000"));
        assert!(tall.contains("https://site5.inven.co.kr/") && tall.contains("광고 없음"));
        assert!(tall.contains("대기") && tall.contains("https://site26.inven.co.kr/"));
        // 완료 URL은 가로 나열이 아니라 세로로 한 줄씩 쌓인다.
        for line in tall.lines() {
            assert!(
                !(line.contains("site5") && line.contains("site7")),
                "완료 목록이 한 줄에 가로로 붙었습니다: {line}"
            );
        }
        // 낮은 창: 대기는 접히고 진행 중은 남으며, 완료는 세로 tail 또는 요약으로 맞춘다.
        let short = text((100, 12));
        assert!(short.contains("https://site1.inven.co.kr/"));
        assert!(short.contains("이전 완료") || short.contains("22페이지"));
        // 좁은 창: 전체 URL과 오류 문구를 자르지 않고 줄바꿈해 유지한다.
        let narrow = text((50, 40));
        let joined: String = narrow
            .lines()
            .map(|l| l.trim_start())
            .collect::<Vec<_>>()
            .join("");
        assert!(joined.contains("https://m.inven.co.kr/board/game24/4336/1024"));
        assert!(joined.contains("https://site4.inven.co.kr/"));
        assert!(joined.contains("JSON error=1"));
        assert!(!narrow.contains("...") && !narrow.contains('…'));
    }

    #[test]
    fn done_section_stacks_vertically_in_completion_order_and_scrolls_tail() {
        let urls = vec![
            "https://www.inven.co.kr/board/first".to_owned(),
            "https://www.inven.co.kr/board/second".to_owned(),
            "https://www.inven.co.kr/board/third".to_owned(),
            "https://www.inven.co.kr/board/fourth".to_owned(),
        ];
        let state = ProgressState::new(&urls, 1);
        for &idx in &[2usize, 0, 1] {
            state.update(&urls[idx], crate::progress::Event::StartCollection { inspect_only: false });
            state.update(&urls[idx], crate::progress::Event::EndCollection { errors: 0 });
        }
        let rows = state.snapshot();
        let frame: String = live_frame(&rows, Phase::Working, Duration::ZERO, (100, 40), UNICODE)
            .iter()
            .map(|l| l.text.clone())
            .collect::<Vec<_>>()
            .join("\n");
        let p2 = frame.find(&urls[2]).unwrap();
        let p0 = frame.find(&urls[0]).unwrap();
        let p1 = frame.find(&urls[1]).unwrap();
        assert!(p2 < p0 && p0 < p1, "완료 순서 정렬 실패:\n{frame}");
        for line in frame.lines() {
            assert_eq!(
                urls.iter().filter(|u| line.contains(u.as_str())).count(),
                usize::from(urls.iter().any(|u| line.contains(u.as_str()))),
                "한 줄에 URL이 둘 이상 들어갔습니다: {line}"
            );
        }

        // 4번째 URL도 완료한 뒤 높이를 7줄로 줄이면 최근 완료 항목(1, 3)만 세로로 남기고 앞부분(2페이지)은 접는다.
        state.update(&urls[3], crate::progress::Event::StartCollection { inspect_only: false });
        state.update(&urls[3], crate::progress::Event::EndCollection { errors: 0 });
        let rows = state.snapshot();
        let tight: String = live_frame(&rows, Phase::Working, Duration::ZERO, (100, 7), UNICODE)
            .iter()
            .map(|l| l.text.clone())
            .collect::<Vec<_>>()
            .join("\n");
        assert!(tight.contains("이전 완료 2페이지"), "{tight}");
        assert!(!tight.contains(&urls[2]) && !tight.contains(&urls[0]), "{tight}");
        assert!(tight.contains(&urls[1]) && tight.contains(&urls[3]), "{tight}");
    }

    #[test]
    fn identical_frames_write_nothing() {
        let rows = sample();
        let frame = live_frame(&rows, Phase::Working, Duration::ZERO, (80, 20), UNICODE);
        let mut data = Vec::new();
        paint(&mut data, &frame, &frame, (80, 20), false, true).unwrap();
        assert!(data.is_empty());
    }

    #[test]
    fn final_report_shows_result_balance_and_only_failures() {
        let rows = sample();
        let before = Points { ini: 999, beni: 9, exp: Some(100) };
        let mut out = Vec::new();
        final_report_with(
            &mut out,
            &rows,
            before,
            &Ok(Points { ini: 1002, beni: 8, exp: Some(150) }),
            Duration::from_secs(65),
            90,
            false,
            UNICODE,
        )
        .unwrap();
        let text = String::from_utf8(out).unwrap();
        assert!(text.contains("일부 실패") && text.contains("1분 5초"));
        assert!(text.contains("경험치 +50") && text.contains("이니 +3") && text.contains("베니 -1"));
        assert!(text.contains("999 → 1,002") && text.contains("100 → 150"));
        assert!(text.contains("문제 1건") && text.contains("https://site4.inven.co.kr/") && text.contains("JSON error=1"));
        assert!(!text.contains("https://site5.inven.co.kr/"));
        let mut out = Vec::new();
        final_report_with(
            &mut out,
            &rows,
            before,
            &Err(anyhow::anyhow!("조회 실패")),
            Duration::ZERO,
            45,
            false,
            ASCII,
        )
        .unwrap();
        let text = String::from_utf8(out).unwrap();
        assert!(text.contains("최종 잔액 확인 실패") && text.contains("조회 실패") && !text.contains("→"));
    }

    #[test]
    fn row_icons_follow_page_state() {
        let cases = [
            (page(1), UNICODE.waiting, Color::DarkGrey),
            (ProgressInfo { collectors: 1, ..page(1) }, UNICODE.collecting, Color::Cyan),
            (ProgressInfo { pending: 1, total_ads: 1, finished_passes: 1, ..page(1) }, UNICODE.clicking, Color::Cyan),
            (ProgressInfo { finished_passes: 1, ..page(1) }, UNICODE.empty, Color::Yellow),
            (ProgressInfo { finished_passes: 1, errors: 1, ..page(1) }, UNICODE.fail, Color::Red),
            (ProgressInfo { finished_passes: 1, total_ads: 2, clicked: 2, failed: 2, ..page(1) }, UNICODE.fail, Color::Red),
            (ProgressInfo { finished_passes: 1, total_ads: 2, clicked: 2, redirects: 1, failed: 1, ..page(1) }, UNICODE.warn, Color::Yellow),
            (ProgressInfo { finished_passes: 1, total_ads: 1, clicked: 1, redirects: 1, ..page(1) }, UNICODE.done, Color::Green),
        ];
        for (row, icon, color) in cases {
            let style = row_style(&row, UNICODE);
            assert_eq!((style.icon, style.color), (icon, color), "{row:?}");
        }
    }

    #[test]
    fn column_alignment_stays_fixed_across_progress_stages() {
        let short_url = "https://it.inven.co.kr/".to_owned();
        let long_url = "https://www.inven.co.kr/board/ff14/4336/717515".to_owned();
        let frame_rows = |long_info| {
            let rows = vec![
                (
                    short_url.clone(),
                    ProgressInfo {
                        collectors: 1,
                        ..page(2)
                    },
                ),
                (long_url.clone(), long_info),
            ];
            live_frame(&rows, Phase::Working, Duration::ZERO, (100, 30), UNICODE)
        };
        let detail_col = |lines: &[Line]| -> usize {
            let line = lines
                .iter()
                .find(|l| l.text.contains(&short_url))
                .expect("short url line");
            line.text.find("수집 ").expect("detail column")
        };
        let while_waiting = frame_rows(page(2));
        let while_done = frame_rows(ProgressInfo {
            finished_passes: 2,
            total_ads: 3,
            clicked: 3,
            redirects: 3,
            ..page(2)
        });
        assert_eq!(detail_col(&while_waiting), detail_col(&while_done));
    }

    #[test]
    fn balance_refresh_formats_deltas_unchanged_and_errors() {
        let before = Points {
            ini: 100,
            beni: 10,
            exp: Some(200),
        };
        let prev = Points {
            ini: 105,
            beni: 10,
            exp: Some(205),
        };
        let curr = Points {
            ini: 120,
            beni: 9,
            exp: Some(230),
        };
        let mut out = Vec::new();
        balance_refresh_with(
            &mut out,
            before,
            Some(prev),
            &Ok(curr),
            "00:25:10",
            90,
            false,
            UNICODE,
        )
        .unwrap();
        let changed = String::from_utf8(out).unwrap();
        assert!(changed.contains("[00:25:10] 재조회"));
        assert!(changed.contains("100 → 120 (+20)") && changed.contains("10 → 9 (-1)"));
        assert!(changed.contains("경험치 200 → 230 (+30)") && !changed.contains("변동 없음"));

        let mut out = Vec::new();
        balance_refresh_with(
            &mut out,
            before,
            Some(curr),
            &Ok(curr),
            "00:25:20",
            90,
            false,
            UNICODE,
        )
        .unwrap();
        let same = String::from_utf8(out).unwrap();
        assert!(same.contains("[00:25:20] 재조회") && same.contains("변동 없음"));

        let mut out = Vec::new();
        balance_refresh_with(
            &mut out,
            before,
            Some(curr),
            &Err(anyhow::anyhow!("네트워크 오류")),
            "00:25:30",
            90,
            false,
            ASCII,
        )
        .unwrap();
        let err = String::from_utf8(out).unwrap();
        assert!(err.contains("[00:25:30]") && err.contains("x 잔액 재조회 실패") && err.contains("네트워크 오류"));
    }

    #[test]
    #[ignore]
    fn preview_layouts() {
        let rows = sample();
        for size in [(100u16, 40u16), (80, 20), (60, 14), (44, 10)] {
            println!("===== {}x{}", size.0, size.1);
            for l in live_frame(&rows, Phase::Working, Duration::from_secs(83), size, UNICODE) {
                println!("|{}", l.text);
            }
        }
        let mut out = Vec::new();
        final_report_with(&mut out, &rows, Points { ini: 2462270, beni: 58045, exp: Some(1280742) }, &Ok(Points { ini: 2462306, beni: 58045, exp: Some(1280778) }), Duration::from_secs(192), 90, false, UNICODE).unwrap();
        println!("===== final\n{}", String::from_utf8(out).unwrap());
    }
}
