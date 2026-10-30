# 아키텍처

> 월드 데이터, 맵 편집기, 냄새 시스템, 편집/시뮬레이션 모드 분리까지 구현된 기준. 개미·여왕개미는 아직 없다.
> 작업 계획·테스트·성능 측정 계획은 [`PLAN.md`](PLAN.md), 미정 규칙은 [`OPEN_QUESTIONS.md`](OPEN_QUESTIONS.md).

## 1. 프로젝트 구조

```
AntSimulator.sln                 # 전체 (코어 + 테스트 + 벤치마크 + Godot 앱)
Directory.Build.props            # 공통 빌드 설정 (nullable, 경고=오류, 결정적 빌드)
src/AntSim.Core/                 # 순수 C# 시뮬레이션 코어 (net8.0, Godot 참조 없음)
  Worlds/        World, WorldLayer, Chunk, WorldConfig, WorldRules, PassageMap, TileState·열거형
  Editing/       MapEditor(원자적 편집·Undo/Redo), EditOp, EditorTool, EditorStroke, EditorText
  Serialization/ MapSerializer(버전 있는 바이너리 형식), MapFormatException
  Rendering/     ChunkRasterizer, TilePalette (청크 → RGBA 픽셀, Godot 비의존)
  Util/          Fnv1a64(결정적 해시), GridLine(브레젠험)
  Worlds/        + WorldPresets(Empty, StandardNest), World.Clone
  Scent/         ScentSettings(검증), ScentState(종류×레이어 float 이중 버퍼), ScentSystem
  Simulation/    SimulationSession(복제 월드 실험), SimulationHost(계산 스레드)
  Ants/          AntSettings, AntPopulation(SoA), AntSystem(이동), AntMovement(통과·경로 검사), AntSpawner, AntRandom
  Serialization/ + ExperimentSerializer(.antexp), ChecksumStreams
  Rendering/     + HeatmapRasterizer
godot/                           # Godot 4 .NET 프로젝트 (렌더링·입력·UI만)
  project.godot, Main.tscn, AntSim.App.csproj, AntSim.App.sln
  Scripts/ Main(조립 루트·입력), Main.Simulation(시뮬레이션 모드), MapView(청크 텍스처),
           HeatmapView, AntView(MultiMesh), EditorCamera, EditorPanel, SimulationPanel, ModeBar, Main.SmokeTest
tests/AntSim.Core.Tests/         # xUnit, Godot 없이 실행
tools/AntSim.Benchmarks/         # 기준 성능 측정
tools/godot-smoke-test.sh        # Godot 통합 점검 (헤드리스 / xvfb)
```

의존 방향: `AntSim.App(Godot) → AntSim.Core`. 코어는 Godot을 모른다.

## 2. 데이터 모델

### 타일 (속성별 연속 배열)

| 평면(`TilePlane`) | 타입 | 값 |
|---|---|---|
| Terrain | byte | 흙 0, 물 1 |
| Solid | byte | 없음 0, 토양 1, 바위 2 |
| Food | byte | 먹이 수량 |
| Larva | byte | 유충 수량 |

- 청크(64×64)마다 평면별 `byte[4096]`을 둔다(SoA). 1024² 맵은 레이어당 4MB, 2레이어 8MB.
- 맵 크기는 청크의 배수일 필요가 없다(1000×1000 → 16×16 청크, 가장자리 청크는 일부만 유효).
- 불변 조건(`WorldRules.Validate`)
  - 고체 블록이 있으면 먹이·유충 = 0 (독점)
  - 먹이 + 유충 ≤ `MaxQuantityPerTile`(기본 10, 맵 파일에 저장)
  - 열거형 값 범위
- `TileState`는 한 타일의 네 속성을 묶은 값 타입으로, 검증·Undo 기록·정보 조회에 쓴다. 저장소는 SoA 그대로다.

### 통로 (`PassageMap`)

- 타일 속성과 분리된 연결 데이터. 좌표 (x, y)에 통로가 있으면 지상(x, y) ↔ 개미집(x, y)이 양방향으로 이어진다.
- 좌표당 1비트 비트셋(1024² 기준 128KB). 조회 O(1), 순회는 선형 인덱스 오름차순(결정적).
- 통로가 바뀌면 두 레이어의 해당 청크를 더티로 표시한다(렌더링 갱신).

### 냄새

```
ScentTypeSettings { Key, DisplayName, Source(Food|Larva|없음), Diffusion D, PassageDiffusion Dp, Decay k, Emission e }
ScentSettings     { TickSeconds (기본 0.1), Types[] }           // 불변, Validate() 통과해야 적용
ScentState        { [종류][레이어] float[] Current / Next }     // 레이어 전체 연속 배열(W*H)
```

- 냄새 종류 추가 = `ScentTypeSettings` 하나 추가(코드 변경 없음). `Source`가 없으면 타일에서 발생하지 않는다(이후 개미 페로몬용).
- 틱 순서 고정: **확산 → 감쇠 → 발생**.
  1. 확산(이웃당 교환율): `C'ᵢ = Cᵢ + D·dt·Σ(Cⱼ − Cᵢ)` (j = 맵 안의 비고체 4이웃) + 통로 중심이면 `Dp·dt·(C반대 − Cᵢ)` (양쪽 중심 모두 비고체일 때만)
  2. 감쇠: `C ← C·exp(−k·dt)`
  3. 발생: `C ← C + e·dt·수량`
- 고체 칸은 농도 0, 교환 없음. 맵 밖과도 교환 없음(밀폐). 감쇠·발생이 0이면 총량이 보존된다.
- 안정성(비음수) 조건 `(4·D + Dp)·dt ≤ 1`을 틱·계수 변경 시 검증한다. 기본값 0.30. 반올림 오차 대비로 0 미만은 0으로 자른다.
- 지원 범위(감사 F03/F06): 종류 ≤ `ScentSettings.MaxTypes`(256), 키·표시 이름 UTF-8 ≤ `MaxNameUtf8Bytes`(65,536바이트),
  발생 계수 e ≤ `MaxEmission`(1e6), 농도 0 ~ `ScentState.MaxConcentration`(1e30). 설정은 `Validate()`가, 농도는 저장 전
  `ExperimentSerializer.ValidateForSave`와 불러오기가 검사한다. 저장할 수 있는 설정·상태는 같은 구현으로 반드시 다시 불러올 수 있다.
  D = Dp = 0이면 확산을 계산하지 않고 그대로 복사한다(큰 값에서 ∞−∞ = NaN 방지).
- 한 틱 뒤에도 저장 가능(재검증 F03): float 계수 변환·반올림 때문에 안정 조건을 만족해도 상한 근처 값이 몇 ULP 넘을 수 있다
  (예: CFL = 1, 상한 농도에서 +1 ULP). 감쇠·발생 단계에서 0 미만은 0으로, 상한을 64 ULP 이내로 넘은 값은 상한으로 되돌린다.
  그보다 큰 값(지원 범위 밖 입력)은 그대로 두어 저장이 거부된다. 상한 이하 값은 바꾸지 않으므로 정상 범위 결과·해시는 같다.
  이 단계는 `System.Numerics.Vector`로 계산하며 스칼라식과 비트 단위로 같다.
- `ScentSystem`은 청크 `Version`이 바뀐 청크만 고체/수량 정보를 레이어 전체 배열 캐시로 다시 복사한다.

### 히트맵 (단일 / 복합)

| 모드 | 계산 스레드가 복사하는 필드 | 색 |
|---|---|---|
| 숨김 | 없음 | — |
| 단일(음식 또는 유충) | 선택한 1종 | 기존 LUT(보라→빨강→노랑) |
| 복합(음식 + 유충, 이후 페로몬 포함 전 종류) | 전 종류 | 종류별 램프(음식 주황, 유충 보라)를 렌더링 단계에서 합성 |

- 농도 데이터는 합치지 않는다. 스냅샷은 계산 스레드가 만든 복사본이고 렌더러는 읽기만 한다.
- 정규화는 냄새 종류별로 독립: 자동 최대값(종류별 현재 최대) 또는 종류별 고정 최대값, 선형/로그. `HeatmapQuantizer`가
  농도 → 0~255 단계를 계산하며 단일·복합이 같은 양자화를 쓴다(선형은 SIMD).
- 합성(`CompositeHeatmapRasterizer`, 기본 `WeightedAverage`):
  - 색 = Σ wᵢ·colorᵢ / Σ wᵢ, wᵢ = 강도ᵢ^γ (γ = 0.5) → RGB가 두 색 범위를 넘지 않음(오버플로 없음), 약한 냄새도 비율보다 크게 보임
  - 불투명도 = 1 − Π(1 − αᵢ), αᵢ는 강도에 비례(20~235) → 겹칠수록 진하고, 옅은 냄새는 배경을 거의 가리지 않음
  - 한 종류만 있는 칸은 그 램프 색 그대로, 모두 0이면 투명
  - 비교용 `AdditiveClamped`(더한 뒤 255로 자름)도 선택 가능
- 팔레트(`CompositeHeatmapPalette`)는 냄새 키 → 색. 새 페로몬은 예비 색을 자동 배정하며 `Set`으로 교체할 수 있다.
- 계산 스레드는 재생 중 스냅샷을 최대 30Hz로만 게시(명령·선택 변경·일시정지 시 즉시)한다.

### 개미 (단계 A)

- `AntPopulation`: 속성별 연속 배열(SoA). 개미마다 객체·Godot 노드를 만들지 않는다. 순회는 인덱스 순서.
- 한 틱(`AntSystem.Tick`): 개체별 간격(0.5~1초 × 개체 편차)마다 현재 방향 ±30° 안에서 목표 방향 선택 → 최대 회전 속도로 회전 →
  현재 타일 먹이+유충 수량에 따른 속도 배율(100/95/90/85%)로 부드럽게 수렴 → 격자 DDA로 경로의 모든 타일을 검사하며 이동(관통·모서리 틈 방지)
  → 막히면 경계 앞에서 멈추고 열린 방향으로 돌아섬 → 통로 중심 타일에 새로 들어서면 확률적으로 레이어 전환.
- 통과 가능: 흙(먹이·유충 포함) / 불가: 물, 토양, 바위, 맵 밖. 이동 능력(`AntMobility`)은 플래그라 이후 수영 등을 추가할 수 있다.
- 결정성: 개체별 xorshift64* 난수 상태를 배열에 저장. 같은 시드·월드·설정이면 같은 결과(실험 파일 저장 후 이어서 실행해도 동일).
- 시뮬레이션 틱 순서: 냄새 → 개미. Reset은 개미를 같은 시드로 다시 생성한다.
- 표시: 계산 스레드가 개미 위치(이전·현재 틱)를 스냅샷(`AntFrame`)으로 게시하고, Godot `AntView`가 MultiMesh 하나로 그리며 틱 사이를 보간한다.

### 편집 / 시뮬레이션 모드

| | 편집 모드 | 시뮬레이션 모드 |
|---|---|---|
| 월드 | 원본(`Main._world`) | 시작 시 복제(`SimulationSession`) |
| 쓰기 | `MapEditor`(원자적 명령, Undo/Redo) | 계산 스레드의 세션만(현재는 냄새만 변함) |
| 저장 | 맵 파일 `.antmap` (v1, 이전과 같음) | 실험 상태 파일 `.antexp` v2 (설정·틱·시작 월드·현재 월드·냄새·개미 설정·개미 상태). v1 파일도 읽음 |
| 원본 반영 | — | 하지 않음. 편집 모드로 돌아가면 원본 그대로 |

계산 스레드 명령 계약(`SimulationHost`, 감사 F02/F07/F05):
- 상태는 Running → Stopping → Stopped, 또는 계산 스레드 예외 시 Faulted. Running이 아닐 때 들어온 명령은 큐에 넣지 않고
  즉시 실패(Stopped: `ObjectDisposedException`, Faulted: 원인을 담은 `InvalidOperationException`).
- 큐에 들어간 명령은 반드시 완료된다: 실행됨(결과/예외), 종료로 취소(`TaskCanceledException`), 또는 스레드 실패로 실패.
- 실행권: 계산 스레드는 같은 lock 안에서 "상태가 Running인지 확인 + 큐에서 꺼내기"를 한 번에 한다(재검증 F02). 꺼낸 명령만
  끝까지 실행되고, Dispose가 먼저 Stopping으로 바꾸면 남은 명령은 콜백 없이 취소된다.
- 명령이 예외로 끝나도 그때까지 바뀐 세션 상태는 그대로이며 호스트 틱과 다음 스냅샷이 그 상태를 따른다.
- `ResetAsync`는 초기화된 월드 복제와 초기화 세대(`ResetCount`)를 돌려준다. 화면은 이 월드로 바꾸고, 세대가 낮은 스냅샷은 버린다.

파일 로더 검증(감사 F01/E01, 재검증 N01): 크기 필드로 배열을 만들기 전에 실제 남은 바이트를 확인한다. `Load(Stream)`은 레코드 하나만
읽고 체크섬 직후 반환하므로 뒤에 이어진 데이터는 스트림에 남는다. 길이를 알 수 없는(seek 불가) 스트림은 끝까지 미리 읽지 않고,
레코드에 반드시 들어 있어야 하는 최소 길이만큼만 받아서 확인한다(`LookaheadStream`: 4KiB부터 받은 양에 비례해 늘어나는 조각, 할당은
받은 데이터의 약 2배 + 4KiB 이내). 잘못된 매직 값은 첫 8바이트만 읽고 거부한다. 정상 대형 레코드는 검증 구간(맵 본문, 냄새 농도)을
메모리에 받아 둔 뒤 읽으므로, seek 불가 입력의 최대 추가 메모리는 그 레코드 크기 정도다. 실험 파일의 내장 맵은 선언된 구간만 읽는
체크섬 스트림으로 읽는다.

실행 중 임의 편집(실험 도구)은 아직 없다. 추가할 때는 `SimulationHost.RunExclusiveAsync`로 틱 사이에 세션을 바꾸는 방식이 된다.

## 3. 주요 클래스

| 클래스 | 책임 |
|---|---|
| `WorldConfig` | 크기·청크 분할(불변), 좌표 변환 |
| `World` | 레이어 2개 + `PassageMap` + `WorldRules`, 콘텐츠 해시 |
| `WorldLayer` | 청크 격자, 타일 조회/검증된 쓰기, 더티 청크 수집, 행 복사 |
| `Chunk` | 평면별 배열, `Version`/`IsDirty` |
| `MapEditor` | 편집의 단일 진입점. 연산 배치를 임시 상태에 적용해 전부 검증한 뒤 일괄 반영(원자성), Undo/Redo(256단계 + 메모리 상한 64MiB 추정), 스트로크 묶음. 고체 배치(수량 제거·물→흙)와 통로 생성(지상 검사·물→흙·개미집 3×3 굴착)도 각각 하나의 원자적 명령 |
| `WorldPresets` | `Empty`(빈 월드), `StandardNest`(개미집 전부 토양, 새 맵 기본값) |
| `ScentSystem` | 냄새 한 틱(확산 → 감쇠 → 발생) |
| `SimulationSession` | 복제 월드 + 냄새 상태 + 틱. 단일 틱, 초기화, 설정 변경, 상태 해시 |
| `SimulationHost` | 계산 스레드. 재생/일시정지/단일 틱/초기화/설정/임의 작업 명령, 히트맵 스냅샷 게시 |
| `ExperimentSerializer` | 실험 상태 파일 저장/불러오기 |
| `HeatmapRasterizer` | 단일 히트맵: 농도 → RGBA(선형/로그, 자동/고정 최대값, 256단계 색 조회표) |
| `HeatmapQuantizer` | 농도 → 0~255 단계(단일·복합 공용, 선형 SIMD) |
| `CompositeHeatmapRasterizer` | 복합 히트맵: 종류별 정규화 후 색 합성(가중 평균/가산), 램프별 조회표 캐시 |
| `CompositeHeatmapPalette`, `HeatmapRamp` | 냄새 키 → 색 램프(교체·확장 가능) |
| `EditOp` | 데이터로만 된 편집 명령(이후 계산 스레드 명령 큐로 그대로 전달 가능) |
| `EditorStroke` | 클릭·드래그 경로 보간, 스트로크 내 타일당 1회 적용, 실패 집계 |
| `MapSerializer` | 버전 있는 바이너리 형식 저장/불러오기, 손상·규칙 위반 거부 |
| `ChunkRasterizer` | 청크 → RGBA8 픽셀(Godot `Image` 형식과 동일) |
| `MapView` (Godot) | 청크별 `ImageTexture`, 더티 청크만 갱신, 화면↔타일 좌표 변환 |
| `Main` (Godot) | 입력 → 편집 명령, 단축키, 파일 대화상자 |

## 4. 데이터 흐름

```
편집 모드 (메인 스레드)
  입력 → Main → EditorStroke/EditOp → MapEditor(검증·적용·Undo) → World(원본)
  MapView._Process ← CollectDirtyChunks → ChunkRasterizer → 청크 ImageTexture.Update

시뮬레이션 모드
  시작:  World(원본).Clone() → SimulationSession ─넘김─▶ SimulationHost(계산 스레드)
         세션.World.Clone() → 표시용 복제본(메인 스레드 전용, MapView가 그림)
  메인 스레드                                 계산 스레드
  재생/일시정지/단일 틱/초기화 ──명령──▶      고정 dt 틱: 확산 → 감쇠 → 발생
  설정 적용 ──명령(검증)──▶                  다음 틱부터 적용
  실험 저장 ──RunExclusive──▶                틱 사이에 직렬화
  히트맵 종류들·레이어 선택 ──▶              선택 필드들을 스냅샷(풀 재사용)으로 게시(재생 중 ≤30Hz)
  HeatmapView ◀── TryTakeSnapshot ─────────  (메인 스레드는 세션을 직접 읽지 않음)
```

- 재생 속도는 "실제 시간당 몇 틱을 돌릴지"에만 영향을 준다. 결과는 틱 수에만 의존한다(테스트로 검증).
- 계산이 실시간을 못 따라가면 한 번에 최대 64틱까지만 따라잡고 밀린 시간은 버린다.

## 5. 재현성 원칙

- 고정 dt 틱, 틱 번호 기반 진행. 실시간 경과는 "몇 틱을 돌릴지"에만 영향.
- 순회 순서는 항상 배열 순서(행 우선·인덱스 오름차순). `Dictionary` 순회 결과에 의존하지 않는다.
- 난수가 필요해지면 시드 고정 자체 PRNG만 사용(`System.Random` 금지).
- 재현성 범위: **같은 빌드·같은 실행 환경에서 비트 단위로 같은 결과**. 냄새 계산은 단일 스레드, 고정 순서다.
  실험 파일을 저장 후 이어서 실행한 결과 = 끊김 없이 실행한 결과(테스트로 검증).
- `World.ComputeContentHash()`로 상태를 비교한다. 월드 좌표 순서로 계산하므로 청크 크기와 무관하다.
