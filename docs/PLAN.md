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
| 2 | 냄새 코어: `ScentRegistry`/`ScentField`/`ScentSystem`, 설정 검증, 확산→감쇠→발생, 통로 이동 | 다음 |
| 3 | `SimulationHost`(계산 스레드, 고정 0.1s 틱, 명령 큐), 재생/정지/단일 틱, 냄새 종류별 히트맵 | |
| 4 | 실험 재현: 실험 파일(맵 + 냄새 설정 + 냄새 상태), 헤드리스 실행기, 골든 해시 테스트 | |
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
