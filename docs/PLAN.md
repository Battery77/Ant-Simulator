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
