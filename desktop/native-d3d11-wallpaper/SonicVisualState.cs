using System.Numerics;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
internal struct SonicVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 Uv;

    public SonicVertex(Vector3 position, Vector3 normal, Vector2 uv)
    {
        Position = position;
        Normal = normal;
        Uv = uv;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SonicConstants
{
    public Matrix4x4 ViewProjection;
    public Vector4 AudioA;
    public Vector4 AudioB;
    public Vector4 AudioC;
    public Vector4 Params;
    public Vector4 Runtime;
    public Vector4 Primary;
    public Vector4 Secondary;
    public Vector4 Highlight;
    public Vector4 Tone;
    public Vector4 SonicTexture;
    public Vector4 Motion;
    public Vector4 BeatEvent;
    public Vector4 BeatTiming;
    public Vector4 Ripple0;
    public Vector4 Ripple1;
    public Vector4 Ripple2;
    public Vector4 Ripple3;
    public Vector4 Ripple4;
    public Vector4 Ripple5;
    public Vector4 Ripple6;
    public Vector4 Ripple7;
    public Vector4 Ripple8;
    public Vector4 Ripple9;
    public Vector4 Meteor0;
    public Vector4 Meteor1;
    public Vector4 Meteor2;
    public Vector4 Meteor3;
    public Vector4 Meteor4;
    public Vector4 Meteor5;
    public Vector4 Meteor6;
    public Vector4 Meteor7;
    public Vector4 Meteor8;
    public Vector4 Meteor9;
    public Vector4 Meteor10;
    public Vector4 Meteor11;
    public Vector4 Meteor12;
    public Vector4 Meteor13;
    public Vector4 Meteor14;
    public Vector4 Meteor15;
    public Vector4 Meteor16;
    public Vector4 Meteor17;
    public Vector4 Meteor18;
    public Vector4 Meteor19;
    public Vector4 Spark0;
    public Vector4 Spark1;
    public Vector4 Spark2;
    public Vector4 Spark3;
    public Vector4 Spark4;
    public Vector4 Spark5;
    public Vector4 Spark6;
    public Vector4 Spark7;
    public Vector4 Spark8;
    public Vector4 Spark9;
    public Vector4 Spark10;
    public Vector4 Spark11;
    public Vector4 Spark12;
    public Vector4 Spark13;
    public Vector4 Spark14;
    public Vector4 Spark15;
    public Vector4 Spark16;
    public Vector4 Spark17;
    public Vector4 Spark18;
    public Vector4 Spark19;
    public Vector4 Spark20;
    public Vector4 Spark21;
    public Vector4 Spark22;
    public Vector4 Spark23;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SonicBloomConstants
{
    public Vector4 SourceSize;
    public Vector4 TargetSize;
    public Vector4 Params;
    public Vector4 Tint;
}

internal struct NativeRipple
{
    public Vector2 Position;
    public float Age;
    public float Strength;
    public float Type;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SonicMeteor
{
    public bool Active;
    public float X;
    public float Y;
    public float Z;
    public float Vx;
    public float Vz;
    public float Speed;
    public float Strength;
    public float Age;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SonicImpactSpark
{
    public bool Active;
    public float X;
    public float Y;
    public float Z;
    public float Vx;
    public float Vy;
    public float Vz;
    public float Age;
    public float Life;
    public float Strength;
}
