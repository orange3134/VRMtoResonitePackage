using System.Security.Cryptography;
using System.Text;
using Elements.Assets;

namespace VrmToResonitePackage.Expressions;

public static class ExpressionAnimationConverter
{
    public static string BindingId(ExpressionBinding binding) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding.Key))).ToLowerInvariant()[..24];

    /// <summary>Convert unweighted Unity Hermite curves exactly to cubic Bezier controls.</summary>
    public static AnimX ConvertClip(ExpressionClip clip)
    {
        var animation = new AnimX { Name = clip.Name, GlobalDuration = Math.Max(clip.Duration, 0.001f) };
        foreach (var curve in clip.Curves)
        {
            var track = animation.AddTrack<CurveFloatAnimationTrack>();
            track.Node = "Expression";
            track.Property = BindingId(curve.Binding);
            for (int i = 0; i < curve.Keys.Count; i++)
            {
                var key = curve.Keys[i];
                float left = key.Value, right = key.Value;
                if (i > 0 && float.IsFinite(key.InSlope)) left -= key.InSlope * (key.Time - curve.Keys[i - 1].Time) / 3;
                if (i + 1 < curve.Keys.Count && float.IsFinite(key.OutSlope)) right += key.OutSlope * (curve.Keys[i + 1].Time - key.Time) / 3;
                bool hold = float.IsInfinity(key.OutSlope) || (i + 1 < curve.Keys.Count && float.IsInfinity(curve.Keys[i + 1].InSlope));
                track.InsertKeyFrame(key.Value, key.Time, hold ? KeyframeInterpolation.Hold : KeyframeInterpolation.CubicBezier, left, right);
            }
        }
        return animation;
    }
}
