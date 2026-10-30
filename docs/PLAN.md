# 작업 계획 (2차 명세 기준)

## 1. 기존 시스템 분석 결과

이전 단계(1단계)에서 만든 코드:

| 구성 | 상태 | 판단 |
|---|---|---|
| `WorldConfig` (크기·청크 분할, 비배수 크기 지원) | 명세와 일치 | **그대로 재사용** |
| `World` / `WorldLayer` / `Chunk` (청크 격자, 더티 추적, Version) | 구조는 일치, 저장 속성이 다름 | **수정** |
| `TypeRegistry` (문자열 키 ↔ ushort ID) | 지형·블록이 고정 종류로 확정되어 불필요 | **삭제** |
| `Fnv1a64`, 콘텐츠 해시 | 재현성 검증에 계속 필요 | **재사용** |
| 테스트 45개, 벤치마크 도구 | 대부분 새 모델로 이식 가능 | **수정·확장** |
| Godot 프로젝트 | 없음 | 신규 |

## 2. 명세와의 차이

| 명세 | 기존 | 조치 |
|---|---|---|
| 지형 흙/물, 고체 토양/바위, 수량 먹이/유충 | 범용 `ushort` 타일 1개 + 블록 1개 | 속성별 평면 4개(byte)로 교체 |
| 먹이·유충 공존, 합계 ≤ 10 | 블록 1칸 1개 | Food/Larva 평면 분리, `WorldRules` 검증 |
| 고체 블록 독점 | 규칙 없음 | `WorldRules`에서 검사 |
| 통로를 별도 연결 데이터로 | 없음 | `PassageMap`(비트셋) 신규 |
| 원자적 편집, Undo/Redo, 드래그 | 없음 | `MapEditor`, `EditorStroke` 신규 |
| 버전 있는 맵 저장 | 없음 | `MapSerializer` v1 신규 |
| Godot 편집기 | 없음 | `godot/` 신규 |
| 냄새, 재생/정지/단일 틱, 히트맵 | 없음 | Phase 2~3 |

## 3. 변경 파일과 이유 (Phase 1)

수정
- `Worlds/Chunk.cs` — ushort 타일/블록 2개 배열 → 평면별 byte 배열 4개. 통로 변경 시 다시 그리기용 `MarkDirty` 추가.
- `Worlds/WorldLayer.cs` — 타입 ID 검증 대신 `WorldRules` 검증. `TileState` 조회/쓰기, 행 복사(저장·해시용) 추가.
- `Worlds/World.cs` — 레지스트리 제거, `PassageMap`·`WorldRules` 소유, 통로 설정 API.
- 기존 테스트(`WorldLayerTests`, `DirtyTrackingTests`, `ContentHashTests`, `TestWorlds`) — 새 API로 이식. 검증 의도는 유지.
- `tools/AntSim.Benchmarks/Program.cs` — 새 API와 편집·저장·래스터 측정 추가.

삭제
- `Content/TypeRegistry.cs`, `TypeRegistryTests.cs` — 지형·블록 종류가 명세로 고정되어 문자열 레지스트리가 불필요. 냄새 종류 확장성은 Phase 2의 `ScentRegistry`가 담당.

신규
- `Worlds/TileTypes.cs`, `WorldRules.cs`, `WorldError.cs`, `PassageMap.cs`
- `Editing/*`, `Serialization/*`, `Rendering/*`, `Util/GridLine.cs`
- `godot/*`, `tools/godot-smoke-test.sh`

## 4. 구현 작업 순서

| Phase | 내용 | 상태 |
|---|---|---|
| 1 | 월드 데이터 구조, 맵 편집기(지형·블록·수량·통로 편집, 클릭·드래그, Undo/Redo, 타일 정보, 저장/불러오기), Godot 편집기 UI | ✅ |
| 2 | 냄새 코어: `ScentSettings`/`ScentState`/`ScentSystem`, 설정 검증, 확산→감쇠→발생, 통로 이동 | ✅ (3차) |
| 3 | `SimulationHost`(계산 스레드, 고정 0.1s 틱, 명령 큐), 재생/정지/단일 틱/초기화, 냄새 종류별 히트맵, 편집/시뮬레이션 모드 분리 | ✅ (3차) |
| 4 | 실험 재현: 실험 파일 ✅ / 헤드리스 실행기(CLI), 골든 해시 테스트 | 일부 |
| 5 | 측정 기반 최적화: 활성 청크, SIMD, 행 묶음 병렬(결정성 유지) | |

개미·여왕개미는 이 단계들 이후에 다룬다.

## 5. 자동 테스트 계획

| 영역 | 테스트 | 위치 |
|---|---|---|
| 월드 구조 | 설정 검증, 비배수 크기·가장자리 청크, 경계 밖 거부, 레이어 독립, 행 복사 | `WorldConfigTests`, `WorldLayerTests` |
| 규칙 | 수량 합계 ≤ 10 경계값, 고체 독점, 상한 설정 범위 | `WorldRulesTests` |
| 통로 | 추가/제거·중복, 결정적 순회 순서, 두 레이어 더티 표시 | `PassageMapTests` |
| 편집 | 연산별 성공/실패, 배치 원자성(실패 시 해시 불변), 같은 타일 연속 연산, Undo/Redo 해시 복원, 기록 상한, 스트로크 묶음 | `MapEditorTests` |
| 드래그 | 경로 보간, 타일당 1회 적용, 실패 타일 건너뛰기, 맵 밖 무시 | `EditorStrokeTests` |
| 저장 | 왕복 해시 일치, 청크 크기 무관, 출력 결정성, 매직/버전/잘림/체크섬/규칙 위반 거부, 원자적 파일 교체 | `MapSerializerTests` |
| 렌더 데이터 | 팔레트 색, 패딩 투명, 수량·통로 표시 | `ChunkRasterizerTests` |
| 재현성 | 같은 편집 → 같은 해시, 고정 해시 값 | `ContentHashTests` |
| Godot 통합 | 실제 씬에서 편집 → 텍스처 갱신 → Undo/Redo → 저장/불러오기, (디스플레이 모드) 마우스 드래그 입력 | `tools/godot-smoke-test.sh` |
| Phase 2 예정 | 총량 보존(감쇠·발생 0, 밀폐), 대칭 확산, 고체 차단, 경계 밖 유출 없음, 청크 경계 연속성(청크 크기 바꿔도 동일 결과), 비음수, 발생량 ∝ 수량, 통로만 레이어 간 이동, 불안정 설정 거부, 같은 입력 → 비트 동일 | `ScentSystemTests` |
| Phase 3 예정 | 프레임 시간을 어떻게 나눠도 같은 틱 수·해시, 단일 틱 정확히 1틱, 실행 중 편집이 틱 경계에서만 적용 | `SimulationHostTests` |

## 6. 성능 측정 계획

- 도구: `tools/AntSim.Benchmarks` (Release, 워밍업 1회 + 7회 중앙값). 결과는 [`BENCHMARKS.md`](BENCHMARKS.md)에 날짜·환경과 함께 기록.
- 기준 맵: 1024×1024 × 2레이어 (+ 필요 시 2048²로 확장성 확인).
- Phase 1 항목(측정 완료): 월드 생성, 타일 조회/쓰기, 편집 명령 처리량, 드래그 스트로크, 더티 수집, 청크 래스터라이즈, 저장/불러오기, 해시.
- Phase 2 항목: 냄새 1틱 비용(종류 수 × 레이어), 통로 수에 따른 비용, 메모리 사용량. 목표 판단 기준: 0.1s 틱 예산 대비 비율.
- Phase 3 항목: 히트맵 업로드 비용(1024² float → 텍스처), 프레임 시간(Godot), 계산 스레드 틱 지연.
- 최적화(Phase 5)는 위 기준선 대비 개선을 수치로 확인하고, 결정성 테스트가 계속 통과할 때만 채택.

---

# 3차 변경 계획 (편집/시뮬레이션 분리, 냄새, 복합 편집 규칙)

## 변경할 클래스와 이유

| 클래스 | 변경 | 이유 (요구사항 번호) |
|---|---|---|
| `MapEditor.Transform`/`Stage` | `PlaceSolid`가 수량 제거 + 물→흙을 함께 처리. `AddPassage`가 지상 검사·물→흙·개미집 3×3 굴착·통로를 한 배치로 스테이징 | 3, 4, 6 |
| `WorldError`, `EditorText` | `PassageBlockedBySolid`, `PassageAreaOutOfBounds` 추가 | 6 |
| `EditHistory`/`EditRecord` | 단계 상한 256 유지 + 메모리 상한(추정 바이트) 추가, 확정된 기록의 색인 사전 해제 | 7 |
| `World` | `Clone()` (시뮬레이션용 독립 복제) | 2 |
| `Chunk`/`WorldLayer` | 평면 채우기(`Fill`), 복제용 복사 | 2, 5 |
| 신규 `WorldPresets` | `Empty`(기존 동작)와 `StandardNest`(개미집 전부 토양) | 5 |
| 신규 `Scent/*` | `ScentSettings`(검증 포함), `ScentField`, `ScentSystem`(확산→지수 감쇠→발생) | 1 |
| 신규 `Simulation/*` | `SimulationSession`(복제 월드 + 냄새 + 틱, 초기화), `SimulationHost`(계산 스레드, 재생/일시정지/단일 틱/초기화, 히트맵 스냅샷 게시) | 2 |
| 신규 `Serialization/ExperimentSerializer` | 실험 상태 파일(.antexp) — 맵 파일과 분리 | 2, 7 |
| `MapSerializer` | 형식 v1 **변경 없음**(기존 파일 호환). 체크섬 입출력 도우미만 공용 파일로 이동 | 5 |
| 신규 `Rendering/HeatmapRasterizer` | 선형/로그, 자동/고정 최대값 | 7 |
| Godot `Main`, 신규 `ModeBar`, `SimulationPanel`, `HeatmapView` | 모드 전환, 시뮬레이션 제어 UI, 히트맵 오버레이 | 2, 7 |

## 기존 코드에 미치는 영향

- 월드 저장 구조, 청크, 더티 추적, 맵 파일 형식은 그대로다. 기존 .antmap 파일은 그대로 열린다(개미집이 빈 상태로 저장된 맵은 빈 상태로 열린다).
- `new World(config)`는 계속 빈 월드를 만든다. 새 프리셋은 `WorldPresets.StandardNest`로 별도 제공하며, 편집기의 "새 맵"이 이것을 쓴다.
- 동작이 바뀌는 기존 테스트
  - `PlaceSolid_OnTileWithQuantity_IsRejected` → 덮어쓰기로 바뀜(수정)
  - 맵 가장자리 통로 스트로크 → 3×3이 안 들어가는 칸은 실패로 보고(수정)
  - Godot 스모크 테스트 → 새 맵의 개미집이 토양으로 채워지므로 시나리오 수정
- 시뮬레이션 모드에서는 월드 편집이 불가능하고, 원본은 시뮬레이션이 바꾸지 않는다.

## 냄새 계산 정의

틱 dt, 종류별 계수 D(이웃당), Dp(통로), k(감쇠), e(발생)에 대해 각 레이어의 비고체 칸 i:

1. 확산: `C'_i = C_i + D·dt·Σ_{j∈열린 4이웃}(C_j − C_i)` + 통로 중심이면 `Dp·dt·(C_반대레이어 − C_i)` (양쪽 중심이 모두 비고체일 때만). 고체 칸·맵 밖 이웃과는 교환하지 않는다. 고체 칸 농도는 0.
2. 감쇠: `C'' = C' · exp(−k·dt)`
3. 발생: `C''' = C'' + e·dt·수량_i`

안정성(비음수) 조건: `(4·D + Dp)·dt ≤ 1`. 틱이나 계수를 바꿀 때 검증하며, 위반하면 적용을 거부한다. 기본값 0.30.

## 테스트 계획

| 영역 | 항목 |
|---|---|
| 고체 덮어쓰기 | 먹이·유충 0 + 배치, 고체 교체, 물→흙, Undo로 수량·지형·블록 복원, 제거 시 물로 돌아가지 않음, 최종 불변 조건 |
| 통로 | 지상 고체면 거부, 4개 가장자리 거부(원자성: 아무것도 안 바뀜), 지상 물→흙, 개미집 3×3 굴착·흙화, 개미집 수량 보존, Undo 1회로 전체 복원, 삭제는 연결만, 삭제 Undo, 드래그 묶음 |
| 프리셋 | StandardNest 상태, Empty 유지, 저장 왕복 |
| 기록 | 256단계, 메모리 상한 초과 시 오래된 기록부터 제거 |
| 냄새 | 총량 보존(k=0·발생 0), 대칭 확산, 고체 차단, 밀폐 경계, 청크 크기 무관(비트 동일), 비음수, 정확한 1틱 수치(확산→감쇠→발생 순서), 지수 감쇠, 발생 ∝ 수량, 통로로만 레이어 간 이동·중심 좌표만, 고체 통로 차단, 설정 검증(불안정 틱 거부), 새 냄새 종류 추가 |
| 세션/호스트 | 원본과 독립(양방향), 초기화, 결정성(같은 입력 → 같은 해시), 단일 틱, 재생/일시정지, 스냅샷 게시 |
| 실험 파일 | 왕복(비트 동일), 저장 후 이어서 실행한 결과 = 끊김 없이 실행한 결과, 손상·버전 거부, 맵 파일과 서로 혼동 거부 |
| 히트맵 | 선형/로그, 자동/고정 최대값, 0 투명 |
| Godot | 스모크 테스트에 모드 전환·재생·단일 틱·초기화·실험 저장/불러오기 추가 |

## 3차 구현 결과

- 계획한 클래스를 모두 구현했다. 맵 파일 형식(v1)은 바꾸지 않았다.
- 기존 테스트 중 규칙 변경으로 바뀐 것은 계획대로 2건(`PlaceSolid_OnTileWithQuantity_IsRejected` → `…_RemovesQuantities`, 가장자리 통로 스트로크)이다.
- 자동 테스트 98 → 187개, Godot 스모크 테스트에 시뮬레이션 모드 시나리오 추가.
- 구현 중 발견해 고친 결함: `SimulationHost.RunExclusiveAsync`로 진행한 틱이 공개 `Tick`에 반영되지 않던 문제.

## PR #2 독립 감사 수정 (F01~F07, E01)

- 결함마다 수정 전에 실패하는 재현 테스트를 먼저 작성했다(`tests/AntSim.Core.Tests/AuditRegressionTests.cs`, 기준 해시 `AuditGoldenTests.cs`).
  큰 할당 테스트는 실제 큰 배열을 만들지 않고, 할당 전 거부와 스레드 할당량(`GC.GetAllocatedBytesForCurrentThread`)으로 확인한다.
  동시성 테스트는 sleep 대신 `SimulationHost.TestHook` 동기화 지점을 쓴다.
- 파일 형식(.antmap v1, .antexp v1)은 바꾸지 않았다. 공개 API 변경: `SimulationHost.ResetAsync()`가 `Task<SimulationResetResult>`를 반환,
  `SimulationSession.ResetCount`, `SimulationSnapshot.ResetCount`, `ScentSettings.MaxTypes/MaxNameUtf8Bytes/MaxEmission`,
  `ScentState.MaxConcentration/AllValuesInSupportedRange`, `ExperimentSerializer.ValidateForSave`, `MapSerializer.EncodedLength`.
- 자동 테스트 212(감사 직전 PR #2 head cc5d416) → 267개(신규 55개).

## PR #2 독립 재검증 수정 (F02, F03, N01)

- 기준 da05632. 재검증 보고서의 반례(T1~T3)를 올바른 계약을 기대하는 회귀 테스트로 옮겼다(`tests/AntSim.Core.Tests/VerificationFollowupTests.cs`).
  신규 테스트는 수정 전 코드(격리 worktree, 테스트용 동기화 지점 한 줄만 추가)에서 Debug/Release 모두 15개 실패, 수정 후 통과.
- F02: 명령 꺼내기 직전 상태 확인을 같은 lock 안으로 옮김(`SimulationHost.DrainCommands`). 테스트 동기화 지점 "before-dequeue" 추가.
- F03: 감쇠·발생 단계에서 상한을 반올림 여유(64 ULP) 이내로 넘은 값만 상한으로 되돌림. 지원 범위·파일 형식은 그대로.
- N01: `EnsureKnownLength`(끝까지 버퍼링) 제거, `LookaheadStream`으로 레코드에 필요한 바이트만 받아 검증. 파일 형식은 그대로.
- 공개 API 변경 없음(내부 API만: `StreamLimits.ForRecord`, `LookaheadStream`, `ScentSystem.RoundingSlackUlps` 등).
- 자동 테스트 267 → 309개.

---

# 개미 AI — 단계 A (개미 개체와 이동)

브랜치 `ants-stage-a`는 PR #2 head(`ccr-ed3f4c3b-scpwcl` @ `cc5d416`)에서 시작한 stacked 브랜치다.
PR #2가 `main`에 병합되면 이 브랜치를 `main` 기준으로 리베이스하고 PR 대상을 `main`으로 바꾼다.

## 범위

| 포함(단계 A) | 제외(단계 B~D 이후) |
|---|---|
| 일개미 데이터(SoA), 생성·표시 | 페로몬 감지, 집 냄새, 먹이 기억, 집 방향 기억 벡터 |
| 연속 좌표 이동, 무작위 탐색(0.5~1초, ±30°), 부드러운 회전 | 성향, 목표(Utility)·행동 큐, 재계획 |
| 경로 검사 이동(관통 방지), 막히면 다른 방향 | 먹이 줍기·운반·내려놓기 |
| 통로 중심에서만 레이어 전환 | 여왕·병정·전투·생리·수영·굴착 등(명세 14절) |
| 개체별 속도(±20%)·크기(±15%), 먹이·유충 감속 | |

## 클래스와 책임 (`src/AntSim.Core/Ants`)

| 클래스 | 책임 |
|---|---|
| `AntSettings` | 생성·이동 설정(속도·크기 편차, 탐색 간격·각도, 회전 속도, 수량별 감속 구간, 감속 완화 시간, 통로 이용 확률). 검증 |
| `AntPopulation` | 개미 SoA 배열(ID, 계급, 레이어, 이동 능력, 위치·이전 위치, 방향·목표 방향, 기본 속도·현재 배율, 크기, 탐색 타이머, 마지막 타일, 개체별 난수 상태) |
| `AntSystem` | 한 틱 갱신: 탐색 방향 → 감속 → 경로 검사 이동 → 막힘 회피 → 통로 레이어 전환. 단계 B~D는 방향 결정에 가중치를 더하는 식으로 확장 |
| `AntMovement` | 통과 가능 판정(흙 가능, 물·토양·바위 불가, 먹이·유충 가능), 격자 DDA 경로 검사 |
| `AntSpawner` | 초기 배치(통로마다 개미집 3×3 굴착 공간, 통로가 없으면 지상 중앙 주변), 잘못된 위치 거부 |
| `AntRandom` | 개체별 결정적 난수(xorshift64*) |

기존 시스템 재사용: `World`/청크 타일 조회(`GetState`), `PassageMap`, `SimulationSession`(복제 월드·Reset·고정 틱),
`SimulationHost`(계산 스레드·스냅샷 풀), `ExperimentSerializer`(v2로 확장), Godot `MapView`·카메라·패널.

## 결정한 세부 동작 (확인 필요)

1. **초기 배치**: 통로가 있으면 각 통로의 개미집 3×3에 고르게 나눠 생성(지하에서 시작). 통로가 없으면 지상 맵 중앙 반경 12타일.
2. **통로 이용**: 단계 A에는 목표가 없으므로, 통로 중심 타일에 새로 들어섰을 때 50% 확률로 반대 레이어로 이동(설정값). 단계 C에서 목표·행동이 결정하도록 바뀐다.
3. **막혔을 때**: 경계 직전에서 멈추고 ±45°, ±90°, ±135°(좌우 무작위), 180° 순서로 0.6타일 앞을 살펴 처음 열린 방향으로 즉시 돌아선다.
4. **회전 속도**: 목표 방향으로 최대 240°/초(설정값).
5. **감속 완화**: 타일이 바뀌면 속도 배율이 시간 상수 0.15초로 새 값에 수렴(설정값, 0이면 즉시).
6. **표시 크기**: 몸 길이 0.8타일 × 개체 편차. 이동 판정과 무관.
7. **틱**: 개미는 냄새와 같은 시뮬레이션 틱(기본 0.1초)에 갱신되며 순서는 냄새 → 개미. 화면은 틱 사이를 보간한다.

## 구현 결과와 알려진 문제

- 자동 테스트 212 → 258개(개미 이동·장애물·관통·통로·생성·편차·감속·결정성, 세션·실험 파일·계산 스레드 연동, v1 실험 파일 호환).
- Godot 스모크 테스트에 개미 시나리오 추가: 생성·표시, 개체 수 적용(초기화), 단일 틱, 재생/일시정지, 통로로 지상 이동, 실제 클릭 선택(디스플레이 모드), 화면 픽셀 확인.
- 구현 중 발견해 고친 결함
  - `AntSettings` 정적 초기화 순서 때문에 `AntSettings.None`의 속도 구간이 null이던 문제.
  - Godot에서 개미가 전혀 그려지지 않던 문제: MultiMesh가 자동 계산한 AABB로 2D 컬링되어 사라짐. 월드 범위 `CustomAabb`로 해결.
    최소 재현 프로젝트에서 수정 전 실패·수정 후 정상을 확인했다. 앱 스모크 테스트의 픽셀 검사는 "개미가 실제로 그려짐"을 확인하지만,
    이 컬링 결함을 수정 없이 재현하지는 못했다(나타나는 조건을 완전히 좁히지 못함).
- 알려진 한계
  - 개미집 3×3에 많은 개미를 만들면 화면상 겹쳐 보인다(점 이동이며 개미 간 충돌은 범위 밖).
  - 통로 이용은 확률 규칙(50%)이라 목표 없이 오르내린다(단계 C에서 대체).
  - 헤드리스 실행에서는 마우스 이벤트가 전달되지 않아 실제 클릭 선택은 디스플레이 모드에서만 검증한다.
