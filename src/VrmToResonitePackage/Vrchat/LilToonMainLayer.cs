using System.Numerics;

namespace VrmToResonitePackage.Vrchat;

public sealed class LilToonMainLayer
{
    public bool Enabled { get; set; }
    public string TextureGuid { get; set; }
    public int UVMode { get; set; }
    public string MaskGuid { get; set; }
    public Vector4 Color { get; set; } = Vector4.One;
    public Vector2 Scale { get; set; } = Vector2.One;
    public Vector2 Offset { get; set; }
    public float Angle { get; set; }
    public int BlendMode { get; set; }
    public int AlphaMode { get; set; }
    public float Lighting { get; set; } = 1f;
    public string[] UnsupportedFeatures { get; set; } = Array.Empty<string>();
}
