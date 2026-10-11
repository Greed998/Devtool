using System.Runtime.Versioning;
using System.Text;

namespace DevToolbox;

[SupportedOSPlatform("windows")]
public sealed class RaidPage : UserControl
{
    private readonly ComboBox _level = new();
    private readonly NumericUpDown _disks = new();
    private readonly NumericUpDown _size = new();
    private readonly ComboBox _unit = new();
    private readonly NumericUpDown _spares = new();
    private readonly NumericUpDown _groups = new();
    private readonly Label _groupsLabel = new();
    private readonly NumericUpDown _rebuild = new();
    private readonly ComboBox _ure = new();

    private readonly Label _usable = new();
    private readonly Label _usableSub = new();
    private readonly ListView _facts = new();
    private readonly ListView _compare = new();
    private readonly RichTextBox _notes = new();

    private bool _ready;

    public RaidPage()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.White;

        // ================= bal oldali beállítások
        var side = new Panel { Dock = DockStyle.Left, Width = 330, Padding = new Padding(16, 14, 16, 12) };
        var f = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };

        f.Controls.Add(Head("Tömb felépítése"));

        f.Controls.Add(Lbl("RAID szint"));
        _level.DropDownStyle = ComboBoxStyle.DropDownList;
        _level.Width = 285;
        foreach (RaidLevel l in Enum.GetValues<RaidLevel>())
            _level.Items.Add(RaidCalculator.LevelName(l));
        _level.SelectedIndex = (int)RaidLevel.Raid5;
        _level.SelectedIndexChanged += (_, _) => { UpdateGroupVisibility(); Recalc(); };
        f.Controls.Add(_level);

        f.Controls.Add(Lbl("Lemezek száma"));
        Num(_disks, 1, 256, 4);
        f.Controls.Add(_disks);

        f.Controls.Add(Lbl("Egy lemez mérete"));
        var sizeRow = new FlowLayoutPanel
        { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0) };
        Num(_size, 1, 1000, 4);
        _size.DecimalPlaces = 2;
        _size.Increment = 1;
        _size.Width = 110;
        _unit.DropDownStyle = ComboBoxStyle.DropDownList;
        _unit.Width = 80;
        _unit.Margin = new Padding(8, 3, 3, 3);
        foreach (var u in RaidCalculator.Units) _unit.Items.Add(u.Name);
        _unit.SelectedIndex = 0;
        _unit.SelectedIndexChanged += (_, _) => Recalc();
        sizeRow.Controls.Add(_size);
        sizeRow.Controls.Add(_unit);
        f.Controls.Add(sizeRow);

        f.Controls.Add(Lbl("Hot spare lemezek"));
        Num(_spares, 0, 32, 0);
        f.Controls.Add(_spares);

        _groupsLabel.Text = "Csoportok száma (RAID 50/60)";
        _groupsLabel.AutoSize = true;
        _groupsLabel.ForeColor = Color.FromArgb(70, 78, 92);
        _groupsLabel.Margin = new Padding(0, 10, 0, 3);
        f.Controls.Add(_groupsLabel);
        Num(_groups, 2, 32, 2);
        f.Controls.Add(_groups);

        f.Controls.Add(Head("Újraépítés"));

        f.Controls.Add(Lbl("Újraépítési sebesség (MB/s)"));
        Num(_rebuild, 10, 2000, 100);
        _rebuild.Increment = 10;
        f.Controls.Add(_rebuild);

        f.Controls.Add(Lbl("Lemezosztály (olvasásihiba-arány)"));
        _ure.DropDownStyle = ComboBoxStyle.DropDownList;
        _ure.Width = 285;
        foreach (var u in RaidCalculator.UreClasses) _ure.Items.Add(u.Name);
        _ure.SelectedIndex = 0;
        _ure.SelectedIndexChanged += (_, _) => Recalc();
        f.Controls.Add(_ure);

        f.Controls.Add(new Label
        {
            Text = "Az olvasásihiba-arány (URE) a lemez adatlapján szerepel. Ez mondja meg, " +
                   "mekkora eséllyel akad el az újraépítés egy javíthatatlan blokkon.",
            AutoSize = true,
            MaximumSize = new Size(285, 0),
            ForeColor = Color.FromArgb(120, 127, 140),
            Font = new Font("Segoe UI", 8.25F),
            Margin = new Padding(0, 8, 0, 4)
        });

        var btnCopy = new Button
        {
            Text = "Eredmény másolása",
            Width = 285,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            Margin = new Padding(0, 14, 0, 4)
        };
        btnCopy.FlatAppearance.BorderColor = Color.FromArgb(203, 207, 214);
        btnCopy.Click += (_, _) => CopyResult();
        f.Controls.Add(btnCopy);

        side.Controls.Add(f);

        // ================= jobb oldali eredmény
        var resultHeader = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Color.FromArgb(246, 247, 249) };
        _usable.Text = "–";
        _usable.Font = new Font("Segoe UI Semibold", 22F);
        _usable.ForeColor = Color.FromArgb(0, 90, 165);
        _usable.AutoSize = true;
        _usable.Location = new Point(16, 14);
        _usableSub.Text = "";
        _usableSub.AutoSize = true;
        _usableSub.ForeColor = Color.FromArgb(88, 96, 110);
        _usableSub.Location = new Point(18, 56);
        resultHeader.Controls.Add(_usable);
        resultHeader.Controls.Add(_usableSub);

        SetupList(_facts, ("Jellemző", 220), ("Érték", 340));
        _facts.ShowGroups = false;

        SetupList(_compare, ("RAID szint", 190), ("Hasznos kapacitás", 150),
            ("Hatékonyság", 95), ("Elviselt hiba", 220), ("Írás", 55));
        _compare.ShowGroups = false;

        _notes.Dock = DockStyle.Fill;
        _notes.ReadOnly = true;
        _notes.BorderStyle = BorderStyle.None;
        _notes.BackColor = Color.White;
        _notes.Font = new Font("Segoe UI", 9F);

        var factsPanel = new Panel { Dock = DockStyle.Fill };
        factsPanel.Controls.Add(_facts);
        factsPanel.Controls.Add(SubHeader("Részletek"));

        var comparePanel = new Panel { Dock = DockStyle.Fill };
        comparePanel.Controls.Add(_compare);
        comparePanel.Controls.Add(SubHeader("Ugyanezekkel a lemezekkel – összehasonlítás"));

        var notesPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 0, 8, 6) };
        notesPanel.Controls.Add(_notes);
        var notesHost = new Panel { Dock = DockStyle.Fill };
        notesHost.Controls.Add(notesPanel);
        notesHost.Controls.Add(SubHeader("Megjegyzések és figyelmeztetések"));

        var lower = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            BorderStyle = BorderStyle.None
        };
        lower.ApplyWhenSized(200);
        lower.Panel1.Controls.Add(comparePanel);
        lower.Panel2.Controls.Add(notesHost);

        var upper = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            BorderStyle = BorderStyle.None
        };
        upper.ApplyWhenSized(210);
        upper.Panel1.Controls.Add(factsPanel);
        upper.Panel2.Controls.Add(lower);

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(upper);
        right.Controls.Add(resultHeader);

        Controls.Add(right);
        Controls.Add(side);

        _ready = true;
        UpdateGroupVisibility();
        Recalc();
    }

    // ---------------------------------------------------------------- segédek

    private void Num(NumericUpDown n, int min, int max, int val)
    {
        n.Minimum = min; n.Maximum = max; n.Value = val;
        n.Width = 285;
        n.Margin = new Padding(0, 0, 0, 2);
        n.ValueChanged += (_, _) => Recalc();
    }

    private static Label Head(string t) => new()
    {
        Text = t,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 10F),
        ForeColor = Color.FromArgb(28, 32, 40),
        Margin = new Padding(0, 14, 0, 8)
    };

    private static Label Lbl(string t) => new()
    {
        Text = t,
        AutoSize = true,
        ForeColor = Color.FromArgb(70, 78, 92),
        Margin = new Padding(0, 10, 0, 3)
    };

    private static Label SubHeader(string t) => new()
    {
        Text = "  " + t,
        Dock = DockStyle.Top,
        Height = 28,
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.FromArgb(246, 247, 249),
        ForeColor = Color.FromArgb(60, 68, 82),
        Font = new Font("Segoe UI Semibold", 9F)
    };

    private static void SetupList(ListView lv, params (string H, int W)[] cols)
    {
        lv.View = View.Details;
        lv.Dock = DockStyle.Fill;
        lv.FullRowSelect = true;
        lv.BorderStyle = BorderStyle.None;
        lv.MultiSelect = false;
        foreach (var c in cols) lv.Columns.Add(c.H, c.W);
    }

    private void UpdateGroupVisibility()
    {
        var isNested = (RaidLevel)_level.SelectedIndex is RaidLevel.Raid50 or RaidLevel.Raid60;
        _groups.Enabled = isNested;
        _groupsLabel.ForeColor = isNested ? Color.FromArgb(70, 78, 92) : Color.FromArgb(180, 185, 194);
    }

    private RaidInput CurrentInput() => new()
    {
        Level = (RaidLevel)Math.Max(0, _level.SelectedIndex),
        DiskCount = (int)_disks.Value,
        DiskSize = (double)_size.Value,
        Unit = _unit.SelectedItem?.ToString() ?? "TB",
        HotSpares = (int)_spares.Value,
        Groups = (int)_groups.Value,
        RebuildMBps = (int)_rebuild.Value,
        UreRate = RaidCalculator.UreClasses[Math.Max(0, _ure.SelectedIndex)].Rate
    };

    // ---------------------------------------------------------------- számolás

    private void Recalc()
    {
        if (!_ready) return;

        var inp = CurrentInput();
        var r = RaidCalculator.Compute(inp);

        _facts.BeginUpdate();
        _facts.Items.Clear();

        if (!r.Valid)
        {
            _usable.Text = "—";
            _usable.ForeColor = Color.FromArgb(190, 40, 40);
            _usableSub.Text = r.Error;
            _facts.Items.Add(new ListViewItem(new[] { "Hiba", r.Error })
            { ForeColor = Color.FromArgb(190, 40, 40) });
            _facts.EndUpdate();
            _notes.Clear();
            FillCompare(inp);
            return;
        }

        _usable.ForeColor = Color.FromArgb(0, 90, 165);
        _usable.Text = RaidCalculator.BothUnits(r.UsableBytes);
        _usableSub.Text =
            $"hasznos kapacitás · {RaidCalculator.LevelName(inp.Level)} · " +
            $"{inp.DiskCount} × {inp.DiskSize:0.##} {inp.Unit}" +
            (inp.HotSpares > 0 ? $" ({inp.HotSpares} hot spare)" : "");

        void Row(string k, string v, Color? c = null)
        {
            var lvi = new ListViewItem(new[] { k, v });
            if (c.HasValue) lvi.ForeColor = c.Value;
            _facts.Items.Add(lvi);
        }

        Row("Nyers kapacitás (összes lemez)", RaidCalculator.BothUnits(r.RawBytes));
        Row("Hasznos kapacitás", RaidCalculator.BothUnits(r.UsableBytes));
        Row("Redundanciára megy", RaidCalculator.BothUnits(r.RedundancyBytes));
        if (r.SpareBytes > 0) Row("Hot spare tartalék", RaidCalculator.BothUnits(r.SpareBytes));
        Row("Kihasználtság", $"{r.EfficiencyPercent:0.#}%");
        Row("Tömbben dolgozó lemezek", $"{r.ArrayDisks} db (minimum {r.MinDisks})");

        Row("Elviselt lemezhiba", r.ToleranceText,
            r.FaultTolerance == 0 ? Color.FromArgb(190, 40, 40) : Color.FromArgb(20, 120, 60));

        Row("Írási büntetés", $"{r.WritePenalty}× (minden logikai íráshoz ennyi fizikai I/O)");
        Row("Olvasási sávszélesség", $"kb. {r.ReadFactor:0}× egy lemezhez képest");
        Row("Újraépítés becsült ideje", RaidCalculator.Duration(r.RebuildTime));

        if (r.UreRiskPercent >= 0)
        {
            var c = r.UreRiskPercent >= 30 ? Color.FromArgb(190, 40, 40)
                  : r.UreRiskPercent >= 10 ? Color.FromArgb(185, 100, 0)
                  : Color.FromArgb(20, 120, 60);
            Row("Olvasási hiba esélye újraépítéskor", $"{r.UreRiskPercent:0.#}%", c);
        }

        _facts.EndUpdate();

        FillCompare(inp);
        FillNotes(r);
    }

    private void FillCompare(RaidInput inp)
    {
        _compare.BeginUpdate();
        _compare.Items.Clear();

        foreach (var c in RaidCalculator.CompareAll(inp))
        {
            ListViewItem lvi;
            if (c.Valid)
            {
                lvi = new ListViewItem(new[]
                {
                    c.Name,
                    RaidCalculator.BothUnits(c.UsableBytes),
                    $"{c.EfficiencyPercent:0.#}%",
                    c.ToleranceText.Length > 46 ? c.ToleranceText[..45] + "…" : c.ToleranceText,
                    c.WritePenalty + "×"
                });

                if (c.Level == inp.Level)
                {
                    lvi.Font = new Font(_compare.Font, FontStyle.Bold);
                    lvi.BackColor = Color.FromArgb(233, 242, 253);
                }
            }
            else
            {
                lvi = new ListViewItem(new[] { c.Name, "—", "", c.Error, "" })
                { ForeColor = Color.FromArgb(150, 156, 168) };
            }

            _compare.Items.Add(lvi);
        }

        _compare.EndUpdate();
    }

    private void FillNotes(RaidResult r)
    {
        _notes.Clear();

        void Line(string t, Color c, bool bold = false)
        {
            _notes.SelectionStart = _notes.TextLength;
            _notes.SelectionFont = new Font("Segoe UI", 9F, bold ? FontStyle.Bold : FontStyle.Regular);
            _notes.SelectionColor = c;
            _notes.AppendText(t + Environment.NewLine + Environment.NewLine);
        }

        foreach (var w in r.Warnings) Line("⚠  " + w, Color.FromArgb(185, 60, 20), true);

        if (r.UreNote.Length > 0)
            Line(r.UreNote, Color.FromArgb(60, 66, 78));

        foreach (var n in r.Notes) Line("•  " + n, Color.FromArgb(70, 78, 92));

        _notes.SelectionStart = 0;
        _notes.ScrollToCaret();
    }

    private void CopyResult()
    {
        var inp = CurrentInput();
        var r = RaidCalculator.Compute(inp);

        var sb = new StringBuilder();
        sb.AppendLine("RAID kalkuláció – DevToolbox");
        sb.AppendLine($"{RaidCalculator.LevelName(inp.Level)}   " +
                      $"{inp.DiskCount} × {inp.DiskSize:0.##} {inp.Unit}" +
                      (inp.HotSpares > 0 ? $"   + {inp.HotSpares} hot spare" : ""));
        sb.AppendLine(new string('-', 60));

        if (!r.Valid) { sb.AppendLine("Hiba: " + r.Error); }
        else
        {
            foreach (ListViewItem i in _facts.Items)
                sb.AppendLine($"  {i.SubItems[0].Text,-34} {i.SubItems[1].Text}");
            sb.AppendLine();
            foreach (var w in r.Warnings) sb.AppendLine("  FIGYELEM: " + w);
            foreach (var n in r.Notes) sb.AppendLine("  - " + n);
        }

        try { Clipboard.SetText(sb.ToString()); } catch { }
    }
}
