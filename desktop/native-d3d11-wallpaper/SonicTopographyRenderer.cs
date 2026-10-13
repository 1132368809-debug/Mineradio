using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

internal sealed partial class D3DParticleRenderer
{
    private const float SonicTerrainRadius = 4.18f;
    private const float SonicRecordSpinSpeed = 0.045f;
    private const float SonicMainWaveMinSpacing = 0.667f;
    private bool _sonicAudioInitialized;
    private float _sonicLastAudioTime = -100f;
    private float _sonicEnergyFast;
    private float _sonicEnergySlow;
    private float _sonicEnergyPool;
    private float _sonicLiftDrive;
    private float _sonicPreviousRawKick;
    private float _sonicMappedKick;
    private Vector4 _sonicMappedBandsA;
    private Vector4 _sonicMappedBandsB;
    private int _sonicBeatSerial = -1;
    private int _sonicBeatLaneSequence;
    private float _sonicBeatEnvelope;
    private float _sonicBeatAge = 10f;
    private float _sonicBeatStrength;
    private float _sonicBeatImpact;
    private float _sonicBeatCombo;
    private float _sonicBeatLane;
    private float _sonicBeatPhase;
    private float _sonicBeatTempoConfidence;
    private float _sonicLastBeatAt = -100f;
    private float _sonicFallbackKickPrev;
    private float _sonicSpinClock;
    private float _sonicWaveReservoirCharge;

    private void CreateSonicGeometry()
    {
        var (cubeVertices, cubeIndices) = BuildSonicCubeGeometry();
        var (columnVertices, columnIndices) = BuildSonicColumnGeometry(8);
        var (discVertices, discIndices) = BuildSonicDiscGeometry(4.55f, 384);
        _sonicCubeIndexCount = cubeIndices.Length;
        _sonicColumnIndexCount = columnIndices.Length;
        _sonicDiscIndexCount = discIndices.Length;
        _sonicCubeVertexBuffer = CreateImmutableBuffer(cubeVertices, BindFlags.VertexBuffer);
        _sonicCubeIndexBuffer = CreateImmutableBuffer(cubeIndices, BindFlags.IndexBuffer);
        _sonicColumnVertexBuffer = CreateImmutableBuffer(columnVertices, BindFlags.VertexBuffer);
        _sonicColumnIndexBuffer = CreateImmutableBuffer(columnIndices, BindFlags.IndexBuffer);
        _sonicDiscVertexBuffer = CreateImmutableBuffer(discVertices, BindFlags.VertexBuffer);
        _sonicDiscIndexBuffer = CreateImmutableBuffer(discIndices, BindFlags.IndexBuffer);
    }

    private ID3D11Buffer CreateImmutableBuffer<T>(T[] values, BindFlags bindFlags) where T : struct
    {
        var byteWidth = Marshal.SizeOf<T>() * Math.Max(1, values.Length);
        var handle = GCHandle.Alloc(values, GCHandleType.Pinned);
        try
        {
            return _device.CreateBuffer(new BufferDescription
            {
                BindFlags = bindFlags,
                ByteWidth = (uint)byteWidth,
                Usage = ResourceUsage.Immutable,
            }, new SubresourceData(handle.AddrOfPinnedObject(), (uint)byteWidth));
        }
        finally
        {
            handle.Free();
        }
    }

    private static (SonicVertex[] Vertices, uint[] Indices) BuildSonicCubeGeometry()
    {
        const float half = 0.5f;
        var vertices = new[]
        {
            new SonicVertex(new Vector3(-half, 0f, -half), new Vector3(0f, 0f, -1f), new Vector2(0f, 1f)),
            new SonicVertex(new Vector3( half, 0f, -half), new Vector3(0f, 0f, -1f), new Vector2(1f, 1f)),
            new SonicVertex(new Vector3( half, 1f, -half), new Vector3(0f, 0f, -1f), new Vector2(1f, 0f)),
            new SonicVertex(new Vector3(-half, 1f, -half), new Vector3(0f, 0f, -1f), new Vector2(0f, 0f)),
            new SonicVertex(new Vector3( half, 0f,  half), new Vector3(0f, 0f, 1f), new Vector2(0f, 1f)),
            new SonicVertex(new Vector3(-half, 0f,  half), new Vector3(0f, 0f, 1f), new Vector2(1f, 1f)),
            new SonicVertex(new Vector3(-half, 1f,  half), new Vector3(0f, 0f, 1f), new Vector2(1f, 0f)),
            new SonicVertex(new Vector3( half, 1f,  half), new Vector3(0f, 0f, 1f), new Vector2(0f, 0f)),
            new SonicVertex(new Vector3(-half, 0f,  half), new Vector3(-1f, 0f, 0f), new Vector2(0f, 1f)),
            new SonicVertex(new Vector3(-half, 0f, -half), new Vector3(-1f, 0f, 0f), new Vector2(1f, 1f)),
            new SonicVertex(new Vector3(-half, 1f, -half), new Vector3(-1f, 0f, 0f), new Vector2(1f, 0f)),
            new SonicVertex(new Vector3(-half, 1f,  half), new Vector3(-1f, 0f, 0f), new Vector2(0f, 0f)),
            new SonicVertex(new Vector3( half, 0f, -half), new Vector3(1f, 0f, 0f), new Vector2(0f, 1f)),
            new SonicVertex(new Vector3( half, 0f,  half), new Vector3(1f, 0f, 0f), new Vector2(1f, 1f)),
            new SonicVertex(new Vector3( half, 1f,  half), new Vector3(1f, 0f, 0f), new Vector2(1f, 0f)),
            new SonicVertex(new Vector3( half, 1f, -half), new Vector3(1f, 0f, 0f), new Vector2(0f, 0f)),
            new SonicVertex(new Vector3(-half, 1f, -half), new Vector3(0f, 1f, 0f), new Vector2(0f, 1f)),
            new SonicVertex(new Vector3( half, 1f, -half), new Vector3(0f, 1f, 0f), new Vector2(1f, 1f)),
            new SonicVertex(new Vector3( half, 1f,  half), new Vector3(0f, 1f, 0f), new Vector2(1f, 0f)),
            new SonicVertex(new Vector3(-half, 1f,  half), new Vector3(0f, 1f, 0f), new Vector2(0f, 0f)),
            new SonicVertex(new Vector3(-half, 0f,  half), new Vector3(0f, -1f, 0f), new Vector2(0f, 1f)),
            new SonicVertex(new Vector3( half, 0f,  half), new Vector3(0f, -1f, 0f), new Vector2(1f, 1f)),
            new SonicVertex(new Vector3( half, 0f, -half), new Vector3(0f, -1f, 0f), new Vector2(1f, 0f)),
            new SonicVertex(new Vector3(-half, 0f, -half), new Vector3(0f, -1f, 0f), new Vector2(0f, 0f)),
        };
        var indices = new uint[]
        {
            0, 1, 2, 0, 2, 3,
            4, 5, 6, 4, 6, 7,
            8, 9, 10, 8, 10, 11,
            12, 13, 14, 12, 14, 15,
            16, 17, 18, 16, 18, 19,
            20, 21, 22, 20, 22, 23,
        };
        return (vertices, indices);
    }

    private static (SonicVertex[] Vertices, uint[] Indices) BuildSonicColumnGeometry(int sides)
    {
        sides = Math.Clamp(sides, 6, 16);
        const float radius = 0.5f;
        var vertices = new List<SonicVertex>(sides * 4 + sides + 2);
        var indices = new List<uint>(sides * 9);

        // Keep each side face independent so its radial normal produces crisp metallic facets.
        for (var i = 0; i < sides; i++)
        {
            var a0 = i * MathF.Tau / sides;
            var a1 = (i + 1) * MathF.Tau / sides;
            var p0 = new Vector3(MathF.Cos(a0) * radius, 0f, MathF.Sin(a0) * radius);
            var p1 = new Vector3(MathF.Cos(a1) * radius, 0f, MathF.Sin(a1) * radius);
            var mid = (a0 + a1) * 0.5f;
            var normal = new Vector3(MathF.Cos(mid), 0f, MathF.Sin(mid));
            var start = (uint)vertices.Count;
            vertices.Add(new SonicVertex(p0, normal, new Vector2(0f, 1f)));
            vertices.Add(new SonicVertex(p1, normal, new Vector2(1f, 1f)));
            vertices.Add(new SonicVertex(p1 + Vector3.UnitY, normal, new Vector2(1f, 0f)));
            vertices.Add(new SonicVertex(p0 + Vector3.UnitY, normal, new Vector2(0f, 0f)));
            indices.Add(start);
            indices.Add(start + 1);
            indices.Add(start + 2);
            indices.Add(start);
            indices.Add(start + 2);
            indices.Add(start + 3);
        }

        // A separate cap gives the top a true upward normal and circular UVs for cap glow.
        var capCenter = (uint)vertices.Count;
        vertices.Add(new SonicVertex(new Vector3(0f, 1f, 0f), Vector3.UnitY, new Vector2(0.5f, 0.5f)));
        for (var i = 0; i <= sides; i++)
        {
            var angle = i * MathF.Tau / sides;
            var x = MathF.Cos(angle) * radius;
            var z = MathF.Sin(angle) * radius;
            vertices.Add(new SonicVertex(
                new Vector3(x, 1f, z),
                Vector3.UnitY,
                new Vector2(x / (radius * 2f) + 0.5f, z / (radius * 2f) + 0.5f)));
        }
        for (var i = 0; i < sides; i++)
        {
            indices.Add(capCenter);
            indices.Add(capCenter + (uint)i + 1);
            indices.Add(capCenter + (uint)i + 2);
        }

        return (vertices.ToArray(), indices.ToArray());
    }

    private static (SonicVertex[] Vertices, uint[] Indices) BuildSonicDiscGeometry(float radius, int segments)
    {
        segments = Math.Clamp(segments, 48, 384);
        var vertices = new SonicVertex[segments + 2];
        vertices[0] = new SonicVertex(new Vector3(0f, -1.030f, 0f), Vector3.UnitY, new Vector2(0.5f, 0.5f));
        for (var i = 0; i <= segments; i++)
        {
            var angle = (i / (float)segments) * MathF.Tau;
            var x = MathF.Cos(angle) * radius;
            var z = MathF.Sin(angle) * radius;
            vertices[i + 1] = new SonicVertex(new Vector3(x, -1.030f, z), Vector3.UnitY, new Vector2(x / (radius * 2f) + 0.5f, z / (radius * 2f) + 0.5f));
        }
        var indices = new uint[segments * 3];
        for (var i = 0; i < segments; i++)
        {
            var offset = i * 3;
            indices[offset] = 0;
            indices[offset + 1] = (uint)(i + 1);
            indices[offset + 2] = (uint)(i + 2);
        }
        return (vertices, indices);
    }

    private static int ResolveSonicTerrainGrid(WallpaperState state)
    {
        return state.Quality switch
        {
            "eco" => SonicTerrainGridLow,
            "balanced" => SonicTerrainGridMedium,
            "ultra" => SonicTerrainGridUltra,
            _ => SonicTerrainGridHigh,
        };
    }

    private void UpdateSonicAudioMapping(WallpaperState state, float time, float dt)
    {
        if (time - _sonicLastAudioTime > 0.75f) _sonicAudioInitialized = false;
        _sonicLastAudioTime = time;

        var sonicSubBass = state.HasSonicBands ? state.SonicSubBass : state.SubBass;
        var sonicBassBody = state.HasSonicBands ? state.SonicBassBody : state.BassBody;
        var sonicLowMid = state.HasSonicBands ? state.SonicLowMid : state.LowMid;
        var sonicMidFlow = state.HasSonicBands ? state.SonicMidFlow : state.MidFlow;
        var sonicHighMid = state.HasSonicBands ? state.SonicHighMid : state.HighMid;
        var sonicPresence = state.HasSonicBands ? state.SonicPresence : state.Presence;
        var sonicBrilliance = state.HasSonicBands ? state.SonicBrilliance : state.Brilliance;
        var sonicAir = state.HasSonicBands ? state.SonicAir : state.Air;
        var sonicBandPeak = MathF.Max(
            MathF.Max(MathF.Max(sonicSubBass, sonicBassBody), MathF.Max(sonicLowMid, sonicMidFlow)),
            MathF.Max(MathF.Max(sonicHighMid, sonicPresence), MathF.Max(sonicBrilliance, sonicAir)));
        var audible = SmoothStep(
            0.012f,
            0.10f,
            MathF.Max(
                state.Energy,
                MathF.Max(sonicBandPeak, MathF.Max(state.KickEnvelope, state.VisualImpactStrength))
            )
        );
        var rawBandsA = new Vector4(
            Math.Clamp(sonicSubBass * state.VisualEqLowBreath, 0f, 1.45f),
            Math.Clamp(sonicBassBody * state.VisualEqLowWeight, 0f, 1.45f),
            Math.Clamp(sonicLowMid * state.VisualEqSlowFlow, 0f, 1.35f),
            Math.Clamp(sonicMidFlow * state.VisualEqMidDirection, 0f, 1.35f)
        ) * audible;
        var rawBandsB = new Vector4(
            Math.Clamp(sonicHighMid * state.VisualEqEdgePeak, 0f, 1.35f),
            Math.Clamp(sonicPresence * state.VisualEqSparkle, 0f, 1.35f),
            Math.Clamp(sonicBrilliance * state.VisualEqSparkle, 0f, 1.35f),
            Math.Clamp(sonicAir * state.VisualEqAirDust, 0f, 1.25f)
        ) * audible;
        var rawKick = Math.Clamp(
            MathF.Max(
                state.KickEnvelope * state.VisualEqKickSoft * state.VisualEqKickRelease * MathF.Max(0.32f, state.KickConfidence),
                state.VisualImpactStrength * 0.72f + state.VisualImpactPulse * 0.28f
            ) * audible,
            0f,
            1.45f
        );
        var rawEnergy = Math.Clamp(
            rawBandsA.X * 0.20f
            + rawBandsA.Y * 0.24f
            + rawBandsA.Z * 0.12f
            + rawBandsA.W * 0.14f
            + rawBandsB.X * 0.10f
            + rawBandsB.Y * 0.08f
            + rawBandsB.Z * 0.05f
            + rawBandsB.W * 0.03f
            + rawKick * 0.18f
            + state.VisualStructure * 0.08f * audible,
            0f,
            1.35f
        );
        if (!_sonicAudioInitialized)
        {
            _sonicAudioInitialized = true;
            _sonicEnergyFast = rawEnergy;
            _sonicEnergySlow = rawEnergy;
            _sonicEnergyPool = rawEnergy * 0.52f;
            _sonicLiftDrive = 0f;
            _sonicPreviousRawKick = rawKick;
            _sonicMappedKick = MathF.Min(rawKick, 0.24f + _sonicEnergyPool * 0.68f);
            _sonicMappedBandsA = rawBandsA * (0.72f + _sonicEnergyPool * 0.24f);
            _sonicMappedBandsB = rawBandsB * (0.76f + _sonicEnergyPool * 0.16f);
            _sonicFallbackKickPrev = rawKick;
            _sonicWaveReservoirCharge = rawEnergy * 0.18f;
            return;
        }

        _sonicEnergyFast = SmoothMacro(_sonicEnergyFast, rawEnergy, dt, 0.115f, 1.45f, 0.90f);
        _sonicEnergySlow = SmoothMacro(_sonicEnergySlow, rawEnergy, dt, 0.012f, 0.24f, 0.15f);
        var liftTarget = Math.Clamp(
            SmoothStep(0.045f, 0.26f, _sonicEnergyFast - _sonicEnergySlow) * 0.72f
            + SmoothStep(0.58f, 1.02f, rawEnergy) * 0.28f,
            0f,
            1.15f
        );
        _sonicLiftDrive = SmoothMacro(_sonicLiftDrive, liftTarget, dt, 0.080f, 0.86f, 0.34f);
        var poolTarget = Math.Clamp(
            _sonicEnergySlow * 0.58f
            + _sonicEnergyFast * 0.16f
            + state.VisualStructure * 0.12f * audible
            + state.Energy * 0.10f * audible,
            0f,
            1.08f
        );
        _sonicEnergyPool = SmoothMacro(_sonicEnergyPool, poolTarget, dt, 0.032f, 0.34f, 0.17f);

        var kickRise = MathF.Max(0f, rawKick - _sonicPreviousRawKick);
        _sonicPreviousRawKick = SmoothMacro(_sonicPreviousRawKick, rawKick, dt, 0.20f, 3.2f, 1.8f);
        var waveTarget = Math.Clamp(
            rawKick * 0.56f
            + SmoothStep(0.045f, 0.22f, kickRise) * 0.58f
            + _sonicLiftDrive * 0.24f,
            0f,
            1.25f
        );
        var waveCeiling = 0.24f + _sonicEnergyPool * 0.68f;
        waveTarget = MathF.Min(waveTarget, waveCeiling);

        var lowPoolScale = state.HasSonicBands ? 1f : 0.70f + _sonicEnergyPool * 0.27f;
        var midPoolScale = state.HasSonicBands ? 1f : 0.76f + _sonicEnergyPool * 0.17f + _sonicLiftDrive * 0.07f;
        var highPoolScale = state.HasSonicBands ? 1f : 0.74f + _sonicEnergyFast * 0.11f + _sonicMappedKick * 0.10f;
        var targetA = new Vector4(
            rawBandsA.X * lowPoolScale,
            rawBandsA.Y * lowPoolScale,
            rawBandsA.Z * midPoolScale,
            rawBandsA.W * midPoolScale
        );
        var targetB = rawBandsB * highPoolScale;
        if (state.HasSonicBands)
        {
            _sonicMappedKick = SmoothMacro(_sonicMappedKick, waveTarget, dt, 0.19f, 3.2f, 1.55f);
            _sonicMappedBandsA = SmoothSonicBands(_sonicMappedBandsA, targetA, dt, 0.14f, 2.35f, 1.15f);
            _sonicMappedBandsB = SmoothSonicBands(_sonicMappedBandsB, targetB, dt, 0.18f, 3.0f, 1.75f);
        }
        else
        {
            _sonicMappedKick = SmoothMacro(_sonicMappedKick, waveTarget, dt, 0.15f, 2.15f, 0.92f);
            _sonicMappedBandsA = SmoothSonicBands(_sonicMappedBandsA, targetA, dt, 0.048f, 0.36f, 0.19f);
            _sonicMappedBandsB = SmoothSonicBands(_sonicMappedBandsB, targetB, dt, 0.056f, 0.42f, 0.23f);
        }
    }

    private static Vector4 SmoothSonicBands(Vector4 current, Vector4 target, float dt, float alpha60, float risePerSecond, float fallPerSecond)
    {
        return new Vector4(
            SmoothMacro(current.X, target.X, dt, alpha60, risePerSecond, fallPerSecond),
            SmoothMacro(current.Y, target.Y, dt, alpha60, risePerSecond, fallPerSecond),
            SmoothMacro(current.Z, target.Z, dt, alpha60, risePerSecond, fallPerSecond),
            SmoothMacro(current.W, target.W, dt, alpha60, risePerSecond, fallPerSecond)
        );
    }

    private void UpdateSonicBeatPulse(WallpaperState state, float time, float dt)
    {
        var triggered = false;
        if (state.BeatSerial > 0 && state.BeatSerial != _sonicBeatSerial)
        {
            _sonicBeatSerial = state.BeatSerial;
            if (MathF.Max(state.BeatStrength, state.BeatImpact) >= 0.12f
                && time - _sonicLastBeatAt >= 0.11f)
            {
                StartSonicBeatPulse(
                    time,
                    state.BeatStrength,
                    state.BeatImpact,
                    state.BeatCombo,
                    state.BeatPhase,
                    state.BeatTempoConfidence);
                triggered = true;
            }
        }

        var fallbackKick = Math.Clamp(
            state.KickEnvelope * MathF.Max(0.28f, state.KickConfidence),
            0f,
            1.4f);
        var fallbackRise = fallbackKick - _sonicFallbackKickPrev;
        _sonicFallbackKickPrev = SmoothMacro(_sonicFallbackKickPrev, fallbackKick, dt, 0.24f, 3.6f, 1.9f);
        if (!triggered
            && state.BeatTempoConfidence < 0.20f
            && time - _sonicLastBeatAt >= 0.12f
            && fallbackKick > 0.30f
            && fallbackRise > 0.055f)
        {
            StartSonicBeatPulse(time, fallbackKick, fallbackKick, "beat", 0f, 0f);
        }

        _sonicBeatAge += dt;
        const float attack = 0.026f;
        const float hold = 0.040f;
        const float release = 0.220f;
        if (_sonicBeatAge < attack)
        {
            _sonicBeatEnvelope = SmoothStep(0f, attack, _sonicBeatAge);
        }
        else if (_sonicBeatAge < attack + hold)
        {
            _sonicBeatEnvelope = 1f;
        }
        else
        {
            var releaseT = (_sonicBeatAge - attack - hold) / release;
            _sonicBeatEnvelope = 1f - SmoothStep(0f, 1f, releaseT);
        }
        if (_sonicBeatAge >= attack + hold + release) _sonicBeatEnvelope = 0f;
    }

    private void StartSonicBeatPulse(
        float time,
        float strength,
        float impact,
        string combo,
        float phase,
        float tempoConfidence)
    {
        _sonicLastBeatAt = time;
        _sonicBeatAge = 0f;
        _sonicBeatStrength = Math.Clamp(strength, 0.16f, 1f);
        _sonicBeatImpact = Math.Clamp(impact, 0f, 1f);
        _sonicBeatCombo = SonicBeatComboCode(combo);
        _sonicBeatPhase = Math.Clamp(phase, 0f, 1f);
        _sonicBeatTempoConfidence = Math.Clamp(tempoConfidence, 0f, 1f);

        var sequence = _sonicBeatLaneSequence++ & 3;
        _sonicBeatLane = sequence switch
        {
            0 => 0f,
            1 => 1f,
            2 => 2f,
            _ => 1f,
        };
        if (_sonicBeatCombo is 1f or 2f) _sonicBeatLane = 3f;

        if (_sonicBeatCombo is 1f or 2f
            && MathF.Max(_sonicBeatStrength, _sonicBeatImpact) >= 0.66f
            && CanSpawnSonicMainWave(1f))
        {
            SpawnSonicRipple(Vector2.Zero, 0.42f + _sonicBeatStrength * 0.34f, 0f);
        }
    }

    private static float SonicBeatComboCode(string combo)
    {
        return (combo ?? "").Trim().ToLowerInvariant() switch
        {
            "downbeat" => 1f,
            "drop" => 2f,
            "accent" => 3f,
            "rebound" => 4f,
            "push" => 5f,
            _ => 0f,
        };
    }

    private void RenderSonicPreset(WallpaperState state, float time)
    {
        var dt = Math.Clamp(time - _lastRenderTime, 0f, 0.06f);
        _lastRenderTime = time;
        _sonicSpinClock = Wrap(_sonicSpinClock + dt * SonicRecordSpinSpeed, MathF.Tau);
        UpdateSonicAudioMapping(state, time, dt);
        UpdateSonicBeatPulse(state, time, dt);
        UpdateSonicRipples(state, time, dt);
        UpdateSonicMeteors(state, time, dt);
        var sonicTerrainGrid = ResolveSonicTerrainGrid(state);
        var sonicTerrainInstanceCount = sonicTerrainGrid * sonicTerrainGrid;
        var sonicTerrainInstanceCountU = (uint)sonicTerrainInstanceCount;
        if (_lastLoggedTexturePreset != state.Preset || _lastLoggedSonicTerrainGrid != sonicTerrainGrid)
        {
            _lastLoggedTexturePreset = state.Preset;
            _lastLoggedSonicTerrainGrid = sonicTerrainGrid;
            _log($"render-preset preset={state.Preset} sonic-native quality={state.Quality} grid={sonicTerrainGrid} instances={sonicTerrainInstanceCount}");
        }

        var sceneTarget = _sonicSceneTarget ?? _renderTarget;
        if (sceneTarget == null) return;
        _context.OMSetRenderTargets(sceneTarget, _sonicDepthView);
        _context.RSSetViewport(0, 0, _width, _height);
        _context.ClearRenderTargetView(sceneTarget, DebugClearColor());
        if (_sonicDepthView != null)
        {
            _context.ClearDepthStencilView(_sonicDepthView, DepthStencilClearFlags.Depth, 1f, 0);
        }
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(_sonicInputLayout);
        _context.VSSetShader(_sonicVertexShader);
        _context.VSSetConstantBuffer(0, _sonicConstantBuffer);
        _context.GSSetShader(null);
        _context.PSSetShader(_sonicPixelShader);
        _context.PSSetConstantBuffer(0, _sonicConstantBuffer);
        unsafe
        {
            float* blendFactor = stackalloc float[4] { 0f, 0f, 0f, 0f };
            _context.OMSetBlendState(_blendState, blendFactor, uint.MaxValue);
        }

        var useTexture = LoadTextureIfNeeded(state.Texture, state.Preset, false);
        if (useTexture) BindPixelTexture();

        var constants = BuildSonicConstants(state, time, 0f, useTexture, sonicTerrainGrid);
        constants.Params.W = 0f;
        UploadSonicConstants(constants);
        _context.IASetVertexBuffer(0, _sonicColumnVertexBuffer, (uint)Marshal.SizeOf<SonicVertex>(), 0);
        _context.IASetIndexBuffer(_sonicColumnIndexBuffer, Format.R32_UInt, 0);
        _context.DrawIndexedInstanced((uint)_sonicColumnIndexCount, sonicTerrainInstanceCountU, 0, 0, 0);

        constants.Params.W = 1f;
        UploadSonicConstants(constants);
        _context.IASetVertexBuffer(0, _sonicDiscVertexBuffer, (uint)Marshal.SizeOf<SonicVertex>(), 0);
        _context.IASetIndexBuffer(_sonicDiscIndexBuffer, Format.R32_UInt, 0);
        _context.DrawIndexed((uint)_sonicDiscIndexCount, 0, 0);

        unsafe
        {
            float* blendFactor = stackalloc float[4] { 0f, 0f, 0f, 0f };
            _context.OMSetBlendState(_additiveBlendState, blendFactor, uint.MaxValue);
        }

        _context.IASetVertexBuffer(0, _sonicColumnVertexBuffer, (uint)Marshal.SizeOf<SonicVertex>(), 0);
        _context.IASetIndexBuffer(_sonicColumnIndexBuffer, Format.R32_UInt, 0);
        constants.Params.W = 8f;
        UploadSonicConstants(constants);
        _context.DrawIndexedInstanced((uint)_sonicColumnIndexCount, sonicTerrainInstanceCountU, 0, 0, 0);

        _context.IASetVertexBuffer(0, _sonicCubeVertexBuffer, (uint)Marshal.SizeOf<SonicVertex>(), 0);
        _context.IASetIndexBuffer(_sonicCubeIndexBuffer, Format.R32_UInt, 0);

        constants.Params.W = 4f;
        UploadSonicConstants(constants);
        _context.DrawIndexedInstanced((uint)_sonicCubeIndexCount, SonicFloatingBlockCount, 0, 0, 0);

        constants.Params.W = 5f;
        UploadSonicConstants(constants);
        _context.DrawIndexedInstanced((uint)_sonicCubeIndexCount, SonicMeteorTrailParticleCount, 0, 0, 0);

        constants.Params.W = 7f;
        UploadSonicConstants(constants);
        _context.DrawIndexedInstanced((uint)_sonicCubeIndexCount, SonicImpactSparkCount, 0, 0, 0);

        constants.Params.W = 3f;
        UploadSonicConstants(constants);
        _context.DrawIndexedInstanced((uint)_sonicCubeIndexCount, SonicMeteorCount, 0, 0, 0);

        constants.Params.W = 2f;
        UploadSonicConstants(constants);
        _context.DrawIndexedInstanced((uint)_sonicCubeIndexCount, SonicMeteorCount, 0, 0, 0);

        RenderSonicEmissionSource(constants, sonicTerrainInstanceCountU);
        ApplySonicBloom(state);
        if (string.IsNullOrWhiteSpace(_sonicDebugPass))
        {
            RenderLyric(state, time);
        }
        _swapChain.Present(1, PresentFlags.None);
    }

    private void RenderSonicEmissionSource(SonicConstants constants, uint terrainInstanceCount)
    {
        if (_sonicEmissionTarget == null) return;

        _context.OMSetRenderTargets(_sonicEmissionTarget, _sonicDepthView);
        _context.RSSetViewport(0, 0, _width, _height);
        _context.ClearRenderTargetView(_sonicEmissionTarget, new Color4(0f, 0f, 0f, 0f));
        if (_sonicDepthView != null)
        {
            _context.ClearDepthStencilView(_sonicDepthView, DepthStencilClearFlags.Depth, 1f, 0);
        }
        unsafe
        {
            float* blendFactor = stackalloc float[4] { 0f, 0f, 0f, 0f };
            _context.OMSetBlendState(_additiveBlendState, blendFactor, uint.MaxValue);
        }

        _context.IASetVertexBuffer(0, _sonicCubeVertexBuffer, (uint)Marshal.SizeOf<SonicVertex>(), 0);
        _context.IASetIndexBuffer(_sonicCubeIndexBuffer, Format.R32_UInt, 0);
        constants.Params.W = 4f;
        UploadSonicConstants(constants);
        _context.DrawIndexedInstanced((uint)_sonicCubeIndexCount, SonicFloatingBlockCount, 0, 0, 0);
        constants.Params.W = 5f;
        UploadSonicConstants(constants);
        _context.DrawIndexedInstanced((uint)_sonicCubeIndexCount, SonicMeteorTrailParticleCount, 0, 0, 0);
        constants.Params.W = 7f;
        UploadSonicConstants(constants);
        _context.DrawIndexedInstanced((uint)_sonicCubeIndexCount, SonicImpactSparkCount, 0, 0, 0);
        constants.Params.W = 3f;
        UploadSonicConstants(constants);
        _context.DrawIndexedInstanced((uint)_sonicCubeIndexCount, SonicMeteorCount, 0, 0, 0);
        constants.Params.W = 2f;
        UploadSonicConstants(constants);
        _context.DrawIndexedInstanced((uint)_sonicCubeIndexCount, SonicMeteorCount, 0, 0, 0);
    }

    private void ApplySonicBloom(WallpaperState state)
    {
        if (_renderTarget == null || _sonicSceneView == null || _sonicSceneTarget == null || _sonicEmissionView == null)
        {
            return;
        }
        if (_sonicBloomHalfAView == null || _sonicBloomHalfATarget == null
            || _sonicBloomHalfBView == null || _sonicBloomHalfBTarget == null
            || _sonicBloomQuarterAView == null || _sonicBloomQuarterATarget == null
            || _sonicBloomQuarterBView == null || _sonicBloomQuarterBTarget == null)
        {
            return;
        }

        var bloomEnabled = state.VisualBloomEnabled && state.VisualBloom > 0.01f;
        var backgroundLuma = _debugVisible
            ? (string.Equals(_debugBackground, "light", StringComparison.OrdinalIgnoreCase) ? 0.82f : 0.08f)
            : state.BackgroundLuma;
        var backgroundContrast = _debugVisible
            ? (string.Equals(_debugBackground, "light", StringComparison.OrdinalIgnoreCase) ? 0.46f : 0.72f)
            : state.BackgroundContrast;
        var intensity = bloomEnabled
            ? Math.Clamp(0.34f + state.VisualBloom * 0.68f + state.Air * 0.095f + state.Brilliance * 0.090f, 0.18f, 1.48f)
            : 0f;
        intensity *= 1.12f + (0.78f - 1.12f) * Math.Clamp(backgroundLuma, 0f, 1f);
        intensity *= 0.96f + (1.04f - 0.96f) * Math.Clamp(backgroundContrast, 0f, 1f);
        // The bright pass reads a dedicated emission RT, so a low threshold is safe and
        // preserves small metallic cap highlights without admitting the desktop scene.
        var threshold = Math.Clamp(0.158f - state.VisualBloom * 0.026f - state.Brilliance * 0.014f + backgroundLuma * 0.034f, 0.10f, 0.20f);
        if (!string.IsNullOrWhiteSpace(_sonicDebugPass))
        {
            var debugSignature = FormattableString.Invariant(
                $"{_sonicDebugPass}|threshold={threshold:0.000}|intensity={intensity:0.000}|visualBloom={state.VisualBloom:0.000}|enabled={state.VisualBloomEnabled}");
            if (!string.Equals(_lastLoggedSonicBloomDebug, debugSignature, StringComparison.Ordinal))
            {
                _lastLoggedSonicBloomDebug = debugSignature;
                _log($"sonic-bloom-debug pass={_sonicDebugPass} threshold={threshold.ToString("0.000", CultureInfo.InvariantCulture)} intensity={intensity.ToString("0.000", CultureInfo.InvariantCulture)} visualBloom={state.VisualBloom.ToString("0.000", CultureInfo.InvariantCulture)} enabled={state.VisualBloomEnabled}");
            }
        }
        ClearSonicBloomShaderResources();

        if (_sonicDebugPass == "scene")
        {
            RenderSonicBloomPass(
                _renderTarget,
                _sonicSceneView,
                null,
                null,
                _width,
                _height,
                _width,
                _height,
                5f,
                threshold,
                Math.Max(0.18f, intensity));
            ClearSonicBloomShaderResources();
            return;
        }

        if (_sonicDebugPass == "bright")
        {
            RenderSonicBloomPass(
                _renderTarget,
                _sonicEmissionView,
                null,
                null,
                _width,
                _height,
                _width,
                _height,
                0f,
                threshold,
                Math.Max(0.18f, intensity));
            ClearSonicBloomShaderResources();
            return;
        }

        RenderSonicBloomPass(
            _sonicBloomHalfATarget,
            _sonicEmissionView,
            null,
            null,
            _width,
            _height,
            Math.Max(1, _width / 2),
            Math.Max(1, _height / 2),
            0f,
            threshold,
            intensity);
        RenderSonicBloomPass(
            _sonicBloomHalfBTarget,
            _sonicBloomHalfAView,
            null,
            null,
            Math.Max(1, _width / 2),
            Math.Max(1, _height / 2),
            Math.Max(1, _width / 2),
            Math.Max(1, _height / 2),
            1f,
            threshold,
            intensity);
        RenderSonicBloomPass(
            _sonicBloomHalfATarget,
            _sonicBloomHalfBView,
            null,
            null,
            Math.Max(1, _width / 2),
            Math.Max(1, _height / 2),
            Math.Max(1, _width / 2),
            Math.Max(1, _height / 2),
            2f,
            threshold,
            intensity);
        RenderSonicBloomPass(
            _sonicBloomQuarterATarget,
            _sonicBloomHalfAView,
            null,
            null,
            Math.Max(1, _width / 2),
            Math.Max(1, _height / 2),
            Math.Max(1, _width / 4),
            Math.Max(1, _height / 4),
            3f,
            threshold,
            intensity);
        RenderSonicBloomPass(
            _sonicBloomQuarterBTarget,
            _sonicBloomQuarterAView,
            null,
            null,
            Math.Max(1, _width / 4),
            Math.Max(1, _height / 4),
            Math.Max(1, _width / 4),
            Math.Max(1, _height / 4),
            1f,
            threshold,
            intensity);
        RenderSonicBloomPass(
            _sonicBloomQuarterATarget,
            _sonicBloomQuarterBView,
            null,
            null,
            Math.Max(1, _width / 4),
            Math.Max(1, _height / 4),
            Math.Max(1, _width / 4),
            Math.Max(1, _height / 4),
            2f,
            threshold,
            intensity);
        if (_sonicDebugPass == "bloom")
        {
            RenderSonicBloomPass(
                _renderTarget,
                _sonicSceneView,
                _sonicBloomHalfAView,
                _sonicBloomQuarterATarget != null ? _sonicBloomQuarterAView : null,
                _width,
                _height,
                _width,
                _height,
                6f,
                threshold,
                Math.Max(0.18f, intensity));
            ClearSonicBloomShaderResources();
            return;
        }
        // Normal output and --sonic-debug-pass=composite both use this final scene+bloom composite.
        RenderSonicBloomPass(
            _renderTarget,
            _sonicSceneView,
            _sonicBloomHalfAView,
            _sonicBloomQuarterATarget != null ? _sonicBloomQuarterAView : null,
            _width,
            _height,
            _width,
            _height,
            4f,
            threshold,
            intensity);
        ClearSonicBloomShaderResources();
    }

    private void RenderSonicBloomPass(
        ID3D11RenderTargetView target,
        ID3D11ShaderResourceView source,
        ID3D11ShaderResourceView? bloomA,
        ID3D11ShaderResourceView? bloomB,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        float mode,
        float threshold,
        float intensity)
    {
        ClearSonicBloomShaderResources();
        _context.OMSetRenderTargets(target);
        _context.RSSetViewport(0, 0, targetWidth, targetHeight);
        _context.ClearRenderTargetView(target, new Color4(0f, 0f, 0f, 0f));
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(null);
        _context.VSSetShader(_bloomVertexShader);
        _context.GSSetShader(null);
        _context.PSSetShader(_bloomPixelShader);
        _context.PSSetConstantBuffer(0, _bloomConstantBuffer);
        _context.PSSetSamplers(0, 1, new[] { _samplerState });
        _context.PSSetShaderResources(0, 1, new[] { source });
        if (bloomA != null) _context.PSSetShaderResources(1, 1, new[] { bloomA });
        if (bloomB != null) _context.PSSetShaderResources(2, 1, new[] { bloomB });
        UploadSonicBloomConstants(new SonicBloomConstants
        {
            SourceSize = new Vector4(sourceWidth, sourceHeight, 1f / Math.Max(1, sourceWidth), 1f / Math.Max(1, sourceHeight)),
            TargetSize = new Vector4(targetWidth, targetHeight, 1f / Math.Max(1, targetWidth), 1f / Math.Max(1, targetHeight)),
            Params = new Vector4(mode, threshold, intensity, Math.Clamp(intensity * 0.72f, 0f, 1.45f)),
            Tint = new Vector4(0.66f, 0.78f, 1.0f, 1f),
        });
        // Post-process targets are cleared and written exactly once. Blending here can
        // attenuate low-alpha bloom energy before the final premultiplied composition.
        _context.OMSetBlendState(null);
        _context.Draw(3, 0);
    }

    private void UploadSonicBloomConstants(SonicBloomConstants constants)
    {
        _context.Map(_bloomConstantBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out var cbMap).CheckError();
        Marshal.StructureToPtr(constants, cbMap.DataPointer, false);
        _context.Unmap(_bloomConstantBuffer, 0);
    }

    private void ClearSonicBloomShaderResources()
    {
        _context.PSSetShaderResources(0, 3, new ID3D11ShaderResourceView[] { null!, null!, null! });
    }

    private SonicConstants BuildSonicConstants(WallpaperState state, float time, float mode, bool useTexture = false, int sonicTerrainGrid = SonicTerrainGridMedium)
    {
        var aspect = _height > 0 ? _width / (float)_height : 1f;
        var depth = Math.Clamp(state.VisualDepth, 0.65f, 1.8f);
        var bandsA = _sonicMappedBandsA;
        var bandsB = _sonicMappedBandsB;
        var lowDrive = Math.Clamp(bandsA.X * 0.45f + bandsA.Y * 0.75f + bandsA.Z * 0.35f, 0f, 1.55f);
        var midDrive = Math.Clamp(bandsA.W * 0.60f + bandsB.X * 0.30f, 0f, 1.35f);
        var highDrive = Math.Clamp(bandsB.Y * 0.45f + bandsB.Z * 0.38f + bandsB.W * 0.30f, 0f, 1.30f);
        var kick = Math.Clamp(_sonicMappedKick, 0f, 1.15f);
        var cameraEnergy = Math.Clamp(lowDrive * 0.18f + midDrive * 0.18f + highDrive * 0.12f + _sonicEnergyPool * 0.34f + kick * 0.18f, 0f, 1.25f);
        var cameraActive = SmoothStep(0.06f, 0.32f, cameraEnergy);
        var idleEye = new Vector3(0f, 13.2f + depth * 0.30f, -2.70f);
        var liveEye = new Vector3(0f, 5.10f + depth * 0.80f, -7.75f - depth * 0.80f);
        var eye = Vector3.Lerp(idleEye, liveEye, cameraActive);
        var target = Vector3.Lerp(new Vector3(0f, -0.82f, 0f), new Vector3(0f, -0.58f, 0f), cameraActive);
        var playerRadiusScale = Math.Clamp((6.4f + _desktopViewControl.Z) / 6.4f, 2.4f / 6.4f, 14f / 6.4f);
        eye = target + (eye - target) * playerRadiusScale;
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        var fov = 0.64f + (0.62f - 0.64f) * cameraActive;
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(fov, Math.Clamp(aspect, 0.45f, 2.8f), 0.08f, 80f);
        var playerDragModel = Matrix4x4.CreateRotationX(_desktopViewControl.X)
            * Matrix4x4.CreateRotationY(_desktopViewControl.Y);
        var viewProjection = playerDragModel * view * projection;
        var intensity = Math.Clamp(state.VisualIntensity, 0.35f, 2.2f);
        var speed = Math.Clamp(state.VisualSpeed, 0.05f, 2.3f);
        var opacity = Math.Clamp(state.Opacity, 0f, 1f);
        var warmth = Math.Clamp(state.Warmth > 0.001f ? state.Warmth : lowDrive / MathF.Max(0.001f, lowDrive + highDrive), 0f, 1f);
        var brightness = Math.Clamp(state.Brightness > 0.001f ? state.Brightness : highDrive / MathF.Max(0.001f, lowDrive + highDrive), 0f, 1f);
        var sharpness = Math.Clamp(state.VisualGloss * 0.42f + bandsB.Y * 0.34f + bandsB.Z * 0.30f, 0f, 1.2f);
        var glowIntensity = Math.Clamp((0.58f + state.VisualBloom * 0.36f) * (state.VisualBloomEnabled ? 1f : 0.62f) * (0.84f + state.VisualColor * 0.18f), 0.35f, 1.65f);
        var terrainRadius = SonicTerrainRadius;
        var terrainSpacing = terrainRadius * 2f / MathF.Max(1f, sonicTerrainGrid - 1f);
        var terrainCell = terrainSpacing * Math.Clamp(1.00f + intensity * 0.035f, 1.00f, 1.08f);
        var backgroundLuma = _debugVisible
            ? (string.Equals(_debugBackground, "light", StringComparison.OrdinalIgnoreCase) ? 0.82f : 0.08f)
            : state.BackgroundLuma;
        var backgroundContrast = _debugVisible
            ? (string.Equals(_debugBackground, "light", StringComparison.OrdinalIgnoreCase) ? 0.46f : 0.72f)
            : state.BackgroundContrast;
        var backgroundBusy = _debugVisible ? 0.10f : state.BackgroundBusy;
        return new SonicConstants
        {
            ViewProjection = viewProjection,
            AudioA = new Vector4(lowDrive, midDrive, highDrive, kick),
            AudioB = bandsA,
            AudioC = bandsB,
            Params = new Vector4(sonicTerrainGrid, terrainRadius, terrainCell, mode),
            Runtime = new Vector4(time, intensity, speed, opacity),
            Primary = state.Primary,
            Secondary = state.Secondary,
            Highlight = state.Highlight,
            Tone = new Vector4(warmth, brightness, sharpness, glowIntensity),
            SonicTexture = new Vector4(useTexture ? 1f : 0f, backgroundLuma, backgroundContrast, backgroundBusy),
            Motion = new Vector4(_sonicSpinClock, SonicRecordSpinSpeed, _sonicEnergyPool, _sonicWaveReservoirCharge),
            BeatEvent = new Vector4(_sonicBeatEnvelope, _sonicBeatStrength, _sonicBeatLane, _sonicBeatCombo),
            BeatTiming = new Vector4(_sonicBeatAge, _sonicBeatPhase, _sonicBeatTempoConfidence, _sonicBeatImpact),
            Ripple0 = SonicRippleVector(0),
            Ripple1 = SonicRippleVector(1),
            Ripple2 = SonicRippleVector(2),
            Ripple3 = SonicRippleVector(3),
            Ripple4 = SonicRippleVector(4),
            Ripple5 = SonicRippleVector(5),
            Ripple6 = SonicRippleVector(6),
            Ripple7 = SonicRippleVector(7),
            Ripple8 = SonicRippleVector(8),
            Ripple9 = SonicRippleVector(9),
            Meteor0 = SonicMeteorVector(0),
            Meteor1 = SonicMeteorVector(1),
            Meteor2 = SonicMeteorVector(2),
            Meteor3 = SonicMeteorVector(3),
            Meteor4 = SonicMeteorVector(4),
            Meteor5 = SonicMeteorVector(5),
            Meteor6 = SonicMeteorVector(6),
            Meteor7 = SonicMeteorVector(7),
            Meteor8 = SonicMeteorVector(8),
            Meteor9 = SonicMeteorVector(9),
            Meteor10 = SonicMeteorVector(10),
            Meteor11 = SonicMeteorVector(11),
            Meteor12 = SonicMeteorVector(12),
            Meteor13 = SonicMeteorVector(13),
            Meteor14 = SonicMeteorVector(14),
            Meteor15 = SonicMeteorVector(15),
            Meteor16 = SonicMeteorVector(16),
            Meteor17 = SonicMeteorVector(17),
            Meteor18 = SonicMeteorVector(18),
            Meteor19 = SonicMeteorVector(19),
            Spark0 = SonicImpactSparkVector(0),
            Spark1 = SonicImpactSparkVector(1),
            Spark2 = SonicImpactSparkVector(2),
            Spark3 = SonicImpactSparkVector(3),
            Spark4 = SonicImpactSparkVector(4),
            Spark5 = SonicImpactSparkVector(5),
            Spark6 = SonicImpactSparkVector(6),
            Spark7 = SonicImpactSparkVector(7),
            Spark8 = SonicImpactSparkVector(8),
            Spark9 = SonicImpactSparkVector(9),
            Spark10 = SonicImpactSparkVector(10),
            Spark11 = SonicImpactSparkVector(11),
            Spark12 = SonicImpactSparkVector(12),
            Spark13 = SonicImpactSparkVector(13),
            Spark14 = SonicImpactSparkVector(14),
            Spark15 = SonicImpactSparkVector(15),
            Spark16 = SonicImpactSparkVector(16),
            Spark17 = SonicImpactSparkVector(17),
            Spark18 = SonicImpactSparkVector(18),
            Spark19 = SonicImpactSparkVector(19),
            Spark20 = SonicImpactSparkVector(20),
            Spark21 = SonicImpactSparkVector(21),
            Spark22 = SonicImpactSparkVector(22),
            Spark23 = SonicImpactSparkVector(23),
        };
    }

    private void UploadSonicConstants(SonicConstants constants)
    {
        _context.Map(_sonicConstantBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out var cbMap).CheckError();
        Marshal.StructureToPtr(constants, cbMap.DataPointer, false);
        _context.Unmap(_sonicConstantBuffer, 0);
    }

    private void UpdateSonicRipples(WallpaperState state, float time, float dt)
    {
        for (var i = 0; i < _sonicRipples.Length; i++)
        {
            if (_sonicRipples[i].Strength <= 0.005f) continue;
            _sonicRipples[i].Age += dt;
            var lifetime = _sonicRipples[i].Type >= 1.5f
                ? 1.50f
                : (_sonicRipples[i].Type >= 0.5f ? 2.20f : 4.25f);
            if (_sonicRipples[i].Age > lifetime)
            {
                _sonicRipples[i].Age = -10f;
                _sonicRipples[i].Strength = 0f;
            }
        }

        // Blue waves are structural accents only. Continuous FFT bands drive
        // column motion; meteor impacts keep their separate purple local wave.
        _sonicWaveReservoirCharge = MathF.Max(0f, _sonicWaveReservoirCharge - dt * 0.45f);
    }

    private bool CanSpawnSonicMainWave(float visualSpeed)
    {
        var speed = 1.18f + MathF.Max(0.05f, visualSpeed) * 0.18f;
        for (var i = 0; i < _sonicRipples.Length; i++)
        {
            var ripple = _sonicRipples[i];
            if (ripple.Strength <= 0.005f || ripple.Age < 0f || ripple.Type >= 1.5f) continue;
            if (ripple.Position.LengthSquared() > 0.0004f) continue;
            var travel = ripple.Age * speed / SonicTerrainRadius;
            if (travel < SonicMainWaveMinSpacing) return false;
        }
        return true;
    }

    private void SpawnSonicRipple(Vector2 position, float strength, float type = 0f)
    {
        var ripple = _sonicRipples[_sonicRippleIndex];
        ripple.Position = position;
        ripple.Age = 0f;
        ripple.Strength = Math.Clamp(strength, 0f, 1.35f);
        ripple.Type = Math.Clamp(type, 0f, 2f);
        _sonicRipples[_sonicRippleIndex] = ripple;
        _sonicRippleIndex = (_sonicRippleIndex + 1) % _sonicRipples.Length;
    }

    private Vector4 SonicRippleVector(int index)
    {
        if (index < 0 || index >= _sonicRipples.Length || _sonicRipples[index].Strength <= 0.005f)
        {
            return new Vector4(0f, 0f, -10f, 0f);
        }
        var ripple = _sonicRipples[index];
        return new Vector4(ripple.Position.X, ripple.Position.Y, ripple.Age + ripple.Type * 10f, ripple.Strength);
    }

    private void UpdateSonicMeteors(WallpaperState state, float time, float dt)
    {
        UpdateSonicImpactSparks(dt);
        for (var i = 0; i < _sonicMeteors.Length; i++)
        {
            if (!_sonicMeteors[i].Active) continue;
            var meteor = _sonicMeteors[i];
            meteor.Age += dt;
            meteor.X += meteor.Vx * dt;
            meteor.Z += meteor.Vz * dt;
            meteor.Y -= meteor.Speed * dt;
            if (meteor.Y <= 0.06f || meteor.Age > 3.6f)
            {
                SpawnSonicRipple(new Vector2(meteor.X, meteor.Z), 0.42f + meteor.Strength * 0.56f, 2f);
                SpawnSonicImpactSparks(new Vector3(meteor.X, 0.34f, meteor.Z), meteor.Strength);
                meteor.Active = false;
                meteor.Strength = 0f;
                meteor.Y = -1000f;
            }
            _sonicMeteors[i] = meteor;
        }

        var meteorDrive = Math.Clamp(
            state.VisualImpactPulse * 0.55f
            + state.KickEnvelope * 0.30f
            + state.Presence * 0.24f
            + state.Brilliance * 0.28f
            + state.Air * 0.16f,
            0f,
            1.6f
        );
        if (meteorDrive > 0.46f && _sonicLastMeteorAt < 0f)
        {
            SpawnSonicMeteor(0.38f + meteorDrive * 0.58f);
            SpawnSonicMeteor(0.30f + meteorDrive * 0.46f);
            _sonicLastMeteorAt = time;
        }
        else if (meteorDrive > 0.46f && time - _sonicLastMeteorAt > MathF.Max(0.55f, 1.48f - meteorDrive * 0.34f))
        {
            SpawnSonicMeteor(0.32f + meteorDrive * 0.64f);
            _sonicLastMeteorAt = time;
        }
    }

    private void SpawnSonicMeteor(float strength)
    {
        var angle = Rand() * MathF.PI;
        var dist = 4.10f + Rand() * 1.10f;
        var targetAngle = angle + (Rand() - 0.5f) * 1.30f;
        var targetRadius = 0.75f + Rand() * 2.65f;
        var targetX = MathF.Cos(targetAngle) * targetRadius;
        var targetZ = MathF.Sin(targetAngle) * targetRadius;
        var meteor = _sonicMeteors[_sonicMeteorIndex];
        meteor.Active = true;
        meteor.X = MathF.Cos(angle) * dist;
        meteor.Z = MathF.Sin(angle) * dist;
        meteor.Y = 1.65f + Rand() * 1.15f;
        meteor.Speed = 0.78f + Rand() * 0.34f + strength * 0.28f;
        var travelTime = MathF.Max(0.65f, meteor.Y / meteor.Speed);
        meteor.Vx = (targetX - meteor.X) / travelTime;
        meteor.Vz = (targetZ - meteor.Z) / travelTime;
        meteor.Strength = Math.Clamp(strength, 0f, 1.45f);
        meteor.Age = 0f;
        _sonicMeteors[_sonicMeteorIndex] = meteor;
        _sonicMeteorIndex = (_sonicMeteorIndex + 1) % _sonicMeteors.Length;
    }

    private void UpdateSonicImpactSparks(float dt)
    {
        for (var i = 0; i < _sonicImpactSparks.Length; i++)
        {
            if (!_sonicImpactSparks[i].Active) continue;
            var spark = _sonicImpactSparks[i];
            spark.Age += dt;
            if (spark.Age >= spark.Life)
            {
                spark.Active = false;
                spark.Strength = 0f;
                spark.X = 0f;
                spark.Y = -1000f;
                spark.Z = 0f;
                _sonicImpactSparks[i] = spark;
                continue;
            }
            spark.Vy -= (3.8f + spark.Strength * 1.8f) * dt;
            spark.X += spark.Vx * dt;
            spark.Y += spark.Vy * dt;
            spark.Z += spark.Vz * dt;
            if (spark.Y < 0.080f)
            {
                spark.Y = 0.080f;
                spark.Vy = MathF.Abs(spark.Vy) * 0.22f;
                spark.Vx *= 0.74f;
                spark.Vz *= 0.74f;
            }
            _sonicImpactSparks[i] = spark;
        }
    }

    private void SpawnSonicImpactSparks(Vector3 position, float strength)
    {
        var count = strength > 1.0f ? 10 : 8;
        for (var i = 0; i < count; i++)
        {
            var angle = RandAngle();
            var speed = 0.58f + Rand() * 1.34f + strength * 0.52f;
            var lift = 0.72f + Rand() * 1.25f + strength * 0.58f;
            var spark = _sonicImpactSparks[_sonicImpactSparkIndex];
            spark.Active = true;
            spark.X = position.X + (Rand() - 0.5f) * 0.18f;
            spark.Y = position.Y + Rand() * 0.18f;
            spark.Z = position.Z + (Rand() - 0.5f) * 0.18f;
            spark.Vx = MathF.Cos(angle) * speed;
            spark.Vy = lift;
            spark.Vz = MathF.Sin(angle) * speed;
            spark.Age = 0f;
            spark.Life = 0.46f + Rand() * 0.44f;
            spark.Strength = Math.Clamp(strength * (0.72f + Rand() * 0.52f), 0f, 1.35f);
            _sonicImpactSparks[_sonicImpactSparkIndex] = spark;
            _sonicImpactSparkIndex = (_sonicImpactSparkIndex + 1) % _sonicImpactSparks.Length;
        }
    }

    private Vector4 SonicMeteorVector(int index)
    {
        if (index < 0 || index >= _sonicMeteors.Length || !_sonicMeteors[index].Active)
        {
            return new Vector4(0f, -1000f, 0f, 0f);
        }
        var meteor = _sonicMeteors[index];
        return new Vector4(meteor.X, meteor.Y, meteor.Z, meteor.Strength);
    }

    private Vector4 SonicImpactSparkVector(int index)
    {
        if (index < 0 || index >= _sonicImpactSparks.Length || !_sonicImpactSparks[index].Active)
        {
            return new Vector4(0f, -1000f, 0f, 0f);
        }
        var spark = _sonicImpactSparks[index];
        var fade = 1f - Math.Clamp(spark.Age / MathF.Max(0.001f, spark.Life), 0f, 1f);
        return new Vector4(spark.X, spark.Y, spark.Z, spark.Strength * fade);
    }

}
