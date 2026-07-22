// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

// libKernel's address-wait primitives (sceKernelSyncOnAddress*) are the PS5's
// futex-style wait/wake: a thread parks on a guest address until another thread
// wakes that address. Guest runtimes (seen driving Juicy Realm, PPSA19268)
// build their own spinlocks/queues on top of it and call the wait in a hot
// loop; left unimplemented, every wait returns immediately and the runtime
// busy-spins forever (millions of calls, no forward progress).
//
// This implements wait/wake over the existing cooperative-block scheduler,
// keyed on the address. The real primitive takes a compare value so the wait
// only sleeps while the address still holds the expected value; that exact
// value is not recovered here, so each wait is given a bounded deadline and
// treated as a spurious-wakeup-tolerant park: a genuinely missed wake
// self-heals when the deadline expires and the guest re-checks its own
// condition, which futex callers already tolerate. A matching wake releases
// waiters immediately through the same key.
public static class KernelSyncOnAddressCompatExports
{
    // Safety-net poll interval. Real releases come from the wake side (generation
    // bump + WakeBlockedThreads); this only bounds how long a wait that genuinely
    // raced/missed its wake stays parked before the guest re-evaluates. Kept
    // large: a short interval turns every parked waiter into a hot re-poll that
    // steals scheduler bandwidth from the threads that actually make progress
    // (including the ones that would issue the wake), so it must be a rare last
    // resort, not a spin substitute.
    private static readonly TimeSpan WaitSelfHealTimeout = TimeSpan.FromMilliseconds(100);

    // Per-address host gate for the non-cooperative (host main thread) fallback,
    // which cannot use the guest-thread scheduler's block mechanism.
    private static readonly ConcurrentDictionary<ulong, object> _hostAddressGates = new();

    // Per-address wake generation. A wait captures the current generation and
    // its wake predicate stays unsatisfied (keeps the thread parked) until a
    // wake bumps it. This is what actually holds the thread blocked: a bare
    // "always satisfied" predicate is treated as an immediate late-arrival by
    // the dispatcher's race guard and never yields, leaving the guest to
    // busy-spin. The generation also closes the register-vs-park race for free:
    // a wake landing in that window bumps the generation, so the predicate is
    // already satisfied and the guest correctly resumes at once.
    private static readonly ConcurrentDictionary<ulong, long> _wakeGenerations = new();

    // --- TEMP DIAGNOSTIC (remove after investigation) ---
    private static readonly bool _logSyncOnAddress = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_SYNC_ON_ADDRESS"),
        "1",
        StringComparison.Ordinal);
    private static readonly string? _syncLogTriggerFile =
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_SYNC_ON_ADDRESS_TRIGGER_FILE");
    private static volatile bool _syncLogTriggerObserved;
    private static readonly Timer? _syncLogTriggerPoller = CreateSyncLogTriggerPoller();
    private static readonly ConcurrentDictionary<ulong, long> _waitLogCounts = new();
    private static long _wakeLogCount;
    // --- END TEMP DIAGNOSTIC ---

    private static bool SyncDiagnosticsEnabled =>
        _logSyncOnAddress &&
        (string.IsNullOrWhiteSpace(_syncLogTriggerFile) || _syncLogTriggerObserved);

    private static Timer? CreateSyncLogTriggerPoller()
    {
        if (!_logSyncOnAddress || string.IsNullOrWhiteSpace(_syncLogTriggerFile))
        {
            return null;
        }

        return new Timer(
            static _ =>
            {
                if (!_syncLogTriggerObserved && File.Exists(_syncLogTriggerFile))
                {
                    _syncLogTriggerObserved = true;
                }
            },
            null,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(250));
    }

    private static long CurrentGeneration(ulong address) =>
        _wakeGenerations.TryGetValue(address, out var generation) ? generation : 0;

    private static string WakeKey(ulong address) => $"sceKernelSyncOnAddress:{address:X16}";

    [SysAbiExport(
        Nid = "Hc4CaR6JBL0",
        ExportName = "sceKernelSyncOnAddressWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWait(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var observedGeneration = CurrentGeneration(address);
        var deadline = GuestThreadExecution.ComputeDeadlineTimestamp(WaitSelfHealTimeout);

        // --- TEMP DIAGNOSTIC (remove after investigation) ---
        if (SyncDiagnosticsEnabled)
        {
            var callCount = _waitLogCounts.AddOrUpdate(address, 1, static (_, current) => current + 1);
            // Log the first 5 calls per address, then every 500th, to avoid
            // flooding the console while still showing whether this address
            // keeps getting called and whether IsGuestThread flips.
            if (callCount <= 5 || callCount % 500 == 0)
            {
                Console.Error.WriteLine(
                    $"[SYNCDIAG] wait addr=0x{address:X16} call#{callCount} " +
                    $"managed={Environment.CurrentManagedThreadId} " +
                    $"is_guest_thread={GuestThreadExecution.IsGuestThread} " +
                    $"guest_handle=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} " +
                    $"observed_gen={observedGeneration} current_gen={CurrentGeneration(address)}");
            }
        }
        // --- END TEMP DIAGNOSTIC ---

        // Cooperative path: stay parked until a wake bumps this address's
        // generation (or the deadline expires as a self-heal). The guest
        // re-evaluates its own condition after resuming.
        if (GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                "sceKernelSyncOnAddressWait",
                WakeKey(address),
                resumeHandler: () => (int)OrbisGen2Result.ORBIS_GEN2_OK,
                wakeHandler: () => CurrentGeneration(address) != observedGeneration,
                deadline))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        // --- TEMP DIAGNOSTIC (remove after investigation) ---
        if (SyncDiagnosticsEnabled)
        {
            var callCount = _waitLogCounts.TryGetValue(address, out var current) ? current : 0;
            if (callCount <= 5 || callCount % 500 == 0)
            {
                Console.Error.WriteLine(
                    $"[SYNCDIAG] wait addr=0x{address:X16} FELL THROUGH to host-gate fallback " +
                    $"(RequestCurrentThreadBlock returned false) managed={Environment.CurrentManagedThreadId}");
            }
        }
        // --- END TEMP DIAGNOSTIC ---

        // Non-cooperative caller (host main thread): bounded host wait so a
        // missed wake self-heals instead of hanging.
        var gate = _hostAddressGates.GetOrAdd(address, static _ => new object());
        lock (gate)
        {
            if (CurrentGeneration(address) == observedGeneration)
            {
                Monitor.Wait(gate, WaitSelfHealTimeout);
            }
        }

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "q2y-wDIVWZA",
        ExportName = "sceKernelSyncOnAddressWake",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWake(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // rsi carries the number of waiters to release (1 = wake-one, a large
        // value = wake-all); default to all if it looks unset.
        var requested = unchecked((long)ctx[CpuRegister.Rsi]);
        var wakeCount = requested is > 0 and < int.MaxValue ? (int)requested : int.MaxValue;

        // --- TEMP DIAGNOSTIC (remove after investigation) ---
        if (SyncDiagnosticsEnabled)
        {
            var wakeLogIndex = Interlocked.Increment(ref _wakeLogCount);
            if (wakeLogIndex <= 50 || wakeLogIndex % 100 == 0)
            {
                Console.Error.WriteLine(
                    $"[SYNCDIAG] wake addr=0x{address:X16} call#{wakeLogIndex} requested={requested} " +
                    $"managed={Environment.CurrentManagedThreadId} " +
                    $"is_guest_thread={GuestThreadExecution.IsGuestThread} " +
                    $"guest_handle=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} " +
                    $"has_waiters_registered={_waitLogCounts.ContainsKey(address)}");
            }
        }
        // --- END TEMP DIAGNOSTIC ---

        // Bump the generation first so a wait that has registered but not yet
        // parked sees the change and resumes instead of missing this wake.
        _wakeGenerations.AddOrUpdate(address, 1, static (_, current) => current + 1);

        GuestThreadExecution.Scheduler?.WakeBlockedThreads(WakeKey(address), wakeCount);

        if (_hostAddressGates.TryGetValue(address, out var gate))
        {
            lock (gate)
            {
                Monitor.PulseAll(gate);
            }
        }

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        var value = (int)result;
        ctx[CpuRegister.Rax] = unchecked((ulong)value);
        return value;
    }
}
