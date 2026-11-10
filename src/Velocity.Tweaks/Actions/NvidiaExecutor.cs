using Velocity.Core;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Velocity.Tweaks.Actions;

/// <summary>
/// Настройки драйвера NVIDIA через NVAPI (подсистема DRS — Driver Settings).
/// Это тот же механизм, которым пользуются «Панель управления NVIDIA»
/// и NVIDIA Profile Inspector.
///
/// Осторожность здесь важнее полноты: если nvapi64.dll нет, видеокарта не NVIDIA
/// или любой вызов вернул ошибку — молча ничего не делаем. Испортить пользователю
/// профиль драйвера хуже, чем не применить твик.
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe class NvidiaExecutor
{
    // ─────────────── Идентификаторы функций NVAPI ───────────────
    private const uint FN_Initialize = 0x0150E828;
    private const uint FN_Unload = 0xD22BDD7E;
    private const uint FN_DRS_CreateSession = 0x0694D52E;
    private const uint FN_DRS_DestroySession = 0xDAD9CFF8;
    private const uint FN_DRS_LoadSettings = 0x375DBD6B;
    private const uint FN_DRS_SaveSettings = 0xFCBC7E14;
    private const uint FN_DRS_GetBaseProfile = 0xDA8466A0;
    private const uint FN_DRS_SetSetting = 0x577DD202;
    private const uint FN_DRS_GetSetting = 0x73BF8338;
    private const uint FN_DRS_DeleteProfileSetting = 0xE4A26362;

    // ─────────────── Раскладка NVDRS_SETTING версии 1 ───────────────
    // Структура фиксированная, смещения считаны из заголовка nvapi.h:
    //   0     version            (4)
    //   4     settingName        (4096 = 2048 × UTF-16)
    //   4100  settingId          (4)
    //   4104  settingType        (4)
    //   4108  settingLocation    (4)
    //   4112  isCurrentPredefined(4)
    //   4116  isPredefinedValid  (4)
    //   4120  predefinedValue    (4100 — объединение)
    //   8220  currentValue       (4100 — объединение)
    private const int SettingSize = 12320;
    private const int OffSettingId = 4100;
    private const int OffSettingType = 4104;
    private const int OffCurrentValue = 8220;

    // NVAPI кодирует версию структуры как «размер | (номер версии << 16)».
    private const uint NVDRS_SETTING_VER = SettingSize | (1u << 16);

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface64(uint offset);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnSimple();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnHandle(out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnSession(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnProfile(IntPtr session, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnSetSetting(IntPtr session, IntPtr profile, byte* setting);
    // Внимание: у GetSetting четыре параметра — идентификатор настройки передаётся
    // ОТДЕЛЬНО, а не читается из структуры. С трёхпараметрической сигнатурой вызов
    // формально «удаётся», но значение читается из мусора в регистре.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnGetSetting(IntPtr session, IntPtr profile, uint settingId, byte* setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnDeleteSetting(IntPtr session, IntPtr profile, uint settingId);

    private static T? Resolve<T>(uint id) where T : Delegate
    {
        var ptr = QueryInterface64(id);
        return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    /// <summary>Доступен ли драйвер NVIDIA. Проверяется один раз и кэшируется.</summary>
    public static bool IsAvailable => _available ??= Probe();
    private static bool? _available;

    private static bool Probe()
    {
        try
        {
            var init = Resolve<FnSimple>(FN_Initialize);
            return init is not null && init() == 0;
        }
        catch (DllNotFoundException) { return false; }   // драйвера NVIDIA нет
        catch (EntryPointNotFoundException) { return false; }
        catch { return false; }
    }

    /// <summary>Читает текущее значение настройки. null — настройка не задана или NVAPI недоступен.</summary>
    public static uint? Read(uint settingId)
    {
        if (!IsAvailable) return null;

        return WithProfile((session, profile) =>
        {
            var get = Resolve<FnGetSetting>(FN_DRS_GetSetting);
            if (get is null) return (uint?)null;

            var buffer = stackalloc byte[SettingSize];
            new Span<byte>(buffer, SettingSize).Clear();
            *(uint*)buffer = NVDRS_SETTING_VER;

            // NVAPI_SETTING_NOT_FOUND (-166) — настройка просто не задана в профиле,
            // это нормальное состояние, а не ошибка.
            return get(session, profile, settingId, buffer) == 0
                ? *(uint*)(buffer + OffCurrentValue)
                : null;
        });
    }

    public static void Apply(NvidiaProfileAction action)
    {
        if (!IsAvailable)
            throw new InvalidOperationException(L10n.T("Драйвер NVIDIA не обнаружен"));

        if (!uint.TryParse(action.Setting.Replace("0x", ""),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var settingId))
            throw new ArgumentException(L10n.F("Идентификатор настройки «{0}» не распознан", action.Setting));

        Write(settingId, action.Value);
    }

    public static void Write(uint settingId, uint value)
    {
        var ok = WithProfile((session, profile) =>
        {
            var set = Resolve<FnSetSetting>(FN_DRS_SetSetting);
            var save = Resolve<FnSession>(FN_DRS_SaveSettings);
            if (set is null || save is null) return false;

            var buffer = stackalloc byte[SettingSize];
            new Span<byte>(buffer, SettingSize).Clear();
            *(uint*)buffer = NVDRS_SETTING_VER;
            *(uint*)(buffer + OffSettingId) = settingId;
            *(uint*)(buffer + OffSettingType) = 0;       // NVDRS_DWORD_TYPE
            *(uint*)(buffer + OffCurrentValue) = value;

            if (set(session, profile, buffer) != 0) return false;
            return save(session) == 0;
        });

        if (!ok) throw new InvalidOperationException(L10n.F("NVAPI отклонил настройку 0x{0:X8}", settingId));
    }

    /// <summary>
    /// Убирает настройку из профиля, возвращая её к значению по умолчанию.
    ///
    /// Это не то же самое, что записать ноль: если до нас настройки в профиле не было,
    /// откат обязан её удалить, иначе мы навсегда зафиксируем чужое значение.
    /// </summary>
    public static bool Delete(uint settingId)
    {
        if (!IsAvailable) return false;

        return WithProfile((session, profile) =>
        {
            var del = Resolve<FnDeleteSetting>(FN_DRS_DeleteProfileSetting);
            var save = Resolve<FnSession>(FN_DRS_SaveSettings);
            if (del is null || save is null) return false;

            var code = del(session, profile, settingId);
            // -166 NVAPI_SETTING_NOT_FOUND — удалять было нечего, цель достигнута.
            if (code != 0 && code != -166) return false;
            return save(session) == 0;
        });
    }

    /// <summary>Снимок состояния для отката.</summary>
    public static StateRecord Capture(NvidiaProfileAction action)
    {
        uint? current = null;
        if (uint.TryParse(action.Setting.Replace("0x", ""),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var id))
        {
            current = Read(id);
        }

        return new StateRecord
        {
            Kind = "nvidia",
            Target = action.Setting,
            Value = current?.ToString()
        };
    }

    public static void Restore(StateRecord record)
    {
        if (!uint.TryParse(record.Target.Replace("0x", ""),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var id)) return;

        if (record.Value is null) Delete(id);
        else if (uint.TryParse(record.Value, out var value)) Write(id, value);
    }

    /// <summary>
    /// Открывает сессию DRS, отдаёт глобальный профиль и гарантированно закрывает сессию.
    /// Незакрытая сессия держит базу настроек драйвера заблокированной.
    /// </summary>
    private static T? WithProfile<T>(Func<IntPtr, IntPtr, T?> body)
    {
        var create = Resolve<FnHandle>(FN_DRS_CreateSession);
        var load = Resolve<FnSession>(FN_DRS_LoadSettings);
        var baseProfile = Resolve<FnProfile>(FN_DRS_GetBaseProfile);
        var destroy = Resolve<FnSession>(FN_DRS_DestroySession);

        if (create is null || load is null || baseProfile is null || destroy is null) return default;

        IntPtr session = IntPtr.Zero;
        try
        {
            if (create(out session) != 0 || session == IntPtr.Zero) return default;
            if (load(session) != 0) return default;
            if (baseProfile(session, out var profile) != 0 || profile == IntPtr.Zero) return default;

            return body(session, profile);
        }
        catch { return default; }
        finally
        {
            if (session != IntPtr.Zero) { try { destroy(session); } catch { } }
        }
    }

    /// <summary>
    /// Настройки, в идентификаторах которых мы уверены. Каталог задаёт их строкой,
    /// но здесь держим проверенный список, чтобы было с чем сверяться.
    /// </summary>
    public static class Settings
    {
        /// <summary>Режим управления электропитанием. 0 — адаптивный, 1 — максимальная производительность.</summary>
        public const uint PowerMode = 0x1057EB71;
        /// <summary>Потоковая оптимизация OpenGL. 0 — авто, 1 — включить, 2 — выключить.</summary>
        public const uint ThreadedOptimization = 0x20C1221E;
        /// <summary>Максимум заранее подготовленных кадров. 1 — минимальная задержка ввода.</summary>
        public const uint MaxPreRenderedFrames = 0x007BA09E;
        /// <summary>Качество фильтрации текстур. 0x10 — «Высокая производительность», 0 — «Качество».</summary>
        public const uint TextureFilterQuality = 0x00CE2691;
    }
}
