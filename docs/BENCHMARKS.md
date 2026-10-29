# 기준 성능 측정

실행: `dotnet run -c Release --project tools/AntSim.Benchmarks [-- <size> <iterations>]`
환경: Intel Xeon 2.10GHz (클라우드 VM), 4 논리 코어, .NET 8.0, Release, 1024×1024, 청크 64, 7회 중앙값.
VM이라 같은 항목도 실행마다 최대 2~3배 흔들릴 수 있다(예: 월드 생성 4.8~37ms). 비교는 같은 환경에서 연속 실행한 값으로 한다.

## Phase 1 — 월드 데이터 + 편집기 (2026-10-29)

| 항목 | 시간 | 요소당 |
|---|---:|---:|
| World 생성 (2 레이어, 8MB) | 4.8 ms | 2.3 ns/타일 |
| `TrySetState` 전체 순회 (검증 + 4평면 쓰기) | 22.9 ms | 21.8 ns |
| `GetState` 전체 순회 | 7.2 ms | 6.9 ns |
| `CopyRow` 전체 (1 평면) | 0.68 ms | 0.64 ns |
| `MapEditor.Apply` 단일 연산 100k회 + 전체 Undo | 222 ms | 1.1 µs/연산 |
| 드래그 스트로크 (51k칸, Undo 1단계) | 30.8 ms | 0.6 µs/칸 |
| `CollectDirtyChunks` (256 청크) | 0.003 ms | 12.5 ns/청크 |
| `ChunkRasterizer` 레이어 전체 (256 청크) | 11.3 ms | 10.8 ns/타일 |
| `MapSerializer.Save` (8.0 MiB) | 13.7 ms | 6.5 ns/타일 |
| `MapSerializer.Load` (검증 포함) | 26.3 ms | 12.6 ns/타일 |
| `ComputeContentHash` | 14.9 ms | 7.1 ns/타일 |

해석
- 편집 명령은 연산당 약 1µs(검증·Undo 기록 포함). 드래그 한 프레임에 수십~수백 칸이므로 프레임 예산(16ms)에 비해 충분히 작다.
- 레이어 전체 다시 그리기는 약 11ms. 평소에는 더티 청크(64²당 약 44µs)만 다시 그리므로 문제 없다. 새 맵·불러오기 때만 전체 비용이 든다.
- 저장 파일은 압축 없이 8MiB. 크기가 문제가 되면 형식 v2에서 압축을 추가한다(버전 필드로 구분).

## 1단계 기준선 (이전 모델: ushort 타일/블록, 참고용)

| 항목 | 시간 |
|---|---:|
| World 생성 | 4.8 ms |
| SetTile 전체 순회 | 22.9 ms |
| GetTile 전체 순회 | 7.3 ms |
| ComputeContentHash | 14.7 ms |
