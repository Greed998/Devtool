namespace DevToolbox;

internal static class SplitHelper
{
    /// <summary>
    /// A SplitterDistance-t csak akkor allitja be, amikor a vezerlonek mar tenyleges merete van,
    /// es a hataron belulre vagja. Igy nem dob kivetelt indulaskor.
    /// </summary>
    public static void ApplyWhenSized(this SplitContainer sc, int distance)
    {
        void Handler(object? sender, EventArgs e)
        {
            var span = sc.Orientation == Orientation.Vertical ? sc.Width : sc.Height;
            var max = span - sc.SplitterWidth - sc.Panel2MinSize;
            if (max <= sc.Panel1MinSize) return;   // meg tul kicsi, varunk

            try { sc.SplitterDistance = Math.Clamp(distance, sc.Panel1MinSize, max); }
            catch { /* ha megsem fer bele, marad az alapertelmezes */ }

            sc.SizeChanged -= Handler;             // egyszer fut le
        }

        sc.SizeChanged += Handler;
    }

    /// <summary>
    /// A ListView csoportjait osszecsukhatova teszi. Ehhez a CollapsedState-et
    /// explicit ertekre kell allitani - Default eseten a Windows nem rajzol nyilat.
    /// </summary>
    public static void MakeGroupsCollapsible(this ListView lv)
    {
        try
        {
            foreach (ListViewGroup g in lv.Groups)
                if (g.CollapsedState == ListViewGroupCollapsedState.Default)
                    g.CollapsedState = ListViewGroupCollapsedState.Expanded;
        }
        catch { /* regi ComCtl32 eseten nem tamogatott */ }
    }

    /// <summary>Csoport letrehozasa a megadott kezdo allapottal.</summary>
    public static ListViewGroup NewGroup(string header, bool collapsed = false) =>
        new(header)
        {
            HeaderAlignment = HorizontalAlignment.Left,
            CollapsedState = collapsed
                ? ListViewGroupCollapsedState.Collapsed
                : ListViewGroupCollapsedState.Expanded
        };
}
