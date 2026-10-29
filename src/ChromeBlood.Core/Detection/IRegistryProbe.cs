namespace ChromeBlood.Core.Detection;

public enum RegistryHiveScope
{
    CurrentUser,
    LocalMachine
}

public enum RegistryViewScope
{
    Registry32,
    Registry64
}

public enum RegistryValueType
{
    Text,
    ExpandString,
    DWord,
    QWord,
    MultiString,
    Binary
}

public sealed record RegistryValueId
{
    public RegistryValueId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record RegistryValueDefinition(
    RegistryValueId Id,
    RegistryHiveScope Hive,
    RegistryViewScope View,
    string SubKey,
    string ValueName,
    RegistryValueType Type);

public sealed record RegistryValueData(
    RegistryValueType Type,
    string? Text,
    int? DWord,
    long? QWord,
    IReadOnlyList<string>? MultiString,
    IReadOnlyList<byte>? Binary)
{
    public static RegistryValueData FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new RegistryValueData(RegistryValueType.Text, value, null, null, null, null);
    }

    public static RegistryValueData FromExpandString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new RegistryValueData(RegistryValueType.ExpandString, value, null, null, null, null);
    }

    public static RegistryValueData FromDWord(int value)
        => new(RegistryValueType.DWord, null, value, null, null, null);

    public static RegistryValueData FromQWord(long value)
        => new(RegistryValueType.QWord, null, null, value, null, null);

    public static RegistryValueData FromMultiString(IEnumerable<string> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new RegistryValueData(RegistryValueType.MultiString, null, null, null, value.ToArray(), null);
    }

    public static RegistryValueData FromBinary(IEnumerable<byte> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new RegistryValueData(RegistryValueType.Binary, null, null, null, null, value.ToArray());
    }
}

public sealed record RegistryValueObservation(ProbeStatus Status, RegistryValueData? Value)
{
    public static RegistryValueObservation Available(RegistryValueData value) => new(ProbeStatus.Available, value);

    public static RegistryValueObservation Missing() => new(ProbeStatus.Missing, null);

    public static RegistryValueObservation AccessDenied() => new(ProbeStatus.AccessDenied, null);

    public static RegistryValueObservation Unavailable() => new(ProbeStatus.Unavailable, null);

    public static RegistryValueObservation NotAllowed() => new(ProbeStatus.NotAllowed, null);
}

public interface IRegistryProbe
{
    RegistryValueObservation ReadValue(RegistryValueId definitionId);
}
