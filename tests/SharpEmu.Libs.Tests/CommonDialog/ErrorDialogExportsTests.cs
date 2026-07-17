// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.CommonDialog;
using Xunit;

namespace SharpEmu.Libs.Tests.CommonDialog;

// ErrorDialogExports stores process-wide state in static fields.
// Disable parallel execution to prevent tests from interfering with each other.
[CollectionDefinition("ErrorDialogExports", DisableParallelization = true)]
public sealed class ErrorDialogExportsCollection;

[Collection("ErrorDialogExports")]
public sealed class ErrorDialogExportsTests
{
    private const ulong BaseAddress = 0x2_0000_0000;
    private const ulong ParamAddress = BaseAddress + 0x100;

    private const int AlreadyInitialized = unchecked((int)0x80ED0001);
    private const int NotInitialized = unchecked((int)0x80ED0002);
    private const int ArgNull = unchecked((int)0x80ED0005);

    private const int StatusNone = 0;
    private const int StatusInitialized = 1;
    private const int StatusFinished = 3;

    private readonly FakeCpuMemory _memory = new(BaseAddress, 0x1000);
    private readonly CpuContext _ctx;

    public ErrorDialogExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);

        // Ensure every test starts from a clean state.
        ErrorDialogExports.ErrorDialogTerminate(_ctx);
    }

    [Fact]
    public void GetStatus_BeforeInitialize_ReturnsNone()
    {
        AssertExportResult(
            StatusNone,
            ErrorDialogExports.ErrorDialogGetStatus);
    }

    [Fact]
    public void Initialize_FirstCall_SucceedsAndSetsInitializedStatus()
    {
        AssertExportResult(
            0,
            ErrorDialogExports.ErrorDialogInitialize);

        AssertExportResult(
            StatusInitialized,
            ErrorDialogExports.ErrorDialogGetStatus);
    }

    [Fact]
    public void Initialize_SecondCall_ReturnsAlreadyInitialized()
    {
        AssertExportResult(
            0,
            ErrorDialogExports.ErrorDialogInitialize);

        AssertExportResult(
            AlreadyInitialized,
            ErrorDialogExports.ErrorDialogInitialize);
    }

    [Fact]
    public void Open_WithoutInitialize_ReturnsNotInitialized()
    {
        _ctx[CpuRegister.Rdi] = ParamAddress;

        AssertExportResult(
            NotInitialized,
            ErrorDialogExports.ErrorDialogOpen);
    }

    [Fact]
    public void Open_WithNullParameter_ReturnsArgNull()
    {
        AssertExportResult(
            0,
            ErrorDialogExports.ErrorDialogInitialize);

        _ctx[CpuRegister.Rdi] = 0;

        AssertExportResult(
            ArgNull,
            ErrorDialogExports.ErrorDialogOpen);
    }

    [Fact]
    public void Open_AfterInitialize_ReportsFinishedImmediately()
    {
        AssertExportResult(
            0,
            ErrorDialogExports.ErrorDialogInitialize);

        _ctx[CpuRegister.Rdi] = ParamAddress;

        AssertExportResult(
            0,
            ErrorDialogExports.ErrorDialogOpen);

        // There is no host dialog, so guests polling the status must not spin forever.
        AssertExportResult(
            StatusFinished,
            ErrorDialogExports.ErrorDialogGetStatus);

        AssertExportResult(
            StatusFinished,
            ErrorDialogExports.ErrorDialogUpdateStatus);
    }

    [Fact]
    public void Close_SetsStatusFinished()
    {
        AssertExportResult(
            0,
            ErrorDialogExports.ErrorDialogInitialize);

        AssertExportResult(
            0,
            ErrorDialogExports.ErrorDialogClose);

        AssertExportResult(
            StatusFinished,
            ErrorDialogExports.ErrorDialogGetStatus);
    }

    [Fact]
    public void Terminate_ResetsStateAndAllowsReinitialize()
    {
        AssertExportResult(
            0,
            ErrorDialogExports.ErrorDialogInitialize);

        AssertExportResult(
            0,
            ErrorDialogExports.ErrorDialogTerminate);

        AssertExportResult(
            StatusNone,
            ErrorDialogExports.ErrorDialogGetStatus);

        AssertExportResult(
            0,
            ErrorDialogExports.ErrorDialogInitialize);
    }

    private void AssertExportResult(
        int expected,
        Func<CpuContext, int> export)
    {
        var result = export(_ctx);

        Assert.Equal(expected, result);
        Assert.Equal(
            unchecked((ulong)expected),
            _ctx[CpuRegister.Rax]);
    }
}
