# InvenAdClicker

인벤 광고 클릭 자동화를 위한 콘솔 프로그램입니다. Selenium WebDriver(Chrome) 또는 Microsoft Playwright를 사용해 로그인 후 지정된 부모 URL들의 광고 링크를 수집하고, 남는 워커가 발생하는 시점부터 클릭을 병렬로 시작합니다(예: 워커 3개, 남은 부모 URL 2개 → 1개 워커는 클릭으로 전환). 기본 네비게이션 대기 기준은 DOMContentLoaded이며(Playwright), Selenium은 document.readyState가 complete/interactive이면 진행합니다.

## 주요 기능

- 자동 로그인/보안 저장: 최초 1회 계정 정보를 입력하면 로컬에 암호화 저장하고 이후 자동 로그인.
- 수집-클릭 파이프라인: 광고 링크 수집과 클릭을 동시에 진행. 부모 URL 수가 워커 수보다 적으면 남는 워커가 즉시 클릭 작업으로 전환.
- 병렬 처리/최적화: `MaxDegreeOfParallelism` 만큼 동시 처리. Headless 구동과 이미지/CSS/폰트 차단 등으로 리소스 절약.
- 엔진 선택: `BrowserType` 설정으로 `Selenium` 또는 `Playwright` 선택 가능.
- 실시간 진행률/로깅: URL 별 상태, 총 광고 수, 클릭 완료 수, 대기 클릭 수, 오류, 동작 스레드 수를 표 형태로 표시.

## 설치 및 실행 방법

1. 사전 준비
   - Windows + Google Chrome 설치.
   - .NET 8 LTS(Desktop Runtime 또는 SDK) 권장.
2. 소스/바이너리 확보
   - 소스 빌드: `dotnet restore && dotnet build -c Release`
   - Playwright 사용 시 최초 1회: `pwsh -c "playwright install"` 또는 `npx playwright install`
3. 설정 파일 편집(`appsettings.json`)

   아래는 기본 예시입니다(`AppSettings` 섹션을 편집).

   ```json
   {
     "AppSettings": {
       "MaxDegreeOfParallelism": 3,
       "IframeTimeoutSeconds": 5,
       "RetryCount": 1,
       "ClickDelayMilliseconds": 300,
       "PageLoadTimeoutMilliseconds": 3000,
       "CommandTimeoutMilliSeconds": 10000,
       "CollectionAttempts": 1,
       "DisableImages": true,
       "DisableCss": true,
       "DisableFonts": true,
       "TargetUrls": [
         "https://www.inven.co.kr/"
       ],
       "BrowserType": "Selenium" // 또는 "Playwright"
     }
   }
   ```

   - TargetUrls: 처리할 부모 URL 목록.
   - BrowserType: `Selenium` / `Playwright` 중 선택.
   - CollectionAttempts: 같은 페이지에서 수집 반복 횟수(배너 회전 대응).
   - PageLoadTimeoutMilliseconds / CommandTimeoutMilliSeconds: 페이지/커맨드 타임아웃.
   - DisableImages/Css/Fonts: 불필요 리소스 차단.

4. 실행
   - `dotnet run -c Release`
   - 최초 실행 시 콘솔에서 인벤 ID/Password를 입력하면 로컬에 암호화 저장됩니다. 이후 자동 로그인으로 진행됩니다.

5. 종료
   - 모든 작업 완료 시 총 실행 시간이 표시되며 종료 대기 메시지가 출력됩니다.
   - 수동 종료: `Ctrl + C`.

## 사용 예시

- 파이프라인 동작: 부모 URL이 10개이고 워커가 3개라면, 8개 수집 완료 후 남은 2개의 수집은 2개 워커가 담당하고, 남는 1개 워커는 수집된 광고를 즉시 클릭.
- 안정성 조정: `RetryCount`, `ClickDelayMilliseconds`, 페이지/커맨드 타임아웃으로 차단 위험과 성능 균형 조절.
- 엔진 전환: `BrowserType`만 바꿔 Selenium ↔ Playwright 전환.

## 주의 사항

과도한 자동화 트래픽은 서버/네트워크 보안에서 비정상으로 인지될 수 있습니다. 충분한 딜레이와 적절한 반복 횟수를 사용하고, 장시간 연속 실행을 지양하세요.

## 라이선스

본 프로젝트는 MIT 라이선스 하에 배포됩니다. 자세한 내용은 저장소의 `LICENSE`를 참고하세요.
