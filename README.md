# InvenAdClicker

인벤 광고 클릭 자동화를 위한 콘솔 프로그램입니다.

본 프로젝트는 브라우저 자동화 학습을 위해 작성되었으며, 인벤 서버에 대한 악의적 트래픽을 발생시키기 위함이 아닙니다.

본 프로그램을 과도하게 사용하여 발생하는 문제에 대하여 작성자는 어떠한 책임도 지지 않습니다.

## 주요 기능

- **자동 로그인/보안 저장**: 최초 1회 계정 정보를 입력하면 로컬에 암호화 저장하고 이후 자동 로그인.
- **수집-클릭 파이프라인**: 링크 수집을 우선하나, 수집이 끝날 때 까지 대기하지 않고 노는 워커 없이 클릭을 동시에 처리합니다.
- **병렬 처리/최적화**: `MaxDegreeOfParallelism` 설정에 따라 여러 작업을 동시에 처리하며, 불필요한 리소스(이미지, CSS 등)를 차단하여 리소스를 절약합니다.
- **엔진 선택**: `appsettings.json` 파일에서 `BrowserType`을 `Selenium` 또는 `Playwright`로 간단히 변경하여 사용 가능합니다. 지금은 둘 다 지원하지만 나중에 Selenium이 사라질 수 있습니다.
- **실시간 진행률**: URL 별 작업 상태를 실시간으로 콘솔에 표시합니다.

## 사용법

### 1. 사전 준비

- **공통**: Windows 운영체제와 Google Chrome 최신 버전이 설치되어 있어야 합니다.
- **.NET 런타임**: [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)이 설치되어 있어야 합니다.
- **Playwright 사용 시**: `BrowserType`을 `Playwright`로 설정한 경우, **최초 1회** 터미널(PowerShell 또는 CMD)에서 아래 명령어를 실행해 드라이버를 설치해야 합니다.
  ```shell
  pwsh -c "playwright install"
  ```
  또는 Node.js가 설치된 경우:
  ```shell
  npx playwright install
  ```

  또는 루트 디렉토리에 있는 `playwright.ps1`를 실행해도 됩니다.

### 2. 설정 (`appsettings.json`)

실행 파일 옆의 `appsettings.json` 파일을 열어 필요에 맞게 수정합니다.

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
    "DisableImages": true,
    "DisableCss": true,
    "DisableFonts": true,
    "BrowserType": "Playwright",
    "TargetUrls": [
        "링크들"
    ]
  }
}
```
- `BrowserType`: `Playwright` (권장) 또는 `Selenium` 중에서 선택합니다.
- `TargetUrls`: 광고를 수집하고 클릭할 페이지 URL 목록입니다.
- `MaxDegreeOfParallelism`: 동시에 실행할 작업의 수입니다.
- `ClickDelayMilliseconds`: 광고 클릭 후 대기 시간 (밀리초) 입니다.
- `PageLoadTimeoutMilliseconds`: 페이지 로딩 대기 시간입니다. 네트워크가 불안정하면 값을 늘리세요.

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