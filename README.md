# Ant-Simulator
개미 페로몬 시뮬레이터 — 게임형 + 과학 실험형 2D 개미 생태계 시뮬레이터 (Godot 4 .NET / C#)

## 구성
- `src/AntSim.Core` — Godot에 의존하지 않는 시뮬레이션 코어
- `tests/AntSim.Core.Tests` — 단위 테스트 (Godot 없이 실행)
- `tools/AntSim.Benchmarks` — 기준 성능 측정

## 빌드 / 테스트
```
dotnet test
dotnet run -c Release --project tools/AntSim.Benchmarks
```
.NET 8 SDK 필요.

## 문서
- [아키텍처 설계](docs/ARCHITECTURE.md)
- [결정해야 할 게임 규칙](docs/OPEN_QUESTIONS.md)
- [구현 로드맵](docs/ROADMAP.md)
- [기준 성능 측정](docs/BENCHMARKS.md)
