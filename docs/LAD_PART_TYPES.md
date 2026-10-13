# TIA Portal LAD XML Part 종류

OB에서 LAD(Ladder Diagram)로 프로그래밍할 때 `<Part Name="...">` 에 사용되는 Part 종류 정리.

> 실제 TIA Portal Export XML에서 추출한 데이터 기준 (원본 프로젝트는 저장소에 포함하지 않음).

## 접점 (Input)

| Part Name | 설명 | Mitsubishi 대응 |
|-----------|------|-----------------|
| `Contact` | 일반 접점 (a접점, `Negated` 속성으로 b접점) | LD, LDI, AND, ANI, OR, ORI |
| `PContact` | 상승 에지 접점 (P트리거) | LDP, ANDP, ORP |
| `NContact` | 하강 에지 접점 (N트리거) | LDF, ANDF, ORF |
| `PBox` | 상승 에지 박스 (RLO 기반, 메모리 비트 필요) | MEP |
| `O` | OR 분기 (병렬 연결) | ORB |

### Contact의 Negated 속성

```xml
<!-- a접점 (NO) -->
<Part Name="Contact" UId="22" />

<!-- b접점 (NC) -->
<Part Name="Contact" UId="22">
  <Negated Name="operand" />
</Part>
```

## 출력 (Output)

| Part Name | 설명 | Mitsubishi 대응 |
|-----------|------|-----------------|
| `Coil` | 일반 코일 | OUT |
| `SCoil` | 셋 코일 (래치 ON) | SET |
| `RCoil` | 리셋 코일 (래치 OFF) | RST |
| `PCoil` | 상승 에지 코일 | - |
| `Rs` | RS 플립플롭 | SET/RST 조합 |

## 비교 연산

| Part Name | 연산자 | 설명 | Mitsubishi 대응 |
|-----------|--------|------|-----------------|
| `Eq` | == | 같다 | LD=, AND=, OR= |
| `Ne` | != | 다르다 | LD<>, AND<>, OR<> |
| `Gt` | > | 크다 | LD>, AND>, OR> |
| `Ge` | >= | 크거나 같다 | LD>=, AND>=, OR>= |
| `Lt` | < | 작다 | LD<, AND<, OR< |
| `Le` | <= | 작거나 같다 | LD<=, AND<=, OR<= |

### 비교 연산 XML 예시

```xml
<Access Scope="GlobalVariable" UId="21">
  <Symbol><Component Name="D100" /></Symbol>
</Access>
<Access Scope="LiteralConstant" UId="22">
  <Constant>
    <ConstantType>Int</ConstantType>
    <ConstantValue>0</ConstantValue>
  </Constant>
</Access>
<Part Name="Eq" UId="23">
  <TemplateValue Name="Card" Type="Cardinality">2</TemplateValue>
</Part>
```

## 이동 / 변환

| Part Name | 설명 | Mitsubishi 대응 |
|-----------|------|-----------------|
| `Move` | 값 이동 | MOV, MOVP, DMOV, DMOVP |
| `S_Move` | 문자열 이동 | - |
| `Convert` | 타입 변환 (Int↔Real 등) | - |

### Move XML 예시

```xml
<Part Name="Move" UId="32">
  <TemplateValue Name="Card" Type="Cardinality">2</TemplateValue>
</Part>
```

- `DisabledENO="true"` 속성: ENO(Enable Out) 비활성화

## 산술 연산

| Part Name | 연산 | 설명 | Mitsubishi 대응 |
|-----------|------|------|-----------------|
| `Add` | + | 덧셈 | +, +P, D+ |
| `Sub` | - | 뺄셈 | -, D- |
| `Mul` | × | 곱셈 | *, D* |
| `Div` | ÷ | 나눗셈 | /, D/ |
| `Calc` | 수식 | 복합 수식 계산 | - |
| `Inc` | ++ | 1 증가 | INC |

## 논리(비트) 연산

| Part Name | 연산 | 설명 |
|-----------|------|------|
| `And` | AND | 비트 AND |
| `Xor` | XOR | 비트 XOR |
| `Shr` | >> | 비트 우측 시프트 |

## 제어

| Part Name | 설명 | Mitsubishi 대응 |
|-----------|------|-----------------|
| `Jump` | 레이블로 점프 | CJ |
| `ReturnFalse` | FALSE 리턴 | - |
| `ReturnValue` | 값 리턴 | - |

## 타이머 / 카운터 (시스템 FB)

| Part Name | 설명 | 비고 |
|-----------|------|------|
| `TON` | 온딜레이 타이머 | 인스턴스 DB 필요 |
| `TOF` | 오프딜레이 타이머 | 인스턴스 DB 필요 |
| `TP` | 펄스 타이머 | 인스턴스 DB 필요 |
| `CTU` | 업 카운터 | 인스턴스 DB 필요 |
| `CTD` | 다운 카운터 | 인스턴스 DB 필요 |

> 타이머/카운터는 FB이므로 `<Part>` 가 아닌 `<Call>` 로 호출될 수도 있음.

## 시스템 / 라이브러리 블록 (프로젝트에서 발견된 것)

| Part Name | 설명 |
|-----------|------|
| `Program_Alarm` | 프로그램 알람 |
| `DeviceStates` | 디바이스 상태 조회 |
| `ModuleStates` | 모듈 상태 조회 |
| `RDREC` | 데이터 레코드 읽기 |
| `GET_DIAG` | 진단 정보 조회 |
| `Get_IM_Data` | I&M 데이터 조회 |
| `RT_INFO` | 런타임 정보 |
| `GEO2LOG` / `LOG2GEO` | 지리/논리 주소 변환 |
| `Normalize` / `Scale_X` / `UNSCALE` | 스케일링 |
| `SCATTER` / `GATHER` | 바이트 분산/수집 |
| `Chars_TO_Strg` / `Strg_TO_Chars` | 문자 변환 |
| `CONCAT` / `MID` / `LEFT` / `LEN` / `FIND` | 문자열 처리 |
| `MAX_LEN` | 문자열 최대 길이 |
| `Upper_Bound` / `Lower_Bound` | 배열 범위 |
| `T_CONV` / `S_CONV` / `S_COMP` | 타입/안전 변환 |
| `HTA` | Hex to ASCII |
| `WR_LOC_T` / `RD_LOC_T` | 로컬 시간 읽기/쓰기 |
| `FillBlockI` / `MoveBlockI` | 블록 채우기/이동 |
| `GetChecksum` / `GetSMCinfo` / `Get_Name` | 시스템 정보 |
| `Ident_Profile` | 식별 프로파일 |

## 요약

| 카테고리 | 종류 수 | 비고 |
|----------|---------|------|
| 접점 | 5 | Contact, PContact, NContact, PBox, O |
| 출력 | 5 | Coil, SCoil, RCoil, PCoil, Rs |
| 비교 | 6 | Eq, Ne, Gt, Ge, Lt, Le |
| 이동/변환 | 3 | Move, S_Move, Convert |
| 산술 | 6 | Add, Sub, Mul, Div, Calc, Inc |
| 논리/비트 | 3 | And, Xor, Shr |
| 제어 | 3 | Jump, ReturnFalse, ReturnValue |
| **순수 LAD 합계** | **31종** | |
| 시스템 FB/FC | 다수 | 프로젝트에 따라 다름 |
