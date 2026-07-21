// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

public sealed class PthreadMutexSemanticsTests
{
    [Fact]
    public void AdaptiveMutex_SelfLockIsIdempotent()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong mutexAddress = memoryBase + 0x100;
        var memory = new AllocatingCpuMemory(memoryBase, 0x4000);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(context.TryWriteUInt64(mutexAddress, 1)); // Static adaptive initializer.
        context[CpuRegister.Rdi] = mutexAddress;

        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(context));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(context));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
        Assert.NotEqual(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
    }

    [Fact]
    public void AdaptiveMutex_GuestTrackedSelfLockReturnsDeadlockAndSingleUnlockReleases()
    {
        const ulong memoryBase = 0x1_0001_0000;
        const ulong mutexAddress = memoryBase + 0x100;
        var memory = new AllocatingCpuMemory(memoryBase, 0x4000);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(context.TryWriteUInt64(mutexAddress, 1)); // Static adaptive initializer.
        context[CpuRegister.Rdi] = mutexAddress;

        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(context));

        var currentThreadHandle = KernelPthreadState.GetCurrentThreadHandle();
        Assert.True(context.TryWriteUInt64(mutexAddress + 8, currentThreadHandle));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK,
            KernelPthreadCompatExports.PthreadMutexLock(context));

        Assert.True(context.TryWriteUInt64(mutexAddress + 8, 0));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexTrylock(context));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
    }

    [Fact]
    public void RecursiveMutex_GuestTrackedSelfLockKeepsRecursiveSemantics()
    {
        const ulong memoryBase = 0x1_0002_0000;
        const ulong attrAddress = memoryBase + 0x100;
        const ulong mutexAddress = memoryBase + 0x200;
        var memory = new AllocatingCpuMemory(memoryBase, 0x4000);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = attrAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexattrInit(context));
        context[CpuRegister.Rsi] = 2;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexattrSettype(context));

        context[CpuRegister.Rdi] = mutexAddress;
        context[CpuRegister.Rsi] = attrAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexInit(context));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(context));

        var currentThreadHandle = KernelPthreadState.GetCurrentThreadHandle();
        Assert.True(context.TryWriteUInt64(mutexAddress + 8, currentThreadHandle));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(context));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
        Assert.NotEqual(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
    }

    [Fact]
    public async Task ContendedMutex_HandsOffOneHostWaiterAtATime()
    {
        const ulong memoryBase = 0x2_0000_0000;
        const ulong mutexAddress = memoryBase + 0x100;
        var memory = new AllocatingCpuMemory(memoryBase, 0x4000);
        var ownerContext = new CpuContext(memory, Generation.Gen5);
        Assert.True(ownerContext.TryWriteUInt64(mutexAddress, 1));
        ownerContext[CpuRegister.Rdi] = mutexAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(ownerContext));

        using var waitersStarted = new CountdownEvent(2);
        using var firstAcquired = new ManualResetEventSlim(false);
        using var secondAcquired = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);
        var acquisitionCount = 0;

        Task<(int LockResult, int UnlockResult)> StartWaiter() =>
            Task.Factory.StartNew(
                () =>
                {
                    var waiterContext = new CpuContext(memory, Generation.Gen5);
                    waiterContext[CpuRegister.Rdi] = mutexAddress;
                    waitersStarted.Signal();
                    var lockResult = KernelPthreadCompatExports.PthreadMutexLock(waiterContext);
                    if (lockResult != 0)
                    {
                        return (lockResult, int.MinValue);
                    }

                    if (Interlocked.Increment(ref acquisitionCount) == 1)
                    {
                        firstAcquired.Set();
                        releaseFirst.Wait(TimeSpan.FromSeconds(5));
                    }
                    else
                    {
                        secondAcquired.Set();
                    }

                    var unlockResult = KernelPthreadCompatExports.PthreadMutexUnlock(waiterContext);
                    return (lockResult, unlockResult);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

        var firstWaiter = StartWaiter();
        var secondWaiter = StartWaiter();
        Assert.True(waitersStarted.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(50);
        Assert.Equal(0, Volatile.Read(ref acquisitionCount));

        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(ownerContext));
        Assert.True(firstAcquired.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref acquisitionCount));
        releaseFirst.Set();
        Assert.True(secondAcquired.Wait(TimeSpan.FromSeconds(5)));

        var results = await Task.WhenAll(firstWaiter, secondWaiter).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, result => Assert.Equal((0, 0), result));
        Assert.Equal(2, Volatile.Read(ref acquisitionCount));
    }

    [Fact]
    public async Task ContendedHostMutex_ServicesGuestExceptionSafePointWhileWaiting()
    {
        const ulong memoryBase = 0x2_1000_0000;
        const ulong mutexAddress = memoryBase + 0x100;
        var memory = new AllocatingCpuMemory(memoryBase, 0x4000);
        var ownerContext = new CpuContext(memory, Generation.Gen5);
        ownerContext[CpuRegister.Rdi] = mutexAddress;
        Assert.True(ownerContext.TryWriteUInt64(mutexAddress, 1));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(ownerContext));

        var scheduler = new BlockingImportSafePointScheduler();
        var previousScheduler = GuestThreadExecution.Scheduler;
        Task<(int LockResult, int UnlockResult)>? waiterTask = null;
        var ownerReleased = false;
        try
        {
            GuestThreadExecution.Scheduler = scheduler;
            waiterTask = Task.Factory.StartNew(
                () =>
                {
                    var waiterContext = new CpuContext(memory, Generation.Gen5);
                    waiterContext[CpuRegister.Rdi] = mutexAddress;
                    var previousFrame = GuestThreadExecution.EnterImportCallFrame(
                        returnRip: 0x8_0001_0000,
                        resumeRsp: memoryBase + 0x3000,
                        returnSlotAddress: memoryBase + 0x2FF8);
                    try
                    {
                        var lockResult = KernelPthreadCompatExports.PthreadMutexLock(waiterContext);
                        var unlockResult = lockResult == 0
                            ? KernelPthreadCompatExports.PthreadMutexUnlock(waiterContext)
                            : int.MinValue;
                        return (lockResult, unlockResult);
                    }
                    finally
                    {
                        GuestThreadExecution.RestoreImportCallFrame(previousFrame);
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            Assert.True(scheduler.Serviced.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(scheduler.CapturedImportContinuation);
            Assert.False(waiterTask.IsCompleted);

            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(ownerContext));
            ownerReleased = true;
            Assert.Equal((0, 0), await waiterTask.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            if (!ownerReleased)
            {
                _ = KernelPthreadCompatExports.PthreadMutexUnlock(ownerContext);
            }

            if (waiterTask is not null)
            {
                _ = await Task.WhenAny(waiterTask, Task.Delay(TimeSpan.FromSeconds(5)));
            }

            GuestThreadExecution.Scheduler = previousScheduler;
        }
    }

    [Fact]
    public async Task ContendedMutex_PreservesMutualExclusionUnderLoad()
    {
        const ulong memoryBase = 0x3_0000_0000;
        const ulong mutexAddress = memoryBase + 0x100;
        const int workerCount = 4;
        const int iterationsPerWorker = 250;
        var memory = new AllocatingCpuMemory(memoryBase, 0x4000);
        var initializationContext = new CpuContext(memory, Generation.Gen5);
        Assert.True(initializationContext.TryWriteUInt64(mutexAddress, 1));
        initializationContext[CpuRegister.Rdi] = mutexAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(initializationContext));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(initializationContext));

        using var start = new ManualResetEventSlim(false);
        var insideCriticalSection = 0;
        var mutualExclusionViolations = 0;
        var protectedCounter = 0;
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    var context = new CpuContext(memory, Generation.Gen5);
                    context[CpuRegister.Rdi] = mutexAddress;
                    start.Wait();
                    for (var iteration = 0; iteration < iterationsPerWorker; iteration++)
                    {
                        if (KernelPthreadCompatExports.PthreadMutexLock(context) != 0)
                        {
                            throw new InvalidOperationException("pthread mutex lock failed during contention stress.");
                        }

                        if (Interlocked.Increment(ref insideCriticalSection) != 1)
                        {
                            Interlocked.Increment(ref mutualExclusionViolations);
                        }

                        protectedCounter++;
                        Thread.SpinWait(20);
                        Interlocked.Decrement(ref insideCriticalSection);

                        if (KernelPthreadCompatExports.PthreadMutexUnlock(context) != 0)
                        {
                            throw new InvalidOperationException("pthread mutex unlock failed during contention stress.");
                        }
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        start.Set();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, Volatile.Read(ref mutualExclusionViolations));
        Assert.Equal(workerCount * iterationsPerWorker, protectedCounter);
    }

    private sealed class AllocatingCpuMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private readonly ulong _baseAddress;
        private readonly byte[] _storage;
        private ulong _nextAllocation;

        public AllocatingCpuMemory(ulong baseAddress, int size)
        {
            _baseAddress = baseAddress;
            _storage = new byte[size];
            _nextAllocation = baseAddress + 0x1000;
        }

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

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            var mask = alignment - 1;
            var aligned = (_nextAllocation + mask) & ~mask;
            if (!TryResolve(aligned, checked((int)size), out _))
            {
                address = 0;
                return false;
            }

            address = aligned;
            _nextAllocation = aligned + size;
            return true;
        }

        public bool TryFreeGuestMemory(ulong address) =>
            address >= _baseAddress && address < _baseAddress + (ulong)_storage.Length;

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < _baseAddress)
            {
                return false;
            }

            var relative = virtualAddress - _baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }

    private sealed class BlockingImportSafePointScheduler : IGuestThreadScheduler
    {
        public ManualResetEventSlim Serviced { get; } = new(false);

        public bool CapturedImportContinuation { get; private set; }

        public bool SupportsGuestContextTransfer => false;

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context)
        {
        }

        public void ServicePendingGuestExceptionAtBlockingImport(CpuContext callerContext)
        {
            CapturedImportContinuation =
                GuestThreadExecution.TryCaptureCurrentImportContinuation(callerContext, out _);
            Serviced.Set();
        }

        public bool TryStartThread(
            CpuContext creatorContext,
            GuestThreadStartRequest request,
            out string? error)
        {
            error = null;
            return false;
        }

        public bool TryJoinThread(
            CpuContext callerContext,
            ulong threadHandle,
            out ulong returnValue,
            out string? error)
        {
            returnValue = 0;
            error = null;
            return false;
        }

        public void Pump(CpuContext callerContext, string reason)
        {
        }

        public int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue) => 0;

        public bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority) => false;

        public bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask) => false;

        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => [];

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out string? error)
        {
            error = null;
            return false;
        }

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong arg2,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out ulong returnValue,
            out string? error)
        {
            returnValue = 0;
            error = null;
            return false;
        }

        public bool TryCallGuestContinuation(
            CpuContext callerContext,
            GuestCpuContinuation continuation,
            string reason,
            out string? error)
        {
            error = null;
            return false;
        }

        public bool TryRaiseGuestException(
            CpuContext callerContext,
            ulong threadHandle,
            ulong handler,
            int exceptionType,
            out string? error)
        {
            error = null;
            return false;
        }
    }
}
