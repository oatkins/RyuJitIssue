namespace RyuJitIssue
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using BenchmarkDotNet.Attributes;
    using BenchmarkDotNet.Configs;
    using BenchmarkDotNet.Environments;
    using BenchmarkDotNet.Jobs;
    using BenchmarkDotNet.Running;

    public class Program
    {
        static void Main(string[] args)
        {
            // To see the issue in the debugger, uncomment the following two lines
            // and run a Release build on a machine where RyuJIT is the default
            // jit compiler.

            ////var p = new Program();
            ////p.AddRemoveItemsFromDictionary();

            var job = Job.Dry
                .WithEvaluateOverhead(false)
                .WithInvocationCount(1)
                .WithLaunchCount(1)
                .WithTargetCount(1)
                .WithUnrollFactor(1)
                .WithWarmupCount(0)
                .With(Platform.X64);

            var legacyJob = job.With(Jit.LegacyJit);
            var ryuJitJob = job.With(Jit.RyuJit);

            // Using BenchmarkRunner primarily to be able to compare compilers
            // easily. It's not really a "benchmark". We run two jobs, one of which
            // (LegacyJIT) completes fast, and the other (RyuJIT) fails.
            BenchmarkRunner.Run<Program>(
                ManualConfig.Create(DefaultConfig.Instance)
                    .With(legacyJob)
                    .With(ryuJitJob));
        }

        [Benchmark]
        public void AddRemoveItemsFromDictionary()
        {
            const int AddingThreadCount = 1;
            const int RemovingThreadCount = 10;

            // The ServiceStack ConcurrentDictionary that exhibits the problem.
            var dic = new ServiceStack.Net30.Collections.Concurrent.ConcurrentDictionary<object, object>();

            // A safe .NET object to track the keys currently in the dictionary.
            var keys = new System.Collections.Concurrent.ConcurrentBag<object>();

            var waitHandles = new List<WaitHandle>();

            int runningAddingThreads = AddingThreadCount;

            // Start adding items to the dictionary. We currently do this on a single
            // thread, which is enough to reproduce the problem.
            for (int i = 0; i < AddingThreadCount; i++)
            {
                var h = new AutoResetEvent(false);
                waitHandles.Add(h);

                var t = new Thread(
                    new ThreadStart(
                        () =>
                        {
                            for (int ct = 0; ct < 1000; ct++)
                            {
                                var key = new object();
                                dic.TryAdd(key, key);
                                keys.Add(key);
                            }

                            // When the number of adding threads running reaches zero,
                            // the removing threads will run only until the dictionary
                            // is empty.
                            Interlocked.Decrement(ref runningAddingThreads);

                            h.Set();
                        }));

                // This seems to help us to keep the controlling thread responsive
                // so that it can kill the crashed threads later.
                t.Priority = ThreadPriority.BelowNormal;
                t.IsBackground = true;

                t.Start();
            }

            // Use several threads to attempt to remove items from the dictionary
            // concurrently.
            for (int i = 0; i < RemovingThreadCount; i++)
            {
                var h = new AutoResetEvent(false);
                waitHandles.Add(h);

                var t = new Thread(
                    new ThreadStart(
                        () =>
                        {
                            while (runningAddingThreads > 0 || dic.Any())
                            {
                                object key;
                                object value;
                                if (keys.TryTake(out key))
                                {
                                    // This is the line that appears to hang the app when built
                                    // on RyuJIT.
                                    dic.TryRemove(key, out value);
                                }
                            }

                            h.Set();
                        }));

                t.Priority = ThreadPriority.BelowNormal;
                t.IsBackground = true;

                t.Start();
            }

            if (!WaitHandle.WaitAll(waitHandles.ToArray(), 10000))
            {
                throw new InvalidOperationException("Test did not complete within 10 seconds.");
            }
        }
    }
}
