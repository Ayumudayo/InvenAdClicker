use std::sync::Arc;

use super::lexer::Tok;

#[derive(Clone, Debug)]
pub(super) enum V {
    Str(String),
    Num(f64),
    Bool(bool),
    Undef,
    /// 객체. `true`는 스크립트가 직접 만든 빈 객체(`{}`)라 없는 속성이 확실히 `undefined`다.
    Obj(bool),
    Func(Arc<Func>),
    Unk,
}

#[derive(Debug)]
pub(super) struct Func {
    pub toks: Arc<Vec<Tok>>,
    pub params: Vec<String>,
    pub start: usize,
    pub end: usize,
}

impl V {
    pub(super) fn truthy(&self) -> Option<bool> {
        match self {
            V::Str(s) => Some(!s.is_empty()),
            V::Num(n) => Some(*n != 0.0 && !n.is_nan()),
            V::Bool(b) => Some(*b),
            V::Undef => Some(false),
            V::Obj(_) | V::Func(_) => Some(true),
            V::Unk => None,
        }
    }

    pub(super) fn text(&self) -> Option<String> {
        match self {
            V::Str(s) => Some(s.clone()),
            V::Num(n) if n.fract() == 0.0 && n.is_finite() => Some(format!("{}", *n as i64)),
            V::Num(n) => Some(n.to_string()),
            V::Bool(b) => Some(b.to_string()),
            V::Undef => Some("undefined".into()),
            _ => None,
        }
    }
}

pub(super) fn loose_eq(a: &V, b: &V) -> Option<bool> {
    match (a, b) {
        (V::Unk, _) | (_, V::Unk) => None,
        (V::Undef, V::Undef) => Some(true),
        (V::Undef, _) | (_, V::Undef) => Some(false),
        (V::Obj(_) | V::Func(_), _) | (_, V::Obj(_) | V::Func(_)) => None,
        (V::Num(x), V::Num(y)) => Some(x == y),
        (V::Bool(x), V::Bool(y)) => Some(x == y),
        _ => Some(a.text()? == b.text()?),
    }
}

pub(super) fn strict_eq(a: &V, b: &V) -> Option<bool> {
    match (a, b) {
        (V::Str(_), V::Str(_))
        | (V::Num(_), V::Num(_))
        | (V::Bool(_), V::Bool(_))
        | (V::Undef, V::Undef) => loose_eq(a, b),
        (V::Unk, _) | (_, V::Unk) | (V::Obj(_) | V::Func(_), _) | (_, V::Obj(_) | V::Func(_)) => {
            None
        }
        _ => Some(false),
    }
}

pub(super) fn add(a: &V, b: &V) -> V {
    match (a, b) {
        (V::Num(x), V::Num(y)) => V::Num(x + y),
        (V::Str(_), _) | (_, V::Str(_)) => match (a.text(), b.text()) {
            (Some(x), Some(y)) => V::Str(x + &y),
            _ => V::Unk,
        },
        _ => V::Unk,
    }
}

pub(super) fn binop(op: &str, a: &V, b: &V) -> V {
    let flag = |r: Option<bool>| r.map_or(V::Unk, V::Bool);
    match op {
        "+" => add(a, b),
        "==" => flag(loose_eq(a, b)),
        "!=" => flag(loose_eq(a, b).map(|r| !r)),
        "===" => flag(strict_eq(a, b)),
        "!==" => flag(strict_eq(a, b).map(|r| !r)),
        "-" | "*" | "/" | "%" | "<" | ">" | "<=" | ">=" => match (a, b) {
            (V::Num(x), V::Num(y)) => match op {
                "-" => V::Num(x - y),
                "*" => V::Num(x * y),
                "/" => V::Num(x / y),
                "%" => V::Num(x % y),
                "<" => V::Bool(x < y),
                ">" => V::Bool(x > y),
                "<=" => V::Bool(x <= y),
                _ => V::Bool(x >= y),
            },
            _ => V::Unk,
        },
        _ => V::Unk,
    }
}

/// application/x-www-form-urlencoded (URLSearchParams.toString과 같은 규칙).
pub(super) fn form_encode(text: &str) -> String {
    let mut out = String::new();
    for b in text.bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'*' | b'-' | b'.' | b'_' => out.push(b as char),
            b' ' => out.push('+'),
            _ => out.push_str(&format!("%{b:02X}")),
        }
    }
    out
}

/// a1mediation.js의 fetchTimeout과 zicfm.js의 makeUrlA1BP가 붙이는 값. 메타 태그가 없으면 0이다.
pub(super) fn with_a1bp(url: &str) -> String {
    if url.contains("a1bp=") {
        url.to_owned()
    } else {
        format!("{url}{}a1bp=0", if url.contains('?') { '&' } else { '?' })
    }
}
