using System.Runtime.Versioning;
using System.Text;

namespace DevToolbox;

[SupportedOSPlatform("windows")]
public sealed class HardwarePage : UserControl
{
    private readonly ListView _info = new();
    private readonly ListView _sensors = new();
    private readonly ListView _smart = new();
    private readonly RichTextBox _smartDetail = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly Label _hint = new();
    private readonly ToolStripStatusLabel _status = new();
    private readonly ToolStripButton _chkLive;

    private SensorReader? _reader;
    private List<SmartDrive> _smartDrives = new();
    private bool _loaded;
    private volatile bool _reading;
    private readonly Dictionary<string, ListViewItem> _sensorItems = new();      // egyszerre csak egy hattérszalas szenzorolvasas

    public HardwarePage()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.White;

        var tools = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            RenderMode = ToolStripRenderMode.System,
            Padding = new Padding(6, 4, 6, 4)
        };
        var btnRefresh = new ToolStripButton("Frissítés") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnRefresh.Click += async (_, _) => await ReloadAsync();

        _chkLive = new ToolStripButton("Élő szenzorok")
        {
            CheckOnClick = true,
            Checked = AppSettings.Current.LiveSensors,
            DisplayStyle = ToolStripItemDisplayStyle.Text
        };
        _chkLive.CheckedChanged += (_, _) => _timer.Enabled = _chkLive.Checked && Visible;

        var btnExport = new ToolStripButton("Mentés szövegfájlba") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnExport.Click += (_, _) => Export();

        var btnDiag = new ToolStripButton("Diagnosztika") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnDiag.ToolTipText = "Miért nem látszik a hőmérséklet?";
        btnDiag.Click += (_, _) => ShowDiagnostics();

        tools.Items.AddRange(new ToolStripItem[]
            { btnRefresh, _chkLive, new ToolStripSeparator(), btnExport, btnDiag });

        SetupList(_info, ("Tulajdonság", 220), ("Érték", 380));
        SetupList(_sensors, ("Érzékelő", 200), ("Érték", 85), ("Típus", 100));
        SetupList(_smart, ("", 24), ("Meghajtó", 250), ("Állapot", 110));
        _smart.ShowGroups = false;
        _smart.SelectedIndexChanged += (_, _) => ShowSmartDetail();

        _smartDetail.Dock = DockStyle.Fill;
        _smartDetail.ReadOnly = true;
        _smartDetail.BorderStyle = BorderStyle.None;
        _smartDetail.BackColor = Color.White;
        _smartDetail.Font = new Font("Segoe UI", 9F);

        _hint.Dock = DockStyle.Top;
        _hint.Height = 34;
        _hint.Padding = new Padding(10, 8, 10, 6);
        _hint.BackColor = Color.FromArgb(255, 248, 225);
        _hint.ForeColor = Color.FromArgb(120, 88, 10);
        _hint.Visible = false;

        // ---- bal: leltár
        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(_info);
        left.Controls.Add(MakeHeader("Rendszer és hardver"));

        // ---- jobb felső: élő szenzorok
        var sensorPanel = new Panel { Dock = DockStyle.Fill };
        sensorPanel.Controls.Add(_sensors);
        sensorPanel.Controls.Add(_hint);
        sensorPanel.Controls.Add(MakeHeader("Hőmérséklet és terhelés"));

        // ---- jobb alsó: SMART
        var smartSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 5,
            BorderStyle = BorderStyle.None
        };
        smartSplit.ApplyWhenSized(120);
        smartSplit.Panel1.Controls.Add(_smart);
        smartSplit.Panel2.Controls.Add(_smartDetail);

        var smartPanel = new Panel { Dock = DockStyle.Fill };
        smartPanel.Controls.Add(smartSplit);
        smartPanel.Controls.Add(MakeHeader("Lemezek állapota (SMART)"));

        var rightSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            BorderStyle = BorderStyle.None
        };
        rightSplit.ApplyWhenSized(230);
        rightSplit.Panel1.Controls.Add(sensorPanel);
        rightSplit.Panel2.Controls.Add(smartPanel);

        var mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
            BorderStyle = BorderStyle.None
        };
        mainSplit.ApplyWhenSized(580);
        mainSplit.Panel1.Controls.Add(left);
        mainSplit.Panel2.Controls.Add(rightSplit);

        var strip = new StatusStrip { SizingGrip = false };
        _status.Text = "Készen áll.";
        strip.Items.Add(_status);

        Controls.Add(mainSplit);
        Controls.Add(tools);
        Controls.Add(strip);

        _timer.Interval = Math.Clamp(AppSettings.Current.SensorRefreshSeconds, 1, 60) * 1000;
        _timer.Tick += async (_, _) => await UpdateSensorsAsync();

        // Ha a fül nem látszik, ne pörgesse a szenzorokat feleslegesen.
        VisibleChanged += (_, _) => _timer.Enabled = Visible && _chkLive.Checked && _loaded;
    }

    private static Label MakeHeader(string text) => new()
    {
        Text = "  " + text,
        Dock = DockStyle.Top,
        Height = 28,
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.FromArgb(246, 247, 249),
        ForeColor = Color.FromArgb(60, 68, 82),
        Font = new Font("Segoe UI Semibold", 9F)
    };

    private static void SetupList(ListView lv, params (string Header, int Width)[] cols)
    {
        lv.View = View.Details;
        lv.Dock = DockStyle.Fill;
        lv.FullRowSelect = true;
        lv.BorderStyle = BorderStyle.None;
        lv.ShowGroups = true;
        lv.MultiSelect = false;
        lv.HideSelection = false;
        foreach (var c in cols) lv.Columns.Add(c.Header, c.Width);
    }

    // ================================================================ betöltés

    public async void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        // async void: ha a betoltes kivetelt dob, az itt elnyelodne es osszeomlaszthatna
        // az appot. Ezert becsomagoljuk, es a hibat a felhasznalonak jelezzuk.
        try { await ReloadAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show("A fül betöltése hibába ütközött:\n\n" + ex.Message,
                "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task ReloadAsync()
    {
        _status.Text = "Hardveradatok lekérdezése…";

        // A WMI lekérdezések másodpercekig tarthatnak -> háttérszálon futnak.
        var sections = await Task.Run(() =>
        {
            try { return HardwareInfo.Collect(); }
            catch { return new List<HwSection>(); }
        });

        FillInfo(sections);

        _status.Text = "Érzékelők indítása…";
        await Task.Run(() =>
        {
            if (_reader is null)
            {
                _reader = new SensorReader();
                _reader.Open();
            }
        });

        if (!HardwareInfo.IsElevated())
        {
            _hint.Text = "Rendszergazdai jog nélkül a hőmérsékletek és a SMART adatok egy része nem olvasható. " +
                         "Program → Újraindítás rendszergazdaként.";
            _hint.Visible = true;
        }
        else if (_reader?.OpenError is not null)
        {
            _hint.Text = "Szenzorok: " + _reader.OpenError;
            _hint.Visible = true;
        }
        else if (_reader is { DriverLoaded: false })
        {
            _hint.Text = SensorReader.IsMemoryIntegrityEnabled()
                ? "A Memóriaintegritás (Core Isolation) be van kapcsolva – ez blokkolja a " +
                  "szenzor-illesztőt, ezért nincs CPU-hőmérséklet. Részletek: Diagnosztika."
                : "A CPU-hőmérséklethez szükséges kernel-illesztő nem töltődött be. " +
                  "Alaplapi (ACPI) és lemez-hőfokot ilyenkor is mutatunk. Részletek: Diagnosztika.";
            _hint.Visible = true;
        }
        else
        {
            _hint.Visible = false;
        }

        await UpdateSensorsAsync();
        await LoadSmartAsync();

        _timer.Enabled = Visible && _chkLive.Checked;
        _status.Text = "Kész.";
    }

    private void FillInfo(List<HwSection> sections)
    {
        _info.BeginUpdate();
        _info.Items.Clear();
        _info.Groups.Clear();

        foreach (var sec in sections)
        {
            var g = SplitHelper.NewGroup(sec.Title, sec.Collapsed);
            _info.Groups.Add(g);
            foreach (var row in sec.Rows)
                _info.Items.Add(new ListViewItem(new[] { row.Key, row.Value }) { Group = g });
        }

        if (_info.Items.Count == 0)
            _info.Items.Add(new ListViewItem(new[] { "Nincs adat", "a WMI lekérdezés nem adott vissza semmit" }));

        _info.EndUpdate();
        _info.MakeGroupsCollapsible();
    }

    // ================================================================ szenzorok

    private async Task UpdateSensorsAsync()
    {
        if (_reader is null || _reading) return;
        _reading = true;

        try
        {
            // A hardver frissitese SMART- es I/O-muveleteket indit: sosem az UI szalon.
            var readings = await Task.Run(() =>
            {
                var list = new List<SensorReader.Reading>();
                try { list.AddRange(_reader.Read()); } catch { }

                // Az ACPI zonak kernel illeszto nelkul is olvashatok, es gyorsitotarazva
                // vannak - erdemes mindig mellevenni.
                try { list.AddRange(SensorReader.ReadAcpiThermalZones()); } catch { }

                return list;
            });

            if (IsDisposed || !IsHandleCreated) return;
            FillSensors(readings);
        }
        finally { _reading = false; }
    }

    private void FillSensors(List<SensorReader.Reading> readings)
    {
        var wanted = readings
            .Where(r => r.Type is "Hőmérséklet" or "Terhelés" or "Ventilátor")
            .ToList();

        if (wanted.Count == 0)
        {
            if (_sensors.Items.Count == 1 && _sensors.Groups.Count == 0) return;   // mar ez all ott
            _sensors.BeginUpdate();
            _sensors.Items.Clear();
            _sensors.Groups.Clear();
            _sensorItems.Clear();
            _sensors.Items.Add(new ListViewItem(new[]
                { "Nincs olvasható érzékelő", "–", "lásd a Diagnosztika gombot" }));
            _sensors.EndUpdate();
            return;
        }

        var keys = wanted.Select(Key).ToList();
        var sameSet = keys.Count == _sensorItems.Count && keys.All(_sensorItems.ContainsKey);

        // Ha ugyanazok az erzekelok, csak az ertekeket irjuk at. A teljes ujraepites
        // villog es csoportokkal kifejezetten lassu.
        if (sameSet)
        {
            _sensors.BeginUpdate();
            foreach (var r in wanted)
            {
                var item = _sensorItems[Key(r)];
                var text = Format(r);
                if (item.SubItems[1].Text != text) item.SubItems[1].Text = text;
                var color = ColorFor(r);
                if (item.ForeColor != color) item.ForeColor = color;
            }
            _sensors.EndUpdate();
            return;
        }

        _sensors.BeginUpdate();
        _sensors.Items.Clear();
        _sensors.Groups.Clear();
        _sensorItems.Clear();

        foreach (var grp in wanted.GroupBy(r => r.Hardware))
        {
            var g = SplitHelper.NewGroup(grp.Key);
            _sensors.Groups.Add(g);

            foreach (var r in grp.OrderBy(x => x.Type).ThenBy(x => x.Sensor))
            {
                var lvi = new ListViewItem(new[] { r.Sensor, Format(r), r.Type })
                { Group = g, ForeColor = ColorFor(r) };
                _sensors.Items.Add(lvi);
                _sensorItems[Key(r)] = lvi;
            }
        }

        _sensors.EndUpdate();
        _sensors.MakeGroupsCollapsible();
    }

    private static string Key(SensorReader.Reading r) => $"{r.Hardware}|{r.Sensor}|{r.Type}";

    private static string Format(SensorReader.Reading r) =>
        r.Unit is "°C" or "%" ? $"{r.Value:0.#} {r.Unit}" : $"{r.Value:0} {r.Unit}";

    private static Color ColorFor(SensorReader.Reading r)
    {
        if (r.Unit != "°C") return Color.FromArgb(40, 46, 58);

        var crit = AppSettings.Current.TempCriticalCelsius;
        var warn = AppSettings.Current.TempWarnCelsius;
        return r.Value >= crit ? Color.FromArgb(200, 30, 30)
             : r.Value >= warn ? Color.FromArgb(200, 120, 0)
             : Color.FromArgb(20, 120, 60);
    }

    // ================================================================ SMART

    private async Task LoadSmartAsync()
    {
        _status.Text = "SMART adatok olvasása…";

        _smartDrives = await Task.Run(() =>
        {
            try { return SmartInfo.Collect(WmiContext.Local, true, _reader); }
            catch { return new List<SmartDrive>(); }
        });

        _smart.BeginUpdate();
        _smart.Items.Clear();

        foreach (var d in _smartDrives)
        {
            var mark = d.Verdict switch
            {
                SmartVerdict.Kritikus => "!!",
                SmartVerdict.Figyelendo => "!",
                SmartVerdict.Rendben => "✓",
                _ => "?"
            };

            var label = d.Model;
            if (d.SizeBytes > 0) label += $"  ({Fmt.Bytes(d.SizeBytes)})";

            var lvi = new ListViewItem(new[] { mark, label, VerdictText(d.Verdict) }) { Tag = d };
            lvi.ForeColor = VerdictColor(d.Verdict);
            if (d.Verdict >= SmartVerdict.Figyelendo)
                lvi.Font = new Font(_smart.Font, FontStyle.Bold);
            _smart.Items.Add(lvi);
        }

        if (_smart.Items.Count == 0)
            _smart.Items.Add(new ListViewItem(new[] { "?", "Nem található fizikai lemez", "" }));

        _smart.EndUpdate();

        if (_smart.Items.Count > 0) _smart.Items[0].Selected = true;
    }

    private static string VerdictText(SmartVerdict v) => v switch
    {
        SmartVerdict.Kritikus => "Hibás",
        SmartVerdict.Figyelendo => "Figyelendő",
        SmartVerdict.Rendben => "Rendben",
        _ => "Nincs adat"
    };

    private static Color VerdictColor(SmartVerdict v) => v switch
    {
        SmartVerdict.Kritikus => Color.FromArgb(190, 30, 30),
        SmartVerdict.Figyelendo => Color.FromArgb(185, 100, 0),
        SmartVerdict.Rendben => Color.FromArgb(20, 120, 60),
        _ => Color.FromArgb(130, 136, 148)
    };

    private void ShowSmartDetail()
    {
        _smartDetail.Clear();
        if (_smart.SelectedItems.Count == 0) return;
        if (_smart.SelectedItems[0].Tag is not SmartDrive d) return;

        App(d.Model, 12F, FontStyle.Bold, VerdictColor(d.Verdict));

        var meta = new List<string>();
        if (d.SizeBytes > 0) meta.Add(Fmt.Bytes(d.SizeBytes));
        if (d.Firmware.Length > 0) meta.Add("firmware: " + d.Firmware);
        if (d.Serial.Length > 0) meta.Add("sorozatszám: " + d.Serial);
        if (d.Source.Length > 0) meta.Add("forrás: " + d.Source);
        if (meta.Count > 0) App(string.Join("  ·  ", meta), 8.5F, FontStyle.Regular, Color.FromArgb(130, 137, 150));
        App("");

        App("Értékelés", 9.5F, FontStyle.Bold, Color.FromArgb(40, 46, 58));
        foreach (var f in d.Findings)
            App("• " + f, 9.5F, FontStyle.Regular, Color.FromArgb(50, 56, 68));
        App("");

        if (d.ExtraSensors.Count > 0)
        {
            App("Élettartam és hőmérséklet", 9.5F, FontStyle.Bold, Color.FromArgb(40, 46, 58));
            foreach (var (k, v) in d.ExtraSensors)
                App($"   {k,-28} {v}", 9F, FontStyle.Regular, Color.FromArgb(50, 56, 68));
            App("");
        }

        if (d.Attributes.Count > 0)
        {
            App("SMART attribútumok", 9.5F, FontStyle.Bold, Color.FromArgb(40, 46, 58));
            App($"   {"ID",-5}{"Megnevezés",-34}{"Nyers érték",-22}{"Akt.",-6}{"Küszöb",-7}",
                8.5F, FontStyle.Bold, Color.FromArgb(120, 127, 140));

            foreach (var a in d.Attributes.OrderBy(x => x.Id))
            {
                var color = a.BelowThreshold || (a.Critical && a.Raw > 0)
                    ? Color.FromArgb(180, 40, 40)
                    : Color.FromArgb(60, 66, 78);

                App($"   {a.Id,-5}{Trunc(a.Name, 33),-34}{Trunc(a.RawDisplay, 21),-22}{a.Current,-6}{a.Threshold,-7}",
                    8.5F, FontStyle.Regular, color);
            }
        }

        _smartDetail.SelectionStart = 0;
        _smartDetail.ScrollToCaret();
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private void App(string text, float size = 9.5F, FontStyle style = FontStyle.Regular, Color? color = null)
    {
        _smartDetail.SelectionStart = _smartDetail.TextLength;
        _smartDetail.SelectionLength = 0;
        _smartDetail.SelectionFont = new Font(
            style == FontStyle.Regular && size <= 9F ? "Consolas" : "Segoe UI", size, style);
        _smartDetail.SelectionColor = color ?? Color.Black;
        _smartDetail.AppendText(text + Environment.NewLine);
    }

    private void ShowDiagnostics()
    {
        var temps = _sensors.Items.Cast<ListViewItem>()
            .Count(i => i.SubItems.Count > 2 && i.SubItems[2].Text == "Hőmérséklet");

        var sb = new StringBuilder();
        sb.AppendLine("Érzékelő-diagnosztika");
        sb.AppendLine(new string('-', 48));
        sb.AppendLine($"Jogosultság:            {(HardwareInfo.IsElevated() ? "rendszergazda" : "NORMÁL – ez kevés")}");
        sb.AppendLine($"Kernel-illesztő:        {(_reader is { DriverLoaded: true } ? "betöltve" : "NEM töltődött be")}");
        sb.AppendLine($"Megjelenített hőfokok:  {temps} db");
        sb.AppendLine($"ACPI termikus zónák:    {SensorReader.ReadAcpiThermalZones().Count} db");
        sb.AppendLine($"SMART lemez-hőfokok:    {_smartDrives.Count(d => d.ExtraSensors.Any(e => e.Key.Contains("Hőmérséklet")))} db");
        sb.AppendLine($"Felismert hardver:      {_reader?.HardwareCount ?? 0} db");
        sb.AppendLine($"Frissítési időköz:      {_timer.Interval / 1000} mp");
        if (_reader?.OpenError is { } err) sb.AppendLine($"Hiba:                   {err}");
        if (_reader is not null) sb.AppendLine($"Részletek:              {_reader.Diagnostics}");
        sb.AppendLine();
        var hvci = SensorReader.IsMemoryIntegrityEnabled();
        sb.AppendLine($"Memóriaintegritás:      {(hvci ? "BE – ez a legvalószínűbb ok" : "ki")}");
        sb.AppendLine();

        if (hvci)
        {
            sb.AppendLine("A Memóriaintegritás (Core Isolation / HVCI) blokkolja a szenzor-");
            sb.AppendLine("illesztőket, mert azok szerepelnek a Microsoft sérülékeny-illesztő");
            sb.AppendLine("listáján. Ez új gépeken alapból be van kapcsolva.");
            sb.AppendLine();
            sb.AppendLine("Két lehetőség:");
            sb.AppendLine("  1. Marad bekapcsolva, és beéred az ACPI + NVMe hőfokokkal.");
            sb.AppendLine("     Laptopon az ACPI zóna gyakorlatilag a CPU hőfokát követi.");
            sb.AppendLine("  2. Windows biztonság → Eszközbiztonság → Magelkülönítés →");
            sb.AppendLine("     Memóriaintegritás kikapcsolása, majd újraindítás.");
            sb.AppendLine("     FIGYELEM: ez gyengíti a gép védelmét. Céges gépen kérdezd meg");
            sb.AppendLine("     a biztonsági felelőst – gyakran házirend írja elő.");
        }
        else
        {
            sb.AppendLine("Ha a kernel-illesztő nem tölt be, a további lehetséges okok:");
            sb.AppendLine("  • egy másik monitorozó program (HWiNFO, AIDA64, MSI Afterburner)");
            sb.AppendLine("    lefoglalta ugyanazt az illesztőt – zárd be és próbáld újra");
            sb.AppendLine("  • vírusirtó blokkolja az illesztő betöltését");
            sb.AppendLine("  • virtuális gépen fut, ahol nincs valódi érzékelő");
        }

        sb.AppendLine();
        sb.AppendLine("A lemezek hőfoka SMART-ból ilyenkor is olvasható, az alaplapé ACPI-ból.");
        sb.AppendLine("Az érzékelők frissítési időköze a Beállításokban állítható.");

        MessageBox.Show(sb.ToString(), "Érzékelő-diagnosztika",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ================================================================ export

    private void Export()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Hardver jelentés mentése",
            Filter = "Szövegfájl|*.txt",
            FileName = $"hardver_{Environment.MachineName}_{DateTime.Now:yyyyMMdd_HHmm}.txt"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var sb = new StringBuilder();
        sb.AppendLine("DevToolbox – hardver jelentés");
        sb.AppendLine($"Készült: {DateTime.Now:yyyy.MM.dd HH:mm}");
        sb.AppendLine(new string('=', 64));
        sb.AppendLine();

        foreach (ListViewGroup g in _info.Groups)
        {
            sb.AppendLine("[" + g.Header + "]");
            foreach (ListViewItem i in g.Items)
                sb.AppendLine($"  {i.SubItems[0].Text,-28} {i.SubItems[1].Text}");
            sb.AppendLine();
        }

        sb.AppendLine("[Lemezek állapota]");
        foreach (var d in _smartDrives)
        {
            sb.AppendLine($"  {d.Model} – {VerdictText(d.Verdict)}");
            foreach (var f in d.Findings) sb.AppendLine("    • " + f);
            foreach (var a in d.Attributes.OrderBy(x => x.Id))
                sb.AppendLine($"      {a.Id,-4} {a.Name,-34} {a.RawDisplay,-20} akt:{a.Current} küszöb:{a.Threshold}");
            foreach (var (k, v) in d.ExtraSensors) sb.AppendLine($"      {k,-34} {v}");
            sb.AppendLine();
        }

        if (_reader is not null)
        {
            sb.AppendLine("[Érzékelők]");
            foreach (var r in _reader.Read().Where(r => r.Type is "Hőmérséklet" or "Terhelés"))
                sb.AppendLine($"  {r.Hardware} / {r.Sensor,-24} {r.Value:0.#} {r.Unit}");
        }

        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            MessageBox.Show("Mentve:\n" + dlg.FileName, "Kész",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _reader?.Dispose();
        }
        base.Dispose(disposing);
    }
}
