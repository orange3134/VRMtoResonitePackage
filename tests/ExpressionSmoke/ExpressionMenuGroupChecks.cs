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
        Check(partition.Select(g => string.Join(",", g)).SequenceEqual(new[] { "shared", "left", "right", "both", "other" }),
            "partially shared hands, combined poses and unmapped clips have distinct groups without aliases");
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
        var groups = groupsRoot.Children.Where(s => s.Name != "Back").ToArray();
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
            Check(groups[i].GetComponent<ContextMenuItemSource>().Label.Value == "Group " + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "consecutive Group labels");
            var entries = groups[i].GetComponent<ContextMenuSubmenu>().ItemsRoot.Target;
            Check(entries.FindChild("Back").GetComponent<ContextMenuSubmenu>().ItemsRoot.Target == groupsRoot,
                "group Back returns to group selection");
            var groupButtons = entries.GetComponentsInChildren<ButtonDynamicImpulseTriggerWithReference<Slot>>();
            Check(groupButtons.Count is > 0 and <= 8, "at most eight expressions per menu (Back is navigation)");
            var categories = new HashSet<(bool, bool, bool)>();
            foreach (var button in groupButtons)
            {
                var target = button.Slot.GetComponent<BooleanReferenceDriver<Slot>>().FalseTarget.Target;
                Check(target != null && targets.Add(target), "each expression has exactly one reachable button");
                categories.Add((leftOnly.Contains(target), rightOnly.Contains(target), mapped.Contains(target)));
                buttons.Add(button);
            }
            Check(categories.Count == 1, "shared, left, right, combined and other expressions never mix within a group");
        }
        Check(targets.SetEquals(root.FindChild("Clips").Children), "numbered groups cover the entire selectable catalog");
        Check(items.GetComponentsInChildren<ButtonDynamicImpulseTriggerWithReference<Slot>>().Count == buttons.Count,
            "no duplicate expression buttons outside the numbered groups");
        Console.WriteLine($"PASS: direct selection has {buttons.Count} unique expressions in {groups.Length} groups (max 8)");
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
