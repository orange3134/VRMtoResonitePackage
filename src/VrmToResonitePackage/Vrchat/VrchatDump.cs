using Elements.Core;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>
/// Diagnostic (engine-independent): extracts a .unitypackage, parses the primary VRChat avatar
/// and prints the humanoid map, visemes, blink, PhysBones and material assignments. Lets the
/// parsing layer be verified without booting FrooxEngine.
/// </summary>
internal static class VrchatDump
{
    public static int Dump(string packagePath, string avatarOverride)
    {
        Console.WriteLine($"### VRChat dump: {packagePath}");
        void Log(object m) => Console.WriteLine(m);
        UniLog.OnLog += Log;
        UniLog.OnWarning += Log;
        UniLog.OnError += Log;
        try
        {
            using UnityPackage package = UnityPackage.Extract(packagePath);
            Console.WriteLine($"アセット数: {package.Assets.Count}  " +
                              $"(prefab {package.ByExtension(".prefab").Count()}, " +
                              $"fbx {package.ByExtension(".fbx").Count()}, " +
                              $"mat {package.ByExtension(".mat").Count()}, " +
                              $"png {package.ByExtension(".png").Count()})");

            Console.WriteLine();
            Console.WriteLine("候補診断:");
            VrchatAvatarParser.DiagnoseCandidates(package);

            VrchatAvatar avatar = VrchatAvatarParser.Parse(package, avatarOverride);

            Console.WriteLine();
            Console.WriteLine($"アバター: {avatar.Name}");
            Console.WriteLine($"prefab : {avatar.PrefabPath}");
            Console.WriteLine($"FBX    : {Path.GetFileName(avatar.FbxPath)} (guid {avatar.FbxGuid})");
            Console.WriteLine($"FBX scale: {avatar.FbxImportScale:G6}");
            Console.WriteLine($"FBXマテリアル対応: {avatar.FbxMaterialGuids.Count}");
            Console.WriteLine($"視点    : {(avatar.ViewPosition.HasValue ? avatar.ViewPosition.Value.ToString() : "(なし)")}");

            Console.WriteLine();
            Console.WriteLine($"ヒューマノイドボーン ({avatar.HumanBones.Count}):");
            foreach ((string vrm, string bone) in avatar.HumanBones.OrderBy(k => k.Key))
            {
                Console.WriteLine($"  {vrm,-26} -> {bone}");
            }

            Console.WriteLine();
            Console.WriteLine($"ビセーム ({avatar.Visemes.Count}):");
            foreach (VrchatViseme v in avatar.Visemes)
            {
                Console.WriteLine($"  {v.ResonitePreset,-4} -> '{v.BlendShapeName}' on {v.MeshGameObjectName}");
            }

            Console.WriteLine();
            Console.WriteLine(avatar.Blink != null
                ? $"瞬き: blendshape index {avatar.Blink.BlendShapeIndex} on {avatar.Blink.MeshGameObjectName}"
                : "瞬き: (なし)");
            Console.WriteLine($"目ボーン: L={avatar.LeftEyeBoneName ?? "-"}, R={avatar.RightEyeBoneName ?? "-"}");

            Console.WriteLine();
            Console.WriteLine($"PhysBone ({avatar.PhysBones.Count}):");
            foreach (VrchatPhysBone pb in avatar.PhysBones)
            {
                Console.WriteLine($"  root={pb.RootBoneName} pull={pb.Pull} spring={pb.Spring} " +
                                  $"stiffness={pb.Stiffness} gravity={pb.Gravity} immobile={pb.Immobile} " +
                                  $"radius={pb.Radius} colliders={pb.Colliders.Count} ignore={pb.IgnoreBoneNames.Count}");
            }

            Console.WriteLine();
            Console.WriteLine($"マテリアル割当 ({avatar.RendererMaterials.Count} renderer):");
            foreach (VrchatRendererMaterials rm in avatar.RendererMaterials)
            {
                string mats = string.Join(", ", rm.MaterialGuids.Select(g =>
                {
                    UnityAsset a = package.ByGuid(g);
                    return a != null ? Path.GetFileNameWithoutExtension(a.LogicalPath) : (g ?? "(none)");
                }));
                Console.WriteLine($"  {rm.RendererGameObjectName}: [{mats}]");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"VRChat dump 失敗: {ex}");
            return 1;
        }
        finally
        {
            UniLog.OnLog -= Log;
            UniLog.OnWarning -= Log;
            UniLog.OnError -= Log;
        }
    }
}
