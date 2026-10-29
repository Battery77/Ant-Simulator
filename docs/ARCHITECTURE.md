# 아키텍처 설계

> 상태: 1단계(월드 데이터 모델) 구현 완료. 나머지 항목은 설계안이며, `OPEN_QUESTIONS.md`의 규칙이 정해지면 구체화한다.

기존 프로젝트 분석 결과: 저장소에는 `README.md`만 있었고 재사용할 코드나 교체할 아키텍처는 없었다.

## 1. 프로젝트 폴더 구조

```
AntSimulator.sln
Directory.Build.props          # 공통 빌드 설정 (nullable, 경고=오류, 결정적 빌드)
src/
  AntSim.Core/                 # 시뮬레이션 코어 (net8.0, Godot 의존성 없음)
    Worlds/                    # World, WorldLayer, Chunk, WorldConfig, 좌표      [1단계 ✅]
    Content/                   # TypeRegistry (타일·블록·냄새 타입 정의)           [1단계 ✅]
    Util/                      # 결정적 해시 등                                   [1단계 ✅]
    Editing/                   # 브러시, 편집 명령, Undo/Redo                      [2단계]
    Scent/                     # ScentField, 확산·감쇠 시스템                      [3단계]
    Simulation/                # 고정 틱 실행기, 시스템 파이프라인, 결정적 난수     [4단계]
    Serialization/             # 맵/실험 저장·불러오기                             [5단계]
  AntSim.Godot/                # Godot 4 .NET 프로젝트 (렌더링·입력·UI만 담당)       [6단계]
tests/
  AntSim.Core.Tests/           # xUnit, Godot 없이 실행
tools/
  AntSim.Benchmarks/           # 기준 성능 측정 (Stopwatch 기반, 의존성 없음)
docs/                          # 설계, 미정 규칙, 로드맵, 벤치마크 기록
```

`AntSim.Core`는 Godot 어셈블리를 참조하지 않는다. Godot 프로젝트가 코어를 `ProjectReference`로 참조하는 단방향 의존이다.
Godot 4.x .NET이 net8.0을 지원하므로 코어도 net8.0을 대상으로 한다.

## 2. 데이터 모델

### 월드 / 레이어 / 청크

| 개념 | 표현 | 비고 |
|---|---|---|
| World | 레이어 2개 + 타입 레지스트리 | 지상(`Surface`), 개미집(`Nest`) |
| WorldConfig | 너비·높이·청크 크기 (불변) | 청크 크기는 8~256의 2의 거듭제곱(기본 64). 맵 크기는 청크 배수일 필요 없음 (예: 1000×1000 → 16×16 청크, 가장자리 청크는 40칸만 유효) |
| WorldLayer | 청크 배열 (행 우선) | 좌표 → 청크 = 시프트, 청크 내 위치 = 마스크 |
| Chunk | `ushort[] tiles`, `ushort[] blocks` (SoA, stride = 청크 크기) | `Version`, `IsDirty`로 변경 추적 |

- **타일 / 블록 해석 (가정, 확인 필요)**: 타일 = 칸의 바닥 재질, 블록 = 그 칸을 점유하는 물체(없으면 0).
  두 값은 같은 칸에 독립적으로 저장된다. 이 해석은 `OPEN_QUESTIONS.md` Q1에서 확정 대상이다.
- 타일/블록 값은 `ushort` 타입 ID(최대 65,536종). 문자열 키 ↔ ID 매핑은 `TypeRegistry`가 담당하며, 등록 순서가 같으면 ID도 같다(재현성).
- 타입 정의(`TypeDefinition`)는 현재 `Id/Key/DisplayName`만 가진다. 통행 가능·굴착 가능 같은 속성은 게임 규칙이므로 규칙 확정 후 추가한다.
- 메모리: 1024×1024 기준 레이어당 타일 2MB + 블록 2MB, 2레이어 합계 약 8MB.

**왜 타일/블록은 청크별 배열인가?** 렌더러 업로드, 편집 Undo 스냅샷, 저장, 변경 감지가 모두 청크 단위로 이뤄진다. 청크(64×64 ushort = 8KB)는 L1 캐시에 들어간다.

### 냄새 (3단계 설계안)

```
ScentField (레이어 하나 × 냄새 종류 하나)
  float[] current   // Width*Height, 행 우선, 레이어 전체 연속 배열
  float[] next      // 이중 버퍼 (확산 계산용)
  ulong   activeChunkMask[]  // 청크별 "0이 아닌 값 존재" 플래그 → 빈 영역 건너뛰기
ScentRegistry: 냄새 종류 정의 (키, 확산 계수, 감쇠 계수 …) — 값은 미정 규칙
```

- 요구사항대로 **냄새 종류별로 레이어 전체를 하나의 연속 배열**로 둔다. 확산 스텐실이 청크 경계 처리 없이 행 단위 SIMD로 돌 수 있다.
- 청크는 냄새에서 "활성 영역" 단위로만 쓴다.
- 이중 버퍼: 각 칸의 다음 값은 이전 버퍼만 읽어 계산 → 행 묶음 병렬화해도 결과가 결정적.
- 기본 수치 모델(후보): 명시적 4-이웃 확산 + 지수 감쇠. `c' = c + D·(Σ이웃 − 4c) − k·c`, 안정 조건 `D ≤ 0.25`.
- 메모리: 1024² float = 4MB × 이중버퍼 2 × 종류 K × 레이어 2 → K=4일 때 64MB.

## 3. 주요 클래스와 인터페이스

구현됨(1단계):

```csharp
sealed class WorldConfig   { Width, Height, ChunkSize, ChunkShift, ChunkMask, ChunksX, ChunksY, InBounds(), ToChunk() }
sealed class World         { Surface, Nest, GetLayer(LayerId), TileTypes, BlockTypes, ComputeContentHash() }
sealed class WorldLayer    { GetTile/SetTile, GetBlock/SetBlock, GetChunk, Chunks, CollectDirtyChunks(), ComputeContentHash() }
sealed class Chunk         { Coord, Origin, ValidWidth/Height, Version, IsDirty, Tiles, Blocks, TileRow(), BlockRow() }
sealed class TypeRegistry  { Register(key, name), Contains(id), Get(id), GetId(key), CreateWithEmpty() }
struct Fnv1a64             // 결정적 해시
```

계획(이후 단계):

```csharp
// 2단계 편집
interface IBrush { void Enumerate(TileCoord center, Action<int,int> visit); }   // 점, 사각형, 원, 선
sealed class EditCommand   // 청크별 변경 전/후 diff를 기록 → Undo/Redo
sealed class EditHistory   { Apply(cmd), Undo(), Redo() }

// 3단계 냄새
sealed class ScentField    { Span<float> Current; void Deposit(x,y,amount); }
sealed class ScentSystem : ISimulationSystem   // 확산·감쇠

// 4단계 시뮬레이션
interface ISimulationSystem { void Tick(in TickContext ctx); }
sealed class SimulationRunner { Accumulate(realDelta) → 고정 dt로 N틱 실행; TickIndex; Seed }
struct DeterministicRandom   // PCG/xoshiro, 시드 고정, System.Random 대신 사용
```

## 4. 시스템 간 데이터 흐름

```
 [Godot 입력/UI] ──편집 명령──▶ EditHistory ──▶ World(타일/블록) ──Dirty 청크──┐
                                                    │                           ▼
                                                    │ (읽기)              [Godot 렌더러]
 [SimulationRunner] 고정 dt 틱 ─▶ ScentSystem ──▶ ScentField ──표시용 복사──▶ (청크 텍스처 갱신)
        ▲                                                                        
 Godot _Process(delta) → runner.Accumulate(delta)   ※ 시뮬레이션은 렌더 프레임과 독립
```

- 쓰기 주체: 편집기(틱 사이에만 적용)와 시뮬레이션 시스템. 렌더러는 읽기만 한다.
- 편집 명령은 큐에 쌓였다가 **틱 경계에서** 적용 → 재현 시 "틱 N에 명령 X" 로그로 동일 실행 재생 가능.
- 렌더러는 `CollectDirtyChunks`로 바뀐 청크만 텍스처에 업로드한다. 타일마다 Node를 만들지 않는다.
- 냄새 오버레이는 선택된 종류 하나만, 필요하면 다운샘플해 업로드한다.

## 5. 예상 성능 병목

측정값은 `BENCHMARKS.md` 참고 (1단계 기준선 측정 완료).

| 영역 | 예상 비용 | 대응 방안 (측정 후 결정) |
|---|---|---|
| 냄새 확산 | 1M칸 × K종 × 2레이어 / 틱. K=4면 틱당 8M칸 갱신, 스칼라 ~1ns/칸이면 ~8ms | 활성 청크만 계산, `System.Numerics.Vector` SIMD, 행 묶음 병렬, 틱당 확산 횟수 조절 |
| 칸 단위 API 호출 | `SetTile` 1회 ~22ns (경계·ID 검사 포함) | 편집기 대량 채우기는 청크 단위 span 연산으로 (2단계) |
| 렌더 업로드 | 전체 맵 1M픽셀 매 프레임 업로드는 낭비 | 더티 청크 텍스처만 갱신, 냄새는 낮은 빈도/해상도 |
| 메모리 | 냄새 종류 수에 비례 (종류당 16MB, 2레이어·이중버퍼) | 종류 수 확정 후 판단, 필요 시 half/ushort 양자화 |
| 결정성 vs 병렬화 | 부동소수점 합산 순서가 바뀌면 결과가 달라짐 | 칸별 계산은 순서 무관한 스텐실만 병렬화, 리덕션은 고정 순서 |
| Undo 메모리 | 큰 브러시 = 많은 칸 기록 | 청크 단위 diff, 이력 상한 |

## 6. 재현성 원칙

- 고정 dt 틱, 틱 번호 기반 진행. 실시간 경과 시간은 "몇 틱을 돌릴지"에만 영향.
- 난수는 시드 고정된 자체 PRNG만 사용 (`System.Random`, `Guid`, `DateTime` 금지).
- 순회 순서는 항상 배열 순서(행 우선). `Dictionary` 순회 결과에 의존하지 않는다.
- `World.ComputeContentHash()`로 상태를 비교한다. 해시는 월드 좌표 순서로 계산하므로 청크 크기와 무관하다.
