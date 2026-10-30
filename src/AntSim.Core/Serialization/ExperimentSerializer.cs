using System.Runtime.InteropServices;
using AntSim.Core.Ants;
using AntSim.Core.Scent;
using AntSim.Core.Simulation;
using AntSim.Core.Worlds;

namespace AntSim.Core.Serialization;

/// <summary>
/// 시뮬레이션 실험 상태 파일(.antexp). 맵 파일(.antmap)과 분리되어 있으며, 실험을 그대로 이어서 실행할 수 있도록
/// 설정·틱·시작 월드·현재 월드·냄새 필드 전체를 담는다. 모든 정수·실수는 리틀 엔디언.
/// v2는 v1 끝(냄새 다음, 체크섬 앞)에 개미 설정과 개체 상태를 추가했다. v1 파일은 월드·냄새·틱·경과 시간을 그대로 두고
/// 개미 0마리(<see cref="AntSettings.None"/>)로 불러온다. 저장은 항상 v2다.
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
    /// <summary>저장 형식 버전. 저장은 항상 이 버전(v2)으로 하고, 불러오기는 v1(개미 구간 없음)과 v2를 모두 지원한다.</summary>
    public const ushort CurrentVersion = 2;

    /// <summary>냄새 종류 수 상한(<see cref="ScentSettings.MaxTypes"/>와 같다: 저장할 수 있으면 불러올 수 있다).</summary>
    public const int MaxScentTypes = ScentSettings.MaxTypes;
    private static ReadOnlySpan<byte> Magic => "ANTEXP\0\0"u8;

    public static void Save(SimulationSession session, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(stream);
        EnsureLittleEndian();
        ValidateForSave(session);
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
        ArgumentNullException.ThrowIfNull(session);
        // 저장할 수 없는 상태는 임시 파일을 만들기 전에 거부한다(기존 파일 보존).
        ValidateForSave(session);
        string temp = path + ".tmp";
        try
        {
            using (var file = File.Create(temp)) Save(session, file);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    /// <summary>
    /// 저장하면 같은 구현으로 다시 불러올 수 있는 상태인지 쓰기 전에 검사한다. 불가능하면 <see cref="InvalidOperationException"/>.
    /// (설정: <see cref="ScentSettings.Validate"/> — 종류 수·문자열 길이·계수 범위, 농도: 0 ~ <see cref="ScentState.MaxConcentration"/>,
    /// 월드: 내장 맵 길이가 int 범위)
    /// </summary>
    public static void ValidateForSave(SimulationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var errors = session.Settings.Validate();
        if (errors.Count > 0)
            throw new InvalidOperationException($"저장할 수 없는 설정입니다: {string.Join(" ", errors)}");
        if (!session.Scent.AllValuesInSupportedRange())
            throw new InvalidOperationException(
                $"저장할 수 없는 냄새 농도가 있습니다(음수·NaN·무한대 또는 {ScentState.MaxConcentration:0e0} 초과).");
        foreach (var world in new[] { session.InitialWorld, session.World })
        {
            long mapBytes = MapSerializer.EncodedLength(world);
            if (mapBytes > int.MaxValue)
                throw new InvalidOperationException($"월드가 너무 커서 실험 파일에 담을 수 없습니다({mapBytes}바이트).");
        }
    }

    public static SimulationSession Load(string path)
    {
        using var file = File.OpenRead(path);
        return Load(file);
    }

    /// <summary>
    /// 스트림의 현재 위치에서 실험 상태 레코드 하나를 읽는다. 체크섬(레코드의 마지막 8바이트)을 읽은 직후 반환하며 그 뒤는 읽지 않으므로,
    /// 같은 스트림에 이어진 다음 레코드를 다시 Load할 수 있다(seek 가능 여부와 무관). 형식 오류는 <see cref="MapFormatException"/>.
    /// </summary>
    public static SimulationSession Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        EnsureLittleEndian();
        // 크기 필드를 믿고 큰 배열을 먼저 만들지 않도록 매 단계 실제로 남은(seek 불가면 실제로 받은) 바이트와 비교한다(F01/E01).
        // 레코드 하나만 읽고 체크섬 직후에서 멈춘다(끝까지 미리 읽지 않음, N01).
        stream = StreamLimits.ForRecord(stream);
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
        // 종류마다 최소 문자열 길이 2×4 + 발생원 2 + 계수 4×8 = 42바이트.
        StreamLimits.Require(stream, 42L * typeCount, "냄새 설정");
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

        // 냄새 배열(Current/Next)을 만들기 전에 농도 본문 전체가 실제로 있는지 확인한다.
        // 종류 ≤ 256, 타일 ≤ 2^28이므로 long 곱은 넘치지 않는다.
        long scentBytes = (long)typeCount * World.LayerCount * current.Config.TileCount * sizeof(float);
        StreamLimits.Require(stream, scentBytes + 8, "냄새 농도");
        var scent = new ScentState(current.Config, typeCount);
        for (int t = 0; t < typeCount; t++)
        for (int l = 0; l < World.LayerCount; l++)
        {
            var field = scent.MutableField(t, (LayerId)l);
            reader.Read(MemoryMarshal.AsBytes(field));
            foreach (float v in field)
                if (!(v >= 0 && v <= ScentState.MaxConcentration))
                    throw new MapFormatException($"냄새 농도에 지원 범위(0 ~ {ScentState.MaxConcentration:0e0}) 밖의 값(음수·NaN·무한대 포함)이 있습니다.");
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

    /// <summary>
    /// 내장 맵을 읽는다. 선언된 길이를 남은 파일 길이로 먼저 검증하고, 바이트 배열로 통째로 복사하지 않고
    /// 그 구간만 읽는 스트림으로 맵 로더에 넘긴다(맵 로더도 본문 크기를 할당 전에 검증한다).
    /// </summary>
    private static World ReadWorld(ChecksumReader reader)
    {
        int length = reader.ReadInt32();
        if (length <= 0) throw new MapFormatException($"월드 데이터 길이가 잘못되었습니다: {length}.");
        StreamLimits.Require(reader.BaseStream, length, "내장 월드");
        var section = new ChecksumSectionStream(reader, length);
        var world = MapSerializer.Load(section);
        if (section.Remaining != 0)
            throw new MapFormatException($"내장 월드 길이가 맵 데이터와 맞지 않습니다(남은 {section.Remaining}바이트).");
        return world;
    }

    private static void EnsureLittleEndian()
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("실험 파일은 리틀 엔디언 플랫폼에서만 지원합니다.");
    }
}
