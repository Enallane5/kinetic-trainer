using System.Buffers.Binary;
using System.Text;
using KineticTrainer.Core.Rides;
using static KineticTrainer.Recording.Fit.FitProfile;

namespace KineticTrainer.Recording.Fit;

/// <summary>
/// Writes a standard FIT activity file: 14-byte header, definition/data messages (little-endian), file CRC.
/// Message set: file_id, file_creator, device_info, event(start), record×N, event(stop), lap, session, activity.
/// All numeric encoding follows the FIT profile scales (time ×1000 ms, distance ×100 cm, speed ×1000 mm/s).
/// </summary>
public sealed class FitEncoder
{
    private const byte HeaderSize = 14;
    private const byte ProtocolVersion = 0x20; // 2.0
    private const ushort ProfileVersion = 2194; // 21.94

    public ushort Product { get; init; } = 1;

    public uint SerialNumber { get; init; } = 0x4B54_0001; // "KT"

    public string ProductName { get; init; } = "Kinetic Trainer";

    public ushort SoftwareVersion { get; init; } = 10; // 0.10

    public byte[] Encode(RideRecording ride)
    {
        var body = new MemoryStream();
        var w = new Writer(body);
        var s = ride.Summary;
        var startFit = ToFitTime(ride.StartedAt);
        var endFit = ToFitTime(ride.StoppedAt);
        var elapsedMs = (uint)Math.Round(s.ElapsedSeconds * 1000);

        // file_id (local 0)
        w.Definition(0, MesgFileId,
            (FileIdType, 1, BtEnum), (FileIdManufacturer, 2, BtUInt16), (FileIdProduct, 2, BtUInt16),
            (FileIdSerialNumber, 4, BtUInt32z), (FileIdTimeCreated, 4, BtUInt32), (FileIdProductName, 20, BtString));
        w.Data(0, b =>
        {
            b.U8(FileTypeActivity); b.U16(ManufacturerDevelopment); b.U16(Product); b.U32(SerialNumber); b.U32(startFit); b.Str(ProductName, 20);
        });

        // file_creator (local 1)
        w.Definition(1, MesgFileCreator, (FileCreatorSoftwareVersion, 2, BtUInt16));
        w.Data(1, b => b.U16(SoftwareVersion));

        // device_info (local 2)
        w.Definition(2, MesgDeviceInfo,
            (FieldTimestamp, 4, BtUInt32), (DeviceInfoDeviceIndex, 1, BtUInt8), (DeviceInfoManufacturer, 2, BtUInt16),
            (DeviceInfoProduct, 2, BtUInt16), (DeviceInfoSoftwareVersion, 2, BtUInt16), (DeviceInfoProductName, 20, BtString));
        w.Data(2, b =>
        {
            b.U32(startFit); b.U8(DeviceIndexCreator); b.U16(ManufacturerDevelopment); b.U16(Product); b.U16(SoftwareVersion); b.Str(ProductName, 20);
        });

        // event timer start (local 3)
        w.Definition(3, MesgEvent, (FieldTimestamp, 4, BtUInt32), (EventEvent, 1, BtEnum), (EventEventType, 1, BtEnum), (EventEventGroup, 1, BtUInt8));
        w.Data(3, b => { b.U32(startFit); b.U8(EventTimer); b.U8(EventTypeStart); b.U8(0); });

        // records (local 4)
        w.Definition(4, MesgRecord,
            (FieldTimestamp, 4, BtUInt32), (RecordDistance, 4, BtUInt32), (RecordSpeed, 2, BtUInt16), (RecordPower, 2, BtUInt16), (RecordCadence, 1, BtUInt8));
        uint lastTs = 0;
        foreach (var sample in ride.Samples)
        {
            var ts = ToFitTime(sample.Timestamp);
            if (ts < lastTs)
            {
                ts = lastTs; // FIT timestamps must not go backwards
            }
            lastTs = ts;
            w.Data(4, b =>
            {
                b.U32(ts);
                b.U32((uint)Math.Round(sample.DistanceMeters * 100));
                b.U16(sample.SpeedMetersPerSecond is { } v ? ClampU16(v * 1000) : InvalidUInt16);
                b.U16(sample.PowerWatts is { } p && p >= 0 ? ClampU16(p) : InvalidUInt16);
                b.U8(sample.CadenceRpm is { } c && c >= 0 ? ClampU8(c) : InvalidUInt8);
            });
        }

        // event timer stop_all
        w.Data(3, b => { b.U32(endFit); b.U8(EventTimer); b.U8(EventTypeStopAll); b.U8(0); });

        // lap (local 5)
        w.Definition(5, MesgLap,
            (FieldTimestamp, 4, BtUInt32), (FieldMessageIndex, 2, BtUInt16), (LapStartTime, 4, BtUInt32),
            (LapTotalElapsedTime, 4, BtUInt32), (LapTotalTimerTime, 4, BtUInt32), (LapTotalDistance, 4, BtUInt32),
            (LapAvgSpeed, 2, BtUInt16), (LapMaxSpeed, 2, BtUInt16), (LapAvgPower, 2, BtUInt16), (LapMaxPower, 2, BtUInt16),
            (LapNormalizedPower, 2, BtUInt16), (LapAvgCadence, 1, BtUInt8), (LapMaxCadence, 1, BtUInt8),
            (LapEvent, 1, BtEnum), (LapEventType, 1, BtEnum), (LapLapTrigger, 1, BtEnum), (LapSport, 1, BtEnum), (LapSubSport, 1, BtEnum));
        w.Data(5, b =>
        {
            b.U32(endFit); b.U16(0); b.U32(startFit);
            // total_timer_time is written equal to total_elapsed_time on purpose: the ride timer never pauses (sensor gaps
            // are left as gaps in the records; consumers derive moving time from speed).
            b.U32(elapsedMs); b.U32(elapsedMs); b.U32((uint)Math.Round(s.DistanceMeters * 100));
            b.U16(Speed(s.AverageSpeedMetersPerSecond)); b.U16(Speed(s.MaxSpeedMetersPerSecond));
            b.U16(Power(s.AveragePowerWatts)); b.U16(Power(s.MaxPowerWatts)); b.U16(Power(s.NormalizedPowerWatts));
            b.U8(Cadence(s.AverageCadenceRpm)); b.U8(Cadence(s.MaxCadenceRpm));
            b.U8(EventLap); b.U8(EventTypeStop); b.U8(LapTriggerSessionEnd); b.U8(SportCycling); b.U8(SubSportIndoorCycling);
        });

        // session (local 6)
        w.Definition(6, MesgSession,
            (FieldTimestamp, 4, BtUInt32), (FieldMessageIndex, 2, BtUInt16), (SessionStartTime, 4, BtUInt32),
            (SessionTotalElapsedTime, 4, BtUInt32), (SessionTotalTimerTime, 4, BtUInt32), (SessionTotalDistance, 4, BtUInt32),
            (SessionAvgSpeed, 2, BtUInt16), (SessionMaxSpeed, 2, BtUInt16), (SessionAvgPower, 2, BtUInt16), (SessionMaxPower, 2, BtUInt16),
            (SessionNormalizedPower, 2, BtUInt16), (SessionFirstLapIndex, 2, BtUInt16), (SessionNumLaps, 2, BtUInt16),
            (SessionAvgCadence, 1, BtUInt8), (SessionMaxCadence, 1, BtUInt8),
            (SessionEvent, 1, BtEnum), (SessionEventType, 1, BtEnum), (SessionSport, 1, BtEnum), (SessionSubSport, 1, BtEnum), (SessionTrigger, 1, BtEnum));
        w.Data(6, b =>
        {
            b.U32(endFit); b.U16(0); b.U32(startFit);
            b.U32(elapsedMs); b.U32(elapsedMs); b.U32((uint)Math.Round(s.DistanceMeters * 100));
            b.U16(Speed(s.AverageSpeedMetersPerSecond)); b.U16(Speed(s.MaxSpeedMetersPerSecond));
            b.U16(Power(s.AveragePowerWatts)); b.U16(Power(s.MaxPowerWatts)); b.U16(Power(s.NormalizedPowerWatts));
            b.U16(0); b.U16(1);
            b.U8(Cadence(s.AverageCadenceRpm)); b.U8(Cadence(s.MaxCadenceRpm));
            b.U8(EventSession); b.U8(EventTypeStop); b.U8(SportCycling); b.U8(SubSportIndoorCycling); b.U8(SessionTriggerActivityEnd);
        });

        // activity (local 7)
        w.Definition(7, MesgActivity,
            (FieldTimestamp, 4, BtUInt32), (ActivityTotalTimerTime, 4, BtUInt32), (ActivityLocalTimestamp, 4, BtUInt32),
            (ActivityNumSessions, 2, BtUInt16), (ActivityType, 1, BtEnum), (ActivityEvent, 1, BtEnum), (ActivityEventType, 1, BtEnum));
        var local = ride.StoppedAt.ToLocalTime();
        var localFit = (uint)Math.Floor((local.DateTime - Epoch.DateTime).TotalSeconds);
        w.Data(7, b =>
        {
            b.U32(endFit); b.U32(elapsedMs); b.U32(localFit); b.U16(1); b.U8(ActivityTypeManual); b.U8(EventActivity); b.U8(EventTypeStop);
        });

        var data = body.ToArray();
        var file = new byte[HeaderSize + data.Length + 2];
        file[0] = HeaderSize;
        file[1] = ProtocolVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), ProfileVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)data.Length);
        Encoding.ASCII.GetBytes(".FIT").CopyTo(file, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(12), FitCrc.Compute(file.AsSpan(0, 12)));
        data.CopyTo(file, HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(HeaderSize + data.Length), FitCrc.Compute(file.AsSpan(0, HeaderSize + data.Length)));
        return file;
    }

    private static ushort Speed(double? mps) => mps is { } v ? ClampU16(v * 1000) : InvalidUInt16;

    private static ushort Power(double? w) => w is { } v && v >= 0 ? ClampU16(v) : InvalidUInt16;

    private static byte Cadence(double? rpm) => rpm is { } v && v >= 0 ? ClampU8(v) : InvalidUInt8;

    private static ushort ClampU16(double v) => (ushort)Math.Clamp(Math.Round(v), 0, InvalidUInt16 - 1);

    private static byte ClampU8(double v) => (byte)Math.Clamp(Math.Round(v), 0, InvalidUInt8 - 1);

    private sealed class Writer(MemoryStream stream)
    {
        public void Definition(byte local, ushort globalNum, params (byte Field, byte Size, byte BaseType)[] fields)
        {
            stream.WriteByte((byte)(0x40 | local));
            stream.WriteByte(0);            // reserved
            stream.WriteByte(0);            // architecture: little-endian
            Span<byte> g = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(g, globalNum);
            stream.Write(g);
            stream.WriteByte((byte)fields.Length);
            foreach (var (field, size, baseType) in fields)
            {
                stream.WriteByte(field);
                stream.WriteByte(size);
                stream.WriteByte(baseType);
            }
        }

        public void Data(byte local, Action<FieldWriter> write)
        {
            stream.WriteByte(local);
            write(new FieldWriter(stream));
        }
    }

    private sealed class FieldWriter(MemoryStream stream)
    {
        public void U8(byte v) => stream.WriteByte(v);

        public void U16(ushort v)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, v);
            stream.Write(b);
        }

        public void U32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            stream.Write(b);
        }

        /// <summary>Fixed-width, NUL-terminated, NUL-padded string field.</summary>
        public void Str(string s, int size)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            var n = Math.Min(bytes.Length, size - 1);
            stream.Write(bytes, 0, n);
            for (var i = n; i < size; i++)
            {
                stream.WriteByte(0);
            }
        }
    }
}
