# Ant-Simulator 작업 규칙

## UI·시각화 기능 작업 절차

UI나 시각화(히트맵, 오버레이, 패널 등) 기능을 구현할 때는 다음을 따른다.

1. 구현 후 빌드와 자동 테스트를 실행하고 결과(실패 포함)를 보고한다.
   - `dotnet build -c Release`, `dotnet test`
2. 가능하면 Godot를 실제로 실행해서 기능을 조작한다.
   - 헤드리스: `GODOT=<Godot .NET 실행 파일> tools/godot-smoke-test.sh`
   - 실제 렌더링 + 마우스 입력 + 스크린샷: `GODOT=... tools/godot-smoke-test.sh --display` (xvfb 필요)
   - 새 기능은 `godot/Scripts/Main.SmokeTest.cs`에 조작 시나리오와 캡처를 추가한다.
3. 변경 전후 스크린샷을 제공한다. 변경 전 화면은 변경 직전 커밋(예: `git worktree`)에서 같은 시나리오로 캡처한다.
4. 히트맵처럼 시간에 따라 변하는 기능은 서로 다른 시점(틱)의 화면도 제공한다.
5. 직접 실행·조작해서 확인하지 못한 동작은 **미검증**이라고 명시한다.
6. 스크린샷은 실제 프로그램에서 캡처한 것만 제공하고, 캡처 조건(Godot 버전, 빌드 구성, 틱, 레이어, 모드)을 함께 적는다.
   예상 화면·목업은 실제 캡처와 섞지 않고, 만들 경우 "목업"이라고 분명히 표시한다.
7. 기능이 완성되어도 사용자 승인 없이 `main`에 병합하지 않는다. PR까지만 만든다. Force push 금지.

## 기본 명령

```
dotnet build -c Release
dotnet test
dotnet run -c Release --project tools/AntSim.Benchmarks
```

설계 문서: `docs/ARCHITECTURE.md`, 작업 계획: `docs/PLAN.md`, 미정 규칙: `docs/OPEN_QUESTIONS.md`, 성능 기록: `docs/BENCHMARKS.md`.
