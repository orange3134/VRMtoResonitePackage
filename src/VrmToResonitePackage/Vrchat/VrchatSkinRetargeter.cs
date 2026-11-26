using Elements.Assets;
using Elements.Core;
using FrooxEngine;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Composes bind pose corrections from prefab skin binding and subsequent armature merges.</summary>
internal sealed class VrchatSkinRetargeter
{
    private readonly Dictionary<SkinnedMeshRenderer, Dictionary<int, float4x4>> _corrections = new();
    private readonly Dictionary<SkinnedMeshRenderer, HashSet<string>> _unusedBones = new();

    public void ExcludeUnusedBones(SkinnedMeshRenderer renderer, HashSet<string> unused)
        => _unusedBones[renderer] = unused;

    internal static int RemoveUnusedWeights(MeshX mesh, HashSet<string> unused)
    {
        if (!mesh.HasBoneBindings) return 0;
        var excluded = Enumerable.Range(0, mesh.BoneCount).Select(i => unused.Contains(mesh.GetBone(i).Name)).ToArray();
        int changed = 0;
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            var binding = mesh.RawBoneBindings[v];
            bool removed = false;
            for (int i = 0; i < 4; i++)
                if (binding.GetWeight(i) > 0 && excluded[binding.GetBoneIndex(i)])
                {
                    binding.SetBinding(i, -1, 0);
                    removed = true;
                }
            if (!removed || binding.TotalWeight <= 0) continue;
            binding.Sort();
            binding.Normalize();
            mesh.RawBoneBindings[v] = binding;
            changed++;
        }
        return changed;
    }

    public void CorrectBindPose(SkinnedMeshRenderer renderer, int index, float4x4 correction)
    {
        if (!_corrections.TryGetValue(renderer, out var bones))
            _corrections.Add(renderer, bones = new());
        bones[index] = correction * bones.GetValueOrDefault(index, float4x4.Identity);
    }

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
                CorrectBindPose(renderer, i, target.GlobalToLocal * source.LocalToGlobal);
            renderer.Bones[i] = target;
            rewritten++;
        }
        return rewritten;
    }

    public async Task SaveMeshes()
    {
        foreach (var renderer in _corrections.Keys.Union(_unusedBones.Keys))
        {
            // Prefab filtering can remove a renderer after its initial binding.
            if (renderer.IsDestroyed) continue;
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
            var corrections = _corrections.GetValueOrDefault(renderer);
            foreach (var (index, correction) in corrections ?? [])
            {
                if (index >= mesh.BoneCount) continue;
                mesh.GetBone(index).BindPose = correction * mesh.GetBone(index).BindPose;
            }
            int changed = _unusedBones.TryGetValue(renderer, out var unused) ? RemoveUnusedWeights(mesh, unused) : 0;
            if (changed == 0 && corrections == null)
            {
                await default(ToWorld);
                continue;
            }
            Uri uri = await MeshAssetWriter.SaveAsync(renderer.Engine.LocalDB, mesh).ConfigureAwait(false);
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
            UniLog.Log($"Updated skin on {renderer.Slot.Name}: {corrections?.Count ?? 0} bind poses, {changed} vertices with unused influences.");
        }
    }
}
