using System.Security;
using Microsoft.Win32;

namespace ChromeBlood.Core.Detection;

public sealed class WindowsRegistryProbe : IRegistryProbe
{
    private readonly Dictionary<RegistryValueId, RegistryValueDefinition> _definitions;

    public WindowsRegistryProbe(IEnumerable<RegistryValueDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        Dictionary<RegistryValueId, RegistryValueDefinition> allowlist = [];
        foreach (RegistryValueDefinition definition in definitions)
        {
            ValidateDefinition(definition);
            if (!allowlist.TryAdd(definition.Id, definition))
            {
                throw new ArgumentException("Registry definition IDs must be unique.", nameof(definitions));
            }
        }

        _definitions = allowlist;
    }

    public RegistryValueObservation ReadValue(RegistryValueId definitionId)
    {
        ArgumentNullException.ThrowIfNull(definitionId);
        if (!_definitions.TryGetValue(definitionId, out RegistryValueDefinition? definition))
        {
            return RegistryValueObservation.NotAllowed();
        }

        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(GetHive(definition.Hive), GetView(definition.View));
            using RegistryKey? key = baseKey.OpenSubKey(definition.SubKey, false);
            if (key is null || !ContainsValueName(key.GetValueNames(), definition.ValueName))
            {
                return RegistryValueObservation.Missing();
            }

            RegistryValueKind actualKind = key.GetValueKind(definition.ValueName);
            if (GetValueType(actualKind) != definition.Type)
            {
                return RegistryValueObservation.Unavailable();
            }

            object? value = key.GetValue(definition.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value is null
                ? RegistryValueObservation.Missing()
                : RegistryValueObservation.Available(ToValueData(definition.Type, value));
        }
        catch (UnauthorizedAccessException)
        {
            return RegistryValueObservation.AccessDenied();
        }
        catch (SecurityException)
        {
            return RegistryValueObservation.AccessDenied();
        }
        catch (IOException)
        {
            return RegistryValueObservation.Unavailable();
        }
    }

    internal static bool ContainsValueName(IEnumerable<string> valueNames, string expectedValueName)
    {
        ArgumentNullException.ThrowIfNull(valueNames);
        ArgumentNullException.ThrowIfNull(expectedValueName);
        return valueNames.Contains(expectedValueName, StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateDefinition(RegistryValueDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(definition.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.SubKey);
        ArgumentNullException.ThrowIfNull(definition.ValueName);
        if (definition.SubKey.StartsWith('\\')
            || definition.SubKey.StartsWith('/')
            || definition.SubKey.Split(['\\', '/']).Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Registry definitions must name a local relative subkey.", nameof(definition));
        }
    }

    private static RegistryHive GetHive(RegistryHiveScope hive)
        => hive switch
        {
            RegistryHiveScope.CurrentUser => RegistryHive.CurrentUser,
            RegistryHiveScope.LocalMachine => RegistryHive.LocalMachine,
            _ => throw new ArgumentOutOfRangeException(nameof(hive))
        };

    private static RegistryView GetView(RegistryViewScope view)
        => view switch
        {
            RegistryViewScope.Registry32 => RegistryView.Registry32,
            RegistryViewScope.Registry64 => RegistryView.Registry64,
            _ => throw new ArgumentOutOfRangeException(nameof(view))
        };

    private static RegistryValueType GetValueType(RegistryValueKind kind)
        => kind switch
        {
            RegistryValueKind.String => RegistryValueType.Text,
            RegistryValueKind.ExpandString => RegistryValueType.ExpandString,
            RegistryValueKind.DWord => RegistryValueType.DWord,
            RegistryValueKind.QWord => RegistryValueType.QWord,
            RegistryValueKind.MultiString => RegistryValueType.MultiString,
            RegistryValueKind.Binary => RegistryValueType.Binary,
            _ => throw new InvalidDataException("The registry value kind is not supported.")
        };

    private static RegistryValueData ToValueData(RegistryValueType type, object value)
        => type switch
        {
            RegistryValueType.Text => RegistryValueData.FromString((string)value),
            RegistryValueType.ExpandString => RegistryValueData.FromExpandString((string)value),
            RegistryValueType.DWord => RegistryValueData.FromDWord((int)value),
            RegistryValueType.QWord => RegistryValueData.FromQWord((long)value),
            RegistryValueType.MultiString => RegistryValueData.FromMultiString((string[])value),
            RegistryValueType.Binary => RegistryValueData.FromBinary((byte[])value),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
}
