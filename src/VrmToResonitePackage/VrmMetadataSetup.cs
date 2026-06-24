using FrooxEngine;
using VrmToResonitePackage.Vrm;

namespace VrmToResonitePackage;

internal static class VrmMetadataSetup
{
    private const string VrmMetadataNamespace = "vrm";
    private const string VrmMetaVariable = VrmMetadataNamespace + "/meta";

    public static void Apply(Slot root, VrmModel vrm)
    {
        if (string.IsNullOrWhiteSpace(vrm.MetaJson))
        {
            return;
        }

        License license = root.GetComponent<License>() ?? root.AttachComponent<License>();
        license.RequireCredit.Value = true;
        license.CreditString.Value = vrm.MetaJson;
        license.CanExport.Value = false;

        DynamicVariableSpace space = root.GetComponents<DynamicVariableSpace>()
            .FirstOrDefault(s => string.Equals(s.SpaceName.Value, VrmMetadataNamespace, StringComparison.Ordinal))
            ?? root.AttachComponent<DynamicVariableSpace>();
        space.SpaceName.Value = VrmMetadataNamespace;
        space.OnlyDirectBinding.Value = true;

        Slot metadataSlot = root.FindChild("VRM Metadata") ?? root.AddSlot("VRM Metadata");
        DynamicField<string> variable = metadataSlot.GetComponents<DynamicField<string>>()
            .FirstOrDefault(v => string.Equals(v.VariableName.Value, VrmMetaVariable, StringComparison.Ordinal))
            ?? metadataSlot.AttachComponent<DynamicField<string>>();
        variable.VariableName.Value = VrmMetaVariable;
        variable.TargetField.Target = license.CreditString;
        variable.OverrideOnLink.Value = true;
    }
}
