// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelFileExtendedExportsTests
{
    [Fact]
    public void WriteGuestBufferPaged_SplitsAnUnalignedPayloadAtGuestPageBoundaries()
    {
        const ulong baseAddress = 0x1_0000_0000;
        var memory = new PageBoundedCpuMemory(baseAddress, 0xD000);
        var payload = Enumerable.Range(0, 0x9000).Select(index => (byte)index).ToArray();

        var result = KernelMemoryCompatExports.TryWriteGuestBufferPaged(
            memory,
            baseAddress + 0x3FF0,
            payload);

        Assert.True(result);
        Assert.Equal(payload, memory.Read(baseAddress + 0x3FF0, payload.Length));
        Assert.Equal([0x10, 0x4000, 0x4000, 0xFF0], memory.WriteLengths);
    }

    [Fact]
    public void WriteGuestBufferPaged_RejectsAnUnmappedGuestPage()
    {
        const ulong baseAddress = 0x1_0000_0000;
        var memory = new PageBoundedCpuMemory(baseAddress, 0xC000, rejectedPage: 1);
        var payload = new byte[0x5000];

        var result = KernelMemoryCompatExports.TryWriteGuestBufferPaged(
            memory,
            baseAddress + 0x2000,
            payload);

        Assert.False(result);
    }

    private sealed class PageBoundedCpuMemory(
        ulong baseAddress,
        int size,
        int rejectedPage = -1) : ICpuMemory
    {
        private const ulong PageSize = 0x4000;
        private readonly byte[] _storage = new byte[size];

        public List<int> WriteLengths { get; } = [];

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            var relative = virtualAddress - baseAddress;
            var firstPage = (int)(relative / PageSize);
            var lastPage = source.IsEmpty
                ? firstPage
                : (int)((relative + (ulong)source.Length - 1) / PageSize);
            if (firstPage != lastPage || firstPage == rejectedPage)
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            WriteLengths.Add(source.Length);
            return true;
        }

        public byte[] Read(ulong address, int length)
        {
            var result = new byte[length];
            Assert.True(TryRead(address, result));
            return result;
        }

        private bool TryResolve(ulong address, int length, out int offset)
        {
            offset = 0;
            if (address < baseAddress)
            {
                return false;
            }

            var relative = address - baseAddress;
            if (relative > (ulong)_storage.Length ||
                (ulong)length > (ulong)_storage.Length - relative)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
