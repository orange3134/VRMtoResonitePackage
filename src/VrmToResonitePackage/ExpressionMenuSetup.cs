using Elements.Core;
using FrooxEngine;
using VrmToResonitePackage.Vrm;
using ColorProfile = Renderite.Shared.ColorProfile;

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
///     ValueField&lt;int&gt;        (currently selected expression index; Reset = 0)
///   Expressions/Items slot
///     &lt;expression&gt; slot x N
///       ContextMenuItemSource (label + icon; color driven white/pink by selection)
///       StaticTexture2D + SpriteProvider  (the item's icon)
///       ButtonValueSet&lt;float&gt; x N  (one per unique morph field, pushes the weight
///                                    for this expression into the matching SmoothValue)
///       ButtonValueSet&lt;int&gt;        (writes this item's index into the ValueField)
///       ValueEqualityDriver&lt;int&gt;   (drives a BooleanValueDriver when selected)
///       BooleanValueDriver&lt;colorX&gt; (drives the item Color: white / pink)
///     Reset slot (all weights -> 0, index 0)
/// </summary>
internal static class ExpressionMenuSetup
{
    /// <summary>Preset name + icon URL, in the fixed order we present them.</summary>
    private static readonly (string preset, string iconUrl)[] EmotionPresets =
    {
        ("happy", "resdb:///a3b67e86407b719fc5023f03c8fc5ab6486ebe8834b9d12dbacaf7347578aab5.webp"),
        ("angry", "resdb:///11a4635a39119c4204c848f1e42ddafa009ad70eb04b666d246e2d73307c6126.webp"),
        ("sad", "resdb:///fc79689c99ab5c5828c943492a3ec3158e088b64dcdbe6f804934316f8112a22.webp"),
        ("relaxed", "resdb:///b3f3319d6e206116101500d9307cbb6699119f93ecee72c05a393654a0227a20.webp"),
        ("surprised", "resdb:///e9d013f28bdfeb29ad7f5f49ae39d670293f37f89c48735c808fc9b30d1a0a72.webp"),
        ("neutral", NeutralIconUrl),
    };

    /// <summary>Also used for the root "Expression" submenu button.</summary>
    private const string NeutralIconUrl =
        "resdb:///5e825a7a76c784575d4a17d87eb406793ad5c0bfb26d3e9d48c6e07d284d0038.webp";

    private const string ResetIconUrl =
        "resdb:///391de2844bdd33eb12ccdeb7162edb9b5033f01946672cac59dc541440ba7f0d.webp";

    // メニュー項目の色: 通常=白、使用中=ピンク。いずれも Linear プロファイルで生成
    // （colorX の既定は sRGB なので明示指定）。
    private static readonly colorX NormalColor = new(1f, 1f, 1f, 1f, ColorProfile.Linear);
    private static readonly colorX SelectedColor = new(1f, 0.53f, 0.69f, 1f, ColorProfile.Linear);

    public static void Setup(Slot root, VrmModel vrm)
    {
        var resolver = new BlendshapeResolver(root, vrm);

        // Resolve every emotion preset into a set of (field, weight) targets, dropping
        // anything that can't be resolved or is already driven by another system
        // (visemes, blink, face tracking).
        var emotions = new List<ResolvedEmotion>();
        foreach ((string preset, string iconUrl) in EmotionPresets)
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
            emotions.Add(new ResolvedEmotion(displayName, iconUrl, targets));
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
        SetupIcon(expressionsSlot, rootItemSource, NeutralIconUrl);

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

        // Tracks which expression is currently applied. Reset = 0, expressions = 1..N.
        // Each item drives the matching context-menu Color (white / pink) from this.
        ValueField<int> selectedIndex = expressionsSlot.AttachComponent<ValueField<int>>();
        selectedIndex.Value.Value = 0; // 初期状態は Reset 相当。

        // One context-menu item per emotion.
        int index = 1;
        foreach (ResolvedEmotion emotion in emotions)
        {
            Slot itemSlot = itemsSlot.AddSlot(emotion.DisplayName);
            ContextMenuItemSource itemSource = itemSlot.AttachComponent<ContextMenuItemSource>();
            itemSource.Label.Value = emotion.DisplayName;
            itemSource.Color.Value = NormalColor; // ドライバーが上書きするが既定として白を入れておく。
            itemSource.CloseMenuOnPress.Value = false; // メニューを開いたまま表情を切り替えられるように。

            SetupIcon(itemSlot, itemSource, emotion.IconUrl);

            foreach (IField<float> field in fields)
            {
                ButtonValueSet<float> setter = itemSlot.AttachComponent<ButtonValueSet<float>>();
                setter.TargetValue.Target = smoothByField[field].TargetValue;
                setter.SetValue.Value = emotion.Targets.TryGetValue(field, out float weight) ? weight : 0f;
            }

            SetupSelectionColor(itemSlot, itemSource, selectedIndex, index);
            index++;
        }

        // Reset item: drives every morph back to zero (index 0).
        Slot resetSlot = itemsSlot.AddSlot("Reset");
        ContextMenuItemSource resetSource = resetSlot.AttachComponent<ContextMenuItemSource>();
        resetSource.Label.Value = "Reset";
        resetSource.Color.Value = NormalColor;
        resetSource.CloseMenuOnPress.Value = false; // メニューを開いたまま表情を切り替えられるように。
        SetupIcon(resetSlot, resetSource, ResetIconUrl);
        foreach (IField<float> field in fields)
        {
            ButtonValueSet<float> setter = resetSlot.AttachComponent<ButtonValueSet<float>>();
            setter.TargetValue.Target = smoothByField[field].TargetValue;
            setter.SetValue.Value = 0f;
        }
        SetupSelectionColor(resetSlot, resetSource, selectedIndex, 0);

        UniLog.Log($"表情メニューを生成しました（表情 {emotions.Count} 個、モーフ {fields.Count} 個をドライブ）。");
    }

    /// <summary>Attaches an icon (StaticTexture2D + SpriteProvider) and points the item's Sprite at it.</summary>
    private static void SetupIcon(Slot itemSlot, ContextMenuItemSource itemSource, string iconUrl)
    {
        StaticTexture2D texture = itemSlot.AttachComponent<StaticTexture2D>();
        texture.URL.Value = new Uri(iconUrl);

        SpriteProvider sprite = itemSlot.AttachComponent<SpriteProvider>();
        sprite.Texture.Target = texture;
        // Rect/Scale/FixedSize は OnAwake の既定（0,0,1,1 / 1 / 8）のままで全面表示。

        itemSource.Sprite.Target = sprite;
    }

    /// <summary>
    /// Wires the item's Color to flip to <see cref="SelectedColor"/> while
    /// <paramref name="selectedIndex"/> equals <paramref name="ownIndex"/>, otherwise
    /// <see cref="NormalColor"/>. Also adds the ButtonValueSet that records the selection.
    /// </summary>
    private static void SetupSelectionColor(
        Slot itemSlot, ContextMenuItemSource itemSource, ValueField<int> selectedIndex, int ownIndex)
    {
        // 押下でこの項目を「選択中」にする。
        ButtonValueSet<int> selectSetter = itemSlot.AttachComponent<ButtonValueSet<int>>();
        selectSetter.TargetValue.Target = selectedIndex.Value;
        selectSetter.SetValue.Value = ownIndex;

        // 色: 選択中=ピンク、非選択=白。
        BooleanValueDriver<colorX> colorDriver = itemSlot.AttachComponent<BooleanValueDriver<colorX>>();
        colorDriver.FalseValue.Value = NormalColor;
        colorDriver.TrueValue.Value = SelectedColor;
        colorDriver.TargetField.ForceLink(itemSource.Color);

        // selectedIndex == ownIndex のとき colorDriver.State を true にする。
        ValueEqualityDriver<int> equality = itemSlot.AttachComponent<ValueEqualityDriver<int>>();
        equality.TargetValue.Target = selectedIndex.Value;
        equality.Reference.Value = ownIndex;
        equality.Target.ForceLink(colorDriver.State);
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
        public ResolvedEmotion(string displayName, string iconUrl, Dictionary<IField<float>, float> targets)
        {
            DisplayName = displayName;
            IconUrl = iconUrl;
            Targets = targets;
        }

        public string DisplayName { get; }
        public string IconUrl { get; }
        public Dictionary<IField<float>, float> Targets { get; }
    }
}
