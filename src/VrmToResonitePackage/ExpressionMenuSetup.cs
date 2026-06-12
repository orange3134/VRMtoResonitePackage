using Elements.Core;
using FrooxEngine;
using VrmToResonitePackage.Vrm;

namespace VrmToResonitePackage;

/// <summary>
/// Builds a wearer-facing context menu that lets the avatar owner apply the VRM's
/// preset emotions (happy / angry / sad / relaxed / surprised, plus neutral) from
/// inside Resonite. The whole thing is wired out of stock components only — no
/// ProtoFlux — so it survives package import untouched.
///
/// Structure per avatar:
///   Expressions slot
///     RootContextMenuItem -> ContextMenuItemSource ("Expression")
///     ContextMenuSubmenu  -> Items slot
///     SmoothValue&lt;float&gt; x N  (one per unique morph field, drives that field)
///   Expressions/Items slot
///     &lt;expression&gt; slot x N
///       ContextMenuItemSource (label + color, closes menu on press)
///       ButtonValueSet&lt;float&gt; x N  (one per unique morph field, pushes the weight
///                                    for this expression into the matching SmoothValue)
///     Reset slot (all weights -> 0)
/// </summary>
internal static class ExpressionMenuSetup
{
    /// <summary>Preset name + display color, in the fixed order we present them.</summary>
    private static readonly (string preset, colorX color)[] EmotionPresets =
    {
        ("happy", new colorX(0.95f, 0.80f, 0.20f)),     // 黄
        ("angry", new colorX(0.85f, 0.25f, 0.20f)),     // 赤
        ("sad", new colorX(0.30f, 0.45f, 0.85f)),       // 青
        ("relaxed", new colorX(0.35f, 0.75f, 0.40f)),   // 緑
        ("surprised", new colorX(0.30f, 0.75f, 0.80f)), // シアン
        ("neutral", new colorX(0.85f, 0.85f, 0.85f)),   // 白
    };

    private static readonly colorX ResetColor = new(0.55f, 0.55f, 0.55f); // グレー

    public static void Setup(Slot root, VrmModel vrm)
    {
        var resolver = new BlendshapeResolver(root, vrm);

        // Resolve every emotion preset into a set of (field, weight) targets, dropping
        // anything that can't be resolved or is already driven by another system
        // (visemes, blink, face tracking).
        var emotions = new List<ResolvedEmotion>();
        foreach ((string preset, colorX color) in EmotionPresets)
        {
            VrmExpression expression = vrm.Expressions.FirstOrDefault(
                e => string.Equals(e.Preset, preset, StringComparison.OrdinalIgnoreCase));
            if (expression == null)
            {
                continue;
            }

            var targets = new Dictionary<IField<float>, float>();
            foreach (VrmExpressionBind bind in expression.Binds)
            {
                IField<float> field = resolver.Resolve(bind);
                if (field == null)
                {
                    // Resolver already logged the miss.
                    continue;
                }
                if (field.IsDriven || field.IsLinked)
                {
                    UniLog.Warning(
                        $"表情 '{preset}' のブレンドシェイプは既に別のドライバー（ビセーム/瞬き/フェイストラッキング等）に" +
                        "使われているためスキップします。");
                    continue;
                }
                // Same expression may bind the same field more than once; keep the strongest.
                if (!targets.TryGetValue(field, out float existing) || bind.Weight > existing)
                {
                    targets[field] = bind.Weight;
                }
            }

            if (targets.Count == 0)
            {
                continue;
            }

            string displayName = !string.IsNullOrWhiteSpace(expression.Name)
                ? expression.Name
                : Capitalize(preset);
            emotions.Add(new ResolvedEmotion(displayName, color, targets));
        }

        // Warn about expression kinds we intentionally don't surface in the menu.
        foreach (VrmExpression expression in vrm.Expressions)
        {
            if (IsMenuPreset(expression.Preset) || IsHandledElsewhere(expression.Preset))
            {
                continue;
            }
            if (expression.Binds.Count > 0)
            {
                UniLog.Log($"カスタム表情 '{expression.Preset}' はコンテキストメニュー対象外のためスキップしました。");
            }
        }

        if (emotions.Count == 0)
        {
            UniLog.Log("採用できるプリセット表情がないため、表情メニューは生成しません。");
            return;
        }

        // Union of every field any adopted emotion touches, in stable order.
        var fields = new List<IField<float>>();
        foreach (ResolvedEmotion emotion in emotions)
        {
            foreach (IField<float> field in emotion.Targets.Keys)
            {
                if (!fields.Contains(field))
                {
                    fields.Add(field);
                }
            }
        }

        Slot expressionsSlot = root.AddSlot("Expressions");

        // The submenu entry point that gets registered into the wearer's context menu.
        ContextMenuItemSource rootItemSource = expressionsSlot.AttachComponent<ContextMenuItemSource>();
        rootItemSource.Label.Value = "Expression";
        rootItemSource.CloseMenuOnPress.Value = false; // just opens the submenu

        RootContextMenuItem rootMenuItem = expressionsSlot.AttachComponent<RootContextMenuItem>();
        rootMenuItem.Item.Target = rootItemSource;

        Slot itemsSlot = expressionsSlot.AddSlot("Items");
        ContextMenuSubmenu submenu = expressionsSlot.AttachComponent<ContextMenuSubmenu>();
        submenu.ItemsRoot.Target = itemsSlot;

        // One SmoothValue per unique morph field, driving that field. Pressing an
        // emotion item only sets each SmoothValue.TargetValue, so the morph eases in.
        var smoothByField = new Dictionary<IField<float>, SmoothValue<float>>();
        foreach (IField<float> field in fields)
        {
            SmoothValue<float> smooth = expressionsSlot.AttachComponent<SmoothValue<float>>();
            smooth.TargetValue.Value = 0f; // Speed left at its default (10).
            smooth.Value.ForceLink(field);
            smoothByField[field] = smooth;
        }

        // One context-menu item per emotion.
        foreach (ResolvedEmotion emotion in emotions)
        {
            Slot itemSlot = itemsSlot.AddSlot(emotion.DisplayName);
            ContextMenuItemSource itemSource = itemSlot.AttachComponent<ContextMenuItemSource>();
            itemSource.Label.Value = emotion.DisplayName;
            itemSource.Color.Value = emotion.Color;
            itemSource.CloseMenuOnPress.Value = true;

            foreach (IField<float> field in fields)
            {
                ButtonValueSet<float> setter = itemSlot.AttachComponent<ButtonValueSet<float>>();
                setter.TargetValue.Target = smoothByField[field].TargetValue;
                setter.SetValue.Value = emotion.Targets.TryGetValue(field, out float weight) ? weight : 0f;
            }
        }

        // Reset item: drives every morph back to zero.
        Slot resetSlot = itemsSlot.AddSlot("Reset");
        ContextMenuItemSource resetSource = resetSlot.AttachComponent<ContextMenuItemSource>();
        resetSource.Label.Value = "Reset";
        resetSource.Color.Value = ResetColor;
        resetSource.CloseMenuOnPress.Value = true;
        foreach (IField<float> field in fields)
        {
            ButtonValueSet<float> setter = resetSlot.AttachComponent<ButtonValueSet<float>>();
            setter.TargetValue.Target = smoothByField[field].TargetValue;
            setter.SetValue.Value = 0f;
        }

        UniLog.Log($"表情メニューを生成しました（表情 {emotions.Count} 個、モーフ {fields.Count} 個をドライブ）。");
    }

    private static bool IsMenuPreset(string preset)
    {
        return EmotionPresets.Any(p => string.Equals(p.preset, preset, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Presets consumed by viseme / blink / look drivers, not the menu.</summary>
    private static bool IsHandledElsewhere(string preset)
    {
        switch (preset?.ToLowerInvariant())
        {
            case "aa":
            case "ih":
            case "ou":
            case "ee":
            case "oh":
            case "blink":
            case "blinkleft":
            case "blinkright":
            case "lookup":
            case "lookdown":
            case "lookleft":
            case "lookright":
                return true;
            default:
                return false;
        }
    }

    private static string Capitalize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>An emotion that resolved to at least one writable morph field.</summary>
    private sealed class ResolvedEmotion
    {
        public ResolvedEmotion(string displayName, colorX color, Dictionary<IField<float>, float> targets)
        {
            DisplayName = displayName;
            Color = color;
            Targets = targets;
        }

        public string DisplayName { get; }
        public colorX Color { get; }
        public Dictionary<IField<float>, float> Targets { get; }
    }
}
