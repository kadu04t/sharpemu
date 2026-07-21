// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Tests;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

public sealed class PthreadAttributeSemanticsTests
{
    [Fact]
    public void AttrGet_InfersEveryNativeExecutorStackSlot()
    {
        const ulong memoryBase = 0x3_1000_0000;
        const ulong attrAddress = memoryBase + 0x100;
        const ulong outStackAddress = memoryBase + 0x200;
        const ulong outStackSizeAddress = memoryBase + 0x208;
        const ulong stackSize = 0x20_0000;
        const ulong stackStride = 0x100_0000;
        var guestThreadStackBase = OperatingSystem.IsWindows()
            ? 0x00007FFF_E000_0000UL
            : 0x00006FFF_E000_0000UL;
        var memory = new FakeCpuMemory(memoryBase, 0x4000);
        var context = new CpuContext(memory, Generation.Gen5);
        var currentThread = KernelPthreadState.GetCurrentThreadHandle();

        // Slot 48 was the first executor stack below the old 64-slot window;
        // slot 1023 is the final slot the native backend can allocate.
        foreach (var slot in new[] { 48UL, 1023UL })
        {
            var expectedStackAddress = guestThreadStackBase - (slot * stackStride);
            context[CpuRegister.Rsp] = expectedStackAddress + stackSize - 0x20;
            context[CpuRegister.Rdi] = currentThread;
            context[CpuRegister.Rsi] = attrAddress;
            Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadAttrGet(context));

            context[CpuRegister.Rdi] = attrAddress;
            context[CpuRegister.Rsi] = outStackAddress;
            context[CpuRegister.Rdx] = outStackSizeAddress;
            Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadAttrGetstack(context));
            Assert.True(context.TryReadUInt64(outStackAddress, out var actualStackAddress));
            Assert.True(context.TryReadUInt64(outStackSizeAddress, out var actualStackSize));
            Assert.Equal(expectedStackAddress, actualStackAddress);
            Assert.Equal(stackSize, actualStackSize);
        }
    }
}
