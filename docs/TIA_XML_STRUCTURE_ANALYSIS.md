# TIA Portal Openness XML 구조 분석

> 분석 소스: 실제 TIA Portal V20 프로젝트의 Openness Export XML (샘플은 저장소에 포함하지 않음)
> 목적: 미쯔비시 CSV(IL) 템플릿을 지멘스 XML로 포팅하기 위한 구조 파악

---

## 1. 공통 XML 뼈대

모든 블록 타입(OB, FB, FC, DB, TagTable, UDT)은 동일한 외부 구조를 공유한다.

```xml
<?xml version="1.0" encoding="utf-8"?>
<Document>
  <Engineering version="V20" />
  <DocumentInfo>
    <Created>2026-04-07T05:46:37.6582135Z</Created>
    <ExportSetting>WithDefaults</ExportSetting>
    <InstalledProducts>
      <!-- TIA Portal 버전 및 설치된 제품 목록 (Import 시 검증용) -->
      <Product>
        <DisplayName>Totally Integrated Automation Portal</DisplayName>
        <DisplayVersion>V20 Update 4</DisplayVersion>
      </Product>
      <Product>
        <DisplayName>STEP 7 Professional</DisplayName>
        <DisplayVersion>V20 Update 4</DisplayVersion>
      </Product>
      <!-- ... 기타 OptionPackage들 ... -->
    </InstalledProducts>
  </DocumentInfo>

  <!-- 여기에 블록 타입별 실제 내용이 들어감 -->
  <SW.Blocks.OB ID="0">...</SW.Blocks.OB>

</Document>
```

**핵심 포인트:**
- `Engineering version` 은 TIA 버전과 일치해야 함
- `InstalledProducts`는 Import 시 호환성 검증에 사용됨
- 실제 블록 내용은 `<Document>` 바로 아래 하나의 블록 엘리먼트로 존재

---

## 2. OB (Organization Block)

> 소스: `U1_Demo/Program blocks/Startup.xml`
> PackML 로직의 메인 진입점으로 활용 가능

```xml
<SW.Blocks.OB ID="0">
  <AttributeList>
    <AutoNumber>false</AutoNumber>
    <HeaderAuthor />
    <HeaderFamily />
    <HeaderName />
    <HeaderVersion>0.1</HeaderVersion>

    <!-- ★ 인터페이스 정의 (Input/Temp/Constant 섹션) -->
    <Interface>
      <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
        <Section Name="Input">
          <Member Name="LostRetentive" Datatype="Bool" Accessibility="Public" Informative="true">
            <Comment>
              <MultiLanguageText Lang="en-US">True if retentive data are lost</MultiLanguageText>
            </Comment>
          </Member>
        </Section>
        <Section Name="Temp">
          <Member Name="tempIndex" Datatype="Int">
            <Comment>
              <MultiLanguageText Lang="en-US">Index of Unit in the array</MultiLanguageText>
            </Comment>
          </Member>
        </Section>
        <Section Name="Constant" />
      </Sections>
    </Interface>

    <Name>Startup</Name>
    <Namespace>Unit1</Namespace>           <!-- Software Unit 내 네임스페이스 -->
    <Number>1000</Number>                   <!-- OB 번호 -->
    <ProgrammingLanguage>LAD</ProgrammingLanguage>
    <SecondaryType>Startup</SecondaryType>  <!-- OB 유형: Startup, CyclicProgram 등 -->
    <SetENOAutomatically>true</SetENOAutomatically>
  </AttributeList>

  <ObjectList>
    <!-- 블록 코멘트 -->
    <MultilingualText ID="1" CompositionName="Comment">...</MultilingualText>

    <!-- ★ 네트워크 (CompileUnit) 목록 - 래더 로직의 핵심 -->
    <SW.Blocks.CompileUnit ID="3" CompositionName="CompileUnits">
      <!-- 내용은 §6 네트워크 구조 참조 -->
    </SW.Blocks.CompileUnit>

    <SW.Blocks.CompileUnit ID="8" CompositionName="CompileUnits">
      <!-- 추가 네트워크... -->
    </SW.Blocks.CompileUnit>
  </ObjectList>
</SW.Blocks.OB>
```

**OB 주요 속성:**
| 속성 | 설명 | 예시 |
|------|------|------|
| `Name` | OB 이름 | `Startup`, `Main` |
| `Number` | OB 번호 | `1000`, `1` |
| `SecondaryType` | OB 유형 | `Startup`, `ProgramCycle` |
| `ProgrammingLanguage` | 프로그래밍 언어 | `LAD`, `SCL`, `STL` |
| `Namespace` | Software Unit 네임스페이스 | `Unit1` |

**OB SecondaryType 종류:**
- `ProgramCycle` → OB1 (Main)
- `Startup` → OB100 계열
- `TimeError` → OB80
- `CyclicInterrupt` → OB30~38

---

## 3. FB (Function Block)

> 소스: `U1_Demo/Program blocks/CallUnit.xml`
> 상태 유지가 필요한 로직 (PackML 상태머신, 서보 제어 등)

```xml
<SW.Blocks.FB ID="0">
  <AttributeList>
    <Access>Unpublished</Access>           <!-- Published / Unpublished -->
    <AutoNumber>true</AutoNumber>
    <HeaderAuthor>Siemens_FA_SUP</HeaderAuthor>
    <HeaderFamily />
    <HeaderName />
    <HeaderVersion>0.1</HeaderVersion>

    <!-- ★ FB는 Input/Output/InOut/Static/Temp/Constant 6개 섹션 가능 -->
    <Interface>
      <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
        <Section Name="Input">
          <Member Name="safetyReleased" Datatype="Bool" Remanence="NonRetain" Accessibility="Public">
            <AttributeList>
              <BooleanAttribute Name="ExternalAccessible" SystemDefined="true">true</BooleanAttribute>
              <BooleanAttribute Name="ExternalVisible" SystemDefined="true">true</BooleanAttribute>
              <BooleanAttribute Name="ExternalWritable" SystemDefined="true">true</BooleanAttribute>
            </AttributeList>
            <Comment>
              <MultiLanguageText Lang="en-US">Status from safety program</MultiLanguageText>
            </Comment>
          </Member>
        </Section>
        <Section Name="Output">
          <Member Name="acknowledgeSafety" Datatype="Bool" Remanence="NonRetain" Accessibility="Public">
            ...
          </Member>
        </Section>
        <Section Name="InOut">
          <Member Name="globalStatus" Datatype="&quot;LAF_typeGeneralStatus&quot;" Accessibility="Public">
            <!-- UDT 참조 시 &quot; 이스케이프 필요 -->
            <Sections>
              <Section Name="None">
                <Member Name="acknowledge" Datatype="Bool" />
                <Member Name="disableAlarms" Datatype="Bool" />
                ...
              </Section>
            </Sections>
          </Member>
        </Section>
        <Section Name="Static">
          <!-- FB만 가지는 영역 - Instance DB에 저장되는 데이터 -->
          <Member Name="internalState" Datatype="Int" Remanence="NonRetain" .../>
        </Section>
        <Section Name="Temp" />
        <Section Name="Constant" />
      </Sections>
    </Interface>

    <Name>CallUnit</Name>
    <Namespace>Unit1</Namespace>
    <Number>1001</Number>
    <ProgrammingLanguage>LAD</ProgrammingLanguage>
    <SetENOAutomatically>true</SetENOAutomatically>
  </AttributeList>

  <ObjectList>
    <!-- 네트워크 목록 (OB와 동일한 CompileUnit 구조) -->
    <SW.Blocks.CompileUnit ID="..." CompositionName="CompileUnits">
      ...
    </SW.Blocks.CompileUnit>
  </ObjectList>
</SW.Blocks.FB>
```

**FB vs FC 차이점:**
| 구분 | FB | FC |
|------|----|----|
| 태그 | `SW.Blocks.FB` | `SW.Blocks.FC` |
| Static 섹션 | 있음 (Instance DB에 저장) | 없음 |
| Instance DB | 필요 | 불필요 |
| Remanence | 지원 (Retain/NonRetain) | 미지원 |
| 용도 | 상태 유지 로직 | 무상태 로직 |

---

## 4. FC (Function)

> 소스: `U1_Demo/Program blocks/CollectAlarms.xml`
> 무상태 로직 (알람 수집, 주소 계산 등)

```xml
<SW.Blocks.FC ID="0">
  <AttributeList>
    <Access>Unpublished</Access>
    <AutoNumber>true</AutoNumber>
    <HeaderVersion>0.1</HeaderVersion>

    <!-- ★ FC는 Input/Output/InOut/Temp/Constant 5개 섹션 (Static 없음) -->
    <Interface>
      <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
        <Section Name="Input">
          <Member Name="resetAlarms" Datatype="Bool" Accessibility="Public">
            <Comment>
              <MultiLanguageText Lang="en-US">Resets any currently active alarms</MultiLanguageText>
            </Comment>
          </Member>
        </Section>
        <Section Name="Output" />
        <Section Name="InOut">
          <!-- Array[*] = 가변 길이 배열 파라미터 -->
          <Member Name="emStatus" Datatype="Array[*] of &quot;LUC_typeEmFeedback&quot;" Accessibility="Public">
            ...
          </Member>
        </Section>
        <Section Name="Temp">
          <Member Name="tempUpperBound" Datatype="DInt" />
        </Section>
        <Section Name="Constant" />
        <Section Name="Return">
          <Member Name="Ret_Val" Datatype="Void" />
        </Section>
      </Sections>
    </Interface>

    <Name>CollectAlarms</Name>
    <Namespace>Unit1</Namespace>
    <ProgrammingLanguage>LAD</ProgrammingLanguage>
  </AttributeList>

  <ObjectList>
    <!-- CompileUnit 구조는 OB/FB와 동일 -->
  </ObjectList>
</SW.Blocks.FC>
```

---

## 5. DB (Data Block)

### 5.1 GlobalDB (글로벌 데이터 블록)

> 소스: `U1_Demo/Program blocks/Alarms.xml`
> PackML 상태, 설정값, 알람 등 데이터 저장용

```xml
<SW.Blocks.GlobalDB ID="0">
  <AttributeList>
    <Access>Published</Access>
    <AutoNumber>true</AutoNumber>
    <DBAccessibleFromOPCUA>true</DBAccessibleFromOPCUA>
    <DBAccessibleFromWebserver>true</DBAccessibleFromWebserver>
    <HeaderVersion>0.1</HeaderVersion>

    <!-- ★ GlobalDB는 Static 섹션만 사용 -->
    <Interface>
      <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
        <Section Name="Static">
          <Member Name="errors" Datatype="Array[0..3] of &quot;LAF_typeAlarm&quot;"
                  Remanence="NonRetain" Accessibility="Public">
            <AttributeList>
              <BooleanAttribute Name="ExternalAccessible" SystemDefined="true">true</BooleanAttribute>
              <BooleanAttribute Name="ExternalVisible" SystemDefined="true">true</BooleanAttribute>
              <BooleanAttribute Name="ExternalWritable" SystemDefined="true">false</BooleanAttribute>
              <BooleanAttribute Name="SetPoint" SystemDefined="true">false</BooleanAttribute>
            </AttributeList>
            <Comment>
              <MultiLanguageText Lang="en-US">Unit Errors</MultiLanguageText>
            </Comment>
            <!-- 중첩 구조체의 서브 멤버 정의 -->
            <Sections>
              <Section Name="None">
                <Member Name="trigger" Datatype="Bool">
                  <Subelement Path="0">
                    <Comment>
                      <MultiLanguageText Lang="en-US">Error in subordinated Modules</MultiLanguageText>
                    </Comment>
                  </Subelement>
                </Member>
                <Member Name="reaction" Datatype="DInt" />
              </Section>
            </Sections>
          </Member>
        </Section>
      </Sections>
    </Interface>

    <IsOnlyStoredInLoadMemory>false</IsOnlyStoredInLoadMemory>
    <IsRetainMemResEnabled>false</IsRetainMemResEnabled>
    <IsWriteProtectedInAS>false</IsWriteProtectedInAS>
    <MemoryReserve>100</MemoryReserve>
    <Name>Alarms</Name>
    <Namespace>Unit1</Namespace>
    <Number>83</Number>
    <ProgrammingLanguage>DB</ProgrammingLanguage>   <!-- DB 전용 언어 -->
  </AttributeList>

  <ObjectList>
    <MultilingualText ID="1" CompositionName="Comment">...</MultilingualText>
    <MultilingualText ID="3" CompositionName="Title">...</MultilingualText>
  </ObjectList>
</SW.Blocks.GlobalDB>
```

### 5.2 InstanceDB (인스턴스 데이터 블록)

> 소스: `U1_Demo/Program blocks/Instance DBs/InstProDiag.xml`
> FB 호출 시 자동 생성되는 인스턴스 데이터

```xml
<SW.Blocks.InstanceDB ID="0">
  <AttributeList>
    <AutoNumber>true</AutoNumber>
    <DBAccessibleFromOPCUA>true</DBAccessibleFromOPCUA>
    <DBAccessibleFromWebserver>true</DBAccessibleFromWebserver>
    <HeaderVersion>0.1</HeaderVersion>

    <!-- ★ Instance DB 고유 속성 -->
    <InstanceOfName>Unit1.ProDiag</InstanceOfName>   <!-- 원본 FB 이름 -->
    <InstanceOfType>FB</InstanceOfType>               <!-- FB 인스턴스임을 명시 -->

    <Interface>
      <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
        <Section Name="Input" />
        <Section Name="Output" />
        <Section Name="InOut" />
        <Section Name="Static">
          <!-- FB의 Static과 동일한 멤버를 가지며, 실제 값/StartValue 포함 가능 -->
          <Member Name="PC_Flags" Datatype="PC_Flags_V2" Version="1.0"
                  Remanence="NonRetain" Accessibility="Public">
            ...
          </Member>
        </Section>
      </Sections>
    </Interface>

    <Name>InstProDiag</Name>
    <Namespace>Unit1</Namespace>
    <ProgrammingLanguage>DB</ProgrammingLanguage>
  </AttributeList>
</SW.Blocks.InstanceDB>
```

**DB 타입 정리:**
| 타입 | 태그 | 용도 |
|------|------|------|
| GlobalDB | `SW.Blocks.GlobalDB` | 독립 데이터 저장 |
| InstanceDB | `SW.Blocks.InstanceDB` | FB 인스턴스 데이터 |

---

## 6. Tag Table (PLC 태그 테이블)

> 소스: `U1_Demo/PLC tags/Unit1.xml`
> 미쯔비시 Comment CSV에 대응하는 구조

```xml
<SW.Tags.PlcTagTable ID="0">
  <AttributeList>
    <Access>Published</Access>
    <Name>Unit1</Name>                     <!-- 태그 테이블 이름 -->
  </AttributeList>
  <ObjectList>

    <!-- ★ 개별 태그 정의 -->
    <SW.Tags.PlcTag ID="1" CompositionName="Tags">
      <AttributeList>
        <DataTypeName>Bool</DataTypeName>            <!-- 데이터 타입 -->
        <ExternalAccessible>true</ExternalAccessible>
        <ExternalVisible>true</ExternalVisible>
        <ExternalWritable>true</ExternalWritable>
        <LogicalAddress>%Q0.0</LogicalAddress>       <!-- ★ I/O 주소 -->
        <Name>0.000CC1-KE52_Q0.0</Name>              <!-- 태그 이름 -->
      </AttributeList>
      <ObjectList>
        <MultilingualText ID="2" CompositionName="Comment">
          <ObjectList>
            <MultilingualTextItem ID="3" CompositionName="Items">
              <AttributeList>
                <Culture>en-US</Culture>
                <Text>Yellow signal stack light</Text>  <!-- 코멘트 -->
              </AttributeList>
            </MultilingualTextItem>
          </ObjectList>
        </MultilingualText>
      </ObjectList>
    </SW.Tags.PlcTag>

    <!-- 다음 태그... -->
    <SW.Tags.PlcTag ID="4" CompositionName="Tags">
      <AttributeList>
        <DataTypeName>Bool</DataTypeName>
        <LogicalAddress>%Q0.1</LogicalAddress>
        <Name>0.000CC1-KE52_Q0.1</Name>
      </AttributeList>
      ...
    </SW.Tags.PlcTag>

  </ObjectList>
</SW.Tags.PlcTagTable>
```

**태그 주소 형식 (LogicalAddress):**
| 접두어 | 의미 | 미쯔비시 대응 |
|--------|------|--------------|
| `%I` | 디지털 입력 | X |
| `%Q` | 디지털 출력 | Y |
| `%M` | 내부 메모리 (비트) | M |
| `%MW` | 내부 메모리 (워드) | D |
| `%MD` | 내부 메모리 (더블워드) | D (2워드) |
| `%DB` | 데이터 블록 | — (DB로 대체) |

**DataTypeName 종류:**
- 기본: `Bool`, `Byte`, `Word`, `DWord`, `Int`, `DInt`, `Real`, `LReal`, `String`, `WString`
- 시간: `Time`, `LTime`, `DTL`
- UDT 참조: `"LAF_typeAlarm"` (따옴표 이스케이프 `&quot;`)

---

## 7. UDT (User Defined Type / PLC Data Types)

> 소스: `PLC data types/PII - SIMATIC RF260R IO-Link.xml`

```xml
<SW.Types.PlcStruct ID="0">
  <AttributeList>
    <Interface>
      <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
        <Section Name="None">
          <Member Name="PAE -  UID Byte 0" Datatype="Byte">
            <AttributeList>
              <BooleanAttribute Name="ExternalAccessible" SystemDefined="true">true</BooleanAttribute>
              <BooleanAttribute Name="ExternalVisible" SystemDefined="true">true</BooleanAttribute>
              <BooleanAttribute Name="ExternalWritable" SystemDefined="true">true</BooleanAttribute>
              <BooleanAttribute Name="SetPoint" SystemDefined="true">false</BooleanAttribute>
            </AttributeList>
          </Member>
          <Member Name="PAE -  UID Byte 1" Datatype="Byte">...</Member>
          ...
        </Section>
      </Sections>
    </Interface>
    <Name>PII - SIMATIC RF260R IO-Link</Name>
  </AttributeList>
</SW.Types.PlcStruct>
```

---

## 8. 네트워크(CompileUnit) 상세 구조 - LAD

> 래더 프로그램의 핵심. 미쯔비시 래더 CSV에 대응하는 부분.

### 8.1 LAD 네트워크 기본 구조

각 네트워크 = 하나의 `SW.Blocks.CompileUnit`

```xml
<SW.Blocks.CompileUnit ID="3" CompositionName="CompileUnits">
  <AttributeList>
    <NetworkSource>
      <FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5">
        <Parts>
          <!-- 접점, 코일, 함수 블록 등의 요소 정의 -->
        </Parts>
        <Wires>
          <!-- 요소들 간의 연결 (배선) 정의 -->
        </Wires>
      </FlgNet>
    </NetworkSource>
    <ProgrammingLanguage>LAD</ProgrammingLanguage>
  </AttributeList>
  <ObjectList>
    <!-- 네트워크 코멘트 -->
    <MultilingualText ID="4" CompositionName="Comment">
      <ObjectList>
        <MultilingualTextItem ID="5" CompositionName="Items">
          <AttributeList>
            <Culture>en-US</Culture>
            <Text>네트워크 설명</Text>
          </AttributeList>
        </MultilingualTextItem>
      </ObjectList>
    </MultilingualText>
    <!-- 네트워크 타이틀 -->
    <MultilingualText ID="6" CompositionName="Title">
      <ObjectList>
        <MultilingualTextItem ID="7" CompositionName="Items">
          <AttributeList>
            <Culture>en-US</Culture>
            <Text>Unit Index Number</Text>
          </AttributeList>
        </MultilingualTextItem>
      </ObjectList>
    </MultilingualText>
  </ObjectList>
</SW.Blocks.CompileUnit>
```

### 8.2 FlgNet 내부: Parts (요소 정의)

```xml
<Parts>
  <!-- 1. 변수 접근 (Access) -->

  <!-- 글로벌 상수 -->
  <Access Scope="GlobalConstant" UId="21">
    <Constant Name="UNIT01" />
  </Access>

  <!-- 로컬 변수 -->
  <Access Scope="LocalVariable" UId="22">
    <Symbol>
      <Component Name="tempIndex" />
    </Symbol>
  </Access>

  <!-- 글로벌 변수 (DB 멤버 접근) -->
  <Access Scope="GlobalVariable" UId="33">
    <Symbol>
      <Component Name="Unit1.Config" />      <!-- DB 이름 -->
      <Component Name="unit" />              <!-- 구조체 멤버 -->
      <Component Name="referenceDesignator" />
    </Symbol>
  </Access>

  <!-- 리터럴 상수 -->
  <Access Scope="LiteralConstant" UId="47">
    <Constant>
      <ConstantType>DInt</ConstantType>
      <ConstantValue>0</ConstantValue>
    </Constant>
  </Access>

  <!-- 2. 명령어 (Part) -->

  <!-- MOVE 명령 -->
  <Part Name="Move" UId="23" DisabledENO="true">
    <TemplateValue Name="Card" Type="Cardinality">1</TemplateValue>
  </Part>

  <!-- 접점 (NO Contact) -->
  <Part Name="Contact" UId="24" />

  <!-- 접점 (NC Contact - 부정) -->
  <Part Name="Contact" UId="25" Negated="true" />

  <!-- 코일 (Output) -->
  <Part Name="Coil" UId="26" />

  <!-- FB 호출 -->
  <Call>
    <CallInfo Name="LAF_ControlEm" BlockType="FC" />
    <!-- 또는 FB 호출 시 Instance DB 지정 -->
    <CallInfo Name="LAF_SummarizeCommandsToUnit" BlockType="FB">
      <Instance Scope="GlobalVariable" UId="...">
        <Component Name="InstSummarize" />
      </Instance>
    </CallInfo>
  </Call>
</Parts>
```

### 8.3 FlgNet 내부: Wires (배선 정의)

```xml
<Wires>
  <!-- Powerrail → 명령어 EN 핀 (전원선 연결) -->
  <Wire UId="24">
    <Powerrail />
    <NameCon UId="23" Name="en" />
  </Wire>

  <!-- 변수 → 명령어 입력 핀 -->
  <Wire UId="25">
    <IdentCon UId="21" />              <!-- UId=21인 Access 요소 -->
    <NameCon UId="23" Name="in" />     <!-- UId=23인 Part의 "in" 핀 -->
  </Wire>

  <!-- 명령어 출력 핀 → 변수 -->
  <Wire UId="26">
    <NameCon UId="23" Name="out1" />   <!-- Part의 출력 핀 -->
    <IdentCon UId="22" />              <!-- UId=22인 Access 요소 -->
  </Wire>

  <!-- 접점 → 접점 직렬 연결 (AND) -->
  <Wire UId="27">
    <NameCon UId="24" Name="out" />
    <NameCon UId="25" Name="in" />
  </Wire>

  <!-- OR 연결: 같은 목적지에 여러 소스 와이어 -->
  <Wire UId="28">
    <OpenCon UId="30" />               <!-- 분기점 -->
    <NameCon UId="26" Name="in" />
  </Wire>
</Wires>
```

**Wire 연결 타입:**
| 태그 | 의미 |
|------|------|
| `<Powerrail />` | 전원선 (좌측 레일) |
| `<IdentCon UId="N" />` | UId=N인 Access 요소에 연결 |
| `<NameCon UId="N" Name="pin" />` | UId=N인 Part의 특정 핀에 연결 |
| `<OpenCon UId="N" />` | 개방 연결점 (분기/OR) |

### 8.4 SCL 네트워크 구조 (참고)

LAD 대신 SCL로 작성된 네트워크의 경우:

```xml
<NetworkSource>
  <StructuredText xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4">
    <Token Text="IF" UId="24" />
    <Blank Num="1" UId="25" />
    <Access Scope="Call" UId="26">
      <Instruction Name="LEN" UId="27">
        <Token Text="(" UId="28" />
        <Parameter Name="IN" UId="29">
          <Access Scope="GlobalVariable" UId="33">
            <Symbol UId="34">
              <Component Name="Unit1.Config" UId="35" />
              <Token Text="." UId="36" />
              <Component Name="unit" UId="37" />
            </Symbol>
          </Access>
        </Parameter>
        <Token Text=")" UId="43" />
      </Instruction>
    </Access>
    <Token Text="THEN" UId="52" />
    ...
  </StructuredText>
</NetworkSource>
```

---

## 9. UId 체계

모든 `<Parts>`와 `<Wires>` 내 요소는 고유한 `UId`를 가진다.

- UId는 네트워크 내에서 유일해야 함
- 보통 21부터 시작하여 순차 증가
- `<Wire>`에서 `<IdentCon>`, `<NameCon>`이 이 UId를 참조하여 연결
- CompileUnit의 ID는 16진수 (`"3"`, `"8"`, `"D"`, `"12"` ...)

---

## 10. Interface 섹션 정리

### 블록 타입별 지원 섹션

| Section | OB | FB | FC | GlobalDB | InstanceDB |
|---------|----|----|----|---------|-----------| 
| Input | O | O | O | - | (from FB) |
| Output | - | O | O | - | (from FB) |
| InOut | - | O | O | - | (from FB) |
| Static | - | O | - | O | O |
| Temp | O | O | O | - | - |
| Constant | O | O | O | - | - |
| Return | - | - | O | - | - |

### Member 공통 속성

```xml
<Member Name="varName"
        Datatype="Bool"              <!-- 데이터 타입 -->
        Remanence="NonRetain"        <!-- NonRetain / Retain / SetInIDB -->
        Accessibility="Public"       <!-- Public / Unpublished -->
        Informative="true">          <!-- 읽기 전용 (OB Input 등) -->
  <AttributeList>
    <BooleanAttribute Name="ExternalAccessible" SystemDefined="true">true</BooleanAttribute>
    <BooleanAttribute Name="ExternalVisible" SystemDefined="true">true</BooleanAttribute>
    <BooleanAttribute Name="ExternalWritable" SystemDefined="true">true</BooleanAttribute>
    <BooleanAttribute Name="SetPoint" SystemDefined="true">false</BooleanAttribute>
  </AttributeList>
  <Comment>
    <MultiLanguageText Lang="en-US">설명</MultiLanguageText>
  </Comment>
  <!-- 초기값 설정 -->
  <StartValue>0</StartValue>
</Member>
```

---

## 11. 미쯔비시 → 지멘스 매핑 가이드

### Comment CSV → Tag Table XML

| 미쯔비시 CSV 컬럼 | 지멘스 XML 대응 |
|-------------------|----------------|
| `Key` | `<Name>` (태그 이름) |
| `Device` (M/D/T/L) | `<LogicalAddress>` 접두어 (%M/%MW/%MD) |
| `Address` | `<LogicalAddress>` 주소값 |
| `Comment` | `<MultiLanguageText>` 코멘트 |

### 디바이스 타입 변환

| 미쯔비시 | 지멘스 | 비고 |
|----------|--------|------|
| M (비트) | %M0.0 ~ | 비트 주소로 변환 필요 |
| D (워드) | %MW100 ~ | 워드 주소 |
| T (타이머) | IEC 타이머 FB | 태그가 아닌 FB로 구현 |
| L (래치) | %M (Retain) | Retain 속성 설정 |

### Ladder CSV → CompileUnit XML

| 미쯔비시 IL | 지멘스 LAD XML |
|-------------|---------------|
| `LD M100` | `<Part Name="Contact" />` + Powerrail Wire |
| `AND M101` | `<Part Name="Contact" />` + 직렬 Wire |
| `OR M102` | 병렬 Wire 구조 |
| `ANI M103` | `<Part Name="Contact" Negated="true" />` |
| `OUT M200` | `<Part Name="Coil" />` |
| `MOVP K10 D100` | `<Part Name="Move" />` + Wire 연결 |
| `LD= D100 K0` | `<Part Name="Eq" />` (비교 명령) |

### PackML 상태머신 포팅 전략

```
미쯔비시 (CSV 래더)           →  지멘스 (XML)
─────────────────────────────────────────────
EM_COMMENT_Test.csv           →  Tag Table XML (PackML 상태 태그)
  또는                        →  GlobalDB XML (구조체로 관리)
EM_PackML_Test.csv (래더)     →  OB/FB XML (PackML 상태머신 로직)
EM_Step_Start/N/End.csv       →  FC XML (스텝 래더 로직)
Unit_COMMENT_Test.csv         →  Tag Table XML (Unit 레벨 태그)
```

---

## 12. 최소 템플릿 예제

### 12.1 빈 FC (LAD) 템플릿

```xml
<?xml version="1.0" encoding="utf-8"?>
<Document>
  <Engineering version="V20" />
  <DocumentInfo>
    <Created>2026-04-08T00:00:00Z</Created>
    <ExportSetting>WithDefaults</ExportSetting>
    <InstalledProducts>
      <Product>
        <DisplayName>Totally Integrated Automation Portal</DisplayName>
        <DisplayVersion>V20 Update 4</DisplayVersion>
      </Product>
      <Product>
        <DisplayName>STEP 7 Professional</DisplayName>
        <DisplayVersion>V20 Update 4</DisplayVersion>
      </Product>
    </InstalledProducts>
  </DocumentInfo>
  <SW.Blocks.FC ID="0">
    <AttributeList>
      <AutoNumber>true</AutoNumber>
      <HeaderAuthor />
      <HeaderFamily />
      <HeaderName />
      <HeaderVersion>0.1</HeaderVersion>
      <Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
  <Section Name="Input" />
  <Section Name="Output" />
  <Section Name="InOut" />
  <Section Name="Temp" />
  <Section Name="Constant" />
  <Section Name="Return">
    <Member Name="Ret_Val" Datatype="Void" />
  </Section>
</Sections></Interface>
      <Name>MyFunction</Name>
      <Number>1</Number>
      <ProgrammingLanguage>LAD</ProgrammingLanguage>
      <SetENOAutomatically>true</SetENOAutomatically>
    </AttributeList>
    <ObjectList>
      <MultilingualText ID="1" CompositionName="Comment">
        <ObjectList>
          <MultilingualTextItem ID="2" CompositionName="Items">
            <AttributeList>
              <Culture>en-US</Culture>
              <Text />
            </AttributeList>
          </MultilingualTextItem>
        </ObjectList>
      </MultilingualText>
      <MultilingualText ID="3" CompositionName="Title">
        <ObjectList>
          <MultilingualTextItem ID="4" CompositionName="Items">
            <AttributeList>
              <Culture>en-US</Culture>
              <Text />
            </AttributeList>
          </MultilingualTextItem>
        </ObjectList>
      </MultilingualText>
    </ObjectList>
  </SW.Blocks.FC>
</Document>
```

### 12.2 Tag Table 템플릿

```xml
<?xml version="1.0" encoding="utf-8"?>
<Document>
  <Engineering version="V20" />
  <DocumentInfo>
    <Created>2026-04-08T00:00:00Z</Created>
    <ExportSetting>WithDefaults</ExportSetting>
    <InstalledProducts>
      <Product>
        <DisplayName>Totally Integrated Automation Portal</DisplayName>
        <DisplayVersion>V20 Update 4</DisplayVersion>
      </Product>
      <Product>
        <DisplayName>STEP 7 Professional</DisplayName>
        <DisplayVersion>V20 Update 4</DisplayVersion>
      </Product>
    </InstalledProducts>
  </DocumentInfo>
  <SW.Tags.PlcTagTable ID="0">
    <AttributeList>
      <Name>MyTagTable</Name>
    </AttributeList>
    <ObjectList>
      <SW.Tags.PlcTag ID="1" CompositionName="Tags">
        <AttributeList>
          <DataTypeName>Bool</DataTypeName>
          <ExternalAccessible>true</ExternalAccessible>
          <ExternalVisible>true</ExternalVisible>
          <ExternalWritable>true</ExternalWritable>
          <LogicalAddress>%M0.0</LogicalAddress>
          <Name>State_Undefine</Name>
        </AttributeList>
        <ObjectList>
          <MultilingualText ID="2" CompositionName="Comment">
            <ObjectList>
              <MultilingualTextItem ID="3" CompositionName="Items">
                <AttributeList>
                  <Culture>en-US</Culture>
                  <Text>State UNDEFINE</Text>
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
        </ObjectList>
      </SW.Tags.PlcTag>
    </ObjectList>
  </SW.Tags.PlcTagTable>
</Document>
```

---

## 13. 주의사항

1. **ID 자동 할당**: Import 시 `ID` 속성은 TIA가 재할당하므로 연속적이면 됨
2. **UId 유일성**: 네트워크 내 `UId`는 반드시 유일해야 Import 성공
3. **네임스페이스**: `<Namespace>` 는 Software Unit 이름과 일치해야 함
4. **UDT 참조**: `&quot;TypeName&quot;` 형태로 이스케이프. 해당 UDT가 프로젝트에 먼저 존재해야 함
5. **InstalledProducts**: TIA 버전이 맞지 않으면 Import 거부될 수 있음
6. **인코딩**: 반드시 `UTF-8` (미쯔비시 CSV의 UTF-16LE와 다름)
7. **타이머 차이**: 미쯔비시 T 디바이스 → 지멘스는 IEC 타이머 FB(TON, TOF, TP) 사용
