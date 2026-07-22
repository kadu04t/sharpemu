// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VideoOutFlipStatusTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong StatusAddress = MemoryBase + 0x100;

    [Fact]
    public void SubmitFlip_ReportsCompletedFlipArgumentInStatus()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        var handle = VideoOutExports.VideoOutOpen(context);
        Assert.True(handle > 0);

        try
        {
            const long flipArgument = unchecked((long)0x8000_0000_0000_002AUL);
            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            context[CpuRegister.Rsi] = unchecked((ulong)-1L);
            context[CpuRegister.Rdx] = 0;
            context[CpuRegister.Rcx] = unchecked((ulong)flipArgument);
            Assert.Equal(0, VideoOutExports.VideoOutSubmitFlip(context));

            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            context[CpuRegister.Rsi] = StatusAddress;
            Assert.Equal(0, VideoOutExports.VideoOutGetFlipStatus(context));

            Span<byte> status = stackalloc byte[0x28];
            Assert.True(memory.TryRead(StatusAddress, status));
            Assert.Equal(
                unchecked((ulong)flipArgument),
                BinaryPrimitives.ReadUInt64LittleEndian(status[0x18..0x20]));
        }
        finally
        {
            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            VideoOutExports.VideoOutClose(context);
        }
    }
}
