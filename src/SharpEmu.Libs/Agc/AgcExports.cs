// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SharpEmu.Libs.Agc;

public static class AgcExports
{
    private const uint ShaderFileHeader = 0x34333231;
    private const uint ShaderVersion = 0x18;
    private const uint ItNop = 0x10;
    private const uint ItSetBase = 0x11;
    private const uint ItIndexBufferSize = 0x13;
    private const uint ItIndexBase = 0x26;
    private const uint ItIndexType = 0x2A;
    private const uint ItNumInstances = 0x2F;
    private const uint ItDrawIndexOffset2 = 0x35;
    private const uint ItDispatchDirect = 0x15;
    private const uint ItDispatchIndirect = 0x16;
    private const uint ItWaitRegMem = 0x3C;
    private const uint ItEventWrite = 0x46;
    private const uint ItSetShReg = 0x76;
    private const uint ItGetLodStats = 0x8E;
    private const uint RZero = 0x00;
    private const uint RDrawIndexAuto = 0x04;
    private const uint RDrawReset = 0x05;
    private const uint RWaitFlipDone = 0x06;
    private const uint RAcbReset = 0x09;
    private const uint RWaitMem32 = 0x0A;
    private const uint RPushMarker = 0x0B;
    private const uint RPopMarker = 0x0C;
    private const uint RShRegsIndirect = 0x11;
    private const uint RCxRegsIndirect = 0x12;
    private const uint RUcRegsIndirect = 0x13;
    private const uint RAcquireMem = 0x14;
    private const uint RWriteData = 0x15;
    private const uint RWaitMem64 = 0x16;
    private const uint RFlip = 0x17;
    private const uint RReleaseMem = 0x18;
    private const uint RDmaData = 0x19;
    private const uint SpiShaderPgmLoPs = 0x8;
    private const uint SpiShaderPgmHiPs = 0x9;
    private const uint SpiShaderPgmLoEs = 0xC8;
    private const uint SpiShaderPgmHiEs = 0xC9;
    private const uint SpiShaderPgmLoLs = 0x148;
    private const uint SpiShaderPgmHiLs = 0x149;
    private const uint SpiPsInputEna = 0x1B3;
    private const uint SpiPsInputAddr = 0x1B4;
    private const uint ComputePgmLo = 0x20C;
    private const uint ComputePgmHi = 0x20D;
    private const uint SpiPsInputCntl0 = 0x191;
    private const uint VgtPrimitiveType = 0x242;
    private const uint PsTextureUserDataRegister = 0xC;
    private const uint Gen5TextureFormatR8G8B8A8Unorm = 56;
    private const uint Gen5TextureType2D = 9;
    private const ulong VideoOutPixelFormatA8R8G8B8Srgb = 0x80000000;
    private const ulong VideoOutPixelFormatA8B8G8R8Srgb = 0x80002200;
    private const ulong VideoOutPixelFormatB8G8R8A8Unorm = 0x8100000000000000;
    private const ulong VideoOutPixelFormatR8G8B8A8Unorm = 0x8100000022000000;
    private const uint RegisterDefaultsVersion7 = 7;
    private const uint RegisterDefaultsVersion8 = 8;
    private const uint RegisterDefaultsVersion10 = 10;
    private const int RegisterDefaultsSize = 0x40;
    private const int RegisterDefaultBlockSize = 16 * 8;

    private const ulong ShaderUserDataOffset = 0x08;
    private const ulong ShaderCodeOffset = 0x10;
    private const ulong ShaderCxRegistersOffset = 0x18;
    private const ulong ShaderShRegistersOffset = 0x20;
    private const ulong ShaderSpecialsOffset = 0x28;
    private const ulong ShaderInputSemanticsOffset = 0x30;
    private const ulong ShaderOutputSemanticsOffset = 0x38;
    private const ulong ShaderNumInputSemanticsOffset = 0x50;
    private const ulong ShaderNumOutputSemanticsOffset = 0x56;
    private const ulong ShaderTypeOffset = 0x5A;
    private const ulong ShaderNumShRegistersOffset = 0x5C;
    private const ulong CommandBufferCursorUpOffset = 0x10;
    private const ulong CommandBufferCursorDownOffset = 0x18;
    private const ulong CommandBufferCallbackOffset = 0x20;
    private const ulong CommandBufferReservedDwOffset = 0x30;
    private const ulong ShaderSpecialGeCntlOffset = 0x00;
    private const ulong ShaderSpecialVgtShaderStagesEnOffset = 0x08;
    private const ulong ShaderSpecialVgtGsOutPrimTypeOffset = 0x20;
    private const ulong ShaderSpecialGeUserVgprEnOffset = 0x28;
    private const uint CbSetShRegisterRangeMarker = 0x6875000D;
    private static readonly object _submitTraceGate = new();
    private static readonly HashSet<uint> _tracedDcbSizes = new();
    private static readonly HashSet<(ulong Es, ulong Ps, GuestDrawKind Kind)> _tracedShaderTranslations = new();
    private static long _dcbWriteDataTraceCount;
    private static long _dcbWaitRegMemTraceCount;
    private static long _createShaderTraceCount;
    private static long _packetPayloadTraceCount;
    private static long _unsatisfiedWaitTraceCount;
    private static long _shaderTranslationMissTraceCount;
    private static readonly object _softwarePresenterGate = new();
    private static readonly Dictionary<(ulong Source, ulong Destination), ulong> _softwarePresenterFingerprints = new();
    private static readonly object _registerDefaultsGate = new();
    private static readonly ConditionalWeakTable<object, RegisterDefaultsAllocation> _registerDefaultsAllocations = new();
    private static readonly ConditionalWeakTable<object, SubmittedGpuState> _submittedGpuStates = new();

    private static readonly RegisterDefaultGroup[] PrimaryRegisterDefaults =
    [
        new(0, 3, 0x0BC65DA4, [new(0x08F, 0)]),
        new(0, 4, 0x9E5AD592, [new(0x08E, 0)]),
        new(0, 12, 0x6DE4C312, [new(0x203, 0)]),
        new(0, 72, 0x38E92C91,
        [
            new(0x318, 0),
            new(0x31B, 0),
            new(0x31C, 0),
            new(0x31D, 0),
            new(0x31E, 0x48),
            new(0x31F, 0),
            new(0x321, 0),
            new(0x323, 0),
            new(0x324, 0),
            new(0x325, 0),
            new(0x390, 0),
            new(0x398, 0),
            new(0x3A0, 0),
            new(0x3A8, 0),
            new(0x3B0, 0),
            new(0x3B8, 0x0006C000),
        ]),
        new(0, 73, 0x0B177B43, [new(0x00C, 0), new(0x00D, 0x40004000)]),
        new(0, 74, 0x48531062, [new(0x191, 0)]),
        new(0, 76, 0x7690AF6F,
        [
            new(0x10F, 0x4E7E0000),
            new(0x111, 0x4E7E0000),
            new(0x113, 0x4E7E0000),
            new(0x110, 0),
            new(0x112, 0),
            new(0x114, 0),
            new(0x094, 0x80000000),
            new(0x095, 0x40004000),
            new(0x0B4, 0),
            new(0x0B5, 0),
        ]),
        new(1, 13, 0xC918DF3E, [new(0x20C, 0), new(0x20D, 0)]),
        new(1, 14, 0xC9751C9C, [new(0x0C8, 0), new(0x0C9, 0)]),
        new(1, 18, 0xC9E01B31, [new(0x008, 0), new(0x009, 0)]),
        new(2, 3, 0x105971C2, [new(0x25B, 0)]),
        new(2, 7, 0x40D49AD1, [new(0x262, 0)]),
        new(2, 12, 0x9EBFAB10, [new(0x242, 0)]),
    ];

    private static readonly RegisterDefaultGroup[] InternalRegisterDefaults =
    [
        new(0, 0, 0x8FB4EDB5, [new(0x00E, 0)]),
        new(0, 1, 0xB994AD29, [new(0x2AF, 0)]),
        new(0, 2, 0xD427322F, [new(0x314, 0)]),
        new(0, 3, 0xF58FEA31, [new(0x1B5, 0)]),
        new(1, 0, 0x6AC156EF, [new(0x216, 0)]),
        new(1, 1, 0x6AC15610, [new(0x217, 0)]),
        new(1, 2, 0x6AC15009, [new(0x219, 0)]),
        new(1, 3, 0x6AC153BA, [new(0x21A, 0)]),
        new(1, 4, 0xBE7DCD73, [new(0x27D, 0)]),
        new(1, 5, 0x0C4B1438, [new(0x22A, 0)]),
        new(1, 6, 0xDB00D71A, [new(0x204, 0)]),
        new(1, 7, 0xDB00D249, [new(0x205, 0)]),
        new(1, 8, 0xDB00EC60, [new(0x206, 0)]),
        new(1, 9, 0x0C4D6FE4, [new(0x080, 0)]),
        new(1, 10, 0x0C4A80EF, [new(0x100, 0)]),
        new(1, 11, 0x0DD283E7, [new(0x006, 0)]),
        new(1, 12, 0xC620E68C, [new(0x081, 0)]),
        new(1, 13, 0xC67EFACF, [new(0x101, 0)]),
        new(1, 14, 0xD9E6D9F7, [new(0x001, 0)]),
        new(2, 0, 0x31F34B9F, [new(0x24F, 0)]),
        new(2, 1, 0xAC0F9E76, [new(0x80003FFF, 0)]),
        new(2, 2, 0x929FD95D, [new(0x250, 0)]),
    ];

    private readonly record struct TextureDescriptor(
        ulong Address,
        uint Width,
        uint Height,
        uint Format,
        uint TileMode,
        uint Type);

    private sealed class SubmittedDcbState
    {
        public Dictionary<uint, uint> CxRegisters { get; } = new();
        public Dictionary<uint, uint> ShRegisters { get; } = new();
        public Dictionary<uint, uint> UcRegisters { get; } = new();
        public TextureDescriptor? PresenterTexture { get; set; }
        public GuestDrawKind GuestDrawKind { get; set; }
        public bool SawIndexedDraw { get; set; }
    }

    private sealed class SubmittedGpuState
    {
        public object Gate { get; } = new();
        public SubmittedDcbState Graphics { get; } = new();
        public Dictionary<uint, SubmittedDcbState> ComputeQueues { get; } = new();
    }

    private readonly record struct RegisterDefaultValue(uint Offset, uint Value);

    private readonly record struct RegisterDefaultGroup(
        uint Space,
        uint Index,
        uint Type,
        RegisterDefaultValue[] Registers);

    private sealed record RegisterDefaultsAllocation(ulong Primary, ulong Internal);

    [SysAbiExport(
        Nid = "23LRUSvYu1M",
        ExportName = "sceAgcInit",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int Init(CpuContext ctx)
    {
        var stateAddress = ctx[CpuRegister.Rdi];
        var version = (uint)ctx[CpuRegister.Rsi];
        if (stateAddress == 0 || !IsSupportedRegisterDefaultsVersion(version))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        TraceAgc($"agc.init state=0x{stateAddress:X16} version={version}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "2JtWUUiYBXs",
        ExportName = "sceAgcGetRegisterDefaults2",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetRegisterDefaults2(CpuContext ctx) =>
        ReturnRegisterDefaults(ctx, internalDefaults: false);

    [SysAbiExport(
        Nid = "wRbq6ZjNop4",
        ExportName = "sceAgcGetRegisterDefaults2Internal",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetRegisterDefaults2Internal(CpuContext ctx) =>
        ReturnRegisterDefaults(ctx, internalDefaults: true);

    [SysAbiExport(
        Nid = "f3dg2CSgRKY",
        ExportName = "sceAgcCreateShader",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CreateShader(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        var headerAddress = ctx[CpuRegister.Rsi];
        var codeAddress = ctx[CpuRegister.Rdx];
        if (headerAddress == 0 || codeAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadUInt32(ctx, headerAddress, out var fileHeader) ||
            !TryReadUInt32(ctx, headerAddress + sizeof(uint), out var version))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (fileHeader != ShaderFileHeader || version != ShaderVersion)
        {
            TraceCreateShader(destinationAddress, headerAddress, codeAddress, $"invalid-header file=0x{fileHeader:X8} version=0x{version:X8}");
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!RelocatePointerField(ctx, headerAddress + ShaderCxRegistersOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderShRegistersOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderUserDataOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderSpecialsOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderInputSemanticsOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderOutputSemanticsOffset) ||
            !ctx.TryWriteUInt64(headerAddress + ShaderCodeOffset, codeAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!TryReadUInt64(ctx, headerAddress + ShaderUserDataOffset, out var userDataAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (userDataAddress != 0 &&
            (!RelocatePointerField(ctx, userDataAddress) ||
             !RelocatePointerField(ctx, userDataAddress + 0x08) ||
             !RelocatePointerField(ctx, userDataAddress + 0x10) ||
             !RelocatePointerField(ctx, userDataAddress + 0x18) ||
             !RelocatePointerField(ctx, userDataAddress + 0x20)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!PatchShaderProgramRegisters(ctx, headerAddress, codeAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (destinationAddress != 0 &&
            !ctx.TryWriteUInt64(destinationAddress, headerAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceCreateShader(destinationAddress, headerAddress, codeAddress, "ok");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "vcmNN+AAXnY",
        ExportName = "sceAgcSetCxRegIndirectPatchSetAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetCxRegIndirectPatchSetAddress(CpuContext ctx) =>
        SetIndirectPatchAddress(ctx, "cx");

    [SysAbiExport(
        Nid = "Qrj4c+61z4A",
        ExportName = "sceAgcSetShRegIndirectPatchSetAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetShRegIndirectPatchSetAddress(CpuContext ctx) =>
        SetIndirectPatchAddress(ctx, "sh");

    [SysAbiExport(
        Nid = "6lNcCp+fxi4",
        ExportName = "sceAgcSetUcRegIndirectPatchSetAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetUcRegIndirectPatchSetAddress(CpuContext ctx) =>
        SetIndirectPatchAddress(ctx, "uc");

    [SysAbiExport(
        Nid = "d-6uF9sZDIU",
        ExportName = "sceAgcSetCxRegIndirectPatchAddRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetCxRegIndirectPatchAddRegisters(CpuContext ctx) =>
        AddIndirectPatchRegisters(ctx, "cx");

    [SysAbiExport(
        Nid = "z2duB-hHQSM",
        ExportName = "sceAgcSetShRegIndirectPatchAddRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetShRegIndirectPatchAddRegisters(CpuContext ctx) =>
        AddIndirectPatchRegisters(ctx, "sh");

    [SysAbiExport(
        Nid = "vRoArM9zaIk",
        ExportName = "sceAgcSetUcRegIndirectPatchAddRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetUcRegIndirectPatchAddRegisters(CpuContext ctx) =>
        AddIndirectPatchRegisters(ctx, "uc");

    [SysAbiExport(
        Nid = "D9sr1xGUriE",
        ExportName = "sceAgcCreatePrimState",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CreatePrimState(CpuContext ctx)
    {
        var cxRegistersAddress = ctx[CpuRegister.Rdi];
        var ucRegistersAddress = ctx[CpuRegister.Rsi];
        var hullShaderAddress = ctx[CpuRegister.Rdx];
        var geometryShaderAddress = ctx[CpuRegister.Rcx];
        var primitiveType = (uint)ctx[CpuRegister.R8];

        if (cxRegistersAddress == 0 || ucRegistersAddress == 0 || hullShaderAddress != 0 || geometryShaderAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadByte(ctx, geometryShaderAddress + ShaderTypeOffset, out var shaderType) || !IsEsGeometryShaderType(shaderType) ||
            !TryReadUInt64(ctx, geometryShaderAddress + ShaderSpecialsOffset, out var specialsAddress) ||
            specialsAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!CopyShaderRegister(ctx, specialsAddress + ShaderSpecialVgtShaderStagesEnOffset, cxRegistersAddress) ||
            !CopyShaderRegister(ctx, specialsAddress + ShaderSpecialVgtGsOutPrimTypeOffset, cxRegistersAddress + 8) ||
            !CopyShaderRegister(ctx, specialsAddress + ShaderSpecialGeCntlOffset, ucRegistersAddress) ||
            !CopyShaderRegister(ctx, specialsAddress + ShaderSpecialGeUserVgprEnOffset, ucRegistersAddress + 8) ||
            !TryWriteUInt32(ctx, ucRegistersAddress + 16, VgtPrimitiveType) ||
            !TryWriteUInt32(ctx, ucRegistersAddress + 20, primitiveType))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.create_prim_state cx=0x{cxRegistersAddress:X16} uc=0x{ucRegistersAddress:X16} gs=0x{geometryShaderAddress:X16} type={shaderType} prim=0x{primitiveType:X8}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "HV4j+E0MBHE",
        ExportName = "sceAgcCreateInterpolantMapping",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CreateInterpolantMapping(CpuContext ctx)
    {
        var registersAddress = ctx[CpuRegister.Rdi];
        var geometryShaderAddress = ctx[CpuRegister.Rsi];
        var pixelShaderAddress = ctx[CpuRegister.Rdx];

        if (registersAddress == 0 || geometryShaderAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadUInt64(ctx, geometryShaderAddress + ShaderOutputSemanticsOffset, out var outputSemanticsAddress) ||
            !TryReadUInt32(ctx, geometryShaderAddress + ShaderNumOutputSemanticsOffset, out var outputSemanticsCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ulong inputSemanticsAddress = 0;
        if (pixelShaderAddress != 0 &&
            (!TryReadUInt64(ctx, pixelShaderAddress + ShaderInputSemanticsOffset, out inputSemanticsAddress) ||
             !TryReadUInt32(ctx, pixelShaderAddress + ShaderNumInputSemanticsOffset, out _)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        for (uint i = 0; i < 32; i++)
        {
            uint value = 0;
            if (i < outputSemanticsCount && outputSemanticsAddress != 0)
            {
                var flat = false;
                if (pixelShaderAddress != 0 && inputSemanticsAddress != 0 &&
                    TryReadUInt32(ctx, inputSemanticsAddress + (i * sizeof(uint)), out var inputSemantic))
                {
                    flat = ((inputSemantic >> 22) & 0x1) != 0;
                }

                value = i | (flat ? 0x400u : 0u);
            }

            var destination = registersAddress + (i * 8);
            if (!TryWriteUInt32(ctx, destination, SpiPsInputCntl0 + i) ||
                !TryWriteUInt32(ctx, destination + sizeof(uint), value))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        TraceAgc($"agc.create_interpolant_mapping regs=0x{registersAddress:X16} gs=0x{geometryShaderAddress:X16} ps=0x{pixelShaderAddress:X16}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "V++UgBtQhn0",
        ExportName = "sceAgcGetDataPacketPayloadAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetDataPacketPayloadAddress(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        var commandAddress = ctx[CpuRegister.Rsi];
        var type = (int)ctx[CpuRegister.Rdx];
        if (outputAddress == 0 || commandAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var payloadAddress = commandAddress + 8;
        if (type == 0)
        {
            if (!TryReadUInt32(ctx, commandAddress, out var header))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            payloadAddress = (header & 0x3FFF_0000u) == 0x3FFF_0000u
                ? 0
                : commandAddress + 4;
        }

        if (!ctx.TryWriteUInt64(outputAddress, payloadAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (ShouldTraceHotPath(ref _packetPayloadTraceCount))
        {
            TraceAgc(
                $"agc.get_packet_payload out=0x{outputAddress:X16} cmd=0x{commandAddress:X16} " +
                $"type={type} payload=0x{payloadAddress:X16}");
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "LtTouSCZjHM",
        ExportName = "sceAgcCbNop",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbNop(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dwordCount = (uint)ctx[CpuRegister.Rsi];
        if (commandBufferAddress == 0 || dwordCount < 2 || dwordCount > 0x4001)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, dwordCount, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(dwordCount, ItNop, RZero)))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint index = 1; index < dwordCount; index++)
        {
            if (!TryWriteUInt32(ctx, commandAddress + ((ulong)index * sizeof(uint)), 0))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "k3GhuSNmBLU",
        ExportName = "sceAgcCbDispatch",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbDispatch(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var groupCountX = (uint)ctx[CpuRegister.Rsi];
        var groupCountY = (uint)ctx[CpuRegister.Rdx];
        var groupCountZ = (uint)ctx[CpuRegister.Rcx];
        var modifier = (uint)ctx[CpuRegister.R8];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, ItDispatchDirect, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, groupCountX) ||
            !TryWriteUInt32(ctx, commandAddress + 8, groupCountY) ||
            !TryWriteUInt32(ctx, commandAddress + 12, groupCountZ) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (modifier & 0xA038u) | 0x41u))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "UZbQjYAwwXM",
        ExportName = "sceAgcCbSetShRegistersDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbSetShRegistersDirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var registersAddress = ctx[CpuRegister.Rsi];
        var registerCount = (uint)ctx[CpuRegister.Rdx];
        if (registerCount == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (commandBufferAddress == 0 || registersAddress == 0 || registerCount > 4096)
        {
            return ReturnPointer(ctx, 0);
        }

        var registers = new RegisterDefaultValue[registerCount];
        for (uint index = 0; index < registerCount; index++)
        {
            var entryAddress = registersAddress + ((ulong)index * 8);
            if (!TryReadUInt32(ctx, entryAddress, out var offset) ||
                !TryReadUInt32(ctx, entryAddress + sizeof(uint), out var value))
            {
                return ReturnPointer(ctx, 0);
            }

            registers[index] = new RegisterDefaultValue(offset, value);
        }

        Array.Sort(registers, static (left, right) => left.Offset.CompareTo(right.Offset));
        ulong firstCommandAddress = 0;
        var startIndex = 0;
        while (startIndex < registers.Length)
        {
            var endIndex = startIndex + 1;
            while (endIndex < registers.Length &&
                   registers[endIndex].Offset == registers[endIndex - 1].Offset + 1)
            {
                endIndex++;
            }

            var valueCount = (uint)(endIndex - startIndex);
            var packetDwords = valueCount + 2;
            if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
                !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItSetShReg, 0)) ||
                !TryWriteUInt32(ctx, commandAddress + 4, registers[startIndex].Offset & 0xFFFFu))
            {
                return ReturnPointer(ctx, 0);
            }

            firstCommandAddress = firstCommandAddress == 0 ? commandAddress : firstCommandAddress;
            for (var index = startIndex; index < endIndex; index++)
            {
                if (!TryWriteUInt32(
                        ctx,
                        commandAddress + 8 + ((ulong)(index - startIndex) * sizeof(uint)),
                        registers[index].Value))
                {
                    return ReturnPointer(ctx, 0);
                }
            }

            startIndex = endIndex;
        }

        return ReturnPointer(ctx, firstCommandAddress);
    }

    [SysAbiExport(
        Nid = "JrtiDtKeS38",
        ExportName = "sceAgcAcbResetQueue",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbResetQueue(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RAcbReset)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "cFazmnXpJOE",
        ExportName = "sceAgcAcbEventWrite",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbEventWrite(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var eventType = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var eventAddress = ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 || eventType >= 0x40)
        {
            return ReturnPointer(ctx, 0);
        }

        var hasAddress = (eventType & ~1u) == 0x38;
        var packetDwords = hasAddress ? 4u : 2u;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItEventWrite, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, hasAddress ? eventType | 0x100u : eventType & 0x3Fu))
        {
            return ReturnPointer(ctx, 0);
        }

        if (hasAddress &&
            (!TryWriteUInt32(ctx, commandAddress + 8, (uint)eventAddress & ~7u) ||
             !TryWriteUInt32(ctx, commandAddress + 12, (uint)(eventAddress >> 32))))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "KT-hTp-Ch14",
        ExportName = "sceAgcAcbAcquireMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbAcquireMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var gcrControl = (uint)ctx[CpuRegister.Rsi];
        var baseAddress = ctx[CpuRegister.Rdx];
        var sizeBytes = ctx[CpuRegister.Rcx];
        var pollCycles = (uint)ctx[CpuRegister.R8];
        var noSize = sizeBytes == ulong.MaxValue;
        if (commandBufferAddress == 0 ||
            (!noSize && (sizeBytes & 0xFF) != 0) ||
            (!noSize && (sizeBytes >> 40) != 0) ||
            (baseAddress & 0xFF) != 0 ||
            (baseAddress >> 40) != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RAcquireMem)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0x8000_0000u) ||
            !TryWriteUInt32(ctx, commandAddress + 8, noSize ? 0 : (uint)(sizeBytes >> 8)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(baseAddress >> 8)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 24, pollCycles / 40) ||
            !TryWriteUInt32(ctx, commandAddress + 28, gcrControl))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "htn36gPnBk4",
        ExportName = "sceAgcAcbWaitRegMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbWaitRegMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var size = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var compareFunction = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var address = ctx[CpuRegister.R8];
        var reference = ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var mask) ||
            !TryReadUInt32(ctx, stackAddress + (2 * sizeof(ulong)), out var pollCycles) ||
            commandBufferAddress == 0 ||
            size > 1 ||
            compareFunction > 7 ||
            cachePolicy > 3)
        {
            return ReturnPointer(ctx, 0);
        }

        var packetDwords = size == 0 ? 6u : 9u;
        var packetRegister = size == 0 ? RWaitMem32 : RWaitMem64;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItNop, packetRegister)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (uint)address) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(address >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)mask))
        {
            return ReturnPointer(ctx, 0);
        }

        if (size == 0)
        {
            if (!TryWriteUInt32(ctx, commandAddress + 16, compareFunction) ||
                !TryWriteUInt32(ctx, commandAddress + 20, (uint)reference))
            {
                return ReturnPointer(ctx, 0);
            }
        }
        else if (!TryWriteUInt32(ctx, commandAddress + 16, (uint)(mask >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 20, (uint)reference) ||
                 !TryWriteUInt32(ctx, commandAddress + 24, (uint)(reference >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 28, compareFunction) ||
                 !TryWriteUInt32(ctx, commandAddress + 32, pollCycles / 40))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "eZ4+17OQz4Q",
        ExportName = "sceAgcAcbWriteData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbWriteData(CpuContext ctx) =>
        DcbWriteData(ctx);

    [SysAbiExport(
        Nid = "j3EtxFkSIhQ",
        ExportName = "sceAgcAcbDispatchIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbDispatchIndirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var argumentsAddress = ctx[CpuRegister.Rsi];
        var modifier = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 4, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(4, ItDispatchIndirect, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (uint)argumentsAddress) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(argumentsAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (modifier & 0xA038u) | 0x41u))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "n2fD4A+pb+g",
        ExportName = "sceAgcCbSetShRegisterRangeDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbSetShRegisterRangeDirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var offset = (uint)ctx[CpuRegister.Rsi];
        var valuesAddress = ctx[CpuRegister.Rdx];
        var valueCount = (uint)ctx[CpuRegister.Rcx];
        if (commandBufferAddress == 0 || offset == 0 || offset > 0x3FF || valueCount == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var markerAddress) ||
            !TryWriteUInt32(ctx, markerAddress, Pm4(2, ItNop, RZero)) ||
            !TryWriteUInt32(ctx, markerAddress + 4, CbSetShRegisterRangeMarker) ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, valueCount + 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(valueCount + 2, ItSetShReg, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, offset))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint i = 0; i < valueCount; i++)
        {
            var value = 0u;
            if (valuesAddress != 0 &&
                !TryReadUInt32(ctx, valuesAddress + (i * sizeof(uint)), out value))
            {
                return ReturnPointer(ctx, 0);
            }

            if (!TryWriteUInt32(ctx, commandAddress + 8 + (i * sizeof(uint)), value))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        TraceAgc($"agc.cb_set_sh_range buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} offset=0x{offset:X8} count={valueCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "wr23dPKyWc0",
        ExportName = "sceAgcCbReleaseMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbReleaseMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var action = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var gcrControl = (uint)(ctx[CpuRegister.Rdx] & 0xFFFF);
        var destination = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.R8] & 0xFF);
        var destinationAddress = ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + 8, out var dataSelectionRaw) ||
            !TryReadUInt64(ctx, stackAddress + 16, out var data) ||
            !TryReadUInt64(ctx, stackAddress + 24, out var gdsOffsetRaw) ||
            !TryReadUInt64(ctx, stackAddress + 32, out var gdsSizeRaw) ||
            !TryReadUInt64(ctx, stackAddress + 40, out var interruptRaw) ||
            !TryReadUInt64(ctx, stackAddress + 48, out var interruptContextIdRaw))
        {
            return ReturnPointer(ctx, 0);
        }

        var dataSelection = (uint)(dataSelectionRaw & 0xFF);
        var gdsOffset = (uint)(gdsOffsetRaw & 0xFFFF);
        var gdsSize = (uint)(gdsSizeRaw & 0xFFFF);
        var interrupt = (uint)(interruptRaw & 0xFF);
        var interruptContextId = (uint)interruptContextIdRaw;
        if (commandBufferAddress == 0 ||
            destination > 1 ||
            dataSelection > 3 ||
            gdsOffset != 0 ||
            gdsSize > 2 ||
            interrupt > 3)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RReleaseMem)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, action | (cachePolicy << 8)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 8,
                gcrControl | (dataSelection << 16) | (interrupt << 24)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)destinationAddress) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(destinationAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, (uint)data) ||
            !TryWriteUInt32(ctx, commandAddress + 24, (uint)(data >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 28, interruptContextId))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.cb_release_mem buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"action=0x{action:X2} gcr=0x{gcrControl:X4} dst=0x{destinationAddress:X16} data_sel={dataSelection} data=0x{data:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "TRO721eVt4g",
        ExportName = "sceAgcDcbResetQueue",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbResetQueue(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var op = (uint)ctx[CpuRegister.Rsi];
        var state = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 || op != 0x3FF || state != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RDrawReset)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_reset_queue buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "ZvwO9euwYzc",
        ExportName = "sceAgcDcbSetCxRegistersIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetCxRegistersIndirect(CpuContext ctx) =>
        DcbSetRegistersIndirect(ctx, RCxRegsIndirect, "cx");

    [SysAbiExport(
        Nid = "-HOOCn0JY48",
        ExportName = "sceAgcDcbSetShRegistersIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetShRegistersIndirect(CpuContext ctx) =>
        DcbSetRegistersIndirect(ctx, RShRegsIndirect, "sh");

    [SysAbiExport(
        Nid = "hvUfkUIQcOE",
        ExportName = "sceAgcDcbSetUcRegistersIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetUcRegistersIndirect(CpuContext ctx) =>
        DcbSetRegistersIndirect(ctx, RUcRegsIndirect, "uc");

    [SysAbiExport(
        Nid = "GIIW2J37e70",
        ExportName = "sceAgcDcbSetIndexSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexSize(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexSize = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        if (commandBufferAddress == 0 || cachePolicy != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItIndexType, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, indexSize))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_index_size buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} size={indexSize}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "tSBxhAPyytQ",
        ExportName = "sceAgcDcbSetNumInstances",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetNumInstances(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var instanceCount = (uint)ctx[CpuRegister.Rsi];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNumInstances, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, instanceCount))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_num_instances buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} count={instanceCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "Yw0jKSqop+E",
        ExportName = "sceAgcDcbDrawIndexAuto",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexAuto(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexCount = (uint)ctx[CpuRegister.Rsi];
        var modifier = ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 || modifier != 0x4000_0000)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 7, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(7, ItNop, RDrawIndexAuto)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, indexCount) ||
            !TryWriteUInt32(ctx, commandAddress + 8, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 20, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 24, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_draw_index_auto buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} count={indexCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "rUuVjyR+Rd4",
        ExportName = "sceAgcDcbGetLodStatsGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbGetLodStatsGetSize(CpuContext ctx)
    {
        var counterCount = (uint)ctx[CpuRegister.Rdi];
        ctx[CpuRegister.Rax] = 0x10u + (counterCount * sizeof(uint));
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "vuSXe69VILM",
        ExportName = "sceAgcDcbGetLodStats",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbGetLodStats(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var cachePolicy = (uint)ctx[CpuRegister.Rsi] & 0x3u;
        var destinationAddress = ctx[CpuRegister.Rdx];
        var control = (uint)ctx[CpuRegister.Rcx];
        var counterMask = (uint)ctx[CpuRegister.R8] & 0xFFu;
        var resetCounters = (uint)ctx[CpuRegister.R9] & 0x1u;
        if (!TryReadUInt64(ctx, ctx[CpuRegister.Rsp] + sizeof(ulong), out var enableRaw) ||
            !TryReadUInt64(ctx, ctx[CpuRegister.Rsp] + (2 * sizeof(ulong)), out var counterSelectRaw) ||
            commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var enable = (uint)enableRaw & 0x1u;
        var counterSelect = (uint)counterSelectRaw & 0xFFu;
        var packetControl =
            (cachePolicy << 28) |
            (enable << 19) |
            (resetCounters << 18) |
            (counterMask << 10) |
            (counterSelect << 2);
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, ItGetLodStats, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, control) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)destinationAddress & ~0x3Fu) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(destinationAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 16, packetControl))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_get_lod_stats buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"dst=0x{destinationAddress:X16} control=0x{control:X8} counters=0x{counterMask:X2}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "aJf+j5yntiU",
        ExportName = "sceAgcDcbEventWrite",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbEventWrite(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var eventType = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var eventAddress = ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 || eventType > 0x3F || eventAddress != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItEventWrite, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, eventType))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_event_write buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} type={eventType}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "57labkp+rSQ",
        ExportName = "sceAgcDcbAcquireMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbAcquireMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var engine = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var cbDbOp = (uint)ctx[CpuRegister.Rdx];
        var gcrControl = (uint)ctx[CpuRegister.Rcx];
        var baseAddress = ctx[CpuRegister.R8];
        var sizeBytes = ctx[CpuRegister.R9];
        if (!TryReadUInt32(ctx, ctx[CpuRegister.Rsp] + sizeof(ulong), out var pollCycles))
        {
            return ReturnPointer(ctx, 0);
        }

        var noSize = sizeBytes == ulong.MaxValue;
        if (commandBufferAddress == 0 ||
            engine > 1 ||
            (!noSize && (sizeBytes & 0xFF) != 0) ||
            (!noSize && (sizeBytes >> 40) != 0) ||
            (baseAddress & 0xFF) != 0 ||
            (baseAddress >> 40) != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RAcquireMem)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (engine << 31) | cbDbOp) ||
            !TryWriteUInt32(ctx, commandAddress + 8, noSize ? 0 : (uint)(sizeBytes >> 8)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(baseAddress >> 8)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 24, pollCycles / 40) ||
            !TryWriteUInt32(ctx, commandAddress + 28, gcrControl))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_acquire_mem buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"engine={engine} cbdb=0x{cbDbOp:X8} gcr=0x{gcrControl:X8} base=0x{baseAddress:X16} size=0x{sizeBytes:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "i1jyy49AjXU",
        ExportName = "sceAgcDcbWriteData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbWriteData(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var destination = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var destinationAddress = ctx[CpuRegister.Rcx];
        var dataAddress = ctx[CpuRegister.R8];
        var dwordCount = (uint)ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var incrementRaw) ||
            !TryReadUInt64(ctx, stackAddress + (2 * sizeof(ulong)), out var writeConfirmRaw))
        {
            return ReturnPointer(ctx, 0);
        }

        var increment = (uint)(incrementRaw & 0xFF);
        var writeConfirm = (uint)(writeConfirmRaw & 0xFF);
        if (commandBufferAddress == 0 ||
            destinationAddress == 0 ||
            dataAddress == 0 ||
            dwordCount > 0x3FFD)
        {
            return ReturnPointer(ctx, 0);
        }

        var packetDwords = dwordCount + 4;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItNop, RWriteData)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 4,
                destination | (cachePolicy << 8) | (increment << 16) | (writeConfirm << 24)) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)destinationAddress) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(destinationAddress >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint index = 0; index < dwordCount; index++)
        {
            if (!TryReadUInt32(ctx, dataAddress + ((ulong)index * sizeof(uint)), out var value) ||
                !TryWriteUInt32(ctx, commandAddress + 16 + ((ulong)index * sizeof(uint)), value))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        if (ShouldTraceHotPath(ref _dcbWriteDataTraceCount))
        {
            TraceAgc(
                $"agc.dcb_write_data buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
                $"dst={destination} cache={cachePolicy} addr=0x{destinationAddress:X16} count={dwordCount} " +
                $"increment={increment} confirm={writeConfirm}");
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "VmW0Tdpy420",
        ExportName = "sceAgcDcbWaitRegMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbWaitRegMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var size = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var compareFunction = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var operation = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.R8] & 0xFF);
        var address = ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var reference) ||
            !TryReadUInt64(ctx, stackAddress + (2 * sizeof(ulong)), out var mask) ||
            !TryReadUInt32(ctx, stackAddress + (3 * sizeof(ulong)), out var pollCycles))
        {
            return ReturnPointer(ctx, 0);
        }

        if (commandBufferAddress == 0 ||
            size > 1 ||
            compareFunction > 7 ||
            operation > 4 ||
            cachePolicy > 3)
        {
            return ReturnPointer(ctx, 0);
        }

        var standardWait = operation is 2 or 3;
        var packetDwords = standardWait ? 7u : size == 0 ? 6u : 9u;
        var packetRegister = size == 0 ? RWaitMem32 : RWaitMem64;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress))
        {
            return ReturnPointer(ctx, 0);
        }

        if (standardWait)
        {
            if (!TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItWaitRegMem, 0)) ||
                !TryWriteUInt32(ctx, commandAddress + 4, compareFunction | ((operation & 1) << 8)) ||
                !TryWriteUInt32(ctx, commandAddress + 8, (uint)address) ||
                !TryWriteUInt32(ctx, commandAddress + 12, (uint)(address >> 32)) ||
                !TryWriteUInt32(ctx, commandAddress + 16, (uint)reference) ||
                !TryWriteUInt32(ctx, commandAddress + 20, (uint)mask) ||
                !TryWriteUInt32(ctx, commandAddress + 24, pollCycles / 40))
            {
                return ReturnPointer(ctx, 0);
            }
        }
        else if (!TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItNop, packetRegister)) ||
                 !TryWriteUInt32(ctx, commandAddress + 4, (uint)address) ||
                 !TryWriteUInt32(ctx, commandAddress + 8, (uint)(address >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 12, (uint)mask))
        {
            return ReturnPointer(ctx, 0);
        }
        else if (size == 0)
        {
            if (!TryWriteUInt32(ctx, commandAddress + 16, compareFunction | (operation << 8)) ||
                !TryWriteUInt32(ctx, commandAddress + 20, (uint)reference))
            {
                return ReturnPointer(ctx, 0);
            }
        }
        else if (!TryWriteUInt32(ctx, commandAddress + 16, (uint)(mask >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 20, (uint)reference) ||
                 !TryWriteUInt32(ctx, commandAddress + 24, (uint)(reference >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 28, compareFunction | (operation << 8)) ||
                 !TryWriteUInt32(ctx, commandAddress + 32, pollCycles / 40))
        {
            return ReturnPointer(ctx, 0);
        }

        if (ShouldTraceHotPath(ref _dcbWaitRegMemTraceCount))
        {
            TraceAgc(
                $"agc.dcb_wait_reg_mem buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
                $"size={size} compare={compareFunction} op={operation} cache={cachePolicy} " +
                $"addr=0x{address:X16} ref=0x{reference:X16} mask=0x{mask:X16} poll={pollCycles}");
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "WmAc2MEj6Io",
        ExportName = "sceAgcDcbDmaData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDmaData(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var destination = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var destinationCachePolicy = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var source = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var destinationAddress = ctx[CpuRegister.R8];
        var sourceCachePolicy = (uint)(ctx[CpuRegister.R9] & 0xFF);
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var control4Raw) ||
            !TryReadUInt64(ctx, stackAddress + (2 * sizeof(ulong)), out var sourceAddress) ||
            !TryReadUInt32(ctx, stackAddress + (3 * sizeof(ulong)), out var byteCount) ||
            !TryReadUInt64(ctx, stackAddress + (4 * sizeof(ulong)), out var control7Raw) ||
            !TryReadUInt64(ctx, stackAddress + (5 * sizeof(ulong)), out var control8Raw) ||
            !TryReadUInt64(ctx, stackAddress + (6 * sizeof(ulong)), out var control9Raw))
        {
            return ReturnPointer(ctx, 0);
        }

        if (commandBufferAddress == 0 || byteCount == 0 || (byteCount & 3) != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var control4 = (uint)(control4Raw & 0xFF);
        var control7 = (uint)(control7Raw & 0xFF);
        var control8 = (uint)(control8Raw & 0xFF);
        var control9 = (uint)(control9Raw & 0xFF);
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RDmaData)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 4,
                destination |
                (destinationCachePolicy << 8) |
                (source << 16) |
                (sourceCachePolicy << 24)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 8,
                control4 | (control7 << 8) | (control8 << 16) | (control9 << 24)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, byteCount) ||
            !ctx.TryWriteUInt64(commandAddress + 16, destinationAddress) ||
            !ctx.TryWriteUInt64(commandAddress + 24, sourceAddress))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_dma_data buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"dst=0x{destinationAddress:X16} src=0x{sourceAddress:X16} bytes={byteCount} " +
            $"control0=0x{destination | (destinationCachePolicy << 8) | (source << 16) | (sourceCachePolicy << 24):X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "RmaJwLtc8rY",
        ExportName = "sceAgcDcbSetBaseIndirectArgs",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetBaseIndirectArgs(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var baseIndex = (uint)ctx[CpuRegister.Rsi];
        var address = ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 4, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(4, ItSetBase, 0) | (baseIndex << 1)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 1) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)address & ~7u) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(address >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "CtB+A9-VxO0",
        ExportName = "sceAgcDcbDispatchIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDispatchIndirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dataOffset = (uint)ctx[CpuRegister.Rsi];
        var modifier = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 3, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(3, ItDispatchIndirect, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, dataOffset) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (modifier & 0xA038u) | 0x41u))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "+kSrjIVxKFE",
        ExportName = "sceAgcDcbPushMarker",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbPushMarker(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var markerAddress = ctx[CpuRegister.Rsi];
        if (commandBufferAddress == 0 ||
            !TryReadGuestCString(ctx, markerAddress, 4095, out var marker))
        {
            return ReturnPointer(ctx, 0);
        }

        var payloadDwords = Math.Max(((uint)marker.Length + 4) / 4, 1);
        var packetDwords = payloadDwords + 1;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItNop, RPushMarker)))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint index = 0; index < payloadDwords; index++)
        {
            uint value = 0;
            for (uint byteIndex = 0; byteIndex < sizeof(uint); byteIndex++)
            {
                var markerIndex = (index * sizeof(uint)) + byteIndex;
                if (markerIndex < (uint)marker.Length)
                {
                    value |= (uint)marker[(int)markerIndex] << ((int)byteIndex * 8);
                }
            }

            if (!TryWriteUInt32(ctx, commandAddress + 4 + ((ulong)index * sizeof(uint)), value))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "H7uZqCoNuWk",
        ExportName = "sceAgcDcbPopMarker",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbPopMarker(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RPopMarker)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "IxYiarKlXxM",
        ExportName = "sceAgcDmaDataPatchSetDstAddressOrOffset",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DmaDataPatchSetDstAddressOrOffset(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var destinationAddress = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register) ||
            op != ItNop ||
            register != RDmaData)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.TryWriteUInt64(commandAddress + 16, destinationAddress)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "3KDcnM3lrcU",
        ExportName = "sceAgcWaitRegMemPatchAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WaitRegMemPatchAddress(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var address = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var fieldOffset = op == ItWaitRegMem
            ? 8UL
            : op == ItNop && register is RWaitMem32 or RWaitMem64
                ? 4UL
                : 0;
        if (fieldOffset == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.TryWriteUInt64(commandAddress + fieldOffset, address)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "0fWWK5uG9rQ",
        ExportName = "sceAgcQueueEndOfPipeActionPatchAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int QueueEndOfPipeActionPatchAddress(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var address = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register) ||
            op != ItNop ||
            register != RReleaseMem)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.TryWriteUInt64(commandAddress + 12, address)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "l4fM9K-Lyks",
        ExportName = "sceAgcDcbSetIndexBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexBuffer(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexBufferAddress = ctx[CpuRegister.Rsi];
        var indexCount = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(3, ItIndexBase, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (uint)(indexBufferAddress & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(indexBufferAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, Pm4(2, ItIndexBufferSize, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 16, indexCount))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_index_buffer buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} addr=0x{indexBufferAddress:X16} count={indexCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "B+aG9DUnTKA",
        ExportName = "sceAgcDcbDrawIndexOffset",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexOffset(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexOffset = (uint)ctx[CpuRegister.Rsi];
        var indexCount = (uint)ctx[CpuRegister.Rdx];
        var flags = (uint)ctx[CpuRegister.Rcx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, ItDrawIndexOffset2, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, indexCount) ||
            !TryWriteUInt32(ctx, commandAddress + 8, indexOffset) ||
            !TryWriteUInt32(ctx, commandAddress + 12, indexCount) ||
            !TryWriteUInt32(ctx, commandAddress + 16, flags & 0xE000_0001u))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_draw_index_offset buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} offset={indexOffset} count={indexCount} flags=0x{flags:X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "MWiElSNE8j8",
        ExportName = "sceAgcDcbWaitUntilSafeForRendering",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbWaitUntilSafeForRendering(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var videoOutHandle = (uint)ctx[CpuRegister.Rsi];
        var displayBufferIndex = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 7, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(7, ItNop, RWaitFlipDone)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, videoOutHandle) ||
            !TryWriteUInt32(ctx, commandAddress + 8, displayBufferIndex) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 20, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 24, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_wait_safe buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} handle={videoOutHandle} index={displayBufferIndex}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "YUeqkyT7mEQ",
        ExportName = "sceAgcDcbSetFlip",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetFlip(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var videoOutHandle = (uint)ctx[CpuRegister.Rsi];
        var displayBufferIndex = (int)ctx[CpuRegister.Rdx];
        var flipMode = (uint)ctx[CpuRegister.Rcx];
        var flipArg = unchecked((ulong)ctx[CpuRegister.R8]);
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 6, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(6, ItNop, RFlip)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, videoOutHandle) ||
            !TryWriteUInt32(ctx, commandAddress + 8, unchecked((uint)displayBufferIndex)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, flipMode) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(flipArg & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, (uint)(flipArg >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_flip buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} handle={videoOutHandle} index={displayBufferIndex} mode={flipMode} arg=0x{flipArg:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "w2rJhmD+dsE",
        ExportName = "sceAgcDriverAddEqEvent",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverAddEqEvent(CpuContext ctx)
    {
        var equeue = ctx[CpuRegister.Rdi];
        var eventId = ctx[CpuRegister.Rsi];
        var userData = ctx[CpuRegister.Rdx];
        if (!KernelEventQueueCompatExports.RegisterEvent(
                equeue,
                eventId,
                KernelEventQueueCompatExports.KernelEventFilterGraphics,
                userData))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        TraceAgc($"agc.driver_add_eq_event eq=0x{equeue:X16} id=0x{eventId:X16} udata=0x{userData:X16}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "DL2RXaXOy88",
        ExportName = "sceAgcDriverDeleteEqEvent",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverDeleteEqEvent(CpuContext ctx)
    {
        var equeue = ctx[CpuRegister.Rdi];
        var eventId = ctx[CpuRegister.Rsi];
        if (!KernelEventQueueCompatExports.DeleteRegisteredEvent(
                equeue,
                eventId,
                KernelEventQueueCompatExports.KernelEventFilterGraphics))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        TraceAgc($"agc.driver_delete_eq_event eq=0x{equeue:X16} id=0x{eventId:X16}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "UglJIZjGssM",
        ExportName = "sceAgcDriverSubmitDcb",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSubmitDcb(CpuContext ctx)
    {
        var packetAddress = ctx[CpuRegister.Rdi];
        if (packetAddress == 0 ||
            !TryReadUInt64(ctx, packetAddress, out var commandAddress) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var dwordCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var tracePackets = false;
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"), "1", StringComparison.Ordinal))
        {
            lock (_submitTraceGate)
            {
                tracePackets = _tracedDcbSizes.Add(dwordCount);
            }
        }

        if (tracePackets)
        {
            TraceAgc($"agc.driver_submit_dcb packet=0x{packetAddress:X16} addr=0x{commandAddress:X16} dwords={dwordCount}");
        }

        var gpuState = _submittedGpuStates.GetValue(ctx.Memory, static _ => new SubmittedGpuState());
        lock (gpuState.Gate)
        {
            ParseSubmittedDcb(ctx, gpuState.Graphics, commandAddress, dwordCount, tracePackets);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "gSRnr79F8tQ",
        ExportName = "sceAgcDriverSubmitAcb",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSubmitAcb(CpuContext ctx)
    {
        var ownerHandle = (uint)ctx[CpuRegister.Rdi];
        var packetAddress = ctx[CpuRegister.Rsi];
        if (packetAddress == 0 ||
            !TryReadUInt64(ctx, packetAddress, out var commandAddress) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var dwordCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var tracePackets = false;
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"), "1", StringComparison.Ordinal))
        {
            lock (_submitTraceGate)
            {
                tracePackets = _tracedDcbSizes.Add(dwordCount);
            }
        }

        if (tracePackets)
        {
            TraceAgc(
                $"agc.driver_submit_acb owner={ownerHandle} packet=0x{packetAddress:X16} " +
                $"addr=0x{commandAddress:X16} dwords={dwordCount}");
        }

        var gpuState = _submittedGpuStates.GetValue(ctx.Memory, static _ => new SubmittedGpuState());
        lock (gpuState.Gate)
        {
            if (!gpuState.ComputeQueues.TryGetValue(ownerHandle, out var queueState))
            {
                queueState = new SubmittedDcbState();
                gpuState.ComputeQueues.Add(ownerHandle, queueState);
            }

            ParseSubmittedDcb(ctx, queueState, commandAddress, dwordCount, tracePackets);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "h9z6+0hEydk",
        ExportName = "sceAgcSuspendPoint",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SuspendPoint(CpuContext ctx)
    {
        TraceAgc("agc.suspend_point");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "qj7QZpgr9Uw",
        ExportName = "sceAgcUnknownQj7QZpgr9Uw",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int UnknownQj7QZpgr9Uw(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 1, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, 0x8000_0000))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.unknown_qj7 buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"arg1=0x{ctx[CpuRegister.Rsi]:X16} arg2=0x{ctx[CpuRegister.Rdx]:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    private static void ParseSubmittedDcb(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong commandAddress,
        uint dwordCount,
        bool tracePackets)
    {
        if (commandAddress == 0 || dwordCount == 0 || dwordCount > 1_000_000)
        {
            return;
        }

        var offset = 0u;
        while (offset < dwordCount)
        {
            var currentAddress = commandAddress + ((ulong)offset * sizeof(uint));
            if (!TryReadUInt32(ctx, currentAddress, out var header))
            {
                return;
            }

            var packetType = header >> 30;
            if (packetType == 2)
            {
                if (tracePackets)
                {
                    TraceAgc(
                        $"agc.dcb.packet dw={offset} addr=0x{currentAddress:X16} " +
                        $"header=0x{header:X8} len=1 type=2");
                }

                offset++;
                continue;
            }

            if (packetType != 3)
            {
                return;
            }

            var length = Pm4Length(header);
            if (length == 0 || offset + length > dwordCount)
            {
                return;
            }

            var op = (header >> 8) & 0xFFu;
            var register = (header >> 2) & 0x3Fu;
            if (tracePackets)
            {
                TraceSubmittedPacket(ctx, currentAddress, offset, header, length, op, register);
            }

            if (op == ItSetShReg &&
                TryReadTextureDescriptor(ctx, currentAddress, length, out var texture))
            {
                state.PresenterTexture = texture;
            }

            ApplySubmittedRegisters(ctx, state, currentAddress, length, op, register);

            if (op == ItEventWrite &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + sizeof(uint), out var eventTypeRaw))
            {
                var eventType = eventTypeRaw & 0x3Fu;
                var triggered = KernelEventQueueCompatExports.TriggerRegisteredEvents(
                    eventType,
                    KernelEventQueueCompatExports.KernelEventFilterGraphics,
                    eventType);
                if (tracePackets)
                {
                    TraceAgc($"agc.dcb.event type=0x{eventType:X2} queues={triggered}");
                }
            }

            if (op == ItNop && register == RReleaseMem && length >= 7)
            {
                ApplySubmittedReleaseMem(ctx, currentAddress, tracePackets);
            }

            if (op == ItNop && register == RWriteData && length >= 4)
            {
                ApplySubmittedWriteData(ctx, currentAddress, length, tracePackets);
            }

            if (op == ItNop && register == RDmaData && length >= 8)
            {
                ApplySubmittedDmaData(ctx, currentAddress, tracePackets);
            }

            if (op == ItNop &&
                register is RWaitMem32 or RWaitMem64 &&
                length >= (register == RWaitMem32 ? 6u : 9u))
            {
                ObserveSubmittedWaitRegMem(ctx, currentAddress, register == RWaitMem64, tracePackets);
            }

            if (op == ItWaitRegMem && length >= 7)
            {
                ObserveSubmittedStandardWaitRegMem(ctx, currentAddress, tracePackets);
            }

            if (op == ItDrawIndexOffset2 &&
                length >= 5 &&
                TryReadUInt32(ctx, currentAddress + 4, out var indexCount) &&
                indexCount != 0)
            {
                state.SawIndexedDraw = true;
                TryTranslateGuestDraw(ctx, state, indexCount);
            }

            if (op == ItNop &&
                register == RDrawIndexAuto &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var autoIndexCount) &&
                autoIndexCount != 0)
            {
                state.SawIndexedDraw = true;
                TryTranslateGuestDraw(ctx, state, autoIndexCount);
            }

            if (op == ItNop && register == RFlip && length >= 6)
            {
                if (!TryReadUInt32(ctx, currentAddress + 4, out var videoOutHandle) ||
                    !TryReadUInt32(ctx, currentAddress + 8, out var displayBufferIndexRaw) ||
                    !TryReadUInt32(ctx, currentAddress + 12, out var flipMode) ||
                    !TryReadUInt32(ctx, currentAddress + 16, out var flipArgLo) ||
                    !TryReadUInt32(ctx, currentAddress + 20, out var flipArgHi))
                {
                    return;
                }

                var flipArg = unchecked((long)(((ulong)flipArgHi << 32) | flipArgLo));
                var displayBufferIndex = unchecked((int)displayBufferIndexRaw);
                if (state.SawIndexedDraw && state.PresenterTexture is { } sourceTexture)
                {
                    _ = TrySoftwarePresent(
                        ctx,
                        sourceTexture,
                        unchecked((int)videoOutHandle),
                        displayBufferIndex);
                }
                else if (state.SawIndexedDraw &&
                         state.GuestDrawKind != GuestDrawKind.None &&
                         VideoOutExports.TryGetDisplayBufferInfo(
                             unchecked((int)videoOutHandle),
                             displayBufferIndex,
                             out var displayBuffer))
                {
                    VulkanVideoPresenter.SubmitGuestDraw(
                        state.GuestDrawKind,
                        displayBuffer.Width,
                        displayBuffer.Height);
                }

                _ = VideoOutExports.SubmitFlipFromAgc(ctx, unchecked((int)videoOutHandle), displayBufferIndex, unchecked((int)flipMode), flipArg);
                state.SawIndexedDraw = false;
                state.GuestDrawKind = GuestDrawKind.None;
            }

            offset += length;
        }
    }

    private static void ApplySubmittedDmaData(
        CpuContext ctx,
        ulong packetAddress,
        bool tracePacket)
    {
        if (!TryReadUInt32(ctx, packetAddress + 12, out var byteCount) ||
            !TryReadUInt64(ctx, packetAddress + 16, out var destinationAddress) ||
            !TryReadUInt64(ctx, packetAddress + 24, out var sourceAddress))
        {
            return;
        }

        var copied =
            byteCount != 0 &&
            byteCount <= 256u * 1024u * 1024u &&
            destinationAddress != 0 &&
            sourceAddress != 0 &&
            TryCopyGuestMemory(ctx, sourceAddress, destinationAddress, byteCount);
        if (tracePacket)
        {
            TraceAgc(
                $"agc.dcb.dma_data dst=0x{destinationAddress:X16} src=0x{sourceAddress:X16} " +
                $"bytes={byteCount} copied={copied}");
        }
    }

    private static void ApplySubmittedWriteData(
        CpuContext ctx,
        ulong packetAddress,
        uint packetLength,
        bool tracePacket)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out var control) ||
            !TryReadUInt64(ctx, packetAddress + 8, out var destinationAddress))
        {
            return;
        }

        var destination = control & 0xFFu;
        var increment = (control >> 16) & 0xFFu;
        var dwordCount = packetLength - 4;
        var wroteData = destination is 1 or 2 or 4 or 5;
        for (uint index = 0; wroteData && index < dwordCount; index++)
        {
            var sourceAddress = packetAddress + 16 + ((ulong)index * sizeof(uint));
            var targetAddress = destinationAddress +
                (increment == 0 ? (ulong)index * sizeof(uint) : 0);
            wroteData =
                TryReadUInt32(ctx, sourceAddress, out var value) &&
                TryWriteUInt32(ctx, targetAddress, value);
        }

        if (tracePacket)
        {
            TraceAgc(
                $"agc.dcb.write_data dst={destination} addr=0x{destinationAddress:X16} " +
                $"count={dwordCount} increment={increment} wrote={wroteData}");
        }
    }

    private static void ObserveSubmittedWaitRegMem(
        CpuContext ctx,
        ulong packetAddress,
        bool is64Bit,
        bool tracePacket)
    {
        if (!TryReadUInt64(ctx, packetAddress + 4, out var address) ||
            !TryReadUInt32(ctx, packetAddress + (is64Bit ? 28u : 16u), out var control))
        {
            return;
        }

        ulong mask;
        ulong reference;
        ulong value;
        if (is64Bit)
        {
            if (!TryReadUInt64(ctx, packetAddress + 12, out mask) ||
                !TryReadUInt64(ctx, packetAddress + 20, out reference) ||
                !TryReadUInt64(ctx, address, out value))
            {
                return;
            }
        }
        else
        {
            if (!TryReadUInt32(ctx, packetAddress + 12, out var mask32) ||
                !TryReadUInt32(ctx, packetAddress + 20, out var reference32) ||
                !TryReadUInt32(ctx, address, out var value32))
            {
                return;
            }

            mask = mask32;
            reference = reference32;
            value = value32;
        }

        var compareFunction = control & 0xFFu;
        TraceSubmittedWait(
            address,
            value,
            mask,
            reference,
            compareFunction,
            is64Bit ? 64 : 32,
            tracePacket);
    }

    private static void ObserveSubmittedStandardWaitRegMem(
        CpuContext ctx,
        ulong packetAddress,
        bool tracePacket)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out var control) ||
            !TryReadUInt64(ctx, packetAddress + 8, out var address) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var reference) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var mask) ||
            !TryReadUInt32(ctx, address, out var value))
        {
            return;
        }

        TraceSubmittedWait(address, value, mask, reference, control & 0x7u, 32, tracePacket);
    }

    private static void TraceSubmittedWait(
        ulong address,
        ulong value,
        ulong mask,
        ulong reference,
        uint compareFunction,
        int bits,
        bool tracePacket)
    {
        var maskedValue = value & mask;
        var satisfied = compareFunction switch
        {
            0 => false,
            1 => maskedValue < reference,
            2 => maskedValue <= reference,
            3 => maskedValue == reference,
            4 => maskedValue != reference,
            5 => maskedValue >= reference,
            6 => maskedValue > reference,
            _ => true,
        };
        if (!tracePacket && (satisfied || !ShouldTraceHotPath(ref _unsatisfiedWaitTraceCount)))
        {
            return;
        }

        TraceAgc(
            $"agc.dcb.wait_reg_mem bits={bits} addr=0x{address:X16} " +
            $"value=0x{value:X16} mask=0x{mask:X16} ref=0x{reference:X16} " +
            $"compare={compareFunction} satisfied={satisfied}");
    }

    private static void ApplySubmittedReleaseMem(
        CpuContext ctx,
        ulong packetAddress,
        bool tracePacket)
    {
        if (!TryReadUInt32(ctx, packetAddress + 8, out var control) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var destinationLo) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var destinationHi) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var dataLo) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var dataHi))
        {
            return;
        }

        var dataSelection = (control >> 16) & 0xFFu;
        var destinationAddress = ((ulong)destinationHi << 32) | destinationLo;
        var data = ((ulong)dataHi << 32) | dataLo;
        var wroteData = dataSelection switch
        {
            1 or 2 => TryWriteUInt32(ctx, destinationAddress, dataLo),
            3 => ctx.TryWriteUInt64(destinationAddress, data),
            _ => false,
        };

        if (tracePacket)
        {
            TraceAgc(
                $"agc.dcb.release_mem dst=0x{destinationAddress:X16} data_sel={dataSelection} " +
                $"data=0x{data:X16} wrote={wroteData}");
        }
    }

    private static void ApplySubmittedRegisters(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        uint op,
        uint register)
    {
        if (op == ItSetShReg)
        {
            if (packetLength < 3 ||
                !TryReadUInt32(ctx, packetAddress + sizeof(uint), out var startRegister))
            {
                return;
            }

            for (uint index = 0; index < packetLength - 2; index++)
            {
                if (!TryReadUInt32(
                        ctx,
                        packetAddress + 8 + ((ulong)index * sizeof(uint)),
                        out var value))
                {
                    return;
                }

                state.ShRegisters[startRegister + index] = value;
            }

            return;
        }

        if (op != ItNop ||
            register is not (RCxRegsIndirect or RShRegsIndirect or RUcRegsIndirect) ||
            packetLength < 4 ||
            !TryReadUInt32(ctx, packetAddress + sizeof(uint), out var registerCount) ||
            !TryReadUInt64(ctx, packetAddress + 8, out var registersAddress))
        {
            return;
        }

        var destination = register switch
        {
            RCxRegsIndirect => state.CxRegisters,
            RShRegsIndirect => state.ShRegisters,
            _ => state.UcRegisters,
        };
        for (uint index = 0; index < registerCount; index++)
        {
            var entryAddress = registersAddress + ((ulong)index * 8);
            if (!TryReadUInt32(ctx, entryAddress, out var registerOffset) ||
                !TryReadUInt32(ctx, entryAddress + sizeof(uint), out var value))
            {
                return;
            }

            if (registerOffset != 0)
            {
                destination[registerOffset] = value;
            }
        }
    }

    private static void TryTranslateGuestDraw(
        CpuContext ctx,
        SubmittedDcbState state,
        uint vertexCount)
    {
        if (state.GuestDrawKind != GuestDrawKind.None || vertexCount != 3)
        {
            return;
        }

        var hasExportShader = TryGetShaderAddress(
            state.ShRegisters,
            SpiShaderPgmLoEs,
            SpiShaderPgmHiEs,
            out var exportShaderAddress);
        var hasPixelShader = TryGetShaderAddress(
            state.ShRegisters,
            SpiShaderPgmLoPs,
            SpiShaderPgmHiPs,
            out var pixelShaderAddress);
        var hasPsInputEna = state.CxRegisters.TryGetValue(SpiPsInputEna, out var psInputEna);
        var hasPsInputAddr = state.CxRegisters.TryGetValue(SpiPsInputAddr, out var psInputAddr);
        if (!hasExportShader ||
            !hasPixelShader ||
            !hasPsInputEna ||
            !hasPsInputAddr ||
            !Gen5ShaderTranslator.TryTranslate(
                ctx,
                exportShaderAddress,
                pixelShaderAddress,
                psInputEna,
                psInputAddr,
                out var drawKind))
        {
            TraceShaderTranslationMiss(
                vertexCount,
                hasExportShader,
                exportShaderAddress,
                hasPixelShader,
                pixelShaderAddress,
                hasPsInputEna,
                psInputEna,
                hasPsInputAddr,
                psInputAddr);
            return;
        }

        state.GuestDrawKind = drawKind;
        lock (_submitTraceGate)
        {
            if (_tracedShaderTranslations.Add((exportShaderAddress, pixelShaderAddress, drawKind)))
            {
                TraceAgc(
                    $"agc.shader_translated kind={drawKind} es=0x{exportShaderAddress:X16} " +
                    $"ps=0x{pixelShaderAddress:X16} vertices={vertexCount}");
            }
        }
    }

    private static void TraceShaderTranslationMiss(
        uint vertexCount,
        bool hasExportShader,
        ulong exportShaderAddress,
        bool hasPixelShader,
        ulong pixelShaderAddress,
        bool hasPsInputEna,
        uint psInputEna,
        bool hasPsInputAddr,
        uint psInputAddr)
    {
        if (!ShouldTraceHotPath(ref _shaderTranslationMissTraceCount))
        {
            return;
        }

        TraceAgc(
            $"agc.shader_translate_miss vertices={vertexCount} " +
            $"es={(hasExportShader ? $"0x{exportShaderAddress:X16}" : "missing")} " +
            $"ps={(hasPixelShader ? $"0x{pixelShaderAddress:X16}" : "missing")} " +
            $"ps_ena={(hasPsInputEna ? $"0x{psInputEna:X8}" : "missing")} " +
            $"ps_addr={(hasPsInputAddr ? $"0x{psInputAddr:X8}" : "missing")}");
    }

    private static bool TryGetShaderAddress(
        IReadOnlyDictionary<uint, uint> registers,
        uint loRegister,
        uint hiRegister,
        out ulong address)
    {
        address = 0;
        if (!registers.TryGetValue(loRegister, out var lo) ||
            !registers.TryGetValue(hiRegister, out var hi))
        {
            return false;
        }

        address = ((ulong)hi << 40) | ((ulong)lo << 8);
        return address != 0;
    }

    private static bool TryReadTextureDescriptor(
        CpuContext ctx,
        ulong packetAddress,
        uint packetLength,
        out TextureDescriptor descriptor)
    {
        descriptor = default;
        if (packetLength < 10 ||
            !TryReadUInt32(ctx, packetAddress + 4, out var startRegister))
        {
            return false;
        }

        var valueCount = packetLength - 2;
        if (startRegister > PsTextureUserDataRegister ||
            startRegister + valueCount < PsTextureUserDataRegister + 8)
        {
            return false;
        }

        var descriptorAddress =
            packetAddress +
            8 +
            ((ulong)(PsTextureUserDataRegister - startRegister) * sizeof(uint));
        Span<uint> fields = stackalloc uint[4];
        for (var i = 0; i < fields.Length; i++)
        {
            if (!TryReadUInt32(ctx, descriptorAddress + ((ulong)i * sizeof(uint)), out fields[i]))
            {
                return false;
            }
        }

        var address = ((((ulong)fields[1] << 32) | fields[0]) & 0xFF_FFFF_FFFFUL) << 8;
        var width = (((fields[1] >> 30) & 0x3u) | ((fields[2] & 0xFFFu) << 2)) + 1;
        var height = ((fields[2] >> 14) & 0x3FFFu) + 1;
        var format = (fields[1] >> 20) & 0x1FFu;
        var tileMode = (fields[3] >> 20) & 0x1Fu;
        var type = (fields[3] >> 28) & 0xFu;
        if (address == 0 || width == 0 || height == 0)
        {
            return false;
        }

        descriptor = new TextureDescriptor(address, width, height, format, tileMode, type);
        return true;
    }

    private static bool TrySoftwarePresent(
        CpuContext ctx,
        TextureDescriptor source,
        int videoOutHandle,
        int displayBufferIndex)
    {
        if (source.Format != Gen5TextureFormatR8G8B8A8Unorm ||
            source.TileMode != 0 ||
            source.Type != Gen5TextureType2D ||
            source.Width > 8192 ||
            source.Height > 8192 ||
            !VideoOutExports.TryGetDisplayBufferInfo(videoOutHandle, displayBufferIndex, out var destination) ||
            destination.Address == 0 ||
            destination.Width == 0 ||
            destination.Height == 0 ||
            destination.Width > 8192 ||
            destination.Height > 8192 ||
            destination.TilingMode != 0 ||
            destination.PixelFormat is not (
                VideoOutPixelFormatA8R8G8B8Srgb or
                VideoOutPixelFormatA8B8G8R8Srgb or
                VideoOutPixelFormatB8G8R8A8Unorm or
                VideoOutPixelFormatR8G8B8A8Unorm))
        {
            return false;
        }

        var sourceByteCount = checked((ulong)source.Width * source.Height * 4);
        if (sourceByteCount > 256UL * 1024UL * 1024UL)
        {
            return false;
        }

        var sourceBytes = new byte[(int)sourceByteCount];
        if (!ctx.Memory.TryRead(source.Address, sourceBytes))
        {
            return false;
        }

        var fingerprint = ComputeFingerprint(sourceBytes);
        var fingerprintKey = (source.Address, destination.Address);
        lock (_softwarePresenterGate)
        {
            if (_softwarePresenterFingerprints.TryGetValue(fingerprintKey, out var previousFingerprint) &&
                previousFingerprint == fingerprint)
            {
                return true;
            }
        }

        var destinationPitch = destination.PitchInPixel == 0
            ? destination.Width
            : destination.PitchInPixel;
        if (destinationPitch < destination.Width)
        {
            return false;
        }

        var destinationRow = new byte[checked((int)destinationPitch * 4)];
        var rgbaDestination = destination.PixelFormat is
            VideoOutPixelFormatA8B8G8R8Srgb or
            VideoOutPixelFormatR8G8B8A8Unorm;
        for (uint y = 0; y < destination.Height; y++)
        {
            var sourceY = (uint)(((ulong)y * source.Height) / destination.Height);
            for (uint x = 0; x < destination.Width; x++)
            {
                var sourceX = (uint)(((ulong)x * source.Width) / destination.Width);
                var sourceOffset = checked((int)(((ulong)sourceY * source.Width + sourceX) * 4));
                var destinationOffset = checked((int)x * 4);
                if (rgbaDestination)
                {
                    destinationRow[destinationOffset + 0] = sourceBytes[sourceOffset + 0];
                    destinationRow[destinationOffset + 1] = sourceBytes[sourceOffset + 1];
                    destinationRow[destinationOffset + 2] = sourceBytes[sourceOffset + 2];
                }
                else
                {
                    destinationRow[destinationOffset + 0] = sourceBytes[sourceOffset + 2];
                    destinationRow[destinationOffset + 1] = sourceBytes[sourceOffset + 1];
                    destinationRow[destinationOffset + 2] = sourceBytes[sourceOffset + 0];
                }

                destinationRow[destinationOffset + 3] = sourceBytes[sourceOffset + 3];
            }

            var destinationAddress = destination.Address + ((ulong)y * destinationPitch * 4);
            if (!ctx.Memory.TryWrite(destinationAddress, destinationRow))
            {
                return false;
            }
        }

        lock (_softwarePresenterGate)
        {
            _softwarePresenterFingerprints[fingerprintKey] = fingerprint;
        }

        VideoOutExports.SubmitHostRgbaFrame(sourceBytes, source.Width, source.Height);
        TraceAgc(
            $"agc.software_presenter src=0x{source.Address:X16} {source.Width}x{source.Height} fmt={source.Format} " +
            $"dst=0x{destination.Address:X16} {destination.Width}x{destination.Height} fingerprint=0x{fingerprint:X16}");
        return true;
    }

    private static ulong ComputeFingerprint(ReadOnlySpan<byte> bytes)
    {
        const ulong fnvOffsetBasis = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;
        var fingerprint = fnvOffsetBasis;
        foreach (var value in bytes)
        {
            fingerprint = (fingerprint ^ value) * fnvPrime;
        }

        return fingerprint;
    }

    private static void TraceSubmittedPacket(
        CpuContext ctx,
        ulong packetAddress,
        uint dwordOffset,
        uint header,
        uint length,
        uint op,
        uint register)
    {
        TraceAgc(
            $"agc.dcb.packet dw={dwordOffset} addr=0x{packetAddress:X16} header=0x{header:X8} len={length} op=0x{op:X2} reg=0x{register:X2}");

        var payloadCount = Math.Min(length - 1, 32u);
        for (uint i = 0; i < payloadCount; i++)
        {
            if (!TryReadUInt32(ctx, packetAddress + ((ulong)(i + 1) * sizeof(uint)), out var value))
            {
                return;
            }

            TraceAgc($"agc.dcb.payload dw={dwordOffset + i + 1} value=0x{value:X8}");
        }

        if (op != ItNop ||
            register is not (RCxRegsIndirect or RShRegsIndirect or RUcRegsIndirect) ||
            length < 4 ||
            !TryReadUInt32(ctx, packetAddress + 4, out var registerCount) ||
            !TryReadUInt64(ctx, packetAddress + 8, out var registersAddress))
        {
            return;
        }

        var registerSpace = register == RCxRegsIndirect ? "cx" : register == RShRegsIndirect ? "sh" : "uc";
        var tracedCount = Math.Min(registerCount, 256u);
        TraceAgc($"agc.dcb.indirect space={registerSpace} regs=0x{registersAddress:X16} count={registerCount}");
        for (uint i = 0; i < tracedCount; i++)
        {
            var entryAddress = registersAddress + ((ulong)i * 8);
            if (!TryReadUInt32(ctx, entryAddress, out var registerOffset) ||
                !TryReadUInt32(ctx, entryAddress + 4, out var value))
            {
                TraceAgc($"agc.dcb.indirect_read_failed space={registerSpace} index={i} addr=0x{entryAddress:X16}");
                return;
            }

            TraceAgc($"agc.dcb.reg space={registerSpace} index={i} offset=0x{registerOffset:X4} value=0x{value:X8}");
        }

        if (tracedCount != registerCount)
        {
            TraceAgc($"agc.dcb.indirect_truncated space={registerSpace} traced={tracedCount} total={registerCount}");
        }
    }

    private static bool PatchShaderProgramRegisters(CpuContext ctx, ulong headerAddress, ulong codeAddress)
    {
        if (!TryReadUInt64(ctx, headerAddress + ShaderShRegistersOffset, out var shRegistersAddress) ||
            !TryReadByte(ctx, headerAddress + ShaderTypeOffset, out var shaderType) ||
            !TryReadByte(ctx, headerAddress + ShaderNumShRegistersOffset, out var registerCount))
        {
            return false;
        }

        if (shRegistersAddress == 0 || registerCount < 2)
        {
            return false;
        }

        if (!TryReadUInt32(ctx, shRegistersAddress, out var loRegister) ||
            !TryReadUInt32(ctx, shRegistersAddress + 8, out var hiRegister))
        {
            return false;
        }

        var expectedLo = shaderType switch
        {
            0 => ComputePgmLo,
            1 => SpiShaderPgmLoPs,
            2 or 6 => SpiShaderPgmLoEs,
            7 => SpiShaderPgmLoLs,
            _ => 0u,
        };
        var expectedHi = shaderType switch
        {
            0 => ComputePgmHi,
            1 => SpiShaderPgmHiPs,
            2 or 6 => SpiShaderPgmHiEs,
            7 => SpiShaderPgmHiLs,
            _ => 0u,
        };
        if (expectedLo == 0 || loRegister != expectedLo || hiRegister != expectedHi)
        {
            TraceCreateShader(0, headerAddress, codeAddress, $"unexpected-registers type={shaderType} lo=0x{loRegister:X8} hi=0x{hiRegister:X8}");
            return false;
        }

        var loValue = (uint)((codeAddress >> 8) & 0xFFFF_FFFFUL);
        var hiValue = (uint)((codeAddress >> 40) & 0xFFUL);
        return TryWriteUInt32(ctx, shRegistersAddress + sizeof(uint), loValue) &&
               TryWriteUInt32(ctx, shRegistersAddress + 8 + sizeof(uint), hiValue);
    }

    private static bool IsEsGeometryShaderType(byte shaderType) =>
        shaderType is 2 or 6;

    private static int SetIndirectPatchAddress(CpuContext ctx, string registerSpace)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var registersAddress = ctx[CpuRegister.Rsi];
        if (commandAddress == 0 || registersAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryWriteUInt32(ctx, commandAddress + 8, (uint)(registersAddress & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(registersAddress >> 32)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.patch_{registerSpace}_addr cmd=0x{commandAddress:X16} regs=0x{registersAddress:X16}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int AddIndirectPatchRegisters(CpuContext ctx, string registerSpace)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var registerCount = (uint)ctx[CpuRegister.Rsi];
        if (commandAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadUInt32(ctx, commandAddress + 4, out var currentCount) ||
            !TryWriteUInt32(ctx, commandAddress + 4, currentCount + registerCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.patch_{registerSpace}_add cmd=0x{commandAddress:X16} add={registerCount} total={currentCount + registerCount}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int DcbSetRegistersIndirect(CpuContext ctx, uint packetRegister, string registerSpace)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var registersAddress = ctx[CpuRegister.Rsi];
        var registerCount = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 4, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(4, ItNop, packetRegister)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, registerCount) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(registersAddress & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(registersAddress >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_{registerSpace}_indirect buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} regs=0x{registersAddress:X16} count={registerCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    private static bool TryAllocateCommandDwords(CpuContext ctx, ulong commandBufferAddress, uint sizeDwords, out ulong commandAddress)
    {
        commandAddress = 0;
        if (sizeDwords == 0 ||
            !TryReadUInt64(ctx, commandBufferAddress + CommandBufferCursorUpOffset, out var cursorUp) ||
            !TryReadUInt64(ctx, commandBufferAddress + CommandBufferCursorDownOffset, out var cursorDown) ||
            !TryReadUInt64(ctx, commandBufferAddress + CommandBufferCallbackOffset, out var callback) ||
            !TryReadUInt32(ctx, commandBufferAddress + CommandBufferReservedDwOffset, out var reservedDwords))
        {
            return false;
        }

        var availableDwords = cursorDown >= cursorUp
            ? Math.Min((cursorDown - cursorUp) / sizeof(uint), uint.MaxValue)
            : 0;
        var remainingDwords = (uint)Math.Max(availableDwords, reservedDwords) - reservedDwords;
        if (sizeDwords > remainingDwords)
        {
            TraceAgc($"agc.cmd_alloc_full buf=0x{commandBufferAddress:X16} need={sizeDwords} remaining={remainingDwords} callback=0x{callback:X16}");
            return false;
        }

        var nextCursor = cursorUp + ((ulong)sizeDwords * sizeof(uint));
        if (!ctx.TryWriteUInt64(commandBufferAddress + CommandBufferCursorUpOffset, nextCursor))
        {
            return false;
        }

        commandAddress = cursorUp;
        return true;
    }

    private static bool CopyShaderRegister(CpuContext ctx, ulong sourceAddress, ulong destinationAddress)
    {
        if (!TryReadUInt32(ctx, sourceAddress, out var offset) ||
            !TryReadUInt32(ctx, sourceAddress + sizeof(uint), out var value))
        {
            return false;
        }

        return TryWriteUInt32(ctx, destinationAddress, offset) &&
               TryWriteUInt32(ctx, destinationAddress + sizeof(uint), value);
    }

    private static bool RelocatePointerField(CpuContext ctx, ulong fieldAddress)
    {
        if (!TryReadUInt64(ctx, fieldAddress, out var relativeAddress))
        {
            return false;
        }

        if (relativeAddress == 0)
        {
            return true;
        }

        return ctx.TryWriteUInt64(fieldAddress, fieldAddress + relativeAddress);
    }

    private static int ReturnRegisterDefaults(CpuContext ctx, bool internalDefaults)
    {
        var version = (uint)ctx[CpuRegister.Rdi];
        if (!IsSupportedRegisterDefaultsVersion(version))
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryGetRegisterDefaultsAllocation(ctx, out var allocation))
        {
            return ReturnPointer(ctx, 0);
        }

        var address = internalDefaults ? allocation.Internal : allocation.Primary;
        TraceAgc($"agc.get_register_defaults internal={internalDefaults} version={version} address=0x{address:X16}");
        return ReturnPointer(ctx, address);
    }

    private static bool IsSupportedRegisterDefaultsVersion(uint version)
    {
        return version is
            RegisterDefaultsVersion7 or
            RegisterDefaultsVersion8 or
            RegisterDefaultsVersion10;
    }

    private static bool TryGetRegisterDefaultsAllocation(
        CpuContext ctx,
        out RegisterDefaultsAllocation allocation)
    {
        lock (_registerDefaultsGate)
        {
            if (_registerDefaultsAllocations.TryGetValue(ctx.Memory, out allocation!))
            {
                return true;
            }

            if (!TryBuildRegisterDefaults(
                    ctx,
                    PrimaryRegisterDefaults,
                    cxTableLength: 78,
                    shTableLength: 29,
                    ucTableLength: 20,
                    out var primaryAddress) ||
                !TryBuildRegisterDefaults(
                    ctx,
                    InternalRegisterDefaults,
                    cxTableLength: 4,
                    shTableLength: 15,
                    ucTableLength: 3,
                    out var internalAddress))
            {
                allocation = null!;
                return false;
            }

            allocation = new RegisterDefaultsAllocation(primaryAddress, internalAddress);
            _registerDefaultsAllocations.Add(ctx.Memory, allocation);
            return true;
        }
    }

    private static bool TryBuildRegisterDefaults(
        CpuContext ctx,
        RegisterDefaultGroup[] groups,
        int cxTableLength,
        int shTableLength,
        int ucTableLength,
        out ulong address)
    {
        var cxTableOffset = AlignUp(RegisterDefaultsSize, sizeof(ulong));
        var shTableOffset = cxTableOffset + (cxTableLength * sizeof(ulong));
        var ucTableOffset = shTableOffset + (shTableLength * sizeof(ulong));
        var typesOffset = AlignUp(ucTableOffset + (ucTableLength * sizeof(ulong)), sizeof(uint));
        var registerBlocksOffset = AlignUp(typesOffset + (groups.Length * 3 * sizeof(uint)), sizeof(ulong));
        var blobLength = registerBlocksOffset + (groups.Length * RegisterDefaultBlockSize);

        if (!KernelMemoryCompatExports.TryAllocateHleData(ctx, (ulong)blobLength, 0x1000, out address))
        {
            return false;
        }

        var blob = new byte[blobLength];
        WriteBlobUInt64(blob, 0x00, address + (ulong)cxTableOffset);
        WriteBlobUInt64(blob, 0x08, address + (ulong)shTableOffset);
        WriteBlobUInt64(blob, 0x10, address + (ulong)ucTableOffset);
        WriteBlobUInt64(blob, 0x30, address + (ulong)typesOffset);
        WriteBlobUInt32(blob, 0x38, (uint)groups.Length);

        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var group = groups[groupIndex];
            if (group.Registers.Length > 16)
            {
                return false;
            }

            var tableOffset = group.Space switch
            {
                0 => cxTableOffset,
                1 => shTableOffset,
                2 => ucTableOffset,
                _ => -1,
            };
            var tableLength = group.Space switch
            {
                0 => cxTableLength,
                1 => shTableLength,
                2 => ucTableLength,
                _ => 0,
            };
            if (tableOffset < 0 || group.Index >= tableLength)
            {
                return false;
            }

            var registerBlockOffset = registerBlocksOffset + (groupIndex * RegisterDefaultBlockSize);
            WriteBlobUInt64(
                blob,
                tableOffset + ((int)group.Index * sizeof(ulong)),
                address + (ulong)registerBlockOffset);

            var typeEntryOffset = typesOffset + (groupIndex * 3 * sizeof(uint));
            WriteBlobUInt32(blob, typeEntryOffset, group.Type);
            WriteBlobUInt32(blob, typeEntryOffset + sizeof(uint), (group.Index * 4) + group.Space);

            for (var registerIndex = 0; registerIndex < group.Registers.Length; registerIndex++)
            {
                var register = group.Registers[registerIndex];
                var registerOffset = registerBlockOffset + (registerIndex * 2 * sizeof(uint));
                WriteBlobUInt32(blob, registerOffset, register.Offset);
                WriteBlobUInt32(blob, registerOffset + sizeof(uint), register.Value);
            }
        }

        return ctx.Memory.TryWrite(address, blob);
    }

    private static int AlignUp(int value, int alignment) =>
        (value + alignment - 1) & -alignment;

    private static void WriteBlobUInt32(Span<byte> blob, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(blob[offset..], value);

    private static void WriteBlobUInt64(Span<byte> blob, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(blob[offset..], value);

    private static int ReturnPointer(CpuContext ctx, ulong pointer)
    {
        ctx[CpuRegister.Rax] = pointer;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(int)result);
        return (int)result;
    }

    private static uint Pm4(uint lengthDwords, uint op, uint register) =>
        0xC0000000u |
        ((((ushort)lengthDwords - 2u) & 0x3FFFu) << 16) |
        ((op & 0xFFu) << 8) |
        ((register & 0x3Fu) << 2);

    private static uint Pm4Length(uint header) =>
        ((header >> 16) & 0x3FFFu) + 2u;

    private static bool TryReadByte(CpuContext ctx, ulong address, out byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = buffer[0];
        return true;
    }

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static bool TryReadUInt64(CpuContext ctx, ulong address, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return true;
    }

    private static bool TryReadGuestCString(
        CpuContext ctx,
        ulong address,
        int maximumLength,
        out byte[] bytes)
    {
        if (address == 0)
        {
            bytes = [];
            return true;
        }

        var values = new List<byte>(Math.Min(maximumLength, 128));
        for (var index = 0; index < maximumLength; index++)
        {
            if (!TryReadByte(ctx, address + (ulong)index, out var value))
            {
                bytes = [];
                return false;
            }

            if (value == 0)
            {
                bytes = [.. values];
                return true;
            }

            values.Add(value);
        }

        bytes = [];
        return false;
    }

    private static bool TryGetPacketIdentity(
        CpuContext ctx,
        ulong commandAddress,
        out uint op,
        out uint register)
    {
        op = 0;
        register = 0;
        if (commandAddress == 0 || !TryReadUInt32(ctx, commandAddress, out var header))
        {
            return false;
        }

        op = (header >> 8) & 0xFFu;
        register = (header >> 2) & 0x3Fu;
        return true;
    }

    private static bool TryCopyGuestMemory(
        CpuContext ctx,
        ulong sourceAddress,
        ulong destinationAddress,
        uint byteCount)
    {
        if (sourceAddress == destinationAddress)
        {
            return true;
        }

        var buffer = new byte[Math.Min(byteCount, 64u * 1024u)];
        ulong offset = 0;
        while (offset < byteCount)
        {
            var chunkLength = (int)Math.Min((ulong)buffer.Length, byteCount - offset);
            var chunk = buffer.AsSpan(0, chunkLength);
            if (!ctx.Memory.TryRead(sourceAddress + offset, chunk) ||
                !ctx.Memory.TryWrite(destinationAddress + offset, chunk))
            {
                return false;
            }

            offset += (uint)chunkLength;
        }

        return true;
    }

    private static bool ShouldTraceHotPath(ref long counter)
    {
        var count = Interlocked.Increment(ref counter);
        return count <= 8 || count % 100_000 == 0;
    }

    private static void TraceAgc(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] {message}");
    }

    private static void TraceCreateShader(ulong destinationAddress, ulong headerAddress, ulong codeAddress, string detail)
    {
        var isOk = string.Equals(detail, "ok", StringComparison.Ordinal);
        if (isOk &&
            (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"), "1", StringComparison.Ordinal) ||
             !ShouldTraceHotPath(ref _createShaderTraceCount)))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] agc.create_shader dst=0x{destinationAddress:X16} header=0x{headerAddress:X16} code=0x{codeAddress:X16} {detail}");
    }
}
