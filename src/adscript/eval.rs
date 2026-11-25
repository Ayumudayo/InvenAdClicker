use std::collections::{HashMap, HashSet};
use std::sync::{Arc, LazyLock};

use regex::Regex;
use reqwest::Url;

use super::Env;
use super::lexer::{lex, Part, Tok};
use super::value::{add, binop, form_encode, strict_eq, with_a1bp, Func, V};

static BOARD_PATH: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"^/board/([^/]+/\d+/\d+)").unwrap());
static IFRAME_SRC: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"(?is)<iframe\b[^>]*?\ssrc\s*=\s*(?:"([^"]*)"|'([^']*)')"#).unwrap()
});
static FETCH_CALL: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"fetchTimeout\s*\(\s*(?:'([^']*)'|"([^"]*)")\s*,\s*\d+\s*,\s*(?:'([^']*)'|"([^"]*)")"#)
        .unwrap()
});
static ID_ATTR: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r#"(?i)\sid\s*=\s*(?:"([^"]*)"|'([^']*)')"#).unwrap());

fn either(c: &regex::Captures<'_>, a: usize, b: usize) -> String {
    c.get(a)
        .or_else(|| c.get(b))
        .map_or(String::new(), |m| m.as_str().to_owned())
}

pub(super) fn load_env(env: &mut Env, toks: &Arc<Vec<Tok>>) {
    let mut run = Run::new(env, HashSet::new(), None);
    run.top_level(toks);
}

pub(super) fn eval_page_slots(
    env: &Env,
    inline: &[String],
    ids: HashSet<String>,
    page: &Url,
) -> Vec<String> {
    let mut env = env.clone();
    let board = BOARD_PATH
        .captures(page.path())
        .map(|c| c[1].to_owned());
    let mut run = Run::new(&mut env, ids, board);
    for script in inline {
        run.top_level(&lex(script));
    }
    let mut seen = HashSet::new();
    run.out.retain(|u| seen.insert(u.clone()));
    run.out
}

enum Flow {
    Normal,
    Ret(V),
    Brk,
}

const STEP_LIMIT: u32 = 2_000_000;
const DEPTH_LIMIT: u32 = 24;

struct Cur {
    t: Arc<Vec<Tok>>,
    i: usize,
    end: usize,
}

impl Cur {
    fn peek(&self) -> Option<&Tok> {
        self.at(0)
    }
    fn at(&self, k: usize) -> Option<&Tok> {
        let j = self.i + k;
        (j < self.end).then(|| &self.t[j])
    }
    fn is_p(&self, p: &str) -> bool {
        matches!(self.peek(), Some(Tok::P(q)) if *q == p)
    }
    fn is_id(&self, k: &str) -> bool {
        matches!(self.peek(), Some(Tok::Id(q)) if q == k)
    }
    fn eat_p(&mut self, p: &str) -> bool {
        let hit = self.is_p(p);
        if hit {
            self.i += 1;
        }
        hit
    }
    fn done(&self) -> bool {
        self.i >= self.end
    }
    /// 여는 괄호 위치에서 짝이 맞는 닫는 괄호의 위치를 찾는다.
    fn matching(&self, open: usize) -> usize {
        let mut depth = 0i32;
        let mut j = open;
        while j < self.end {
            if let Tok::P(p) = &self.t[j] {
                match *p {
                    "(" | "[" | "{" => depth += 1,
                    ")" | "]" | "}" => {
                        depth -= 1;
                        if depth == 0 {
                            return j;
                        }
                    }
                    _ => {}
                }
            }
            j += 1;
        }
        self.end
    }
    fn skip_group(&mut self) {
        self.i = (self.matching(self.i) + 1).min(self.end);
    }
}

struct Run<'e> {
    env: &'e mut Env,
    locals: Vec<HashMap<String, V>>,
    ids: HashSet<String>,
    board: Option<String>,
    out: Vec<String>,
    steps: u32,
    depth: u32,
    target: Option<String>,
}

impl<'e> Run<'e> {
    fn new(env: &'e mut Env, ids: HashSet<String>, board: Option<String>) -> Self {
        Run {
            env,
            locals: Vec::new(),
            ids,
            board,
            out: Vec::new(),
            steps: 0,
            depth: 0,
            target: None,
        }
    }

    fn top_level(&mut self, toks: &Arc<Vec<Tok>>) {
        let mut c = Cur {
            t: Arc::clone(toks),
            i: 0,
            end: toks.len(),
        };
        while !c.done() {
            self.stmt(&mut c, true);
        }
    }

    fn exhausted(&self) -> bool {
        self.steps > STEP_LIMIT
    }

    // ---- 결과 수집 ----

    fn sink(&mut self, html: &str) {
        if !html.contains("adstream_sx") && !html.contains(" id") {
            return;
        }
        for c in ID_ATTR.captures_iter(html) {
            self.ids.insert(either(&c, 1, 2));
        }
        for c in IFRAME_SRC.captures_iter(html) {
            let src = either(&c, 1, 2);
            if src.contains("adstream_sx") {
                self.out.push(src);
            }
        }
        for c in FETCH_CALL.captures_iter(html) {
            let (id, url) = (either(&c, 1, 2), either(&c, 3, 4));
            self.fetch_timeout(&id, &url);
        }
    }

    fn fetch_timeout(&mut self, id: &str, url: &str) {
        // 대상 요소가 없으면 a1mediation.js는 결과를 표시하지 않는다.
        if self.ids.contains(id) && url.contains("adstream_sx") {
            self.out.push(with_a1bp(url));
        }
    }

    // ---- 변수 ----

    fn declare(&mut self, name: &str, v: V) {
        match self.locals.last_mut() {
            Some(scope) => {
                scope.insert(name.to_owned(), v);
            }
            None => {
                self.env.globals.insert(name.to_owned(), v);
            }
        }
    }

    fn assign(&mut self, path: &str, v: V) {
        let path = path.trim_start_matches("window.");
        if !path.contains('.') {
            for scope in self.locals.iter_mut().rev() {
                if let Some(slot) = scope.get_mut(path) {
                    if !matches!(v, V::Unk) || matches!(slot, V::Unk) {
                        *slot = v;
                    }
                    return;
                }
            }
        }
        let known = self.env.globals.get(path).is_some_and(|old| !matches!(old, V::Unk));
        if !matches!(v, V::Unk) || !known {
            self.env.globals.insert(path.to_owned(), v);
        }
    }

    fn lookup(&self, path: &str) -> V {
        let explicit_global = path.starts_with("window.");
        let path = path.trim_start_matches("window.");
        let (root, rest) = path.split_once('.').map_or((path, None), |(a, b)| (a, Some(b)));
        for scope in self.locals.iter().rev() {
            if let Some(v) = scope.get(root) {
                return match (rest, v) {
                    (None, v) => v.clone(),
                    (Some("length"), V::Str(s)) => V::Num(s.chars().count() as f64),
                    (Some(_), V::Obj(true)) => V::Undef,
                    _ => V::Unk,
                };
            }
        }
        if let Some(v) = self.env.globals.get(path) {
            return v.clone();
        }
        if let Some((parent, _)) = path.rsplit_once('.') {
            if matches!(self.env.globals.get(parent), Some(V::Obj(true))) {
                return V::Undef;
            }
        }
        if rest.is_none() {
            if matches!(
                root,
                "document" | "window" | "location" | "navigator" | "$" | "jQuery" | "console"
            ) {
                return V::Obj(false);
            }
            if explicit_global {
                return V::Undef;
            }
        }
        V::Unk
    }

    // ---- 문장 ----

    fn stmt(&mut self, c: &mut Cur, live: bool) -> Flow {
        self.steps += 1;
        let live = live && !self.exhausted();
        let start = c.i;
        let flow = self.stmt_inner(c, live);
        if c.i == start {
            c.i += 1;
        }
        flow
    }

    fn stmt_inner(&mut self, c: &mut Cur, live: bool) -> Flow {
        let keyword = match c.peek() {
            Some(Tok::Id(k)) => k.clone(),
            Some(Tok::P("{")) => return self.block(c, live),
            Some(Tok::P(";")) => {
                c.i += 1;
                return Flow::Normal;
            }
            _ => String::new(),
        };
        match keyword.as_str() {
            "var" | "let" | "const" => {
                c.i += 1;
                loop {
                    match c.peek() {
                        Some(Tok::Id(name)) => {
                            let name = name.clone();
                            c.i += 1;
                            let v = if c.eat_p("=") { self.assign_expr(c, live) } else { V::Undef };
                            if live {
                                self.declare(&name, v);
                            }
                        }
                        _ => {
                            self.assign_expr(c, false);
                        }
                    }
                    if !c.eat_p(",") {
                        break;
                    }
                }
                c.eat_p(";");
                Flow::Normal
            }
            "if" => {
                c.i += 1;
                let cond = self.paren(c, live);
                let truth = if live { cond.truthy() } else { None };
                let a = self.stmt(c, live && truth == Some(true));
                let mut flow = if truth == Some(true) { a } else { Flow::Normal };
                if c.is_id("else") {
                    c.i += 1;
                    let b = self.stmt(c, live && truth == Some(false));
                    if truth == Some(false) {
                        flow = b;
                    }
                }
                flow
            }
            "switch" => self.switch(c, live),
            "return" => {
                c.i += 1;
                let v = if c.is_p(";") || c.is_p("}") || c.done() {
                    V::Undef
                } else {
                    self.expr(c, live)
                };
                c.eat_p(";");
                if live { Flow::Ret(v) } else { Flow::Normal }
            }
            "break" | "continue" => {
                c.i += 1;
                if matches!(c.peek(), Some(Tok::Id(_))) {
                    c.i += 1;
                }
                c.eat_p(";");
                if live { Flow::Brk } else { Flow::Normal }
            }
            "throw" => {
                c.i += 1;
                self.expr(c, false);
                c.eat_p(";");
                if live { Flow::Ret(V::Unk) } else { Flow::Normal }
            }
            "function" => {
                c.i += 1;
                let name = match c.peek() {
                    Some(Tok::Id(n)) => Some(n.clone()),
                    _ => None,
                };
                if name.is_some() {
                    c.i += 1;
                }
                let f = self.function_rest(c);
                if let (true, Some(name)) = (live, name) {
                    self.declare(&name, V::Func(f));
                }
                Flow::Normal
            }
            "try" => {
                c.i += 1;
                let flow = self.block(c, live);
                if c.is_id("catch") {
                    c.i += 1;
                    if c.is_p("(") {
                        c.skip_group();
                    }
                    self.block(c, false);
                }
                if c.is_id("finally") {
                    c.i += 1;
                    self.block(c, live);
                }
                flow
            }
            // 루프는 유한 시간 종료를 보장하기 위해 본문을 평가하지 않고 건너뛴다.
            "for" | "while" => {
                c.i += 1;
                if c.is_p("(") {
                    c.skip_group();
                }
                self.stmt(c, false);
                Flow::Normal
            }
            "do" => {
                c.i += 1;
                self.stmt(c, false);
                if c.is_id("while") {
                    c.i += 1;
                    if c.is_p("(") {
                        c.skip_group();
                    }
                }
                c.eat_p(";");
                Flow::Normal
            }
            _ => {
                self.expr(c, live);
                c.eat_p(";");
                Flow::Normal
            }
        }
    }

    fn block(&mut self, c: &mut Cur, mut live: bool) -> Flow {
        if !c.eat_p("{") {
            return self.stmt(c, live);
        }
        let mut result = Flow::Normal;
        while !c.done() && !c.is_p("}") {
            let flow = self.stmt(c, live);
            if live && !matches!(flow, Flow::Normal) {
                result = flow;
                live = false;
            }
        }
        c.eat_p("}");
        result
    }

    fn paren(&mut self, c: &mut Cur, live: bool) -> V {
        if !c.eat_p("(") {
            return V::Unk;
        }
        let v = self.expr(c, live);
        c.eat_p(")");
        v
    }

    fn switch(&mut self, c: &mut Cur, live: bool) -> Flow {
        c.i += 1;
        let subject = self.paren(c, live);
        if !c.eat_p("{") {
            return Flow::Normal;
        }
        let body_start = c.i;
        let close = c.matching(body_start - 1);
        let live = live && !matches!(subject, V::Unk);
        // 첫 번째 통과: 일치하는 case부터 실행한다.
        let (flow, matched, default_at) = self.switch_pass(c, live, &subject, None);
        let flow = match (matched, default_at) {
            (false, Some(at)) if live => {
                c.i = at;
                self.switch_pass(c, true, &subject, Some(at)).0
            }
            _ => flow,
        };
        c.i = (close + 1).min(c.end);
        match flow {
            Flow::Ret(v) => Flow::Ret(v),
            _ => Flow::Normal,
        }
    }

    /// `from_default`가 주어지면 그 위치부터 바로 실행한다.
    fn switch_pass(
        &mut self,
        c: &mut Cur,
        live: bool,
        subject: &V,
        from_default: Option<usize>,
    ) -> (Flow, bool, Option<usize>) {
        let mut active = from_default.is_some();
        let mut matched = false;
        let mut finished = false;
        let mut default_at = None;
        let mut flow = Flow::Normal;
        while !c.done() && !c.is_p("}") {
            if c.is_id("case") {
                c.i += 1;
                let v = self.expr(c, live && !active && !finished);
                c.eat_p(":");
                if live && !active && !finished && strict_eq(subject, &v) == Some(true) {
                    active = true;
                    matched = true;
                }
            } else if c.is_id("default") {
                c.i += 1;
                c.eat_p(":");
                if default_at.is_none() {
                    default_at = Some(c.i);
                }
            } else {
                let run = live && active && !finished;
                match self.stmt(c, run) {
                    Flow::Brk if run => finished = true,
                    Flow::Ret(v) if run => {
                        flow = Flow::Ret(v);
                        finished = true;
                    }
                    _ => {}
                }
            }
        }
        (flow, matched || from_default.is_some(), default_at)
    }

    fn function_rest(&mut self, c: &mut Cur) -> Arc<Func> {
        let mut params = Vec::new();
        if c.is_p("(") {
            let close = c.matching(c.i);
            c.i += 1;
            while c.i < close {
                if let Some(Tok::Id(p)) = c.peek() {
                    params.push(p.clone());
                    c.i += 1;
                }
                // 기본값 등은 건너뛴다.
                while c.i < close && !c.is_p(",") {
                    c.i += 1;
                }
                c.eat_p(",");
            }
            c.i = (close + 1).min(c.end);
        }
        let (start, end) = if c.is_p("{") {
            let close = c.matching(c.i);
            let span = (c.i + 1, close);
            c.i = (close + 1).min(c.end);
            span
        } else {
            (c.i, c.i)
        };
        Arc::new(Func {
            toks: Arc::clone(&c.t),
            params,
            start,
            end,
        })
    }

    fn invoke(&mut self, f: &Func, args: Vec<V>) -> V {
        if self.depth >= DEPTH_LIMIT || self.exhausted() {
            return V::Unk;
        }
        self.depth += 1;
        let mut scope = HashMap::new();
        for (i, p) in f.params.iter().enumerate() {
            scope.insert(p.clone(), args.get(i).cloned().unwrap_or(V::Undef));
        }
        self.locals.push(scope);
        let mut c = Cur {
            t: Arc::clone(&f.toks),
            i: f.start,
            end: f.end,
        };
        let mut result = V::Undef;
        while !c.done() {
            match self.stmt(&mut c, true) {
                Flow::Ret(v) => {
                    result = v;
                    break;
                }
                Flow::Brk | Flow::Normal => {}
            }
        }
        self.locals.pop();
        self.depth -= 1;
        result
    }

    // ---- 식 ----

    fn expr(&mut self, c: &mut Cur, live: bool) -> V {
        let mut v = self.assign_expr(c, live);
        while c.eat_p(",") {
            v = self.assign_expr(c, live);
        }
        v
    }

    fn assign_expr(&mut self, c: &mut Cur, live: bool) -> V {
        self.target = None;
        let left = self.ternary(c, live);
        let target = self.target.take();
        let op = match c.peek() {
            Some(Tok::P(p @ ("=" | "+=" | "-=" | "*=" | "/="))) => *p,
            _ => return left,
        };
        c.i += 1;
        let right = self.assign_expr(c, live);
        let Some(path) = target else { return right };
        if !live {
            return right;
        }
        let value = match op {
            "=" => right,
            "+=" => add(&left, &right),
            _ => V::Unk,
        };
        if path.contains('.') {
            if let V::Str(s) = &value {
                let s = s.clone();
                self.sink(&s);
            }
        }
        self.assign(&path, value.clone());
        value
    }

    fn ternary(&mut self, c: &mut Cur, live: bool) -> V {
        let cond = self.binary(c, live, 0);
        if !c.eat_p("?") {
            return cond;
        }
        self.target = None;
        let truth = if live { cond.truthy() } else { None };
        let a = self.assign_expr(c, live && truth == Some(true));
        c.eat_p(":");
        let b = self.assign_expr(c, live && truth == Some(false));
        self.target = None;
        match truth {
            Some(true) => a,
            Some(false) => b,
            None => V::Unk,
        }
    }

    fn binary(&mut self, c: &mut Cur, live: bool, level: usize) -> V {
        const LEVELS: &[&[&str]] = &[
            &["||", "??"],
            &["&&"],
            &["|"],
            &["^"],
            &["&"],
            &["==", "!=", "===", "!=="],
            &["<", ">", "<=", ">=", "instanceof", "in"],
            &["+", "-"],
            &["*", "/", "%"],
        ];
        if level >= LEVELS.len() {
            return self.unary(c, live);
        }
        let mut left = self.binary(c, live, level + 1);
        loop {
            let op = match c.peek() {
                Some(Tok::P(p)) if LEVELS[level].contains(p) => *p,
                Some(Tok::Id(k)) if LEVELS[level].contains(&k.as_str()) => {
                    if k == "in" { "in" } else { "instanceof" }
                }
                _ => break,
            };
            c.i += 1;
            self.target = None;
            left = match op {
                "||" | "&&" | "??" => {
                    let lt = if live { left.truthy() } else { None };
                    let take_right = match op {
                        "||" => lt == Some(false),
                        "&&" => lt == Some(true),
                        _ => live && matches!(left, V::Undef),
                    };
                    let right = self.binary(c, live && take_right, level + 1);
                    match (op, lt) {
                        _ if take_right => right,
                        ("||", Some(true)) | ("&&", Some(false)) => left,
                        ("??", _) if !matches!(left, V::Unk) => left,
                        _ => V::Unk,
                    }
                }
                _ => {
                    let right = self.binary(c, live, level + 1);
                    binop(op, &left, &right)
                }
            };
            self.target = None;
        }
        left
    }

    fn unary(&mut self, c: &mut Cur, live: bool) -> V {
        match c.peek() {
            Some(Tok::P("!")) => {
                c.i += 1;
                let v = self.unary(c, live);
                self.target = None;
                v.truthy().map_or(V::Unk, |b| V::Bool(!b))
            }
            Some(Tok::P("-" | "+" | "~" | "++" | "--")) => {
                let neg = c.is_p("-");
                c.i += 1;
                let v = self.unary(c, live);
                self.target = None;
                match v {
                    V::Num(n) if neg => V::Num(-n),
                    V::Num(n) => V::Num(n),
                    _ => V::Unk,
                }
            }
            Some(Tok::Id(k)) if matches!(k.as_str(), "typeof" | "void" | "delete" | "await") => {
                let void = k == "void";
                c.i += 1;
                self.unary(c, live);
                self.target = None;
                if void { V::Undef } else { V::Unk }
            }
            Some(Tok::Id(k)) if k == "new" => {
                c.i += 1;
                self.primary(c, false);
                while c.is_p(".") {
                    c.i += 2;
                }
                if c.is_p("(") {
                    c.skip_group();
                }
                self.target = None;
                V::Obj(false)
            }
            _ => self.postfix(c, live),
        }
    }

    fn arrow_ahead(&self, c: &Cur) -> bool {
        match c.peek() {
            Some(Tok::Id(_)) => matches!(c.at(1), Some(Tok::P("=>"))),
            Some(Tok::P("(")) => {
                let close = c.matching(c.i);
                matches!(c.t.get(close + 1), Some(Tok::P("=>"))) && close + 1 < c.end
            }
            _ => false,
        }
    }

    fn skip_arrow(&mut self, c: &mut Cur) {
        if c.is_p("(") {
            c.skip_group();
        } else {
            c.i += 1;
        }
        c.eat_p("=>");
        if c.is_p("{") {
            c.skip_group();
        } else {
            self.assign_expr(c, false);
        }
    }

    fn primary(&mut self, c: &mut Cur, live: bool) -> (V, Option<String>) {
        if self.arrow_ahead(c) {
            self.skip_arrow(c);
            return (V::Unk, None);
        }
        let Some(tok) = c.peek().cloned() else {
            return (V::Unk, None);
        };
        match tok {
            Tok::Num(n) => {
                c.i += 1;
                (V::Num(n), None)
            }
            Tok::Str(s) => {
                c.i += 1;
                (V::Str(s), None)
            }
            Tok::Re => {
                c.i += 1;
                (V::Unk, None)
            }
            Tok::Tpl(parts) => {
                c.i += 1;
                let mut text = Some(String::new());
                for part in parts {
                    let piece = match part {
                        Part::Lit(s) => Some(s),
                        Part::Expr(toks) => {
                            let mut sub = Cur {
                                end: toks.len(),
                                t: toks,
                                i: 0,
                            };
                            self.expr(&mut sub, live).text()
                        }
                    };
                    text = text.zip(piece).map(|(a, b)| a + &b);
                }
                (text.map_or(V::Unk, V::Str), None)
            }
            Tok::Id(name) => {
                c.i += 1;
                match name.as_str() {
                    "true" => (V::Bool(true), None),
                    "false" => (V::Bool(false), None),
                    "null" | "undefined" => (V::Undef, None),
                    "this" => (V::Obj(false), None),
                    "function" => {
                        if matches!(c.peek(), Some(Tok::Id(_))) {
                            c.i += 1;
                        }
                        (V::Func(self.function_rest(c)), None)
                    }
                    _ => (self.lookup(&name), Some(name)),
                }
            }
            Tok::P("(") => {
                c.i += 1;
                let v = self.expr(c, live);
                c.eat_p(")");
                (v, None)
            }
            Tok::P("[") => {
                c.skip_group();
                (V::Obj(false), None)
            }
            Tok::P("{") => {
                let empty = matches!(c.at(1), Some(Tok::P("}")));
                c.skip_group();
                (V::Obj(empty), None)
            }
            Tok::P(_) => (V::Unk, None),
        }
    }

    fn postfix(&mut self, c: &mut Cur, live: bool) -> V {
        let (mut value, mut path) = self.primary(c, live);
        loop {
            match c.peek() {
                Some(Tok::P("." | "?.")) => {
                    c.i += 1;
                    if c.is_p("(") {
                        continue;
                    }
                    let Some(Tok::Id(name)) = c.peek().cloned() else { break };
                    c.i += 1;
                    match &path {
                        Some(p) => {
                            let next = format!("{p}.{name}");
                            value = self.lookup(&next);
                            path = Some(next);
                        }
                        None => {
                            value = match (&value, name.as_str()) {
                                (V::Str(s), "length") => V::Num(s.chars().count() as f64),
                                (V::Obj(true), _) => V::Undef,
                                _ => V::Unk,
                            };
                        }
                    }
                }
                Some(Tok::P("[")) => {
                    c.i += 1;
                    let key = self.expr(c, live);
                    c.eat_p("]");
                    path = match (&path, key.text()) {
                        (Some(p), Some(k)) if !matches!(key, V::Undef) => Some(format!("{p}.{k}")),
                        _ => None,
                    };
                    value = path.as_deref().map_or(V::Unk, |p| self.lookup(p));
                }
                Some(Tok::P("(")) => {
                    let close = c.matching(c.i);
                    // 닫히지 않은 호출은 브라우저에서도 구문 오류라 실행하지 않는다.
                    let closed = close < c.end;
                    c.i += 1;
                    let mut args = Vec::new();
                    while c.i < close && !c.done() {
                        args.push(self.assign_expr(c, live && closed));
                        if !c.eat_p(",") {
                            break;
                        }
                    }
                    c.i = (close + 1).min(c.end);
                    value = if live && closed {
                        self.call(path.as_deref(), &value, args)
                    } else {
                        V::Unk
                    };
                    path = None;
                }
                Some(Tok::P("++" | "--")) => {
                    c.i += 1;
                    value = V::Unk;
                    path = None;
                }
                _ => break,
            }
        }
        self.target = path;
        value
    }

    fn call(&mut self, path: Option<&str>, callee: &V, args: Vec<V>) -> V {
        self.steps += 1;
        for a in &args {
            if let V::Str(s) = a {
                let s = s.clone();
                self.sink(&s);
            }
        }
        let name = path.map(|p| p.trim_start_matches("window."));
        let arg = |i: usize| args.get(i).and_then(V::text);
        match name {
            Some("fetchTimeout") => {
                if let (Some(id), Some(url)) = (arg(0), arg(2)) {
                    self.fetch_timeout(&id, &url);
                }
                V::Unk
            }
            Some("getBoardParam") => match arg(0) {
                // URLSearchParams(qstr) + location.pathname의 게시판 경로.
                Some(q) => {
                    let mut s = q;
                    if let Some(board) = &self.board {
                        if !s.is_empty() {
                            s.push('&');
                        }
                        s.push_str(&format!("ipage={}", form_encode(board)));
                    }
                    V::Str(s)
                }
                None => V::Unk,
            },
            Some("getCookie") => V::Str(String::new()),
            Some("MOBILE.Ad.makeUrlA1BP") => arg(0).map_or(V::Unk, |u| V::Str(with_a1bp(&u))),
            Some("encodeURIComponent" | "encodeURI" | "String") => {
                arg(0).map_or(V::Unk, V::Str)
            }
            Some(
                "$" | "jQuery" | "document.getElementById" | "document.querySelector"
                | "document.createElement",
            ) => V::Obj(false),
            Some("document.write" | "document.writeln") => V::Undef,
            _ => {
                let f = match name.map(|n| self.lookup(n)) {
                    Some(V::Func(f)) => Some(f),
                    _ => match callee {
                        V::Func(f) => Some(Arc::clone(f)),
                        _ => None,
                    },
                };
                match f {
                    Some(f) => self.invoke(&f, args),
                    None => V::Unk,
                }
            }
        }
    }
}
