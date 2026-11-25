use crate::auth::{accepted, authenticate};
use crate::client::*;
use crate::config::{self, Config, PageTarget};
use crate::log::SessionLog;
use crate::pipeline::*;
use crate::placements;
use crate::progress::{Event, Outcome, PageState, ProgressState, Totals};
use crate::secrets::{self, Credentials};
use std::io::{BufRead, BufReader, Read, Write};
use std::net::TcpListener;
use std::path::Path;
use std::sync::Arc;
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};
use tempfile::tempdir;

struct Reply {
    status: u16,
    content_type: &'static str,
    body: Vec<u8>,
}

impl Reply {
    fn json(status: u16, text: &str) -> Self {
        Self {
            status,
            content_type: "application/json",
            body: text.as_bytes().to_vec(),
        }
    }
}

// 각 요청을 끝까지 받아 실제로 제출된 경로와 폼을 검사한다.
fn serve(replies: Vec<Reply>) -> (String, JoinHandle<Vec<(String, String)>>) {
    let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
    listener.set_nonblocking(true).unwrap();
    let addr = listener.local_addr().unwrap();
    let handle = thread::spawn(move || {
        let mut requests = Vec::new();
        for reply in replies {
            let deadline = Instant::now() + Duration::from_secs(4);
            let mut socket = loop {
                match listener.accept() {
                    Ok((s, _)) => break s,
                    Err(e)
                        if e.kind() == std::io::ErrorKind::WouldBlock
                            && Instant::now() < deadline =>
                    {
                        thread::sleep(Duration::from_millis(2))
                    }
                    Err(e) => panic!("missing expected request: {e}"),
                }
            };
            socket.set_nonblocking(false).unwrap();
            socket
                .set_read_timeout(Some(Duration::from_secs(3)))
                .unwrap();
            let mut reader = BufReader::new(&socket);
            let mut request = String::new();
            reader.read_line(&mut request).unwrap();
            let mut length = 0;
            loop {
                let mut line = String::new();
                assert_ne!(reader.read_line(&mut line).unwrap(), 0);
                if line == "\r\n" {
                    break;
                }
                if let Some((key, value)) = line.split_once(':') {
                    if key.eq_ignore_ascii_case("content-length") {
                        length = value.trim().parse().unwrap();
                    }
                }
            }
            let mut body = vec![0; length];
            reader.read_exact(&mut body).unwrap();
            requests.push((request, String::from_utf8(body).unwrap()));
            write!(socket, "HTTP/1.1 {} Test\r\nContent-Type: {}\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
                reply.status, reply.content_type, reply.body.len()).unwrap();
            socket.write_all(&reply.body).unwrap();
        }
        requests
    });
    (format!("http://{addr}"), handle)
}

fn login_replies(outcomes: &[(u16, &str)]) -> Vec<Reply> {
    outcomes
        .iter()
        .flat_map(|(status, json)| {
            [
                Reply {
                    status: 200,
                    content_type: "text/html",
                    body: br#"<input value="token" id="stoken">"#.to_vec(),
                },
                Reply::json(*status, json),
            ]
        })
        .collect()
}

fn pair(id: &str, password: &str) -> Credentials {
    Credentials {
        id: id.into(),
        password: password.into(),
    }
}

fn session_text(dir: &Path) -> String {
    let entry = std::fs::read_dir(dir).unwrap().next().unwrap().unwrap();
    std::fs::read_to_string(entry.path()).unwrap()
}

#[tokio::test]
async fn rejected_then_valid_prompts_for_both_and_returns_only_verified_pair() {
    let tmp = tempdir().unwrap();
    let log = SessionLog::new(tmp.path(), None, false).unwrap();
    let (url, server) = serve(login_replies(&[
        (200, r#"{"error":"로그인 정보가 일치하지 않습니다."}"#),
        (200, r#"{ "LOGIN" : true }"#),
    ]));
    let mut prompts = 0;
    let read = |hint: &str| {
        assert!(hint.is_empty());
        prompts += 1;
        Ok(pair("second", " pass "))
    };
    let (_, entered) = authenticate(
        Some(pair("first", "bad")),
        "first",
        read,
        (&format!("{url}/form"), &format!("{url}/submit")),
        &log,
    )
    .await
    .unwrap();
    let submitted: Vec<_> = server
        .join()
        .unwrap()
        .into_iter()
        .filter(|(r, _)| r.starts_with("POST /submit "))
        .collect();
    assert_eq!(prompts, 1);
    assert_eq!(submitted.len(), 2);
    assert!(submitted[0].1.contains("user_id=first"));
    assert!(submitted[1].1.contains("user_id=second"));
    assert!(submitted[1]
        .1
        .contains(&format!("password={}", hex::encode(b" pass "))));
    let verified = entered.unwrap();
    assert_eq!(
        (verified.id.as_str(), verified.password.as_str()),
        ("second", " pass ")
    );
    let text = session_text(tmp.path());
    assert!(text.contains("1/3") && text.contains("로그인에 성공"));
    assert!(!text.contains("first") && !text.contains("second") && !text.contains(" pass "));
}

#[tokio::test]
async fn stops_on_third_rejection() {
    let tmp = tempdir().unwrap();
    let log = SessionLog::new(tmp.path(), None, false).unwrap();
    let (url, server) = serve(login_replies(&[(200, r#"{"LOGIN":false}"#); 3]));
    let mut prompts = 0;
    let read = |_: &str| {
        prompts += 1;
        Ok(pair("fixture", "invalid"))
    };
    let result = authenticate(None, "", read, (&url, &url), &log).await;
    assert!(result.err().unwrap().to_string().contains("3회 실패"));
    assert_eq!(server.join().unwrap().len(), 6);
    assert_eq!(prompts, 3);
    assert!(!tmp.path().join("credentials.dat").exists());
}

#[tokio::test]
async fn server_outage_or_unknown_response_does_not_reprompt() {
    for (status, body) in [
        (503, "offline"),
        (429, "limit"),
        (200, "<html>maintenance</html>"),
        (200, "{}"),
        (200, r#"{"error":"점검 중입니다"}"#),
    ] {
        let tmp = tempdir().unwrap();
        let log = SessionLog::new(tmp.path(), None, false).unwrap();
        let (url, server) = serve(login_replies(&[(status, body)]));
        let result = authenticate(
            Some(pair("fixture", "dummy")),
            "",
            |_| panic!("unexpected retry"),
            (&url, &url),
            &log,
        )
        .await;
        assert!(result.is_err());
        assert!(!session_text(tmp.path()).contains("1/3"));
        assert_eq!(server.join().unwrap().len(), 2);
    }
    assert!(accepted(r#"{"result":"dormant","LOGIN":false,"error":"휴면"}"#).is_err());
}

#[tokio::test]
async fn legacy_korean_http_body_decodes_using_response_charset() {
    let mut body = b"<span>".to_vec();
    body.extend(b"\xc0\xcc\xb4\xcf</span><span class=\"point-red\">123</span><span>\xba\xa3\xb4\xcf</span><span class=\"point-red\">4</span>");
    let (url, server) = serve(vec![Reply {
        status: 200,
        content_type: "text/html; charset=euc-kr",
        body,
    }]);
    let text = Inven::new()
        .unwrap()
        .http
        .get(url)
        .send()
        .await
        .unwrap()
        .text()
        .await
        .unwrap();
    let pts = Points::parse(&text).unwrap();
    assert_eq!((pts.ini, pts.beni), (123, 4));
    server.join().unwrap();
}

#[test]
fn config_uses_executable_directory_accepts_bom_and_rejects_invalid_json() {
    let tmp = tempdir().unwrap();
    let path = tmp.path().join("config.json");
    std::fs::write(&path, "\u{feff}{\"log_dir\":\"기록\"}").unwrap();
    assert_eq!(
        Config::load(tmp.path()).unwrap().log_dir,
        tmp.path().join("기록")
    );
    std::fs::write(&path, b"{invalid").unwrap();
    assert!(Config::load(tmp.path()).is_err());
    assert!(Config::default().id.is_empty());
}

#[test]
fn logs_are_distinct_and_readable_in_windows_powershell() {
    let tmp = tempdir().unwrap();
    for _ in 0..2 {
        let log = SessionLog::new(tmp.path(), None, false).unwrap();
        log.record("실행 중단");
    }
    let paths: Vec<_> = std::fs::read_dir(tmp.path()).unwrap().collect();
    assert_eq!(paths.len(), 2);
    for entry in paths {
        assert!(std::fs::read_to_string(entry.unwrap().path())
            .unwrap()
            .starts_with("\u{feff}["));
    }
}

#[tokio::test]
async fn zero_jitter_and_collection_errors_survive_worker_completion() {
    jitter(0, 0).await;
    let tmp = tempdir().unwrap();
    let pages = vec!["invalid URL".into()];
    let state = Arc::new(ProgressState::new(&pages, 2));
    let ctx = Arc::new(Ctx {
        inven: Inven::new().unwrap(),
        state: Arc::clone(&state),
        log: Arc::new(SessionLog::new(tmp.path(), None, false).unwrap()),
        scripts: placements::ScriptCache::new(placements::public_client().unwrap()),
        collect_only: false,
        collected: Default::default(),
        cfg: Config {
            pages: pages.into_iter().map(PageTarget::Standard).collect(),
            passes: 2,
            page_delay_ms: 0,
            page_jitter_ms: 0,
            ..Default::default()
        },
    });
    run_workers(&ctx).await.unwrap();
    let (_, row) = state.snapshot().pop().unwrap();
    assert_eq!(
        (row.errors, row.state(), row.collectors),
        (2, PageState::Failed, 0)
    );
}

#[cfg(windows)]
#[test]
fn windows_store_ignores_legacy_passwords_and_atomically_replaces_verified_pair() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("credentials.dat");
    std::fs::write(&path, b"old password only").unwrap();
    assert!(secrets::load(dir.path()).unwrap().is_none());
    for (id, pw) in [
        ("account-a", "example-password-A"),
        ("account-b", "example-password-B"),
    ] {
        secrets::save(dir.path(), &pair(id, pw)).unwrap();
        let data = std::fs::read(&path).unwrap();
        assert!(!data.windows(pw.len()).any(|w| w == pw.as_bytes()));
        let saved = secrets::load(dir.path()).unwrap().unwrap();
        assert_eq!((saved.id.as_str(), saved.password.as_str()), (id, pw));
    }
}

#[tokio::test]
async fn silent_peer_reaches_the_request_deadline() {
    let socket = TcpListener::bind(("127.0.0.1", 0)).unwrap();
    let addr = socket.local_addr().unwrap();
    // Listening without responding keeps the request open until its deadline.
    let ad = AdLink {
        url: format!("http://{addr}/"),
        page: format!("http://{addr}/"),
    };
    let client = Inven::new().unwrap();
    let response = tokio::time::timeout(Duration::from_secs(20), client.click(&ad))
        .await
        .expect("configured request deadline did not fire");
    assert!(response
        .unwrap_err()
        .downcast_ref::<reqwest::Error>()
        .unwrap()
        .is_timeout());
}

#[tokio::test]
async fn results_distinguish_http_200_from_reported_failures() {
    let replies = vec![
        Reply::json(200, "{}"),
        Reply::json(200, r#"{"result":"error"}"#),
        Reply::json(200, r#"{"error":false}"#),
        Reply::json(200, r#"{"success":false}"#),
        Reply::json(200, "<html>ordinary response</html>"),
        Reply::json(302, ""),
        Reply::json(403, ""),
        Reply::json(503, ""),
    ];
    let (url, server) = serve(replies);
    let client = Inven::new().unwrap();
    let ad = AdLink {
        url: url.clone(),
        page: url,
    };
    for expected in [
        Outcome::Unconfirmed,
        Outcome::Failed,
        Outcome::Unconfirmed,
        Outcome::Failed,
        Outcome::Unconfirmed,
        Outcome::Redirected,
        Outcome::Failed,
        Outcome::Failed,
    ] {
        assert_eq!(client.click(&ad).await.unwrap().outcome, expected);
    }
    assert_eq!(server.join().unwrap().len(), 8);
}

#[test]
fn simultaneous_workers_keep_distinct_statuses_and_counts() {
    let url = "local";
    let state = ProgressState::new(&[url.into(), url.into()], 1);
    assert_eq!(
        state.update(
            url,
            Event::StartCollection {
                inspect_only: false
            }
        ),
        1
    );
    state.update(url, Event::LinkQueued);
    state.update(url, Event::StartRequest);
    let r = &state.snapshot()[0].1;
    assert_eq!((r.collectors, r.requests, r.state()), (1, 1, PageState::Collecting));
    assert_eq!(
        state.update(
            url,
            Event::StartCollection {
                inspect_only: false
            }
        ),
        2
    );
    state.update(url, Event::EndRequest(Outcome::Unconfirmed));
    state.update(
        url,
        Event::EndCollection { errors: 0 },
    );
    assert_eq!(state.snapshot()[0].1.state(), PageState::Collecting);
    state.update(
        url,
        Event::EndCollection { errors: 0 },
    );
    let rows = state.snapshot();
    let row = &rows[0].1;
    assert_eq!(
        (rows.len(), row.finished_passes, row.state()),
        (1, 2, PageState::PartialFailure)
    );
    let totals = Totals::from_rows(&rows);
    assert_eq!(
        (totals.clicked, totals.unconfirmed, totals.errors + totals.failed),
        (1, 1, 0)
    );
}

#[test]
fn missing_config_is_created_once_and_existing_values_survive() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("config.json");
    std::thread::scope(|scope| {
        for _ in 0..4 {
            scope.spawn(|| Config::load(dir.path()).unwrap());
        }
    });
    assert_eq!(
        std::fs::read_to_string(&path).unwrap(),
        include_str!("../config.json.example")
    );
    let edited = r#"{"log_dir":"나의 기록"}"#;
    std::fs::write(&path, edited).unwrap();
    assert_eq!(
        Config::load(dir.path()).unwrap().log_dir,
        dir.path().join("나의 기록")
    );
    assert_eq!(std::fs::read_to_string(path).unwrap(), edited);
}

#[test]
fn html_placements_follow_document_structure_and_final_base_url() {
    let page = reqwest::Url::parse("https://www.inven.co.kr/board/fixture").unwrap();
    let html = r#"
        <base href="https://zicf.inven.co.kr/RealMedia/ads/">
        <!-- <iframe src="adstream_sx.ads/comment"></iframe> -->
        <template><iframe src="adstream_sx.ads/template"></iframe></template>
        <noscript><iframe src="adstream_sx.ads/fallback"></iframe></noscript>
        <script>const all = '<iframe src="adstream_sx.ads/catalog"></iframe>';</script>
        <textarea><iframe src="adstream_sx.ads/example"></iframe></textarea>
        <iframe srcdoc="" src="adstream_sx.ads/ignored"></iframe>
        <iframe src="https://example.invalid/RealMedia/ads/adstream_sx.ads/ignored"></iframe>
        <iframe SRC="adstream_sx.ads/page@Named?lang=ko&amp;view=1"></iframe>"#;
    let found = placements::parse(&page, html, &[]);
    assert_eq!(found.frames.len(), 1);
    assert_eq!(
        found.frames[0].as_str(),
        "https://zicf.inven.co.kr/RealMedia/ads/adstream_sx.ads/page@Named?lang=ko&view=1"
    );
    assert_eq!(found.scripted, 0);
}

#[test]
fn inline_initialization_follows_only_reachable_targets() {
    let page = reqwest::Url::parse("https://m.inven.co.kr/fixture/").unwrap();
    let markup = r#"
        <div id="tail"><script>fetchTimeout('tail', 5000, '//zicf.inven.co.kr/RealMedia/ads/adstream_sx.ads/page@Bottom', '', 300, 250);</script></div>
        <div id="unused"><script>if (false) fetchTimeout('unused', 5000, '//zicf.inven.co.kr/RealMedia/ads/adstream_sx.ads/page@Dead', '', 300, 250);</script></div>
        <div id="other"><script>fetchTimeout('missing', 5000, '//zicf.inven.co.kr/RealMedia/ads/adstream_sx.ads/page@Missing', '', 300, 250);</script></div>
        <div id="data"><script type="application/json">fetchTimeout('data', 5000, '//zicf.inven.co.kr/RealMedia/ads/adstream_sx.ads/page@Data', '', 300, 250);</script></div>"#;
    let found = placements::parse(&page, markup, &["var MOBILE = window.MOBILE || {};"]);
    let urls: Vec<_> = found.frames.iter().map(|u| u.as_str()).collect();
    assert_eq!(
        urls,
        ["https://zicf.inven.co.kr/RealMedia/ads/adstream_sx.ads/page@Bottom?a1bp=0"]
    );
    assert_eq!(found.scripted, 1);
}

// zicf.js에서 실제로 쓰이는 형태를 줄여 옮긴 것.
const ZICF_FIXTURE: &str = r#"
    var INVEN = window.INVEN || {};
    INVEN.Ad = window.INVEN.Ad || {};
    INVEN.Ad.isMaintenance = false;
    INVEN.Ad.showTop800 = function(site, ibt) {
        if (INVEN.Ad.isMaintenance) { document.write("<img src='https://placehold.co/728x90'>");return false; }
        document.write('<div class="comAdTrafficTrackerWrapper">');
        document.write ("<iframe class='comAdTrafficTracker' src='https://zicf.inven.co.kr/RealMedia/ads/adstream_sx.ads/inven/"+site+"@x72?ipos=comAdTopBanner&ibt="+ibt+"'></iframe>");
        document.write ("<iframe id='comAdTopBanner' src='https://zicf.inven.co.kr/RealMedia/ads/adstream_sx.ads/inven/"+site+"@x60?ipos=comAdTopBanner&ibt="+ibt+"'></iframe>");
    }
    INVEN.Ad.showThumbNail = function(site) {
        return; // 중지
        //document.write ("<iframe src='https://zicf.inven.co.kr/RealMedia/ads/adstream_sx.ads/inven/"+site+"@x10'></iframe>");
    }
    INVEN.Ad.showDniAd = function(site, pos) {
        switch (pos) {
            case "top":
                document.write ("<iframe src='https://zicf.inven.co.kr/RealMedia/ads/adstream_sx.ads/inven/"+site+"@x20'></iframe>");
                break;
            case "bot":
                document.write ("<iframe src='https://zicf.inven.co.kr/RealMedia/ads/adstream_sx.ads/inven/"+site+"@x21'></iframe>");
                break;
            default:
                return false;
        }
    }
    INVEN.Ad.bbsSubject = function(site, boardnum, id) {
        var pagecode = '';
        if (boardnum == '5313' || boardnum == '2417') {
            pagecode = boardnum+'bbs';
        } else {
            pagecode = site;
        }
        if (pagecode == 'sytest') {
            document.write("<iframe src='https://zicf.inven.co.kr/RealMedia/ads/adstream_sx.ads/www.sytest.com/test@x08'></iframe>");
        } else if (id) {
            fetchTimeout(id, 5000, `https://zicf.inven.co.kr/RealMedia/ads/adstream_sx.ads/inven/${pagecode}@x08?ipos=comAdBBSSubject_board`, '', 300, 250);
        }
    }
    INVEN.Ad.showTi = function(site, selector) {
        if (getCookie("floating_ad") != "close") {
            document.write('<iframe id="comAdTi" src="https://zicf.inven.co.kr/RealMedia/ads/adstream_sx.ads/inven/'+site+'@x19?ipos=comAdTi"></iframe>');
        }
    }
    function getBoardParam(qstr) {
        const search = new URLSearchParams(qstr);
        return search;
    }
    MOBILE.Ad.showM320Float = function(site) {
        if (MOBILE.Ad.changeSection("320x50float", site)=='none') { return false;}
        let url = MOBILE.Ad.makeUrlA1BP(`//zicf.inven.co.kr/RealMedia/ads/adstream_sx.ads/minven/${site}@Bottom?${getBoardParam('ibt=ns1459')}`);
        document.write (`<iframe id="comAdM320Float" src="${url}"></iframe>`);
    }
    MOBILE.Ad.changeSection = function(posNum, site) {
        // if (site == 'lol') return 'none';
        return site;
    }
    $(document).ready(function() { INVEN.Ad.showTop800('never', 'x'); });
"#;

#[test]
fn page_slot_calls_are_expanded_through_the_ad_script() {
    let page = reqwest::Url::parse("https://m.inven.co.kr/board/ff14/4336/99").unwrap();
    let markup = r#"
        <script>INVEN.Ad.showTop800('webzine','ad-header');</script>
        <script>INVEN.Ad.showThumbNail('webzine'); INVEN.Ad.showDniAd('webzine', 'bot');</script>
        <div id="subject"><script>INVEN.Ad.bbsSubject('ff14', '2417', 'subject');</script></div>
        <script>INVEN.Ad.bbsSubject('ff14', '9999', 'absent');</script>
        <script>if (unknownFlag) { INVEN.Ad.showTi('guess'); } INVEN.Ad.showTi('ff14');</script>
        <script>MOBILE.Ad.showM320Float('ff14');</script>
        <template><script>INVEN.Ad.showTop800('hidden','x');</script></template>"#;
    let zicfm = "var MOBILE = window.MOBILE || {}; MOBILE.Ad = window.MOBILE.Ad || {};";
    let found = placements::parse(&page, markup, &[zicfm, ZICF_FIXTURE]);
    let urls: Vec<_> = found.frames.iter().map(|u| u.as_str()).collect();
    let base = "https://zicf.inven.co.kr/RealMedia/ads/adstream_sx.ads";
    assert_eq!(
        urls,
        [
            format!("{base}/inven/webzine@x72?ipos=comAdTopBanner&ibt=ad-header"),
            format!("{base}/inven/webzine@x60?ipos=comAdTopBanner&ibt=ad-header"),
            format!("{base}/inven/webzine@x21"),
            format!("{base}/inven/2417bbs@x08?ipos=comAdBBSSubject_board&a1bp=0"),
            format!("{base}/inven/ff14@x19?ipos=comAdTi"),
            format!("{base}/minven/ff14@Bottom?ibt=ns1459&ipage=ff14%2F4336%2F99&a1bp=0"),
        ]
    );
    assert_eq!(found.scripted, 6);
}

#[test]
fn malformed_or_hostile_scripts_terminate_without_slots() {
    let page = reqwest::Url::parse("https://www.inven.co.kr/").unwrap();
    let markup = r#"
        <script>INVEN.Ad.loop('a');</script>
        <script>var x = /["'`]/g; INVEN.Ad.showTop800(</script>
        <script>while (true) {} for (;;) { INVEN.Ad.showTop800('loop','x'); }</script>"#;
    let script = r#"
        INVEN = {}; INVEN.Ad = {};
        INVEN.Ad.loop = function(site) { INVEN.Ad.loop(site); };
        INVEN.Ad.showTop800 = function(site, ibt) {
            document.write("<iframe src='https://zicf.inven.co.kr/RealMedia/ads/adstream_sx.ads/inven/"+site+"@x60'></iframe>");
        };
        INVEN.Ad.broken = function( { ] ) "#;
    let found = placements::parse(&page, markup, &[script]);
    assert!(found.frames.is_empty());
}

#[tokio::test]
async fn pages_without_ad_scripts_fetch_nothing_else() {
    let markup = r#"<div id="tail"><script>fetchTimeout('tail', 5000, '//zicf.inven.co.kr/RealMedia/ads/adstream_sx.ads/page@Bottom', '', 300, 250);</script></div>"#;
    let (url, server) = serve(vec![Reply {
        status: 200,
        content_type: "text/html",
        body: markup.as_bytes().to_vec(),
    }]);
    let scripts = placements::ScriptCache::new(placements::public_client().unwrap());
    let result = Inven::new()
        .unwrap()
        .collect_page(&scripts, &url)
        .await
        .unwrap();
    assert_eq!(
        (result.placements.frames.len(), result.placements.scripted),
        (0, 0)
    );
    assert!(result.links.is_empty() && result.errors.is_empty());
    assert_eq!(server.join().unwrap().len(), 1);
}

#[test]
fn observed_status_uses_latest_successfully_examined_pass() {
    let pages = vec!["fixture".to_string()];
    let state = ProgressState::new(&pages, 2);
    state.update(
        "fixture",
        Event::StartCollection {
            inspect_only: false,
        },
    );
    state.update(
        "fixture",
        Event::StartCollection {
            inspect_only: false,
        },
    );
    state.update(
        "fixture",
        Event::Observed {
            pass: 2,
            frames: 0,
        },
    );
    state.update(
        "fixture",
        Event::EndCollection { errors: 0 },
    );
    state.update(
        "fixture",
        Event::Observed {
            pass: 1,
            frames: 1,
        },
    );
    state.update(
        "fixture",
        Event::EndCollection { errors: 0 },
    );
    let rows = state.snapshot();
    let row = &rows[0].1;
    assert_eq!(
        (row.frames, row.state()),
        (0, PageState::NoAds)
    );
}

#[test]
fn legacy_slots_are_ignored_when_reading_settings() {
    let dir = tempdir().unwrap();
    std::fs::write(
        dir.path().join("config.json"),
        r#"{"slots":"obsolete","log_dir":"existing"}"#,
    )
    .unwrap();
    assert_eq!(
        Config::load(dir.path()).unwrap().log_dir,
        dir.path().join("existing")
    );
    let template: serde_json::Value =
        serde_json::from_str(include_str!("../config.json.example")).unwrap();
    assert!(template.get("slots").is_none());
}

#[tokio::test]
async fn inspection_entry_finishes_in_normal_pipeline_with_no_frame_or_click_request() {
    let page =
        r#"<iframe src="https://zicf.inven.co.kr/RealMedia/ads/adstream_sx.ads/fixture"></iframe>"#;
    let (url, server) = serve(vec![Reply {
        status: 200,
        content_type: "text/html",
        body: page.as_bytes().to_vec(),
    }]);
    let dir = tempdir().unwrap();
    std::fs::write(
        dir.path().join("config.json"),
        serde_json::json!({
            "pages":[{"inspect_only":url}], "passes":1, "concurrency":1, "click_concurrency":1,
            "page_delay_ms":0, "page_jitter_ms":0
        })
        .to_string(),
    )
    .unwrap();
    let cfg = Config::load(dir.path()).unwrap();
    assert_eq!(cfg.page_urls(), [url]);
    let state = Arc::new(ProgressState::new(&cfg.page_urls(), 1));
    // These clients cannot reach an external server even if the branch regresses.
    let blocked = reqwest::Client::builder()
        .proxy(reqwest::Proxy::all("http://127.0.0.1:1").unwrap())
        .build()
        .unwrap();
    let ctx = Arc::new(Ctx {
        inven: Inven::with_clients(blocked.clone(), blocked),
        state: state.clone(),
        log: Arc::new(SessionLog::new(&dir.path().join("logs"), None, false).unwrap()),
        cfg,
        scripts: placements::ScriptCache::new(placements::public_client().unwrap()),
        collect_only: false,
        collected: Default::default(),
    });
    run_workers(&ctx).await.unwrap();
    let rows = state.snapshot();
    let row = &rows[0].1;
    assert_eq!(
        (row.state(), row.frames, row.clicked, row.errors),
        (PageState::Inspected, 1, 0, 0)
    );
    assert_eq!(server.join().unwrap().len(), 1);
}

#[test]
fn pages_without_ads_are_highlighted_but_not_failures() {
    let urls = vec!["empty".to_owned()];
    let state = ProgressState::new(&urls, 1);
    state.update("empty", Event::StartCollection { inspect_only: false });
    state.update("empty", Event::EndCollection { errors: 0 });
    let rows = state.snapshot();
    let row = &rows[0].1;
    assert_eq!((row.state(), row.no_ads(), row.has_problem()), (PageState::NoAds, true, false));
    let totals = Totals::from_rows(&rows);
    assert_eq!((totals.ads, totals.clicked, totals.failed, totals.errors), (0, 0, 0, 0));
}
// 광고 서버는 소문자 헤더 이름으로 온 클릭을 HTTP 200 {"error":"1"}로 거절했다.
#[tokio::test]
async fn click_requests_use_title_case_header_names() {
    let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
    let addr = listener.local_addr().unwrap();
    let handle = thread::spawn(move || {
        let (socket, _) = listener.accept().unwrap();
        let mut reader = BufReader::new(&socket);
        let mut names = Vec::new();
        loop {
            let mut line = String::new();
            reader.read_line(&mut line).unwrap();
            if line == "\r\n" {
                break;
            }
            if let Some((name, _)) = line.split_once(':') {
                names.push(name.to_owned());
            }
        }
        let mut s = &socket;
        write!(s, "HTTP/1.1 302 Found\r\nLocation: http://example.invalid/\r\nContent-Length: 0\r\n\r\n").unwrap();
        names
    });
    let ad = AdLink {
        url: format!("http://{addr}/RealMedia/ads/click_lx.ads/x?a1_ve=cl"),
        page: "https://www.inven.co.kr/".into(),
    };
    let reply = Inven::new().unwrap().click(&ad).await.unwrap();
    assert_eq!(reply.outcome, Outcome::Redirected);
    let names = handle.join().unwrap();
    for expected in ["Host", "User-Agent", "Referer", "Accept"] {
        assert!(names.iter().any(|n| n == expected), "{expected} missing in {names:?}");
    }
    assert!(names.iter().all(|n| n.chars().next().is_some_and(char::is_uppercase)));
}

type CapturedRequest = (String, Vec<String>, String);

fn serve_raw(
    build: impl FnOnce(&str) -> Vec<(u16, String, String)> + Send + 'static,
) -> (String, JoinHandle<Vec<CapturedRequest>>) {
    let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
    listener.set_nonblocking(true).unwrap();
    let base = format!("http://{}", listener.local_addr().unwrap());
    let responses = build(&base);
    let handle = thread::spawn(move || {
        let mut received = Vec::new();
        for (status, extra_headers, reply_body) in responses {
            let deadline = Instant::now() + Duration::from_secs(4);
            let mut socket = loop {
                match listener.accept() {
                    Ok((s, _)) => break s,
                    Err(e)
                        if e.kind() == std::io::ErrorKind::WouldBlock
                            && Instant::now() < deadline =>
                    {
                        thread::sleep(Duration::from_millis(2))
                    }
                    Err(e) => panic!("missing expected request: {e}"),
                }
            };
            socket.set_nonblocking(false).unwrap();
            socket
                .set_read_timeout(Some(Duration::from_secs(3)))
                .unwrap();
            let mut reader = BufReader::new(&socket);
            let mut req_line = String::new();
            reader.read_line(&mut req_line).unwrap();
            let mut headers = Vec::new();
            let mut length = 0;
            loop {
                let mut line = String::new();
                assert_ne!(reader.read_line(&mut line).unwrap(), 0);
                if line == "\r\n" {
                    break;
                }
                if let Some((k, v)) = line.split_once(':') {
                    if k.eq_ignore_ascii_case("content-length") {
                        length = v.trim().parse().unwrap();
                    }
                }
                headers.push(line.trim_end().to_owned());
            }
            let mut body = vec![0; length];
            reader.read_exact(&mut body).unwrap();
            received.push((req_line, headers, String::from_utf8(body).unwrap()));
            write!(
                socket,
                "HTTP/1.1 {status} Test\r\n{extra_headers}Content-Length: {}\r\nConnection: close\r\n\r\n{reply_body}",
                reply_body.len()
            )
            .unwrap();
        }
        received
    });
    (base, handle)
}

#[tokio::test]
async fn inventory_html_parses_xp_and_points_and_falls_back_to_imart() {
    let inv_html = r#"
        <dt class="exp">경험치</dt><dd class="exp">
            <span class="exp-value">1,280,742</span> <span class="exp-per">(89%)</span>
        </dd>
        <dt class="pt">포인트</dt><dd class="pt">
            <p><span class="sub-title">이니</span> <span class="inni-value">2,462,827</span></p>
            <p><span class="sub-title">베니</span> <span class="veni-value">58,062</span></p>
        </dd>"#;
    assert_eq!(
        Points::parse(inv_html).unwrap(),
        Points {
            ini: 2_462_827,
            beni: 58_062,
            exp: Some(1_280_742),
        }
    );
    let (url, server) = serve(vec![
        Reply {
            status: 200,
            content_type: "text/html",
            body: b"<script>login('');</script>".to_vec(),
        },
        Reply {
            status: 200,
            content_type: "text/html",
            body: r#"<span>이니</span><span class="point-red">50</span><span>베니</span><span class="point-red">7</span>"#.as_bytes().to_vec(),
        },
    ]);
    let pts = Inven::new()
        .unwrap()
        .points_from(&format!("{url}/inv"), &format!("{url}/imart"))
        .await
        .unwrap();
    assert_eq!(
        pts,
        Points {
            ini: 50,
            beni: 7,
            exp: None
        }
    );
    assert_eq!(server.join().unwrap().len(), 2);
}

#[tokio::test]
async fn complete_login_posts_skill_point_parses_rewards_and_follows_surl() {
    let (base, server) = serve_raw(|base| {
        let json_body = format!(
            r#"{{"result":"success","login":"<span style='color:blue'>1,000 이니</span>","skill":"50 경험치","url":"{base}/after"}}"#
        );
        vec![
            (200, "Content-Type: application/json\r\n".into(), json_body),
            (200, "Set-Cookie: VIEW_TYPE=mobile; path=/\r\n".into(), "ok".into()),
        ]
    });

    let inven = Inven::new().unwrap();
    let notes = inven
        .complete_login(&format!("{base}/chk/skill/point"), &format!("{base}/fallback"))
        .await
        .unwrap();
    assert_eq!(notes, ["불로소득 1,000 이니", "인증보너스 50 경험치"]);
    let reqs = server.join().unwrap();
    assert!(reqs[0].0.starts_with("POST /chk/skill/point "));
    assert!(reqs[0].2.contains("surl="));
    assert!(reqs[1].0.starts_with("GET /after "));

    let empty = parse_login_reward(
        r#"{"result":"success","login":false,"skill":"","sevent":0,"doubleEventMsg":null,"url":""}"#,
    )
    .unwrap();
    assert!(empty.notes.is_empty());
}

#[tokio::test]
async fn warm_oax_sets_cookie_shared_across_http_and_click_clients() {
    let (url, server) = serve_raw(|_| {
        vec![
            (200, "Set-Cookie: OAX=TestOax123456789; path=/\r\n".into(), "ok".into()),
            (200, String::new(), "ad".into()),
            (302, "Location: http://example.invalid/\r\n".into(), String::new()),
        ]
    });
    let inven = Inven::new().unwrap();
    inven.warm_oax(&format!("{url}/oax")).await.unwrap();
    inven.http.get(format!("{url}/ad")).send().await.unwrap();
    let reply = inven
        .click(&AdLink {
            url: format!("{url}/click"),
            page: "https://www.inven.co.kr/".into(),
        })
        .await
        .unwrap();
    assert_eq!(reply.outcome, Outcome::Redirected);

    let reqs = server.join().unwrap();
    assert!(reqs[0].0.starts_with("GET /oax "));
    for idx in [1, 2] {
        let cookie = reqs[idx]
            .1
            .iter()
            .find(|h| h.to_ascii_lowercase().starts_with("cookie:"))
            .expect("expected Cookie header");
        assert!(
            cookie.contains("OAX=TestOax123456789"),
            "missing OAX in {cookie}"
        );
    }
}

#[test]
fn settle_delay_ms_defaults_to_4000_when_omitted_from_config() {
    let dir = tempdir().unwrap();
    std::fs::write(dir.path().join("config.json"), r#"{"passes":1}"#).unwrap();
    let cfg = Config::load(dir.path()).unwrap();
    assert_eq!(cfg.settle_delay_ms, 4000);
}

#[test]
fn config_deduplicates_pages_and_validates_env_urls() {
    let dir = tempdir().unwrap();
    std::fs::write(
        dir.path().join("config.json"),
        serde_json::json!({
            "pages": [
                "https://www.inven.co.kr/",
                {"inspect_only": "https://m.inven.co.kr/"},
                "https://www.inven.co.kr/",
                "https://it.inven.co.kr/"
            ]
        })
        .to_string(),
    )
    .unwrap();
    let cfg = Config::load(dir.path()).unwrap();
    assert_eq!(
        cfg.page_urls(),
        [
            "https://www.inven.co.kr/",
            "https://m.inven.co.kr/",
            "https://it.inven.co.kr/",
        ]
    );

    let parsed = config::parse_env_pages(
        "https://m.inven.co.kr/, https://news.inven.co.kr/",
        &cfg.pages,
    )
    .unwrap();
    assert!(matches!(
        &parsed[0],
        PageTarget::InspectOnly { inspect_only } if inspect_only == "https://m.inven.co.kr/"
    ));
    assert!(matches!(
        &parsed[1],
        PageTarget::Standard(u) if u == "https://news.inven.co.kr/"
    ));
    assert!(config::parse_env_pages("not-a-url", &cfg.pages).is_err());
    assert!(config::parse_env_pages("ftp://www.inven.co.kr/", &cfg.pages).is_err());
}

#[tokio::test]
async fn script_cache_deduplicates_same_url_and_loads_distinct_urls() {
    let (base1, srv1) = serve(vec![Reply {
        status: 200,
        content_type: "application/javascript",
        body: b"var SCRIPT_A = '1';".to_vec(),
    }]);
    let (base2, srv2) = serve(vec![Reply {
        status: 200,
        content_type: "application/javascript",
        body: b"var SCRIPT_B = '2';".to_vec(),
    }]);
    let cache = placements::ScriptCache::new(placements::public_client().unwrap());
    let u1 = reqwest::Url::parse(&format!("{base1}/zicf.js")).unwrap();
    let u2 = reqwest::Url::parse(&format!("{base2}/zicfm.js")).unwrap();
    let (r1a, r1b, r2) = tokio::join!(cache.get(&u1), cache.get(&u1), cache.get(&u2));
    assert!(Arc::ptr_eq(&r1a.unwrap(), &r1b.unwrap()));
    assert!(r2.is_ok());
    assert_eq!(srv1.join().unwrap().len(), 1);
    assert_eq!(srv2.join().unwrap().len(), 1);
}
