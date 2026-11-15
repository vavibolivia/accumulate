# 바이낸스 1분봉 누적 매수/매도 (Android + PC 서버)

**구성**
- `server/` — PC 프로그램. 바이낸스 **모든 페어**(현물 전체, 옵션으로 USDⓈ-M 무기한 선물)의 1분봉을 수집·보관하고
  기간(1·3·6개월, 1·2·3·5년) × 분할 방식(A/B/C) 별 누적 매수/매도/순매수를 계산.
  **PC 전체화면 대시보드**(`http://localhost:8080/`)로 순위 표 + 누적 그래프(페어 금액·코인 수, 그룹 전체)를 보여주고,
  안드로이드 앱에 `GET /ranking.json` 을 제공. **5분마다 검증**: 저장된 어제 1분봉 합계를 바이낸스 일봉과 대조해 다르면 다시 받아 고침
- `app/` — 안드로이드 앱. 첫 화면은 서버가 계산한 **모든 페어 순매수 순위**(시장·페어 그룹·정렬·검색·합계),
  페어를 누르면 휴대폰에서 1분봉 정밀 계산 + 누적 그래프(실시간)
- `core/` — 계산·수집·저장·검증 공통 코드 (앱과 서버가 같은 식을 씀)

## PC 버전 (WPF, 윈도우 전체화면 앱)
`pc/` — C# .NET 8 WPF. 계산은 같은 폴더의 `binance-cumulative-server.jar` 가 하고 앱이 창 없이 켜고 끈다 (Java 17+ 필요).
- 위: 시장 · 기간 · 분할 방식(A/B/C) · 검색 · 페어 그룹(USDT·BTC·FDUSD… / 전체 USD 환산)
- 왼쪽: 모든 페어 순매수 순위 표 (열 머리글로 정렬, ↑↓ 이동)
- 오른쪽 "선택한 페어": **가격 캔들** + 누적 순매수(금액) + 누적 순매수(코인) + 그룹 전체 — x 축이 함께 확대·이동,
  마우스를 올리면 그 시점 가격(시·고·저·종)과 누적값 표시. A·B 방식이면 "가격에서 추정한 값" 경고
- 오른쪽 "여러 페어 한눈에": 표 위쪽 12개 페어의 작은 그래프 (가격 파란 선 + 누적 순매수), 누르면 큰 그래프
- F11 전체화면 · Ctrl+F 검색 · F5 새로고침 · Ctrl+B 백그라운드 집계 · Esc 전체화면 끄기 · 아래 '서버 로그'
- **로컬 DB (SQLite)** `%AppData%\BinanceCumulative\app.db` — `BinanceCumulative.Pc.Core/Storage/LocalDb.cs`
  - `settings`: 앱 설정 (예전 `settings.json` 은 처음 한 번 자동으로 옮겨 옴)
  - `ranking_cache` · `series_cache` · `group_cache`: 서버 응답 저장본. 서버가 꺼져 있으면 마지막 순위와 그래프를 그대로 보여주고(상단 노란 안내),
    페어를 다시 고르면 저장본을 먼저 그린 뒤 서버에서 새로 받아 바꿈. 30일 안 쓴 그래프 저장본은 시작할 때 지움
  - `daily_agg`: **종목별 일별 집계** — 시장 · 심볼 · 분할 방식(A/B/C) · UTC 날짜마다 한 줄.
    그날의 순매수·매수·매도·거래대금(호가 자산), 순매수 코인 수, 시·고·저·종가, 그리고 **저장된 가장 예전 날부터 그날까지의 누적** 순매수(금액·코인)·누적 거래대금.
    `final=0` 이면 아직 진행 중인 날(오늘). 설정 → 'CSV 내보내기' 로 전체를 뽑을 수 있음
- **코인별 합산 보기** — 표는 항상 같은 코인의 모든 페어를 한 줄로 합친다 (BNB/USDT + BNB/BTC + BNB/ETH → BNB).
  각 페어 금액을 호가 자산의 현재 USDT 가격으로 환산해 더하고, 위의 **USD 기준 / BTC 기준** 만 고른다 (BTC 기준 = USD 값 ÷ BTCUSDT 현재가).
  코인 수는 배수(1000PEPE)를 풀어 더함. 상세 그래프는 거래대금이 가장 큰 페어를 그 호가 자산 단위로 보여준다 (`Calc/RankingCalc.cs` MergeByCoin)
- **일별 집계 DB 의 USDT 환산** — `daily_agg` 의 `*_usd` 열: 페어마다 **그날의** 환율(`usd_rate` = 호가 자산 1개의 USDT 가격, {호가}USDT 종가 또는 USDT{호가} 종가의 역수)로 바꾼 금액.
  BTC 호가 페어는 날마다 그날 BTCUSDT 종가를 쓴다. 환율 페어(USDT 호가·USDT base)를 먼저 집계하고 나머지를 나중에 집계한다.
  `daily_coin` 뷰 = 같은 코인의 모든 페어를 USDT 로 더한 하루 한 줄 (`net_usd`, `cum_net_usd`, 실제 코인 수 `net_coins` …). 설정 → '코인별 CSV'
- 로그 파일: `%AppData%\BinanceCumulative\server.log` (계산 서버 출력), `aggregate.log` (일별 집계 시작·오류·완료), `error.log` (앱 예외)
- **일별 집계 (백그라운드)** — `Storage/DailyAggregator.cs`
  - `▶ 백그라운드 집계`: 순위에 있는 모든 종목 × 세 방식을 서버 `/series?from=0` (저장된 가장 예전 1분봉부터 오늘까지) 으로 받아 하루 단위로 나눠 DB 에 쌓음.
    **화면은 그대로 켜 둔 채** 뒤에서 돌고, 상단에 **진도율 %** 와 진행 막대 표시. 다시 누르면 중지(다음에 이어서)
  - 다시 돌리면 마감된 날은 건너뛰고 오늘(과 그 뒤)만 받아 이어 씀. 서버가 그 종목의 더 예전 1분봉을 새로 받아 두었으면 처음부터 다시
  - 기본으로 앱이 켜져 있는 동안 6시간마다 자동으로 이어서 돈다 (설정에서 끌 수 있음)
  - `⤓ 트레이로` (Ctrl+B, 선택): 창만 숨김. 계산 서버와 집계는 계속 돌아가고 트레이 아이콘에 진도율이 보임.
    아이콘 두 번 클릭 → 다시 열기, 오른쪽 메뉴 '종료' → 서버·집계까지 끔. 집계 중에 창을 닫으면 트레이로 계속할지 물어봄
  - 수집 기간 기본값이 **120개월(2017-07 바이낸스 첫 1분봉부터 전부)** 로 바뀜. 처음 실행은 모든 페어 × 전 기간을 받으므로 오래 걸림
- 구성: `BinanceCumulative.Pc.Core`(데이터·계산·화면 상태·그래프·DB·집계, 리눅스에서도 테스트) /
  `BinanceCumulative.Pc.Tests` / `BinanceCumulative.Pc`(WPF 화면)
- 빌드(윈도우, .NET 8 SDK): `pc/build-windows.bat` → `pc/dist/BinanceCumulative.exe` (CI 는 windows-latest 에서 zip 생성)

## PC 버전 (브라우저 대시보드) 실행
`binance-cumulative-pc.zip` 을 풀고 (`./gradlew :server:pcZip` → `server/build/distributions/`)
1. Java 17 이상 설치 (https://adoptium.net)
2. 윈도우 `start-windows.bat` / 맥·리눅스 `start-mac-linux.sh`
3. 크롬·엣지가 대시보드를 전체화면으로 엶 — F/F11 전체화면, ↑↓ 페어 이동, `/` 검색, R 새로고침
4. 휴대폰 앱 설정 → 서버 주소 `http://<PC 내부 IP>:8080` (같은 Wi-Fi)

- 페어 그룹: USDT·USDC·FDUSD·BTC·ETH·BNB·TRY … 별로 비교하거나, **전체(USD 환산·근사)** — 호가 자산의 현재 USDT 가격으로 환산
- 저장: 최근 2개월만 1분봉 원본, 그 전은 1분봉으로 계산한 월·일 합계만 남김 (기간 시작이 그 구간이면 하루 단위로 맞춤).
  모두 원본으로 남기려면 `--keep-raw` (디스크 수십~수백 GB 추정)
- 옵션: `--markets spot,futures` `--quote USDT` `--months 12` `--interval 5` `--verify 5` `--offline` `--cmc-key` · 전체는 `--help`
- 처음 실행 시 모든 페어 × 5년치를 받으므로 수 시간이 걸릴 수 있음(추정). 받는 동안에도 30초마다 계산된 페어부터 표시

## 기능
- 심볼 입력 (예: `BTCUSDT`, `BTCUSDC`, `ETHBTC`), 시장 선택: **현물** / **USDⓈ-M 선물**
- 기간: **최근 1·3·6개월, 1·2·3·5년** 또는 **From 날짜 직접 지정** (2017-07-14 바이낸스 첫 1분봉까지).
  시작을 누른 시각에 From 이 고정되고, 그때부터 지금까지 누적한 뒤 실시간으로 이어서 더합니다.
- **받은 1분봉은 기기에 보관** → 다음 조회는 즉시
  - UTC 월 단위 파일. 지난 달은 압축하고 세 분할 방식(A/B/C)의 합계를 미리 계산해 둠
  - 조회 = 다 채운 달의 합계 더하기 + From 이 걸친 달·최근 7일만 1분봉 읽기 (5년치 약 0.2초, PC 기준)
  - 시작하면 ① 저장된 데이터로 먼저 즉시 계산 → ② 빠진 달·최근 분만 받아 저장 → ③ 다시 계산 → ④ 실시간,
    실시간으로 마감된 1분봉도 계속 저장
  - 과거 달은 바이낸스 공식 일괄 데이터 [data.binance.vision](https://data.binance.vision) 월/일 zip 으로 받고,
    없으면 REST. 처음 5년치를 받을 때만 다운로드(수백 MB 추정, Wi-Fi 권장)와 계산 시간이 듭니다.
  - 설정에서 저장 용량 확인·전체 삭제
- 설정 화면 (⚙)
  - **크게 표시할 단위**: 금액(심볼 오른쪽 자산: USDT·USDC·FDUSD·BTC …) 또는 코인 수(왼쪽 자산: BTC·ETH …).
    두 단위 모두 항상 계산·표시됩니다. 실제 자산 이름은 Binance exchangeInfo 에서 가져옵니다.
  - **매수/매도 분할 방식**: A 가격 경로 비율(기본) · B 종가 위치 비율 · 바이낸스 실제 taker 데이터
  - **API Key** (선택): Android Keystore 로 암호화해 기기 안에만 저장, 요청 헤더 `X-MBX-APIKEY` 로 전송.
    시세 조회에는 키가 필요 없습니다. 조회 전용 키만 쓰세요.
- 요약: **금액(USDT 등)과 코인 수(BTC 등)를 항상 함께** 표시 — 누적 매수 / 매도 / 순매수 / 합계, VWAP, 체결 수
- 발행량 · 판단 지표 (CoinGecko 공개 API, 없으면 CoinMarketCap — 설정에서 CMC API Key 입력 시): 유통량, 총 발행량, 최대 발행량,
  누적 순매수 ÷ 유통량, 누적 거래량 ÷ 유통량, 유통률. `1000PEPE` 처럼 배수 표기된 선물은 실제 코인 수로 환산
- 1분봉 목록 (최신순): 시각, 종가, 누적 순매수(금액), 누적 순매수(코인 수). `●` 는 진행 중인 봉
- 연결 끊김 시 지수 백오프로 재연결하고, 끊긴 동안의 봉은 REST로 백필

## 입력 검증 (시작 버튼을 누를 때)
- 심볼: 영문 대문자·숫자 2~30자 → Binance exchangeInfo 로 **실제 존재 여부와 거래 중(TRADING) 여부** 확인
- From: 미래 시각 불가, 2017-07-14 이후
- API Key: 비어 있거나 영문·숫자 64자

## 계산 방식
1. REST `GET /api/v3/klines` (선물: `/fapi/v1/klines`), `interval=1m` 으로 기간 시작부터 현재까지 받아옵니다.
2. 이후 `<symbol>@kline_1m` WebSocket 으로 진행 중인 봉을 계속 갱신합니다.
3. 봉마다 금액(Quote 면 quoteVolume, Base 면 volume)을 매수/매도로 나눕니다.

   **A. 가격 경로 비율 (기본)**
   - 양봉(C ≥ O): 시가→저가→고가→종가. up = H − L, down = (O − L) + (H − C)
   - 음봉(C < O): 시가→고가→저가→종가. up = (H − O) + (C − L), down = H − L
   - 매수 = 금액 × up / (up + down), 매도 = 금액 − 매수

   **B. 종가 위치 비율**: 매수 = 금액 × (C − L) / (H − L)

   고가 = 저가이면 50 : 50. **Taker** 는 바이낸스가 주는 실제 시장가 매수 체결량을 씁니다.
4. 누적 순매수 = Σ (매수 − 매도). 같은 봉이 다시 오면 이전 기여분을 빼고 새 값을 더해 **중복 집계가 없습니다.**

자세한 설명: `docs/calculation-presentation.html`
핵심 로직: `core/CandleSplit.kt`, `core/KlineAccumulator.kt`

## 빌드
- Android Studio 에서 `binance-cumulative-android` 폴더를 열고 실행, 또는
- `./gradlew :app:assembleDebug` → `app/build/outputs/apk/debug/app-debug.apk`
- 테스트: `./gradlew :core:test :app:testDebugUnitTest` (계산·저장·순위 + 화면/서버연동 Robolectric) · 린트: `./gradlew :app:lintDebug`

GitHub Actions(`.github/workflows/android.yml`)가 push 시 테스트 후 디버그 APK 를 빌드해
Artifacts(`binance-cumulative-debug-apk`)로 올립니다.

요구 사항: JDK 17, Android SDK 35, minSdk 24 (Android 7.0+)

> 참고: 일부 국가/지역에서는 `api.binance.com` 접속이 제한될 수 있습니다.
