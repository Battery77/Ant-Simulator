using System.Buffers.Binary;
using AntSim.Core.Scent;
using AntSim.Core.Serialization;
using AntSim.Core.Simulation;
using AntSim.Core.Worlds;
using Xunit.Abstractions;

namespace AntSim.Core.Tests;

// PR #2 독립 재검증(da05632)에서 재현된 결함 F02(종료/실행권 경합), F03(농도 상한 경계), N01(non-seek 과잉 읽기)의 회귀 테스트.
// 각 테스트는 올바른 계약을 기대한다(결함 관찰값을 확인하는 재현 하네스와 반대).

public class VerificationF02DisposeRaceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static SimulationHost NewHost() =>
        new(new SimulationSession(TestWorlds.Create(5, 5), ScentSettings.Default));

    private static async Task AssertTerminates(Task task, string name)
    {
        var finished = await Task.WhenAny(task, Task.Delay(Timeout));
        Assert.True(finished == task, $"{name}이(가) {Timeout.TotalSeconds}초 안에 끝나지 않음");
    }

    /// <summary>
    /// 테스트 A — 종료가 먼저 실행권을 얻는 순서(재검증 T1): 계산 스레드가 종료 검사를 통과하고 다음 명령을 꺼내기 직전에 멈춘 사이
    /// Dispose가 Stopping으로 바꾼다. 그 명령은 실행되지 않고 취소되어야 한다.
    /// </summary>
    [Fact]
    public async Task DisposeBeforeDequeue_PendingCallbackNeverRuns_AndIsCanceled()
    {
        var host = NewHost();
        using var entered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var atBoundary = new ManualResetEventSlim();
        using var releaseBoundary = new ManualResetEventSlim();
        Task? dispose = null;
        try
        {
            var first = host.RunExclusiveAsync(_ => { entered.Set(); releaseFirst.Wait(Timeout); return 7; });
            Assert.True(entered.Wait(Timeout), "첫 명령이 시작되지 않음");

            int armed = 1;
            host.TestHook = point =>
            {
                if (point != "before-dequeue" || Interlocked.Exchange(ref armed, 0) == 0) return;
                atBoundary.Set();
                releaseBoundary.Wait(Timeout);
            };
            int calls = 0;
            var target = host.RunExclusiveAsync(_ => Interlocked.Increment(ref calls));

            releaseFirst.Set();
            Assert.True(atBoundary.Wait(Timeout), "계산 스레드가 명령을 꺼내기 직전 지점에 도달하지 않음");
            Assert.Equal(0, calls);
            Assert.False(target.IsCompleted);

            dispose = Task.Run(host.Dispose);
            Assert.True(SpinWait.SpinUntil(() => host.DisposeRequested, Timeout), "Dispose가 Stopping으로 바꾸지 않음");
            releaseBoundary.Set();

            await AssertTerminates(dispose, "Dispose");
            await AssertTerminates(target, "대기 명령");
            Assert.Equal(7, await first);
            Assert.Equal(0, Volatile.Read(ref calls));
            Assert.True(target.IsCanceled, $"대기 명령 상태: {target.Status}");
            Assert.Null(host.Failure);
        }
        finally
        {
            releaseFirst.Set();
            releaseBoundary.Set();
            host.TestHook = null;
            dispose ??= Task.Run(host.Dispose);
            Assert.True(dispose.Wait(Timeout), "정리 중 Dispose가 끝나지 않음");
        }
    }

    /// <summary>
    /// 테스트 B — 명령이 먼저 실행권을 얻는 순서: 실행 중인 명령은 Dispose와 무관하게 정확히 한 번 끝까지 실행되고
    /// (성공 또는 실제 예외), 그 뒤 대기 명령은 취소된다. 예외여도 공개 Tick은 세션과 맞는다(F07).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunningCallback_CompletesExactlyOnce_QueuedAreCanceled(bool throws)
    {
        var host = NewHost();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task? dispose = null;
        try
        {
            int runs = 0, queuedRuns = 0;
            var running = host.RunExclusiveAsync(s =>
            {
                Interlocked.Increment(ref runs);
                entered.Set();
                release.Wait(Timeout);
                s.Step();
                if (throws) throw new InvalidOperationException("명령 예외");
                return s.Tick;
            });
            Assert.True(entered.Wait(Timeout));
            var queued = new[]
            {
                host.RunExclusiveAsync(_ => Interlocked.Increment(ref queuedRuns)),
                host.RunExclusiveAsync(_ => Interlocked.Increment(ref queuedRuns)),
            };

            dispose = Task.Run(host.Dispose);
            Assert.True(SpinWait.SpinUntil(() => host.DisposeRequested, Timeout));
            release.Set();

            await AssertTerminates(dispose, "Dispose");
            await AssertTerminates(running, "실행 중 명령");
            foreach (var q in queued) await AssertTerminates(q, "대기 명령");

            Assert.Equal(1, runs);
            if (throws)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => running);
                Assert.Equal("명령 예외", error.Message);
            }
            else
            {
                Assert.Equal(1L, await running);
            }
            Assert.Equal(1, host.Tick);
            Assert.Equal(0, queuedRuns);
            Assert.All(queued, q => Assert.True(q.IsCanceled, $"대기 명령 상태: {q.Status}"));
        }
        finally
        {
            release.Set();
            dispose ??= Task.Run(host.Dispose);
            Assert.True(dispose.Wait(Timeout));
        }
    }

    /// <summary>
    /// 테스트 C-1 — 여러 스레드가 동시에 명령을 넣는 중 Dispose: 접수된 명령은 모두 끝나고, 실행된 콜백 수 = 성공 Task 수,
    /// Dispose 반환 후에는 어떤 콜백도 실행되지 않는다. 접수 거부는 ObjectDisposedException뿐이다.
    /// </summary>
    [Fact]
    public async Task ConcurrentEnqueueDuringDispose_EveryAcceptedTaskTerminates_AndNoCallbackRunsAfterDispose()
    {
        for (int round = 0; round < 20; round++)
        {
            var host = NewHost();
            int callbacks = 0;
            const int producers = 4, perProducer = 50;
            var accepted = new System.Collections.Concurrent.ConcurrentBag<Task<int>>();
            using var start = new Barrier(producers + 1);
            var threads = Enumerable.Range(0, producers).Select(_ => Task.Run(() =>
            {
                start.SignalAndWait(Timeout);
                for (int i = 0; i < perProducer; i++)
                {
                    try { accepted.Add(host.RunExclusiveAsync(_ => Interlocked.Increment(ref callbacks))); }
                    catch (ObjectDisposedException) { }
                }
            })).ToArray();

            start.SignalAndWait(Timeout);
            host.Dispose();
            int callbacksAtDispose = Volatile.Read(ref callbacks);
            await AssertTerminates(Task.WhenAll(threads), "생산자");

            foreach (var task in accepted) await AssertTerminates(task, "접수된 명령");
            Assert.Equal(callbacksAtDispose, Volatile.Read(ref callbacks));
            Assert.Equal(accepted.Count(t => t.IsCompletedSuccessfully), callbacksAtDispose);
            Assert.All(accepted, t => Assert.True(t.IsCompletedSuccessfully || t.IsCanceled, $"상태: {t.Status}"));
        }
    }

    /// <summary>테스트 C-2 — 계산 스레드 자신이 명령 안에서 Dispose: self-join 없이 끝나고, 뒤 명령은 취소, 중복 Dispose와 새 명령 거부.</summary>
    [Fact]
    public async Task DisposeFromWorkerCallback_DoesNotSelfJoin_AndCancelsTheRest()
    {
        var host = NewHost();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var blocker = host.RunExclusiveAsync(_ => { entered.Set(); release.Wait(Timeout); return 0; });
            Assert.True(entered.Wait(Timeout));
            var selfDispose = host.RunExclusiveAsync(_ => { host.Dispose(); return 1; });
            int laterRuns = 0;
            var later = host.RunExclusiveAsync(_ => Interlocked.Increment(ref laterRuns));
            release.Set();

            await AssertTerminates(selfDispose, "명령 안 Dispose");
            await AssertTerminates(later, "뒤 명령");
            Assert.Equal(1, await selfDispose);
            Assert.True(later.IsCanceled);
            Assert.Equal(0, laterRuns);

            var second = Task.Run(host.Dispose);
            await AssertTerminates(second, "중복 Dispose");
            Assert.Throws<ObjectDisposedException>(() => { _ = host.StepAsync(); });
        }
        finally
        {
            release.Set();
            host.Dispose();
        }
    }

    /// <summary>테스트 C-3 — 계산 스레드 실패 직전에 꺼낼 수 있었던 명령도 실행되지 않고 실패로 끝나며 새 명령은 거부된다.</summary>
    [Fact]
    public async Task WorkerFailureAtDequeueBoundary_FailsPendingWithoutRunning()
    {
        var host = NewHost();
        using var entered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        try
        {
            var first = host.RunExclusiveAsync(_ => { entered.Set(); releaseFirst.Wait(Timeout); return 0; });
            Assert.True(entered.Wait(Timeout));
            int armed = 1;
            host.TestHook = point =>
            {
                if (point == "before-dequeue" && Interlocked.Exchange(ref armed, 0) == 1)
                    throw new InvalidOperationException("계산 스레드 시험 예외");
            };
            int calls = 0;
            var pending = host.RunExclusiveAsync(_ => Interlocked.Increment(ref calls));
            releaseFirst.Set();

            await AssertTerminates(pending, "대기 명령");
            Assert.Equal(0, await first);
            Assert.Equal(0, calls);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
            Assert.Equal("계산 스레드 시험 예외", error.InnerException?.Message);
            Assert.Throws<InvalidOperationException>(() => { _ = host.StepAsync(); });
        }
        finally
        {
            releaseFirst.Set();
            host.TestHook = null;
            host.Dispose();
        }
    }
}
