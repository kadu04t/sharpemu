// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

public sealed class AvPlayerIsoBmffTests
{
    [Fact]
    public void SegmentSizeStopsAfterMdatAndMoov()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var output = File.OpenWrite(path))
            {
                output.Write(new byte[13]);
                WriteBox(output, "ftyp", 24);
                WriteBox(output, "free", 0);
                WriteBox(output, "mdat", 4);
                WriteBox(output, "moov", 8);
                output.Write(new byte[17]);
            }

            Assert.True(AvPlayerExports.TryGetIsoBmffSegmentSize(path, 13, out var size));
            Assert.Equal(68UL, size);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SegmentSizeRejectsDataWithoutMdatAndMoov()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var output = File.OpenWrite(path))
            {
                WriteBox(output, "ftyp", 16);
                WriteBox(output, "free", 8);
            }

            Assert.False(AvPlayerExports.TryGetIsoBmffSegmentSize(path, 0, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void WriteBox(Stream output, string type, int payloadSize)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)(payloadSize + header.Length)));
        System.Text.Encoding.ASCII.GetBytes(type, header[4..]);
        output.Write(header);
        output.Write(new byte[payloadSize]);
    }
}
