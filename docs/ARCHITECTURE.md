# 아키텍처

> Phase 1(월드 데이터 구조 + 맵 편집기) 구현 완료 기준. 냄새·시뮬레이션 스레드는 설계안이다.
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
  Scent/         [Phase 2] ScentRegistry, ScentField, ScentSystem
  Simulation/    [Phase 3] SimulationHost(계산 스레드), 명령 큐, 고정 틱
godot/                           # Godot 4 .NET 프로젝트 (렌더링·입력·UI만)
  project.godot, Main.tscn, AntSim.App.csproj, AntSim.App.sln
  Scripts/ Main(조립 루트·입력), MapView(청크 텍스처), EditorCamera, EditorPanel, Main.SmokeTest
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

### 냄새 (Phase 2 설계)

```
ScentDefinition { Key, SourcePlane(Food|Larva), EmissionPerUnit, DecayRate, Diffusion, PassageDiffusion }
ScentField (냄새 종류 × 레이어) { float[] Current, float[] Next }   // 레이어 전체 연속 배열(W*H)
ScentSettings { TickSeconds = 0.1, 종류별 계수 … }                    // 실행 중 변경 가능, 검증 후 적용
```

- 냄새 종류 추가 = `ScentDefinition` 등록 하나(필드·시스템 코드 변경 없음).
- 틱 순서: **확산 → 감쇠 → 발생**. 확산은 Current만 읽고 Next에 쓴 뒤 교환(이중 버퍼).
- 고체 칸은 농도 0이며 이웃과 플럭스를 교환하지 않는다. 맵 경계 밖 이웃도 플럭스 0(밀폐 경계 → 감쇠·발생이 없으면 총량 보존).
- 레이어 간 이동은 통로 좌표에서만 일어난다.
- 냄새 배열은 청크와 무관한 레이어 전체 배열이므로 청크 경계에서도 같은 공식이 적용된다(연속성).
- 비음수 보장 조건: `(4·D + Dp)·dt ≤ 1`, `k·dt ≤ 1`. 설정 적용 시 검증한다. 초기값(0.6, 0.6, 0.025, dt 0.1)은 이웃당 교환율 해석 기준 0.30, 0.0025로 만족한다(해석은 Q1 확인 필요).

## 3. 주요 클래스

| 클래스 | 책임 |
|---|---|
| `WorldConfig` | 크기·청크 분할(불변), 좌표 변환 |
| `World` | 레이어 2개 + `PassageMap` + `WorldRules`, 콘텐츠 해시 |
| `WorldLayer` | 청크 격자, 타일 조회/검증된 쓰기, 더티 청크 수집, 행 복사 |
| `Chunk` | 평면별 배열, `Version`/`IsDirty` |
| `MapEditor` | 편집의 단일 진입점. 연산 배치를 임시 상태에 적용해 전부 검증한 뒤 일괄 반영(원자성), Undo/Redo, 스트로크 묶음 |
| `EditOp` | 데이터로만 된 편집 명령(이후 계산 스레드 명령 큐로 그대로 전달 가능) |
| `EditorStroke` | 클릭·드래그 경로 보간, 스트로크 내 타일당 1회 적용, 실패 집계 |
| `MapSerializer` | 버전 있는 바이너리 형식 저장/불러오기, 손상·규칙 위반 거부 |
| `ChunkRasterizer` | 청크 → RGBA8 픽셀(Godot `Image` 형식과 동일) |
| `MapView` (Godot) | 청크별 `ImageTexture`, 더티 청크만 갱신, 화면↔타일 좌표 변환 |
| `Main` (Godot) | 입력 → 편집 명령, 단축키, 파일 대화상자 |

## 4. 데이터 흐름

```
Phase 1 (현재: 편집만, 단일 스레드)
  입력 이벤트 ─▶ Main ─EditorStroke/EditOp─▶ MapEditor ─검증·적용─▶ World
                                                                   │ 더티 청크
  MapView._Process ◀────────────── CollectDirtyChunks ◀───────────┘ → ChunkRasterizer → ImageTexture.Update

Phase 3 (계획: 시뮬레이션 스레드 분리)
  Godot 메인 스레드                         계산 스레드 (SimulationHost)
  입력 → EditOp ──(명령 큐)──────────────▶  틱 경계에서 MapEditor.Apply
  재생/정지/단일 틱 ──(제어 큐)──────────▶  고정 dt 0.1s 틱: 확산 → 감쇠 → 발생
  히트맵 텍스처 ◀──(완료된 스냅샷 교환)───  틱 후 표시용 버퍼 게시
```

- Phase 3부터 월드와 냄새 필드는 계산 스레드만 쓴다. 메인 스레드는 명령을 넣고, 게시된 스냅샷만 읽는다.
- 편집 명령이 데이터(`EditOp`)라 큐로 넘기기 쉽고, "틱 N에 명령 X" 기록으로 실험을 재생할 수 있다.

## 5. 재현성 원칙

- 고정 dt 틱, 틱 번호 기반 진행. 실시간 경과는 "몇 틱을 돌릴지"에만 영향.
- 순회 순서는 항상 배열 순서(행 우선·인덱스 오름차순). `Dictionary` 순회 결과에 의존하지 않는다.
- 난수가 필요해지면 시드 고정 자체 PRNG만 사용(`System.Random` 금지).
- `World.ComputeContentHash()`로 상태를 비교한다. 월드 좌표 순서로 계산하므로 청크 크기와 무관하다.
