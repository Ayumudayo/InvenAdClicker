//! 인벤 광고 스크립트(zicf.js, zicfm.js)가 만드는 광고 슬롯을 찾는 작은 평가기.
//!
//! 브라우저는 페이지의 `INVEN.Ad.showTop800('webzine', ...)` 같은 인라인 호출을 실행해
//! `document.write`로 광고 iframe을 만든다. 여기서는 JS 엔진 없이 그 함수들이 쓰는 문법의
//! 일부(문자열 연결, 변수, if/switch, 함수 호출)만 해석해 같은 광고 프레임 주소를 계산한다.
//! 해석하지 못하는 값은 `Unk`로 남기고, 그런 값이 들어간 주소나 조건 분기는 버린다.

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use reqwest::Url;

mod eval;
mod lexer;
mod value;

pub(crate) use lexer::{lex, Tok};

/// 광고 스크립트를 실행한 뒤의 전역 상태.
#[derive(Clone, Default)]
pub(crate) struct Env {
    globals: HashMap<String, value::V>,
}

impl Env {
    /// 외부 광고 스크립트의 최상위 문장을 실행해 `INVEN.Ad.*` 함수들을 등록한다.
    pub fn load(&mut self, toks: &Arc<Vec<Tok>>) {
        eval::load_env(self, toks);
    }
}

/// 페이지 인라인 스크립트를 실행해 광고 프레임 주소(상대 주소 포함)를 반환한다.
pub(crate) fn page_slots(
    env: &Env,
    inline: &[String],
    ids: HashSet<String>,
    page: &Url,
) -> Vec<String> {
    eval::eval_page_slots(env, inline, ids, page)
}
