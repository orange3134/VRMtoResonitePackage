using System.Text.Json;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;
using SkyFrost.Base;
using VrmToResonitePackage;
using VrmToResonitePackage.Expressions;
using Comment = FrooxEngine.Comment;

internal static class ExpressionLicenseChecks
{
    public static void Verify(Slot expressions)
    {
        var handGestures = expressions.FindChild("Inputs").FindChild("HandGestures");
        var notices = handGestures.FindChild("THIRD-PARTY-NOTICES");
        string attribution = notices?.GetComponent<Comment>()?.Text.Value;
        Check(handGestures.GetComponent<Comment>() == null &&
            attribution?.Contains("Avatar Expression Editor v1.12.1") == true &&
            !attribution.Contains("Copyright (c)") && !attribution.Contains("ResoPon"), "separate source attribution");
        using var stream = typeof(ExpressionSystemSetup).Assembly.GetManifestResourceStream("ResoPon.AvatarExpressionEditorLicense");
        using var source = JsonDocument.Parse(stream);
        var values = source.RootElement;
        Check(notices.Children.Count == 1, "one source tool license slot");
        var entry = notices.Children.Single();
        Check(entry.Name == values.GetProperty("toolName").GetString(), "exact source tool name, authors, markup and version");
        var license = entry.GetComponent<License>();
        var comment = entry.GetComponent<Comment>();
        Check(entry.GetComponents<Component>().Count == 2 && license != null && comment != null, "source License and Comment components only");
        Check(license.RequireCredit.Value == values.GetProperty("requireCredit").GetBoolean() &&
            license.CreditString.Value == values.GetProperty("creditString").GetString() &&
            license.CanExport.Value == values.GetProperty("canExport").GetBoolean(), "all source License values");
        Check(comment.Text.Value == values.GetProperty("commentText").GetString(), "exact source Comment text including line endings");
        foreach (var component in new Component[] { license, comment })
            Check(((IField<bool>)component.GetSyncMember(0)).Value &&
                ((IField<int>)component.GetSyncMember(1)).Value == 0 &&
                ((IField<bool>)component.GetSyncMember(2)).Value, "source persistence, update order and enabled state");
    }

    public static async Task Run(Slot parent, string artifacts)
    {
        foreach (bool menu in new[] { false, true })
        {
            var avatar = parent.AddSlot("License fixture");
            Slot clone = null, restored = null;
            try
            {
                var field = avatar.AttachComponent<ValueField<float>>().Value;
                var model = new ExpressionModel();
                var clip = new ExpressionClip { Id = "smile", Name = "Smile", Duration = 1 };
                var curve = new ExpressionCurve { Binding = new("Face", "Smile") };
                curve.Keys.Add(new(0, 1, 0, 0)); clip.Curves.Add(curve); model.Clips.Add(clip);
                var expressions = await ExpressionSystemSetup.BuildAsync(avatar, model, _ => field, menu: menu);
                for (int i = 0; i < 5; i++) await default(NextUpdate);
                Verify(expressions);
                clone = avatar.Duplicate(parent);
                Verify(clone.FindChild("Expressions"));
                var graph = avatar.SaveObject(DependencyHandling.CollectAssets);
                var record = RecordHelper.CreateForObject<SkyFrost.Base.Record>(avatar.Name, avatar.World.LocalUser.MachineID, null);
                string package = Path.Combine(artifacts, $"License-{menu}.resonitepackage");
                var engine = avatar.Engine;
                await default(ToBackground);
                using (var output = File.Create(package))
                    await PackageCreator.BuildPackage(engine, record, graph, output, includeVariants: false);
                await default(ToWorld);
                Check(PackageInspector.Inspect(package, verbose: false) == 0, "package inspection");
                restored = parent.AddSlot("Restored license fixture");
                await PackageImporter.ImportPackage(package, restored);
                await default(ToWorld);
                Verify(restored.FindChild("Expressions"));
                Console.WriteLine($"LICENSE: menu={menu}: generation, clone, package inspection and reload preserve source components");
            }
            finally { restored?.Destroy(); clone?.Destroy(); avatar.Destroy(); }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("License: " + message);
    }
}
