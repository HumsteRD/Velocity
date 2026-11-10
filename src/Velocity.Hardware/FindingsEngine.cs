using Velocity.Core;

namespace Velocity.Hardware;

/// <summary>
/// Превращает снимок железа в список находок. Это то, что пользователь видит бесплатно,
/// и главный аргумент к покупке (docs/01-PROJECT-PLAN.md).
///
/// Два правила, которые нельзя нарушать:
///  1. Не выдумываем проблему. Нет данных — нет находки (молчим, а не гадаем).
///  2. Не обещаем цифру, которой не измеряли. «Обычно +5–12 FPS», а не «+40% FPS».
/// </summary>
public static class FindingsEngine
{
    public static List<Finding> Analyze(HardwareReport r)
    {
        var findings = new List<Finding>();

        AnalyzeDisplays(r, findings);
        AnalyzeMemory(r, findings);
        AnalyzeStorage(r, findings);
        AnalyzeGpu(r, findings);
        AnalyzeWindows(r, findings);
        AnalyzePower(r, findings);
        AnalyzeInput(r, findings);
        AnalyzeBackground(r, findings);
        AnalyzeNetwork(r, findings);
        AnalyzeGames(r, findings);
        AnalyzeStartup(r, findings);
        AnalyzeDetox(r, findings);

        return [.. findings.OrderByDescending(f => f.Severity).ThenByDescending(f => f.ScoreWeight)];
    }

    // ─────────────────────────── Дисплей ───────────────────────────

    private static void AnalyzeDisplays(HardwareReport r, List<Finding> f)
    {
        foreach (var d in r.Displays)
        {
            var label = d.FriendlyName ?? d.DeviceName ?? L10n.T("Монитор");

            // Топ-находка продукта: монитор способен на больше, чем Windows ему выставила.
            if (d.MaxRefreshAtCurrentResolution > d.RefreshHz + 1)
            {
                double ratio = (double)d.MaxRefreshAtCurrentResolution / Math.Max(d.RefreshHz, 1);
                f.Add(new Finding
                {
                    Id = "display.refresh_not_max",
                    Category = FindingCategory.Display,
                    Severity = ratio >= 1.5 ? FindingSeverity.Critical : FindingSeverity.Warning,
                    Title = L10n.F("{0}: {1} Гц вместо {2} Гц", label, d.RefreshHz, d.MaxRefreshAtCurrentResolution),
                    Detail = L10n.F("Монитор поддерживает {0} Гц на текущем разрешении ", d.MaxRefreshAtCurrentResolution) +
                             L10n.F("{0}×{1}, но Windows выставила {2} Гц. ", d.Width, d.Height, d.RefreshHz) +
                             L10n.F("Ты видишь в {0:0.0} раза меньше кадров, чем способен показать экран — ", ratio) +
                             L10n.T("даже если игра выдаёт 200 FPS."),
                    Recommendation = L10n.F("Выставить {0} Гц", d.MaxRefreshAtCurrentResolution),
                    FixTweakId = "display.max_refresh",
                    ExpectedGain = L10n.T("Плавность картинки и заметно ниже задержка ввода"),
                    ScoreWeight = 20
                });
            }

            // Частота выше доступна, но на другом разрешении — тут решать пользователю.
            else if (d.MaxRefreshAnyResolution > d.RefreshHz + 1)
            {
                f.Add(new Finding
                {
                    Id = "display.higher_hz_other_resolution",
                    Category = FindingCategory.Display,
                    Severity = FindingSeverity.Info,
                    Title = L10n.F("{0}: {1} Гц доступны на другом разрешении", label, d.MaxRefreshAnyResolution),
                    Detail = L10n.F("Сейчас {0}×{1} @ {2} Гц. ", d.Width, d.Height, d.RefreshHz) +
                             L10n.F("На пониженном разрешении монитор выдаёт до {0} Гц. ", d.MaxRefreshAnyResolution) +
                             L10n.T("Это компромисс между чёткостью и плавностью — решение за тобой."),
                    ScoreWeight = 0
                });
            }

            if (d.BitsPerPixel is > 0 and < 32)
            {
                f.Add(new Finding
                {
                    Id = "display.color_depth",
                    Category = FindingCategory.Display,
                    Severity = FindingSeverity.Warning,
                    Title = L10n.F("{0}: глубина цвета {1} бит", label, d.BitsPerPixel),
                    Detail = L10n.T("Режим ниже 32 бит даёт полосы на градиентах. Обычно это следствие ") +
                             L10n.T("кривого режима вывода или ограничений кабеля."),
                    Recommendation = L10n.T("Выставить 32 бита"),
                    FixTweakId = "display.color_depth",
                    ScoreWeight = 4
                });
            }
        }

        // Разная частота у мониторов — известный источник микрофризов на части GPU.
        var refreshRates = r.Displays.Where(d => d.RefreshHz > 0).Select(d => d.RefreshHz).Distinct().ToList();
        if (refreshRates.Count > 1)
        {
            f.Add(new Finding
            {
                Id = "display.mixed_refresh",
                Category = FindingCategory.Display,
                Severity = FindingSeverity.Warning,
                Title = L10n.F("Мониторы работают на разной частоте ({0} Гц)", string.Join(" / ", refreshRates.Order())),
                Detail = L10n.T("Смешанные частоты обновления — известная причина микрофризов в играх: ") +
                         L10n.T("композитор вынужден синхронизировать разные тайминги. ") +
                         L10n.T("Особенно заметно, если на втором мониторе идёт видео или стрим."),
                Recommendation = L10n.T("Выровнять частоту или отключать второй монитор на время игры"),
                ScoreWeight = 6
            });
        }
    }

    // ─────────────────────────── Память ───────────────────────────

    private static void AnalyzeMemory(HardwareReport r, List<Finding> f)
    {
        var mem = r.Memory;
        if (mem.Modules.Count == 0) return;

        // Эвристика XMP/EXPO: частота на уровне базовой JEDEC.
        // Точно определить профиль SPD без чтения по SMBus нельзя, поэтому формулировка осторожная,
        // а порог разделён на два уровня: ниже базовой JEDEC — почти наверняка профиль выключен,
        // ровно на верхней ступени JEDEC — возможен честный комплект без разгона.
        (int Certain, int Possible) jedec = mem.Kind switch
        {
            MemoryKind.Ddr5 => (4800, 5600),
            MemoryKind.Ddr4 => (2400, 2666),
            MemoryKind.Ddr3 => (1333, 1600),
            _ => (0, 0)
        };

        if (jedec.Possible > 0 && mem.ConfiguredSpeedMhz > 0 && mem.ConfiguredSpeedMhz <= jedec.Possible
            && r.Machine.Chassis != ChassisKind.Laptop)
        {
            bool certain = mem.ConfiguredSpeedMhz <= jedec.Certain;
            f.Add(new Finding
            {
                Id = "ram.xmp_likely_off",
                Category = FindingCategory.HardwareBios,
                Severity = certain ? FindingSeverity.Critical : FindingSeverity.Warning,
                Title = L10n.F("Память работает на {0} МГц — похоже, XMP/EXPO выключен", mem.ConfiguredSpeedMhz),
                Detail = L10n.F("{0} на {1} МГц — это базовая частота JEDEC, ", mem.Kind, mem.ConfiguredSpeedMhz) +
                         L10n.T("то есть модули работают на «безопасном» минимуме, а не на своей паспортной скорости. ") +
                         L10n.T("Профиль XMP (Intel) или EXPO (AMD) включается одной галочкой в BIOS и обычно ") +
                         L10n.T("поднимает частоту в 1.3–1.5 раза. Для игр это одна из самых крупных бесплатных прибавок."),
                Recommendation = L10n.F("Зайти в BIOS ({0}) → ", r.Machine.BoardManufacturer ?? L10n.T("материнская плата")) +
                                 L10n.T("включить профиль XMP / EXPO / DOCP"),
                ExpectedGain = L10n.T("Обычно +5–15% FPS, на встроенной графике заметно больше"),
                ScoreWeight = certain ? 20 : 10
            });
        }

        // Одноканал при нескольких модулях = планки в слотах одного канала.
        if (mem.Modules.Count >= 2 && mem.Channels == 1)
        {
            f.Add(new Finding
            {
                Id = "ram.single_channel",
                Category = FindingCategory.HardwareBios,
                Severity = FindingSeverity.Critical,
                Title = L10n.T("Память работает в одноканальном режиме"),
                Detail = L10n.F("Установлено модулей: {0}, но все они в слотах одного канала ", mem.Modules.Count) +
                         $"({string.Join(", ", mem.Modules.Select(m => m.Slot).Where(s => s is not null))}). " +
                         L10n.T("Пропускная способность вдвое ниже возможной."),
                Recommendation = L10n.T("Переставить планки через слот (обычно A2 и B2 — второй и четвёртый от процессора)"),
                ExpectedGain = L10n.T("Обычно +5–15% FPS, на встроенной графике до +30%"),
                ScoreWeight = 18
            });
        }

        if (mem.Modules.Count == 1 && mem.TotalGb >= 8 && r.Machine.Chassis != ChassisKind.Laptop)
        {
            f.Add(new Finding
            {
                Id = "ram.single_module",
                Category = FindingCategory.HardwareBios,
                Severity = FindingSeverity.Warning,
                Title = L10n.T("Установлен только один модуль памяти"),
                Detail = L10n.T("Одна планка = одноканальный режим. Вторая планка того же объёма даёт ") +
                         L10n.T("двухканальный режим и заметный прирост в играх — часто больше, чем апгрейд процессора."),
                Recommendation = L10n.T("Добавить второй модуль такого же объёма"),
                ExpectedGain = L10n.T("Обычно +5–15% FPS"),
                ScoreWeight = 10
            });
        }

        if (mem.TotalGb is > 0 and < 12)
        {
            f.Add(new Finding
            {
                Id = "ram.low_capacity",
                Category = FindingCategory.Memory,
                Severity = mem.TotalGb < 8 ? FindingSeverity.Critical : FindingSeverity.Warning,
                Title = L10n.F("Всего {0} ГБ оперативной памяти", mem.TotalGb),
                Detail = L10n.T("Современные игры уверенно занимают 8–12 ГБ. При нехватке система начинает ") +
                         L10n.T("выгружать данные в файл подкачки — это и есть те самые фризы при загрузке локаций."),
                Recommendation = L10n.T("Апгрейд до 16 ГБ (в двухканале)"),
                ScoreWeight = mem.TotalGb < 8 ? 12 : 6
            });
        }

        if (mem.Pagefile.Disabled)
        {
            f.Add(new Finding
            {
                Id = "ram.pagefile_disabled",
                Category = FindingCategory.Memory,
                Severity = FindingSeverity.Warning,
                Title = L10n.T("Файл подкачки отключён"),
                Detail = L10n.T("Популярный «твик» из .bat-паков, который ничего не ускоряет, зато роняет игры ") +
                         L10n.T("на Unreal Engine 5 и всё, что резервирует много виртуальной памяти."),
                Recommendation = L10n.T("Включить файл подкачки на самом быстром SSD"),
                FixTweakId = "ram.pagefile",
                ScoreWeight = 8
            });
        }
    }

    // ─────────────────────────── Накопители ───────────────────────────

    private static void AnalyzeStorage(HardwareReport r, List<Finding> f)
    {
        var system = r.Storage.FirstOrDefault(s => s.IsSystemDrive);

        if (system is { Kind: StorageKind.Hdd })
        {
            f.Add(new Finding
            {
                Id = "storage.system_on_hdd",
                Category = FindingCategory.Storage,
                Severity = FindingSeverity.Critical,
                Title = L10n.T("Windows установлена на жёсткий диск"),
                Detail = L10n.F("Системный диск — {0} (HDD). Это узкое место всей системы: ", system.Model) +
                         L10n.T("долгая загрузка, фризы при подгрузке текстур, медленный отклик. ") +
                         L10n.T("Переезд на SSD даёт больше ощутимой разницы, чем любая программная оптимизация."),
                Recommendation = L10n.T("Перенести систему на SSD"),
                ScoreWeight = 15
            });
        }

        // Флешки и внешние диски исключаем: «мало места на флешке» — не проблема производительности.
        foreach (var s in r.Storage.Where(s => s is { SizeGb: > 0, FreeGb: > 0, IsRemovable: false }))
        {
            if (s.FreePercent < 10)
            {
                f.Add(new Finding
                {
                    Id = "storage.low_free_space",
                    Category = FindingCategory.Storage,
                    Severity = s.FreePercent < 5 ? FindingSeverity.Critical : FindingSeverity.Warning,
                    Title = L10n.F("{0}: свободно {1} ГБ из {2} ГБ ({3:0}%)", s.Model, s.FreeGb, s.SizeGb, s.FreePercent),
                    Detail = s.Kind is StorageKind.Ssd or StorageKind.Nvme
                        ? L10n.T("У SSD при заполнении выше 90% резко падает скорость записи: ") +
                          L10n.T("заканчивается SLC-кэш и негде выполнять сборку мусора.")
                        : L10n.T("Заполненный HDD сильнее фрагментируется и медленнее отдаёт данные."),
                    Recommendation = L10n.T("Освободить место (цель — минимум 15% свободного)"),
                    ScoreWeight = 8
                });
            }

            if (s is { Kind: StorageKind.Hdd, IsSystemDrive: false, DriveLetters.Count: > 0 })
            {
                f.Add(new Finding
                {
                    Id = "storage.games_maybe_on_hdd",
                    Category = FindingCategory.Storage,
                    Severity = FindingSeverity.Info,
                    Title = L10n.F("Есть жёсткий диск: {0} ({1})", s.Model, string.Join(", ", s.DriveLetters)),
                    Detail = L10n.T("Если игры установлены сюда — время загрузки уровней и подгрузка текстур ") +
                             L10n.T("будут заметно хуже, чем на SSD. Стоит проверить, где лежат библиотеки Steam/Epic."),
                    Recommendation = L10n.T("Перенести активные игры на SSD"),
                    ScoreWeight = 0
                });
            }

            if (s.HealthStatus is "Warning" or "Unhealthy")
            {
                f.Add(new Finding
                {
                    Id = "storage.health",
                    Category = FindingCategory.Storage,
                    Severity = FindingSeverity.Critical,
                    Title = L10n.F("{0}: состояние «{1}»", s.Model, s.HealthStatus),
                    Detail = L10n.T("Windows сообщает о проблемах с накопителем. Это не про производительность — ") +
                             L10n.T("это про сохранность данных."),
                    Recommendation = L10n.T("Сделать резервную копию и проверить SMART"),
                    ScoreWeight = 10
                });
            }
        }
    }

    // ─────────────────────────── GPU ───────────────────────────

    private static void AnalyzeGpu(HardwareReport r, List<Finding> f)
    {
        var gpu = r.Gpus.FirstOrDefault(g => !g.IsIntegrated) ?? r.Gpus.FirstOrDefault();
        if (gpu is null) return;

        if (gpu.Vendor == GpuVendor.Microsoft)
        {
            f.Add(new Finding
            {
                Id = "gpu.no_driver",
                Category = FindingCategory.Gpu,
                Severity = FindingSeverity.Critical,
                Title = L10n.T("Драйвер видеокарты не установлен"),
                Detail = L10n.T("Система использует «Базовый видеоадаптер (Майкрософт)» — это заглушка без ") +
                         L10n.T("3D-ускорения. Игры либо не запустятся, либо будут рендериться программно."),
                Recommendation = L10n.T("Установить драйвер с сайта производителя видеокарты"),
                ScoreWeight = 25
            });
            return;
        }

        if (gpu.DriverDate is { } date)
        {
            var age = DateTime.Now - date;
            if (age.TotalDays > 365)
            {
                f.Add(new Finding
                {
                    Id = "gpu.driver_outdated",
                    Category = FindingCategory.Gpu,
                    Severity = age.TotalDays > 730 ? FindingSeverity.Warning : FindingSeverity.Info,
                    Title = L10n.F("Драйвер видеокарты от {0:MM.yyyy} (больше {1} назад)", date, Plural.Months((int)(age.TotalDays / 30))),
                    Detail = L10n.F("Установлена версия {0}. ", gpu.VendorDriverVersion ?? gpu.DriverVersion) +
                             L10n.T("Свежие драйверы регулярно дают прирост в новых играх и чинят стоттеры — ") +
                             L10n.T("особенно это касается Intel Arc и новых поколений AMD."),
                    Recommendation = L10n.T("Обновить драйвер с сайта производителя"),
                    ScoreWeight = 5
                });
            }
        }

        if (r.Gpus.Count(g => !g.IsIntegrated) > 0 && r.Gpus.Any(g => g.IsIntegrated)
            && r.Machine.Chassis == ChassisKind.Laptop)
        {
            f.Add(new Finding
            {
                Id = "gpu.hybrid_laptop",
                Category = FindingCategory.Gpu,
                Severity = FindingSeverity.Info,
                Title = L10n.T("Гибридная графика: встроенная + дискретная"),
                Detail = L10n.T("На ноутбуках игры регулярно запускаются на встроенном видеоядре вместо дискретной карты — ") +
                         L10n.T("это мгновенная потеря в разы. Стоит проверить, какой GPU реально рендерит игру, ") +
                         L10n.T("и при возможности включить MUX-переключатель на дискретную."),
                Recommendation = L10n.T("Проверить назначение GPU для игр и настройки MUX"),
                ScoreWeight = 0
            });
        }

        if (gpu.VramGb is > 0 and <= 4 && !gpu.IsIntegrated)
        {
            f.Add(new Finding
            {
                Id = "gpu.low_vram",
                Category = FindingCategory.Gpu,
                Severity = FindingSeverity.Info,
                Title = L10n.F("{0} ГБ видеопамяти", gpu.VramGb),
                Detail = L10n.T("Современные игры на высоких текстурах выходят за 6–8 ГБ. При нехватке VRAM ") +
                         L10n.T("данные начинают ходить через системную память — это резкие просадки 1% low, ") +
                         L10n.T("а не плавное снижение FPS."),
                Recommendation = L10n.T("Снизить качество текстур, включить апскейлер (DLSS/FSR/XeSS)"),
                ScoreWeight = 0
            });
        }
    }

    // ─────────────────────────── Windows ───────────────────────────

    private static void AnalyzeWindows(HardwareReport r, List<Finding> f)
    {
        var t = r.Tuning;

        if (t.GameDvrEnabled == true)
        {
            f.Add(new Finding
            {
                Id = "win.gamedvr_on",
                Category = FindingCategory.Windows,
                Severity = FindingSeverity.Warning,
                Title = L10n.T("Фоновая запись Game DVR включена"),
                Detail = L10n.T("Xbox Game Bar держит наготове буфер записи последних минут игры. ") +
                         L10n.T("Это стоит кадров даже когда ты ничего не записываешь."),
                Recommendation = L10n.T("Отключить фоновую запись"),
                FixTweakId = "win.gamedvr_off",
                ExpectedGain = L10n.T("Обычно +2–6% FPS и меньше просадок 1% low"),
                ScoreWeight = 8
            });
        }

        if (t.HvciEnabled == true)
        {
            f.Add(new Finding
            {
                Id = "sec.hvci_on",
                Category = FindingCategory.Security,
                Severity = FindingSeverity.Warning,
                Title = L10n.T("Включена «Целостность памяти» (Memory Integrity / HVCI)"),
                Detail = L10n.T("Самый крупный одиночный источник потерь производительности на Windows 11: ") +
                         L10n.T("виртуализация проверяет драйверы в реальном времени. В играх, упирающихся в процессор, ") +
                         L10n.T("это обычно 5–15% FPS. Но это реальная защита от подмены драйверов — ") +
                         L10n.T("отключать стоит осознанно, а не «потому что так в гайде»."),
                Recommendation = L10n.T("Взвесить: отключение даёт FPS, но снижает защиту системы"),
                FixTweakId = "sec.hvci_off",
                ExpectedGain = L10n.T("Обычно +5–15% FPS при упоре в CPU"),
                ScoreWeight = 10
            });
        }

        // VBS отдельно от HVCI: гипервизор поднимается и стоит производительности сам по себе,
        // даже когда «Целостность памяти» выключена.
        if (t is { VbsEnabled: true, HvciEnabled: not true })
        {
            f.Add(new Finding
            {
                Id = "sec.vbs_on",
                Category = FindingCategory.Security,
                Severity = FindingSeverity.Warning,
                Title = L10n.T("Включена безопасность на основе виртуализации (VBS)"),
                Detail = L10n.T("Windows запускает систему поверх гипервизора. Даже без «Целостности памяти» ") +
                         L10n.T("это стоит нескольких процентов в играх, упирающихся в процессор. ") +
                         L10n.T("Важно: VBS нужен для WSL2, Hyper-V, Docker и «Песочницы Windows» — ") +
                         L10n.T("если ты ими пользуешься, отключать нельзя."),
                Recommendation = L10n.T("Отключить, если не используешь WSL2 / Hyper-V / Docker"),
                FixTweakId = "sec.vbs_off",
                ExpectedGain = L10n.T("Обычно +3–10% FPS при упоре в CPU"),
                ScoreWeight = 8
            });
        }

        if (t.HardwareAcceleratedGpuScheduling == false)
        {
            f.Add(new Finding
            {
                Id = "gpu.hags_off",
                Category = FindingCategory.Gpu,
                Severity = FindingSeverity.Info,
                Title = L10n.T("Аппаратное ускорение планирования GPU выключено"),
                Detail = L10n.T("HAGS перекладывает управление очередью кадров с процессора на видеокарту. ") +
                         L10n.T("Эффект неоднозначный: на связке NVIDIA + Reflex обычно плюс, на части систем — минус. ") +
                         L10n.T("Это ровно тот случай, когда нужно замерить до и после, а не верить гайдам."),
                Recommendation = L10n.T("Включить и сравнить замером"),
                FixTweakId = "gpu.hags",
                ScoreWeight = 0
            });
        }

        if (t.GameModeEnabled == false && r.Os.IsWindows11)
        {
            f.Add(new Finding
            {
                Id = "win.game_mode_off",
                Category = FindingCategory.Windows,
                Severity = FindingSeverity.Info,
                Title = L10n.T("Игровой режим Windows выключен"),
                Detail = L10n.T("В Windows 11 игровой режим стал полезным: он приоритизирует игру и откладывает ") +
                         L10n.T("установку обновлений и перезагрузку драйверов во время игры."),
                Recommendation = L10n.T("Включить игровой режим"),
                FixTweakId = "win.game_mode_on",
                ScoreWeight = 0
            });
        }

        if (t.SystemResponsiveness is > 10)
        {
            f.Add(new Finding
            {
                Id = "win.system_responsiveness",
                Category = FindingCategory.Windows,
                Severity = FindingSeverity.Info,
                Title = $"SystemResponsiveness = {t.SystemResponsiveness}",
                Detail = L10n.T("Доля процессорного времени, резервируемая под фоновые задачи. ") +
                         L10n.T("Значение 10 оставляет играм чуть больше, чем стандартные 20."),
                Recommendation = L10n.T("Выставить 10"),
                FixTweakId = "win.system_responsiveness",
                ScoreWeight = 0
            });
        }

        if (r.Os.Build is > 0 and < 19041)
        {
            f.Add(new Finding
            {
                Id = "os.outdated",
                Category = FindingCategory.Windows,
                Severity = FindingSeverity.Warning,
                Title = L10n.F("Старая сборка Windows ({0})", r.Os.FullBuild),
                Detail = L10n.T("Часть игровых оптимизаций (HAGS, современный flip-model вывод, DirectStorage) ") +
                         L10n.T("появилась в более поздних сборках и здесь просто недоступна."),
                Recommendation = L10n.T("Обновить Windows"),
                ScoreWeight = 6
            });
        }
    }

    // ─────────────────────────── Питание ───────────────────────────

    private static void AnalyzePower(HardwareReport r, List<Finding> f)
    {
        var plan = r.Tuning.PowerPlanName;
        if (plan is null) return;

        bool isPowerSaver = plan.Contains("Экономия", StringComparison.OrdinalIgnoreCase)
                            || plan.Contains("Power saver", StringComparison.OrdinalIgnoreCase);
        bool isBalanced = plan.Contains("Сбалансированная", StringComparison.OrdinalIgnoreCase)
                          || plan.Contains("Balanced", StringComparison.OrdinalIgnoreCase);

        if (isPowerSaver)
        {
            f.Add(new Finding
            {
                Id = "power.saver_plan",
                Category = FindingCategory.Power,
                Severity = FindingSeverity.Critical,
                Title = L10n.F("Активна схема питания «{0}»", plan),
                Detail = L10n.T("Режим экономии энергии искусственно ограничивает частоты процессора ") +
                         L10n.T("и агрессивно паркует ядра. В играх это прямая потеря производительности."),
                Recommendation = L10n.T("Переключиться на схему высокой производительности"),
                FixTweakId = "power.ultimate_plan",
                ExpectedGain = L10n.T("На затронутых системах +10–30% FPS"),
                ScoreWeight = 15
            });
        }
        else if (isBalanced && r.Machine.Chassis != ChassisKind.Laptop)
        {
            f.Add(new Finding
            {
                Id = "power.balanced_plan",
                Category = FindingCategory.Power,
                Severity = FindingSeverity.Info,
                Title = L10n.T("Активна сбалансированная схема питания"),
                Detail = L10n.T("Для настольного ПК это разумный компромисс, но парковка ядер и переходы ") +
                         L10n.T("энергосостояний PCIe дают лишние микрозадержки. Отдельная игровая схема их убирает."),
                Recommendation = L10n.T("Создать игровую схему питания"),
                FixTweakId = "power.ultimate_plan",
                ScoreWeight = 0
            });
        }
    }

    // ─────────────────────────── Ввод ───────────────────────────

    private static void AnalyzeInput(HardwareReport r, List<Finding> f)
    {
        if (r.Input.PointerPrecisionEnabled)
        {
            f.Add(new Finding
            {
                Id = "input.mouse_accel_on",
                Category = FindingCategory.Input,
                Severity = FindingSeverity.Warning,
                Title = L10n.T("Включена акселерация мыши"),
                Detail = L10n.T("«Повышенная точность установки указателя» меняет пройденное курсором расстояние ") +
                         L10n.T("в зависимости от скорости движения руки. В шутерах это ломает мышечную память: ") +
                         L10n.T("один и тот же взмах даёт разный поворот."),
                Recommendation = L10n.T("Отключить акселерацию"),
                FixTweakId = "input.mouse_accel_off",
                ExpectedGain = L10n.T("Стабильное прицеливание (на FPS не влияет)"),
                ScoreWeight = 8
            });
        }

        // Замер частоты опроса выполняется только по явному запросу (нужно движение мышью).
        if (r.Input.MeasuredPollingHz > 0)
        {
            int hz = r.Input.MeasuredPollingHz;
            if (hz < 500)
            {
                f.Add(new Finding
                {
                    Id = "input.low_polling_rate",
                    Category = FindingCategory.Input,
                    Severity = hz <= 125 ? FindingSeverity.Warning : FindingSeverity.Info,
                    Title = L10n.F("Фактическая частота опроса мыши: {0} Гц", hz),
                    Detail = hz <= 125
                        ? L10n.T("125 Гц — стандартное значение USB по умолчанию. Между отчётами проходит 8 мс: ") +
                          L10n.T("при быстрых движениях курсор идёт заметными ступеньками. ") +
                          L10n.T("Если мышь игровая, её частота почти наверняка сбита или ограничена подключением.")
                        : L10n.T("Ниже 500 Гц. Для игровой мыши это мало — стоит проверить, во что она воткнута."),
                    Recommendation = L10n.T("Проверить: прямой порт на задней панели (не хаб и не разъём монитора), ") +
                                     L10n.T("энергосбережение USB, настройку частоты в софте мыши"),
                    ExpectedGain = L10n.T("Ровнее движение курсора и точнее прицеливание"),
                    ScoreWeight = 6
                });
            }
        }

        if (r.Input.MouseSensitivity is > 0 and not 10)
        {
            f.Add(new Finding
            {
                Id = "input.mouse_sensitivity_scaled",
                Category = FindingCategory.Input,
                Severity = FindingSeverity.Warning,
                Title = L10n.F("Системная чувствительность мыши {0}/20 вместо 10/20", r.Input.MouseSensitivity),
                Detail = L10n.T("Любое значение кроме 10 (это 6-я позиция из 11 в панели управления) заставляет ") +
                         L10n.T("Windows масштабировать отсчёты сенсора. Появляются пропуски и неравномерность движения."),
                Recommendation = L10n.T("Выставить 6/11 (значение 10) и регулировать чувствительность через DPI"),
                FixTweakId = "input.mouse_sensitivity_1to1",
                ScoreWeight = 6
            });
        }
    }

    // ─────────────────────────── Фон ───────────────────────────

    private static void AnalyzeBackground(HardwareReport r, List<Finding> f)
    {
        var overlays = r.Tuning.ActiveOverlays;
        if (overlays.Count >= 3)
        {
            f.Add(new Finding
            {
                Id = "bg.overlay_stack",
                Category = FindingCategory.Background,
                Severity = overlays.Count >= 5 ? FindingSeverity.Critical : FindingSeverity.Warning,
                Title = L10n.F("Одновременно активны {0}", Plural.Overlays(overlays.Count)),
                Detail = L10n.F("Обнаружены: {0}. ", string.Join(", ", overlays)) +
                         L10n.T("Каждый оверлей встраивается в цепочку вывода кадра. Три и больше хуков подряд — ") +
                         L10n.T("это уже не потеря средних FPS, а регулярные спайки frametime, те самые рывки."),
                Recommendation = L10n.T("Оставить один-два нужных, остальные отключить"),
                ExpectedGain = L10n.T("Заметно ровнее 1% low"),
                ScoreWeight = 10
            });
        }
    }

    // ─────────────────────────── Сеть ───────────────────────────

    private static void AnalyzeNetwork(HardwareReport r, List<Finding> f)
    {
        var active = r.Network.Where(n => n.Connected && n.Kind is NetworkKind.WiFi or NetworkKind.Ethernet).ToList();
        if (active.Count == 0) return;

        if (active.All(n => n.Kind == NetworkKind.WiFi))
        {
            f.Add(new Finding
            {
                Id = "net.wifi_only",
                Category = FindingCategory.Network,
                Severity = FindingSeverity.Warning,
                Title = L10n.T("Подключение только по Wi-Fi"),
                Detail = L10n.T("Wi-Fi даёт не столько высокий пинг, сколько нестабильный: джиттер и редкие потери ") +
                         L10n.T("пакетов. В соревновательных играх это выглядит как «резина» и откаты позиции. ") +
                         L10n.T("Кабель убирает это полностью."),
                Recommendation = L10n.T("Подключиться по Ethernet"),
                ScoreWeight = 6
            });
        }

        foreach (var n in active.Where(n => n.Kind == NetworkKind.Ethernet && n.LinkSpeedMbps is > 0 and < 1000))
        {
            f.Add(new Finding
            {
                Id = "net.slow_link",
                Category = FindingCategory.Network,
                Severity = FindingSeverity.Info,
                Title = L10n.F("{0}: скорость линка {1} Мбит/с", n.ConnectionId ?? n.Name, n.LinkSpeedMbps),
                Detail = L10n.T("Линк договорился на скорость ниже гигабита. Обычно это старый или повреждённый кабель ") +
                         L10n.T("(категории ниже 5e), либо порт роутера на 100 Мбит."),
                Recommendation = L10n.T("Проверить кабель и порт"),
                ScoreWeight = 0
            });
        }
    }

    // ─────────────────────────── Игры ───────────────────────────

    private static void AnalyzeGames(HardwareReport r, List<Finding> f)
    {
        if (r.Games.Count == 0) return;

        var onHdd = r.Games.Where(g => g.DriveKind == StorageKind.Hdd).ToList();
        bool hasFastDrive = r.Storage.Any(s => s.Kind is StorageKind.Ssd or StorageKind.Nvme && !s.IsRemovable);

        if (onHdd.Count > 0 && hasFastDrive)
        {
            var top = onHdd.OrderByDescending(g => g.SizeGb).Take(3).Select(g => g.Name);
            f.Add(new Finding
            {
                Id = "storage.games_on_hdd",
                Category = FindingCategory.Storage,
                Severity = FindingSeverity.Critical,
                Title = L10n.F("{0} на жёстком диске", Plural.Games(onHdd.Count)),
                Detail = L10n.F("Например: {0}. ", string.Join(", ", top)) +
                         L10n.T("На HDD это долгие загрузки уровней и подгрузка текстур прямо в бою — ") +
                         L10n.T("те самые фризы при забегании в новую локацию. При этом в системе есть SSD."),
                Recommendation = L10n.T("Перенести активные игры на SSD (Steam умеет это через «Переместить папку установки»)"),
                ExpectedGain = L10n.T("Загрузки в 3–10 раз быстрее, заметно меньше фризов стриминга"),
                ScoreWeight = 12
            });
        }

        // Игра рядом с системой на забитом диске — частая причина деградации SSD.
        var systemDrive = r.Storage.FirstOrDefault(s => s.IsSystemDrive);
        if (systemDrive is { FreePercent: < 15 } && systemDrive.DriveLetters.Count > 0)
        {
            var onSystem = r.Games.Count(g => g.DriveLetter is { } d
                && systemDrive.DriveLetters.Contains(d, StringComparer.OrdinalIgnoreCase));

            if (onSystem > 0)
            {
                f.Add(new Finding
                {
                    Id = "storage.games_crowding_system",
                    Category = FindingCategory.Storage,
                    Severity = FindingSeverity.Info,
                    Title = L10n.F("{0} на системном диске, где осталось {1:0}%", Plural.Games(onSystem), systemDrive.FreePercent),
                    Detail = L10n.T("Системному диску нужен запас: под файл подкачки, обновления Windows, ") +
                             L10n.T("шейдерные кэши и сборку мусора SSD. Игры проще перенести, чем чистить систему."),
                    Recommendation = L10n.T("Перенести часть игр на другой накопитель"),
                    ScoreWeight = 0
                });
            }
        }
    }

    // ─────────────────────────── Автозагрузка ───────────────────────────

    private static void AnalyzeStartup(HardwareReport r, List<Finding> f)
    {
        if (r.Startup.HeavyItems.Count >= 3)
        {
            f.Add(new Finding
            {
                Id = "bg.heavy_startup",
                Category = FindingCategory.Background,
                Severity = FindingSeverity.Warning,
                Title = L10n.F("В автозагрузке {0}", Plural.With(r.Startup.HeavyItems.Count, L10n.T("тяжёлая программа"), L10n.T("тяжёлых программы"), L10n.T("тяжёлых программ"))),
                Detail = L10n.F("Обнаружены: {0}. ", string.Join(", ", r.Startup.HeavyItems)) +
                         L10n.T("Софт для подсветки и лаунчеры держат фоновые службы, а некоторые ") +
                         L10n.T("(iCUE, Armoury Crate) известны как источники скачков задержки DPC — ") +
                         L10n.T("это микрофризы, которые не видно по среднему FPS."),
                Recommendation = L10n.T("Убрать из автозагрузки то, что не нужно постоянно"),
                ExpectedGain = L10n.T("Ровнее frametime, быстрее загрузка системы"),
                ScoreWeight = 6
            });
        }
        else if (r.Startup.ItemCount >= 15)
        {
            f.Add(new Finding
            {
                Id = "bg.many_startup",
                Category = FindingCategory.Background,
                Severity = FindingSeverity.Info,
                Title = L10n.F("В автозагрузке {0}", Plural.Programs(r.Startup.ItemCount)),
                Detail = L10n.T("Само по себе это не бьёт по FPS, но удлиняет загрузку системы ") +
                         L10n.T("и оставляет в памяти процессы, которые могут проснуться посреди игры."),
                Recommendation = L10n.T("Проредить автозагрузку"),
                ScoreWeight = 0
            });
        }
    }

    // ─────────────────────────── Детокс ───────────────────────────

    private static void AnalyzeDetox(HardwareReport r, List<Finding> f)
    {
        foreach (var tweak in r.Tuning.HarmfulTweaksDetected)
        {
            f.Add(new Finding
            {
                Id = "detox.harmful_tweak",
                Category = FindingCategory.Windows,
                Severity = FindingSeverity.Warning,
                Title = L10n.T("Найден вредный твик от стороннего «оптимизатора»"),
                Detail = tweak,
                Recommendation = L10n.T("Вернуть штатное значение"),
                ScoreWeight = 5
            });
        }
    }
}
