using System.Runtime.Versioning;
using LibreHardwareMonitor.Hardware;

namespace DevToolbox;

public enum SmartVerdict { Ismeretlen, Rendben, Figyelendo, Kritikus }

public sealed class SmartAttribute
{
    public byte Id;
    public string Name = "";
    public byte Current;
    public byte Worst;
    public byte Threshold;
    public ulong Raw;
    public bool Critical;          // ez az attributum onmagaban meghibasodast jelez
    public string Note = "";

    public bool BelowThreshold => Threshold > 0 && Current > 0 && Current <= Threshold;

    public string RawDisplay => Id switch
    {
        190 or 194 => $"{Raw & 0xFF} °C",
        9 => $"{Raw} óra  ({Raw / 24} nap)",
        241 or 242 => Fmt.Bytes(Raw * 512UL),
        _ => Raw.ToString()
    };
}

public sealed class SmartDrive
{
    public string Model = "";
    public string InstanceName = "";
    public string Serial = "";
    public string Firmware = "";
    public ulong SizeBytes;
    public string MediaType = "";
    public bool? PredictFailure;
    public List<SmartAttribute> Attributes = new();
    public List<(string Key, string Value)> ExtraSensors = new();   // NVMe / LHM adatok
    public string Source = "";

    public SmartVerdict Verdict { get; set; } = SmartVerdict.Ismeretlen;
    public List<string> Findings { get; } = new();
}

[SupportedOSPlatform("windows")]
public static class SmartInfo
{
    // A gyakorlatban szamito attributumok. A "critical" azt jelenti: ha nem nulla, baj van.
    private static readonly Dictionary<byte, (string Name, bool Critical, string Note)> Known = new()
    {
        [1] = ("Olvasási hibaarány", false, ""),
        [3] = ("Felpörgési idő", false, ""),
        [4] = ("Indítás/leállítás szám", false, ""),
        [5] = ("Újraosztott szektorok", true,
               "A meghajtó hibás szektorokat talált és tartalékkal pótolta. Ha nő, a lemez romlik."),
        [7] = ("Keresési hibaarány", false, ""),
        [9] = ("Üzemidő", false, ""),
        [10] = ("Újrapörgetési kísérletek", true, "Mechanikai probléma jele."),
        [12] = ("Bekapcsolások száma", false, ""),
        [170] = ("Tartalék blokkok (SSD)", true, "Fogyó tartalék a vezérlőben."),
        [171] = ("Programozási hibák (SSD)", false, ""),
        [172] = ("Törlési hibák (SSD)", false, ""),
        [173] = ("Kopáskiegyenlítés (SSD)", false, ""),
        [174] = ("Váratlan áramkimaradás", false, "Nem szabályos leállások száma."),
        [175] = ("Programozási hiba (teljes lemez)", false, ""),
        [177] = ("Kopási szint (SSD)", false, ""),
        [180] = ("Használatlan tartalék blokkok", false, ""),
        [183] = ("SATA kapcsolat hibák", false, "Kábel- vagy csatlakozóhiba jele."),
        [184] = ("Végponti hibák", true, "Adatátviteli integritási hiba."),
        [187] = ("Nem javítható hibák", true,
                 "A meghajtó olyan hibát jelentett, amit nem tudott kijavítani."),
        [188] = ("Parancs időtúllépés", false, "Gyakran kábel- vagy tápellátási gond."),
        [190] = ("Hőmérséklet (légáram)", false, ""),
        [194] = ("Hőmérséklet", false, ""),
        [196] = ("Újraosztási események", false, ""),
        [197] = ("Függőben lévő hibás szektorok", true,
                 "Instabil szektorok, amiket a meghajtó még nem tudott kiolvasni. Adatvesztés kockázata."),
        [198] = ("Javíthatatlan szektorok", true,
                 "Végleg olvashatatlan szektorok. A rajtuk lévő adat elveszett."),
        [199] = ("UDMA CRC hibák", false,
                 "Szinte mindig KÁBELHIBA, nem a lemez hibája. Cseréld a SATA kábelt."),
        [200] = ("Írási hibaarány", false, ""),
        [202] = ("Fennmaradó élettartam (SSD)", false, ""),
        [230] = ("Meghajtó életgörbe", false, ""),
        [231] = ("Fennmaradó élettartam (SSD)", false, ""),
        [232] = ("Fennmaradó tartalék", false, ""),
        [233] = ("Médiakopás (SSD)", false, ""),
        [241] = ("Összes írt adat", false, ""),
        [242] = ("Összes olvasott adat", false, ""),
    };

    public static List<SmartDrive> Collect(WmiContext ctx, bool includeLocalSensors = true,
                                            SensorReader? reader = null)
    {
        var drives = new List<SmartDrive>();

        // --- 1. fizikai lemezek alapadatai
        foreach (var d in ctx.Query("SELECT * FROM Win32_DiskDrive"))
        {
            var pnp = d["PNPDeviceID"]?.ToString() ?? "";
            drives.Add(new SmartDrive
            {
                Model = (d["Model"]?.ToString() ?? "ismeretlen").Trim(),
                InstanceName = pnp,
                Serial = (d["SerialNumber"]?.ToString() ?? "").Trim(),
                Firmware = (d["FirmwareRevision"]?.ToString() ?? "").Trim(),
                SizeBytes = HardwareInfo.ToULong(d["Size"]),
                MediaType = d["MediaType"]?.ToString() ?? ""
            });
        }

        // --- 2. meghibasodas-elorejelzes
        foreach (var o in ctx.Query("SELECT * FROM MSStorageDriver_FailurePredictStatus", @"root\WMI"))
        {
            var inst = Normalize(o["InstanceName"]?.ToString());
            var drive = Match(drives, inst);
            if (drive is null) continue;
            try { drive.PredictFailure = (bool?)o["PredictFailure"]; } catch { }
            drive.Source = "SATA SMART (WMI)";
        }

        // --- 3. kuszobertekek
        var thresholds = new Dictionary<string, Dictionary<byte, byte>>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in ctx.Query("SELECT * FROM MSStorageDriver_FailurePredictThresholds", @"root\WMI"))
        {
            var inst = Normalize(o["InstanceName"]?.ToString());
            if (o["VendorSpecific"] is not byte[] raw) continue;
            var map = new Dictionary<byte, byte>();
            for (int off = 2; off + 11 < raw.Length; off += 12)
            {
                var id = raw[off];
                if (id == 0) continue;
                map[id] = raw[off + 1];
            }
            thresholds[inst] = map;
        }

        // --- 4. attributumok
        foreach (var o in ctx.Query("SELECT * FROM MSStorageDriver_FailurePredictData", @"root\WMI"))
        {
            var inst = Normalize(o["InstanceName"]?.ToString());
            var drive = Match(drives, inst);
            if (drive is null) continue;
            if (o["VendorSpecific"] is not byte[] raw) continue;

            thresholds.TryGetValue(inst, out var th);
            drive.Attributes = ParseAttributes(raw, th);
            drive.Source = "SATA SMART (WMI)";
        }

        // --- 5. NVMe es egyeb: a szenzorkonyvtar tobbet lat (csak helyben).
        //     A megosztott SensorReader peldanyt hasznaljuk - sajat Computer nyitasa
        //     elrantana a kernel illesztot a homerseklet-panel alol.
        if (includeLocalSensors && !ctx.IsRemote && reader is not null)
            MergeSensorData(drives, reader);

        foreach (var d in drives) Evaluate(d);
        return drives;
    }

    internal static List<SmartAttribute> ParseAttributes(byte[] raw, Dictionary<byte, byte>? th)
    {
        var list = new List<SmartAttribute>();

        // A szerkezet: 2 bajt verzio, majd 30 db 12 bajtos bejegyzes.
        for (int off = 2; off + 11 < raw.Length && list.Count < 30; off += 12)
        {
            var id = raw[off];
            if (id == 0) continue;

            ulong rawVal = 0;
            for (int i = 0; i < 6; i++) rawVal |= (ulong)raw[off + 5 + i] << (8 * i);

            var known = Known.TryGetValue(id, out var k) ? k : ((string?)null, false, "");

            var attr = new SmartAttribute
            {
                Id = id,
                Name = known.Item1 ?? $"Attribútum {id}",
                Current = raw[off + 3],
                Worst = raw[off + 4],
                Raw = rawVal,
                Critical = known.Item2,
                Note = known.Item3
            };

            if (th is not null && th.TryGetValue(id, out var t)) attr.Threshold = t;
            list.Add(attr);
        }

        return list;
    }

    /// <summary>
    /// NVMe meghajtoknal a WMI SMART osztaly ures - a szenzorkonyvtar viszont olvassa.
    /// A KOZOS olvasotol kerjuk el az adatokat, sajat peldanyt nem nyitunk.
    /// </summary>
    private static void MergeSensorData(List<SmartDrive> drives, SensorReader reader)
    {
        try
        {
            foreach (var grp in reader.ReadStorageSensors().GroupBy(x => x.Hardware))
            {
                var drive = drives.FirstOrDefault(d =>
                    !string.IsNullOrEmpty(d.Model) &&
                    (grp.Key.Contains(d.Model, StringComparison.OrdinalIgnoreCase) ||
                     d.Model.Contains(grp.Key, StringComparison.OrdinalIgnoreCase)));

                drive ??= drives.FirstOrDefault(d => d.ExtraSensors.Count == 0);
                if (drive is null) continue;

                foreach (var (_, sensor, text) in grp)
                    drive.ExtraSensors.Add((Translate(sensor), text));

                if (drive.ExtraSensors.Count > 0 && string.IsNullOrEmpty(drive.Source))
                    drive.Source = "NVMe / szenzorkönyvtár";
            }
        }
        catch { }
    }

    private static string Translate(string name) => name switch
    {
        "Temperature" => "Hőmérséklet",
        "Used Space" => "Felhasznált terület",
        "Data Read" => "Összes olvasott adat",
        "Data Written" => "Összes írt adat",
        "Available Spare" => "Elérhető tartalék",
        "Remaining Life" => "Fennmaradó élettartam",
        "Percentage Used" => "Felhasznált élettartam",
        "Write Amplification" => "Írási felerősítés",
        _ => name
    };

    /// <summary>A nyers szamokbol emberi kovetkeztetes.</summary>
    internal static void Evaluate(SmartDrive d)
    {
        var verdict = SmartVerdict.Rendben;

        if (d.PredictFailure == true)
        {
            d.Findings.Add("A meghajtó ÖNMAGA jelzi a közelgő meghibásodást (SMART failure predict). " +
                           "Azonnali mentés és csere indokolt.");
            verdict = SmartVerdict.Kritikus;
        }

        foreach (var a in d.Attributes)
        {
            if (a.BelowThreshold)
            {
                d.Findings.Add($"„{a.Name}” a gyártói küszöb alá esett ({a.Current} ≤ {a.Threshold}). " +
                               "Ez hivatalosan is meghibásodásnak számít.");
                verdict = SmartVerdict.Kritikus;
                continue;
            }

            if (!a.Critical || a.Raw == 0) continue;

            switch (a.Id)
            {
                case 197:
                case 198:
                    d.Findings.Add($"{a.Name}: {a.Raw} db. {a.Note}");
                    verdict = SmartVerdict.Kritikus;
                    break;
                case 5:
                case 187:
                case 184:
                case 10:
                case 170:
                    d.Findings.Add($"{a.Name}: {a.Raw} db. {a.Note}");
                    if (verdict < SmartVerdict.Figyelendo) verdict = SmartVerdict.Figyelendo;
                    break;
            }
        }

        // CRC hiba: nem kritikus, de fontos, mert kabelre utal
        var crc = d.Attributes.FirstOrDefault(a => a.Id == 199);
        if (crc is { Raw: > 0 })
        {
            d.Findings.Add($"UDMA CRC hibák: {crc.Raw} db. {crc.Note}");
            if (verdict < SmartVerdict.Figyelendo) verdict = SmartVerdict.Figyelendo;
        }

        // SSD elettartam a szenzorbol
        foreach (var (k, v) in d.ExtraSensors)
        {
            if (!k.Contains("élettartam", StringComparison.OrdinalIgnoreCase)) continue;
            if (!float.TryParse(v.Replace("%", "").Trim().Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var pct)) continue;

            if (k.StartsWith("Fennmaradó", StringComparison.OrdinalIgnoreCase) && pct < 20)
            {
                d.Findings.Add($"Az SSD fennmaradó élettartama {pct:0}% – tervezd a cserét.");
                if (verdict < SmartVerdict.Figyelendo) verdict = SmartVerdict.Figyelendo;
            }
            if (k.StartsWith("Felhasznált", StringComparison.OrdinalIgnoreCase) && pct > 80)
            {
                d.Findings.Add($"Az SSD élettartamának {pct:0}%-át felhasználta.");
                if (verdict < SmartVerdict.Figyelendo) verdict = SmartVerdict.Figyelendo;
            }
        }

        // hofok
        var temp = d.Attributes.FirstOrDefault(a => a.Id is 194 or 190);
        if (temp is not null)
        {
            var c = temp.Raw & 0xFF;
            if (c >= 60)
            {
                d.Findings.Add($"A meghajtó hőmérséklete {c} °C – ez tartósan soknak számít.");
                if (verdict < SmartVerdict.Figyelendo) verdict = SmartVerdict.Figyelendo;
            }
        }

        if (d.Attributes.Count == 0 && d.ExtraSensors.Count == 0)
        {
            verdict = SmartVerdict.Ismeretlen;
            d.Findings.Add(
                "Ehhez a meghajtóhoz nem érkezett SMART adat. Leggyakoribb okok: nincs " +
                "rendszergazdai jog; USB-s házban lévő lemez (a híd nem továbbítja a SMART " +
                "parancsokat); RAID vezérlő mögötti lemez; vagy virtuális gép.");
        }
        else if (verdict == SmartVerdict.Rendben)
        {
            d.Findings.Add("A vizsgált mutatók rendben vannak, hibajelzés nincs.");
        }

        d.Verdict = verdict;
    }

    internal static string Normalize(string? instanceName)
    {
        var s = instanceName ?? "";
        var idx = s.LastIndexOf('_');
        if (idx > 0 && idx == s.Length - 2) s = s[..idx];   // a lezaro "_0" levagasa
        return s.Trim();
    }

    private static SmartDrive? Match(List<SmartDrive> drives, string instance)
    {
        if (string.IsNullOrEmpty(instance)) return null;
        return drives.FirstOrDefault(d =>
                   d.InstanceName.Equals(instance, StringComparison.OrdinalIgnoreCase))
            ?? drives.FirstOrDefault(d =>
                   d.InstanceName.Length > 0 &&
                   instance.StartsWith(d.InstanceName, StringComparison.OrdinalIgnoreCase));
    }
}
