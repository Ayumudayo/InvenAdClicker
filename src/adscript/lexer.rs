use std::sync::Arc;

#[derive(Clone, Debug, PartialEq)]
pub(crate) enum Tok {
    Id(String),
    Str(String),
    Tpl(Vec<Part>),
    Num(f64),
    P(&'static str),
    Re,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) enum Part {
    Lit(String),
    Expr(Arc<Vec<Tok>>),
}

const PUNCT: &[&str] = &[
    "===", "!==", "...", "==", "!=", "<=", ">=", "&&", "||", "??", "?.", "++", "--", "+=", "-=",
    "*=", "/=", "=>", "{", "}", "(", ")", "[", "]", ";", ",", ".", "<", ">", "+", "-", "*", "/",
    "%", "!", "?", ":", "=", "&", "|", "^", "~",
];

pub(crate) fn lex(src: &str) -> Arc<Vec<Tok>> {
    let s: Vec<char> = src.chars().collect();
    Arc::new(lex_chars(&s))
}

fn lex_chars(s: &[char]) -> Vec<Tok> {
    let mut out = Vec::new();
    let mut i = 0;
    while i < s.len() {
        let c = s[i];
        if c.is_whitespace() {
            i += 1;
        } else if c == '/' && s.get(i + 1) == Some(&'/') {
            while i < s.len() && s[i] != '\n' {
                i += 1;
            }
        } else if c == '/' && s.get(i + 1) == Some(&'*') {
            i += 2;
            while i < s.len() && !(s[i] == '*' && s.get(i + 1) == Some(&'/')) {
                i += 1;
            }
            i += 2;
        } else if c == '\'' || c == '"' {
            let (text, next) = read_str(s, i);
            out.push(Tok::Str(text));
            i = next;
        } else if c == '`' {
            let (parts, next) = read_tpl(s, i);
            out.push(Tok::Tpl(parts));
            i = next;
        } else if c.is_ascii_digit() {
            let start = i;
            while i < s.len() && (s[i].is_ascii_alphanumeric() || s[i] == '.') {
                i += 1;
            }
            let text: String = s[start..i].iter().collect();
            out.push(Tok::Num(text.parse().unwrap_or(f64::NAN)));
        } else if c.is_alphabetic() || c == '_' || c == '$' {
            let start = i;
            while i < s.len() && (s[i].is_alphanumeric() || s[i] == '_' || s[i] == '$') {
                i += 1;
            }
            out.push(Tok::Id(s[start..i].iter().collect()));
        } else if c == '/' && regex_allowed(out.last()) {
            i = skip_regex(s, i);
            out.push(Tok::Re);
        } else if let Some(p) = PUNCT
            .iter()
            .find(|p| p.chars().enumerate().all(|(k, pc)| s.get(i + k) == Some(&pc)))
        {
            out.push(Tok::P(p));
            i += p.len();
        } else {
            i += 1;
        }
    }
    out
}

fn regex_allowed(last: Option<&Tok>) -> bool {
    match last {
        None => true,
        Some(Tok::P(p)) => !matches!(*p, ")" | "]" | "}"),
        Some(Tok::Id(k)) => matches!(k.as_str(), "return" | "typeof" | "case" | "in" | "of"),
        _ => false,
    }
}

fn skip_regex(s: &[char], mut i: usize) -> usize {
    i += 1;
    let mut class = false;
    while i < s.len() {
        match s[i] {
            '\\' => i += 1,
            '[' => class = true,
            ']' => class = false,
            '/' if !class => {
                i += 1;
                break;
            }
            '\n' => break,
            _ => {}
        }
        i += 1;
    }
    while i < s.len() && s[i].is_ascii_alphabetic() {
        i += 1;
    }
    i
}

fn read_escape(s: &[char], i: usize, out: &mut String) -> usize {
    let Some(&c) = s.get(i) else { return i };
    let hex = |from: usize, len: usize| -> Option<char> {
        let text: String = s.get(from..from + len)?.iter().collect();
        char::from_u32(u32::from_str_radix(&text, 16).ok()?)
    };
    match c {
        'n' => out.push('\n'),
        't' => out.push('\t'),
        'r' => out.push('\r'),
        'b' | 'f' | 'v' | '0' => {}
        '\n' => {}
        'x' => {
            if let Some(ch) = hex(i + 1, 2) {
                out.push(ch);
                return i + 3;
            }
            out.push('x');
        }
        'u' => {
            if let Some(ch) = hex(i + 1, 4) {
                out.push(ch);
                return i + 5;
            }
            out.push('u');
        }
        other => out.push(other),
    }
    i + 1
}

fn read_str(s: &[char], start: usize) -> (String, usize) {
    let quote = s[start];
    let mut out = String::new();
    let mut i = start + 1;
    while i < s.len() && s[i] != quote {
        if s[i] == '\\' {
            i = read_escape(s, i + 1, &mut out);
        } else {
            out.push(s[i]);
            i += 1;
        }
    }
    (out, i + 1)
}

fn read_tpl(s: &[char], start: usize) -> (Vec<Part>, usize) {
    let mut parts = Vec::new();
    let mut lit = String::new();
    let mut i = start + 1;
    while i < s.len() && s[i] != '`' {
        if s[i] == '\\' {
            i = read_escape(s, i + 1, &mut lit);
        } else if s[i] == '$' && s.get(i + 1) == Some(&'{') {
            let mut depth = 1;
            let mut j = i + 2;
            while j < s.len() {
                match s[j] {
                    '{' => depth += 1,
                    '}' => {
                        depth -= 1;
                        if depth == 0 {
                            break;
                        }
                    }
                    '\'' | '"' => j = read_str(s, j).1 - 1,
                    '`' => j = read_tpl(s, j).1 - 1,
                    _ => {}
                }
                j += 1;
            }
            if !lit.is_empty() {
                parts.push(Part::Lit(std::mem::take(&mut lit)));
            }
            let end = j.min(s.len());
            parts.push(Part::Expr(Arc::new(lex_chars(&s[i + 2..end]))));
            i = end + 1;
        } else {
            lit.push(s[i]);
            i += 1;
        }
    }
    if !lit.is_empty() {
        parts.push(Part::Lit(lit));
    }
    (parts, i + 1)
}
