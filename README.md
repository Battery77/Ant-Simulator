# Ant-Simulator
개미 페로몬 시뮬레이터 — 게임형 + 과학 실험형 2D 개미 생태계 시뮬레이터 (Godot 4 .NET / C#)

## 구성
- `src/AntSim.Core` — Godot에 의존하지 않는 시뮬레이션 코어 (월드, 편집기, 저장 형식)
- `godot/` — Godot 4.4+ .NET 프론트엔드 (맵 편집기 UI)
- `tests/AntSim.Core.Tests` — 단위 테스트 (Godot 없이 실행)
- `tools/AntSim.Benchmarks` — 기준 성능 측정
- `tools/godot-smoke-test.sh` — Godot 통합 점검

## 빌드 / 테스트
.NET 8 SDK 필요. Godot 프로젝트도 NuGet의 Godot.NET.Sdk로 `dotnet build`가 된다.
```
dotnet build
dotnet test
dotnet run -c Release --project tools/AntSim.Benchmarks
GODOT=/path/to/Godot_mono tools/godot-smoke-test.sh            # 헤드리스
GODOT=/path/to/Godot_mono tools/godot-smoke-test.sh --display  # xvfb: 렌더링·마우스 입력·스크린샷
```

## 실행 (편집기)
Godot 4.4 이상 .NET 버전으로 `godot/project.godot`를 연다.

| 조작 | 동작 |
|---|---|
| 왼쪽 클릭·드래그 | 선택한 도구로 편집 (드래그 한 번 = 실행 취소 1단계) |
| 가운데/오른쪽 드래그, 휠 | 화면 이동, 확대/축소 |
| Tab | 지상 ↔ 개미집 전환 |
| Ctrl+Z / Ctrl+Y (Ctrl+Shift+Z) | 실행 취소 / 다시 실행 |
| Ctrl+S | 저장 |
| 조회 도구로 클릭 / Esc | 타일 정보 고정 / 해제 |

## 문서
- [아키텍처](docs/ARCHITECTURE.md)
- [작업 계획·테스트·성능 측정 계획](docs/PLAN.md)
- [결정이 필요한 규칙](docs/OPEN_QUESTIONS.md)
- [기준 성능 측정](docs/BENCHMARKS.md)
