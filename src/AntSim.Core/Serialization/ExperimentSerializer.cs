using System.Runtime.InteropServices;
using AntSim.Core.Ants;
using AntSim.Core.Scent;
using AntSim.Core.Simulation;
using AntSim.Core.Worlds;

namespace AntSim.Core.Serialization;

/// <summary>
/// 시뮬레이션 실험 상태 파일(.antexp). 맵 파일(.antmap)과 분리되어 있으며, 실험을 그대로 이어서 실행할 수 있도록
/// 설정·틱·시작 월드·현재 월드·냄새 필드 전체를 담는다. 모든 정수·실수는 리틀 엔디언.
/// v2는 v1 끝(냄새 다음, 체크섬 앞)에 개미 설정과 개체 상태를 추가했다. v1 파일은 개미 없이 불러온다.
/// <code>
/// [v1]
/// magic        8 bytes  "ANTEXP\0\0"
/// version      u16      = 1
/// flags        u16      = 0
/// tick         i64
/// elapsed      f64      시뮬레이션 경과 시간(초)
/// settings     f64 tickSeconds, i32 typeCount,
///              typeCount × { string key, string displayName, u8 hasSource, u8 source, f64 D, f64 Dp, f64 k, f64 e }
///              (string = i32 UTF-8 바이트 길이 + 바이트)
/// initialWorld i32 length + 맵 파일 형식(MapSerializer) 바이트
/// currentWorld i32 length + 맵 파일 형식 바이트
/// scent        typeCount × 레이어 2 × (width*height) × f32   (종류 순서 = settings, 레이어 순서 Surface, Nest)
/// checksum     u64      위 모든 바이트의 FNV-1a 64
///
/// [v2] = v1 + (scent 다음, checksum 앞에)
/// antSettings  i32 initialCount, u64 seed, f32 × 12 (baseSpeed, speedVariation, baseSize, sizeVariation,
///              turnIntervalMin, turnIntervalMax, turnIntervalVariation, wanderAngle, turnRate,
///              speedSmoothing, passageUseProbability, 예약 0), i32 tierCount, tierCount × { i32 minQuantity, f32 factor }
/// ants         i32 count, i32 nextId, 이후 배열들(각 count개, 순서 고정):
///              id i32, caste u8, layer u8, mobility u8, x, y, prevX, prevY, heading, targetHeading,
///              baseSpeed, speedFactor, size, turnTimer, turnIntervalScale (f32), lastTile i32, rng u64
/// </code>
/// </summary>
public static class ExperimentSerializer
{
    public const ushort CurrentVersion = 2;
    public const int MaxScentTypes = 256;
    private static ReadOnlySpan<byte> Magic => "ANTEXP\0\0"u8;

    public static void Save(SimulationSession session, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(stream);
        EnsureLittleEndian();
        var writer = new ChecksumWriter(stream);

        writer.Write(Magic);
        writer.WriteUInt16(CurrentVersion);
        writer.WriteUInt16(0);
        writer.WriteInt64(session.Tick);
        writer.WriteDouble(session.ElapsedSeconds);

        var settings = session.Settings;
        writer.WriteDouble(settings.TickSeconds);
        writer.WriteInt32(settings.Types.Count);
        foreach (var type in settings.Types)
        {
            writer.WriteString(type.Key);
            writer.WriteString(type.DisplayName);
            writer.WriteByte(type.Source.HasValue ? (byte)1 : (byte)0);
            writer.WriteByte((byte)(type.Source ?? 0));
            writer.WriteDouble(type.Diffusion);
            writer.WriteDouble(type.PassageDiffusion);
            writer.WriteDouble(type.Decay);
            writer.WriteDouble(type.Emission);
        }

        WriteWorld(writer, session.InitialWorld);
        WriteWorld(writer, session.World);

        for (int t = 0; t < session.Scent.TypeCount; t++)
        for (int l = 0; l < World.LayerCount; l++)
            writer.Write(MemoryMarshal.AsBytes(session.Scent.Field(t, (LayerId)l)));

        WriteAntSettings(writer, session.AntSettings);
        WriteAnts(writer, session.Ants);
        writer.WriteChecksum();
    }

    public static void Save(SimulationSession session, string path)
    {
        string temp = path + ".tmp";
        using (var file = File.Create(temp)) Save(session, file);
        File.Move(temp, path, overwrite: true);
    }

    public static SimulationSession Load(string path)
    {
        using var file = File.OpenRead(path);
        return Load(file);
    }

    public static SimulationSession Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        EnsureLittleEndian();
        var reader = new ChecksumReader(stream);

        Span<byte> magic = stackalloc byte[8];
        reader.Read(magic);
        if (!magic.SequenceEqual(Magic))
            throw new MapFormatException(magic.StartsWith("ANTMAP"u8)
                ? "맵 파일입니다. 실험 상태 파일(.antexp)이 아닙니다."
                : "실험 상태 파일이 아닙니다(매직 값 불일치).");
        ushort version = reader.ReadUInt16();
        if (version == 0 || version > CurrentVersion)
            throw new MapFormatException($"지원하지 않는 실험 파일 버전입니다: {version} (지원: 1~{CurrentVersion}).");
        reader.ReadUInt16(); // flags

        long tick = reader.ReadInt64();
        double elapsed = reader.ReadDouble();
        if (tick < 0 || !double.IsFinite(elapsed) || elapsed < 0) throw new MapFormatException("틱 정보가 잘못되었습니다.");

        double tickSeconds = reader.ReadDouble();
        int typeCount = reader.ReadInt32();
        if (typeCount < 1 || typeCount > MaxScentTypes) throw new MapFormatException($"냄새 종류 수가 잘못되었습니다: {typeCount}.");
        var types = new ScentTypeSettings[typeCount];
        for (int i = 0; i < typeCount; i++)
        {
            string key = reader.ReadString();
            string name = reader.ReadString();
            bool hasSource = reader.ReadByte() != 0;
            var source = (QuantityKind)reader.ReadByte();
            types[i] = new ScentTypeSettings(key, name, hasSource ? source : null,
                reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
        }
        var settings = new ScentSettings { TickSeconds = tickSeconds, Types = types };
        var errors = settings.Validate();
        if (errors.Count > 0) throw new MapFormatException($"실험 설정이 잘못되었습니다: {string.Join(" ", errors)}");

        var initial = ReadWorld(reader);
        var current = ReadWorld(reader);
        if (initial.Config.Width != current.Config.Width || initial.Config.Height != current.Config.Height)
            throw new MapFormatException("시작 월드와 현재 월드의 크기가 다릅니다.");

        var scent = new ScentState(current.Config, typeCount);
        for (int t = 0; t < typeCount; t++)
        for (int l = 0; l < World.LayerCount; l++)
        {
            var field = scent.MutableField(t, (LayerId)l);
            reader.Read(MemoryMarshal.AsBytes(field));
            foreach (float v in field)
                if (!float.IsFinite(v) || v < 0) throw new MapFormatException("냄새 농도에 잘못된 값(음수 또는 NaN)이 있습니다.");
        }

        var antSettings = AntSettings.None;
        var ants = new AntPopulation();
        if (version >= 2)
        {
            antSettings = ReadAntSettings(reader);
            ReadAnts(reader, ants, current);
        }

        reader.VerifyChecksum();
        return new SimulationSession(initial, current, settings, scent, tick, elapsed, antSettings, ants);
    }

    private static void WriteAntSettings(ChecksumWriter writer, AntSettings s)
    {
        writer.WriteInt32(s.InitialCount);
        writer.WriteInt64((long)s.Seed);
        foreach (float v in new[]
        {
            s.BaseSpeed, s.SpeedVariation, s.BaseSize, s.SizeVariation, s.TurnIntervalMin, s.TurnIntervalMax,
            s.TurnIntervalVariation, s.WanderAngleDegrees, s.TurnRateDegreesPerSecond, s.SpeedFactorSmoothingSeconds,
            s.PassageUseProbability, 0f,
        })
            writer.WriteSingle(v);
        writer.WriteInt32(s.QuantitySpeedTiers.Count);
        foreach (var tier in s.QuantitySpeedTiers)
        {
            writer.WriteInt32(tier.MinQuantity);
            writer.WriteSingle(tier.Factor);
        }
    }

    private static AntSettings ReadAntSettings(ChecksumReader reader)
    {
        int count = reader.ReadInt32();
        ulong seed = (ulong)reader.ReadInt64();
        var v = new float[12];
        for (int i = 0; i < v.Length; i++) v[i] = reader.ReadSingle();
        int tierCount = reader.ReadInt32();
        if (tierCount < 1 || tierCount > 256) throw new MapFormatException($"개미 속도 구간 수가 잘못되었습니다: {tierCount}.");
        var tiers = new SpeedTier[tierCount];
        for (int i = 0; i < tierCount; i++) tiers[i] = new SpeedTier(reader.ReadInt32(), reader.ReadSingle());

        var settings = new AntSettings
        {
            InitialCount = count, Seed = seed,
            BaseSpeed = v[0], SpeedVariation = v[1], BaseSize = v[2], SizeVariation = v[3],
            TurnIntervalMin = v[4], TurnIntervalMax = v[5], TurnIntervalVariation = v[6],
            WanderAngleDegrees = v[7], TurnRateDegreesPerSecond = v[8], SpeedFactorSmoothingSeconds = v[9],
            PassageUseProbability = v[10], QuantitySpeedTiers = tiers,
        };
        var errors = settings.Validate();
        if (errors.Count > 0) throw new MapFormatException($"개미 설정이 잘못되었습니다: {string.Join(" ", errors)}");
        return settings;
    }

    private static void WriteAnts(ChecksumWriter writer, AntPopulation ants)
    {
        int n = ants.Count;
        writer.WriteInt32(n);
        writer.WriteInt32(ants.NextId);
        writer.Write(MemoryMarshal.AsBytes(ants.IdArray.AsSpan(0, n)));
        writer.Write(ants.CasteArray.AsSpan(0, n));
        writer.Write(ants.LayerArray.AsSpan(0, n));
        writer.Write(ants.MobilityArray.AsSpan(0, n));
        foreach (var array in FloatArrays(ants)) writer.Write(MemoryMarshal.AsBytes(array.AsSpan(0, n)));
        writer.Write(MemoryMarshal.AsBytes(ants.LastTileArray.AsSpan(0, n)));
        writer.Write(MemoryMarshal.AsBytes(ants.RngArray.AsSpan(0, n)));
    }

    private static void ReadAnts(ChecksumReader reader, AntPopulation ants, World world)
    {
        int n = reader.ReadInt32();
        int nextId = reader.ReadInt32();
        if (n < 0 || n > AntSettings.MaxCount) throw new MapFormatException($"개미 수가 잘못되었습니다: {n}.");
        ants.SetCount(n, nextId);
        reader.Read(MemoryMarshal.AsBytes(ants.IdArray.AsSpan(0, n)));
        reader.Read(ants.CasteArray.AsSpan(0, n));
        reader.Read(ants.LayerArray.AsSpan(0, n));
        reader.Read(ants.MobilityArray.AsSpan(0, n));
        foreach (var array in FloatArrays(ants)) reader.Read(MemoryMarshal.AsBytes(array.AsSpan(0, n)));
        reader.Read(MemoryMarshal.AsBytes(ants.LastTileArray.AsSpan(0, n)));
        reader.Read(MemoryMarshal.AsBytes(ants.RngArray.AsSpan(0, n)));

        // 불러온 개미가 규칙을 어기는 위치(맵 밖·물·고체)에 있으면 거부한다.
        for (int i = 0; i < n; i++)
        {
            if (ants.IdArray[i] < 0 || ants.IdArray[i] >= nextId) throw new MapFormatException($"개미 ID가 잘못되었습니다: {ants.IdArray[i]}.");
            if (ants.LayerArray[i] >= World.LayerCount || ants.CasteArray[i] != (byte)AntCaste.Worker || ants.MobilityArray[i] != (byte)AntMobility.Walk)
                throw new MapFormatException($"개미 {ants.IdArray[i]}의 레이어·계급·이동 능력이 잘못되었습니다.");
            var layer = world.GetLayer((LayerId)ants.LayerArray[i]);
            if (!float.IsFinite(ants.XArray[i]) || !float.IsFinite(ants.YArray[i]) ||
                !AntMovement.IsPassable(layer, ants.XArray[i], ants.YArray[i]))
                throw new MapFormatException($"개미 {ants.IdArray[i]}이 이동할 수 없는 위치에 있습니다.");
        }
    }

    private static float[][] FloatArrays(AntPopulation a) => new[]
    {
        a.XArray, a.YArray, a.PrevXArray, a.PrevYArray, a.HeadingArray, a.TargetHeadingArray,
        a.BaseSpeedArray, a.SpeedFactorArray, a.SizeArray, a.TurnTimerArray, a.TurnIntervalScaleArray,
    };

    private static void WriteWorld(ChecksumWriter writer, World world)
    {
        using var buffer = new MemoryStream();
        MapSerializer.Save(world, buffer);
        writer.WriteInt32((int)buffer.Length);
        writer.Write(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    private static World ReadWorld(ChecksumReader reader)
    {
        int length = reader.ReadInt32();
        if (length <= 0) throw new MapFormatException($"월드 데이터 길이가 잘못되었습니다: {length}.");
        var bytes = new byte[length];
        reader.Read(bytes);
        return MapSerializer.Load(new MemoryStream(bytes, writable: false));
    }

    private static void EnsureLittleEndian()
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("실험 파일은 리틀 엔디언 플랫폼에서만 지원합니다.");
    }
}
