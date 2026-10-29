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
            session = loaded ?? new SimulationSession(_world, _scentSettings);
        }
        catch (ArgumentException e)
        {
            _panel.SetStatus($"시뮬레이션을 시작할 수 없습니다: {e.Message}");
            _modeBar.SetMode(_mode);
            return;
        }

        // 호스트(계산 스레드)에 넘기기 전에 표시용 복제본을 만든다. 이후 세션은 계산 스레드만 다룬다.
        _scentSettings = session.Settings;
        _simWorld = session.World.Clone();
        _host = new SimulationHost(session) { Speed = _speed };
        _mode = AppMode.Simulation;
        _pinnedTile = null;
        _panel.Visible = false;
        _simPanel.Visible = true;
        _modeBar.SetMode(AppMode.Simulation);
        _simPanel.SetSettings(_scentSettings);
        _simPanel.SetSettingsMessage("");
        _heatmapOptions = _simPanel.Heatmap;
        _view.SetWorld(_simWorld);
        SelectHeatmapOnHost();
        _simPanel.SetStatus(loaded is null
            ? "편집 월드를 복제해 새 실험을 시작했습니다."
            : $"실험 상태를 불러왔습니다 (틱 {session.Tick}).");
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

        if (_host.TryTakeSnapshot(out var snapshot))
        {
            if (_snapshot is not null) _host.Release(_snapshot);
            _snapshot = snapshot;
            _heatmapDirty = true;
        }
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

        if (shown is not { } t)
        {
            _simPanel.SetInfo("");
            return;
        }
        string info = EditorText.DescribeTile(_simWorld, _view.Layer, t.X, t.Y);
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
        await _host.StepAsync();
    }

    private async Task ResetSimulation()
    {
        if (_host is null) return;
        await _host.ResetAsync();
        _simPanel.SetStatus("시작 상태로 초기화했습니다.");
    }

    private async Task ApplySettings(ScentSettings settings)
    {
        if (_host is null) return;
        var host = _host;
        var errors = await host.SetSettingsAsync(settings);
        if (host != _host) return;
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
        try
        {
            long tick = await _host.RunExclusiveAsync(session =>
            {
                ExperimentSerializer.Save(session, path);
                return session.Tick;
            });
            _simPanel.SetStatus($"실험 상태 저장됨 (틱 {tick}): {path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _simPanel.SetStatus($"실험 저장 실패: {e.Message}");
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
