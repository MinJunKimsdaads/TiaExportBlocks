# TiaInspector

TIA Portal 프로젝트에서 CPU 한 대를 통째로 읽어내는 GUI 분석기.
**Program blocks · Software units · PLC data types · PLC tags** 전부를
SimaticML XML · 구조화 JSON · 컴파일-프리 스냅샷으로 뽑는다.

읽기 전용 도구다. 컴파일을 돌리지 않으므로 프로젝트 상태를 바꾸지 않는다.

---

## 왜 버전마다 프로세스가 따로인가

.NET 은 한 프로세스에 같은 이름의 어셈블리를 **한 번만** 로드할 수 있고,
언로드나 교체가 불가능하다. Openness 를 한 번 물면 그 버전에 묶인다.
해결책은 하나뿐이다 — **버전별로 프로세스를 따로 두고 부모가 프록시한다.**

```
TiaInspector.exe          런처. Openness 를 절대 참조하지 않는다.
  │                      설치 버전을 스캔하고 사용자가 고른 버전의 워커를 띄운다.
  ├─ TiaInspector.V19.exe  ← V19 Openness 전용 빌드
  ├─ TiaInspector.V20.exe  ← V20 전용
  └─ TiaInspector.V21.exe  ← V21 전용
```

런처가 Openness 타입을 한 번이라도 건드리면 그 버전이 런처 프로세스에 묶여
설계가 무의미해진다. 그래서 `TiaInspector.Launcher.csproj` 에는 Siemens 참조가
하나도 없고, 실행 중인 TIA 버전도 프로세스 실행 파일 경로(`Portal V20`)로
판별한다.

### 어셈블리 레이아웃 (실측)

| | 경로 | 어셈블리 |
|---|---|---|
| V19 / V20 | `PublicAPI\V{n}\` | `Siemens.Engineering.dll` (+ `.Hmi.dll`) |
| **V21** | `PublicAPI\V21\net48\` | `Base`(TiaPortal·Project·HW·Compiler) + `Step7`(SW.\*) + `WinCC` + `WinCCUnified` + `Startdrive` |

`Siemens.Engineering.Contract.dll` 은 전 버전 공통으로 `Bin\PublicAPI\` 에 있다.

**네임스페이스와 타입 이름은 V21 에서도 전부 보존됐다.** 그래서 `src/` 는 한
벌뿐이고, 버전마다 달라지는 건 csproj 의 `<Reference>` 목록과
`DefineConstants` 의 `TIA_Vxx` 뿐이다.

타입 차이는 딱 하나 확인됐다 — V19 와 V20 의 `PlcBlock`·`PlcTag`·`PlcTagTable`
은 완전히 동일하고, **V21 의 `PlcBlock` 에만** `Comment` · `Title` ·
`DownloadDate` · `LoadMemoryLength` · `WorkMemoryLength` 가 추가됐다. 공유
소스는 V19 공통 부분집합으로 쓰고, 이 다섯은 `#if TIA_V21` 뒤에 둬서 V21
빌드의 스냅샷만 더 풍부해지게 했다 (`src/Snapshot.cs`).

---

## 빌드

```powershell
.\build.ps1                  # 설치된 버전 전부 + 런처
.\build.ps1 -Versions 20     # V20 만
```

설치돼 있지 않은 버전은 자동으로 건너뛴다 (HintPath 가 깨져 실패하므로).
산출물은 전부 `bin\Release\` 한 폴더에 모인다 — 런처가 형제 파일로 찾는다.

Openness DLL 은 출력 폴더로 복사하지 **않는다** (`<Private>False</Private>`).
런타임에 `OpennessResolver` 가 설치 경로에서 리졸브한다. 복사하면 버전이 섞인다.

---

## 사용

1. `bin\Release\TiaInspector.exe` 실행
2. 버전 선택 — TIA 가 떠 있으면 그 버전이 굵게 미리 선택된다
   (`TiaInspector.exe 20` 처럼 인자를 주면 묻지 않고 바로 실행)
3. **① 연결** — 실행 중인 TIA 에 붙거나(권장), `.apXX` 프로젝트 파일을 직접 연다
4. **② CPU 선택** — 프로젝트의 CPU 목록
5. **③ 추출 옵션** — 산출물 선택 후 `추출 시작`

프로젝트 파일 직접 열기는 대형 프로젝트에서 몇 분 걸린다. `TIA 창 표시` 를
끄면 백그라운드로 열려 더 빠르다.

---

## 산출물

```
<출력폴더>/<CPU이름>_<타임스탬프>/
  manifest.json          무엇이 나왔고 무엇이 안 나왔는지 (사유 포함)
  snapshot.json          컴파일-프리 스냅샷
  xml/                   SimaticML 원본 — TIA 폴더 구조 그대로
    PLC data types/...
    Program blocks/...
    PLC tags/...
    Software units/<유닛>/...
  json/                  같은 트리, 구조화 JSON
```

### snapshot.json — Export 를 한 번도 부르지 않는 경로

객체 모델만 읽는다. 컴파일도, Export 도 없다.

* **태그·상수** — 전량. 이름·타입·주소·주석(다국어)·`External*`·`IsSafety`.
  `PlcTagTable` 에는 `IsConsistent` 자체가 없어 컴파일 게이트와 무관하다.
* **블록·UDT 메타데이터** — 번호·언어·네임스페이스·헤더·메모리 레이아웃과
  7종 타임스탬프, 그리고 **`IsConsistent`**.
* **`summary.inconsistent`** — export 가 막히는 객체 목록. 프로젝트를 건드리지
  않고 "지금 무엇을 뽑을 수 있는지" 먼저 알 수 있다.

여기 **없는 것**: 블록 인터페이스 멤버, DB 멤버와 시작값, 네트워크 로직.
이 셋은 객체 모델에 노출되지 않아 XML export 말고는 길이 없다.

### json/ — XML 을 정규화한 것

SimaticML 의 `AttributeList`/`ObjectList` 래핑을 걷어내고 바로 쓸 수 있는
모양으로 편다.

```json
{
  "kind": "SW.Blocks.FB",
  "name": "CallGeneral",
  "language": "SCL",
  "interface": {
    "Static": [
      { "name": "statClockByteOld", "datatype": "Byte",
        "remanence": "NonRetain",
        "comment": { "en-US": "Stored clock byte ..." },
        "flags": { "ExternalAccessible": true, "SetPoint": false } }
    ]
  },
  "networks": [
    { "index": 1, "networkSource": { "form": "StructuredText", "scl": "IF ... END_IF;" } },
    { "index": 3, "networkSource": { "form": "FlgNet", "graph": { ... } } }
  ]
}
```

* **SCL 은 토큰 스트림에서 소스 텍스트로 복원한다.** `Token`/`Blank`/`NewLine`/
  `Access`/`LineComment` 를 되짚어 사람이 읽는 형태로 되돌린다. 이름 있는
  전역 상수(`<Constant Name="NO_OF_UNITS"/>`)도 값 없는 리터럴과 구분해 처리한다.
* **LAD/FBD 는 정규형이 없다.** `FlgNet` 을 구조 그대로(`graph`) 싣는다 —
  손실은 없지만 해석은 소비자 몫이다.

원본 충실도가 필요하면 나란히 저장된 `xml/` 을 보면 된다.

---

## 컴파일과 Export 의 관계

Openness 가 Export 에서 요구하는 건 **컴파일이 아니라 `IsConsistent`** 다.
그리고 그 값은 컴파일 없이 읽힌다. 그래서 TiaInspector 는 이 순서로 움직인다:

1. `IsConsistent` 스캔 — 프로젝트 무변경, 비용 거의 0
2. `true` 인 것만 export — 이미 컴파일된 프로젝트라면 대부분 여기서 끝난다
3. `false` 인 것은 건너뛰고 `manifest.json` 에 사유를 남긴다

`inconsistent 건너뛰기` 를 끄면 3번에서 export 를 실제로 시도해 Openness 가
던지는 예외 메시지를 그대로 리포트에 기록한다 — 어떤 사유로 막히는지 봐야 할 때
쓴다.

컴파일 에러가 있는 프로젝트라도 에러와 무관한 블록은 정상적으로 나온다.
`CompilerResultMessage` 가 `Path` 를 달고 블록 단위로 귀속되는 것과 같은 결이다.

---

## 알려진 제약

* **know-how 보호 블록** — 메타데이터는 나오지만 코드는 나오지 않는다.
  프로퍼티 읽기만으로 예외가 나는 경우도 있어 개별적으로 감싸 뒀다.
* **Project Server 세션 프로젝트** — `OpenProject` 는 로컬 `.apXX` 만 연다.
  서버 프로젝트는 TIA 에서 먼저 연 뒤 `실행 중인 TIA 에 연결` 로 붙어야 한다.
* **HMI** — 범위 밖이다. CPU 의 PLC 소프트웨어만 다룬다.
* 워커 프로세스는 x64 전용이다 (Openness 가 x64).

---

## 파일 배치

```
TiaInspector/
  build.ps1
  TiaInspector.common.props     세 버전이 공유하는 MSBuild 설정
  TiaInspector.V19.csproj       ─┐ 참조 목록과 TIA_Vxx 만 다르고
  TiaInspector.V20.csproj        │ 컴파일하는 소스는 동일하다
  TiaInspector.V21.csproj       ─┘
  src/
    AppMain.cs                 진입점. 리졸버를 먼저 걸기 위한 NoInlining 분리
    VersionInfo.cs             빌드 대상 버전과 Openness 경로 규칙
    OpennessResolver.cs        AssemblyResolve + 실제 로드된 버전 대조
    OpennessWorker.cs          Openness 전용 STA 스레드 1개
    TiaSession.cs              Attach / OpenProject
    PlcLocator.cs              그룹까지 재귀해 CPU 찾기
    Snapshot.cs                컴파일-프리 스냅샷
    Extractor.cs               XML export + JSON 변환 + 매니페스트
    SimaticJson.cs             SimaticML → JSON, SCL 토큰 복원
    Json.cs                    최소 JSON 직렬화기
    MainForm.cs                GUI
  TiaInspector.Launcher/
    Program.cs                 인자 처리 + 런처 기동
    TiaInstallScanner.cs       설치·실행 중 TIA 탐지 (Openness 미사용)
    LauncherForm.cs            버전 선택 UI
```
