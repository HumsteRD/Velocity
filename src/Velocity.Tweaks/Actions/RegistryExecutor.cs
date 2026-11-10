using Velocity.Core;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Velocity.Tweaks.Actions;

/// <summary>Чтение и запись реестра с фиксацией прежнего состояния.</summary>
[SupportedOSPlatform("windows")]
public static class RegistryExecutor
{
    public static RegistryHive ParseHive(string hive) => hive.ToUpperInvariant() switch
    {
        "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
        "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
        "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
        "HKU" or "HKEY_USERS" => RegistryHive.Users,
        _ => throw new ArgumentException(L10n.F("Неизвестный куст реестра: {0}", hive))
    };

    /// <summary>Снимает текущее состояние значения. Возвращает null в Value, если его не было.</summary>
    public static StateRecord Capture(RegistryAction action)
    {
        var hive = ParseHive(action.Hive);
        object? raw = null;
        string? kind = null;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(action.Path);
            if (key is not null)
            {
                raw = key.GetValue(action.Name);
                if (raw is not null) kind = key.GetValueKind(action.Name).ToString();
            }
            else
            {
                // Ключа не было вовсе: запоминаем, сколько уровней пути создаст применение,
                // чтобы откат убрал за собой и пустые ключи, а не только значения.
                kind = MissingKeyMarker + MissingDepth(baseKey, action.Path);
            }
        }
        catch { /* нет доступа — фиксируем как «значения нет» */ }

        return new StateRecord
        {
            Kind = "registry",
            Target = $"{action.Hive}\\{action.Path}\\{action.Name}",
            Value = Serialize(raw),
            ValueKind = kind
        };
    }

    public static void Apply(RegistryAction action)
    {
        var hive = ParseHive(action.Hive);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

        if (action.Value is null)
        {
            using var existing = baseKey.OpenSubKey(action.Path, writable: true);
            existing?.DeleteValue(action.Name, throwOnMissingValue: false);
            return;
        }

        using var key = baseKey.CreateSubKey(action.Path, writable: true)
            ?? throw new InvalidOperationException(L10n.F("Не удалось открыть ключ {0}", action.Path));

        key.SetValue(action.Name, Convert(action.Value, action.Kind), MapKind(action.Kind));
    }

    /// <summary>Возвращает значение к состоянию из снимка.</summary>
    public static void Restore(StateRecord record)
    {
        var parts = record.Target.Split('\\');
        if (parts.Length < 3) return;

        var hive = ParseHive(parts[0]);
        var name = parts[^1];
        var path = string.Join('\\', parts[1..^1]);

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

        if (record.Value is null)
        {
            // Значения не было — удаляем то, что мы создали.
            using (var existing = baseKey.OpenSubKey(path, writable: true))
                existing?.DeleteValue(name, throwOnMissingValue: false);

            if (record.ValueKind?.StartsWith(MissingKeyMarker, StringComparison.Ordinal) == true
                && int.TryParse(record.ValueKind[MissingKeyMarker.Length..], out var depth))
                DeleteEmptyKeys(baseKey, path, depth);
            return;
        }

        using var key = baseKey.CreateSubKey(path, writable: true);
        var kind = Enum.TryParse<RegistryValueKind>(record.ValueKind, out var k) ? k : RegistryValueKind.DWord;
        key?.SetValue(name, Deserialize(record.Value, kind), kind);
    }

    private const string MissingKeyMarker = "KeyMissing:";

    private static int MissingDepth(RegistryKey baseKey, string path)
    {
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int existing = parts.Length - 1; existing > 0; existing--)
        {
            using var key = baseKey.OpenSubKey(string.Join('\\', parts[..existing]));
            if (key is not null) return parts.Length - existing;
        }
        return parts.Length;
    }

    /// <summary>Удаляет созданные применением ключи снизу вверх — и только пока они пусты.</summary>
    private static void DeleteEmptyKeys(RegistryKey baseKey, string path, int depth)
    {
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < depth && parts.Length - i > 1; i++)
        {
            var current = string.Join('\\', parts[..(parts.Length - i)]);
            try
            {
                using (var key = baseKey.OpenSubKey(current))
                {
                    if (key is null) continue;
                    if (key.ValueCount > 0 || key.SubKeyCount > 0) return;
                }
                baseKey.DeleteSubKey(current, throwOnMissingSubKey: false);
            }
            catch { return; }
        }
    }

    /// <summary>Совпадает ли текущее значение с ожидаемым (для detect / verify).</summary>
    public static bool Check(RegistryAction action)
    {
        var current = Capture(action).Value;

        // Твики вида «убрать из автозагрузки» применены ровно тогда,
        // когда значения в реестре больше нет.
        if (action.ExpectAbsent) return current is null;

        var expected = action.Expected ?? action.Value;
        if (expected is null) return false;

        return current is not null && current.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static RegistryValueKind MapKind(string kind) => kind.ToLowerInvariant() switch
    {
        "dword" => RegistryValueKind.DWord,
        "qword" => RegistryValueKind.QWord,
        "string" or "sz" => RegistryValueKind.String,
        "expand_string" => RegistryValueKind.ExpandString,
        "multi_string" => RegistryValueKind.MultiString,
        "binary" => RegistryValueKind.Binary,
        _ => RegistryValueKind.DWord
    };

    private static object Convert(string value, string kind) => kind.ToLowerInvariant() switch
    {
        // DWORD может приезжать как 0xFFFFFFFF — уходим через uint, иначе int переполнится.
        "dword" => unchecked((int)ParseUInt(value)),
        "qword" => (long)ParseULong(value),
        "multi_string" => value.Split('|', StringSplitOptions.RemoveEmptyEntries),
        "binary" => System.Convert.FromHexString(value.Replace(" ", "")),
        _ => value
    };

    private static uint ParseUInt(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? uint.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : uint.Parse(value, CultureInfo.InvariantCulture);

    private static ulong ParseULong(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? ulong.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : ulong.Parse(value, CultureInfo.InvariantCulture);

    private static string? Serialize(object? raw) => raw switch
    {
        null => null,
        byte[] bytes => System.Convert.ToHexString(bytes),
        string[] strings => string.Join('|', strings),
        int i => unchecked((uint)i).ToString(CultureInfo.InvariantCulture),
        _ => raw.ToString()
    };

    private static object Deserialize(string value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => unchecked((int)ParseUInt(value)),
        RegistryValueKind.QWord => (long)ParseULong(value),
        RegistryValueKind.Binary => System.Convert.FromHexString(value),
        RegistryValueKind.MultiString => value.Split('|'),
        _ => value
    };
}
