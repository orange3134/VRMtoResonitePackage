using Elements.Assets;
using Elements.Core;
using FrooxEngine;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Preserves skin deformation when Merge Armature replaces a bone's coordinate frame.</summary>
internal sealed class VrchatSkinRetargeter
{
    private readonly Dictionary<SkinnedMeshRenderer, Dictionary<int, float4x4>> _corrections = new();

    public int Retarget(Slot root, IReadOnlyDictionary<Slot, Slot> mappings)
    {
        int rewritten = 0;
        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
        for (int i = 0; i < renderer.Bones.Count; i++)
        {
            Slot source = renderer.Bones[i];
            if (source == null || !mappings.TryGetValue(source, out Slot target)) continue;
            // Modular Avatar's MeshRetargeter uses destination^-1 * source * bindPose.
            // Capture before any source is moved/destroyed; compose successive replacements
            // in order and clone each renderer's mesh only once after all merges.
            if (renderer.Mesh.Target != null)
            {
                if (!_corrections.TryGetValue(renderer, out var bones))
                    _corrections.Add(renderer, bones = new());
                float4x4 correction = target.GlobalToLocal * source.LocalToGlobal;
                bones[i] = correction * bones.GetValueOrDefault(i, float4x4.Identity);
            }
            renderer.Bones[i] = target;
            rewritten++;
        }
        return rewritten;
    }

    public async Task SaveMeshes()
    {
        foreach (var (renderer, corrections) in _corrections)
        {
            var asset = renderer.Mesh.Asset;
            if (asset?.Data == null)
                throw new InvalidDataException($"Cannot retarget unloaded mesh: {renderer.Slot.Name}");
            Slot providerSlot = renderer.Mesh.Target is Component component ? component.Slot : renderer.Slot;
            float[] weights = renderer.BlendShapeWeights.ToArray();
            await default(ToBackground);
            MeshX mesh;
            object readLock = new();
            await asset.RequestReadLock(readLock).ConfigureAwait(false);
            try { mesh = new MeshX(asset.Data); }
            finally { asset.ReleaseReadLock(readLock); }
            foreach (var (index, correction) in corrections)
            {
                if (index >= mesh.BoneCount) continue;
                mesh.GetBone(index).BindPose = correction * mesh.GetBone(index).BindPose;
            }
            Uri uri = await renderer.Engine.LocalDB.SaveAssetAsync(mesh).ConfigureAwait(false);
            await default(ToWorld);
            if (uri == null) throw new IOException($"Failed to save retargeted mesh: {renderer.Slot.Name}");
            // Source providers can be shared by different occurrences of the same clothing.
            // Give this renderer its own provider instead of changing the shared URL.
            var provider = providerSlot.AttachComponent<StaticMesh>();
            provider.URL.Value = uri;
            renderer.Mesh.Target = provider;
            DateTime deadline = DateTime.UtcNow.AddMinutes(2);
            while (provider.Asset?.Data == null || !provider.IsAssetAvailable)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"Retargeted mesh did not load: {renderer.Slot.Name}");
                await default(NextUpdate);
            }
            for (int i = 0; i < Math.Min(weights.Length, renderer.BlendShapeWeights.Count); i++)
                renderer.BlendShapeWeights[i] = weights[i];
            UniLog.Log($"Retargeted bind poses on {renderer.Slot.Name}: {corrections.Count} bone(s).");
        }
    }
}
