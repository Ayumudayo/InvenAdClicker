# InvenAdClicker

인벤 광고 클릭 자동화를 위한 경량 HTTP 전용 CLI 프로그램입니다.

본 프로젝트는 HTTP 프로토콜 및 자동화 학습을 위해 작성되었으며, 인벤 서버에 대한 악의적 트래픽을 발생시키기 위함이 아닙니다.

본 프로그램을 과도하게 사용하여 발생하는 문제에 대하여 작성자는 어떠한 책임도 지지 않습니다.

> **v6.0.0 주요 변경**: .NET/Playwright 브라우저 의존성을 완전히 제거하고 Rust 기반 순수 HTTP 구현으로 전환했습니다. 기존 C#/.NET Playwright 버전은 [`v5.1.0`](https://github.com/Ayumudayo/InvenAdClicker/releases/tag/v5.1.0) 태그에서 확인할 수 있습니다.

## 주요 기능

- **순수 HTTP·경량 슬롯 해석**: 브라우저 없이 페이지 HTML과 인벤 광고 스크립트(`zicf.js`, `zicfm.js`)를 직접 해석해 광고 프레임을 수집합니다.
- **세션·보상 처리**: 로그인 후 출석 보상(`불로소득`/`인증보너스`) 확인, 광고 세션(`OAX`) 초기화, 실행 전후 이니·베니·경험치 변화량 조회를 지원합니다.
- **자격 증명 보안 저장**: 최초 1회 입력한 계정 정보를 Windows DPAPI로 암호화해 실행 파일 폴더의 `credentials.dat`에 저장합니다.
- **실시간 반응형 트래커 UI**: `진행 중` · `문제` · `완료`(완료 순서 세로 목록) · `대기` 섹션을 전체 URL과 함께 표시하며, 터미널 크기에 맞춰 자동 배치합니다.
- **수집/클릭 분리 파이프라인**: 수집 워커와 클릭 워커를 채널로 분리해 병렬 처리하고, 결과와 상세 내역은 `logs/InvenAdClicker-*.log`에 기록합니다.

## 사용법

### 1. 실행

배포 ZIP을 압축 해제한 뒤 `InvenAdClicker.exe`를 실행합니다. 별도 런타임이나 브라우저 설치가 필요하지 않습니다.

- **최초 실행 시**: 실행 파일과 같은 폴더에 `config.json`이 없으면 기본 템플릿으로 자동 생성되며, 터미널에서 인벤 아이디와 비밀번호를 입력하면 암호화 저장됩니다.
- **지원 옵션**:
  - `InvenAdClicker.exe`: 기본 실행 (수집 및 클릭)
  - `InvenAdClicker.exe --collect-only`: 광고 링크 수집까지만 수행하고 클릭 요청은 보내지 않음
  - `InvenAdClicker.exe --inspect-placements [설정 파일]`: 로그인 없이 공개 페이지의 광고 프레임 배치만 점검

### 2. 설정 (`config.json`)

실행 파일과 같은 폴더의 `config.json`을 수정해 대상 페이지와 요청 간격·동시성을 조절할 수 있습니다.

```json
{
  "pages": [
    "https://www.inven.co.kr/",
    "https://it.inven.co.kr/",
    "https://m.inven.co.kr/"
  ],
  "passes": 2,
  "concurrency": 3,
  "click_concurrency": 3,
  "click_delay_ms": 300,
  "click_jitter_ms": 600,
  "page_delay_ms": 300,
  "page_jitter_ms": 500,
  "settle_delay_ms": 4000,
  "log_dir": "logs",
  "log_retention_days": 30
}
```

| 설정 | 기본값 | 설명 |
|---|---|---|
| `pages` | 3개 기본 URL | 순회할 페이지 목록. `{"inspect_only": "URL"}` 형태로 클릭 없이 프레임만 점검 가능 |
| `passes` | `2` | 페이지당 수집 반복 횟수 (최소 `1`) |
| `concurrency` | `3` | 동시 페이지 수집 워커 수 |
| `click_concurrency` | `3` | 동시 광고 클릭 워커 수 |
| `click_delay_ms` / `click_jitter_ms` | `300` / `600` | 클릭 요청 간 기본 대기 및 랜덤 지터(ms) |
| `page_delay_ms` / `page_jitter_ms` | `300` / `500` | 페이지 수집 간 기본 대기 및 랜덤 지터(ms) |
| `settle_delay_ms` | `4000` | 클릭 완료 후 최종 포인트·경험치 조회 전 대기 시간(ms) |
| `log_dir` / `log_retention_days` | `"logs"` / `30` | 로그 저장 폴더 및 보관 일수 |
| `click_limit` | 없음(선택) | 세션당 최대 클릭 요청 수 상한 |

#### 환경 변수 재정의
- `INVEN_ID` / `INVEN_PW`: 무인 실행 또는 계정 지정 로그인
- `INVEN_PAGES`: 쉼표(`,`)로 구분된 URL 목록으로 `pages` 재정의 (`http`/`https` 검증)
- `INVEN_PASSES`, `INVEN_CONCURRENCY`, `INVEN_CLICK_CONCURRENCY`, `INVEN_CLICK_LIMIT`, `INVEN_SETTLE_DELAY_MS`, `INVEN_LOG_RETENTION_DAYS`
- `INVEN_ASCII` / `INVEN_UNICODE`: 터미널 아이콘 스타일 강제 전환

### 3. v5.x에서 업그레이드 시 참고
- 설정 파일이 `appsettings.json`에서 실행 파일 옆 `config.json`으로 변경되었습니다.
- 자격 증명 저장 포맷이 변경되어 v6.0.0 첫 실행 시 로그인 정보를 1회 다시 입력해야 합니다.

## 로컬 빌드 및 검증

Rust (stable) 환경에서 저장소 루트 기준으로 실행합니다.

```powershell
cargo clippy --all-targets --locked -- -W clippy::all
cargo test --release --locked
cargo build --release --locked
```

또는 제공된 스크립트로 빌드할 수 있습니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

## 주의 사항

과도한 자동화 트래픽은 비정상적인 활동으로 간주될 수 있습니다. `concurrency`, `click_concurrency` 및 대기 시간 설정을 적절히 조절하여 단기간에 과도한 요청이 발생하지 않도록 유의하세요.

## 라이선스

본 프로젝트는 MIT 라이선스 하에 배포됩니다. 자세한 내용은 [`LICENSE`](LICENSE) 파일을 참고하세요.
