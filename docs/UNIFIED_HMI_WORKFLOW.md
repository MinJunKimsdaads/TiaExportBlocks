# WinCC Unified 화면을 TIA Portal 밖에서 만들어 넣기 — 작업 흐름

TIA Portal 밖(웹 편집기 등)에서 화면을 정의하고, TiaApiServer 의 REST API 로
TIA 프로젝트에 **네이티브 Unified 화면**으로 넣는 흐름이다. 2026-11-10 에
TIA Portal V21, MTP1200 Unified Comfort 패널 프로젝트에서 끝까지 해 봤고, 2026-11-12 에
나머지 Unified 모델 · 그림 넣기 · 이벤트를 같은 방법으로 확인했다(7장).

이 문서의 이름(`OP01`, `fpCylinder`, `Tank1_Level` …)은 설명용으로 지은 것이다.

---

## 1. 왜 이 방식인가

Unified 화면은 **파일로 가져올 수 없다.** 설치된 V21 Openness 어셈블리
(`Siemens.Engineering.WinCCUnified.dll`)를 리플렉션해 확인했다.

| 대상 | Import / Export |
|---|---|
| 태그 · 텍스트 리스트 · 그래픽 리스트 · 스크립트 모듈 | 있음 |
| 화면 (`HmiScreenComposition`) | **없음** — `Create(string)` · `Find(string)` 뿐 |
| 화면 항목 (`HmiScreenItemBaseComposition`) | **없음** — `Create<T>(string)` · `Create<T>(string, string)` · `Find` |

화면 XML 가져오기(`Screens.Import`)는 클래식(Comfort/Advanced) 패널의 API 다. Unified 에는 입구가 없다.
그래서 **JSON 은 Openness 가 받는 것이 아니라 이 서버가 받는다.** 서버가 JSON 을 읽어
`Create` 와 속성 대입을 반복한다. 쓰는 사람에게는 "파일을 넣는다"로 보인다.

```
웹 편집기 ──(화면.json)──▶ TiaApiServer ──(Openness: Create + 속성 대입)──▶ TIA 프로젝트
     ▲                          │
     └──(catalog: 그래픽 · 페이스플레이트 · 항목 속성 표)──┘
```

---

## 2. 전체 흐름

| 단계 | 누가 | 무엇을 | API |
|---|---|---|---|
| ① 연결 | 사용자 PC | TIA Portal 에 붙는다 | `GET /api/processes` → `GET /api/connect?processId=` |
| ② 재료 추출 | 사용자 PC | 프로젝트의 그래픽 · 페이스플레이트를 **카탈로그 폴더**로 뽑는다 | `catalog/export` (나눠서: `graphics/export` · `faceplates?probe=true` · `item-types`) |
| ③ 그리기 | 웹 | 카탈로그 폴더를 불러와 도구 모음에서 끌어다 화면을 만든다 | (서버 없음) |
| ④ 내보내기 | 웹 | 화면 JSON 을 내려받는다 | (서버 없음) |
| ⑤ 넣기 | 사용자 PC | JSON 을 프로젝트에 넣는다 | `POST /api/hmi-unified/screen` |
| ⑥ 확인 | 사용자 PC | 다시 읽어 본다 · TIA 에서 연다 | `GET /api/hmi-unified/screen` |

**전제** ⑤ 의 대상 프로젝트에는 화면이 가리키는 그래픽과 페이스플레이트 타입이 **이미 있어야 한다**
(프로젝트 그래픽, 프로젝트 라이브러리). ② 를 뽑은 프로젝트와 같은 라이브러리를 쓰는 프로젝트여야 한다.

모든 `hmi-unified` 호출은 `?hmi=<장치 이름>` 이 필요하다. 장치 이름은 `GET /api/devices` 에서
`softwareKind` 가 `HMI_Unified` 인 항목이 든 장치의 `name` 이다(장치 그룹 안에 있어도 찾는다).

---

## 3. 단계별

### ① 연결

```bash
TiaApiServer.V21.exe 8099                       # TIA 를 먼저 띄우고 프로젝트를 연 뒤
curl http://localhost:8099/api/processes
curl "http://localhost:8099/api/connect?processId=<pid>"
curl http://localhost:8099/api/devices          # softwareKind: HMI_Unified 인 장치를 찾는다
```

### ② 재료 추출 (읽기 — 페이스플레이트 조사만 프로젝트를 잠깐 바꾼다)

**한 번에** — 편집기가 불러올 카탈로그 폴더를 만든다. 아래 ⓐ 와 ⓑ 를 함께 돌리고 `catalog.json` 으로 묶는다.

```bash
curl -G "http://localhost:8099/api/hmi-unified/catalog/export" \
  --data-urlencode "hmi=OP01" --data-urlencode "dir=C:\work\catalog"
```

```
C:\work\catalog\
  catalog.json                                   그래픽 이름 → 그림 파일, 페이스플레이트(타입 · 기본 크기 · 인터페이스)
  graphics\<이름>.xml
  graphics\<이름> files\DefaultImageStream.svg    (png · jpg · bmp · emf 일 수도 있다)
```

```json
{ "format": "unified-hmi-catalog", "version": 1,
  "graphics":   [ { "name": "CompanyLogo", "image": "graphics/CompanyLogo files/DefaultImageStream.svg" } ],
  "faceplates": [ { "name": "fpCylinder", "containedType": "V0.5.4\\fpCylinder", "folder": "Actuators/Cylinder",
                    "width": 215, "height": 115, "interface": [ { "name": "interfaceTag", "valueType": "String" } ] } ] }
```

응답은 `{ dir, file, graphics, faceplates, errors, elapsedMs }` 다. 그래픽 187개 · 페이스플레이트 105개인 프로젝트에서
약 100초가 걸렸고, 끝난 뒤 임시 화면은 남지 않았다. 컨테이너로 놓을 수 없는 라이브러리 타입은 카탈로그에서 빠진다.
카탈로그에는 그 프로젝트의 그림과 이름이 그대로 들어 있다 — 공개된 곳에 올리지 않는다.

아래는 같은 일을 나눠서 하는 호출이다.

**ⓐ 그래픽** — 프로젝트 그래픽을 폴더로 내보낸다. 그래픽마다 `<이름>.xml` 과 `<이름> files/` 폴더(그림 파일)가 생긴다.

```bash
curl -G "http://localhost:8099/api/hmi-unified/graphics/export" \
  --data-urlencode "hmi=OP01" --data-urlencode "dir=C:\work\catalog\graphics"
```

그림 형식은 svg · png · jpg · bmp · emf 가 섞여 나온다. emf 는 브라우저가 못 그린다(변환이 필요하다).
V21 은 그래픽이 `project.Graphics` 가 아니라 `GraphicsProvider` 서비스 아래에 있다 — 서버가 둘 다 본다.

**ⓑ 페이스플레이트** — 프로젝트 라이브러리의 타입 중 페이스플레이트인 것과, 그 **인터페이스 속성 이름 · 기본 크기**.

```bash
curl "http://localhost:8099/api/hmi-unified/faceplates?hmi=OP01&probe=true"
```

```json
{ "name": "fpCylinder", "folder": "Actuators/Cylinder", "latest": "0.5.4",
  "containedType": "V0.5.4\\fpCylinder", "faceplate": true, "width": 215, "height": 115,
  "interface": [ { "name": "interfaceTag", "valueType": "String" } ] }
```

인터페이스 속성은 타입만 보고는 알 수 없다. `probe=true` 는 **임시 화면을 만들어 컨테이너를 놓아 보고
읽은 뒤 지운다**(끝나면 임시 화면도 지운다 — 저장은 하지 않는다). 타입 하나에 0.5~1초다.
`probe` 를 빼면 이름과 버전만 빠르게 준다. `filter=<이름 일부>` 로 좁힐 수 있다.
컨테이너로 놓을 수 없는 라이브러리 타입은 `faceplate: false` 로 나온다.

**ⓒ 항목 속성 표** — 화면 항목 종류마다 어떤 속성이 있고 어떤 값을 받는가. Openness 어셈블리를 리플렉션한 것이라 프로젝트와 무관하다(TIA 버전마다 다르다).

```bash
curl "http://localhost:8099/api/hmi-unified/item-types?hmi=OP01"
```

`items`(종류별 속성 · 이벤트 이름) · `parts`(글꼴 · 여백 같은 묶음) · `enums`(열거값)을 준다.
속성의 꼴은 `Int32` · `Boolean` · `color` · `text`(다국어) · `enum:<이름>` · `part:<이름>` · `list:<이름>` 이다.

**ⓓ 기존 화면 읽기** — 이미 있는 화면을 같은 꼴의 JSON 으로. 편집기가 불러와 고칠 수 있다.

```bash
curl "http://localhost:8099/api/hmi-unified/screens?hmi=OP01"                       # 화면 트리
curl -G "http://localhost:8099/api/hmi-unified/screen" \
  --data-urlencode "hmi=OP01" --data-urlencode "screen=Main/Overview"               # 화면 하나
```

### ③ ④ 웹에서 그리고 내보내기

편집기는 ② 의 카탈로그 폴더를 도구 모음으로 쓴다. 내보내는 것은 4장의 화면 JSON 하나(`<화면 이름>.screen.json`)다.
그래픽과 페이스플레이트는 **이름으로만** 가리킨다(그림이나 타입 자체를 싣지 않는다).

1. 화면 이름 · 화면 폴더 · 패널 모델을 정한다(지금은 MTP1200 Unified Comfort, 1280×800 하나).
2. 「카탈로그 폴더 불러오기」로 ② 의 폴더를 고른다. 파일은 브라우저 안에서만 읽힌다 — 어디에도 올라가지 않는다.
   카탈로그 없이도 도형은 그릴 수 있고, 그래픽 · 페이스플레이트는 이름을 직접 적을 수 있다.
3. 도구 모음의 세 탭(도형 · 그래픽 · 페이스플레이트)에서 끌어다 놓는다. 페이스플레이트는 카탈로그의 기본 크기로 놓이고,
   인터페이스 속성마다 HMI 태그 이름을 적는 칸이 생긴다.
4. 내보내기 전에 검사한다 — 이름이 비었거나 겹치면 막고, 카탈로그에 없는 그래픽 · 페이스플레이트 이름과
   화면 밖으로 나간 항목은 경고한다(없는 그래픽 이름을 서버가 거절하지 않는 것을 여기서 잡는다).

편집기가 내는 항목 종류: `HmiRectangle` · `HmiCircle` · `HmiLine` · `HmiText` · `HmiTextBox` · `HmiButton` ·
`HmiIOField` · `HmiGraphicView` · `HmiFaceplateContainer`. 태그 연결은 `ProcessValue`(IO 필드)와 `Visible`,
이벤트는 `Tapped` 를 낸다.

### ⑤ 넣기

```bash
curl -X POST "http://localhost:8099/api/hmi-unified/screen?hmi=OP01" \
  -H "Content-Type: application/json; charset=utf-8" --data-binary "@screen.json"
```

```json
{ "path": "Line1/Overview", "screenCreated": true, "groupsCreated": ["Line1"],
  "itemsCreated": 5, "itemsUpdated": 0, "propertiesSet": 33, "dynamizations": 2, "events": 1,
  "failureCount": 0, "failures": [], "elapsedMs": 3645, "saved": false }
```

- 화면 그룹이 없으면 만든다. 화면이 없으면 만들고, 있으면 고친다.
- 항목은 **이름으로** 찾는다. 있으면 본문에 적힌 속성만 고치고, 없으면 만든다. 본문에 없는 항목은 그대로 둔다.
- 속성 하나가 실패해도 멈추지 않는다. 실패는 `failures` 에 `where`(어느 항목) · `key`(어느 속성) · `error` 로 모인다.
- **저장하지 않는다**(`saved: false`). 확인한 뒤 TIA 에서 저장한다. 저장 전에는 TIA 를 닫으면 사라진다.

### ⑥ 확인 · 되돌리기

```bash
curl -G "http://localhost:8099/api/hmi-unified/screen" --data-urlencode "hmi=OP01" --data-urlencode "screen=Line1/Overview"
curl -X DELETE -G "http://localhost:8099/api/hmi-unified/screen" --data-urlencode "hmi=OP01" --data-urlencode "screen=Line1/Overview" --data-urlencode "item=Start"   # 항목 하나
curl -X DELETE -G "http://localhost:8099/api/hmi-unified/screen" --data-urlencode "hmi=OP01" --data-urlencode "screen=Line1/Overview"                              # 화면
curl -X DELETE -G "http://localhost:8099/api/hmi-unified/screen-group" --data-urlencode "hmi=OP01" --data-urlencode "group=Line1"                                 # 빈 그룹만
```

---

## 4. 화면 JSON

조회(GET)가 내놓는 이름과 넣기(POST)가 받는 이름이 같다 — **Openness 속성 이름 그대로**다.
소문자 키는 예약이다: `group` · `name` · `model` · `type` · `typeName` · `items` · `dynamizations` · `events` · `interface` · `graphics` · `prune`.

```json
{
  "group": "Line1",
  "name": "Overview",
  "Width": 1280, "Height": 800, "BackColor": "#FF1E2A30",
  "items": [
    { "name": "Title", "type": "HmiTextBox", "Left": 40, "Top": 30, "Width": 600, "Height": 60,
      "Text": { "ko-KR": "<body><p>개요</p></body>", "en-US": "<body><p>Overview</p></body>" },
      "Font": { "Size": 28, "Weight": "Bold" } },

    { "name": "Start", "type": "HmiButton", "Left": 70, "Top": 140, "Width": 200, "Height": 70,
      "BackColor": "#FF0F766E", "Text": "<body><p>Start</p></body>",
      "events": { "Tapped": "HMIRuntime.Tags.SysFct.SetTagValue(\"Tank1_Start\", 1);\n" } },

    { "name": "Level", "type": "HmiIOField", "Left": 300, "Top": 140, "Width": 200, "Height": 70,
      "dynamizations": { "ProcessValue": "Tank1_Level" } },

    { "name": "Lamp", "type": "HmiCircle", "CenterX": 120, "CenterY": 290, "Radius": 40,
      "dynamizations": { "BackColor": { "type": "Script", "ScriptCode": "return Tags(\"Tank1_Run\").Read() ? 0xFF22C55E : 0xFF64748B;\n" } } },

    { "name": "Logo", "type": "HmiGraphicView", "Left": 900, "Top": 30, "Width": 300, "Height": 120,
      "Graphic": "CompanyLogo", "GraphicStretchMode": "Uniform" },

    { "name": "Cyl1", "type": "HmiFaceplateContainer", "typeName": "V0.5.4\\fpCylinder",
      "Left": 200, "Top": 400, "Width": 215, "Height": 115,
      "interface": { "interfaceTag": "Cyl1_Interface" } }
  ]
}
```

| 것 | 꼴 |
|---|---|
| 색 | `#AARRGGBB` (또는 `#RRGGBB`) |
| 열거값 | 멤버 이름 (`"Bold"`, `"Uniform"`) |
| 묶음(글꼴 · 여백) | 중첩 객체 — 적은 것만 바뀐다 |
| 글 | HTML 이다: `<body><p>글</p></body>`. 문자열이면 **모든 언어**에, `{ "ko-KR": … }` 이면 그 언어에만 |
| 위치 | 대부분 `Left` · `Top` · `Width` · `Height`. **원 · 타원 · 호 계열은 `CenterX` · `CenterY` · `Radius`**, **선은 `X1` · `Y1` · `X2` · `Y2`**(화면 기준 좌표) |
| 태그 연결 | `"dynamizations": { "<속성>": "<HMI 태그 이름>" }` |
| 스크립트 동적화 | `{ "type": "Script", "ScriptCode": "…" }` |
| 연결 지우기 | `"dynamizations": { "<속성>": null }` |
| 이벤트 | `"events": { "Tapped": "<자바스크립트>" }` — 이벤트 이름은 `item-types` 의 `events` |
| 그래픽 | `"Graphic": "<프로젝트 그래픽 이름>"` |
| 그림 함께 넣기 | 최상위 `"graphics": [ { "name": "Logo", "file": "logo.svg", "data": "<base64>" } ]` — 화면보다 먼저 프로젝트 그래픽으로 들어간다 |
| 화면 이벤트 | 최상위 `"events": { "Loaded": "<자바스크립트>" }` — `Loaded` · `Unloaded` · `Tapped` · `ContextTapped` |
| 통째로 맞추기 | 최상위 `"prune": true` — 본문의 `items` 에 없는 항목을 화면에서 지운다 |
| 모델 표시 | 최상위 `"model"` 은 편집기가 다시 읽을 때 쓰는 표식이다. 서버는 읽지 않는다 |
| 페이스플레이트 | 만들 때 `"typeName": "V<버전>\\<타입 이름>"`, 값은 `"interface": { "<속성>": "<HMI 태그 이름>" }` |

---

## 5. 해 보고 알게 된 것

- **없는 그래픽 이름은 거절되지 않는다.** `Graphic` 은 문자열일 뿐이라 오타가 그대로 들어가고 빈 그림이 된다.
  넣기 전에 ② ⓐ 의 목록과 대조해야 한다. (서버는 아직 검사하지 않는다.)
- **없는 페이스플레이트 버전은 거절된다** — "유효한 버전의 타입을 찾을 수 없다". 그 항목만 실패한다.
- **없는 태그 이름이 어떻게 되는지는 확인하지 않았다.** 시험에는 있는 태그만 썼다.
- **속도** — 항목 5개 생성에 약 3.5초, 수정에 약 1.3초. 속성을 하나씩 넣기 때문이다. 항목 수백 개면 분 단위다.
- **글은 편집 언어로 보인다.** TIA 의 편집 언어가 영어면 영어 글이 보인다 — 한국어만 넣었으면 빈 것처럼 보인다.
- **시스템 진단 컨트롤은 읽지 않는다.** V21 에서 그 속성 하나를 읽으면 TIA 가 죽는다는 보고가 있어 서버가 건너뛴다.
- **이벤트 스크립트의 `PropertyName` · `DynamizationType`** 은 읽으면 "지원하지 않음" 오류가 난다. 조회 응답의 `errors` 에 들어가고 값에는 지장이 없다.
- **선은 `X1` · `Y1` · `X2` · `Y2` 로 넣는다.** 화면 기준 좌표로 그려진다(TIA 편집기에서 눈으로 확인). 넣은 뒤 읽으면
  `Left` · `Top` · `Width` · `Height` 는 기본값 그대로 나온다 — 선의 위치는 그 넷으로 판단하지 않는다.
- **웹 편집기에서 낸 파일을 그대로 넣었다** — 글 · 사각형 · 선 · 버튼 · IO 필드 · 원 · 글 상자 · 그래픽 2개 · 페이스플레이트 1개,
  항목 10개에 실패 0건, 2.1초. TIA 편집기에 편집기에서 그린 자리대로 나왔다.
- **서버를 다시 빌드하면 첫 연결에서 TIA 가 접근 허용을 묻는다.** 창에 답하기 전까지 `/api/connect` 가 멈춰 있다가
  "Security error" 로 시간 초과된다. TIA 에서 허용한 뒤 다시 연결한다.
- **컴파일은 해 보지 않았다.** 만든 화면이 TIA 편집기에 맞게 그려지는 것까지 확인했다.
- **사용 조건** — TIA Portal, Openness 옵션, WinCC Unified 엔지니어링 라이선스, Windows 그룹 `Siemens TIA Openness`.

---

## 6. 남은 일

- 넣기 전 검사: 그래픽 이름 · 페이스플레이트 타입 · 태그 이름이 대상 프로젝트에 있는가.
- 다각형의 `Points`, 화면 창, 트렌드 축 같은 목록형 속성의 쓰기.
- 카탈로그에 HMI 태그 목록 싣기(편집기가 태그 이름을 검사하고 고르게 할 수 있다).
- 편집기: 기존 화면(GET 꼴) 불러오기, 여러 항목 선택 · 정렬, emf 그림 미리보기.
- Swagger 문서(`openapi.json`)에 이 엔드포인트들을 싣기.
- V20 빌드에서의 확인(코드는 리플렉션으로 써서 같은 소스로 컴파일되지만 돌려 보지 않았다).

---

## 7. 2026-11-12 에 더 확인한 것

같은 V21 프로젝트에 시험용 장치를 추가해 확인했다(저장하지 않았다).

### 모델과 화면 크기

하드웨어 카탈로그의 Unified 장치를 하나씩 추가하고(`GET /api/project/add-device?type=OrderNumber:…/21.0.0.0&name=HMI_RT&order=<장치 이름>`)
새 화면의 기본 크기를 읽었다. 모두 카탈로그의 해상도와 같다.

| 계열 | 모델 | 화면 |
|---|---|---|
| Unified Comfort | MTP700 · MTP1000 · MTP1200 · MTP1500 · MTP1900 · MTP2200 | 800×480 · 1280×800 · 1280×800 · 1366×768 · 1920×1080 · 1920×1080 |
| Unified Comfort PRO | MTP1200 · MTP1500 · MTP1900 · MTP2200 | 1280×800 · 1366×768 · 1920×1080 · 1920×1080 |
| Unified Basic | MTP400 · MTP700 · MTP1000 · MTP1200 | 800×480 · 800×480 · 1280×800 · 1280×800 |
| Unified PC | WinCC Unified PC RT | 기본 1920×1080, 다른 크기도 받는다(2560×1440 으로 확인) |

- PC RT 는 `name=` 을 **빈 값**으로 줘야 추가된다(`name=HMI_RT` 는 "Invalid 'Name' parameter").
- 화면의 `Width` · `Height` 는 패널에서도 다른 값이 거절되지 않는다 — 패널 해상도와 맞추는 것은 넣는 쪽의 몫이다.

### 항목 종류

모델 다섯(MTP400 Basic · MTP1000 Basic · MTP700 Comfort · MTP1500 Comfort · MTP2200 Comfort PRO)과 PC RT 에
`item-types` 의 항목 종류 41개를 기본값으로 만들어 봤다(시스템 진단 컨트롤 · 컨테이너 3종 제외).

- 도형 · 글 · 글 상자 · 버튼 · IO 필드 · 그래픽 뷰는 **모든 모델에서** 만들어진다.
- Unified Basic 에서는 7종이 만들어지지 않는다: `HmiFunctionTrendControl` · `HmiMediaControl` · `HmiTrendCompanion` ·
  `HmiProcessDiagnosis…` 4종.
- `HmiLabel` 은 어느 장치에서도 만들어지지 않았다. `HmiProcessControl` 은 패널에서는 안 되고 PC RT 에서만 된다.

### 이벤트

이벤트 이름은 `item-types` 의 `events` 에 있다(어셈블리의 `<종류>EventType` 열거값).

| 대상 | 이벤트 |
|---|---|
| 도형 · 글 · 글 상자 · IO 필드 · 그래픽 뷰 | `Activated` · `Deactivated` · `Tapped` · `KeyDown` · `KeyUp` · `ContextTapped` |
| 버튼 | 위 여섯 + `Down` · `Up` |
| 화면 | `Loaded` · `Unloaded` · `Tapped` · `ContextTapped` |

위 표의 이벤트는 여섯 장치 모두에서 스크립트와 함께 만들어졌다. 만들어지지 않은 것은 알람 표시기의
`AlarmStateTapped` 와 프로세스 진단 컨트롤의 버튼 이벤트 4개뿐이다.

### 그림을 프로젝트 그래픽으로 넣기

```bash
curl -X POST "http://localhost:8099/api/hmi-unified/graphics?hmi=OP01" \
  -H "Content-Type: application/json" --data-binary "@graphics.json"
# { "graphics": [ { "name": "Logo", "file": "logo.svg", "data": "<base64>" } ] }
```

서버가 그래픽 가져오기 문서(`<이름>.xml` + `<이름> files/DefaultImageStream.<확장자>`)를 임시 폴더에 만들어
`Graphics.Import(…, Override)` 로 넣는다. 같은 이름은 덮어쓴다. `data` 는 `data:` URL 이어도 된다.

| 형식 | 결과 |
|---|---|
| svg · png · jpg(jpeg) · bmp · gif · tif | 들어간다. 다시 내보내면 **바이트가 같다** |
| ico | 거절된다 |
| 그림이 아닌 내용(확장자만 png) | 거절된다 — TIA 가 내용을 본다 |

webp 는 해 보지 않았다. 런타임에서 gif · tif 가 어떻게 보이는지는 확인하지 않았다.

화면 JSON 의 최상위 `graphics` 에 실으면 한 번에 된다 — 응답의 `graphicsImported` 가 넣은 수다.
웹 편집기에서 그림 다섯(svg · png · jpg · bmp · gif)을 올려 그린 화면을 MTP700 Basic · MTP1500 Comfort · PC RT(2560×1440)에
넣었다: 그림 5 · 항목 10 · 이벤트 6, 실패 0건, 7~9초.

### 페이스플레이트 타입의 속은 Openness 로 닿지 않는다

`GET /api/hmi-unified/library-type?hmi=&name=<타입 이름>[&dir=]` 로 라이브러리 타입 하나를 있는 그대로 볼 수 있다.

- Unified 페이스플레이트는 런타임에서 일반 `LibraryType` / `LibraryTypeVersion` 이다. 컴포지션은 `Comment` · `Versions` 뿐이고
  `GetSupportedExportFormats()` 는 비어 있다. Unified 페이스플레이트 타입을 다루는 클래스가 어셈블리에 없다.
- `LibraryTypeVersion.Export()` 는 성공으로 돌아오지만 파일에는 작성자 · 버전 · 주석만 있다. `ExportAsDocuments` 는 아무것도 쓰지 않는다.
- 그래서 페이스플레이트는 **놓기와 인터페이스 값 넣기**만 된다. 속을 읽거나, 만들거나, 고칠 수 없다.
  `Edit()` · `Release()` 는 호출해 보지 않았다(프로젝트를 바꾼다).
