internal sealed partial class D3DParticleRenderer
{
    private const string SonicBloomVertexShaderSource = @"
struct VSOut {
  float4 position : SV_POSITION;
  float2 uv : TEXCOORD0;
};

VSOut main(uint vertexId : SV_VertexID) {
  VSOut output;
  float2 pos = vertexId == 0 ? float2(-1.0, -1.0) : (vertexId == 1 ? float2(-1.0, 3.0) : float2(3.0, -1.0));
  output.position = float4(pos, 0.0, 1.0);
  output.uv = float2(pos.x * 0.5 + 0.5, 0.5 - pos.y * 0.5);
  return output;
}";

    private const string SonicBloomPixelShaderSource = @"
cbuffer BloomConstants : register(b0) {
  float4 SourceSize;
  float4 TargetSize;
  float4 Params;
  float4 Tint;
};

Texture2D uSource : register(t0);
Texture2D uBloomA : register(t1);
Texture2D uBloomB : register(t2);
SamplerState uSampler : register(s0);

struct PSIn {
  float4 position : SV_POSITION;
  float2 uv : TEXCOORD0;
};

float luma(float3 c) {
  return dot(c, float3(0.2126, 0.7152, 0.0722));
}

float3 sampleSafe(Texture2D tex, float2 uv) {
  return tex.Sample(uSampler, saturate(uv)).rgb;
}

float4 main(PSIn input) : SV_TARGET {
  float mode = Params.x;
  float threshold = Params.y;
  float intensity = Params.z;
  float spread = Params.w;
  float2 texel = SourceSize.zw;
  float2 uv = input.uv;

  if (mode < 0.5) {
    float3 c = sampleSafe(uSource, uv);
    float brightness = luma(c);
    float maxChannel = max(max(c.r, c.g), c.b);
    float gate = smoothstep(threshold, threshold + 0.26, brightness);
    float colorGate = smoothstep(threshold * 0.96, threshold + 0.18, maxChannel);
    float coolDominance = saturate(max(c.g, c.b) - c.r * 0.72);
    float coolGate = smoothstep(threshold * 0.58, threshold + 0.06, maxChannel) * smoothstep(0.045, 0.30, coolDominance);
    float warmDominance = saturate(c.r - max(c.g, c.b) * 0.82);
    float warmPenalty = lerp(1.0, 0.34, smoothstep(0.04, 0.36, warmDominance));
    float whiteCore = smoothstep(0.82, 1.18, min(min(c.r, c.g), c.b));
    float eligible = max(max(gate, colorGate * 0.24), coolGate * 0.66);
    float3 hot = c * eligible * max(warmPenalty, whiteCore * 0.82);
    hot = lerp(hot, hot * Tint.rgb * 1.12, saturate(coolGate * 0.48));
    hot *= lerp(float3(1.0, 1.0, 1.0), Tint.rgb, 0.28);
    hot *= lerp(1.65, 3.40, saturate(intensity));
    return float4(hot, max(max(gate, colorGate), coolGate) * 0.92);
  }

  if (mode > 0.5 && mode < 2.5) {
    float2 dir = mode < 1.5 ? float2(texel.x, 0.0) : float2(0.0, texel.y);
    float radius = 2.20 + spread * 4.60;
    float3 c = sampleSafe(uSource, uv) * 0.227027;
    c += sampleSafe(uSource, uv + dir * radius * 1.384615) * 0.316216;
    c += sampleSafe(uSource, uv - dir * radius * 1.384615) * 0.316216;
    c += sampleSafe(uSource, uv + dir * radius * 3.230769) * 0.070270;
    c += sampleSafe(uSource, uv - dir * radius * 3.230769) * 0.070270;
    float a = saturate(luma(c) * 1.70);
    return float4(c, a);
  }

  if (mode > 2.5 && mode < 3.5) {
    float2 o = texel * 0.75;
    float3 c =
      sampleSafe(uSource, uv) * 0.40 +
      sampleSafe(uSource, uv + float2(o.x, o.y)) * 0.15 +
      sampleSafe(uSource, uv + float2(-o.x, o.y)) * 0.15 +
      sampleSafe(uSource, uv + float2(o.x, -o.y)) * 0.15 +
      sampleSafe(uSource, uv + float2(-o.x, -o.y)) * 0.15;
    return float4(c, saturate(luma(c) * 1.48));
  }

  if (mode > 4.5 && mode < 5.5) {
    return uSource.Sample(uSampler, uv);
  }

  float4 scene = uSource.Sample(uSampler, uv);
  float3 halfBloom = uBloomA.Sample(uSampler, uv).rgb;
  float3 quarterBloom = uBloomB.Sample(uSampler, uv).rgb;
  float3 bloom = halfBloom * 0.72 + quarterBloom * 0.44;
  if (mode > 5.5 && mode < 6.5) {
    float3 debugBloom = bloom * intensity * lerp(1.40, 2.60, saturate(intensity * 0.62));
    debugBloom *= lerp(float3(1.0, 1.0, 1.0), Tint.rgb, 0.50);
    return float4(debugBloom, saturate(luma(debugBloom) * 2.10));
  }
  float bloomEnergy = saturate(luma(bloom) * 2.35);
  float bloomAlpha = bloomEnergy * saturate(0.22 + intensity * 0.30);
  bloom *= lerp(float3(1.0, 1.0, 1.0), Tint.rgb, 0.46);
  float3 color = scene.rgb + bloom * intensity * lerp(2.00, 4.80, saturate(intensity * 0.60));
  float alpha = saturate(scene.a + bloomAlpha);
  return float4(color, alpha);
}";

    private const string SonicVertexShaderSource = @"
cbuffer SonicConstants : register(b0) {
  row_major float4x4 ViewProjection;
  float4 AudioA;
  float4 AudioB;
  float4 AudioC;
  float4 Params;
  float4 Runtime;
  float4 Primary;
  float4 Secondary;
  float4 Highlight;
  float4 Tone;
  float4 SonicTexture;
  float4 Motion;
  float4 BeatEvent;
  float4 BeatTiming;
  float4 Ripple0;
  float4 Ripple1;
  float4 Ripple2;
  float4 Ripple3;
  float4 Ripple4;
  float4 Ripple5;
  float4 Ripple6;
  float4 Ripple7;
  float4 Ripple8;
  float4 Ripple9;
  float4 Meteor0;
  float4 Meteor1;
  float4 Meteor2;
  float4 Meteor3;
  float4 Meteor4;
  float4 Meteor5;
  float4 Meteor6;
  float4 Meteor7;
  float4 Meteor8;
  float4 Meteor9;
  float4 Meteor10;
  float4 Meteor11;
  float4 Meteor12;
  float4 Meteor13;
  float4 Meteor14;
  float4 Meteor15;
  float4 Meteor16;
  float4 Meteor17;
  float4 Meteor18;
  float4 Meteor19;
  float4 Spark0;
  float4 Spark1;
  float4 Spark2;
  float4 Spark3;
  float4 Spark4;
  float4 Spark5;
  float4 Spark6;
  float4 Spark7;
  float4 Spark8;
  float4 Spark9;
  float4 Spark10;
  float4 Spark11;
  float4 Spark12;
  float4 Spark13;
  float4 Spark14;
  float4 Spark15;
  float4 Spark16;
  float4 Spark17;
  float4 Spark18;
  float4 Spark19;
  float4 Spark20;
  float4 Spark21;
  float4 Spark22;
  float4 Spark23;
};

Texture2D uCover : register(t0);
SamplerState uCoverSampler : register(s0);

struct VSIn {
  float3 position : POSITION;
  float3 normal : NORMAL;
  float2 uv : UV;
  uint instanceId : SV_InstanceID;
};

struct VSOut {
  float4 position : SV_POSITION;
  float2 uv : TEXCOORD0;
  float3 world : TEXCOORD1;
  float3 normal : TEXCOORD2;
  float height : TEXCOORD3;
  float mask : TEXCOORD4;
  float mode : TEXCOORD5;
};

float hash21(float2 p) {
  p = frac(p * float2(123.34, 345.45));
  p += dot(p, p + 34.345);
  return frac(p.x * p.y);
}

float valueNoise(float2 p) {
  float2 i = floor(p);
  float2 f = frac(p);
  float2 u = f * f * (3.0 - 2.0 * f);
  float a = hash21(i);
  float b = hash21(i + float2(1.0, 0.0));
  float c = hash21(i + float2(0.0, 1.0));
  float d = hash21(i + float2(1.0, 1.0));
  return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

float2 sonicMod289(float2 x) {
  return x - floor(x * (1.0 / 289.0)) * 289.0;
}

float3 sonicMod289(float3 x) {
  return x - floor(x * (1.0 / 289.0)) * 289.0;
}

float3 sonicPermute(float3 x) {
  return sonicMod289(((x * 34.0) + 1.0) * x);
}

float sonicSimplex(float2 v) {
  const float4 C = float4(
    0.211324865405187,
    0.366025403784439,
    -0.577350269189626,
    0.024390243902439
  );
  float2 i = floor(v + dot(v, C.yy));
  float2 x0 = v - i + dot(i, C.xx);
  float2 i1 = x0.x > x0.y ? float2(1.0, 0.0) : float2(0.0, 1.0);
  float4 x12 = x0.xyxy + C.xxzz;
  x12.xy -= i1;
  i = sonicMod289(i);
  float3 p = sonicPermute(
    sonicPermute(i.y + float3(0.0, i1.y, 1.0))
    + i.x + float3(0.0, i1.x, 1.0)
  );
  float3 m = max(
    0.5 - float3(dot(x0, x0), dot(x12.xy, x12.xy), dot(x12.zw, x12.zw)),
    0.0
  );
  m = m * m;
  m = m * m;
  float3 x = 2.0 * frac(p * C.www) - 1.0;
  float3 h = abs(x) - 0.5;
  float3 ox = floor(x + 0.5);
  float3 a0 = x - ox;
  m *= 1.79284291400159 - 0.85373472095314 * (a0 * a0 + h * h);
  float3 gradient;
  gradient.x = a0.x * x0.x + h.x * x0.y;
  gradient.yz = a0.yz * x12.xz + h.yz * x12.yw;
  return 130.0 * dot(m, gradient);
}

float sonicRandom(float2 p) {
  return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453123);
}

float2 rotate2D(float2 p, float angle) {
  float c = cos(angle);
  float s = sin(angle);
  return float2(p.x * c - p.y * s, p.x * s + p.y * c);
}

float rippleAt(float2 xz, float4 ripple) {
  if (ripple.z < 0.0 || ripple.w <= 0.001) return 0.0;
  float rippleType = floor(max(0.0, ripple.z) / 10.0 + 0.001);
  float age = ripple.z - rippleType * 10.0;
  float sharpType = saturate(min(rippleType, 1.0));
  float meteorType = step(1.5, rippleType);
  float fromCenter = length(xz - ripple.xy);
  float speed = lerp(1.18 + Runtime.z * 0.18, 1.58 + Runtime.z * 0.24, sharpType);
  speed = lerp(speed, 1.38 + Runtime.z * 0.22, meteorType);
  float waveRadius = age * speed;
  float width = lerp(Params.z * 1.35, 0.045 + age * 0.026, sharpType);
  width = lerp(width, 0.070 + age * 0.030, meteorType);
  float dist = abs(fromCenter - waveRadius);
  float gaussianBand = exp(-(dist * dist) / max(0.0008, width * width));
  float threeCellBand = 1.0 - smoothstep(Params.z * 1.10, Params.z * 1.50, dist);
  float band = lerp(threeCellBand, gaussianBand, saturate(sharpType + meteorType));
  float innerDistance = max(0.0, waveRadius - fromCenter);
  float innerWake = exp(-innerDistance * lerp(6.2, 4.2, sharpType)) * step(fromCenter, waveRadius);
  float outerWake = exp(-abs(fromCenter - waveRadius * lerp(1.10, 1.08, sharpType)) * lerp(6.0, 4.4, sharpType)) * lerp(0.045, 0.035, sharpType);
  float fade = saturate(1.0 - age / lerp(4.05, 2.00, sharpType));
  fade = lerp(fade, saturate(1.0 - age / 1.375), meteorType);
  float elevationScale = lerp(0.54, 0.48, sharpType);
  elevationScale = lerp(elevationScale, 0.78, meteorType);
  float wake = (innerWake * lerp(0.035, 0.022, sharpType) + outerWake) * saturate(sharpType + meteorType);
  return (band * lerp(0.82, 0.86, sharpType) + wake) * ripple.w * fade * elevationScale;
}

float4 meteorAt(uint index) {
  if (index == 0) return Meteor0;
  if (index == 1) return Meteor1;
  if (index == 2) return Meteor2;
  if (index == 3) return Meteor3;
  if (index == 4) return Meteor4;
  if (index == 5) return Meteor5;
  if (index == 6) return Meteor6;
  if (index == 7) return Meteor7;
  if (index == 8) return Meteor8;
  if (index == 9) return Meteor9;
  if (index == 10) return Meteor10;
  if (index == 11) return Meteor11;
  if (index == 12) return Meteor12;
  if (index == 13) return Meteor13;
  if (index == 14) return Meteor14;
  if (index == 15) return Meteor15;
  if (index == 16) return Meteor16;
  if (index == 17) return Meteor17;
  if (index == 18) return Meteor18;
  return Meteor19;
}

float4 sparkAt(uint index) {
  if (index == 0) return Spark0;
  if (index == 1) return Spark1;
  if (index == 2) return Spark2;
  if (index == 3) return Spark3;
  if (index == 4) return Spark4;
  if (index == 5) return Spark5;
  if (index == 6) return Spark6;
  if (index == 7) return Spark7;
  if (index == 8) return Spark8;
  if (index == 9) return Spark9;
  if (index == 10) return Spark10;
  if (index == 11) return Spark11;
  if (index == 12) return Spark12;
  if (index == 13) return Spark13;
  if (index == 14) return Spark14;
  if (index == 15) return Spark15;
  if (index == 16) return Spark16;
  if (index == 17) return Spark17;
  if (index == 18) return Spark18;
  if (index == 19) return Spark19;
  if (index == 20) return Spark20;
  if (index == 21) return Spark21;
  if (index == 22) return Spark22;
  return Spark23;
}

VSOut main(VSIn input) {
  VSOut output;
  float mode = Params.w;
  output.uv = input.uv;
  output.normal = input.normal;
  output.mode = mode;
  output.mask = 1.0;
  output.height = 0.0;

  if (mode > 0.5 && mode < 7.5) {
    if (mode > 6.5) {
      float4 spark = sparkAt(input.instanceId);
      float strength = spark.w;
      float seed = hash21(float2((float)input.instanceId * 0.731, spark.x + spark.z));
      float seed2 = hash21(float2(seed * 9.37, (float)input.instanceId * 1.927));
      float3 local = input.position - float3(0.0, 0.5, 0.0);
      float spin = Runtime.x * (4.6 + seed * 4.0) + seed * 6.2831853;
      float ca = cos(spin);
      float sa = sin(spin);
      float3 rotated = float3(local.x * ca - local.z * sa, local.y, local.x * sa + local.z * ca);
      float scale = (0.028 + seed * 0.045) * (0.45 + strength * 1.05);
      float3 world = spark.xyz + rotated * scale;
      output.world = world;
      output.normal = input.normal;
      output.height = strength + seed2 * 0.18;
      output.mask = strength;
      output.position = mul(float4(world, 1.0), ViewProjection);
      return output;
    }
    if (mode > 5.5) {
      float2 p = input.uv * 2.0 - 1.0;
      float rad = length(p);
      float lift = -0.985 + saturate(AudioA.w * 0.18 + AudioC.y * 0.10) * 0.045;
      float scale = 1.012 + saturate(AudioA.z + AudioC.z) * 0.010;
      float3 world = float3(input.position.x * scale, lift, input.position.z * scale);
      output.world = world;
      output.normal = float3(0.0, 1.0, 0.0);
      output.height = saturate(AudioA.w * 0.70 + AudioA.z * 0.24 + AudioC.w * 0.20);
      output.mask = 1.0 - smoothstep(1.02, 1.08, rad);
      output.position = mul(float4(world, 1.0), ViewProjection);
      return output;
    }
    if (mode > 4.5) {
      uint meteorIndex = input.instanceId / 10;
      uint trailIndex = input.instanceId - meteorIndex * 10;
      float4 meteor = meteorAt(meteorIndex);
      float strength = meteor.w;
      float sub = (float)trailIndex;
      float seed = hash21(float2((float)input.instanceId * 0.171, (float)meteorIndex * 3.113));
      float seed2 = hash21(float2((float)input.instanceId * 1.971, seed * 7.317));
      float seed3 = hash21(float2(seed * 9.13, seed2 * 5.71));
      float3 local = input.position - float3(0.0, 0.5, 0.0);
      float trailT = sub / 9.0;
      float3 fallDir = normalize(float3(-meteor.x * 0.20, -0.10, -meteor.z * 0.20));
      float3 sideDir = normalize(cross(float3(0.0, 1.0, 0.0), fallDir));
      float3 liftDir = normalize(cross(fallDir, sideDir));
      float spiral = Runtime.x * (3.8 + seed * 2.2) + sub * 2.399963;
      float spread = (0.018 + seed2 * 0.060) * (0.72 + trailT * 1.20);
      float tailLen = 0.62 + strength * 1.02;
      float3 offset = -fallDir * (trailT * tailLen)
        + sideDir * (cos(spiral) * spread)
        + liftDir * (sin(spiral) * spread + seed3 * 0.050);
      float drift = sin(Runtime.x * (5.0 + seed * 3.0) + sub) * 0.026;
      float scale = (0.008 + seed * 0.014) * (1.0 - trailT * 0.58) * (0.52 + strength * 0.92);
      float3 world = float3(meteor.x + drift, meteor.y, meteor.z - drift * 0.45) + offset + local * scale;
      output.world = world;
      output.normal = input.normal;
      output.height = strength * (1.0 - trailT * 0.66) + seed * 0.20;
      output.mask = strength * (1.0 - trailT) * (0.46 + seed2 * 0.54);
      output.position = mul(float4(world, 1.0), ViewProjection);
      return output;
    }
    if (mode > 3.5) {
      float id = (float)input.instanceId;
      float seed = hash21(float2(id * 0.137, id * 1.713));
      float seed2 = hash21(float2(id * 2.371, id * 0.617));
      float prominent = smoothstep(0.72, 0.96, seed2);
      float angle = id * 2.399963 + sin(id * 12.9898) * 0.34;
      float radius = 2.75 + seed * 2.35;
      float3 local = input.position - float3(0.0, 0.5, 0.0);
      float pulse = saturate(AudioA.w * 0.68 + AudioA.z * 0.28 + AudioC.y * 0.30 + AudioC.z * 0.16);
      float kickPulse = smoothstep(0.26, 0.82, AudioA.w);
      float bob = sin(Runtime.x * (0.62 + seed * 0.74 + kickPulse * 0.18) + id * 0.73) * (0.10 + pulse * 0.17);
      float blockScale = (0.026 + seed2 * 0.048 + prominent * 0.060) * (0.56 + pulse * 0.70 + kickPulse * 0.22);
      float3 center = float3(cos(angle) * radius, 0.46 + seed * 1.42 + bob * 0.72 + pulse * 0.32 + kickPulse * (0.18 + seed2 * 0.20), sin(angle) * radius);
      float spinA = Runtime.x * (0.45 + seed * 0.62 + kickPulse * 0.54) + id;
      float ca = cos(spinA);
      float sa = sin(spinA);
      float3 rotated = float3(local.x * ca - local.z * sa, local.y, local.x * sa + local.z * ca);
      float3 world = center + rotated * blockScale;
      output.world = world;
      output.normal = input.normal;
      output.height = pulse * 0.68 + kickPulse * 0.34 + seed * 0.20;
      output.mask = (0.08 + pulse * 0.40 + kickPulse * 0.18) * lerp(0.38, 1.0, prominent);
      output.position = mul(float4(world, 1.0), ViewProjection);
      return output;
    }
    if (mode > 2.5) {
      float4 meteor = meteorAt(input.instanceId);
      float strength = meteor.w;
      float3 local = input.position - float3(0.0, 0.5, 0.0);
      float3 fallDir = normalize(float3(-meteor.x * 0.20, -0.10, -meteor.z * 0.20));
      float3 sideDir = normalize(cross(float3(0.0, 1.0, 0.0), fallDir));
      float3 liftDir = normalize(cross(fallDir, sideDir));
      float trailLen = 1.65 + strength * 1.24;
      float trailWidth = 0.012 + strength * 0.008;
      float3 world = float3(meteor.x, meteor.y + 0.30 + strength * 0.16, meteor.z)
        + fallDir * (local.y * trailLen)
        + sideDir * (local.x * trailWidth)
        + liftDir * (local.z * trailWidth);
      output.world = world;
      output.normal = input.normal;
      output.height = strength * 0.62;
      output.mask = strength * 0.26;
      output.position = mul(float4(world, 1.0), ViewProjection);
      return output;
    }
    if (mode > 1.5) {
      float4 meteor = meteorAt(input.instanceId);
      float strength = meteor.w;
      float3 local = input.position - float3(0.0, 0.5, 0.0);
      float bodyScale = 0.025 + strength * 0.035;
      float3 world = float3(meteor.x, meteor.y, meteor.z) + float3(local.x * bodyScale, local.y * (0.16 + strength * 0.22), local.z * bodyScale);
      output.world = world;
      output.normal = input.normal;
      output.height = strength;
      output.mask = strength;
      output.position = mul(float4(world, 1.0), ViewProjection);
      return output;
    }
    output.world = input.position;
    output.position = mul(float4(input.position, 1.0), ViewProjection);
    return output;
  }

  uint gridU = (uint)Params.x;
  float grid = Params.x;
  uint ix = input.instanceId % gridU;
  uint iz = input.instanceId / gridU;
  float2 cellUv = float2((float)ix, (float)iz) / max(1.0, grid - 1.0);
  float2 g = cellUv * 2.0 - 1.0;
  float radius = Params.y;
  float cell = Params.z;
  float terrainGlowShell = mode > 7.5 ? 1.0 : 0.0;
  float terrainCapBloom = mode > 8.5 ? 1.0 : 0.0;
  cell *= lerp(1.0, 1.08, terrainGlowShell);
  cell *= lerp(1.0, 1.04, terrainCapBloom);
  float2 sampleXz = g * radius;
  float2 xz = rotate2D(sampleXz, Motion.x);
  float radial = length(g);
  float mask = 1.0 - smoothstep(0.925, 0.995, radial);
  float edgeColumnScale = lerp(0.42, 1.0, smoothstep(0.025, 0.22, mask));
  cell *= edgeColumnScale;
  // Keep the cover label flat and readable. Terrain starts outside its dark spacer ring.
  float centerReveal = 1.0 - smoothstep(0.350, 0.410, radial);
  float terrainDriveMask = smoothstep(0.390, 0.445, radial);
  cell *= lerp(0.62, 1.0, terrainDriveMask);
  float angle = atan2(sampleXz.y, sampleXz.x);
  float t = Runtime.x * (0.18 + Runtime.z * 0.10);
  float idle =
    sin(dot(sampleXz, float2(1.85, 1.22)) + t * 1.2) * 0.5 +
    sin(length(sampleXz) * 3.45 - t * 1.4) * 0.35 +
    (hash21(cellUv * 91.7) - 0.5) * 0.45;
  idle = idle * 0.5 + 0.5;
  float lowDrive = saturate(AudioA.x);
  float midDrive = saturate(AudioA.y);
  float highDrive = saturate(AudioA.z);
  float kick = AudioA.w;
  float kickFollow = saturate(kick * 0.46);
  float subBass = saturate(AudioB.x * 0.82 + kickFollow * 0.24);
  float bassBody = saturate(AudioB.y * 0.78 + kickFollow * 0.20);
  float lowMid = saturate(AudioB.z);
  float midFlow = saturate(AudioB.w);
  float highMid = saturate(AudioC.x);
  float presence = saturate(AudioC.y);
  float brilliance = saturate(AudioC.z);
  float air = saturate(AudioC.w);
  float energy = saturate(subBass * 0.24 + bassBody * 0.22 + lowMid * 0.16 + midFlow * 0.18 + highMid * 0.13 + presence * 0.16 + brilliance * 0.12 + air * 0.08 + kick * 0.30);
  float beatEnvelope = saturate(BeatEvent.x);
  float beatStrength = saturate(BeatEvent.y);
  float beatLane = BeatEvent.z;
  float beatCombo = BeatEvent.w;
  float beatImpact = saturate(BeatTiming.w);
  float downbeatEvent = 1.0 - step(0.5, abs(beatCombo - 1.0));
  float dropEvent = 1.0 - step(0.5, abs(beatCombo - 2.0));
  float accentEvent = 1.0 - step(0.5, abs(beatCombo - 3.0));
  float heavyBeat = saturate(downbeatEvent + dropEvent);
  float musicActive = smoothstep(0.050, 0.260, energy + kick * 0.18);
  float hardDiscMask = radial <= 1.0 ? 1.0 : 0.0;
  mask = lerp(hardDiscMask, mask, musicActive);
  float density = saturate(Tone.z * 0.48 + highDrive * 0.08 + highMid * 0.08 + presence * 0.10 + brilliance * 0.10 + 0.18);
  float annularCore = smoothstep(0.30, 0.42, radial) * (1.0 - smoothstep(0.78, 0.90, radial));
  float centerDist = length(sampleXz);
  float2 coverUv = saturate(sampleXz / max(0.001, radius * 2.0) + 0.5);
  float3 coverSample = uCover.SampleLevel(uCoverSampler, coverUv, 0).rgb;
  float coverLum = dot(coverSample, float3(0.2126, 0.7152, 0.0722));
  float coverDetail = saturate(abs(coverLum - 0.50) * 2.0);
  float coverCenterMask = (1.0 - smoothstep(radius * 0.05, radius * 0.34, centerDist)) * saturate(SonicTexture.x);
  float coverFaceRidge = smoothstep(0.30, 0.78, coverLum) * (0.55 + coverDetail * 0.45);
  float coverDarkCut = smoothstep(0.20, 0.56, 1.0 - coverLum) * coverDetail;
  float coverDrivenLift = 0.0;
  // Preserve the original Cartesian terrain domain. Moving its low-frequency
  // focus into the playable annulus avoids turning the flat center label into
  // a perfectly concentric bass ring.
  float2 sonicPos = sampleXz / max(0.001, radius) * 60.0;
  float sonicCenterDist = length(sonicPos);
  float sonicRegionDist = length(sonicPos - float2(-22.0, 17.0));
  float sonicTime = Runtime.x;
  float sonicRnd = sonicRandom(sonicPos);

  // Original Sonic Topography idle background.
  float2 movingPos = sonicPos * 0.05 + float2(sonicTime * 0.10, sonicTime * 0.05);
  float baseNoise = (sonicSimplex(movingPos) + 1.0) * 0.5;
  float baseWave = sin(sonicPos.x * 0.15 + sonicPos.y * 0.10 - sonicTime * 0.60) * 0.5 + 0.5;
  float sonicSmoothness = saturate(1.0 - Tone.z * 0.72);
  float globalFalloff = 1.0 - smoothstep(30.0, 60.0, sonicCenterDist);
  float idleElevation = lerp(baseNoise, baseWave, sonicSmoothness * 0.5 + 0.2) * 0.8 * globalFalloff;

  // Original five frequency-region displacements, in the same order and with
  // the same spatial frequencies, gates, and relative amplitudes as the GLSL.
  float subRegion = 1.0 - smoothstep(0.0, 25.0, sonicRegionDist);
  float subLift = subBass * subRegion * 5.0;

  float bassNoise = sonicSimplex(sonicPos * 0.10 - float2(0.0, sonicTime * 0.20));
  float bassRegion = 1.0 - smoothstep(5.0, 35.0, sonicRegionDist + bassNoise * 5.0);
  float bassLift = bassBody * bassRegion * smoothstep(0.0, 1.0, sonicRnd + density * 0.5) * 4.0;

  float lowMidNoise = sonicSimplex(sonicPos * 0.05 + float2(sonicTime * 0.10, 0.0));
  float lowMidLift = lowMid * (lowMidNoise * 0.5 + 0.5) * 2.5;

  float riverFlow = sin(
    sonicPos.x * 0.20 + sonicPos.y * 0.20
    + sonicSimplex(sonicPos * 0.10) * 2.0
    - sonicTime * 2.0
  );
  float midLift = midFlow * max(0.0, riverFlow) * 3.0;

  float highMidRegion = smoothstep(10.0, 45.0, sonicRegionDist);
  float highMidLift = 0.0;
  if (frac(sonicRnd * 13.3) > 0.8) {
    highMidLift = highMid * highMidRegion * frac(sonicRnd * 7.7) * 2.5;
  }

  float audioElevation = subLift + bassLift + lowMidLift + midLift + highMidLift;
  // Beat events modulate broad terrain patches instead of spawning another
  // ring. This preserves Sonic Topography's kick-following column motion while
  // the explicit blue ripple remains a rare downbeat/drop accent.
  float2 beatOffset = beatLane < 0.5 ? float2(-0.65, 0.35)
    : (beatLane < 1.5 ? float2(0.45, 0.58)
    : (beatLane < 2.5 ? float2(0.62, -0.42) : float2(-0.35, -0.62)));
  float beatPatchNoise = sonicSimplex(sonicPos * 0.055 + beatOffset * 2.4);
  float beatPatch = smoothstep(-0.18, 0.58, beatPatchNoise)
    * smoothstep(0.36, 0.46, radial)
    * (1.0 - smoothstep(0.82, 0.96, radial));
  float beatLift = beatEnvelope
    * (0.72 + beatStrength * 1.18 + beatImpact * 0.42 + heavyBeat * 0.46)
    * beatPatch
    * (0.72 + subBass * 0.18 + bassBody * 0.16);
  audioElevation += beatLift;
  if (sonicRnd > 0.99) {
    audioElevation += energy * 5.0;
  }
  audioElevation *= globalFalloff;
  audioElevation = max(0.0, audioElevation - 0.2);

  // 0.12 converts the original Three.js terrain units into this renderer's
  // record scale; it does not alter the relative motion formula.
  float sonicMovementLift = (idleElevation + audioElevation) * 0.12;
  float ripple =
    rippleAt(xz, Ripple0) + rippleAt(xz, Ripple1) + rippleAt(xz, Ripple2) + rippleAt(xz, Ripple3) + rippleAt(xz, Ripple4) +
    rippleAt(xz, Ripple5) + rippleAt(xz, Ripple6) + rippleAt(xz, Ripple7) + rippleAt(xz, Ripple8) + rippleAt(xz, Ripple9);
  float grooveRidge = pow(saturate(1.0 - abs(frac(radial * 54.0) - 0.5) * 2.0), 14.0);
  float fineGroove = pow(saturate(1.0 - abs(frac(radial * 118.0 + hash21(cellUv * 7.0) * 0.05) - 0.5) * 2.0), 20.0);
  float vinylTerrainMask = smoothstep(0.300, 0.382, radial) * (1.0 - smoothstep(0.915, 1.000, radial));
  float outerWallMask = smoothstep(0.905, 0.965, radial) * (1.0 - smoothstep(0.995, 1.022, radial));
  float staticVinylLift = 0.0012 + grooveRidge * 0.0013 + fineGroove * 0.0007 + (hash21(cellUv * 131.0) - 0.5) * 0.00025;
  float liveGrooveLift = (grooveRidge * 0.0070 + fineGroove * 0.0034) * vinylTerrainMask * (0.32 + musicActive * 0.68);
  float liveTerrainLift = (sonicMovementLift + liveGrooveLift) * terrainDriveMask;
  float terrainLift = lerp(staticVinylLift, staticVinylLift + liveTerrainLift, musicActive * terrainDriveMask);
  float rippleRise = ripple * (0.72 + subBass * 0.08 + kick * 0.08 + presence * 0.05);
  float height = (0.008 + terrainLift + rippleRise * 0.40 * musicActive * terrainDriveMask) * Runtime.y;
  height = 1.58 * (1.0 - exp(-height / 1.16));
  height = lerp(min(height, 0.0042 + grooveRidge * 0.0011 + fineGroove * 0.0007), height, musicActive);
  height *= lerp(1.0, 0.98, musicActive);
  height *= terrainDriveMask;
  float outerT = smoothstep(0.62, 0.98, radial);
  height *= 1.0 - outerT * 0.18;
  height *= lerp(1.0, 0.94, vinylTerrainMask * (0.48 + musicActive * 0.22));
  height = min(height, lerp(1.16, 0.82, outerT) + subBass * 0.05 + bassBody * 0.03 + ripple * 0.055);
  height = max(height, outerWallMask * (0.030 + musicActive * (0.026 + kick * 0.006 + ripple * 0.020)));
  mask *= 1.0 - centerReveal;
  height = lerp(0.008, height, mask);
  height *= lerp(1.0, 1.055, terrainGlowShell);
  height *= lerp(1.0, 0.965, terrainCapBloom);
  float3 world = float3(
    xz.x + input.position.x * cell,
    -1.10 + input.position.y * height,
    xz.y + input.position.z * cell
  );

  output.world = world;
  output.height = height;
  output.mask = mask;
  output.position = mul(float4(world, 1.0), ViewProjection);
  return output;
}";

    private const string SonicPixelShaderSource = @"
cbuffer SonicConstants : register(b0) {
  row_major float4x4 ViewProjection;
  float4 AudioA;
  float4 AudioB;
  float4 AudioC;
  float4 Params;
  float4 Runtime;
  float4 Primary;
  float4 Secondary;
  float4 Highlight;
  float4 Tone;
  float4 SonicTexture;
  float4 Motion;
  float4 BeatEvent;
  float4 BeatTiming;
  float4 Ripple0;
  float4 Ripple1;
  float4 Ripple2;
  float4 Ripple3;
  float4 Ripple4;
  float4 Ripple5;
  float4 Ripple6;
  float4 Ripple7;
  float4 Ripple8;
  float4 Ripple9;
  float4 Meteor0;
  float4 Meteor1;
  float4 Meteor2;
  float4 Meteor3;
  float4 Meteor4;
  float4 Meteor5;
  float4 Meteor6;
  float4 Meteor7;
  float4 Meteor8;
  float4 Meteor9;
  float4 Meteor10;
  float4 Meteor11;
  float4 Meteor12;
  float4 Meteor13;
  float4 Meteor14;
  float4 Meteor15;
  float4 Meteor16;
  float4 Meteor17;
  float4 Meteor18;
  float4 Meteor19;
  float4 Spark0;
  float4 Spark1;
  float4 Spark2;
  float4 Spark3;
  float4 Spark4;
  float4 Spark5;
  float4 Spark6;
  float4 Spark7;
  float4 Spark8;
  float4 Spark9;
  float4 Spark10;
  float4 Spark11;
  float4 Spark12;
  float4 Spark13;
  float4 Spark14;
  float4 Spark15;
  float4 Spark16;
  float4 Spark17;
  float4 Spark18;
  float4 Spark19;
  float4 Spark20;
  float4 Spark21;
  float4 Spark22;
  float4 Spark23;
};

Texture2D uCover : register(t0);
SamplerState uCoverSampler : register(s0);

struct PSIn {
  float4 position : SV_POSITION;
  float2 uv : TEXCOORD0;
  float3 world : TEXCOORD1;
  float3 normal : TEXCOORD2;
  float height : TEXCOORD3;
  float mask : TEXCOORD4;
  float mode : TEXCOORD5;
};

float hash21(float2 p) {
  p = frac(p * float2(123.34, 345.45));
  p += dot(p, p + 34.345);
  return frac(p.x * p.y);
}

float valueNoise(float2 p) {
  float2 i = floor(p);
  float2 f = frac(p);
  float2 u = f * f * (3.0 - 2.0 * f);
  float a = hash21(i);
  float b = hash21(i + float2(1.0, 0.0));
  float c = hash21(i + float2(0.0, 1.0));
  float d = hash21(i + float2(1.0, 1.0));
  return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

float2 rotate2D(float2 p, float angle) {
  float c = cos(angle);
  float s = sin(angle);
  return float2(p.x * c - p.y * s, p.x * s + p.y * c);
}

float3 sonicBase1() {
  return float3(0.007, 0.012, 0.020);
}

float3 sonicBase2() {
  return float3(0.018, 0.030, 0.048) + Secondary.rgb * 0.075;
}

float3 sonicGlowColor(float3 world, float seed) {
  float radius = max(0.001, Params.y);
  float centerDist = length(world.xz) / radius;
  float warmBlend = smoothstep(0.0, 1.0, Tone.x * 1.45 + (0.52 - centerDist * 0.58));
  float3 coolCore = lerp(float3(0.00, 0.22, 0.88), Primary.rgb, 0.42);
  float3 coolEdge = lerp(float3(0.60, 0.18, 1.00), Secondary.rgb, 0.46);
  float3 warmCore = lerp(float3(1.00, 0.16, 0.10), Highlight.rgb, 0.36);
  float3 warmEdge = lerp(float3(1.00, 0.56, 0.02), Highlight.rgb, 0.30);
  float3 zoneCore = lerp(coolCore, warmCore, warmBlend);
  float3 zoneEdge = lerp(coolEdge, warmEdge, warmBlend);
  float3 target = lerp(zoneCore, zoneEdge, frac(seed * 11.0));
  float3 brightCool = lerp(coolCore, float3(1.0, 1.0, 1.0), 0.20);
  target = lerp(target, brightCool, saturate(Tone.y) * 0.38);
  return target;
}

float distanceFade(float3 world) {
  return 1.0 - smoothstep(Params.y * 0.70, Params.y * 1.08, length(world.xz));
}

float rippleVisualOne(float2 xz, float4 ripple) {
  if (ripple.z < 0.0 || ripple.w <= 0.001) return 0.0;
  float rippleType = floor(max(0.0, ripple.z) / 10.0 + 0.001);
  float age = ripple.z - rippleType * 10.0;
  float sharpType = saturate(min(rippleType, 1.0));
  float meteorType = step(1.5, rippleType);
  float speed = lerp(1.18 + Runtime.z * 0.18, 1.58 + Runtime.z * 0.24, sharpType);
  speed = lerp(speed, 1.38 + Runtime.z * 0.22, meteorType);
  float waveRadius = age * speed;
  float width = lerp(Params.z * 1.20, 0.032 + age * 0.020, sharpType);
  width = lerp(width, 0.050 + age * 0.024, meteorType);
  float dist = abs(length(xz - ripple.xy) - waveRadius);
  float gaussianBand = exp(-(dist * dist) / max(0.0008, width * width));
  float threeCellBand = 1.0 - smoothstep(Params.z * 1.10, Params.z * 1.50, dist);
  float band = lerp(threeCellBand, gaussianBand, saturate(sharpType + meteorType));
  float fade = saturate(1.0 - age / lerp(4.05, 1.95, sharpType));
  fade = lerp(fade, saturate(1.0 - age / 1.35), meteorType);
  float visualScale = lerp(0.86, 1.28, sharpType);
  visualScale = lerp(visualScale, 1.18, meteorType);
  return band * ripple.w * fade * visualScale;
}

float rippleVisualAt(float2 xz) {
  return saturate(
    rippleVisualOne(xz, Ripple0) + rippleVisualOne(xz, Ripple1) + rippleVisualOne(xz, Ripple2) + rippleVisualOne(xz, Ripple3) + rippleVisualOne(xz, Ripple4) +
    rippleVisualOne(xz, Ripple5) + rippleVisualOne(xz, Ripple6) + rippleVisualOne(xz, Ripple7) + rippleVisualOne(xz, Ripple8) + rippleVisualOne(xz, Ripple9)
  );
}

float rippleMainVisualOne(float2 xz, float4 ripple) {
  if (ripple.z < 0.0) return 0.0;
  float rippleType = floor(max(0.0, ripple.z) / 10.0 + 0.001);
  return rippleType < 1.5 ? rippleVisualOne(xz, ripple) : 0.0;
}

float rippleMeteorVisualOne(float2 xz, float4 ripple) {
  if (ripple.z < 0.0) return 0.0;
  float rippleType = floor(max(0.0, ripple.z) / 10.0 + 0.001);
  return rippleType >= 1.5 ? rippleVisualOne(xz, ripple) : 0.0;
}

float rippleMainVisualAt(float2 xz) {
  return saturate(
    rippleMainVisualOne(xz, Ripple0) + rippleMainVisualOne(xz, Ripple1) + rippleMainVisualOne(xz, Ripple2) + rippleMainVisualOne(xz, Ripple3) + rippleMainVisualOne(xz, Ripple4) +
    rippleMainVisualOne(xz, Ripple5) + rippleMainVisualOne(xz, Ripple6) + rippleMainVisualOne(xz, Ripple7) + rippleMainVisualOne(xz, Ripple8) + rippleMainVisualOne(xz, Ripple9)
  );
}

float rippleMeteorVisualAt(float2 xz) {
  return saturate(
    rippleMeteorVisualOne(xz, Ripple0) + rippleMeteorVisualOne(xz, Ripple1) + rippleMeteorVisualOne(xz, Ripple2) + rippleMeteorVisualOne(xz, Ripple3) + rippleMeteorVisualOne(xz, Ripple4) +
    rippleMeteorVisualOne(xz, Ripple5) + rippleMeteorVisualOne(xz, Ripple6) + rippleMeteorVisualOne(xz, Ripple7) + rippleMeteorVisualOne(xz, Ripple8) + rippleMeteorVisualOne(xz, Ripple9)
  );
}

float sonicEffectActive() {
  float audioEnergy = saturate(AudioA.x * 0.25 + AudioA.y * 0.24 + AudioA.z * 0.20 + AudioA.w * 0.26 + AudioC.y * 0.16 + AudioC.w * 0.10);
  return smoothstep(0.055, 0.285, audioEnergy);
}

float2 coverUvFromWorld(float3 world) {
  float2 local = rotate2D(world.xz, -Motion.x);
  return saturate(local / max(0.001, Params.y * 2.0) + 0.5);
}

float recordRadiusFromWorld(float3 world) {
  return length(world.xz) / max(0.001, Params.y);
}

float recordLabelMask(float recordR) {
  return 1.0 - smoothstep(0.235, 0.330, recordR);
}

float recordVinylMask(float recordR) {
  return smoothstep(0.285, 0.370, recordR) * (1.0 - smoothstep(0.920, 1.020, recordR));
}

float recordOuterRimMask(float recordR) {
  return smoothstep(0.835, 0.965, recordR) * (1.0 - smoothstep(1.000, 1.040, recordR));
}

float circleCoverage(float r, float radius) {
  float aa = max(0.0022, length(float2(ddx(r), ddy(r))) * 2.10);
  return 1.0 - smoothstep(radius - aa, radius + aa, r);
}

float ringCoverage(float r, float innerRadius, float outerRadius) {
  float aa = max(0.0022, length(float2(ddx(r), ddy(r))) * 2.10);
  float outer = 1.0 - smoothstep(outerRadius - aa, outerRadius + aa, r);
  float inner = smoothstep(innerRadius - aa, innerRadius + aa, r);
  return saturate(inner * outer);
}

float4 recordMaterialMasks(float recordR, float useTexture) {
  float label = recordLabelMask(recordR) * useTexture;
  float vinyl = recordVinylMask(recordR) * (1.0 - label);
  float outerRim = recordOuterRimMask(recordR);
  float outside = 1.0 - saturate(max(max(label, vinyl), outerRim));
  return float4(label, vinyl, outerRim, outside);
}

float3 vinylRecordMaterial(float recordR, float angle, float seed, float sheenGain) {
  float grooveA = pow(saturate(1.0 - abs(frac(recordR * 72.0) - 0.5) * 2.0), 20.0);
  float grooveB = pow(saturate(1.0 - abs(frac(recordR * 150.0 + seed * 0.035) - 0.5) * 2.0), 24.0);
  float2 dir = normalize(float2(cos(angle), sin(angle)) + 0.0001);
  float broad = pow(saturate(dot(dir, normalize(float2(-0.78, 0.60))) * 0.5 + 0.5), 2.1);
  broad += pow(saturate(dot(dir, normalize(float2(0.66, -0.74))) * 0.5 + 0.5), 4.8) * 0.48;
  broad *= smoothstep(0.30, 0.72, recordR) * (1.0 - smoothstep(0.96, 1.02, recordR));
  float brush = valueNoise(float2(recordR * 210.0, angle * 38.0 + seed * 5.0));
  float3 vinyl = float3(0.0058, 0.0074, 0.0082);
  vinyl += float3(0.040, 0.047, 0.050) * (grooveA * 0.38 + grooveB * 0.28);
  vinyl += float3(0.055, 0.065, 0.066) * broad * (0.34 + sheenGain * 0.30);
  vinyl += float3(0.018, 0.021, 0.022) * brush * broad * 0.35;
  return vinyl;
}

float3 metallicColumnBase(float recordR, float labelMask, float vinylMask, float outerRimMask, float3 coverColor, float3 glowColor, float heightGlow) {
  float3 coldMetal = float3(0.034, 0.038, 0.041);
  float3 blueSteel = float3(0.018, 0.036, 0.050);
  float3 graphite = float3(0.010, 0.012, 0.014);
  float3 rimSteel = float3(0.024, 0.050, 0.062);
  float3 metal = lerp(graphite, blueSteel, saturate(vinylMask * 0.66 + outerRimMask * 0.42));
  metal = lerp(metal, coldMetal, saturate((1.0 - vinylMask) * 0.12 + Tone.x * 0.06));
  metal = lerp(metal, rimSteel, outerRimMask * 0.55);
  float3 labelMetal = coverColor * 0.034 + glowColor * 0.018 + float3(0.038, 0.040, 0.042);
  metal = lerp(metal, labelMetal, labelMask * (0.10 + heightGlow * 0.030));
  return metal;
}

float metallicSpecular(float3 normal, float3 world, float roughness, float edge, float top, float heightGlow) {
  float3 n = normalize(normal);
  float3 lightDir = normalize(float3(-0.38, 0.74, -0.54));
  float3 viewDir = normalize(float3(0.0, 5.8, -7.2) - world);
  float3 halfDir = normalize(lightDir + viewDir);
  float ndh = saturate(dot(n, halfDir));
  float ndl = saturate(dot(n, lightDir));
  float fresnel = pow(1.0 - saturate(dot(n, viewDir)), 5.2);
  float glossPower = lerp(120.0, 330.0, saturate(1.0 - roughness));
  float glint = pow(ndh, glossPower) * (0.08 + ndl * 0.92);
  float topLine = top * pow(saturate(ndh * 1.30), glossPower * 0.64) * (0.24 + heightGlow * 0.86);
  float edgeLine = edge * (0.18 + fresnel * 2.55) * (0.48 + heightGlow * 1.18);
  return saturate(glint * (0.94 + heightGlow * 1.34) + topLine + edgeLine);
}

float ggxDistribution(float ndh, float roughness) {
  float a = max(0.025, roughness * roughness);
  float a2 = a * a;
  float d = ndh * ndh * (a2 - 1.0) + 1.0;
  return a2 / max(0.00001, 3.14159265 * d * d);
}

float smithG1(float ndv, float roughness) {
  float r = roughness + 1.0;
  float k = (r * r) * 0.125;
  return ndv / max(0.0001, ndv * (1.0 - k) + k);
}

float3 fresnelSchlick(float cosTheta, float3 f0) {
  float f = pow(1.0 - saturate(cosTheta), 5.0);
  return f0 + (1.0 - f0) * f;
}

float3 metallicPbrSpecular(float3 normal, float3 world, float3 baseMetal, float roughness, float edge, float top, float heightGlow) {
  float3 n = normalize(normal);
  float3 lightDir = normalize(float3(-0.42, 0.78, -0.46));
  float3 viewDir = normalize(float3(0.0, 5.8, -7.2) - world);
  float3 halfDir = normalize(lightDir + viewDir);
  float ndl = saturate(dot(n, lightDir));
  float ndv = saturate(dot(n, viewDir));
  float ndh = saturate(dot(n, halfDir));
  float vdh = saturate(dot(viewDir, halfDir));
  float clampedRoughness = clamp(roughness, 0.045, 0.55);
  float3 f0 = saturate(baseMetal * 7.5 + float3(0.025, 0.032, 0.038));
  float d = ggxDistribution(ndh, clampedRoughness);
  float g = smithG1(ndl, clampedRoughness) * smithG1(ndv, clampedRoughness);
  float3 f = fresnelSchlick(vdh, f0);
  float3 spec = (d * g * f) / max(0.025, 4.0 * ndl * ndv);
  float edgeFresnel = pow(1.0 - ndv, 4.2) * edge * (0.55 + heightGlow * 0.80);
  float topCatch = top * pow(saturate(ndh * 1.18), lerp(90.0, 220.0, 1.0 - clampedRoughness)) * (0.28 + heightGlow * 0.74);
  return spec * ndl * (0.42 + heightGlow * 0.52) + f0 * (edgeFresnel + topCatch);
}

float4 main(PSIn input) : SV_TARGET {
  float opacity = saturate(Runtime.w);
  float effectActive = sonicEffectActive();
  if (input.mode > 8.5) {
    float emissionPass = step(9.5, input.mode);
    clip(effectActive - 0.010);
    clip(input.mask - 0.020);
    float3 n = normalize(input.normal);
    float top = saturate(n.y);
    clip(top - 0.52);
    float2 capUv = input.uv * 2.0 - 1.0;
    float capSoft = 1.0 - smoothstep(0.60, 1.02, length(capUv));
    float heightGlow = saturate(input.height * 1.35);
    float ghostHeightGate = smoothstep(0.52, 0.76, input.height);
    clip(ghostHeightGate - 0.010);
    float brightLift = smoothstep(0.34, 0.92, heightGlow);
    float mainWaveGlow = rippleMainVisualAt(input.world.xz);
    float meteorWaveGlow = rippleMeteorVisualAt(input.world.xz);
    float rippleGlow = max(mainWaveGlow, meteorWaveGlow);
    float distFade = distanceFade(input.world);
    float2 emitterLocal = rotate2D(input.world.xz, -Motion.x);
    float seed = hash21(floor(emitterLocal * 17.0));
    float recordR = recordRadiusFromWorld(input.world);
    float4 materialMasks = recordMaterialMasks(recordR, saturate(SonicTexture.x));
    float labelMask = materialMasks.x;
    float vinylMask = materialMasks.y;
    float outerRim = materialMasks.z;
    float onRecord = 1.0 - materialMasks.w;
    float recordCoverage = circleCoverage(recordR, 1.006);
    clip(recordCoverage * onRecord - 0.004);
    float recordAngle01 = atan2(input.world.z, input.world.x) * 0.15915494 + 0.5;
    float3 electricBlue = float3(0.10, 0.55, 1.24);
    float3 violet = float3(0.86, 0.32, 1.18);
    float emitterMask = saturate(labelMask * 0.44 + vinylMask * 0.50 + outerRim * 0.54) * recordCoverage;
    float3 glow = sonicGlowColor(input.world, seed) * Tone.w;
    float4 cover = uCover.Sample(uCoverSampler, coverUvFromWorld(input.world));
    float3 coldEmitter = lerp(Secondary.rgb * 0.62 + float3(0.12, 0.22, 0.30), float3(0.72, 0.86, 1.0), outerRim * 0.20 + vinylMask * 0.10);
    float3 labelEmitter = cover.rgb * 0.16 + coldEmitter * 0.84;
    float3 coolBloom = lerp(coldEmitter, labelEmitter, labelMask * (0.55 + brightLift * 0.12));
    float metalSpec = metallicSpecular(n, input.world, 0.30, capSoft, top, heightGlow);
    float3 metalHighlight = lerp(float3(0.58, 0.74, 0.88), float3(0.86, 0.94, 1.0), brightLift * 0.16 + outerRim * 0.06);
    float3 color = lerp(coolBloom, metalHighlight, saturate(metalSpec * 0.72));
    color += (metalHighlight * 1.52 + electricBlue * 0.58) * brightLift * (0.42 + top * 0.58);
    color = lerp(color, color * float3(0.74, 0.94, 1.14), saturate((vinylMask + outerRim) * 0.18 + rippleGlow * 0.10));
    float topEmitter = smoothstep(0.50, 0.94, metalSpec) * (0.20 + brightLift * 0.42);
    float heightEmitter = brightLift * (0.42 + labelMask * 0.026 + outerRim * 0.024);
    float source = max(heightEmitter, topEmitter * 0.62);
    float sparsePeakEmitter = smoothstep(0.84, 0.99, seed + brightLift * 0.18) * brightLift * (0.45 + heightGlow * 0.75);
    // Keep the emission RT selective, but admit contiguous high terrain so the
    // topographic plateaus bloom as a surface instead of isolated pin lights.
    float selectiveSource = max(topEmitter * 0.30, sparsePeakEmitter);
    source = lerp(source, selectiveSource, emissionPass) * ghostHeightGate;
    float alpha = (source * 0.68 + AudioA.w * topEmitter * 0.012) * capSoft;
    alpha *= input.mask * opacity * (0.30 + distFade * 0.54) * emitterMask * ghostHeightGate;
    alpha = saturate(alpha);
    float hdrEmissionGain = lerp(1.0, 3.0, emissionPass);
    return float4(color * alpha * hdrEmissionGain, saturate(alpha * hdrEmissionGain));
  }
  if (input.mode > 7.5) {
    clip(effectActive - 0.010);
    clip(input.mask - 0.020);
    float3 n = normalize(input.normal);
    float top = saturate(n.y);
    float side = 1.0 - top;
    float heightGlow = saturate(input.height * 1.18);
    float ghostHeightGate = smoothstep(0.52, 0.76, input.height);
    clip(ghostHeightGate - 0.010);
    float distFade = distanceFade(input.world);
    float mainWaveGlow = rippleMainVisualAt(input.world.xz);
    float meteorWaveGlow = rippleMeteorVisualAt(input.world.xz);
    float rippleGlow = max(mainWaveGlow, meteorWaveGlow);
    float2 emitterLocal = rotate2D(input.world.xz, -Motion.x);
    float seed = hash21(floor(emitterLocal * 13.0));
    float recordR = recordRadiusFromWorld(input.world);
    float4 materialMasks = recordMaterialMasks(recordR, saturate(SonicTexture.x));
    float labelMask = materialMasks.x;
    float vinylMask = materialMasks.y;
    float outerRim = materialMasks.z;
    float onRecord = 1.0 - materialMasks.w;
    float recordCoverage = circleCoverage(recordR, 1.006);
    clip(recordCoverage * onRecord - 0.004);
    float emitterMask = saturate(labelMask * 0.38 + vinylMask * 0.26 + outerRim * 0.50) * recordCoverage;
    float3 glow = sonicGlowColor(input.world, seed) * Tone.w;
    glow = lerp(glow * 0.20, glow * float3(0.28, 0.46, 0.72) + Secondary.rgb * 0.13, saturate(vinylMask * 0.62 + outerRim * 0.46));
    float edgeX = smoothstep(0.12, 0.00, input.uv.x) + smoothstep(0.88, 1.0, input.uv.x);
    float edgeY = smoothstep(0.12, 0.00, input.uv.y) + smoothstep(0.88, 1.0, input.uv.y);
    float shellEdge = saturate(edgeX + edgeY);
    float metalSpec = metallicSpecular(n, input.world, 0.34, shellEdge, top, heightGlow);
    float3 metalHighlight = lerp(float3(0.50, 0.66, 0.80), float3(0.78, 0.88, 0.98), top * 0.18 + outerRim * 0.10);
    float3 electricBlue = float3(0.10, 0.50, 1.18);
    float3 violet = float3(0.82, 0.28, 1.12);
    float3 color = lerp(sonicBase1() * 0.70, glow, 0.18 + heightGlow * 0.070) + Secondary.rgb * shellEdge * 0.030;
    color += electricBlue * mainWaveGlow * top * (0.28 + heightGlow * 0.32);
    color += violet * meteorWaveGlow * top * (0.32 + heightGlow * 0.38);
    color += metalHighlight * metalSpec * (0.32 + shellEdge * 0.62);
    color += Highlight.rgb * (rippleGlow * 0.022 + labelMask * heightGlow * top * 0.006);
    color = lerp(color, color * float3(0.72, 0.92, 1.14), saturate((vinylMask + outerRim) * 0.17 + metalSpec * 0.13));
    float edgeEmitter = smoothstep(0.42, 0.90, metalSpec) * (0.26 + shellEdge * 0.78);
    float alpha = (heightGlow * 0.012 + (mainWaveGlow * 0.046 + meteorWaveGlow * 0.054) * top + shellEdge * 0.012 + edgeEmitter * 0.042);
    alpha *= input.mask * opacity * (0.30 + distFade * 0.54) * (0.22 + side * 0.34 + top * 0.18) * emitterMask * ghostHeightGate;
    alpha = saturate(alpha);
    return float4(color * alpha, alpha);
  }
  if (input.mode > 6.5) {
    clip(effectActive - 0.010);
    clip(input.mask - 0.010);
    float3 n = normalize(input.normal);
    float rim = pow(1.0 - saturate(abs(n.y)), 1.45);
    float core = saturate(input.height);
    float seed = hash21(floor(input.world.xz * 45.0) + input.world.yy);
    float flicker = 0.72 + 0.28 * sin(Runtime.x * (20.0 + seed * 24.0) + seed * 60.0);
    float3 glow = sonicGlowColor(input.world, seed) * Tone.w;
    float3 meteorMetal = lerp(Secondary.rgb * 0.72 + float3(0.36, 0.52, 0.66), float3(0.86, 0.94, 1.0), core);
    float3 color = lerp(glow * float3(0.55, 0.74, 1.0), meteorMetal, 0.48 + core * 0.20) * (0.32 + core * 0.70 + rim * 0.24) * flicker;
    float alpha = saturate((0.045 + core * 0.24 + rim * 0.08) * input.mask * opacity);
    return float4(color * alpha, alpha);
  }
  if (input.mode > 5.5) {
    clip(effectActive - 0.010);
    float2 p = input.uv * 2.0 - 1.0;
    float r = length(p);
    float discCoverage = circleCoverage(r, 1.070);
    clip(discCoverage - 0.002);
    float angle = atan2(p.y, p.x);
    float pulse = saturate(input.height);
    float outer = smoothstep(0.78, 1.00, r) * (1.0 - smoothstep(1.012, 1.070, r));
    float innerAir = (1.0 - smoothstep(0.18, 0.58, r)) * 0.006;
    float domeAir = (1.0 - smoothstep(0.34, 0.96, r)) * (0.002 + AudioA.y * 0.002 + AudioC.w * 0.004);
    float rimHaze = smoothstep(0.78, 0.99, r) * (1.0 - smoothstep(1.02, 1.075, r)) * (0.004 + AudioC.y * 0.005);
    float sweep = pow(saturate(1.0 - abs(frac(angle / 6.2831853 * 5.0 - Runtime.x * (0.055 + Runtime.z * 0.035)) - 0.5) * 2.0), 5.0);
    float audioRing = pow(saturate(1.0 - abs(frac(r * 7.5 - Runtime.x * (0.20 + Runtime.z * 0.18)) - 0.5) * 2.0), 9.0);
    float surfaceVeil = (1.0 - smoothstep(0.08, 0.92, r)) * (0.0015 + AudioA.x * 0.0025 + AudioA.y * 0.0020);
    float centerHalo = (1.0 - smoothstep(0.18, 0.46, r)) * (0.002 + AudioA.x * 0.003 + AudioA.w * 0.004);
    float labelRing = exp(-pow((r - 0.38) / 0.030, 2.0)) * (0.008 + AudioA.x * 0.010 + AudioA.w * 0.008);
    float edgeWall = ringCoverage(r, 0.912, 1.066);
    float edgeTicks = pow(saturate(1.0 - abs(frac(angle / 6.2831853 * 144.0 + Runtime.x * 0.030) - 0.5) * 2.0), 20.0);
    float edgePulse = edgeWall * (0.016 + AudioA.x * 0.018 + AudioA.w * 0.025);
    float thicknessGlow = exp(-pow((r - 0.985) / 0.020, 2.0)) * (0.018 + AudioC.y * 0.018 + AudioC.z * 0.012);
    float fog = outer * (0.008 + pulse * 0.024) + innerAir * (0.006 + AudioC.w * 0.006) + domeAir + rimHaze + surfaceVeil;
    fog += sweep * outer * (0.006 + AudioC.y * 0.010 + AudioC.z * 0.006);
    fog += audioRing * outer * (0.006 + AudioA.y * 0.010 + AudioA.w * 0.006);
    fog += centerHalo + labelRing + edgePulse + thicknessGlow;
    fog += edgeTicks * edgeWall * (0.004 + AudioC.y * 0.006 + AudioC.z * 0.006);
    float3 glow = sonicGlowColor(input.world, hash21(p * 13.7 + Runtime.x)) * Tone.w;
    float3 color = lerp(Secondary.rgb * 0.88, glow * float3(0.45, 0.62, 0.90), 0.38) + Highlight.rgb * (sweep * 0.10 + domeAir * 1.2 + surfaceVeil * 0.8 + labelRing * 2.6);
    color += lerp(Primary.rgb, Highlight.rgb, 0.45) * centerHalo * 1.4;
    color += lerp(Secondary.rgb, Highlight.rgb, 0.25) * edgeWall * (0.30 + edgeTicks * 0.28 + AudioA.w * 0.08);
    float alpha = saturate(fog * opacity * 0.46 * discCoverage);
    return float4(color * alpha, alpha);
  }
  if (input.mode > 4.5) {
    clip(effectActive - 0.010);
    clip(input.mask - 0.008);
    float3 n = normalize(input.normal);
    float rim = pow(1.0 - saturate(abs(n.y)), 1.25);
    float core = saturate(input.height);
    float seed = hash21(floor(input.world.xz * 37.0) + input.world.yy);
    float flicker = 0.68 + 0.32 * sin(Runtime.x * (18.0 + seed * 18.0) + seed * 40.0);
    float3 glow = sonicGlowColor(input.world, seed) * Tone.w * float3(0.50, 0.68, 0.95);
    float3 trailMetal = lerp(Secondary.rgb * 0.45 + float3(0.26, 0.42, 0.55), float3(0.76, 0.88, 0.98), 0.28 + core * 0.34);
    float3 color = lerp(glow, trailMetal, 0.42 + core * 0.22) * (0.24 + core * 0.56 + rim * 0.18) * flicker;
    float alpha = saturate((0.060 + core * 0.32 + rim * 0.09) * input.mask * opacity);
    return float4(color * alpha, alpha);
  }
  if (input.mode > 3.5) {
    clip(effectActive - 0.010);
    clip(input.mask - 0.015);
    float3 n = normalize(input.normal);
    float top = saturate(n.y);
    float edgeX = smoothstep(0.08, 0.00, input.uv.x) + smoothstep(0.92, 1.0, input.uv.x);
    float edgeY = smoothstep(0.08, 0.00, input.uv.y) + smoothstep(0.92, 1.0, input.uv.y);
    float edge = saturate(edgeX + edgeY);
    float glow = saturate(input.height);
    float seed = hash21(floor(input.world.xz * 8.0) + input.world.yx);
    float distFade = distanceFade(input.world);
    float3 currentGlow = sonicGlowColor(input.world, seed) * Tone.w * lerp(0.42, 1.0, distFade);
    float3 color = lerp(sonicBase1(), currentGlow, glow * 0.72 + top * 0.22);
    color += currentGlow * edge * (0.22 + glow * 0.44);
    color = lerp(color, sonicBase2(), (1.0 - distFade) * 0.22);
    float alpha = saturate((0.052 + glow * 0.19 + edge * 0.08) * input.mask * opacity * (0.34 + distFade * 0.54));
    return float4(color * alpha, alpha);
  }
  if (input.mode > 2.5) {
    clip(effectActive - 0.010);
    clip(input.mask - 0.01);
    float3 n = normalize(input.normal);
    float rim = pow(1.0 - saturate(abs(n.y)), 1.4);
    float core = saturate(input.height);
    float3 glow = sonicGlowColor(input.world, hash21(input.world.xz + Runtime.x)) * Tone.w * float3(0.44, 0.64, 0.95);
    float3 beamMetal = lerp(Secondary.rgb * 0.46 + float3(0.30, 0.46, 0.60), float3(0.80, 0.92, 1.0), core);
    float3 color = lerp(glow, beamMetal, 0.48 + core * 0.26) * (0.20 + core * 0.42 + rim * 0.22);
    float alpha = saturate((0.018 + core * 0.12 + rim * 0.045) * input.mask * opacity);
    return float4(color * alpha, alpha);
  }
  if (input.mode > 1.5) {
    clip(effectActive - 0.010);
    clip(input.mask - 0.01);
    float3 n = normalize(input.normal);
    float top = saturate(n.y);
    float rim = pow(1.0 - saturate(abs(n.y)), 1.7);
    float core = saturate(input.height);
    float3 glow = sonicGlowColor(input.world, hash21(input.world.xz * 2.7)) * Tone.w * float3(0.48, 0.68, 0.96);
    float3 meteorBody = lerp(Secondary.rgb * 0.48 + float3(0.34, 0.50, 0.64), float3(0.84, 0.94, 1.0), top * 0.42 + core * 0.26);
    float3 color = lerp(glow, meteorBody, 0.40 + core * 0.22) * (0.26 + core * 0.44 + top * 0.16);
    color += meteorBody * rim * (0.070 + core * 0.12);
    float alpha = saturate((0.060 + core * 0.18 + rim * 0.07) * opacity);
    return float4(color * alpha, alpha);
  }
  if (input.mode > 0.5) {
    float2 p = input.uv * 2.0 - 1.0;
    float2 recordP = rotate2D(p, -Motion.x);
    float r = length(p);
    float outerCoverage = circleCoverage(r, 1.0);
    float angle = atan2(recordP.y, recordP.x);
    float seed = valueNoise(recordP * 32.0 + float2(r * 17.0, angle * 0.35));
    float labelDisc = 1.0 - smoothstep(0.252, 0.286, r);
    float bodyBand = ringCoverage(r, 0.270, 1.0);
    float ringBand = ringCoverage(r, 0.760, 1.0);
    // The vinyl is always a solid base. Active terrain grows above it instead of replacing it.
    float drawBand = max(labelDisc, bodyBand);
    drawBand *= outerCoverage;
    clip(drawBand - 0.004);
    float groove = pow(saturate(1.0 - abs(frac(r * 122.0 + seed * 0.014) - 0.5) * 2.0), 18.0);
    float majorGroove = pow(saturate(1.0 - abs(frac(r * 12.5 + 0.12) - 0.5) * 2.0), 20.0);
    float brushed = pow(saturate(1.0 - abs(frac(angle / 6.2831853 * 260.0 + seed * 0.10) - 0.5) * 2.0), 8.0);
    float innerFeather = 1.0 - smoothstep(0.878, 0.925, r);
    float outerLip = smoothstep(0.970, 0.994, r);
    float labelLip = exp(-pow((r - 0.300) / 0.022, 2.0)) + exp(-pow((r - 0.350) / 0.016, 2.0)) * 0.62;
    float2 dir = normalize(p + 0.0001);
    float broad = pow(saturate(dot(dir, normalize(float2(-0.72, 0.70))) * 0.5 + 0.5), 1.85);
    broad += pow(saturate(dot(dir, normalize(float2(0.74, -0.68))) * 0.5 + 0.5), 3.1) * 0.58;
    broad *= smoothstep(0.32, 0.68, r) * (1.0 - smoothstep(0.96, 1.0, r));
    float radialBrush = valueNoise(float2(r * 210.0, angle * 42.0)) * 0.62 + valueNoise(float2(r * 82.0, angle * 19.0 + 7.0)) * 0.38;
    float3 vinylBody = float3(0.0085, 0.0090, 0.0092);
    vinylBody += float3(0.145, 0.145, 0.136) * broad;
    vinylBody += float3(0.038, 0.039, 0.037) * groove * (0.42 + broad * 0.35);
    vinylBody += float3(0.030, 0.031, 0.029) * brushed * (0.10 + radialBrush * 0.10);
    vinylBody += float3(0.022, 0.023, 0.022) * majorGroove * 0.22;
    vinylBody += float3(0.040, 0.041, 0.039) * radialBrush * broad * 0.18;
    vinylBody = lerp(vinylBody, float3(0.0012, 0.0013, 0.0014), saturate(labelLip * 0.72));
    vinylBody += float3(0.040, 0.042, 0.041) * outerLip * 0.45;
    float3 material = lerp(sonicBase2() * 0.42, sonicGlowColor(float3(p.x * Params.y, 0.0, p.y * Params.y), seed) * Tone.w, 0.12 + effectActive * 0.18);
    float3 activeRing = vinylBody * (0.62 + groove * 0.18 + brushed * 0.08) + material * (0.045 + outerLip * 0.038 + innerFeather * 0.018);
    activeRing += float3(0.018, 0.020, 0.021) * (0.20 + outerLip * 0.32);
    float3 color = lerp(vinylBody, activeRing, effectActive);
    color = lerp(color, vinylBody * 0.46 + float3(0.0018, 0.0020, 0.0022), effectActive * ringBand * 0.76);
    float2 labelUv = saturate(recordP / 0.54 + 0.5);
    float4 cover = uCover.Sample(uCoverSampler, labelUv);
    float useTexture = saturate(SonicTexture.x);
    float labelOuter = smoothstep(0.246, 0.274, r) * (1.0 - smoothstep(0.286, 0.322, r));
    float labelFine = exp(-pow((r - 0.302) / 0.010, 2.0)) + exp(-pow((r - 0.338) / 0.009, 2.0)) * 0.52;
    float spindleCore = 1.0 - smoothstep(0.014, 0.021, r);
    float spindleCollar = ringCoverage(r, 0.019, 0.034);
    float spindleSeat = ringCoverage(r, 0.032, 0.044);
    float spindleInnerBevel = exp(-pow((r - 0.020) / 0.0038, 2.0));
    float spindleOuterBevel = exp(-pow((r - 0.033) / 0.0035, 2.0));
    float spindleSweep = pow(saturate(dot(dir, normalize(float2(-0.62, 0.78))) * 0.5 + 0.5), 9.0);
    float3 spindleMetal = float3(0.026, 0.031, 0.034);
    spindleMetal += float3(0.22, 0.27, 0.30) * (spindleInnerBevel * (0.18 + spindleSweep * 0.48));
    spindleMetal += float3(0.12, 0.14, 0.15) * spindleOuterBevel * (0.20 + spindleSweep * 0.34);
    float3 coverDisplay = pow(saturate(cover.rgb), 0.78) * 0.92;
    float3 labelColor = lerp(float3(0.040, 0.043, 0.046), coverDisplay + vinylBody * 0.08, useTexture);
    color = lerp(color, labelColor, labelDisc * 0.985);
    color = lerp(color, float3(0.0022, 0.0025, 0.0028), labelOuter * 0.94);
    color += float3(0.026, 0.028, 0.028) * labelFine * 0.22;
    color = lerp(color, color * 0.30 + float3(0.0015, 0.0018, 0.0020), spindleSeat * 0.72);
    color = lerp(color, spindleMetal, spindleCollar * 0.985);
    color = lerp(color, float3(0.00015, 0.00018, 0.00022), spindleCore * 0.995);

    // Keep the label bezel and the record rim as narrow physical metal rings.
    // These highlights are directional reflections in the base pass, not bloom emitters.
    float innerMetalBand = ringCoverage(r, 0.282, 0.362);
    float outerMetalBand = ringCoverage(r, 0.962, 1.000);
    float innerBevel = exp(-pow((r - 0.288) / 0.008, 2.0)) + exp(-pow((r - 0.354) / 0.008, 2.0));
    float outerBevel = exp(-pow((r - 0.968) / 0.007, 2.0)) + exp(-pow((r - 0.993) / 0.005, 2.0));
    float ringSweep = pow(saturate(dot(dir, normalize(float2(-0.76, 0.65))) * 0.5 + 0.5), 7.5);
    ringSweep += pow(saturate(dot(dir, normalize(float2(0.70, -0.72))) * 0.5 + 0.5), 13.0) * 0.46;
    float ringBrush = valueNoise(float2(r * 620.0, angle * 96.0 + seed * 4.0));
    float3 gunmetal = float3(0.009, 0.012, 0.014);
    gunmetal += float3(0.052, 0.061, 0.064) * (0.18 + ringSweep * 0.66 + ringBrush * 0.10);
    float3 coldSteel = float3(0.18, 0.23, 0.27);
    float3 innerMetal = gunmetal + coldSteel * innerBevel * (0.12 + ringSweep * 0.42);
    innerMetal += float3(0.030, 0.036, 0.038) * ringBrush * 0.20;
    float3 outerMetal = gunmetal * 0.78 + coldSteel * outerBevel * (0.18 + ringSweep * 0.62);
    outerMetal += float3(0.055, 0.068, 0.074) * ringSweep * 0.24;
    color = lerp(color, innerMetal, innerMetalBand * 0.985);
    color = lerp(color, outerMetal, outerMetalBand * 0.990);

    float activeRimWeight = saturate(ringBand * (0.92 + outerLip * 0.18) + labelDisc * 0.92);
    float alpha = saturate(drawBand * opacity * lerp(1.0, 0.90 + activeRimWeight * 0.08, effectActive));
    return float4(color * alpha, alpha);
  }

  clip(input.mask - 0.018);
  float3 n = normalize(input.normal);
  float top = saturate(n.y);
  float side = 1.0 - top;
  float light = saturate(dot(n, normalize(float3(-0.32, 0.68, -0.55)))) * 0.48 + 0.26;
  float heightGlow = saturate(input.height * 1.35);
  float relativeY = saturate((input.world.y + 1.10) / max(0.035, input.height));
  float distTop = 1.0 - relativeY;
  float audioEnergy = saturate(AudioA.x * 0.25 + AudioA.y * 0.24 + AudioA.z * 0.20 + AudioA.w * 0.26 + AudioC.y * 0.16 + AudioC.w * 0.10);
  float musicActive = smoothstep(0.055, 0.285, audioEnergy);
  clip(musicActive - 0.010);
  float vinylIdle = 1.0 - musicActive;
  float distFade = distanceFade(input.world);
  float solidFade = lerp(1.0, distFade, musicActive);
  float seed = hash21(floor(input.world.xz * 18.0));
  float3 currentGlow = sonicGlowColor(input.world, seed) * Tone.w * lerp(0.38, 1.0, distFade);
  float mainWaveGlow = rippleMainVisualAt(input.world.xz);
  float meteorWaveGlow = rippleMeteorVisualAt(input.world.xz);
  float rippleGlow = max(mainWaveGlow, meteorWaveGlow);
  float useTexture = saturate(SonicTexture.x);
  float4 cover = uCover.Sample(uCoverSampler, coverUvFromWorld(input.world));
  float recordR = length(input.world.xz) / max(0.001, Params.y);
  float recordCoverage = circleCoverage(recordR, 1.006);
  float maskCoverage = smoothstep(0.018, 0.16, input.mask) * recordCoverage;
  float labelMask = recordLabelMask(recordR) * useTexture;
  float vinylMask = recordVinylMask(recordR);
  float outerRimMask = recordOuterRimMask(recordR);
  float centerCoverMask = labelMask;
  float2 recordLocal = rotate2D(input.world.xz, -Motion.x);
  float recordAngle = atan2(recordLocal.y, recordLocal.x);
  float recordAngle01 = recordAngle * 0.15915494 + 0.5;
  float blueRing = pow(saturate(1.0 - abs(frac(recordR * 18.0 - Runtime.x * 0.10) - 0.5) * 2.0), 5.4) * vinylMask * musicActive;
  float purpleArc = pow(saturate(1.0 - abs(frac(recordAngle01 * 3.0 + recordR * 2.0 - Runtime.x * 0.08) - 0.5) * 2.0), 8.0) * vinylMask * musicActive;
  float3 electricBlue = float3(0.10, 0.50, 1.18);
  float3 violet = float3(0.82, 0.28, 1.12);
  float3 activeVinylMaterial = vinylRecordMaterial(recordR, recordAngle, seed, saturate(Tone.y + heightGlow * 0.55));
  currentGlow = lerp(currentGlow, currentGlow * float3(0.24, 0.42, 0.70) + Secondary.rgb * 0.070, saturate(vinylMask * 0.76 + outerRimMask * 0.58));
  currentGlow = lerp(currentGlow, cover.rgb * 0.070 + currentGlow * 0.93, labelMask * (0.14 + top * 0.060));
  float3 metalBase = metallicColumnBase(recordR, labelMask, vinylMask, outerRimMask, cover.rgb, currentGlow, heightGlow);
  float3 vinylInk = lerp(float3(0.004, 0.010, 0.012), float3(0.014, 0.032, 0.040), saturate(Tone.y * 0.28 + heightGlow * 0.20));
  float3 sideFill = lerp(Primary.rgb, Secondary.rgb, 0.66) * (0.010 + heightGlow * 0.020);
  vinylInk = lerp(vinylInk, activeVinylMaterial + Secondary.rgb * 0.020, vinylMask * 0.82 + outerRimMask * 0.62);
  float3 bodyColor = lerp(metalBase * 0.28 + vinylInk * 0.10, metalBase * 0.92 + sideFill * 0.055, relativeY * distFade);
  float edgeX = smoothstep(0.055, 0.010, input.uv.x) + smoothstep(0.945, 0.990, input.uv.x);
  float edgeY = smoothstep(0.055, 0.010, input.uv.y) + smoothstep(0.945, 0.990, input.uv.y);
  float edge = saturate(edgeX + edgeY);
  float topIntensity = smoothstep(0.00, 0.40, heightGlow);
  float verticalFalloff = lerp(1.15, 2.55, saturate(Tone.z));
  float sideGlow = smoothstep(0.58 / verticalFalloff, 0.0, distTop) * heightGlow;
  float roughness = saturate(0.205 - Tone.z * 0.10 - heightGlow * 0.12 + vinylMask * 0.030);
  float metalSpec = metallicSpecular(n, input.world, roughness, edge, top, heightGlow);
  float3 pbrSpecular = metallicPbrSpecular(n, input.world, metalBase, roughness, edge, top, heightGlow);
  float pbrSpecLevel = saturate(max(pbrSpecular.r, max(pbrSpecular.g, pbrSpecular.b)) * 1.35);
  float hardSpec = smoothstep(0.52, 0.94, metalSpec);
  float edgeSpec = edge * smoothstep(0.12, 0.86, metalSpec) * (0.58 + heightGlow * 0.72);
  float3 metalHighlight = lerp(float3(0.62, 0.74, 0.84), float3(0.94, 0.98, 1.0), saturate(labelMask * 0.10 + Tone.y * 0.04 + hardSpec * 0.36 + pbrSpecLevel * 0.20));
  metalHighlight = lerp(metalHighlight, Secondary.rgb * 0.14 + float3(0.50, 0.64, 0.76), vinylMask * 0.18 + outerRimMask * 0.16);
  float2 metalDir = normalize(input.world.xz + 0.0001);
  float metalSweep = pow(saturate(dot(metalDir, normalize(float2(-0.78, 0.53))) * 0.5 + 0.5), 7.2);
  metalSweep += pow(saturate(dot(metalDir, normalize(float2(0.62, -0.79))) * 0.5 + 0.5), 12.0) * 0.48;
  float metalBrush = valueNoise(input.world.xz * 68.0 + float2(seed * 3.1, Runtime.x * 0.06));
  float microScratch = pow(valueNoise(input.world.xz * 126.0 + float2(seed * 9.7, -Runtime.x * 0.035)), 3.0);
  float brushedMetal = top * saturate(metalSweep * (0.18 + metalBrush * 0.70) + microScratch * (0.10 + edge * 0.28)) * (0.16 + heightGlow * 0.42) * (0.94 + vinylMask * 0.06);
  float3 capBase = lerp(metalBase * 1.02 + activeVinylMaterial * 0.12, currentGlow, topIntensity * (0.030 + labelMask * 0.035));
  capBase += pbrSpecular * (0.78 + topIntensity * 0.36);
  capBase += metalHighlight * (hardSpec * (0.42 + topIntensity * 0.34) + brushedMetal * 0.56);
  float echoTopGate = top * smoothstep(0.16, 0.62, heightGlow) * (1.0 - labelMask * 0.96);
  float3 echoMetalColor = lerp(electricBlue, violet, saturate(seed * 0.72 + recordR * 0.28));
  capBase += echoMetalColor * echoTopGate * (0.060 + hardSpec * 0.080 + brushedMetal * 0.045);
  capBase += metalHighlight * echoTopGate * (0.055 + edge * 0.040);
  capBase = max(capBase, Highlight.rgb * topIntensity * labelMask * 0.012);
  float3 color = top > 0.5
    ? capBase
    : lerp(bodyColor * 0.38, metalBase * 0.82 + currentGlow * 0.032, sideGlow * 0.20);
  color += pbrSpecular * edge * (0.90 + topIntensity * 0.72);
  color += metalHighlight * hardSpec * edge * (0.58 + topIntensity * 0.70);
  color += (pbrSpecular * 0.80 + metalHighlight * 0.24) * edgeSpec * side * (0.20 + heightGlow * 0.50);
  color += metalHighlight * brushedMetal * (0.26 + topIntensity * 0.38);
  color += currentGlow * edge * top * (0.012 + topIntensity * 0.032 + outerRimMask * 0.018);
  color += (metalHighlight * 0.70 + currentGlow * 0.020) * side * smoothstep(0.055, 0.0, distTop) * heightGlow * 0.30;
  color += sideFill * side * (0.012 + heightGlow * 0.026) * distFade;
  color = lerp(color, activeVinylMaterial + currentGlow * 0.045, vinylMask * top * (0.16 + edge * 0.12) * (1.0 - labelMask));
  float3 labelReflection = cover.rgb * (0.16 + top * 0.055) + metalBase * 0.60 + metalHighlight * hardSpec * 0.34 + currentGlow * (0.014 + top * 0.008);
  color = lerp(color, labelReflection, labelMask * distFade * (0.22 + top * 0.20));
  color += Highlight.rgb * saturate(AudioA.w) * top * labelMask * 0.012;
  float grooveA = pow(saturate(1.0 - abs(frac(recordR * 62.0) - 0.5) * 2.0), 18.0);
  float grooveB = pow(saturate(1.0 - abs(frac(recordR * 134.0 + seed * 0.035) - 0.5) * 2.0), 24.0);
  float grooveLine = saturate(grooveA * 0.72 + grooveB * 0.34);
  float2 recordDir = normalize(input.world.xz + 0.0001);
  float tangentSheen = pow(saturate(abs(dot(recordDir, normalize(float2(-0.70, 0.46))))), 4.8);
  float broadSheen = pow(saturate(dot(recordDir, normalize(float2(-0.82, 0.54))) * 0.5 + 0.5), 2.3);
  broadSheen += pow(saturate(dot(recordDir, normalize(float2(0.66, -0.74))) * 0.5 + 0.5), 5.0) * 0.55;
  broadSheen *= smoothstep(0.18, 0.64, recordR) * (1.0 - smoothstep(0.93, 1.03, recordR));
  float diagonalSheen = pow(saturate(abs(dot(recordDir, normalize(float2(-0.56, 0.82))))), 2.15);
  diagonalSheen *= smoothstep(0.24, 0.72, recordR) * (1.0 - smoothstep(0.96, 1.03, recordR));
  float brushNoise = valueNoise(input.world.xz * 18.0 + float2(seed * 2.0, -seed * 1.7));
  float fineScratch = pow(saturate(brushNoise * 0.85 + grooveLine * 0.45), 2.2);
  float3 vinylTop = lerp(float3(0.0044, 0.0049, 0.0052), float3(0.030, 0.032, 0.031), saturate(grooveLine * 0.30 + tangentSheen * 0.12 + broadSheen * 0.24 + diagonalSheen * 0.16 + fineScratch * 0.08 + Tone.y * 0.026));
  vinylTop += float3(0.034, 0.036, 0.035) * broadSheen * 0.085;
  vinylTop += float3(0.030, 0.032, 0.031) * diagonalSheen * 0.070;
  vinylTop += float3(0.020, 0.021, 0.020) * grooveLine * 0.24;
  float labelDisc = 1.0 - smoothstep(0.260, 0.292, recordR);
  float labelIdle = useTexture * labelDisc * top * solidFade;
  float labelOuterRing = smoothstep(0.275, 0.306, recordR) * (1.0 - smoothstep(0.368, 0.404, recordR));
  float labelFineRing = exp(-pow((recordR - 0.326) / 0.010, 2.0)) + exp(-pow((recordR - 0.364) / 0.009, 2.0));
  float spindleHole = 1.0 - smoothstep(0.014, 0.021, recordR);
  float spindleCollar = ringCoverage(recordR, 0.019, 0.034);
  float spindleSeat = ringCoverage(recordR, 0.032, 0.044);
  float spindleBevel = exp(-pow((recordR - 0.020) / 0.0038, 2.0)) + exp(-pow((recordR - 0.033) / 0.0035, 2.0)) * 0.62;
  float2 spindleDir = normalize(input.world.xz + 0.0001);
  float spindleSweep = pow(saturate(dot(spindleDir, normalize(float2(-0.62, 0.78))) * 0.5 + 0.5), 9.0);
  float3 spindleMetal = float3(0.026, 0.031, 0.034) + float3(0.22, 0.27, 0.30) * spindleBevel * (0.18 + spindleSweep * 0.46);
  float3 labelColor = cover.rgb * (0.58 + top * 0.30) + vinylTop * 0.12;
  float activeVinylTop = musicActive * top * solidFade * vinylMask * (1.0 - labelDisc);
  float activeLabelTop = musicActive * top * solidFade * labelDisc * useTexture;
  float activeOuterEdge = musicActive * top * solidFade * outerRimMask;
  float3 activeGrooveTop = vinylTop * (0.62 + heightGlow * 0.10) + metalBase * 0.44 + metalHighlight * (hardSpec * 0.18 + brushedMetal * 0.13);
  activeGrooveTop += Secondary.rgb * (grooveLine * 0.018 + outerRimMask * 0.024);
  color = lerp(color, activeGrooveTop, activeVinylTop * (0.30 + grooveLine * 0.12 + broadSheen * 0.045));
  color = lerp(color, activeGrooveTop * 0.50 + float3(0.0018, 0.0022, 0.0026), activeOuterEdge * 0.40);
  float3 activeLabelColor = cover.rgb * (0.32 + heightGlow * 0.050) + metalBase * 0.48 + metalHighlight * hardSpec * 0.28 + vinylTop * 0.040;
  color = lerp(color, activeLabelColor, activeLabelTop * (0.38 + heightGlow * 0.080));
  color = lerp(color, float3(0.0015, 0.0018, 0.0021), musicActive * top * solidFade * labelOuterRing * 0.62);
  color += float3(0.026, 0.029, 0.030) * musicActive * top * solidFade * labelFineRing * 0.16;
  color = lerp(color, color * 0.30 + float3(0.0015, 0.0018, 0.0020), musicActive * top * solidFade * spindleSeat * 0.70);
  color = lerp(color, spindleMetal, musicActive * top * solidFade * spindleCollar * 0.96);
  color = lerp(color, float3(0.00015, 0.00018, 0.00022), musicActive * top * solidFade * spindleHole * 0.97);
  color = lerp(color, vinylTop, vinylIdle * top * solidFade * (1.0 - labelDisc * 0.96));
  color = lerp(color, labelColor, vinylIdle * labelIdle * 0.98);
  color = lerp(color, float3(0.0025, 0.0030, 0.0034), vinylIdle * top * solidFade * labelOuterRing * 0.92);
  color += float3(0.028, 0.030, 0.030) * vinylIdle * top * solidFade * labelFineRing * 0.28;
  color = lerp(color, color * 0.30 + float3(0.0015, 0.0018, 0.0020), vinylIdle * top * solidFade * spindleSeat * 0.72);
  color = lerp(color, spindleMetal, vinylIdle * top * solidFade * spindleCollar * 0.98);
  color = lerp(color, float3(0.00015, 0.00018, 0.00022), vinylIdle * top * solidFade * spindleHole * 0.995);
  color = lerp(color, vinylTop * 0.74, vinylIdle * side * solidFade * 0.82);
  color += vinylTop * grooveLine * vinylIdle * top * solidFade * 0.14;
  float outerVinylTone = smoothstep(0.50, 0.96, recordR) * (1.0 - centerCoverMask);
  color = lerp(color, color * float3(0.50, 0.68, 0.73) + Secondary.rgb * 0.045, outerVinylTone * side * 0.42);
  float ringHeightGate = 0.20 + smoothstep(0.12, 0.62, heightGlow) * 0.80;
  color += (electricBlue * blueRing * 0.34 + violet * purpleArc * 0.22) * top * (0.42 + heightGlow * 1.18) * ringHeightGate;
  color += electricBlue * mainWaveGlow * top * 0.12;
  color += violet * meteorWaveGlow * top * 0.14;
  float ringGlowAllowed = saturate(blueRing * 0.54 + purpleArc * 0.42);
  float glowAllowed = saturate(labelMask * 0.24 + edge * 0.48 + topIntensity * 0.44 + echoTopGate * 0.34 + rippleGlow * 0.30 + outerRimMask * 0.20 + ringGlowAllowed);
  color += (currentGlow * 0.022 + pbrSpecular * 1.42 + metalHighlight * hardSpec * 0.70 + metalHighlight * brushedMetal * 0.58) * top * (0.010 + heightGlow * 0.024 + rippleGlow * 0.012) * (0.10 + musicActive * 0.34) * glowAllowed;
  float backgroundLuma = saturate(SonicTexture.y);
  float backgroundContrast = saturate(SonicTexture.z);
  float backgroundBusy = saturate(SonicTexture.w);
  float materialExposure = lerp(1.18, 0.88, backgroundLuma) * lerp(0.96, 1.04, backgroundContrast) * lerp(1.0, 0.94, backgroundBusy);
  color *= materialExposure;
  color += metalHighlight * hardSpec * top * lerp(0.070, 0.024, backgroundLuma) * (0.30 + heightGlow * 0.70);
  color *= (light * lerp(0.46, 0.82, top)) + top * 0.36;
  float fog = (1.0 - distFade) * musicActive;
  color = lerp(color, sonicBase2() * 0.72 + Secondary.rgb * 0.032, fog * (0.30 + side * 0.12));
  float liveAlpha = (0.82 + heightGlow * 0.16 + rippleGlow * 0.06 + top * 0.10) * maskCoverage * opacity * (0.78 + distFade * 0.22);
  float idleAlpha = (0.050 + top * 0.950) * maskCoverage * opacity;
  float alpha = saturate(lerp(idleAlpha, liveAlpha, musicActive));
  return float4(color * alpha, alpha);
}";

}
