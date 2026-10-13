# TIA Portal Third-Party Bridge

**Siemens TIA Portal's Openness API is C#-only.** This repository opens it up:
a local REST server (**TiaApiServer**) that any language can call, and a GUI
extractor (**TiaInspector**) that dumps a whole CPU to XML + JSON.
Supports TIA Portal **V19 / V20 / V21**. Windows x64, .NET Framework 4.8.

> No Siemens software is included or redistributed here — see [NOTICE](NOTICE).
> You need your own licensed TIA Portal installation.

한국어 문서입니다. 아래부터 설치·빌드·사용법 순서로 이어집니다.

---

## 무엇을 하는 도구인가

Openness 는 C# 전용이라 Python·Node·curl·CI 같은 바깥에서 PLC 프로젝트를
건드릴 방법이 없다. 이 저장소는 그 벽을 두 방향으로 넘는다.

```
          Siemens TIA Portal  (실행 중인 프로세스)
                    ↕  Openness API (C# 전용, 로컬 전용)
      ┌─────────────────────────┬──────────────────────────┐
      │  TiaApiServer           │  TiaInspector            │
      │  HTTP REST 브릿지       │  WinForms 추출기         │
      └─────────────────────────┴──────────────────────────┘
                    ↕ REST/JSON              ↕ 파일 산출물
       브라우저 · Python · curl · CI      xml/ · json/ · snapshot.json
```

| | TiaApiServer | TiaInspector |
|---|---|---|
| 형태 | 콘솔 + HTTP 서버 (`localhost:8099`) | GUI + 버전 선택 런처 |
| 쓰임 | 외부 도구가 REST 로 조회 · export · import | 사람이 눌러서 CPU 하나를 통째 추출 |
| 바이너리 | V20 용 1개 + V21 용 1개 | 버전별 3개 + 런처 1개 |
| 프로젝트 변경 | import·compile 호출 시 변경함 | 읽기 전용 (컴파일 안 함) |

둘 다 **로컬 전용**이다. Openness 자체가 로컬 프로세스 간 통신만 지원해서
원격 서버에 올려 쓸 수 없다.

---

## 1. 요구사항

| 항목 | 비고 |
|---|---|
| Windows 10/11 **x64** | Openness 는 x64 전용 |
| TIA Portal **V19 / V20 / V21** | 쓰려는 버전이 설치돼 있어야 한다 |
| .NET Framework 4.8 | Windows 10 1903+ 에 기본 포함 |
| Visual Studio 2022 | 빌드용 (MSBuild) |
| Windows 로컬 그룹 `Siemens TIA Openness` | 사용자 등록 필요 — 아래 2번 |

## 2. Openness 권한 등록 (한 번만)

이걸 빠뜨리면 `new TiaPortal()` 이 Security error 로 죽거나, 더 고약하게는
**세션은 맺어지는데 `TiaPortal.Projects` 가 빈 컬렉션으로 돌아와 "CPU 0대"** 로만 보인다.

```powershell
# 관리자 PowerShell
Add-LocalGroupMember -Group 'Siemens TIA Openness' -Member $env:USERNAME
```

그룹 토큰은 로그인할 때 확정되므로 **로그아웃 후 다시 로그인**해야 적용된다.
그룹 이름은 TIA 버전과 무관하게 하나라서 한 번 등록하면 V19·V20·V21 모두 커버된다.

실행 파일 단위 AllowList 등록이 추가로 필요한 환경이라면
`TiaInspector\register-openness.ps1` 을 관리자 권한으로 실행한다.

## 3. 빌드

```powershell
git clone https://github.com/MinJunKimsdaads/TiaExportBlocks.git
cd TiaExportBlocks

cd TiaApiServer;  .\build.ps1;  cd ..
cd TiaInspector;  .\build.ps1;  cd ..
```

설치돼 있는 TIA 버전만 골라 빌드하고 나머지는 건너뛴다. 옵션:

```powershell
.\build.ps1 -Versions 21          # V21 만
.\build.ps1 -Configuration Debug
```

> **cmd 에서는 `.\build.ps1` 이 조용히 아무것도 안 한다.** cmd 는 PS 스크립트를
> 실행하지 못한다. 이렇게 쓸 것:
> ```
> powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
> ```

산출물:

```
TiaApiServer\bin\Release\TiaApiServer.V20.exe     ← V19 + V20
TiaApiServer\bin\Release\TiaApiServer.V21.exe     ← V21
TiaInspector\bin\Release\TiaInspector.exe         ← 런처 (이걸 실행)
TiaInspector\bin\Release\TiaInspector.V19/20/21.exe
```

Visual Studio 로 `TiaExportBlocks.sln` 을 열어 빌드해도 된다. 단 설치되지 않은
버전의 프로젝트는 레퍼런스를 못 찾아 실패하므로, 터미널에서는 `build.ps1` 이 편하다.

---

## 4. TiaApiServer 사용법

### 4.1 띄우고 붙기

**TIA Portal 을 먼저 실행하고 프로젝트를 열어 둔다.** 그 다음 버전에 맞는 exe 를 띄운다.

```powershell
.\bin\Release\TiaApiServer.V21.exe                  # V21, 포트 8099
.\bin\Release\TiaApiServer.V20.exe 9000             # V20, 포트 9000
.\bin\Release\TiaApiServer.V20.exe 8099 --tia=19    # V19 에 붙기
```

열고 나서 **반드시 연결 먼저** 한다. 이걸 안 하면 다른 호출이 전부
`Not connected to TIA Portal` 로 떨어진다.

```bash
curl http://localhost:8099/api/processes    # 실행 중인 TIA 목록
curl http://localhost:8099/api/connect      # 첫 인스턴스에 attach
curl http://localhost:8099/api/status       # 연결 확인
```

여러 개가 떠 있으면 `/api/connect?processId=<pid>` 로 고른다.
TIA 가 아예 안 떠 있으면 `/api/portal/new` 로 새로 띄울 수 있다.

### 4.2 브라우저에서 쓰기

| URL | 용도 |
|---|---|
| `http://localhost:8099/` | **Swagger UI** — 82개 엔드포인트 전체 + Try it out |
| `http://localhost:8099/explorer` | **CPU Explorer** — 폴더 트리에서 체크박스로 골라 XML export |
| `http://localhost:8099/ui/device-properties` | CPU 속성 편집 |
| `http://localhost:8099/ui/network-properties` | PN/DP 네트워크 속성 편집 |

Swagger 스펙은 exe 에 내장돼 있다. 서버 없이 스펙만 보려면
[`docs/swagger/index.html`](docs/swagger/index.html) 을 브라우저로 열면 된다.

### 4.3 골라서 Export — 가장 많이 쓰는 흐름

트리를 받아서 원하는 leaf 의 `path` 만 모아 POST 하면 그것만 담긴 ZIP 이 온다.

```bash
# ① 트리 조회 — 각 leaf 의 path 가 곧 선택 ID 이자 ZIP 엔트리 경로다
curl "http://localhost:8099/api/plc/tree?plc=PLC_1"

# ② 고른 것만 ZIP 으로
curl -X POST "http://localhost:8099/api/plc/export/selected?plc=PLC_1" \
  -H "Content-Type: application/json" \
  -d '{"paths":["PLC_1/Program blocks/Main.xml","PLC_1/PLC data types/MyUdt.xml"]}' \
  -o selected.zip

# ③ 그대로 되돌리기 — 경로 규칙이 같아서 import 가 바로 받는다
curl "http://localhost:8099/api/plc/import/xml?plc=PLC_1&file=C:\path\selected.zip"
```

ZIP 안 `_export_report.json` 에 `requested / success / failed / notFound` 가 들어온다.
**notFound** 는 요청했는데 산출되지 않은 경로다 — 이름이 바뀌었거나 export 가 실패한 것.

트리 leaf 에 `info.consistent:false` 인 블록은 export 가 실패한다. Explorer 에서는
주황 `inconsistent` 뱃지로 보이고, `&compile=true` 를 붙이면 자동 해결을 시도한다.

Import 순서(타입 → DB → 태그 → 코드블록)는 서버가 알아서 잡는다.

### 4.4 API 한눈에

| 분류 | 내용 |
|---|---|
| 연결 | 프로세스 목록 · attach · 상태 · 새 인스턴스 · 프로젝트 생성 |
| 조회 | 프로젝트 → 디바이스 → PLC 블록 / 태그 / UDT / Software Unit |
| 트리 | `GET /api/plc/tree` — export 가능한 XML 을 TIA 트리 계층으로 |
| Export | 블록 소스(SCL/DB/STL) · 전체 XML ZIP · **선택분만 XML ZIP** · 하드웨어 구성 JSON ZIP · AML |
| Import | XML 파일 · ZIP · POST body 업로드 |
| 라이브러리 | Global Library 열기 · 마스터카피 · 프로젝트 라이브러리 전환 |
| HMI | 태그 테이블 · 텍스트 리스트 · 스크린 · 연결(Connections) — Comfort / Unified |
| 드라이브 | SINAMICS 파라미터 덤프 · 모터 설정 |
| 하드웨어 | 카탈로그 검색 · 호환 모듈 조회 · GSD/GSDML 파싱 · 디바이스 아이템 트리 |
| 진단 | ProDiag 감시(Supervision) 생성 |
| 버전 | `GET /api/dll/versions` · `PUT /api/dll/switch` (Openness 로드 전에만) |

전체 목록과 파라미터는 Swagger UI 또는
[`docs/swagger/openapi.json`](docs/swagger/openapi.json) 에 있다.

---

## 5. TiaInspector 사용법

CPU 한 대를 통째로 읽어 **SimaticML XML + 정규화 JSON + 컴파일-프리 스냅샷**으로 뽑는다.
읽기 전용이라 프로젝트 상태를 바꾸지 않는다.

```powershell
.\bin\Release\TiaInspector.exe        # 런처
.\bin\Release\TiaInspector.exe 20     # 버전 선택 건너뛰고 V20 으로 바로
```

1. 런처가 설치된 TIA 를 스캔한다 — 떠 있는 버전이 굵게 미리 선택된다
2. **① 연결** — 실행 중인 TIA 에 attach(권장) 하거나 `.apXX` 파일을 직접 열기
3. **② CPU 선택**
4. **③ 추출 옵션** 고르고 `추출 시작`

산출물:

```
<출력폴더>/<CPU이름>_<타임스탬프>/
  manifest.json    무엇이 나왔고 무엇이 안 나왔는지 (사유 포함)
  snapshot.json    컴파일-프리 스냅샷
  xml/             SimaticML 원본 — TIA 폴더 구조 그대로
  json/            같은 트리, 구조화 JSON
```

`.apXX` 직접 열기는 대형 프로젝트에서 몇 분 걸린다. `TIA 창 표시` 를 끄면
백그라운드로 열려 더 빠르다. 자세한 건 [`TiaInspector/README.md`](TiaInspector/README.md).

---

## 6. 왜 버전마다 exe 가 갈리나

V19 와 V20 은 Openness 가 `Siemens.Engineering.dll` 단일 어셈블리이고
**어셈블리 이름이 같다.** 그래서 한 번 컴파일해 두면 런타임에 `AssemblyResolve` 로
경로만 바꿔 끼울 수 있다 (`--tia=19`).

V21 은 그 단일 어셈블리가 쪼개졌고 경로도 한 단계 깊어진다.

| | V19 / V20 | V21 (`PublicAPI\V21\net48\`) |
|---|---|---|
| 코어 | `Siemens.Engineering` | `Siemens.Engineering.Base` + `.Step7` |
| HMI | `Siemens.Engineering.Hmi` | `.WinCC` (Comfort/Classic) + `.WinCCUnified` |
| 드라이브 | (코어에 포함) | `.Startdrive` |

**이름이 달라지므로 한 바이너리가 V20 과 V21 을 겸할 수 없다.** 다만
**네임스페이스와 타입 이름은 전 버전 동일**해서 C# 소스는 한 벌을 공유한다 —
버전마다 다른 건 `<Reference>` 목록과 `TIA_Vxx` 심볼뿐이다.

V21 네임스페이스 소속: `Base` = `Siemens.Engineering` · `.Multiuser` · `.Compiler` ·
`.Library*` · `.HW*` / `Step7` = `.SW*` · `.Cax` / `WinCC` = `.Hmi*` /
`WinCCUnified` = `.HmiUnified*` / `Startdrive` = `.MC.Drives*`

**V19 는 따로 빌드하지 않는다.** 소스가 V20 에서 추가된 API
(`MrpInstancesOwner` · `AttributeAccessOptions` · `DriveObjectNumber` ·
`LibraryTypeVersion.ExportAsDocuments` 등)를 쓰기 때문에 V19 레퍼런스로는
컴파일되지 않는다. V19 지원은 **V20 으로 빌드해 런타임에 붙이는** 방식이다.

> **.NET 제약:** 한 프로세스는 같은 어셈블리를 한 번만 로드하고 교체할 수 없다.
> Openness 가 로드된 뒤에는 `/api/dll/switch` 가 409 를 반환한다 — 프로세스를 다시 띄워야 한다.

---

## 7. 자주 걸리는 문제

| 증상 | 원인과 해결 |
|---|---|
| `build.ps1` 이 아무 출력도 없다 | cmd 에서 실행한 것. `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1` |
| `건너뜀: V21 (없는 어셈블리: ...)` | 그 버전 TIA 미설치. `...\Portal V21\PublicAPI\V21\net48\Siemens.Engineering.Base.dll` 존재 확인 |
| `Not connected to TIA Portal` | `/api/connect` 를 먼저 호출. TIA 가 실행 중이어야 한다 |
| Security error / Access denied | 위 2번(그룹 등록 + 재로그인). 그래도 안 되면 관리자 권한 실행 |
| 연결은 되는데 **CPU 0대** | 같은 원인. 예외 없이 `Projects` 가 빈 컬렉션으로 온다 |
| Swagger 에 새 엔드포인트가 없다 | 스펙이 `Program.cs` 에 내장돼 있다. **다시 빌드**해야 반영된다 |
| `MSB3027` (파일 잠김) | 실행 중인 서버를 끈다: `taskkill /f /im TiaApiServer.V21.exe` |
| 포트 충돌 | 다른 포트로: `TiaApiServer.V21.exe 9000` |

빌드·실행 상세는 [`docs/BUILD_AND_RUN.md`](docs/BUILD_AND_RUN.md).

## 8. 저장소 구조

```
TiaApiServer/     Program.cs (단일 파일 서버) · 버전별 csproj · build.ps1
TiaInspector/     src/ (공유 소스 한 벌) · 버전별 csproj · Launcher/ · build.ps1
docs/             BUILD_AND_RUN.md · swagger/ · SimaticML·LAD 분석 노트 · 변환 스크립트
TiaExportBlocks.sln
```

`docs/` 의 `convert_ladder_to_ob.py` · `convert_comment_to_tagtable.py` 는
Mitsubishi GX Works CSV 를 TIA Portal XML 로 바꾸는 변환기다
(주소 매핑 규칙은 `docs/MITSUBISHI_SIEMENS_ADDRESS_MAP.md`).

## 9. 라이선스

[MIT License](LICENSE) · 제3자 고지와 상표 표기는 [NOTICE](NOTICE).

Siemens Openness 어셈블리는 이 저장소에 포함되지 않는다. 프로젝트 파일이
설치된 TIA Portal 경로를 참조할 뿐이며, TIA Portal 은 별도로 라이선스를
취득해 설치해야 한다.
