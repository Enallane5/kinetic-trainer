namespace KineticTrainer.Recording.Fit;

/// <summary>
/// The slice of the Garmin FIT profile this app writes. Numbers are from the FIT SDK Profile (global message numbers,
/// field definition numbers, base types, enum values); docs/FIT.md lists the profile rows used and how they were verified.
/// </summary>
public static class FitProfile
{
    /// <summary>FIT timestamps count seconds from 1989-12-31T00:00:00Z.</summary>
    public static readonly DateTimeOffset Epoch = new(1989, 12, 31, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Whole seconds since the FIT epoch, truncated: a sample at t+0.5 s belongs to second t (keeps 2 Hz data at exactly two records per second).</summary>
    public static uint ToFitTime(DateTimeOffset t) => (uint)Math.Floor((t - Epoch).TotalSeconds);

    public static DateTimeOffset FromFitTime(uint seconds) => Epoch.AddSeconds(seconds);

    // Base types (FIT SDK Fit.cs): high bit set = multi-byte / endian-sensitive.
    public const byte BtEnum = 0x00;
    public const byte BtSInt8 = 0x01;
    public const byte BtUInt8 = 0x02;
    public const byte BtSInt16 = 0x83;
    public const byte BtUInt16 = 0x84;
    public const byte BtSInt32 = 0x85;
    public const byte BtUInt32 = 0x86;
    public const byte BtString = 0x07;
    public const byte BtUInt8z = 0x0A;
    public const byte BtUInt16z = 0x8B;
    public const byte BtUInt32z = 0x8C;

    public const byte InvalidUInt8 = 0xFF;
    public const ushort InvalidUInt16 = 0xFFFF;
    public const uint InvalidUInt32 = 0xFFFFFFFF;

    // Global message numbers.
    public const ushort MesgFileId = 0;
    public const ushort MesgSession = 18;
    public const ushort MesgLap = 19;
    public const ushort MesgRecord = 20;
    public const ushort MesgEvent = 21;
    public const ushort MesgDeviceInfo = 23;
    public const ushort MesgActivity = 34;
    public const ushort MesgFileCreator = 49;

    public const byte FieldTimestamp = 253;
    public const byte FieldMessageIndex = 254;

    // file_id
    public const byte FileIdType = 0;
    public const byte FileIdManufacturer = 1;
    public const byte FileIdProduct = 2;
    public const byte FileIdSerialNumber = 3;
    public const byte FileIdTimeCreated = 4;
    public const byte FileIdProductName = 8;
    public const byte FileTypeActivity = 4;
    public const ushort ManufacturerDevelopment = 255;

    // file_creator
    public const byte FileCreatorSoftwareVersion = 0;

    // device_info
    public const byte DeviceInfoDeviceIndex = 0;
    public const byte DeviceInfoManufacturer = 2;
    public const byte DeviceInfoProduct = 4;
    public const byte DeviceInfoSoftwareVersion = 5;
    public const byte DeviceInfoProductName = 27;
    public const byte DeviceIndexCreator = 0;

    // event
    public const byte EventEvent = 0;
    public const byte EventEventType = 1;
    public const byte EventEventGroup = 4;
    public const byte EventTimer = 0;
    public const byte EventSession = 8;
    public const byte EventLap = 9;
    public const byte EventActivity = 26;
    public const byte EventTypeStart = 0;
    public const byte EventTypeStop = 1;
    public const byte EventTypeStopAll = 4;

    // record
    public const byte RecordCadence = 4;
    public const byte RecordDistance = 5;
    public const byte RecordSpeed = 6;
    public const byte RecordPower = 7;

    // lap
    public const byte LapEvent = 0;
    public const byte LapEventType = 1;
    public const byte LapStartTime = 2;
    public const byte LapTotalElapsedTime = 7;
    public const byte LapTotalTimerTime = 8;
    public const byte LapTotalDistance = 9;
    public const byte LapAvgSpeed = 13;
    public const byte LapMaxSpeed = 14;
    public const byte LapAvgCadence = 17;   // profile: lap field 17 = avg_cadence
    public const byte LapMaxCadence = 18;   // profile: lap field 18 = max_cadence
    public const byte LapAvgPower = 19;
    public const byte LapMaxPower = 20;
    public const byte LapLapTrigger = 24;
    public const byte LapSport = 25;
    public const byte LapNormalizedPower = 33;
    public const byte LapSubSport = 39;
    public const byte LapTriggerSessionEnd = 7;

    // session
    public const byte SessionEvent = 0;
    public const byte SessionEventType = 1;
    public const byte SessionStartTime = 2;
    public const byte SessionSport = 5;
    public const byte SessionSubSport = 6;
    public const byte SessionTotalElapsedTime = 7;
    public const byte SessionTotalTimerTime = 8;
    public const byte SessionTotalDistance = 9;
    public const byte SessionAvgSpeed = 14;
    public const byte SessionMaxSpeed = 15;
    public const byte SessionAvgCadence = 18;
    public const byte SessionMaxCadence = 19;
    public const byte SessionAvgPower = 20;
    public const byte SessionMaxPower = 21;
    public const byte SessionFirstLapIndex = 25;
    public const byte SessionNumLaps = 26;
    public const byte SessionTrigger = 28;
    public const byte SessionNormalizedPower = 34;
    public const byte SessionTriggerActivityEnd = 0;

    // activity
    public const byte ActivityTotalTimerTime = 0;
    public const byte ActivityNumSessions = 1;
    public const byte ActivityType = 2;
    public const byte ActivityEvent = 3;
    public const byte ActivityEventType = 4;
    public const byte ActivityLocalTimestamp = 5;
    public const byte ActivityTypeManual = 0;

    public const byte SportCycling = 2;
    public const byte SubSportIndoorCycling = 6;
}
