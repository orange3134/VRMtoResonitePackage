using Elements.Core;
using FrooxEngine;
using VrmToResonitePackage.Vrm;

namespace VrmToResonitePackage;

/// <summary>
/// Converts VRM spring bones (hair/skirt/accessory physics) to Resonite DynamicBoneChains.
/// Mirrors modular-avatar-resonite's approach: each chain's physics parameters are exposed as
/// DynamicVariables grouped under a shared "modular_avatar" space, and a disabled template chain
/// per template name (hair/skirt/...) feeds opinionated default values into every member chain.
/// The variable names match modular-avatar-resonite exactly so third-party items remain compatible.
/// </summary>
internal static class SpringBoneSetup
{
    // Variable / tag strings must stay byte-for-byte identical to modular-avatar-resonite's
    // ResoNamespaces so chains authored by either tool share the same DynamicVariable space.
    private const string SpaceName = "modular_avatar";
    private const string DynBoneTemplatePrefix = "modular_avatar/dynamic_bone_template.";
    private const string SettingsRootVariable = "modular_avatar/AvatarSettingsRoot";
    private const string DynBoneControllerTag = "modular_avatar/dynamic_bone_controller";

    public static void Apply(Slot root, VrmModel vrm)
    {
        if (vrm.SpringChains.Count == 0)
        {
            return;
        }
        new Converter(root).Run(vrm);
    }

    /// <summary>
    /// Holds the per-run state (the shared template root and which templates were already
    /// generated) that the modular-avatar-resonite reference keeps as instance fields.
    /// </summary>
    private sealed class Converter
    {
        private readonly Slot _root;
        private readonly Dictionary<string, Slot> _slotsByName;
        private readonly Dictionary<int, List<IDynamicBoneCollider>> _colliderCache = new();
        private readonly HashSet<string> _generatedTemplates = new();

        private Slot _settingsRoot;
        private Slot _templateRoot;

        public Converter(Slot root)
        {
            _root = root;
            _slotsByName = SlotIndex.Build(root);
        }

        public void Run(VrmModel vrm)
        {
            int chains = 0;
            foreach (VrmSpringChain chain in vrm.SpringChains)
            {
                foreach (int rootNode in chain.RootNodes)
                {
                    Slot boneRoot = ResolveNode(vrm, rootNode);
                    if (boneRoot == null)
                    {
                        UniLog.Warning($"揺れものボーンのノードが見つかりません: {vrm.GetNodeName(rootNode) ?? rootNode.ToString()}");
                        continue;
                    }
                    DynamicBoneChain dynamicChain = boneRoot.AttachComponent<DynamicBoneChain>();
                    dynamicChain.SetupFromChildren(boneRoot);
                    if (dynamicChain.Bones.Count == 0)
                    {
                        dynamicChain.Destroy();
                        continue;
                    }

                    // Individual (non-templated) settings derived from the VRM chain itself.
                    dynamicChain.BaseBoneRadius.Value = MathX.Max(0.001f, chain.HitRadius);
                    dynamicChain.IsGrabbable.Value = true;

                    foreach (int colliderIndex in chain.ColliderIndices.Distinct())
                    {
                        foreach (IDynamicBoneCollider collider in GetOrCreateColliders(vrm, colliderIndex))
                        {
                            dynamicChain.StaticColliders.Add().Target = collider;
                        }
                    }

                    // Add the template / binding helper slots only AFTER SetupFromChildren so the
                    // chain doesn't pick them up as physics bones.
                    EnsureSettingsRoots();
                    string templateName = GuessTemplateName(boneRoot);
                    GenerateTemplateControls(dynamicChain, templateName);
                    boneRoot.Tag = DynBoneControllerTag;
                    chains++;
                }
            }
            UniLog.Log($"揺れものを {chains} チェーン設定しました（テンプレート: {string.Join(", ", _generatedTemplates.OrderBy(s => s))}）。");
        }

        /// <summary>
        /// Creates (or reuses) the shared "modular_avatar" DynamicVariableSpace, the "Avatar Settings"
        /// slot that publishes the settings root reference, and the "Dynamic Bone Settings" template
        /// container. Idempotent across chains.
        /// </summary>
        private void EnsureSettingsRoots()
        {
            if (_settingsRoot != null)
            {
                return;
            }

            // Reuse an existing modular_avatar space if one is already present on the root.
            DynamicVariableSpace space = _root.GetComponents<DynamicVariableSpace>()
                .FirstOrDefault(s => s.SpaceName.Value == SpaceName);
            if (space == null)
            {
                space = _root.AttachComponent<DynamicVariableSpace>();
                space.SpaceName.Value = SpaceName;
                space.OnlyDirectBinding.Value = true;
            }

            // "Avatar Settings" slot publishes itself as modular_avatar/AvatarSettingsRoot.
            _settingsRoot = _root.AddSlot("<color=#00ffff>Avatar Settings</color>");
            ReferenceField<Slot> selfRef = _settingsRoot.AttachComponent<ReferenceField<Slot>>();
            selfRef.Reference.Target = _settingsRoot;
            DynamicReferenceVariable<Slot> settingsRootVar = _settingsRoot.AttachComponent<DynamicReferenceVariable<Slot>>();
            settingsRootVar.VariableName.Value = SettingsRootVariable;
            settingsRootVar.Reference.DriveFrom(selfRef.Reference);

            _templateRoot = _settingsRoot.AddSlot("Dynamic Bone Settings");
        }

        /// <summary>
        /// Ports modular-avatar-resonite's GenerateTemplateControls: a disabled template chain holds
        /// the opinionated defaults and exposes them as template DynamicVariables, while the member
        /// chain reads those same variables back. Both sides key off the template name.
        /// </summary>
        private void GenerateTemplateControls(DynamicBoneChain db, string templateName)
        {
            if (string.IsNullOrWhiteSpace(templateName))
            {
                templateName = "generic";
            }

            const string intron = ".";

            Slot templateRoot = null;
            DynamicBoneChain templateChain = null;
            Slot templateBindings = null;
            if (!_generatedTemplates.Contains(templateName))
            {
                templateRoot = _templateRoot.AddSlot(templateName);
                templateChain = templateRoot.AttachComponent<DynamicBoneChain>();
                templateChain.Enabled = false;
                _generatedTemplates.Add(templateName);

                templateBindings = templateRoot.AddSlot("(Internal) Bindings");
                SetTemplateDefaultConfig(templateChain, templateName);
            }

            Slot templateNameNode = db.Slot.AddSlot("Template Name");
            IField<string> templateNameField = templateNameNode.AttachComponent<ValueField<string>>().Value;
            templateNameField.Value = templateName;

            Slot bindingsNode = db.Slot.AddSlot("Bindings");

            BindField(chain => chain.Inertia);
            BindField(chain => chain.InertiaForce);
            BindField(chain => chain.Damping);
            BindField(chain => chain.Elasticity);
            BindField(chain => chain.Stiffness);
            BindField(chain => chain.SimulateTerminalBones);
            BindField(chain => chain.DynamicPlayerCollision);
            BindField(chain => chain.CollideWithOwnBody);
            BindField(chain => chain.HandCollisionVibration);
            BindField(chain => chain.CollideWithHead);
            BindField(chain => chain.CollideWithBody);
            BindField(chain => chain.Gravity);
            BindField(chain => chain.GravitySpace.UseParentSpace);
            BindField(chain => chain.GravitySpace.Default);
            BindField(chain => chain.UseUserGravityDirection);
            BindField(chain => chain.LocalForce);
            BindField(chain => chain.GrabSlipping);
            BindField(chain => chain.GrabRadiusTolerance);
            BindField(chain => chain.GrabTerminalBones);
            BindField(chain => chain.GrabVibration);

            void BindField<T>(Func<DynamicBoneChain, Sync<T>> getField)
            {
                Sync<T> field = getField(db);

                // Template side: drive the template chain's field from the template variable.
                if (templateRoot != null)
                {
                    DynamicField<T> templateBinding = templateRoot.AttachComponent<DynamicField<T>>();
                    StringConcatNode(templateBindings, templateRoot.Name_Field, field.Name, templateBinding.VariableName, DynBoneTemplatePrefix, intron);
                    Sync<T> templateField = getField(templateChain);
                    templateBinding.OverrideOnLink.Value = true;
                    templateBinding.TargetField.Value = templateField.ReferenceID;
                }

                // Member side: read the variable back into this chain's field.
                Slot fieldBindingNode = bindingsNode.AddSlot(field.Name);
                DynamicField<T> driver = fieldBindingNode.AttachComponent<DynamicField<T>>();
                driver.TargetField.Value = field.ReferenceID;
                driver.OverrideOnLink.Value = false;
                StringConcatNode(fieldBindingNode, templateNameField, field.Name, driver.VariableName, DynBoneTemplatePrefix, intron);
            }
        }

        /// <summary>
        /// Builds "prefix" + templateName + "." + fieldName into <paramref name="target"/> via a
        /// StringConcatenationDriver, driving the middle segment from the template name field.
        /// </summary>
        private static void StringConcatNode(Slot host, IField<string> templateName, string fieldName,
            IField<string> target, string prefix, string intron)
        {
            StringConcatenationDriver driver = host.AttachComponent<StringConcatenationDriver>();
            driver.TargetString.Target = target;
            driver.Strings.Add().Value = prefix;
            driver.Strings.Add().DriveFrom(templateName);
            driver.Strings.Add().Value = intron + fieldName;
        }

        /// <summary>
        /// Opinionated per-template defaults, identical to modular-avatar-resonite's
        /// SetTemplateDefaultConfig. "generic" keeps Resonite's built-in defaults.
        /// All templates explicitly set DynamicPlayerCollision/CollideWithOwnBody to this
        /// project's verified avatar conventions.
        /// </summary>
        private static void SetTemplateDefaultConfig(DynamicBoneChain db, string templateName)
        {
            switch (templateName)
            {
                case "skirt":
                    // Skirts lack angle constraints and clip easily, so keep them stiff and slow.
                    db.Inertia.Value = 0.8f;
                    db.InertiaForce.Value = 2.0f;
                    db.Damping.Value = 50f;
                    db.Elasticity.Value = 600f;
                    db.Stiffness.Value = 0.75f;
                    break;
                case "breast":
                    db.Inertia.Value = 0.9f;
                    db.InertiaForce.Value = 0.75f;
                    db.Damping.Value = 10f;
                    db.Elasticity.Value = 100f;
                    db.Stiffness.Value = 0.67f;
                    break;
                case "hair":
                case "long_hair":
                    db.Inertia.Value = 0.34f;
                    db.InertiaForce.Value = 2.0f;
                    db.Damping.Value = 16.2f;
                    db.Elasticity.Value = 100f;
                    db.Stiffness.Value = 0.2f;
                    break;
                case "ear":
                    db.Inertia.Value = 0.5f;
                    db.InertiaForce.Value = 2.0f;
                    db.Damping.Value = 12.43f;
                    db.Elasticity.Value = 100f;
                    db.Stiffness.Value = 0.2f;
                    break;
                case "tail":
                    db.Inertia.Value = 0.2f;
                    db.InertiaForce.Value = 2.0f;
                    db.Damping.Value = 5f;
                    db.Elasticity.Value = 100f;
                    db.Stiffness.Value = 0.2f;
                    break;
                default:
                    // generic: use Resonite built-in defaults.
                    break;
            }

            // This project's verified convention; set explicitly even when matching Resonite defaults.
            db.DynamicPlayerCollision.Value = true;
            db.CollideWithOwnBody.Value = false;
        }

        /// <summary>
        /// Walks the bone root and its ancestors from the root downward, returning the first slot
        /// name that maps to a template. modular-avatar-resonite's TemplateFromObjectName plus a
        /// few VRM-specific aliases (Japanese names, VRoid's "Bust" breast bones).
        /// </summary>
        private static string GuessTemplateName(Slot boneRoot)
        {
            var segments = boneRoot.EnumerateParents().Reverse().Append(boneRoot)
                .Select(s => s.Name)
                .Where(s => s != null)
                .ToList();
            foreach (string segment in segments)
            {
                string template = TemplateFromObjectName(segment);
                if (template != null)
                {
                    return template;
                }
            }
            return "generic";
        }

        private static string TemplateFromObjectName(string name)
        {
            name = name.ToLowerInvariant();
            if (name.Contains("pony") || name.Contains("twin")) return "long_hair";
            if (name.Contains("hair") || name.Contains("髪")) return "hair";
            if (name.Contains("tail") || name.Contains("尻尾") || name.Contains("しっぽ")) return "tail";
            if (name.Contains("ear") || name.Contains("kemono") || name.Contains("mimi") || name.Contains("耳")) return "ear";
            // VRoid names breast bones "Bust".
            if (name.Contains("breast") || name.Contains("bust") || name.Contains("胸") || name.Contains("おっぱい")) return "breast";
            if (name.Contains("skirt") || name.Contains("スカート")) return "skirt";
            return null;
        }

        private List<IDynamicBoneCollider> GetOrCreateColliders(VrmModel vrm, int colliderIndex)
        {
            if (_colliderCache.TryGetValue(colliderIndex, out List<IDynamicBoneCollider> existing))
            {
                return existing;
            }
            var result = new List<IDynamicBoneCollider>();
            _colliderCache[colliderIndex] = result;
            if (colliderIndex < 0 || colliderIndex >= vrm.SpringColliders.Count)
            {
                return result;
            }
            VrmSpringCollider collider = vrm.SpringColliders[colliderIndex];
            Slot slot = ResolveNode(vrm, collider.NodeIndex);
            if (slot == null)
            {
                return result;
            }
            // Resonite's sphere collider has no offset field; the slot position is the offset.
            float3 offset = ConvertVector(collider.Offset, vrm.SpecVersionMajor);
            result.Add(CreateSphere(slot, offset, collider.Radius));
            if (collider.Tail.HasValue)
            {
                // VRM1 capsule: approximate with extra spheres along the capsule axis.
                float3 tail = ConvertVector(collider.Tail.Value, vrm.SpecVersionMajor);
                float3 axis = tail - offset;
                int segments = MathX.Clamp((int)MathX.Ceil(axis.Magnitude / MathX.Max(collider.Radius, 0.01f)), 1, 4);
                for (int i = 1; i <= segments; i++)
                {
                    result.Add(CreateSphere(slot, offset + axis * ((float)i / segments), collider.Radius));
                }
            }
            return result;
        }

        private static IDynamicBoneCollider CreateSphere(Slot parent, float3 localPosition, float radius)
        {
            Slot colliderSlot = parent.AddSlot("VRM Collider");
            colliderSlot.LocalPosition = localPosition;
            DynamicBoneSphereCollider sphere = colliderSlot.AttachComponent<DynamicBoneSphereCollider>();
            sphere.Radius.Value = radius;
            return sphere;
        }

        private Slot ResolveNode(VrmModel vrm, int nodeIndex)
        {
            string name = vrm.GetNodeName(nodeIndex);
            if (name == null)
            {
                return null;
            }
            _slotsByName.TryGetValue(name, out Slot slot);
            return slot;
        }

        /// <summary>
        /// VRM0 stores spring data in Unity coordinates (UniVRM flips X on export),
        /// VRM1 stores glTF coordinates which Resonite's importer reads numerically as-is.
        /// </summary>
        private static float3 ConvertVector(System.Numerics.Vector3 v, int specVersionMajor)
        {
            return specVersionMajor == 0
                ? new float3(-v.X, v.Y, v.Z)
                : new float3(v.X, v.Y, v.Z);
        }
    }
}
