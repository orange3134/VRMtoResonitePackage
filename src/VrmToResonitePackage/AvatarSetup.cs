using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.CommonAvatar;
using FrooxEngine.FinalIK;
using Renderite.Shared;
using VrmToResonitePackage.Vrm;

namespace VrmToResonitePackage;

/// <summary>Tunable knobs for the avatar setup; defaults match typical VRM avatars.</summary>
internal sealed class AvatarSetupOptions
{
    public bool FaceTracking { get; set; }
    public bool Protect { get; set; } = true;

    /// <summary>Generate the wearer-facing preset-expression context menu.</summary>
    public bool ExpressionMenu { get; set; } = true;

    /// <summary>View offset forward from the eye midpoint, in meters. Null = auto (eye-distance scaled).</summary>
    public float? ViewForward { get; set; }

    /// <summary>View offset upward from the eye midpoint, in meters. Null = auto (eye-distance scaled).</summary>
    public float? ViewUp { get; set; }

    /// <summary>AvatarRenderSettings near clip distance. Zero or negative disables the override.</summary>
    public float NearClip { get; set; } = 0.075f;

    /// <summary>
    /// Attach a DefaultUserScale component so the wearer is shrunk to the avatar's real size
    /// (DefaultScale = AvatarHeight / Resonite's 1.75m default height) instead of the avatar
    /// being stretched up to the player's height.
    /// </summary>
    public bool DefaultUserScale { get; set; }
}

/// <summary>
/// Headless replication of Resonite's in-game avatar creation (AvatarCreator.RunCreate),
/// driven by the exact humanoid/expression data from the VRM file instead of in-world
/// user alignment and name heuristics.
/// </summary>
internal static class AvatarSetup
{
    private static readonly Dictionary<string, BodyNode> VrmBoneToBodyNode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hips"] = BodyNode.Hips,
        ["spine"] = BodyNode.Spine,
        ["chest"] = BodyNode.Chest,
        ["upperChest"] = BodyNode.UpperChest,
        ["neck"] = BodyNode.Neck,
        ["head"] = BodyNode.Head,
        ["jaw"] = BodyNode.Jaw,
        ["leftEye"] = BodyNode.LeftEye,
        ["rightEye"] = BodyNode.RightEye,
        ["leftShoulder"] = BodyNode.LeftShoulder,
        ["leftUpperArm"] = BodyNode.LeftUpperArm,
        ["leftLowerArm"] = BodyNode.LeftLowerArm,
        ["leftHand"] = BodyNode.LeftHand,
        ["rightShoulder"] = BodyNode.RightShoulder,
        ["rightUpperArm"] = BodyNode.RightUpperArm,
        ["rightLowerArm"] = BodyNode.RightLowerArm,
        ["rightHand"] = BodyNode.RightHand,
        ["leftUpperLeg"] = BodyNode.LeftUpperLeg,
        ["leftLowerLeg"] = BodyNode.LeftLowerLeg,
        ["leftFoot"] = BodyNode.LeftFoot,
        ["leftToes"] = BodyNode.LeftToes,
        ["rightUpperLeg"] = BodyNode.RightUpperLeg,
        ["rightLowerLeg"] = BodyNode.RightLowerLeg,
        ["rightFoot"] = BodyNode.RightFoot,
        ["rightToes"] = BodyNode.RightToes,
        // Thumbs (parser already normalizes VRM0 thumb names to the VRM1 scheme).
        ["leftThumbMetacarpal"] = BodyNode.LeftThumb_Metacarpal,
        ["leftThumbProximal"] = BodyNode.LeftThumb_Proximal,
        ["leftThumbDistal"] = BodyNode.LeftThumb_Distal,
        ["rightThumbMetacarpal"] = BodyNode.RightThumb_Metacarpal,
        ["rightThumbProximal"] = BodyNode.RightThumb_Proximal,
        ["rightThumbDistal"] = BodyNode.RightThumb_Distal,
        ["leftIndexProximal"] = BodyNode.LeftIndexFinger_Proximal,
        ["leftIndexIntermediate"] = BodyNode.LeftIndexFinger_Intermediate,
        ["leftIndexDistal"] = BodyNode.LeftIndexFinger_Distal,
        ["leftMiddleProximal"] = BodyNode.LeftMiddleFinger_Proximal,
        ["leftMiddleIntermediate"] = BodyNode.LeftMiddleFinger_Intermediate,
        ["leftMiddleDistal"] = BodyNode.LeftMiddleFinger_Distal,
        ["leftRingProximal"] = BodyNode.LeftRingFinger_Proximal,
        ["leftRingIntermediate"] = BodyNode.LeftRingFinger_Intermediate,
        ["leftRingDistal"] = BodyNode.LeftRingFinger_Distal,
        ["leftLittleProximal"] = BodyNode.LeftPinky_Proximal,
        ["leftLittleIntermediate"] = BodyNode.LeftPinky_Intermediate,
        ["leftLittleDistal"] = BodyNode.LeftPinky_Distal,
        ["rightIndexProximal"] = BodyNode.RightIndexFinger_Proximal,
        ["rightIndexIntermediate"] = BodyNode.RightIndexFinger_Intermediate,
        ["rightIndexDistal"] = BodyNode.RightIndexFinger_Distal,
        ["rightMiddleProximal"] = BodyNode.RightMiddleFinger_Proximal,
        ["rightMiddleIntermediate"] = BodyNode.RightMiddleFinger_Intermediate,
        ["rightMiddleDistal"] = BodyNode.RightMiddleFinger_Distal,
        ["rightRingProximal"] = BodyNode.RightRingFinger_Proximal,
        ["rightRingIntermediate"] = BodyNode.RightRingFinger_Intermediate,
        ["rightRingDistal"] = BodyNode.RightRingFinger_Distal,
        ["rightLittleProximal"] = BodyNode.RightPinky_Proximal,
        ["rightLittleIntermediate"] = BodyNode.RightPinky_Intermediate,
        ["rightLittleDistal"] = BodyNode.RightPinky_Distal,
    };

    public static void Build(Slot root, VrmModel vrm, AvatarSetupOptions options)
    {
        Dictionary<string, Slot> slotsByName = SlotIndex.Build(root);

        BipedRig rig = SetupRig(root, vrm, slotsByName);
        if (rig == null || !rig.IsBiped)
        {
            UniLog.Warning("ヒューマノイドの必須ボーンが揃っていないため、アバターセットアップをスキップします。");
            return;
        }

        // Facing direction straight from the skeleton, independent of import rotations.
        Slot hips = rig[BodyNode.Hips];
        Slot head = rig[BodyNode.Head];
        float3 up = (head.GlobalPosition - hips.GlobalPosition).Normalized;
        float3 bodyRight = (rig[BodyNode.RightUpperArm].GlobalPosition - rig[BodyNode.LeftUpperArm].GlobalPosition).Normalized;
        float3 forward = MathX.Cross(bodyRight, up).Normalized;

        VRIK ik = rig.Slot.AttachComponent<VRIK>();
        ik.Solver.SimulationSpace.Target = rig.Slot.Parent;
        ik.Solver.OffsetSpace.Target = rig.Slot.Parent;
        ik.Initiate();

        // Temporary stand-ins for the headset / controller alignment objects the in-game
        // AvatarCreator would have the user position manually.
        Slot refs = root.World.AddSlot("VRM Avatar References");
        try
        {
            Slot headsetRef = refs.AddSlot("Headset");
            headsetRef.GlobalPosition = ComputeEyePosition(rig, vrm, up, forward, options);
            headsetRef.GlobalRotation = floatQ.LookRotation(forward, up);

            Slot leftRef = refs.AddSlot("LeftHand");
            Slot rightRef = refs.AddSlot("RightHand");
            SetupHandReference(rig, leftRef, isRight: false, up, forward);
            SetupHandReference(rig, rightRef, isRight: true, up, forward);

            Slot leftHand = rig[BodyNode.LeftHand];
            Slot rightHand = rig[BodyNode.RightHand];
            SetupToolAnchors(leftRef, leftHand);
            SetupToolAnchors(rightRef, rightHand);

            SetupEyesAndBlink(root, rig, vrm, headsetRef, slotsByName);

            VRIKAvatar avatar = root.AttachComponent<VRIKAvatar>();
            avatar.Setup(ik, rig, headsetRef, leftRef, rightRef, null, null, null);

            // VRIKAvatarの初期値はVRM向けに適していないため上書きする。
            avatar.FeetHoverHeight.Value = 0f;
            avatar.MaxFeetVelocityOffset.Value = 0.05f;

            if (options.DefaultUserScale)
            {
                // Resoniteはアバターを着用者の身長(既定1.75m)まで引き伸ばすため、等身の低い
                // アバターが巨大化する。代わりに着用者側を AvatarHeight/1.75 倍に縮めて
                // アバターを原寸サイズで表示する。AvatarHeightはSetupで確定済み。
                const float ResoniteDefaultHeight = 1.75f;
                float scale = avatar.AvatarHeight.Value / ResoniteDefaultHeight;
                if (scale > 0f)
                {
                    DefaultUserScale userScale = root.AttachComponent<DefaultUserScale>();
                    userScale.DefaultScale.Value = scale;
                    UniLog.Log($"DefaultUserScale を付与: AvatarHeight {avatar.AvatarHeight.Value:F3}m / {ResoniteDefaultHeight}m = {scale:F3}");
                }
                else
                {
                    UniLog.Warning("AvatarHeightが0以下のためDefaultUserScaleの付与をスキップします。");
                }
            }

            IAvatarObject leftHandObject = root.GetComponentInChildren((IAvatarObject o) => o.Node == BodyNode.LeftHand);
            IAvatarObject rightHandObject = root.GetComponentInChildren((IAvatarObject o) => o.Node == BodyNode.RightHand);
            if (leftHandObject != null)
            {
                leftHandObject.Slot.AttachComponent<AvatarObjectComponentProxy>().Target.Target = leftHand;
            }
            if (rightHandObject != null)
            {
                rightHandObject.Slot.AttachComponent<AvatarObjectComponentProxy>().Target.Target = rightHand;
            }
        }
        finally
        {
            refs.Destroy();
        }

        // Same root component shuffle as AvatarCreator.RunCreate.
        root.GetComponentsInChildren<ObjectRoot>().ForEach(c => c.Destroy());
        root.GetComponentsInChildren<Grabbable>().ForEach(c => c.Destroy());
        root.GetComponentsInChildren<AvatarGroup>().ForEach(c => c.Destroy());
        Grabbable grabbable = root.AttachComponent<Grabbable>();
        root.AttachComponent<ObjectRoot>();
        root.AttachComponent<AvatarGroup>();
        root.AttachComponent<AvatarRoot>();
        grabbable.CustomCanGrabCheck.Target = Grabbable.UserRootGrabCheck;

        if (options.Protect)
        {
            // Headless there is no signed-in user, but ReassignUserOnPackageImport
            // (default true) hands ownership to whoever imports the package in Resonite.
            root.AttachComponent<SimpleAvatarProtection>();
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                renderer.Slot.AttachComponent<SimpleAvatarProtection>();
            }
        }

        if (options.NearClip > 0f)
        {
            // Pull the near clip in so long bangs don't occlude the first-person view.
            Slot renderSettingsSlot = root.AddSlot("AvatarRenderSettings");
            AvatarRenderSettings renderSettings = renderSettingsSlot.AttachComponent<AvatarRenderSettings>();
            renderSettings.NearClip.Value = options.NearClip;
            renderSettings.FarClip.Value = null;
        }

        // Resonite's own EnsureVoiceOutput runs name-heuristic viseme auto-assignment
        // that misassigns blendshapes on many models, so the voice output is set up
        // here and visemes are wired exclusively from the VRM expression data.
        SetupVoiceOutput(root);
        SetupVisemesFromVrm(root, vrm);
        if (options.FaceTracking)
        {
            AvatarCreator.TrySetupFaceTracking(root);
        }
        if (options.ExpressionMenu)
        {
            ExpressionMenuSetup.Setup(root, vrm);
        }
        SetupAwayIndicator(root);
        SetupFirstPersonVisibility(root, vrm);

        root.GetComponentInChildren((AvatarPoseNode n) => n.Node.Value == BodyNode.Head)?.InstrumentWithViewHeadOverride();
        UniLog.Log("アバターセットアップ完了。");
    }

    // ---------------------------------------------------------------- rig

    private static BipedRig SetupRig(Slot root, VrmModel vrm, Dictionary<string, Slot> slotsByName)
    {
        // The model importer may have already classified the rig heuristically.
        // Keep avatar setup components on the avatar root; only the bone targets live
        // under the imported model hierarchy.
        List<BipedRig> existingRigs = root.GetComponentsInChildren<BipedRig>();

        // The importer's name-heuristic classification can leave bogus entries on BodyNodes
        // the real skeleton never claims (e.g. an accessory chain Spine/SpineRibbon/... gets
        // classified Chest/UpperChest/Neck/Head; the duplicates are rejected but UpperChest
        // sticks because the real skeleton has none, and VRIK then resolves its chest as
        // UpperChest ?? Chest -> the ribbon bone). The VRM humanoid map is the single source
        // of truth, so wipe everything the heuristics produced before assigning.
        var heuristicBones = new Dictionary<BodyNode, string>();
        BipedRig heuristicRig = existingRigs.FirstOrDefault();
        if (heuristicRig != null)
        {
            foreach (KeyValuePair<BodyNode, SyncRef<Slot>> entry in heuristicRig.Bones)
            {
                heuristicBones[entry.Key] = entry.Value.Target?.Name;
            }
        }

        BipedRig rig = root.GetComponent<BipedRig>() ?? root.AttachComponent<BipedRig>();
        foreach (BipedRig existingRig in existingRigs)
        {
            if (existingRig != rig)
            {
                existingRig.Destroy();
            }
        }
        rig.Bones.Clear();

        int mapped = 0;
        var assigned = new HashSet<BodyNode>();
        foreach ((string vrmBone, int nodeIndex) in vrm.HumanBones)
        {
            if (!VrmBoneToBodyNode.TryGetValue(vrmBone, out BodyNode bodyNode))
            {
                continue;
            }
            string nodeName = vrm.GetNodeName(nodeIndex);
            if (nodeName == null || !slotsByName.TryGetValue(nodeName, out Slot boneSlot))
            {
                UniLog.Warning($"VRMボーン '{vrmBone}' のノード '{nodeName}' に対応するスロットが見つかりません。");
                continue;
            }
            rig[bodyNode] = boneSlot;
            assigned.Add(bodyNode);
            mapped++;
        }
        foreach ((BodyNode node, string boneName) in heuristicBones)
        {
            if (!assigned.Contains(node))
            {
                UniLog.Log($"名前推測で分類されたボーン {node} -> '{boneName}' はVRMヒューマノイドマップに無いため破棄しました。");
            }
        }
        UniLog.Log($"VRMヒューマノイドマップからボーンを {mapped} 個割り当てました。");
        rig.GuessForwardFlipped();
        return rig;
    }

    // ---------------------------------------------------------------- alignment

    /// <summary>
    /// Computes the first-person view position. The raw eye-bone midpoint sits inside
    /// the face mesh, so the view is pushed out to roughly the glabella (between the
    /// eyebrows, on the face surface) — the common convention for Resonite avatars.
    /// The offsets scale with the distance between the eye bones unless overridden.
    /// </summary>
    private static float3 ComputeEyePosition(BipedRig rig, VrmModel vrm, float3 up, float3 forward,
        AvatarSetupOptions options)
    {
        Slot leftEye = rig.TryGetBone(BodyNode.LeftEye);
        Slot rightEye = rig.TryGetBone(BodyNode.RightEye);
        if (leftEye != null && rightEye != null)
        {
            float3 eyeMid = (leftEye.GlobalPosition + rightEye.GlobalPosition) * 0.5f;
            float eyeDistance = MathX.Distance(leftEye.GlobalPosition, rightEye.GlobalPosition);
            float forwardOffset = options.ViewForward ?? MathX.Clamp(eyeDistance * 0.7f, 0.03f, 0.09f);
            float upOffset = options.ViewUp ?? MathX.Clamp(eyeDistance * 0.2f, 0.005f, 0.025f);
            return eyeMid + forward * forwardOffset + up * upOffset;
        }

        Slot head = rig[BodyNode.Head];
        float scale = MathX.Abs(head.GlobalScale.y);
        float3 basePosition;
        if (vrm.FirstPersonOffset.HasValue)
        {
            System.Numerics.Vector3 offset = vrm.FirstPersonOffset.Value;
            basePosition = head.GlobalPosition
                           + up * offset.Y * scale
                           + forward * MathX.Abs(offset.Z) * scale;
        }
        else
        {
            basePosition = head.GlobalPosition + up * 0.06f * scale;
        }
        return basePosition
               + forward * (options.ViewForward ?? 0.06f * scale)
               + up * (options.ViewUp ?? 0.01f * scale);
    }

    /// <summary>
    /// Positions and orients a hand reference deterministically from the skeleton:
    /// +Z points from the wrist toward the middle finger, +Y along the back of the hand
    /// (the orientation Resonite expects for hand alignment). VRIK's guessed palm axes
    /// vary between models, so they are not used.
    /// </summary>
    private static void SetupHandReference(BipedRig rig, Slot reference, bool isRight, float3 modelUp, float3 modelForward)
    {
        Slot hand = rig[isRight ? BodyNode.RightHand : BodyNode.LeftHand];
        float3 fingers = ComputeFingerDirection(rig, hand, isRight, modelForward);
        float3 back = ComputeBackOfHand(fingers, modelUp);

        floatQ handRotation = floatQ.LookRotation(fingers, back);
        reference.GlobalPosition = hand.GlobalPosition;
        reference.GlobalRotation = handRotation;

        // Anchor placement (orientation shared with the hand):
        //   Tool      - back 3cm from the tip of middle finger, so it adapts to hand size
        //   GrabArea  - at the base of the middle finger, nudged toward the palm
        //   Toolshelf - at the wrist, offset toward the back of the hand
        Slot middleProximal = rig.TryGetBone(isRight
            ? BodyNode.RightMiddleFinger_Proximal
            : BodyNode.LeftMiddleFinger_Proximal);
        float3 fingerBase = middleProximal?.GlobalPosition ?? (hand.GlobalPosition + fingers * 0.07f);
        float3 fingerTip = ComputeMiddleFingerTip(rig, hand, isRight, fingers);

        CreateAnchorReference(reference, "Tooltip", fingerTip + fingers * - 0.03f, handRotation);
        CreateAnchorReference(reference, "Grabber", fingerBase - back * 0.01f, handRotation);
        CreateAnchorReference(reference, "Shelf", hand.GlobalPosition + back * 0.05f, handRotation);
    }

    private static void CreateAnchorReference(Slot reference, string name, float3 globalPosition, floatQ globalRotation)
    {
        Slot anchor = reference.AddSlot(name);
        anchor.GlobalPosition = globalPosition;
        anchor.GlobalRotation = globalRotation;
    }

    /// <summary>
    /// Estimates the middle fingertip position. The VRM humanoid map only goes down to
    /// the distal phalanx, so this prefers an unmapped end bone parented under the
    /// deepest mapped phalanx (most VRM exports keep one); otherwise it extrapolates
    /// past the deepest phalanx by the length of the preceding segment.
    /// </summary>
    private static float3 ComputeMiddleFingerTip(BipedRig rig, Slot hand, bool isRight, float3 fingers)
    {
        Slot proximal = rig.TryGetBone(isRight ? BodyNode.RightMiddleFinger_Proximal : BodyNode.LeftMiddleFinger_Proximal);
        Slot intermediate = rig.TryGetBone(isRight ? BodyNode.RightMiddleFinger_Intermediate : BodyNode.LeftMiddleFinger_Intermediate);
        Slot distal = rig.TryGetBone(isRight ? BodyNode.RightMiddleFinger_Distal : BodyNode.LeftMiddleFinger_Distal);

        Slot deepest = distal ?? intermediate ?? proximal;
        if (deepest == null)
        {
            // No finger bones - matches the previous fixed wrist offset once the
            // caller adds the 3cm tool clearance.
            return hand.GlobalPosition + fingers * 0.12f;
        }

        Slot endBone = null;
        float endAlong = 0.001f;
        foreach (Slot child in deepest.Children)
        {
            float along = MathX.Dot(child.GlobalPosition - deepest.GlobalPosition, fingers);
            if (along > endAlong)
            {
                endAlong = along;
                endBone = child;
            }
        }
        if (endBone != null)
        {
            return endBone.GlobalPosition;
        }

        Slot previous = deepest == distal ? (intermediate ?? proximal) : (deepest == intermediate ? proximal : null);
        float3 previousPosition = previous?.GlobalPosition ?? hand.GlobalPosition;
        float remaining = MathX.Distance(previousPosition, deepest.GlobalPosition);
        // The part past the deepest phalanx is roughly as long as the preceding
        // segment, shorter when that segment spans more than one phalanx.
        float factor = (deepest == distal && intermediate != null) || (deepest == intermediate && proximal != null)
            ? 0.8f
            : 0.4f;
        return deepest.GlobalPosition + fingers * remaining * factor;
    }

    private static float3 ComputeFingerDirection(BipedRig rig, Slot hand, bool isRight, float3 modelForward)
    {
        BodyNode[] candidates = isRight
            ? new[] { BodyNode.RightMiddleFinger_Proximal, BodyNode.RightIndexFinger_Proximal, BodyNode.RightRingFinger_Proximal }
            : new[] { BodyNode.LeftMiddleFinger_Proximal, BodyNode.LeftIndexFinger_Proximal, BodyNode.LeftRingFinger_Proximal };
        foreach (BodyNode candidate in candidates)
        {
            Slot finger = rig.TryGetBone(candidate);
            if (finger != null)
            {
                float3 direction = finger.GlobalPosition - hand.GlobalPosition;
                if (direction.Magnitude > 0.0001f)
                {
                    return direction.Normalized;
                }
            }
        }
        Slot lowerArm = rig.TryGetBone(isRight ? BodyNode.RightLowerArm : BodyNode.LeftLowerArm);
        if (lowerArm != null)
        {
            float3 armDirection = hand.GlobalPosition - lowerArm.GlobalPosition;
            if (armDirection.Magnitude > 0.0001f)
            {
                return armDirection.Normalized;
            }
        }
        return modelForward;
    }

    private static float3 ComputeBackOfHand(float3 fingers, float3 modelUp)
    {
        // VRM mandates a T-pose rest pose with palms facing down, so the back of
        // the hand always points straight up. Earlier this was derived from the
        // thumb position, but on models where the thumb sits low that estimate
        // drifted away from the true back-of-hand direction, so we fix it to
        // modelUp and only project out the finger component to keep it orthogonal.
        float3 back = modelUp - fingers * MathX.Dot(modelUp, fingers);
        return back.Magnitude > 0.0001f ? back.Normalized : modelUp;
    }

    private static void SetupToolAnchors(Slot handReference, Slot handBone)
    {
        SetupToolAnchor(handBone, handReference.FindChild("Tooltip"), AvatarToolAnchor.Point.Tool);
        SetupToolAnchor(handBone, handReference.FindChild("Grabber"), AvatarToolAnchor.Point.GrabArea);
        SetupToolAnchor(handBone, handReference.FindChild("Shelf"), AvatarToolAnchor.Point.Toolshelf);
    }

    private static void SetupToolAnchor(Slot anchorRoot, Slot anchorReference, AvatarToolAnchor.Point point)
    {
        if (anchorReference == null)
        {
            return;
        }
        Slot slot = anchorRoot.AddSlot(point + " Anchor");
        slot.CopyTransform(anchorReference);
        slot.AttachComponent<AvatarToolAnchor>().AnchorPoint.Value = point;
    }

    // ---------------------------------------------------------------- eyes & blink

    private static void SetupEyesAndBlink(Slot root, BipedRig rig, VrmModel vrm, Slot headsetRef,
        Dictionary<string, Slot> slotsByName)
    {
        Slot head = rig[BodyNode.Head];
        Slot leftEye = rig.TryGetBone(BodyNode.LeftEye);
        Slot rightEye = rig.TryGetBone(BodyNode.RightEye);

        Slot managerSlot = head.AddSlot("Eye Manager");
        managerSlot.CopyTransform(headsetRef);
        EyeManager eyeManager = managerSlot.AttachComponent<EyeManager>();
        managerSlot.AttachComponent<AvatarEyeDataSourceAssigner>().TargetReference.Target = eyeManager.EyeDataSource;
        managerSlot.AttachComponent<AvatarUserReferenceAssigner>().References.Add(eyeManager.SimulatingUser);
        eyeManager.IgnoreLocalUserHead.Value = true;

        if (leftEye != null && rightEye != null)
        {
            // Same pivot construction as AvatarCreator.SetupEyes.
            Slot leftPivot = leftEye.AddSlot("Left Eye Pivot");
            Slot rightPivot = rightEye.AddSlot("Right Eye Pivot");
            leftPivot.Parent = leftEye.Parent;
            rightPivot.Parent = rightEye.Parent;
            leftPivot.GlobalRotation = managerSlot.GlobalRotation;
            rightPivot.GlobalRotation = managerSlot.GlobalRotation;
            leftEye.Parent = leftPivot;
            rightEye.Parent = rightPivot;

            EyeRotationDriver rotationDriver = managerSlot.AttachComponent<EyeRotationDriver>();
            rotationDriver.EyeManager.Target = eyeManager;
            // The default (15) lets eyes swing far enough to clip through VRM face meshes.
            rotationDriver.MaxSwing.Value = 4f;
            EyeRotationDriver.Eye left = rotationDriver.Eyes.Add();
            EyeRotationDriver.Eye right = rotationDriver.Eyes.Add();
            left.Root.Target = leftPivot;
            left.Side.Value = EyeSide.Left;
            left.SetupFromRoot();
            right.Root.Target = rightPivot;
            right.Side.Value = EyeSide.Right;
            right.SetupFromRoot();
        }

        EyeLinearDriver linearDriver = managerSlot.AttachComponent<EyeLinearDriver>();
        linearDriver.EyeManager.Target = eyeManager;

        var resolver = new BlendshapeResolver(root, vrm);
        List<(IField<float> field, float weight)> blinkLeft = ResolveBinds(resolver, vrm, "blinkLeft");
        List<(IField<float> field, float weight)> blinkRight = ResolveBinds(resolver, vrm, "blinkRight");
        List<(IField<float> field, float weight)> blinkBoth = ResolveBinds(resolver, vrm, "blink");

        if (blinkLeft.Count > 0 && blinkRight.Count > 0)
        {
            AddBlinkEyes(linearDriver, blinkLeft, EyeSide.Left);
            AddBlinkEyes(linearDriver, blinkRight, EyeSide.Right);
        }
        else if (blinkBoth.Count > 0)
        {
            AddBlinkEyes(linearDriver, blinkBoth, EyeSide.Combined);
        }

        if (linearDriver.Eyes.Count == 0 && rotationDriverMissing(managerSlot))
        {
            // Nothing useful was created; keep hierarchy clean.
            managerSlot.Destroy();
        }

        static bool rotationDriverMissing(Slot slot)
        {
            return slot.GetComponent<EyeRotationDriver>() == null;
        }
    }

    /// <summary>
    /// Resolves every morph bind of the given expression preset. A blink expression can bind
    /// multiple shapes (e.g. eyelid + eyelash), and each needs its own driver entry. The same
    /// field bound more than once keeps the strongest weight.
    /// </summary>
    private static List<(IField<float> field, float weight)> ResolveBinds(
        BlendshapeResolver resolver, VrmModel vrm, string preset)
    {
        var result = new List<(IField<float> field, float weight)>();
        VrmExpression expression = vrm.Expressions.FirstOrDefault(
            e => string.Equals(e.Preset, preset, StringComparison.OrdinalIgnoreCase));
        if (expression == null)
        {
            return result;
        }
        foreach (VrmExpressionBind bind in expression.Binds)
        {
            IField<float> field = resolver.Resolve(bind);
            if (field == null)
            {
                continue;
            }
            int existing = result.FindIndex(r => r.field == field);
            if (existing >= 0)
            {
                if (bind.Weight > result[existing].weight)
                {
                    result[existing] = (field, bind.Weight);
                }
            }
            else
            {
                result.Add((field, bind.Weight));
            }
        }
        return result;
    }

    /// <summary>Adds one Eye entry per bound morph; ClosedState carries the bind weight.</summary>
    private static void AddBlinkEyes(EyeLinearDriver driver,
        List<(IField<float> field, float weight)> binds, EyeSide side)
    {
        foreach ((IField<float> field, float weight) in binds)
        {
            if (field.IsDriven)
            {
                // 同じシェイプが既に別のEyeエントリ等で使われている（例: blinkLeft/blinkRightが共有）。
                UniLog.Warning($"瞬き({side})のブレンドシェイプは既にドライブされているためスキップします。");
                continue;
            }
            EyeLinearDriver.Eye eye = driver.Eyes.Add();
            eye.Side.Value = side;
            eye.OpenCloseTarget.Target = field;
            eye.ClosedState.Value = weight;
        }
    }

    // ---------------------------------------------------------------- voice & visemes

    /// <summary>
    /// Voice output portion of AvatarCreator.EnsureVoiceOutput, without the
    /// TrySetupVisemes name-heuristics call at its end.
    /// </summary>
    private static void SetupVoiceOutput(Slot root)
    {
        IAvatarObject headObject = root.GetComponentInChildren((IAvatarObject o) => o.Node == BodyNode.Head);
        if (headObject == null)
        {
            return;
        }
        AvatarVoiceSourceAssigner voiceAssigner = headObject.Slot.GetComponentOrAttach<AvatarVoiceSourceAssigner>();
        AvatarAudioOutputManager audioManager = headObject.Slot.GetComponentOrAttach<AvatarAudioOutputManager>();

        VolumeMeter volumeMeter = headObject.Slot.AttachComponent<VolumeMeter>();
        headObject.Slot.AttachComponent<AvatarVoiceSourceAssigner>().TargetReference.Target = volumeMeter.Source;

        AudioOutput audioOutput = root.GetComponentInChildren((AudioOutput o) => o.Source.Target == null);
        if (audioOutput == null)
        {
            audioOutput = headObject.Slot.AttachComponent<AudioOutput>();
            audioOutput.SpatialBlend.Value = 1f;
            audioOutput.Spatialize.Value = true;
            audioOutput.AudioTypeGroup.Value = AudioTypeGroup.Voice;
        }
        audioManager.AudioOutput.Target = audioOutput;
        voiceAssigner.TargetReference.Target = audioOutput.Source;
        headObject.Slot.GetComponentOrAttach<AvatarVoiceRangeVisualizer>();
    }

    // VRM declares only the five vowels; VRChat declares the full 15-viseme set. Both map here.
    // "ee" is the VRM spelling of Resonite's Viseme.E; the rest match the enum names directly.
    private static readonly (string preset, Viseme viseme)[] VisemePresets =
    {
        ("sil", Viseme.Silence),
        ("PP", Viseme.PP),
        ("FF", Viseme.FF),
        ("TH", Viseme.TH),
        ("DD", Viseme.DD),
        ("kk", Viseme.kk),
        ("CH", Viseme.CH),
        ("SS", Viseme.SS),
        ("nn", Viseme.nn),
        ("RR", Viseme.RR),
        ("aa", Viseme.aa),
        ("E", Viseme.E),
        ("ee", Viseme.E),
        ("ih", Viseme.ih),
        ("oh", Viseme.oh),
        ("ou", Viseme.ou),
    };

    /// <summary>
    /// Wires the vowel/viseme expressions directly to Resonite's viseme system. Resonite's own
    /// auto-assignment is bypassed entirely, so only blendshapes the source explicitly declares get
    /// linked. VRM provides the five vowels; VRChat provides up to all 15 visemes.
    /// </summary>
    private static void SetupVisemesFromVrm(Slot root, VrmModel vrm)
    {
        var resolver = new BlendshapeResolver(root, vrm);
        var drivers = new List<DirectVisemeDriver>();

        foreach ((string preset, Viseme viseme) in VisemePresets)
        {
            VrmExpression expression = vrm.Expressions.FirstOrDefault(
                e => string.Equals(e.Preset, preset, StringComparison.OrdinalIgnoreCase));
            if (expression == null)
            {
                continue;
            }
            foreach (VrmExpressionBind bind in expression.Binds.OrderByDescending(b => b.Weight))
            {
                (SkinnedMeshRenderer skin, IField<float> field) = resolver.ResolveWithRenderer(bind);
                if (field == null)
                {
                    continue;
                }
                DirectVisemeDriver driver = skin.Slot.GetComponent<DirectVisemeDriver>()
                                            ?? skin.Slot.AttachComponent<DirectVisemeDriver>();
                driver[viseme].ForceLink(field);
                if (!drivers.Contains(driver))
                {
                    drivers.Add(driver);
                }
                break; // primary bind only per viseme
            }
        }
        if (drivers.Count == 0)
        {
            return;
        }

        VisemeAnalyzer analyzer = root.GetComponentInChildren<VisemeAnalyzer>();
        if (analyzer == null)
        {
            Slot head = root.GetComponentInChildren((IAvatarObject o) => o.Node == BodyNode.Head)?.Slot ?? root;
            analyzer = head.AttachComponent<VisemeAnalyzer>();
            head.AttachComponent<AvatarVoiceSourceAssigner>().TargetReference.Target = analyzer.Source;
        }
        foreach (DirectVisemeDriver driver in drivers)
        {
            if (driver.Source.Target == null)
            {
                driver.Source.Target = analyzer;
            }
        }
        UniLog.Log($"表情からビセームを設定しました ({drivers.Count} ドライバー)。");
    }

    // ---------------------------------------------------------------- misc avatar bits

    /// <summary>Replicates AvatarCreator.SetupAwayIndicator (private in FrooxEngine).</summary>
    private static void SetupAwayIndicator(Slot root)
    {
        IAvatarObject headObject = root.GetComponentInChildren((IAvatarObject o) => o.Node == BodyNode.Head);
        if (headObject == null)
        {
            return;
        }
        AvatarUserReferenceAssigner assigner = headObject.Slot.AttachComponent<AvatarUserReferenceAssigner>();
        IAssetProvider<FrooxEngine.Material> awayMaterial = SimpleAwayIndicator.CreateAwayMaterial(root);
        foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>())
        {
            SimpleAwayIndicator indicator = renderer.Slot.AttachComponent<SimpleAwayIndicator>();
            indicator.AwayMaterial.Target = awayMaterial;
            indicator.Renderer.Target = renderer;
            assigner.References.Add(indicator.User);
        }
    }

    // ---------------------------------------------------------------- first-person visibility

    private const string ModularAvatarNamespace = "modular_avatar";
    private const string AvatarWornLocalVariable = ModularAvatarNamespace + "/AvatarWornLocal";

    private static readonly RenderingContext[] ThirdPersonRenderContexts =
    {
        RenderingContext.ExternalView,
        RenderingContext.Camera,
        RenderingContext.Mirror,
        RenderingContext.Portal,
        RenderingContext.RenderToAsset,
    };

    private static void SetupFirstPersonVisibility(Slot root, VrmModel vrm)
    {
        List<VrmFirstPersonMeshAnnotation> effectiveAnnotations = vrm.FirstPersonMeshAnnotations
            .Where(a => a.Flag is VrmFirstPersonFlag.ThirdPersonOnly or VrmFirstPersonFlag.FirstPersonOnly)
            .ToList();
        if (effectiveAnnotations.Count == 0)
        {
            return;
        }

        Dictionary<string, Slot> slotsByName = SlotIndex.Build(root);
        Dictionary<MeshRenderer, VrmFirstPersonFlag> rendererFlags = new();
        foreach (VrmFirstPersonMeshAnnotation annotation in effectiveAnnotations)
        {
            foreach (MeshRenderer renderer in ResolveAnnotatedRenderers(vrm, slotsByName, annotation))
            {
                if (rendererFlags.TryGetValue(renderer, out VrmFirstPersonFlag existing) && existing != annotation.Flag)
                {
                    UniLog.Warning($"FirstPerson設定が競合しています: renderer={renderer.Slot.Name}, {existing} -> {annotation.Flag}");
                }
                rendererFlags[renderer] = annotation.Flag;
            }
        }

        if (rendererFlags.Count == 0)
        {
            UniLog.Warning("VRM FirstPerson設定は見つかりましたが、対応するRendererが見つかりませんでした。");
            return;
        }

        DynamicVariableSpace space = root.GetComponent<DynamicVariableSpace>()
                                     ?? root.AttachComponent<DynamicVariableSpace>();
        space.SpaceName.Value = ModularAvatarNamespace;
        space.OnlyDirectBinding.Value = true;

        ImportAvatarRootIdentification(root);

        IAssetProvider<FrooxEngine.Material> invisibleMaterial = GetOrCreateInvisibleMaterial(root);
        int configured = 0;
        foreach ((MeshRenderer renderer, VrmFirstPersonFlag flag) in rendererFlags)
        {
            if (renderer.Materials.Count == 0)
            {
                continue;
            }

            switch (flag)
            {
                case VrmFirstPersonFlag.ThirdPersonOnly:
                    AddMaterialOverride(renderer, RenderingContext.UserView, invisibleMaterial);
                    configured++;
                    break;

                case VrmFirstPersonFlag.FirstPersonOnly:
                    foreach (RenderingContext context in ThirdPersonRenderContexts)
                    {
                        AddMaterialOverride(renderer, context, invisibleMaterial);
                    }
                    configured++;
                    break;
            }
        }

        if (configured > 0)
        {
            UniLog.Log($"VRM FirstPerson表示設定を {configured} renderer に適用しました。");
        }
    }

    public static async Task ApplyFirstPersonAutoAsync(Slot root, VrmModel vrm)
    {
        List<VrmFirstPersonMeshAnnotation> autoAnnotations = vrm.FirstPersonMeshAnnotations
            .Where(a => a.Flag == VrmFirstPersonFlag.Auto)
            .ToList();
        if (autoAnnotations.Count == 0)
        {
            return;
        }

        DynamicVariableSpace space = root.GetComponent<DynamicVariableSpace>()
                                     ?? root.AttachComponent<DynamicVariableSpace>();
        space.SpaceName.Value = ModularAvatarNamespace;
        space.OnlyDirectBinding.Value = true;

        ImportAvatarRootIdentification(root);

        Dictionary<string, Slot> slotsByName = SlotIndex.Build(root);
        Slot firstPersonBone = ResolveFirstPersonBone(root, vrm, slotsByName);
        if (firstPersonBone == null)
        {
            UniLog.Warning("VRM FirstPerson Auto skipped: head bone was not found.");
            return;
        }

        IAssetProvider<FrooxEngine.Material> invisibleMaterial = GetOrCreateInvisibleMaterial(root);
        Slot assets = root.FindChild("Assets") ?? root.AddSlot("Assets");
        Slot firstPersonMeshAssets = assets.FindChild("FirstPerson Auto Meshes")
                                     ?? assets.AddSlot("FirstPerson Auto Meshes");
        int configured = 0;

        foreach (VrmFirstPersonMeshAnnotation annotation in autoAnnotations)
        {
            foreach (MeshRenderer renderer in ResolveAnnotatedRenderers(vrm, slotsByName, annotation))
            {
                switch (renderer)
                {
                    case SkinnedMeshRenderer skinned:
                        if (await TrySetupAutoSkinnedRenderer(skinned, firstPersonBone, firstPersonMeshAssets,
                                invisibleMaterial))
                        {
                            configured++;
                        }
                        break;

                    default:
                        if (TrySetupAutoStaticRenderer(renderer, firstPersonBone, invisibleMaterial))
                        {
                            configured++;
                        }
                        break;
                }
            }
        }

        if (configured > 0)
        {
            UniLog.Log($"VRM FirstPerson Auto mesh split applied to {configured} renderer(s).");
        }
    }

    private static async Task<bool> TrySetupAutoSkinnedRenderer(SkinnedMeshRenderer renderer, Slot firstPersonBone,
        Slot meshAssetSlot, IAssetProvider<FrooxEngine.Material> invisibleMaterial)
    {
        if (renderer.Mesh.Target == null || renderer.Mesh.Asset?.Data == null || renderer.Materials.Count == 0)
        {
            return false;
        }

        int[] eraseBones = GetFirstPersonEraseBoneIndices(renderer, firstPersonBone);
        if (eraseBones.Length == 0)
        {
            return false;
        }

        MeshX source;
        await default(ToBackground);
        object readLock = new();
        await renderer.Mesh.Asset.RequestReadLock(readLock).ConfigureAwait(false);
        try
        {
            source = new MeshX(renderer.Mesh.Asset.Data);
        }
        finally
        {
            renderer.Mesh.Asset.ReleaseReadLock(readLock);
        }

        int remainingTriangles = EraseTrianglesByBoneWeight(source, eraseBones);
        if (remainingTriangles == 0)
        {
            await default(ToWorld);
            AddVisibility(renderer, VrmFirstPersonFlag.ThirdPersonOnly, invisibleMaterial);
            return true;
        }
        source.ClearBlendShapes();

        Uri uri = await renderer.Engine.LocalDB.SaveAssetAsync(source).ConfigureAwait(false);
        await default(ToWorld);
        if (uri == null)
        {
            return false;
        }

        Slot headlessSlot = renderer.Slot.AddSlot("_headless_" + renderer.Slot.Name);
        SkinnedMeshRenderer headless = headlessSlot.AttachComponent<SkinnedMeshRenderer>();
        headless.Mesh.Target = meshAssetSlot.AttachStaticMesh(uri, getExisting: false);
        headless.BoundsComputeMethod.Value = renderer.BoundsComputeMethod.Value;
        headless.ExplicitLocalBounds.Value = renderer.ExplicitLocalBounds.Value;
        headless.ProxyBoundsSource.Target = renderer.ProxyBoundsSource.Target;
        foreach (Slot bone in renderer.Bones)
        {
            headless.Bones.Add().Target = bone;
        }

        AddVisibility(renderer, VrmFirstPersonFlag.ThirdPersonOnly, invisibleMaterial);
        AddFirstPersonAutoHeadlessVisibility(headless, renderer, invisibleMaterial);
        return true;
    }

    private static bool TrySetupAutoStaticRenderer(MeshRenderer renderer, Slot firstPersonBone,
        IAssetProvider<FrooxEngine.Material> invisibleMaterial)
    {
        if (!IsDescendantOrSelf(renderer.Slot, firstPersonBone))
        {
            return false;
        }
        AddVisibility(renderer, VrmFirstPersonFlag.ThirdPersonOnly, invisibleMaterial);
        return true;
    }

    private static int[] GetFirstPersonEraseBoneIndices(SkinnedMeshRenderer renderer, Slot firstPersonBone)
    {
        List<int> eraseBones = new();
        for (int i = 0; i < renderer.Bones.Count; i++)
        {
            Slot bone = renderer.Bones[i];
            if (bone != null && IsDescendantOrSelf(bone, firstPersonBone))
            {
                eraseBones.Add(i);
            }
        }
        return eraseBones.ToArray();
    }

    private static Slot ResolveFirstPersonBone(Slot root, VrmModel vrm, Dictionary<string, Slot> slotsByName)
    {
        if (vrm.HumanBones.TryGetValue("head", out int headIndex))
        {
            string headName = vrm.GetNodeName(headIndex);
            if (headName != null && slotsByName.TryGetValue(headName, out Slot headSlot))
            {
                return headSlot;
            }
        }

        return root.GetComponentInChildren<BipedRig>()?.TryGetBone(BodyNode.Head);
    }

    private static bool IsDescendantOrSelf(Slot slot, Slot ancestor)
    {
        for (Slot current = slot; current != null; current = current.Parent)
        {
            if (current == ancestor)
            {
                return true;
            }
        }
        return false;
    }

    private static int EraseTrianglesByBoneWeight(MeshX mesh, int[] eraseBoneIndices)
    {
        var eraseSet = new HashSet<int>(eraseBoneIndices);
        int remaining = 0;
        for (int submeshIndex = 0; submeshIndex < mesh.SubmeshCount; submeshIndex++)
        {
            if (mesh.GetSubmesh(submeshIndex) is not TriangleSubmesh triangles)
            {
                continue;
            }

            for (int i = triangles.Count - 1; i >= 0; i--)
            {
                Triangle triangle = triangles.GetTriangleUnsafe(i);
                if (UsesAnyBone(mesh.RawBoneBindings[triangle.Vertex0IndexUnsafe], eraseSet) ||
                    UsesAnyBone(mesh.RawBoneBindings[triangle.Vertex1IndexUnsafe], eraseSet) ||
                    UsesAnyBone(mesh.RawBoneBindings[triangle.Vertex2IndexUnsafe], eraseSet))
                {
                    triangles.Remove(i);
                }
            }
            remaining += triangles.Count;
        }
        return remaining;
    }

    private static bool UsesAnyBone(BoneBinding binding, HashSet<int> eraseBoneIndices)
    {
        return (binding.weight0 > 0f && eraseBoneIndices.Contains(binding.boneIndex0)) ||
               (binding.weight1 > 0f && eraseBoneIndices.Contains(binding.boneIndex1)) ||
               (binding.weight2 > 0f && eraseBoneIndices.Contains(binding.boneIndex2)) ||
               (binding.weight3 > 0f && eraseBoneIndices.Contains(binding.boneIndex3));
    }

    private static void AddFirstPersonAutoHeadlessVisibility(SkinnedMeshRenderer headless, MeshRenderer source,
        IAssetProvider<FrooxEngine.Material> invisibleMaterial)
    {
        headless.Materials.Clear();
        RenderMaterialOverride materialOverride = headless.Slot.AttachComponent<RenderMaterialOverride>();
        materialOverride.Renderer.Target = headless;
        materialOverride.Context.Value = RenderingContext.UserView;

        for (int i = 0; i < source.Materials.Count; i++)
        {
            headless.Materials.Add(invisibleMaterial);
            RenderMaterialOverride.MaterialOverride entry = materialOverride.Overrides.Add();
            entry.Index.Value = i;
            entry.Material.Target = source.Materials[i];
        }

        DynamicValueVariableDriver<bool> avatarWorn = headless.Slot.AttachComponent<DynamicValueVariableDriver<bool>>();
        avatarWorn.VariableName.Value = AvatarWornLocalVariable;
        avatarWorn.DefaultValue.Value = false;
        avatarWorn.Target.Target = materialOverride.EnabledField;
    }

    private static void AddVisibility(MeshRenderer renderer, VrmFirstPersonFlag flag,
        IAssetProvider<FrooxEngine.Material> invisibleMaterial)
    {
        if (renderer.Materials.Count == 0)
        {
            return;
        }
        switch (flag)
        {
            case VrmFirstPersonFlag.ThirdPersonOnly:
                AddMaterialOverride(renderer, RenderingContext.UserView, invisibleMaterial);
                break;

            case VrmFirstPersonFlag.FirstPersonOnly:
                foreach (RenderingContext context in ThirdPersonRenderContexts)
                {
                    AddMaterialOverride(renderer, context, invisibleMaterial);
                }
                break;
        }
    }

    private static IEnumerable<MeshRenderer> ResolveAnnotatedRenderers(VrmModel vrm,
        Dictionary<string, Slot> slotsByName, VrmFirstPersonMeshAnnotation annotation)
    {
        var seen = new HashSet<MeshRenderer>();
        foreach (Slot slot in ResolveAnnotatedSlots(vrm, slotsByName, annotation))
        {
            foreach (MeshRenderer renderer in slot.GetComponentsInChildren<MeshRenderer>())
            {
                if (seen.Add(renderer))
                {
                    yield return renderer;
                }
            }
        }

        if (seen.Count == 0)
        {
            string nodeName = annotation.NodeIndex >= 0 ? vrm.GetNodeName(annotation.NodeIndex) : null;
            UniLog.Warning($"VRM FirstPerson対象Rendererが見つかりません: mesh={annotation.MeshIndex}, node={annotation.NodeIndex}, name={nodeName ?? "(none)"}");
        }
    }

    private static IEnumerable<Slot> ResolveAnnotatedSlots(VrmModel vrm,
        Dictionary<string, Slot> slotsByName, VrmFirstPersonMeshAnnotation annotation)
    {
        if (annotation.NodeIndex >= 0)
        {
            string nodeName = vrm.GetNodeName(annotation.NodeIndex);
            if (nodeName != null && slotsByName.TryGetValue(nodeName, out Slot slot))
            {
                yield return slot;
                yield break;
            }
        }

        if (annotation.MeshIndex >= 0 && vrm.MeshToNodes.TryGetValue(annotation.MeshIndex, out List<int> nodes))
        {
            foreach (int nodeIndex in nodes)
            {
                string nodeName = vrm.GetNodeName(nodeIndex);
                if (nodeName != null && slotsByName.TryGetValue(nodeName, out Slot slot))
                {
                    yield return slot;
                }
            }
        }
    }

    private static void ImportAvatarRootIdentification(Slot root)
    {
        if (root.FindChild("Avatar Root Identification") != null)
        {
            return;
        }

        string tempPath = Path.Combine(Path.GetTempPath(),
            "VrmToResonitePackage_AvatarRootIdentification_" + Guid.NewGuid().ToString("N") + ".resonitepackage");
        try
        {
            using (System.IO.Stream resource = typeof(AvatarSetup).Assembly.GetManifestResourceStream(
                       "VrmToResonitePackage.Resources.AvatarRootIdentification.resonitepackage"))
            {
                if (resource == null)
                {
                    UniLog.Warning("Avatar Root Identification resource が見つかりませんでした。VRM FirstPerson の装着判定をスキップします。");
                    return;
                }

                using FileStream file = File.Create(tempPath);
                resource.CopyTo(file);
            }

            using RecordPackage package = RecordPackage.Decode(tempPath);
            SkyFrost.Base.Record record = package.MainRecord;
            if (record == null)
            {
                UniLog.Warning("Avatar Root Identification package に main record がありません。VRM FirstPerson の装着判定をスキップします。");
                return;
            }

            string signature = RecordPackage.GetAssetSignature(new Uri(record.AssetURI));
            using System.IO.Stream asset = package.ReadAsset(signature);
            DataTreeDictionary graph = DataTreeConverter.LoadAuto(asset);
            if (graph == null)
            {
                UniLog.Warning("Avatar Root Identification package の DataTree を読み込めませんでした。VRM FirstPerson の装着判定をスキップします。");
                return;
            }

            Slot packageRoot = root.AddSlot("Avatar Root Identification");
            packageRoot.LoadObject(graph, record);
            packageRoot.ForeachComponentInChildren(delegate(IPackageImportEventReceiver receiver)
            {
                receiver.OnPackageImported();
            }, includeLocal: false, cacheItems: true);
        }
        catch (Exception ex)
        {
            UniLog.Warning($"Avatar Root Identification package の読み込みに失敗しました。VRM FirstPerson の装着判定をスキップします: {ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Temp cleanup is best-effort.
            }
        }
    }

    private static IAssetProvider<FrooxEngine.Material> GetOrCreateInvisibleMaterial(Slot root)
    {
        Slot assetsSlot = root.FindChild("Assets") ?? root.AddSlot("Assets");
        PBS_RimSpecular existing = assetsSlot.GetComponentInChildren((PBS_RimSpecular m) =>
            m.Slot.Name == "Invisible Material");
        if (existing != null)
        {
            return existing;
        }

        Slot materialSlot = assetsSlot.AddSlot("Invisible Material");
        PBS_RimSpecular material = materialSlot.AttachComponent<PBS_RimSpecular>();
        material.AlbedoColor.Value = new colorX(0f, 0f, 0f, 0f);
        material.RimColor.Value = new colorX(0f, 0f, 0f, 0f);
        material.Transparent.Value = true;
        return material;
    }

    private static void AddMaterialOverride(MeshRenderer renderer, RenderingContext context,
        IAssetProvider<FrooxEngine.Material> invisibleMaterial)
    {
        RenderMaterialOverride materialOverride = renderer.Slot.AttachComponent<RenderMaterialOverride>();
        materialOverride.Renderer.Target = renderer;
        materialOverride.Context.Value = context;

        for (int i = 0; i < renderer.Materials.Count; i++)
        {
            RenderMaterialOverride.MaterialOverride entry = materialOverride.Overrides.Add();
            entry.Index.Value = i;
            entry.Material.Target = invisibleMaterial;
        }

        DynamicValueVariableDriver<bool> avatarWorn = renderer.Slot.AttachComponent<DynamicValueVariableDriver<bool>>();
        avatarWorn.VariableName.Value = AvatarWornLocalVariable;
        avatarWorn.DefaultValue.Value = false;
        avatarWorn.Target.Target = materialOverride.EnabledField;
    }
}

/// <summary>Builds a name -> slot index over an imported hierarchy (first occurrence wins).</summary>
internal static class SlotIndex
{
    public static Dictionary<string, Slot> Build(Slot root)
    {
        var index = new Dictionary<string, Slot>();
        void Walk(Slot slot)
        {
            string name = slot.Name ?? "";
            if (!index.ContainsKey(name))
            {
                index[name] = slot;
            }
            foreach (Slot child in slot.Children)
            {
                Walk(child);
            }
        }
        Walk(root);
        return index;
    }
}

/// <summary>
/// Maps a VRM morph-target bind (mesh index + morph index) onto an imported
/// SkinnedMeshRenderer blendshape field, preferring name matches via targetNames
/// and falling back to raw indices.
/// </summary>
internal sealed class BlendshapeResolver
{
    private readonly VrmModel _vrm;
    private readonly List<SkinnedMeshRenderer> _renderers;
    private readonly Dictionary<string, Slot> _slotsByName;

    public BlendshapeResolver(Slot root, VrmModel vrm)
    {
        _vrm = vrm;
        _renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>();
        _slotsByName = SlotIndex.Build(root);
    }

    public IField<float> Resolve(VrmExpressionBind bind)
    {
        return ResolveWithRenderer(bind).field;
    }

    public (SkinnedMeshRenderer skin, IField<float> field) ResolveWithRenderer(VrmExpressionBind bind)
    {
        string targetName = null;
        if (bind.MeshIndex >= 0 && bind.MeshIndex < _vrm.MeshTargetNames.Count)
        {
            List<string> names = _vrm.MeshTargetNames[bind.MeshIndex];
            if (bind.MorphIndex >= 0 && bind.MorphIndex < names.Count && !string.IsNullOrEmpty(names[bind.MorphIndex]))
            {
                targetName = names[bind.MorphIndex];
            }
        }

        foreach (SkinnedMeshRenderer skin in EnumerateCandidates(bind))
        {
            if (targetName != null)
            {
                IField<float> byName = skin.TryGetBlendShape(targetName);
                if (byName != null)
                {
                    return (skin, byName);
                }
            }
        }
        foreach (SkinnedMeshRenderer skin in EnumerateCandidates(bind))
        {
            if (bind.MorphIndex >= 0 && bind.MorphIndex < skin.MeshBlendshapeCount)
            {
                return (skin, skin.BlendShapeWeights.GetElement(bind.MorphIndex));
            }
        }
        UniLog.Warning($"ブレンドシェイプが見つかりません: mesh={bind.MeshIndex}, morph={bind.MorphIndex}, name={targetName ?? "(なし)"}");
        return (null, null);
    }

    private IEnumerable<SkinnedMeshRenderer> EnumerateCandidates(VrmExpressionBind bind)
    {
        // Renderers under slots named like the glTF nodes that reference the mesh come first.
        if (_vrm.MeshToNodes.TryGetValue(bind.MeshIndex, out List<int> nodes))
        {
            foreach (int nodeIndex in nodes)
            {
                string nodeName = _vrm.GetNodeName(nodeIndex);
                if (nodeName != null && _slotsByName.TryGetValue(nodeName, out Slot slot))
                {
                    foreach (SkinnedMeshRenderer skin in slot.GetComponentsInChildren<SkinnedMeshRenderer>())
                    {
                        yield return skin;
                    }
                }
            }
        }
        foreach (SkinnedMeshRenderer skin in _renderers)
        {
            yield return skin;
        }
    }
}
