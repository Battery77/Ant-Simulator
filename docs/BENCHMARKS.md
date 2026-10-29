# 기준 성능 측정

실행: `dotnet run -c Release --project tools/AntSim.Benchmarks [-- <size> <iterations>]`

## 1단계 기준선 (2026-10-29)

환경: Intel Xeon 2.10GHz (클라우드 VM), 4 논리 코어, .NET 8.0, Release, 1024×1024, 청크 64, 7회 중앙값

| 항목 | 시간 | 요소당 |
|---|---:|---:|
| World 생성 (2 레이어) | 4.8 ms | 2.3 ns |
| SetTile 전체 순회 (값 변경) | 22.9 ms | 21.8 ns |
| GetTile 전체 순회 (행 우선) | 7.3 ms | 7.0 ns |
| GetTile 무작위 접근 (1M회) | 18.5 ms | 17.7 ns |
| Chunk.TileRow 스캔 (렌더러 업로드 경로) | 1.7 ms | 1.6 ns |
| CollectDirtyChunks (256 청크) | 0.004 ms | 16 ns/청크 |
| ComputeContentHash (2 레이어) | 14.7 ms | 7.0 ns |

해석:
- 칸 단위 `SetTile`은 경계·ID 검사와 버전 갱신 때문에 비싸다. 편집기 대량 채우기는 2단계에서 청크 단위 연산으로 만든다.
- 행 단위 span 스캔은 칸 단위 `GetTile`보다 약 4배 빠르다 → 렌더러·시스템은 span 경로를 쓴다.
- 해시는 바이트 단위 FNV라 느리지만 테스트/검증용이므로 매 틱 호출하지 않는다.
