using FrooxEngine;
using VrmToResonitePackage.Expressions;
using static ExpressionTestFields;

internal static class ExpressionMenuGroupChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Run()
    {
        foreach (int count in new[] { 0, 1, 8, 9, 16, 65 })
        {
            var clips = Enumerable.Range(0, count).Select(i => "clip" + i).ToArray();
            var groups = ExpressionMenuGroups.Build(clips, new string[64]);
            Check(groups.SelectMany(g => g).SequenceEqual(clips), "unmapped catalog remains complete and ordered");
            Check(groups.All(g => g.Length is > 0 and <= 8), "group size boundary");
            Check(groups.Count == (count + 7) / 8, "no empty trailing group");
        }
        var pairs = new string[64];
        pairs[8] = pairs[1] = "shared";
        pairs[16] = "left";
        pairs[2] = "right";
        pairs[9] = "both";
        pairs[63] = "left";
        var partition = ExpressionMenuGroups.Build(new[] { "other", "right", "left", "both", "shared", "left" }, pairs);
        Check(partition.Select(g => string.Join(",", g)).SequenceEqual(new[] { "left,shared", "right", "both", "other" }),
            "shared poses join the left group in catalog order without duplicate right-hand entries");
        var overflowPairs = new string[64];
        for (int i = 1; i < 8; i++) overflowPairs[i * 8] = "left" + i;
        overflowPairs[0] = "shared0";
        overflowPairs[1] = overflowPairs[8] = "shared1";
        overflowPairs[2] = "right";
        var overflowClips = new[] { "shared0", "shared1" }
            .Concat(Enumerable.Range(2, 6).Select(i => "left" + i)).Append("right").ToArray();
        var fullLeft = ExpressionMenuGroups.Build(overflowClips, overflowPairs);
        Check(fullLeft.Count == 2 && fullLeft[0].SequenceEqual(overflowClips.Take(8)) &&
            fullLeft[1].SequenceEqual(new[] { "right" }), "shared and left poses fill one eight-entry group");
        // Nine left-column entries require an expanded gesture table.
        var ninePairs = new string[81];
        for (int i = 0; i < 9; i++) ninePairs[i * 9] = "left" + i;
        ninePairs[1] = "left1";
        var nineClips = Enumerable.Range(0, 9).Select(i => "left" + i).ToArray();
        var splitLeft = ExpressionMenuGroups.Build(nineClips, ninePairs, 9);
        Check(splitLeft.Select(g => g.Length).SequenceEqual(new[] { 8, 1 }) &&
            splitLeft.SelectMany(g => g).SequenceEqual(nineClips), "merged left category still splits at eight");
        for (int i = 0; i < 8; i++) pairs[i * 8] = pairs[i] = "same" + i;
        var identical = ExpressionMenuGroups.Build(Enumerable.Range(0, 8).Select(i => "same" + i), pairs);
        Check(identical.Count == 1 && identical[0].Length == 8, "identical left/right selections share one group");
        var expanded = new string[81];
        expanded[8 * 9] = "left8"; expanded[8] = "right8"; expanded[80] = "both8";
        var extended = ExpressionMenuGroups.Build(new[] { "both8", "right8", "left8" }, expanded, 9);
        Check(extended.Select(g => string.Join(",", g)).SequenceEqual(new[] { "left8", "right8", "both8" }),
            "expanded tables classify the last row/column using their own width");
    }

    public static ButtonDynamicImpulseTriggerWithReference<Slot>[] CheckMenu(Slot root)
    {
        var items = root.FindChild("Inputs").FindChild("ContextMenu");
        CheckHierarchy(items);
        var selection = items.FindChild("Select expression");
        Check(selection != null && items.FindChild("Imported menu") == null &&
            items.FindChild("Hand sign expressions") == null && items.FindChild("Other expressions") == null,
            "one direct selection entrance, without duplicate imported aliases");
        var groupsRoot = selection.GetComponent<ContextMenuSubmenu>().ItemsRoot.Target;
        Check(groupsRoot.FindChild("Back").GetComponent<ContextMenuSubmenu>().ItemsRoot.Target == items,
            "selection Back returns to Expressions");
        bool flat = root.FindChild("Clips").Children.Count <= 8;
        var groups = flat ? new[] { groupsRoot } : groupsRoot.Children.Where(s => s.Name != "Back").ToArray();
        if (flat)
            Check(groupsRoot.GetComponentsInChildren<ContextMenuSubmenu>().Count == 2,
                "small catalogs have only the selection submenu and Back, without subgroups");
        var table = root.FindChild("DV").FindChild("GestureTable")
            .GetComponentsInChildren<DynamicReferenceVariable<Slot>>();
        Slot Pair(int left, int right) => table.Single(v =>
            v.VariableName.Value == $"ExpressionSystem/GestureTable.L{left}R{right}").Reference.Target;
        int gestureCount = (int)Math.Sqrt(table.Count);
        var leftOnly = Enumerable.Range(0, gestureCount).Select(i => Pair(i, 0)).ToHashSet();
        var rightOnly = Enumerable.Range(0, gestureCount).Select(i => Pair(0, i)).ToHashSet();
        var mapped = table.Select(v => v.Reference.Target).ToHashSet();
        var targets = new HashSet<Slot>();
        var buttons = new List<ButtonDynamicImpulseTriggerWithReference<Slot>>();
        for (int i = 0; i < groups.Length; i++)
        {
            var entries = groups[i].GetComponent<ContextMenuSubmenu>().ItemsRoot.Target;
            if (!flat)
            {
                Check(groups[i].GetComponent<ContextMenuItemSource>().Label.Value == "Group " + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "consecutive Group labels");
                Check(entries.FindChild("Back").GetComponent<ContextMenuSubmenu>().ItemsRoot.Target == groupsRoot,
                    "group Back returns to group selection");
            }
            var groupButtons = entries.Children.SelectMany(s => s.GetComponents<ButtonDynamicImpulseTriggerWithReference<Slot>>()).ToArray();
            Check(groupButtons.Length <= 8 && (flat || groupButtons.Length > 0), "at most eight expressions per menu (Back is navigation)");
            var categories = new HashSet<int>();
            foreach (var button in groupButtons)
            {
                var target = button.Slot.GetComponent<BooleanReferenceDriver<Slot>>().FalseTarget.Target;
                Check(target != null && targets.Add(target), "each expression has exactly one reachable button");
                categories.Add(leftOnly.Contains(target) ? 0 : rightOnly.Contains(target) ? 1 : mapped.Contains(target) ? 2 : 3);
                buttons.Add(button);
            }
            if (!flat) Check(categories.Count == 1, "left including shared, right-only, combined and other categories never mix");
        }
        Check(targets.SetEquals(root.FindChild("Clips").Children), "direct or grouped buttons cover the entire selectable catalog");
        Check(items.GetComponentsInChildren<ButtonDynamicImpulseTriggerWithReference<Slot>>().Count == buttons.Count,
            "no duplicate expression buttons outside the numbered groups");
        Console.WriteLine($"PASS: direct selection has {buttons.Count} unique expressions, {(flat ? "without subgroups" : $"in {groups.Length} groups (max 8)")}");
        return buttons.ToArray();
    }

    public static void CheckHierarchy(Slot menu)
    {
        foreach (var submenu in menu.GetComponentsInChildren<ContextMenuSubmenu>())
        {
            var expected = submenu.Slot.Name == "Back" ? submenu.Slot.Parent.Parent : submenu.Slot;
            Check(submenu.ItemsRoot.Target == expected && !submenu.SearchWholeHierarchy.Value,
                "submenus collect their own direct children; Back collects the parent menu's children");
        }
    }
}
