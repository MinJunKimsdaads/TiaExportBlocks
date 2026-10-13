# TiaApiServer - 빌드 및 실행 가이드

## 사전 요구사항

- **Windows 10/11** (x64)
- **TIA Portal V19 / V20 / V21** 중 쓰려는 버전 설치
- **.NET Framework 4.8** (Windows 10 1903+ 기본 포함)
- **Visual Studio 2022** (MSBuild 포함)
- Windows 그룹 **`Siemens TIA Openness`** 에 사용자 등록 (미등록 시 `new TiaPortal()` 이 Security error)

---

## 버전별 빌드 — 왜 exe 가 2개인가

V19/V20 은 `PublicAPI\V{n}\Siemens.Engineering.dll` 로 **어셈블리 이름이 같아서**
한 바이너리가 `AssemblyResolve` 로 둘을 갈아끼울 수 있다.

V21 은 그 단일 어셈블리가 쪼개졌고 경로도 한 단계 깊어진다
(`PublicAPI\V21\net48\`). 이름이 달라지므로 **한 바이너리가 V20 과 V21 을 겸할 수 없다.**
그래서 빌드를 버전별로 나눈다. `Program.cs` 는 한 벌을 공유한다 —
네임스페이스와 타입 이름은 전 버전 동일해서 코드는 손댈 것이 없고, 달라지는 건
`<Reference>` 목록과 `TIA_Vxx` define 뿐이다.

| 빌드 | 참조하는 Openness 어셈블리 | 커버하는 TIA |
|---|---|---|
| **V20** | `Siemens.Engineering` · `.Hmi` · `.Contract` | **V19 + V20** (`--tia=19` 로 V19) |
| **V21** | `.Base` · `.Step7` · `.WinCC` · `.WinCCUnified` · `.Startdrive` · `.Contract` | V21 |

> **V19 는 따로 빌드하지 않는다.** `Program.cs` 가 V20 에서 추가된 API
> (`MrpInstancesOwner` · `AttributeAccessOptions` · `DriveObjectNumber` ·
> `LibraryTypeVersion.ExportAsDocuments` · `LibraryTypeComposition.CreateFromDocuments` 등)를
> 쓰기 때문에 V19 레퍼런스로는 컴파일되지 않는다. V19 지원은 예나 지금이나
> **V20 으로 빌드해 V19 에 런타임으로 붙이는** 방식이다.

V21 네임스페이스 소속: `Base` = `Siemens.Engineering` · `.Multiuser` · `.Compiler` · `.Library*` · `.HW*` /
`Step7` = `.SW*` · `.Cax` / `WinCC` = `.Hmi*` / `WinCCUnified` = `.HmiUnified*` / `Startdrive` = `.MC.Drives*`

---

## 빌드

### 방법 1: build.ps1 (권장)

설치된 버전만 골라 빌드한다. 없는 버전은 건너뛴다.

**PowerShell 에서:**
```powershell
cd <저장소>\TiaApiServer
.\build.ps1
```

**cmd 에서:** (`.\build.ps1` 만 치면 cmd 는 PS 스크립트를 실행하지 못하고 조용히 넘어간다)
```
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

옵션:
```powershell
.\build.ps1 -Versions 21            # V21 만
.\build.ps1 -Configuration Debug
```

출력: `TiaApiServer\bin\Release\TiaApiServer.V20.exe` / `.V21.exe`

### 방법 2: MSBuild 직접

```
"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" TiaApiServer.V21.csproj /p:Configuration=Release
```

> `TiaExportBlocks.sln` 에는 `TiaApiServer.V20` · `TiaApiServer.V21` 과 TiaInspector 4개
> 프로젝트가 모두 등록돼 있다. Visual Studio 에서 솔루션을 빌드해도 버전별 exe 가 나온다.
> 단, 설치되지 않은 TIA 버전의 프로젝트는 레퍼런스를 못 찾아 실패하므로 터미널에서는
> 설치된 버전만 골라 빌드하는 `build.ps1` 이 편하다.

> `MSB3027`(파일 잠김) 이 나면 실행 중인 서버를 먼저 끈다:
> `taskkill /f /im TiaApiServer.V21.exe`

---

## 실행

**TIA Portal 을 먼저 띄우고 프로젝트를 연 다음**, 그 버전에 맞는 exe 를 실행한다.

```
.\bin\Release\TiaApiServer.V21.exe          # 기본 포트 8099
.\bin\Release\TiaApiServer.V21.exe 9000     # 포트 변경
.\bin\Release\TiaApiServer.V20.exe 8099 --tia=19   # V19 에 붙기
```

V20 exe 는 V19/V20 둘 다 지원하므로 `--tia=` 또는 `TIA_VERSION` 환경변수로 대상을 고를 수 있다.
V21 exe 는 V21 전용이라 인자가 필요 없다. 기동 배너에 대상 버전과 경로가 찍힌다.

| 엔드포인트 | 설명 |
|---|---|
| `GET /api/dll/versions` | 설치/실행/대상/로드된 버전 한눈에 |
| `PUT /api/dll/switch?version=19` | 대상 버전 전환 (**Openness 로드 전에만**, V19↔V20) |

> **.NET 제약:** 한 프로세스는 같은 어셈블리를 한 번만 로드하고 교체할 수 없다.
> Openness 가 로드된 뒤에는 `/api/dll/switch` 가 409 를 반환한다 — 프로세스를 다시 띄워야 한다.

---

## 접속

| URL | 용도 |
|---|---|
| `http://localhost:8099/` | Swagger UI — 전체 API 목록 + Try it out |
| `http://localhost:8099/explorer` | **CPU Explorer** — 트리에서 체크박스로 골라 XML export |

### 연결 순서 (이걸 안 하면 전부 404/Not connected)

```
GET  /api/processes      -> 실행 중인 TIA Portal 목록
POST /api/connect        -> 첫 인스턴스에 연결 (?processId= 로 지정 가능)
GET  /api/status         -> 연결 상태 확인
```

---

## CPU Explorer (선택 Export)

`/api/plc/tree` 가 PLC 의 폴더 트리를 돌려주고, 각 leaf 의 `path` 가 곧 **선택 ID 이자
ZIP 엔트리 경로**다. 그 배열을 `/api/plc/export/selected` 에 POST 하면 그것만 담긴 ZIP 이 온다.

```
GET  /api/plc/tree?plc=PLC_1
POST /api/plc/export/selected?plc=PLC_1[&compile=true]
     body: {"paths":["PLC_1/Program blocks/Main.xml","PLC_1/PLC data types/MyUdt.xml"]}
```

- 경로 규칙과 export 순서(타입 → DB → 태그 → 코드블록)는 `/api/plc/export/xml` 과 동일하다.
  받은 ZIP 을 `/api/plc/import/xml` 로 그대로 되돌릴 수 있다.
- ZIP 안 `_export_report.json` 에 `requested / success / failed / notFound`.
  **notFound** = 요청했는데 산출되지 않은 경로(이름 변경 또는 export 실패).
- 트리 leaf 의 `info.consistent:false` 인 블록은 export 가 실패한다.
  Explorer 에서 주황 `inconsistent` 뱃지로 보이며, `compile=true` 로 자동 해결을 시도할 수 있다.
- **Technology objects 는 아직 트리에 없다.** 별도 엔드포인트를 쓸 것:
  `/api/plc/techno-objects`, `/api/plc/techno-objects/export/zip`

---

## 문제 해결

### build.ps1 을 실행했는데 아무 출력도 없다
cmd 에서 `.\build.ps1` 을 친 경우다. cmd 는 PS 스크립트를 실행하지 못한다.
`powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1` 로 실행할 것.

### `건너뜀: V21 (없는 어셈블리: ...)`
그 버전 TIA 가 설치돼 있지 않거나 PublicAPI 구성이 다르다. 경로 확인:
`C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48\Siemens.Engineering.Base.dll`

### "Not connected to TIA Portal"
`/api/connect` 를 먼저 호출한다. TIA Portal 이 실행 중이어야 하며,
없으면 `/api/portal/new` 로 새 인스턴스를 시작할 수 있다.

### 스웨거에 새 엔드포인트가 안 보인다
서버 바이너리가 옛날 것이다. 스펙은 `Program.cs` 에 내장돼 있어 **다시 빌드해야** 반영된다.
(`docs/swagger/openapi.json` 은 파일 사본으로, `docs/swagger/index.html` 로 따로 볼 수 있다.)

### API 호출 시 "Access denied" / Security error
- Windows 그룹 `Siemens TIA Openness` 에 사용자 등록 후 **로그아웃/재로그인** (그룹 토큰은 로그인 시 확정)
- 실행 파일 단위 AllowList 등록: `TiaInspector\register-openness.ps1` (관리자 권한)
- 그래도 안 되면 서버를 관리자 권한으로 실행

### 포트 충돌
다른 포트로 실행: `TiaApiServer.V21.exe 9000`
