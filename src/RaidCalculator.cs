namespace DevToolbox;

public enum RaidLevel { Jbod, Raid0, Raid1, Raid5, Raid6, Raid10, Raid50, Raid60 }

public sealed class RaidInput
{
    public RaidLevel Level = RaidLevel.Raid5;
    public int DiskCount = 4;
    public double DiskSize = 4;
    public string Unit = "TB";
    public int HotSpares;
    public int Groups = 2;              // csak RAID 50 / 60
    public int RebuildMBps = 100;
    public double UreRate = 1e14;       // 1 olvasási hiba ennyi bitenként
}

public sealed class RaidResult
{
    public bool Valid;
    public string Error = "";

    public long RawBytes;               // az összes lemez, tartalékkal együtt
    public long UsableBytes;
    public long RedundancyBytes;        // paritásra/tükrözésre elmenő
    public long SpareBytes;             // hot spare

    public int ArrayDisks;              // a tömbben ténylegesen dolgozó lemezek
    public int MinDisks;
    public int FaultTolerance;
    public string ToleranceText = "";

    public double EfficiencyPercent;
    public int WritePenalty;
    public double ReadFactor;

    public TimeSpan RebuildTime;
    public double UreRiskPercent = -1;  // -1 = nem értelmezhető
    public string UreNote = "";

    public List<string> Notes = new();
    public List<string> Warnings = new();
}

public static class RaidCalculator
{
    public static readonly (string Name, double Bytes)[] Units =
    {
        ("TB",  1_000_000_000_000d),
        ("TiB", 1024d * 1024 * 1024 * 1024),
        ("GB",  1_000_000_000d),
        ("GiB", 1024d * 1024 * 1024),
    };

    public static readonly (string Name, double Rate)[] UreClasses =
    {
        ("Asztali SATA (1 hiba / 10¹⁴ bit)", 1e14),
        ("NAS / vállalati SATA (10¹⁵)", 1e15),
        ("Vállalati SAS (10¹⁶)", 1e16),
        ("SSD, vállalati (10¹⁷)", 1e17),
    };

    public static double UnitBytes(string unit) =>
        Units.FirstOrDefault(u => u.Name.Equals(unit, StringComparison.OrdinalIgnoreCase)).Bytes
        is var b && b > 0 ? b : 1_000_000_000_000d;

    public static string LevelName(RaidLevel l) => l switch
    {
        RaidLevel.Jbod => "JBOD / spanning",
        RaidLevel.Raid0 => "RAID 0 (csíkozás)",
        RaidLevel.Raid1 => "RAID 1 (tükrözés)",
        RaidLevel.Raid5 => "RAID 5 (egyszeres paritás)",
        RaidLevel.Raid6 => "RAID 6 (kétszeres paritás)",
        RaidLevel.Raid10 => "RAID 10 (tükrözés + csíkozás)",
        RaidLevel.Raid50 => "RAID 50",
        RaidLevel.Raid60 => "RAID 60",
        _ => l.ToString()
    };

    public static int MinDisks(RaidLevel l, int groups) => l switch
    {
        RaidLevel.Jbod => 1,
        RaidLevel.Raid0 => 2,
        RaidLevel.Raid1 => 2,
        RaidLevel.Raid5 => 3,
        RaidLevel.Raid6 => 4,
        RaidLevel.Raid10 => 4,
        RaidLevel.Raid50 => 3 * Math.Max(2, groups),
        RaidLevel.Raid60 => 4 * Math.Max(2, groups),
        _ => 2
    };

    public static RaidResult Compute(RaidInput inp)
    {
        var r = new RaidResult();

        var unit = UnitBytes(inp.Unit);
        var diskBytes = (long)Math.Round(inp.DiskSize * unit);

        if (inp.DiskCount < 1) { r.Error = "Legalább egy lemezt meg kell adni."; return r; }
        if (diskBytes <= 0) { r.Error = "A lemez mérete nem lehet nulla."; return r; }
        if (inp.HotSpares < 0 || inp.HotSpares >= inp.DiskCount)
        {
            r.Error = "A hot spare lemezek száma kisebb kell legyen az összes lemez számánál.";
            return r;
        }

        var n = inp.DiskCount - inp.HotSpares;     // a tömbben dolgozó lemezek
        var g = Math.Max(2, inp.Groups);

        r.ArrayDisks = n;
        r.MinDisks = MinDisks(inp.Level, g);
        r.RawBytes = (long)inp.DiskCount * diskBytes;
        r.SpareBytes = (long)inp.HotSpares * diskBytes;

        if (n < r.MinDisks)
        {
            r.Error = $"{LevelName(inp.Level)} legalább {r.MinDisks} lemezt igényel " +
                      $"(hot spare nélkül számolva most {n} van).";
            return r;
        }

        long dataDisks;

        switch (inp.Level)
        {
            case RaidLevel.Jbod:
                dataDisks = n;
                r.FaultTolerance = 0;
                r.ToleranceText = "nincs – egy lemez elvesztése az azon tárolt adatot viszi";
                r.WritePenalty = 1; r.ReadFactor = 1;
                break;

            case RaidLevel.Raid0:
                dataDisks = n;
                r.FaultTolerance = 0;
                r.ToleranceText = "nincs – bármelyik lemez kiesése a TELJES tömböt viszi";
                r.WritePenalty = 1; r.ReadFactor = n;
                break;

            case RaidLevel.Raid1:
                dataDisks = 1;
                r.FaultTolerance = n - 1;
                r.ToleranceText = $"{n - 1} lemez (egynek működnie kell)";
                r.WritePenalty = 2; r.ReadFactor = n;
                break;

            case RaidLevel.Raid5:
                dataDisks = n - 1;
                r.FaultTolerance = 1;
                r.ToleranceText = "1 lemez";
                r.WritePenalty = 4; r.ReadFactor = n;
                break;

            case RaidLevel.Raid6:
                dataDisks = n - 2;
                r.FaultTolerance = 2;
                r.ToleranceText = "2 lemez";
                r.WritePenalty = 6; r.ReadFactor = n;
                break;

            case RaidLevel.Raid10:
                if (n % 2 != 0)
                {
                    r.Error = "A RAID 10 páros számú lemezt igényel.";
                    return r;
                }
                dataDisks = n / 2;
                r.FaultTolerance = 1;
                r.ToleranceText = $"garantáltan 1 lemez; szerencsés esetben {n / 2} " +
                                  "(tükörpáronként egy) – ha egy páron belül esik ki kettő, a tömb elvész";
                r.WritePenalty = 2; r.ReadFactor = n;
                break;

            case RaidLevel.Raid50:
                if (n % g != 0)
                {
                    r.Error = $"A {n} lemez nem osztható el egyenlően {g} csoportra.";
                    return r;
                }
                if (n / g < 3)
                {
                    r.Error = "RAID 50 esetén csoportonként legalább 3 lemez kell.";
                    return r;
                }
                dataDisks = n - g;
                r.FaultTolerance = 1;
                r.ToleranceText = $"csoportonként 1 lemez (összesen legfeljebb {g}, " +
                                  "ha külön csoportokban esnek ki)";
                r.WritePenalty = 4; r.ReadFactor = n;
                break;

            case RaidLevel.Raid60:
                if (n % g != 0)
                {
                    r.Error = $"A {n} lemez nem osztható el egyenlően {g} csoportra.";
                    return r;
                }
                if (n / g < 4)
                {
                    r.Error = "RAID 60 esetén csoportonként legalább 4 lemez kell.";
                    return r;
                }
                dataDisks = n - 2 * g;
                r.FaultTolerance = 2;
                r.ToleranceText = $"csoportonként 2 lemez (összesen legfeljebb {2 * g})";
                r.WritePenalty = 6; r.ReadFactor = n;
                break;

            default:
                r.Error = "Ismeretlen RAID szint.";
                return r;
        }

        r.UsableBytes = dataDisks * diskBytes;
        r.RedundancyBytes = (long)n * diskBytes - r.UsableBytes;
        r.EfficiencyPercent = r.RawBytes > 0 ? r.UsableBytes * 100.0 / r.RawBytes : 0;

        // ---------------- újraépítés ideje
        var rate = Math.Max(1, inp.RebuildMBps) * 1_000_000d;   // MB/s
        r.RebuildTime = TimeSpan.FromSeconds(diskBytes / rate);

        // ---------------- olvasási hiba (URE) kockázata újraépítés közben
        long readBytes = inp.Level switch
        {
            RaidLevel.Raid5 => (n - 1L) * diskBytes,
            RaidLevel.Raid6 => (n - 1L) * diskBytes,
            RaidLevel.Raid50 => (n / g - 1L) * diskBytes,
            RaidLevel.Raid60 => (n / g - 1L) * diskBytes,
            RaidLevel.Raid1 or RaidLevel.Raid10 => diskBytes,
            _ => 0
        };

        if (readBytes > 0)
        {
            var bits = readBytes * 8.0;
            r.UreRiskPercent = (1 - Math.Exp(-bits / inp.UreRate)) * 100.0;

            r.UreNote = inp.Level switch
            {
                RaidLevel.Raid5 or RaidLevel.Raid50 =>
                    "Egyszeres paritásnál egy ilyen hiba az újraépítés közben adatvesztést " +
                    "vagy a tömb elvesztését okozhatja, mert nincs miből pótolni.",
                RaidLevel.Raid6 or RaidLevel.Raid60 =>
                    "Kétszeres paritásnál ez túlélhető: a második paritás pótolja a hibás blokkot.",
                _ =>
                    "Tükrözésnél csak a párt kell végigolvasni, ezért a kockázat nagyságrenddel kisebb."
            };
        }

        BuildNotes(inp, r, n, g, diskBytes);

        r.Valid = true;
        return r;
    }

    private static void BuildNotes(RaidInput inp, RaidResult r, int n, int g, long diskBytes)
    {
        // --- figyelmeztetések
        if (inp.Level is RaidLevel.Raid0 or RaidLevel.Jbod)
            r.Warnings.Add("Ebben a felállásban NINCS redundancia. Egyetlen lemez meghibásodása " +
                           "adatvesztéssel jár – csak akkor használd, ha van külön mentés.");

        if (inp.Level is RaidLevel.Raid5 or RaidLevel.Raid50)
        {
            if (diskBytes >= 4_000_000_000_000L)
                r.Warnings.Add("4 TB-nál nagyobb lemezeknél a RAID 5 kockázatos: az újraépítés " +
                               "órákig-napokig tart, és közben egyetlen olvasási hiba is elviheti " +
                               "a tömböt. Ekkora lemezeknél a RAID 6 az ajánlott.");

            if (r.UreRiskPercent >= 30)
                r.Warnings.Add($"Az újraépítés alatti olvasásihiba-kockázat {r.UreRiskPercent:0}% – " +
                               "ez nagyon magas. Válts RAID 6-ra, vagy jobb URE-mutatójú lemezekre.");
        }

        if (inp.Level == RaidLevel.Raid10 && n >= 8)
            r.Notes.Add("Nagy RAID 10 tömbnél a védettség szerencse kérdése: ha ugyanannak a " +
                        "tükörpárnak mindkét tagja kiesik, a tömb elvész.");

        if (inp.HotSpares == 0 && r.FaultTolerance > 0)
            r.Notes.Add("Nincs hot spare. Meghibásodás esetén az újraépítés csak akkor indul, " +
                        "amikor valaki fizikailag kicseréli a lemezt – addig a tömb védtelen.");
        else if (inp.HotSpares > 0)
            r.Notes.Add($"{inp.HotSpares} hot spare lemez azonnal beugrik meghibásodáskor, " +
                        "így az újraépítés emberi beavatkozás nélkül elindul.");

        // --- írási büntetés magyarázata
        r.Notes.Add(inp.Level switch
        {
            RaidLevel.Raid5 or RaidLevel.Raid50 =>
                "Írási büntetés 4×: egy blokk módosítása két olvasást és két írást jelent " +
                "(régi adat + régi paritás beolvasása, majd mindkettő visszaírása). " +
                "Írásintenzív adatbázisnál ez érezhető.",
            RaidLevel.Raid6 or RaidLevel.Raid60 =>
                "Írási büntetés 6×: két paritásblokkot kell karbantartani, ezért írásban " +
                "a RAID 6 a leglassabb.",
            RaidLevel.Raid10 =>
                "Írási büntetés 2×: minden blokk két lemezre megy. Írásintenzív terhelésnél " +
                "ez a leggyorsabb redundáns megoldás.",
            RaidLevel.Raid1 =>
                "Írási büntetés 2×: minden írás minden tükörre elmegy.",
            _ => "Nincs írási büntetés: az adat közvetlenül a lemezekre kerül."
        });

        // --- kapacitás értelmezése
        r.Notes.Add("A gyártók decimális TB-ban (10¹²) hirdetik a lemezt, a Windows viszont " +
                    "binárisan (TiB, 2⁴⁰) számol – ezért lesz egy 4 TB-os lemez a rendszerben " +
                    "kb. 3,64 TB. A jelentés mindkét értéket mutatja.");

        r.Notes.Add("Az újraépítés becsült ideje ideális, terheletlen tömbre vonatkozik. " +
                    "Éles, használat alatti rendszeren 2–5-ször ennyi is lehet.");

        r.Notes.Add("A RAID nem mentés: nem véd a véletlen törlés, a zsarolóvírus, a tűz vagy " +
                    "a vezérlőhiba ellen. Külön mentés mindenképp kell.");
    }

    // ================================================================ összehasonlítás

    public sealed record Comparison(
        RaidLevel Level, string Name, bool Valid, string Error,
        long UsableBytes, double EfficiencyPercent, string ToleranceText, int WritePenalty);

    /// <summary>Ugyanazzal a lemezkészlettel az összes RAID szint eredménye.</summary>
    public static List<Comparison> CompareAll(RaidInput inp)
    {
        var list = new List<Comparison>();

        foreach (RaidLevel level in Enum.GetValues<RaidLevel>())
        {
            var copy = new RaidInput
            {
                Level = level,
                DiskCount = inp.DiskCount,
                DiskSize = inp.DiskSize,
                Unit = inp.Unit,
                HotSpares = inp.HotSpares,
                Groups = inp.Groups,
                RebuildMBps = inp.RebuildMBps,
                UreRate = inp.UreRate
            };

            var res = Compute(copy);
            list.Add(new Comparison(
                level, LevelName(level), res.Valid, res.Error,
                res.UsableBytes, res.EfficiencyPercent, res.ToleranceText, res.WritePenalty));
        }

        return list;
    }

    /// <summary>Kapacitás mindkét mértékrendszerben.</summary>
    public static string BothUnits(long bytes)
    {
        if (bytes <= 0) return "–";
        var tb = bytes / 1_000_000_000_000d;
        var tib = bytes / (1024d * 1024 * 1024 * 1024);

        if (tb < 1)
        {
            var gb = bytes / 1_000_000_000d;
            var gib = bytes / (1024d * 1024 * 1024);
            return $"{gb:0.##} GB  ({gib:0.##} GiB)";
        }

        return $"{tb:0.##} TB  ({tib:0.##} TiB)";
    }

    public static string Duration(TimeSpan t)
    {
        if (t.TotalHours < 1) return $"{t.TotalMinutes:0} perc";
        if (t.TotalDays < 1) return $"{(int)t.TotalHours} óra {t.Minutes} perc";
        return $"{(int)t.TotalDays} nap {t.Hours} óra";
    }
}
