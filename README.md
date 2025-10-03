# InvenAdClicker

인벤 광고 클릭 자동화를 위한 콘솔 프로그램입니다.

본 프로젝트는 브라우저 자동화 학습을 위해 작성되었으며, 인벤 서버에 대한 악의적 트래픽을 발생시키기 위함이 아닙니다.

본 프로그램을 과도하게 사용하여 발생하는 문제에 대하여 작성자는 어떠한 책임도 지지 않습니다.

## 주요 기능

- **자동 로그인·보안 저장**: 최초 1회 입력한 계정 정보를 암호화해 다음 실행부터 자동 로그인합니다.
- **광고 큐 기반 자동 클릭**: 수집된 광고 링크를 큐로 관리해 다음 클릭을 지연 없이 실행합니다.
- **네트워크 부하 최소화**: Playwright 라우팅으로 불필요한 리소스를 차단해 실행 속도를 높입니다.
- **디버깅 옵션**: `PlaywrightHeadless`, `PlaywrightJavaScriptEnabled` 값으로 브라우저 창과 JavaScript 실행 여부를 제어할 수 있습니다.
- **실시간 진행 상황**: URL별 수집·클릭 현황을 콘솔에서 즉시 확인할 수 있습니다.

## 사용법

### 1. 사전 준비

- **공통**: Windows 운영체제와 Google Chrome 최신 버전이 설치되어 있어야 합니다.
- **.NET 런타임**: [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)이 설치되어 있어야 합니다.
- **Playwright 설치**: **최초 1회** PowerShell(또는 CMD)에서 아래 명령어를 실행해 런타임을 설치합니다.
  ```shell
  pwsh -c "playwright install"
  ```
  또는 Node.js가 설치된 경우:
  ```shell
  npx playwright install
  ```

  또는 루트 디렉토리에 있는 `playwright.ps1`를 실행해도 됩니다.

### 2. 설정 (`appsettings.json`)

기본 설정은 다음과 같습니다.

```json
{
  "AppSettings": {
    "MaxDegreeOfParallelism": 3,
    "IframeTimeoutMilliSeconds": 5000,
    "ClickDelayMilliseconds": 300,
    "PageLoadTimeoutMilliseconds": 5000,
    "CommandTimeoutMilliSeconds": 5000,
    "CollectionAttempts": 1,
    "MaxClickAttempts": 2,
    "PlaywrightHeadless": true,
    "PlaywrightJavaScriptEnabled": true,
    "TargetUrls": [
      "https://www.inven.co.kr/",
      "https://m.inven.co.kr/",
      "https://it.inven.co.kr/"
    ]
  }
}
```

- `MaxDegreeOfParallelism`: 동시에 실행할 작업 수입니다.
- `ClickDelayMilliseconds`: 동일 URL에서 연속 클릭 사이의 대기 시간(밀리초)입니다.
- `PageLoadTimeoutMilliseconds`: 페이지 로드 타임아웃입니다. 네트워크가 느리다면 값을 올려주세요.
- `PlaywrightHeadless`: `false`로 설정하면 디버깅을 위해 실제 브라우저 창을 띄울 수 있습니다.
- `PlaywrightJavaScriptEnabled`: Playwright 컨텍스트의 JavaScript 실행 여부를 제어합니다.
- `TargetUrls`: 수집/클릭을 진행할 URL 목록입니다.

### 3. 실행

`InvenAdClicker.exe`를 실행합니다.

- **최초 실행 시**: 콘솔 창에 인벤 ID와 비밀번호를 입력하라는 메시지가 나타납니다. 입력한 정보는 PC에 암호화되어 안전하게 저장되며, 다음 실행부터는 자동으로 로그인됩니다.

### 4. 종료

- 모든 작업이 완료되면 프로그램은 자동으로 종료됩니다.
- 즉시 종료하려면 콘솔 창에서 `Ctrl + C`를 누르세요.

## 주의 사항

과도한 자동화 트래픽은 비정상적인 활동(DoS 등)으로 간주될 수 있습니다. `ClickDelayMilliseconds` 와 `MaxDegreeOfParallelism` 설정을 적절히 조절하여 단기간에 과도한 트래픽을 일으키지 않게 해야 합니다.

## 라이선스

본 프로젝트는 MIT 라이선스 하에 배포됩니다. 자세한 내용은 `LICENSE` 파일을 참고하세요.
