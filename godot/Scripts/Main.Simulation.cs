using AntSim.Core.Ants;
using AntSim.Core.Editing;
using AntSim.Core.Rendering;
using AntSim.Core.Scent;
using AntSim.Core.Serialization;
using AntSim.Core.Simulation;
using AntSim.Core.Worlds;
using Godot;

namespace AntSim.App;

/// <summary>
/// 시뮬레이션 모드. 시작할 때 편집 월드를 복제해 <see cref="SimulationHost"/>(계산 스레드)에 넘긴다.
/// 메인 스레드는 명령만 보내고, 게시된 냄새 스냅샷으로 히트맵·정보를 표시한다.
/// 표시용 타일 데이터는 메인 스레드 전용 복제본(<see cref="_simWorld"/>)을 쓰므로 계산 스레드와 공유하는 데이터가 없다.
/// </summary>
public partial class Main
{
    private const string ExperimentFileFilter = "*.antexp ; 개미 시뮬레이터 실험 상태";

    private HeatmapView _heatmap = null!;
    private AntView _antView = null!;
    private AntSettings _antSettings = AntSettings.Default;
    private int _selectedAntId = -1;
    private long _selectedAntResetCount = -1; // 선택한 개미의 초기화 세대(초기화 뒤 같은 ID의 다른 개미를 가리키지 않도록)
    private long _frameTick = -1;
    private long _frameResetCount = -1;
    private ulong _frameArrivalMs;
    private SimulationPanel _simPanel = null!;
    private ModeBar _modeBar = null!;
    private SimulationHost? _host;
    private World? _simWorld;
    private ScentSnapshot? _snapshot;
    private ScentSettings _scentSettings = ScentSettings.Default;
    private HeatmapOptions _heatmapOptions = new(HeatmapMode.Single, 0, HeatmapScale.Linear, true, Array.Empty<float>());
    private readonly CompositeHeatmapPalette _compositePalette = CompositeHeatmapPalette.CreateDefault();
    private readonly CompositeHeatmapRasterizer _compositeRasterizer = new();
    private CompositeChannel[] _channels = Array.Empty<CompositeChannel>();
    private LegendEntry[] _legendEntries = Array.Empty<LegendEntry>();
    private double _speed = 1;
    private bool _heatmapDirty;

    /// <summary>화면이 받아들일 최소 초기화 세대. 이보다 이전 세대의 스냅샷은 초기화 전 상태라 버린다(F05).</summary>
    private long _minResetCount;

    /// <summary>화면에 표시 중인 월드(편집 원본 또는 시뮬레이션 표시용 복제본).</summary>
    private World CurrentWorld => _mode == AppMode.Simulation && _simWorld is not null ? _simWorld : _world;

    private void ConnectSimulationPanel()
    {
        _simPanel.PlayRequested += () => _host?.Play();
        _simPanel.PauseRequested += () => _host?.Pause();
        _simPanel.StepRequested += () => _ = StepOnce();
        _simPanel.ResetRequested += () => _ = ResetSimulation();
        _simPanel.SpeedChanged += speed =>
        {
            _speed = speed;
            if (_host is not null) _host.Speed = speed;
        };
        _simPanel.SetLegendGradientProvider(LegendGradient);
        _simPanel.HeatmapChanged += options =>
        {
            _heatmapOptions = options;
            SelectHeatmapOnHost();
        };
        _simPanel.SettingsApplyRequested += settings => _ = ApplySettings(settings);
        _simPanel.AntSettingsApplyRequested += settings => _ = ApplyAntSettings(settings);
        _simPanel.SaveExperimentRequested += () =>
            ShowFileDialog(FileDialog.FileModeEnum.SaveFile, ExperimentFileFilter, path => _ = SaveExperiment(path));
        _simPanel.LoadExperimentRequested += () =>
            ShowFileDialog(FileDialog.FileModeEnum.OpenFile, ExperimentFileFilter, LoadExperiment);
    }

    private void SetMode(AppMode mode)
    {
        if (mode == _mode) return;
        if (mode == AppMode.Simulation) StartSimulation(null);
        else ExitSimulationMode();
    }

    /// <summary>시뮬레이션 모드로 들어간다. <paramref name="loaded"/>가 없으면 현재 편집 월드를 복제해 새 실험을 만든다.</summary>
    private void StartSimulation(SimulationSession? loaded)
    {
        EndStroke();
        StopSimulation();
        SimulationSession session;
        try
        {
            session = loaded ?? new SimulationSession(_world, _scentSettings, _antSettings);
        }
        catch (ArgumentException e)
        {
            _panel.SetStatus($"시뮬레이션을 시작할 수 없습니다: {e.Message}");
            _modeBar.SetMode(_mode);
            return;
        }

        // 호스트(계산 스레드)에 넘기기 전에 표시용 복제본을 만든다. 이후 세션은 계산 스레드만 다룬다.
        _scentSettings = session.Settings;
        _antSettings = session.AntSettings;
        var spawn = session.LastSpawn;
        _simWorld = session.World.Clone();
        _minResetCount = session.ResetCount;
        _host = new SimulationHost(session) { Speed = _speed };
        _mode = AppMode.Simulation;
        _pinnedTile = null;
        _panel.Visible = false;
        _simPanel.Visible = true;
        _modeBar.SetMode(AppMode.Simulation);
        _simPanel.SetSettings(_scentSettings);
        _simPanel.SetAntSettings(_antSettings);
        _simPanel.SetSettingsMessage("");
        ClearAntSelection();
        _frameTick = _frameResetCount = -1;
        _heatmapOptions = _simPanel.Heatmap;
        _view.SetWorld(_simWorld);
        _antView.SetWorldBounds(_simWorld.Config.Width, _simWorld.Config.Height);
        SelectHeatmapOnHost();
        _simPanel.SetStatus((loaded is null
            ? "편집 월드를 복제해 새 실험을 시작했습니다."
            : $"실험 상태를 불러왔습니다 (틱 {session.Tick}).") + SpawnText(spawn));
    }

    private void ExitSimulationMode()
    {
        StopSimulation();
        _mode = AppMode.Edit;
        _pinnedTile = null;
        _simPanel.Visible = false;
        _panel.Visible = true;
        _modeBar.SetMode(AppMode.Edit);
        _heatmap.HideHeatmap();
        _antView.Clear();
        ClearAntSelection();
        _view.SetWorld(_world);
        _panel.SetStatus("편집 모드. 시뮬레이션 결과는 원본 맵에 반영되지 않습니다.");
    }

    private void StopSimulation()
    {
        if (_host is null) return;
        _host.Dispose();
        _host = null;
        _snapshot = null;
        _simWorld = null;
    }

    private void ProcessSimulation(Vector2I? shown)
    {
        if (_host is null || _simWorld is null) return;
        if (_host.Failure is { } failure) _simPanel.SetStatus($"계산 오류로 중지됨: {failure.Message}");

        if (_host.TryTakeSnapshot(out var snapshot) && snapshot.ResetCount < _minResetCount)
        {
            _host.Release(snapshot); // 초기화 이전에 만든 화면 데이터: 새 월드 위에 그리지 않는다
            snapshot = null!;
        }
        if (snapshot is not null)
        {
            if (_snapshot is not null) _host.Release(_snapshot);
            _snapshot = snapshot;
            _heatmapDirty = true;
            // 보간 시작 시각은 (초기화 세대, 틱)이 바뀔 때만 갱신한다. 초기화 뒤 틱 0이 다시 와도 새 프레임으로 본다.
            if (snapshot.Tick != _frameTick || snapshot.ResetCount != _frameResetCount)
            {
                _frameTick = snapshot.Tick;
                _frameResetCount = snapshot.ResetCount;
                _frameArrivalMs = Time.GetTicksMsec();
            }
        }
        UpdateAnts();
        if (_heatmapDirty)
        {
            _heatmapDirty = false;
            UpdateHeatmap();
        }

        double elapsed = _snapshot?.ElapsedSeconds ?? 0;
        _simPanel.SetClock(
            $"틱 {_host.Tick:N0} · {elapsed:0.0}초 · {(_host.IsPlaying ? "재생 중" : "일시정지")}\n" +
            $"틱 간격 {SimulationPanel.FormatNumber(_scentSettings.TickSeconds)}초 · 틱 계산 {_host.LastTickMilliseconds:0.0}ms" +
            (_heatmap.Visible ? $" · 히트맵 {_heatmap.LastRasterMilliseconds:0.0}ms" : ""));

        string? antInfo = DescribeSelectedAnt();
        if (shown is not { } t)
        {
            _simPanel.SetInfo(antInfo ?? "");
            return;
        }
        string info = (antInfo is null ? "" : antInfo + "\n\n") + EditorText.DescribeTile(_simWorld, _view.Layer, t.X, t.Y);
        if (_snapshot is { } s && s.Layer == _view.Layer)
        {
            // 표시 색이 아니라 실제 농도 값을 보여 준다(복합 모드에서는 모든 냄새).
            int index = _simWorld.Config.ToLinearIndex(t.X, t.Y);
            foreach (int type in s.Types)
                if (type < _scentSettings.Types.Count)
                    info += $"\n냄새({_scentSettings.Types[type].DisplayName}): {s.Field(type)[index]:0.#####}";
            if (s.Types.Count > 0) info += $" (틱 {s.Tick})";
        }
        _simPanel.SetInfo(info);
    }

    /// <summary>개미를 그린다. 재생 중에는 마지막 틱 이후 경과 시간만큼 이전 → 현재 위치를 보간한다.</summary>
    private void UpdateAnts()
    {
        if (_snapshot is not { } s || _host is null)
        {
            _antView.Clear();
            return;
        }
        float alpha = 1;
        if (_host.IsPlaying)
        {
            double tickMs = _scentSettings.TickSeconds / _host.Speed * 1000;
            alpha = (float)Math.Clamp((Time.GetTicksMsec() - _frameArrivalMs) / tickMs, 0, 1);
        }
        if (_selectedAntId >= 0 && _selectedAntResetCount != s.ResetCount) ClearAntSelection();
        _antView.ShowFrame(s.Ants, _view.Layer, alpha, _selectedAntId);
        int surface = s.Ants.CountOn(LayerId.Surface);
        _simPanel.SetAntCount($"개미 {s.Ants.Count}마리 (지상 {surface} / 개미집 {s.Ants.Count - surface}) · 이 레이어 표시 {_antView.VisibleCount}");
    }

    /// <summary>클릭 위치에서 가장 가까운 개미(현재 레이어, 1타일 이내)를 고른다. 없으면 false.</summary>
    private bool TrySelectAnt(Vector2 worldPoint)
    {
        if (_snapshot is not { } s) return false;
        var frame = s.Ants;
        int best = -1;
        float bestDistance = 1f;
        for (int i = 0; i < frame.Count; i++)
        {
            if (frame.Layer[i] != (byte)_view.Layer) continue;
            float d = new Vector2(frame.X[i], frame.Y[i]).DistanceTo(worldPoint);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        if (best >= 0) SelectAnt(frame.Id[best], s.ResetCount);
        else ClearAntSelection();
        return best >= 0;
    }

    private void SelectAnt(int id, long resetCount)
    {
        _selectedAntId = id;
        _selectedAntResetCount = resetCount;
    }

    private void ClearAntSelection()
    {
        _selectedAntId = -1;
        _selectedAntResetCount = -1;
    }

    private string? DescribeSelectedAnt()
    {
        if (_selectedAntId < 0 || _snapshot is not { } s || s.ResetCount != _selectedAntResetCount) return null;
        var f = s.Ants;
        int i = Array.IndexOf(f.Id, _selectedAntId, 0, f.Count);
        if (i < 0) return $"개미 #{_selectedAntId}: 없음";
        float degrees = f.Heading[i] * 180 / MathF.PI;
        return $"개미 #{f.Id[i]} (일개미) · {EditorText.Name((LayerId)f.Layer[i])}\n" +
               $"위치 ({f.X[i]:0.00}, {f.Y[i]:0.00}) · 방향 {degrees:0}°\n" +
               $"기본 속도 {f.BaseSpeed[i]:0.00} 타일/초 · 현재 {f.BaseSpeed[i] * f.SpeedFactor[i]:0.00} (×{f.SpeedFactor[i]:0.00})\n" +
               $"외형 크기 {f.Size[i]:0.00} 타일\n" +
               "목표·행동·성향·기억: 단계 B~D에서 추가";
    }

    /// <summary>개미 설정을 적용한다: 그 설정으로 초기화(월드·냄새·틱·개미 재생성)하고 화면을 새 세대로 바꾼다.</summary>
    private async Task ApplyAntSettings(AntSettings settings)
    {
        if (_host is null) return;
        var errors = settings.Validate();
        if (errors.Count > 0)
        {
            _simPanel.SetStatus(string.Join("\n", errors));
            return;
        }
        var result = await ResetHost(settings, "개미 설정 적용");
        if (result is null) return;
        _antSettings = settings;
        _simPanel.SetAntSettings(settings);
        _simPanel.SetStatus("개미 설정을 적용하고 초기화했습니다." + SpawnText(result.Ants));
    }

    private static string SpawnText(AntSpawnReport report) =>
        report.Requested == 0 ? " 개미 없음." :
        report.Failed == 0 ? $" 개미 {report.Spawned}마리 생성." :
        $" 개미 {report.Spawned}/{report.Requested}마리 생성(열린 공간 부족으로 {report.Failed}마리 실패).";

    private void UpdateHeatmap()
    {
        var options = _heatmapOptions;
        var wanted = HeatmapTypes(options);
        if (_snapshot is not { } s || _simWorld is null || wanted.Length == 0 || s.Layer != _view.Layer ||
            !wanted.All(s.Contains))
        {
            // 선택에 맞는 스냅샷이 아직 오지 않았으면 잘못된 색을 보이지 않도록 숨긴다.
            _heatmap.HideHeatmap();
            return;
        }

        if (_legendEntries.Length != wanted.Length) _legendEntries = new LegendEntry[wanted.Length];
        for (int i = 0; i < wanted.Length; i++)
        {
            int type = wanted[i];
            float current = s.MaxOf(type);
            _legendEntries[i] = new LegendEntry(type, current, options.AutoMax ? current : options.FixedMaxOf(type));
        }
        _simPanel.SetLegendValues(_legendEntries);

        var config = _simWorld.Config;
        if (options.Mode == HeatmapMode.Single)
        {
            var entry = _legendEntries[0];
            _heatmap.ShowField(s.Field(entry.TypeIndex), config.Width, config.Height, entry.ShownMax, options.Scale);
            return;
        }

        if (_channels.Length != wanted.Length) _channels = new CompositeChannel[wanted.Length];
        for (int i = 0; i < wanted.Length; i++)
        {
            int type = wanted[i];
            _channels[i] = new CompositeChannel(s.Field(type), _legendEntries[i].ShownMax, options.Scale,
                _compositePalette.Get(_scentSettings.Types[type].Key));
        }
        _heatmap.ShowComposite(_channels, config.Width, config.Height, _compositeRasterizer);
    }

    /// <summary>히트맵 모드가 필요로 하는 냄새 종류(숨김이면 빈 배열 → 계산 스레드가 필드를 복사하지 않음).</summary>
    private int[] HeatmapTypes(HeatmapOptions options) => options.Mode switch
    {
        HeatmapMode.Single when options.TypeIndex >= 0 && options.TypeIndex < _scentSettings.Types.Count => new[] { options.TypeIndex },
        HeatmapMode.Composite => Enumerable.Range(0, _scentSettings.Types.Count).ToArray(),
        _ => Array.Empty<int>(),
    };

    private void SelectHeatmapOnHost()
    {
        _heatmapDirty = true;
        _host?.SelectHeatmap(HeatmapTypes(_heatmapOptions), _view.Layer);
    }

    /// <summary>범례 색 막대. type = -1이면 단일 히트맵 색(기존 LUT), 아니면 복합 채널 색 램프.</summary>
    private Gradient LegendGradient(int type)
    {
        var gradient = new Gradient();
        var offsets = new List<float>();
        var colors = new List<Color>();
        for (int i = 0; i <= 4; i++)
        {
            int q = Math.Max(1, i * 255 / 4);
            uint c = type < 0 ? HeatmapRasterizer.LutColor(q / 255f) : _compositePalette.Get(_scentSettings.Types[type].Key).ColorAt(q);
            offsets.Add(i / 4f);
            colors.Add(new Color((byte)c / 255f, (byte)(c >> 8) / 255f, (byte)(c >> 16) / 255f, Math.Max(0.35f, (byte)(c >> 24) / 255f)));
        }
        gradient.Offsets = offsets.ToArray();
        gradient.Colors = colors.ToArray();
        return gradient;
    }

    private void OnSimulationLayerChanged()
    {
        if (_mode == AppMode.Simulation) SelectHeatmapOnHost();
    }

    private bool HandleSimulationShortcut(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.Space when _host is not null:
                if (_host.IsPlaying) _host.Pause();
                else _host.Play();
                return true;
            case Key.Period:
                _ = StepOnce();
                return true;
            default:
                return false;
        }
    }

    private async Task StepOnce()
    {
        if (_host is null) return;
        await RunHostCommand(_host, h => h.StepAsync(), "단일 틱");
    }

    /// <summary>초기화(현재 개미 설정으로 재생성).</summary>
    private async Task ResetSimulation()
    {
        var result = await ResetHost(null, "초기화");
        if (result is not null) _simPanel.SetStatus("시작 상태로 초기화했습니다." + SpawnText(result.Ants));
    }

    /// <summary>
    /// 초기화 버튼과 개미 설정 적용이 함께 쓰는 경로. 계산 스레드가 한 명령 안에서 월드·냄새·틱·개미·초기화 세대를 바꾸고
    /// 같은 틱 경계에서 복제해 준 월드로 화면 월드(_simWorld)와 MapView를 바꾼다(F05). 이전 세대의 스냅샷(냄새·개미 프레임)은 버리고,
    /// 개미 선택·보간 기준도 새 세대로 초기화한다. 그사이 호스트가 바뀌었거나(불러오기·모드 전환) 더 새로운 초기화가 이미 반영됐으면
    /// 결과를 화면에 적용하지 않는다. 취소·실패면 null.
    /// </summary>
    private async Task<SimulationResetResult?> ResetHost(AntSettings? ants, string what)
    {
        if (_host is null) return null;
        var host = _host;
        SimulationResetResult? result;
        try
        {
            result = await RunHostCommand(host, h => h.ResetAsync(ants), what);
        }
        catch (ArgumentException e)
        {
            if (host == _host) _simPanel.SetStatus($"{what} 실패: {e.Message}");
            return null;
        }
        if (result is null || host != _host || result.ResetCount < _minResetCount) return null;

        _simWorld = result.World;
        _minResetCount = result.ResetCount;
        _view.SetWorld(_simWorld);
        _antView.SetWorldBounds(_simWorld.Config.Width, _simWorld.Config.Height);
        if (_snapshot is not null && _snapshot.ResetCount < _minResetCount)
        {
            host.Release(_snapshot);
            _snapshot = null;
            _heatmap.HideHeatmap();
            _antView.Clear();
        }
        ClearAntSelection();
        _frameTick = _frameResetCount = -1;
        _heatmapDirty = true;
        return result;
    }

    /// <summary>
    /// 호스트 명령을 기다린다. 호스트가 종료(모드 전환)되어 취소되거나 계산 스레드 실패로 거부되면
    /// 예외를 밖으로 던지지 않고 상태 문구만 남긴다. 성공하면 결과, 아니면 null.
    /// </summary>
    private async Task<T?> RunHostCommand<T>(SimulationHost host, Func<SimulationHost, Task<T>> command, string what) where T : class
    {
        try
        {
            return await command(host);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            ReportCommandStopped(host, what, e);
            return null;
        }
    }

    private async Task RunHostCommand(SimulationHost host, Func<SimulationHost, Task> command, string what)
    {
        try
        {
            await command(host);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            ReportCommandStopped(host, what, e);
        }
    }

    private void ReportCommandStopped(SimulationHost host, string what, Exception e)
    {
        string message = e is OperationCanceledException or ObjectDisposedException
            ? $"{what} 요청이 취소되었습니다(시뮬레이션 종료)."
            : $"{what} 요청을 실행하지 못했습니다: {e.Message}";
        if (host == _host) _simPanel.SetStatus(message);
        else _panel.SetStatus(message); // 이미 편집 모드로 나간 경우
    }

    private async Task ApplySettings(ScentSettings settings)
    {
        if (_host is null) return;
        var host = _host;
        var errors = await RunHostCommand(host, h => h.SetSettingsAsync(settings), "설정 적용");
        if (errors is null || host != _host) return;
        if (errors.Count == 0)
        {
            _scentSettings = settings;
            _simPanel.SetSettingsMessage("설정을 적용했습니다. 다음 틱부터 반영됩니다.");
        }
        else
        {
            _simPanel.SetSettingsMessage(string.Join("\n", errors));
        }
    }

    private async Task SaveExperiment(string path)
    {
        if (_host is null) return;
        var host = _host;
        try
        {
            long tick = await host.RunExclusiveAsync(session =>
            {
                ExperimentSerializer.Save(session, path);
                return session.Tick;
            });
            string done = $"실험 상태 저장됨 (틱 {tick}): {path}";
            if (host == _host) _simPanel.SetStatus(done);
            else _panel.SetStatus(done);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (host == _host) _simPanel.SetStatus($"실험 저장 실패: {e.Message}");
            else _panel.SetStatus($"실험 저장 실패: {e.Message}");
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            ReportCommandStopped(host, "실험 저장", e); // 종료로 취소, 계산 스레드 실패, 저장 불가 상태(ValidateForSave)
        }
    }

    private void LoadExperiment(string path)
    {
        try
        {
            StartSimulation(ExperimentSerializer.Load(path));
        }
        catch (Exception e) when (e is MapFormatException or IOException or UnauthorizedAccessException)
        {
            _simPanel.SetStatus($"실험 불러오기 실패: {e.Message}");
        }
    }
}
