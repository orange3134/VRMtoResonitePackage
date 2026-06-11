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
    public float? TargetHeight { get; set; }
    public bool FaceTracking { get; set; }
    public bool Protect { get; set; } = true;

    /// <summary>View offset forward from the eye midpoint, in meters. Null = auto (eye-distance scaled).</summary>
    public float? ViewForward { get; set; }

    /// <summary>View offset upward from the eye midpoint, in meters. Null = auto (eye-distance scaled).</summary>
    public float? ViewUp { get; set; }

    /// <summary>AvatarRenderSettings near clip distance. Zero or negative disables the override.</summary>
    public float NearClip { get; set; } = 0.075f;
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

        if (options.TargetHeight.HasValue)
        {
            BoundingBox bounds = root.ComputeBoundingBox();
            float currentHeight = bounds.Size.y;
            if (currentHeight > 0.01f)
            {
                float factor = options.TargetHeight.Value / currentHeight;
                root.LocalScale *= factor;
                UniLog.Log($"身長 {currentHeight:F3}m -> {options.TargetHeight.Value:F3}m にリスケール (x{factor:F3})");
            }
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
        SetupAwayIndicator(root);

        root.GetComponentInChildren((AvatarPoseNode n) => n.Node.Value == BodyNode.Head)?.InstrumentWithViewHeadOverride();
        UniLog.Log("アバターセットアップ完了。");
    }

    // ---------------------------------------------------------------- rig

    private static BipedRig SetupRig(Slot root, VrmModel vrm, Dictionary<string, Slot> slotsByName)
    {
        // The model importer may have already classified the rig heuristically;
        // reuse the component but override every bone with the VRM's exact mapping.
        // Multiple bone hierarchies can each get classified, so drop the extras.
        List<BipedRig> existingRigs = root.GetComponentsInChildren<BipedRig>();
        BipedRig rig = existingRigs.FirstOrDefault();
        for (int i = 1; i < existingRigs.Count; i++)
        {
            existingRigs[i].Destroy();
        }
        if (rig == null)
        {
            Slot rigSlot = root.GetComponentInChildren<Rig>()?.Slot ?? root;
            rig = rigSlot.AttachComponent<BipedRig>();
        }

        int mapped = 0;
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
            mapped++;
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
        float3 back = ComputeBackOfHand(rig, hand, fingers, isRight, modelUp);

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

    private static float3 ComputeBackOfHand(BipedRig rig, Slot hand, float3 fingers, bool isRight, float3 modelUp)
    {
        float3 back = float3.Zero;
        Slot thumb = rig.TryGetBone(isRight ? BodyNode.RightThumb_Metacarpal : BodyNode.LeftThumb_Metacarpal)
                     ?? rig.TryGetBone(isRight ? BodyNode.RightThumb_Proximal : BodyNode.LeftThumb_Proximal);
        if (thumb != null)
        {
            float3 thumbDirection = thumb.GlobalPosition - hand.GlobalPosition;
            if (thumbDirection.Magnitude > 0.0001f)
            {
                thumbDirection = thumbDirection.Normalized;
                back = isRight
                    ? MathX.Cross(thumbDirection, fingers)
                    : MathX.Cross(fingers, thumbDirection);
            }
        }
        if (back.Magnitude < 0.0001f)
        {
            back = modelUp;
        }
        // VRM rest pose is a T-pose with palms down; the back of the hand points roughly up.
        if (MathX.Dot(back, modelUp) < 0f)
        {
            back = -back;
        }
        back -= fingers * MathX.Dot(back, fingers);
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
        IField<float> blinkLeft = ResolveSingleBind(resolver, vrm, "blinkLeft");
        IField<float> blinkRight = ResolveSingleBind(resolver, vrm, "blinkRight");
        IField<float> blinkBoth = ResolveSingleBind(resolver, vrm, "blink");

        if (blinkLeft != null && blinkRight != null)
        {
            EyeLinearDriver.Eye left = linearDriver.Eyes.Add();
            EyeLinearDriver.Eye right = linearDriver.Eyes.Add();
            left.Side.Value = EyeSide.Left;
            right.Side.Value = EyeSide.Right;
            left.OpenCloseTarget.Target = blinkLeft;
            right.OpenCloseTarget.Target = blinkRight;
        }
        else if (blinkBoth != null)
        {
            EyeLinearDriver.Eye combined = linearDriver.Eyes.Add();
            combined.Side.Value = EyeSide.Combined;
            combined.OpenCloseTarget.Target = blinkBoth;
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

    private static IField<float> ResolveSingleBind(BlendshapeResolver resolver, VrmModel vrm, string preset)
    {
        VrmExpression expression = vrm.Expressions.FirstOrDefault(
            e => string.Equals(e.Preset, preset, StringComparison.OrdinalIgnoreCase));
        if (expression == null)
        {
            return null;
        }
        foreach (VrmExpressionBind bind in expression.Binds.OrderByDescending(b => b.Weight))
        {
            IField<float> field = resolver.Resolve(bind);
            if (field != null)
            {
                return field;
            }
        }
        return null;
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

    private static readonly (string preset, Viseme viseme)[] VisemePresets =
    {
        ("aa", Viseme.aa),
        ("ih", Viseme.ih),
        ("ou", Viseme.ou),
        ("ee", Viseme.E),
        ("oh", Viseme.oh),
    };

    /// <summary>
    /// Wires the five VRM vowel expressions directly to Resonite's viseme system.
    /// Resonite's own auto-assignment is bypassed entirely, so only blendshapes the
    /// VRM explicitly declares get linked.
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
        UniLog.Log($"VRMの母音表情からビセームを設定しました ({drivers.Count} ドライバー)。");
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
