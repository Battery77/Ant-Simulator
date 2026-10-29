# Ant-Simulator PR #2 — 독립 결함 재현·검증 결과

세 항목 모두 .NET 8 Debug/Release에서 재현했다. **T1은 최소 TestHook을 삽입한 별도 복사본의 실행**, **T2·T3는 PR #2 원본 코드의 직접 실행**이다. 프로덕션 수정이나 수정안 적용은 하지 않았다. 기존 267개 테스트 통과는 아래 세 결함의 부재를 뜻하지 않는다.

## A. 검증 환경

- 저장소: https://github.com/Battery77/Ant-Simulator / PR: https://github.com/Battery77/Ant-Simulator/pull/2
- 검사 SHA: **`da05632d1c0a3cbbe0acd569f805ba7f6bd34376`**.
- GitHub `refs/pull/2/head`를 시작·종료 시 `git ls-remote`로 확인했다. 두 번 모두 위 SHA이며 첨부 보고서 기준과 일치한다. 기준 이후 추가 커밋은 없다. PR 상태/Checks는 이번 검사 대상이 아니며 Git 참조로 코드 정체성을 확인했다.
- `main`: `2bc4056ca542811215c2012cdae5d1eef721d10c`. PR #3 코드는 참조·혼합하지 않았다.
- SDK **8.0.427**, .NET runtime **8.0.33**, MSBuild 17.11.69, Debian GNU/Linux 13.6 (trixie), Linux x64.
- 기존 테스트: `source/tests/AntSim.Core.Tests/AntSim.Core.Tests.csproj`, **Debug 267 통과 / 0 실패 / 0 건너뜀**. `logs/baseline.log`, `logs/baseline.trx`에 원시 결과 보존. 기존 전체 테스트의 Release 실행은 하지 않았다.
- 신규 재현: 외부 콘솔 하네스 `harness/Repro.csproj`, **Debug·Release 모두 실행**. 외부 테스트 패키지 없이 실제 Core 프로젝트를 참조한다. 관찰을 검증하는 Assert가 있어 예상 증거를 얻지 못하면 종료 코드 1이다. 마지막 `ALL OBSERVATION ASSERTIONS PASSED`는 **결함 재현 조건을 확인했다**는 뜻이며 제품이 정상이라는 뜻이 아니다.
- 원본 `/workspace/Ant-Simulator`: 브랜치 `work`, HEAD는 위 main SHA, 작업 전후 clean. 브랜치를 전환하지 않았다.
- 격리 체크아웃: `/workspace/pr2-verification/source`, PR #2 SHA에 detached HEAD. 소스·기존 테스트·의존성 선언 변경 없음.
- T1 전용 복사본: `hook-source/`. 해당 SHA의 `git archive HEAD src Directory.Build.props`로 만들었으며 `SimulationHost.cs`의 동기화 호출 **한 줄만 추가**했다. `hook.patch`와 `logs/source-integrity.json`으로 구분한다. Git 브랜치/커밋을 만들지 않았다.
- 원문 보고서는 검증 가설로만 사용했다. 수치 판단은 실제 C# 실행 결과이며 Python 대체 수치 계산을 사용하지 않았다. Python은 파일 복사·무결성 비교·하네스 작성에만 사용했다.

기존 테스트 명령(격리 체크아웃에서):

```bash
source /workspace/.ant-tools/env.sh
cd /workspace/pr2-verification/source
dotnet test tests/AntSim.Core.Tests/AntSim.Core.Tests.csproj -c Debug \
  --logger 'trx;LogFileName=baseline.trx' \
  --results-directory /workspace/pr2-verification/logs
```

전체 신규 재현 실행:

```bash
bash /workspace/pr2-verification/run.sh
```

각 실행에는 외부 `timeout 20` watchdog을 적용했다. 스레드 이벤트와 Task 종결 확인은 5초 제한이며 `finally`에서 모든 차단 이벤트를 해제한다. 이벤트가 실행 순서를 결정하고 상태는 `_gate`를 획득해 확인한다. `Thread.Sleep()`으로 경쟁 조건을 유도하지 않았다. `SpinWait.SpinUntil`은 제한 시간 내 명시적 상태/Task 종결 관찰용이다.

## B. 결과 요약

| 검증 항목 | 결과 | 실행 재현 여부 | 근거 |
|---|---|---|---|
| T1 / F02 | **재현** | 예 — 임시 훅 복사본 | `Stopping` 확인 시 callback 0회·Task 대기. 재개 후 callback 1회, `_disposed=true`, Task `RanToCompletion`, Dispose 완료 |
| T2 / F03 | **재현** | 예 — 원본 | 유효 입력 첫 Save/Load 성공 → Step 1회 → `0x7149F2CB` (상한 `0x7149F2CA` +1 ULP) → 두 번째 Save가 범위 오류로 거부 |
| T3 / N01 | **재현** | 예 — 원본 | non-seek 첫 Load가 두 레코드를 모두 소비. 두 번째 Load EOF 오류. 정상 checksum 이후 추가 Read 및 잘못된 magic 거부 전 전체 읽기 확인 |

첨부 보고서의 핵심 T1~T3 가설은 이번 실행과 일치했다. 단, T3의 공개 API는 “연속 레코드를 지원한다”는 명시적 XML 계약까지 갖추고 있지는 않다. 아래에 문서·구현·실행 증거를 구분한다.

## C. 항목별 상세 결과

아래 소스 위치는 모두 검사 SHA의 원본 기준이다. `source/` 아래에서 확인할 수 있고 GitHub 고정 링크의 기준은 `https://github.com/Battery77/Ant-Simulator/blob/da05632d1c0a3cbbe0acd569f805ba7f6bd34376/`이다.

### T1 / F02 — 종료 후 아직 대기 중이던 명령 실행

**보고서 주장:** `_disposed` 확인과 dequeue가 서로 다른 동기화 경계에 있어 종료 전환 후 미시작 명령을 실행할 수 있다.

**실제 코드:** `src/AntSim.Core/Simulation/SimulationHost.cs` 192행/250행에 “실행 중 명령은 완료, 미시작 명령은 취소”가 명시되어 있다. 253–264행의 Dispose는 `_gate` 안에서 `_disposed=true`, `Stopping`으로 전환한 후 worker Join을 기다린다. DrainCommands의 355행 while 조건은 lock 밖이고 358–360행 lock 안에서는 상태를 재검사하지 않는다. 362행에서 Execute한다.

**임시 변경:** 원본 357행 직전, 즉 while 조건 통과 후 `_gate` 획득 전에 아래 한 줄만 추가했다.

```csharp
TestHook?.Invoke("before-dequeue"); // LOCAL REPRO ONLY: after disposed check, before gate
```

기존 internal volatile `TestHook` 필드를 재사용했다. 하네스는 reflection으로 hook을 설정하고 `_gate`/`_state`/`_disposed`를 관찰한다. 종료 상태나 큐를 직접 수정하지 않는다. 훅은 해당 구간에서 worker를 잠시 멈출 뿐 lock 위치·상태 검사·dequeue·callback 실행 코드를 바꾸지 않는다. 따라서 원본에서 가능한 스케줄을 강제하지만 **무수정 바이너리에서 동일 스케줄을 직접 강제한 결과로 표현하지 않는다**.

**입력/순서:** 5×5, 1종 세션을 생성한다. 먼저 실행 중인 준비 callback을 이벤트로 붙잡아 다음 callback이 확실히 큐에 들어가게 한다. 훅을 설정하고 준비 callback을 풀면 worker가 다음 while 검사를 통과한 직후 멈춘다. 목표 callback 0회와 Task 미완료를 확인하고 별도 Task에서 Dispose한다. `_gate` 아래에서 `Stopping`을 확인한 뒤 worker를 재개한다.

**실행:**

```bash
cd /workspace/pr2-verification
source /workspace/.ant-tools/env.sh
dotnet build hook-harness/Repro.csproj -c Debug
timeout 20 dotnet hook-harness/bin/Debug/net8.0/Repro.dll t1-hook
# Release는 -c Release 및 bin/Release로 동일 실행
```

**실제 결과 — Debug/Release 동일:**

```text
before release: state=Stopping, callbackCount=0,
 target=WaitingForActivation, dispose=Running
after: first=RanToCompletion, callbackCount=1,
 disposedAtCallback=True, target=RanToCompletion,
 dispose=RanToCompletion, state=Stopped, failure=
```

- 목표 callback은 Stopping 전환 당시 아직 dequeue 전이었다. “실행권을 먼저 얻은 명령”으로 해석할 여지가 없다.
- 기대: 목표 callback 0회, Task Canceled, Dispose 완료.
- 실제: 목표 callback 1회, Task RanToCompletion, Dispose 완료. 무한 미완료 결함을 주장하지 않는다.
- 원본 코드 정상 대조(`harness ... t1-control`): 이미 실행 중인 준비 callback을 붙잡은 상태에서 Dispose하고 대기 명령을 둔다. 준비 callback은 RanToCompletion, 대기 callback 0회·Task Canceled, Dispose RanToCompletion. 즉 종료 전에 이미 실행 중인 명령을 완료하는 정상 계약은 이 대조에서 유지됐다.
- 결론: **종료와 미시작 명령 실행권 획득 사이 경쟁 조건 재현**. 테스트는 `harness/Program.cs:28`, 훅용 복사본도 같은 코드다. 로그 `logs/t1-hook-{Debug,Release}.log`, `logs/t1-control-{Debug,Release}.log`.

### T2 / F03 — 한 틱 뒤 지원 농도 상한 초과와 저장 실패

**보고서 주장:** 허용 농도와 CFL 경계 입력이 float 연산 후 상한보다 1 ULP 커져 저장할 수 없다.

**실제 경로:** `Scent/ScentSettings.cs:30,110`은 double CFL을 검사한다. `Scent/ScentSystem.cs:57–58`에서 계수를 float로 바꾸고, 103행에서 네 이웃 flux를 적용한 뒤 120–122행에서 통로 교환을 더한다. `Scent/ScentState.cs:19–27`이 지원 농도 상한과 검사를 정의하며 `Serialization/ExperimentSerializer.cs:97–105`에서 Save 전에 검사한다.

**입력:** 양쪽 레이어가 모두 열린 5×5 월드, `(2,2)` 통로 1개, 종류 1개. `dt=1,D=.012,Dp=.952,k=e=0`, Source=null. 지상 중심 0, 지상 네 이웃과 지하 중심은 `1e30f`, 나머지 0. 원본 World 생성자가 양쪽 레이어를 빈 비고체로 만든다. `TrySetPassage` 성공도 확인했다.

**실행:** `dotnet build harness/Repro.csproj -c Debug` 후 `timeout 20 dotnet harness/bin/Debug/net8.0/Repro.dll t2`. Release도 동일 방식. 테스트 `harness/Program.cs:50`, 로그 `logs/t2-{Debug,Release}.log`.

**실제 결과:**

1. Validate 오류 0, double CFL 출력 `1`.
2. 모든 초기 농도 지원 범위 안.
3. 첫 Save 성공(782바이트), Load 성공, `ComputeStateHash()` 동일.
4. 복원한 실제 `SimulationSession.Step()` 1회, Tick=1.
5. 지상 중심 결과와 상한:

| 값 | C# float의 비트 | 정확한 float 값을 double로 표시(G17) |
|---|---|---|
| 상한 | `0x7149F2CA` | `1.0000000150474662E+30` |
| Step 이후 중심 | `0x7149F2CB` | `1.0000000906053299E+30` |

계산은 실제 ScentSystem의 float 연산이다. double 캐스팅은 이미 계산된 float 값을 자세히 출력하기 위한 용도뿐이다. 비트 차이 **+1 ULP**, 값은 유한하지만 `AllValuesInSupportedRange=False`다.

6. 두 번째 Save에서 `InvalidOperationException: 저장할 수 없는 냄새 농도가 있습니다(음수·NaN·무한대 또는 1e30 초과).` 발생. 출력 스트림 길이는 0. Save가 실패했으므로 경계 반례의 **두 번째 Load는 실행되지 않았다**.

**소규모 대조 — Debug/Release 동일, 모두 Step 후 Save/Load 및 상태 해시 확인 성공:**

| 변경 조건 | CFL | 중심 float | 비트 | 지원 범위 |
|---|---:|---|---|---|
| Dp=.951999 | 0.9999990000000001 | 9.99999E+29 | 0x7149F2BD | true |
| 통로 없음(계수 동일) | 1 | 4.8000003E+28 | 0x6F1B18AC | true |
| 초기 고농도 위치를 모두 1f로 | 1 | 1 | 0x3F800000 | true |

기대는 허용 입력이 한 틱 뒤에도 지원 상태와 저장 가능성을 유지하는 것이다. **원본 대상에서 재현 성공**이다. NaN/Infinity 여부로 대체 판정하지 않았다. 추가 dt=.1, 장시간 실행, 다른 CPU 아키텍처는 검사하지 않았다.

### T3 / N01 — non-seek 레코드 경계 초과 읽기

**공개 계약 조사:** 두 공개 `Load(Stream)`은 일반 Stream을 받으며 CanSeek=true만 요구하거나 EOF까지 반드시 소유한다는 명시적 선행조건이 없다. 형식 문서는 checksum을 마지막 필드로 정의한다. 단, “여러 레코드를 연결해 읽을 수 있다”는 별도 명시 보증도 찾지 못했다. 현재 seek 경로는 checksum에서 반환하고 다음 레코드를 보존하며, 기존 회귀 테스트도 non-seek 입력을 지원 대상으로 사용한다.

이전 PR #2 SHA `cc5d416acdad01654486df5b2e98b077b72ebfc9`와의 **코드 diff**에서 EnsureKnownLength 선행 호출이 추가됐음을 확인했다(`logs/loader-history.diff`). 이전 코드는 바로 ChecksumReader를 생성하고 checksum까지 파싱한다. **이전 SHA 바이너리 실행 비교는 하지 않았다**. 따라서 과거 동작은 코드 분석 근거이고, 현재 seek/non-seek 차이는 실행 근거다.

**대상 위치:** `Serialization/StreamLimits.cs:30–42`의 EOF까지 Read/복사 루프, `MapSerializer.cs:82`(magic 읽기 86행보다 먼저), `ExperimentSerializer.cs:125`(magic 읽기 129행보다 먼저). 내부 맵용 ChecksumSectionStream은 known remaining 경로이므로 이 문제는 외부 non-seek 입력에서 발생한다.

**하네스:** `harness/Program.cs:90` 및 `Watched:117`. CanSeek=false, Length/Position은 NotSupportedException, 읽기 전용, Read(byte[],...)와 Read(Span<...>)를 모두 감시한다. 실제 반환 바이트·소비 위치·요청 크기·호출 횟수를 기록한다. 각 데이터는 정상 Save로 생성하며 서로 다른 월드/실험 상태 해시를 검사한다.

실행: `timeout 20 dotnet harness/bin/Debug/net8.0/Repro.dll t3` (빌드 절차는 T2와 동일, Release도 실행). 로그 `logs/t3-{Debug,Release}.log`.

**A/B — 연속 레코드, Debug/Release 동일:**

| 형식 | A 크기 | B 크기 | 첫 non-seek Load의 실제 소비 | Read 횟수 | 두 번째 Load |
|---|---:|---:|---:|---:|---|
| antmap | 237 | 241 | **478** | 2 (데이터 1 + EOF 1) | MapFormatException: 파일이 예상보다 짧습니다. |
| antexp | 774 | 782 | **1556** | 2 (데이터 1 + EOF 1) | 동일 |

첫 Load 반환값은 A와 해시가 일치한다. 그러나 B까지 전부 소비됐다. 두 번째 Load에서 추가 Read 1회 후 EOF 오류(누적 3회). 같은 바이트를 seek 가능한 MemoryStream으로 읽으면 A 경계 237/774에서 정확히 반환하고, 두 번째 B도 정확한 해시로 복원된다.

대표 Read trace:

```text
map: at=0 request=81920 returned=478; at=478 request=81920 returned=0
exp: at=0 request=81920 returned=1556; at=1556 request=81920 returned=0
```

**C — checksum 이후 감시:** 첫 레코드 끝까지만 읽어 주고 그 이후 Read는 IOException을 던지는 래퍼를 사용했다. 첫 읽기에서 A 전체를 제공했는데도 두 번째 Read를 요구했다. map 소비 237, exp 소비 774, 양쪽 모두 Read=2, 경계 이후 시도=1, IOException. 정상 레코드가 완성되어 있어도 EOF를 확인하려는 추가 읽기 때문에 Load가 반환하지 못하는 경로를 확인했다. 실제 무한 대기 네트워크는 만들지 않았고 이 결과로 실제 네트워크 지연 시간을 주장하지 않는다.

**D — 잘못된 magic:** 4096바이트 0 데이터(첫 8바이트부터 잘못된 magic)를 생성했다. 양쪽 non-seek 로더 모두 4096바이트를 전부 읽고 EOF까지 확인한 뒤 MapFormatException의 magic 오류를 던졌다(Read=2). seek 대조는 8바이트만 소비하고 거부했다. 정확한 표현은 **magic을 검사하기 전에 전체 읽기**이며, magic 오류를 이미 인식한 뒤 읽기를 계속했다는 뜻은 아니다.

기대는 레코드 경계를 보존하고 초기 헤더 오류를 조기에 거부하는 것이다. **레코드 과잉 소비 및 검사 전 전체 버퍼링 재현 성공**. 메모리 증가의 규모·2GiB 상한 효과·실제 열린 파이프의 무한 대기는 실행하지 않았다. 일반 FileStream UI 경로 전체가 실패한다는 주장도 하지 않는다.

## D. 추가 회귀 확인

- 기존 원본 테스트 전체 Debug: 267/267 통과. 이 안에 F01/E01, 호스트 종료/실패, 정상 golden 등 기존 테스트가 포함된다. 기존 테스트를 수정하거나 제외하지 않았다.
- 신규 T1 정상 대조: 진행 중 callback은 완료하고 대기 명령은 취소. Debug/Release 모두 확인.
- 신규 T2: 유효 초기 상태 왕복 및 3개 대조 조건의 한 틱 후 왕복·상태 해시 일치.
- 신규 T3: seek 가능한 연속 정상 map/experiment 2개를 각각 복원하고 해시·위치 확인.
- 신규 작은 방어 입력(`harness/Program.cs:69`, 모드 `regression`): Debug/Release 각각 seek/non-seek으로 실행. 관리 힙 할당량은 `GC.GetAllocatedBytesForCurrentThread` 차이이며 RSS나 프로세스 전체 메모리가 아니다.

| 사례 | 실제 입력 크기 | 거부 이유 | seek / non-seek 관리 할당 바이트 |
|---|---:|---|---:|
| 첫 내장 맵 길이 1,500,000,000 선언 | 88 | 필요한 길이에 비해 남은 바이트 0 | 1176 / 83696 |
| 정상 작은 실험의 끝 16바이트 제거 | 758 | 농도+checksum 필요 208, 남은 192 | 69184 / 152208 |
| 16384×16384 선언, 맵 헤더만 | 25 | 본문 필요 2147483660, 남은 0 | 760 / 83280 |

모두 MapFormatException이며 각 할당 1MiB 미만 조건을 통과했다. 거대한 실제 배열/파일을 생성하지 않았다. F01/E01 방어는 위 실행 범위에서 유지된다. Godot/UI 실행, 광범위 성능 감사, 수정 전 SHA의 위험한 대형 입력 실행은 하지 않았다.

## E. 수정 담당자에게 전달할 결론

### T1

- 최소 조건: callback이 큐에 남아 있을 때 worker가 종료 조건을 통과하고 dequeue lock 전에 멈춤 → Dispose가 Stopping 전환 → worker 재개.
- 원인 지점: SimulationHost.cs 355–362행, 상태 검사와 실행권 획득이 하나의 원자적 결정이 아님.
- 계약: Dispose 전 실행권을 획득한 명령만 끝까지 실행. 아직 큐에 있던 명령은 callback 0회·Task Canceled. 모든 접수 Task는 종결.
- 수정 후 회귀 기준: 본 결정적 순서에서 callback 0회/Canceled/Dispose 완료. 반대 순서인 이미 실행 중 명령은 정상 또는 callback 예외로 종결하고 공개 Tick 동기화 유지. worker 실패·새 요청 거부·중복 Dispose 기존 테스트 유지.
- 권장 검토: 동일 `_gate` 안에서 상태와 실행권을 함께 판단하는 방향. 이번 작업에서 구현하지 않았다.

### T2

- 최소 조건: 위 5×5, 중심 통로, 상한 농도 다섯 칸과 CFL=1 설정.
- 원인 지점: ScentSystem의 float 계수 변환과 확산 후 별도 통로 덧셈의 반올림; 저장 상한 검사는 별도로 엄격함.
- 계약: 지원 입력을 수용했다면 Step 이후도 저장 가능한 상태여야 한다. 지원 범위를 바꾸는 방식이면 입력 단계와 Save/Load 문서·기존 파일 정책을 일관되게 정의해야 한다.
- 회귀 기준: 실제 Save→Load→Step→Save→Load가 성공하고 모든 값이 지원 범위 유지. 또는 명시된 정책에 따라 최초 입력에서 일관되게 거부하며 기존 파일 보존. Debug/Release, 통로 없음·낮은 농도·인접 계수 및 기존 golden/보존성 검사를 유지.
- 주의: 넓은 clamp로 정상 농도·총량 보존·재현성 해시를 임의 변경하지 않도록 검토. 수정안은 작성하지 않았다.

### T3

- 최소 조건: 두 작은 정상 레코드를 붙인 CanSeek=false 스트림, 또는 잘못된 magic 뒤에 작은 본문.
- 원인 지점: 두 Load 진입부의 EnsureKnownLength와 EOF 기반 버퍼링.
- 계약/정책: 공개 Stream API가 하나의 레코드를 읽는다면 checksum 경계에서 반환하고 후속 데이터 보존. 전체 EOF 입력만 받는 새 API가 필요하다면 기존 동작과 구분하여 명시적으로 설계해야 한다.
- 회귀 기준: map/experiment 모두 A·B 두 번 Load 성공, 첫 Load의 소비 위치=A 끝, checksum 이후 강제 EOF 확인 읽기 없음, 잘못된 magic 조기 거부. F01/E01의 선언 길이·잘린 본문 선할당 방어 유지.
- 주의: 기존 antmap v1/antexp v1, seek 파일, 내장 맵의 길이·체크섬 검증을 유지해야 한다. 단순히 길이 검증을 제거해 이전 선할당 취약점을 되살리지 말 것.

현재 하네스는 **현재 결함의 관찰값을 확인**하므로, 수정 후 제품 회귀 테스트로 채택할 때는 각 Assert를 위 올바른 계약에 맞게 바꿔야 한다. 재현 하네스의 종료 코드 0을 수정 완료 기준으로 그대로 사용하면 안 된다.

## F. 최종 상태 및 전달 파일

작업 루트: `/workspace/pr2-verification/`.

- `REPORT.ko.md`: 이 보고서.
- `harness/Program.cs`, `harness/Repro.csproj`: 원본 대상 T1 대조·T2·T3·작은 방어 입력.
- `hook-harness/`: T1 훅 복사본만 참조하는 동일 하네스.
- `hook-source/`, `hook.patch`: 한 줄 훅이 추가된 분리 소스와 정확한 diff.
- `source/`: PR #2 검사 SHA의 무수정 detached checkout.
- `run.sh`: Debug/Release 빌드와 모드별 watchdog 실행.
- `logs/`: 기존 테스트 TRX, 신규 원시 출력, 환경, 원본 Git 전후 상태, PR 참조 전후, 과거 loader diff, 소스 무결성 기록.
- 전달 아카이브: `/workspace/pr2-verification-evidence.tar.gz` (Git 메타데이터와 bin/obj를 제외한 소스·하네스·보고서·로그).

`source`의 `git diff`는 빈 파일(`logs/target.diff`), working tree clean이다. 원본 `/workspace/Ant-Simulator`의 작업 전후 브랜치/HEAD/상태 기록은 바이트 단위로 동일하다. `hook-source`는 Git checkout이 아닌 독립 복사본으로 원본 대비 변경 파일이 SimulationHost.cs 하나임을 확인했다. 생성한 빌드 출력은 해당 분리 경로의 bin/obj에 있다.

**커밋·Push·PR 생성·병합·리베이스 없음. 프로덕션 버그 수정 없음. 환경 초안 변경·게시 없음.** 이 보고서의 실행 결론은 명시된 SHA 및 .NET/Linux x64 환경에 한정한다.
