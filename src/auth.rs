use std::io::{self, IsTerminal, Write};
use std::path::Path;

use anyhow::{bail, Context, Result};
use serde::Deserialize;

use crate::client::{Inven, DISPATCH_URL, MLOGIN_URL};
use crate::log::SessionLog;
use crate::secrets::{self, Credentials};

const MAX_ATTEMPTS: usize = 3;

#[derive(Deserialize)]
struct LoginReply {
    #[serde(rename = "LOGIN")]
    login: Option<bool>,
    error: Option<String>,
    result: Option<String>,
}

pub fn accepted(body: &str) -> Result<bool> {
    let reply: LoginReply = serde_json::from_str(body.trim_start_matches('\u{feff}'))
        .context("로그인 서버의 응답 형식을 확인할 수 없습니다")?;
    if matches!(
        reply.result.as_deref(),
        Some(
            "docert"
                | "dormant"
                | "expire"
                | "dormant_cert"
                | "protect_ip"
                | "security_notify"
                | "force_security_notify"
        )
    ) {
        bail!("인벤 웹사이트에서 계정 확인을 마친 뒤 다시 실행하세요.");
    }
    if let Some(message) = reply.error.as_deref().filter(|s| !s.trim().is_empty()) {
        let mentions_account = ["로그인", "아이디", "비밀번호"]
            .iter()
            .any(|term| message.contains(term));
        let mismatch = ["일치하지", "잘못", "틀렸", "틀립", "올바르지"]
            .iter()
            .any(|term| message.contains(term));
        if reply.login == Some(false) || (mentions_account && mismatch) {
            return Ok(false);
        }
        bail!("로그인 서버에서 오류를 반환했습니다. 인벤 웹사이트에서 계정 상태를 확인하세요.");
    }
    reply
        .login
        .context("로그인 응답에 성공 또는 실패 여부가 없습니다")
}

fn prompt(default_id: &str) -> Result<Credentials> {
    if !io::stdin().is_terminal() {
        bail!("아이디와 비밀번호를 다시 입력하려면 터미널에서 실행하세요. 무인 실행에서는 INVEN_ID와 INVEN_PW를 함께 지정하세요.");
    }
    loop {
        if default_id.is_empty() {
            eprint!("인벤 아이디: ");
        } else {
            eprint!("인벤 아이디 [{default_id}]: ");
        }
        io::stderr().flush()?;
        let mut id = String::new();
        if io::stdin().read_line(&mut id)? == 0 {
            bail!("로그인 입력이 종료됐습니다.");
        }
        let id = if id.trim().is_empty() {
            default_id
        } else {
            id.trim()
        };
        if id.is_empty() {
            eprintln!("아이디를 입력하세요.");
            continue;
        }
        // stderr는 Windows 콘솔에서 유니코드로 출력한다. 암호 입력만 에코를 끈다.
        eprint!("인벤 비밀번호: ");
        io::stderr().flush()?;
        let password = rpassword::read_password().context("비밀번호 입력을 읽지 못했습니다")?;
        if password.is_empty() {
            eprintln!("비밀번호를 입력하세요.");
            continue;
        }
        return Ok(Credentials {
            id: id.to_owned(),
            password,
        });
    }
}

pub async fn login(dir: &Path, id: &str, log: &SessionLog) -> Result<Inven> {
    let id = id.trim();
    let first = match std::env::var("INVEN_PW") {
        Ok(password) => {
            anyhow::ensure!(
                !id.is_empty() && !password.is_empty(),
                "환경 변수로 로그인하려면 아이디와 비밀번호를 함께 지정하세요."
            );
            Some(Credentials {
                id: id.to_owned(),
                password,
            })
        }
        Err(std::env::VarError::NotPresent) => {
            match secrets::load(dir) {
                Ok(saved) => saved.filter(|pair| id.is_empty() || pair.id == id),
                Err(_) => {
                    log.notice("저장된 로그인 정보를 읽을 수 없습니다. 아이디와 비밀번호를 다시 입력하세요.");
                    None
                }
            }
        }
        Err(_) => bail!("INVEN_PW 값을 문자로 읽을 수 없습니다."),
    };
    let (inven, _entered) =
        authenticate(first, id, prompt, (MLOGIN_URL, DISPATCH_URL), log).await?;
    #[cfg(windows)]
    if let Some(pair) = _entered {
        if secrets::save(dir, &pair).is_err() {
            log.notice(
                "로그인에 성공했으나 계정 정보를 저장하지 못했습니다. 이번 세션은 계속합니다.",
            );
        }
    }
    Ok(inven)
}

pub(crate) async fn authenticate(
    mut first: Option<Credentials>,
    id: &str,
    mut read: impl FnMut(&str) -> Result<Credentials>,
    urls: (&str, &str),
    log: &SessionLog,
) -> Result<(Inven, Option<Credentials>)> {
    for attempt in 1..=MAX_ATTEMPTS {
        let entered = first.is_none();
        let pair = match first.take() {
            Some(pair) => pair,
            None => read(if attempt == 1 { id } else { "" })?,
        };
        let inven = Inven::new()?;
        if inven.login(&pair.id, &pair.password, urls).await? {
            log.notice("로그인에 성공했습니다.");
            return Ok((inven, entered.then_some(pair)));
        }
        if attempt < MAX_ATTEMPTS {
            log.notice(&format!(
                "로그인 실패 ({attempt}/{MAX_ATTEMPTS}). 아이디와 비밀번호를 다시 입력하세요."
            ));
        }
    }
    bail!("로그인에 3회 실패하여 앱을 종료합니다.")
}
