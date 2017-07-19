namespace RyuJitIssue
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using BenchmarkDotNet.Attributes;
    using BenchmarkDotNet.Attributes.Jobs;
    using BenchmarkDotNet.Engines;
    using BenchmarkDotNet.Running;
    ////using System.Collections.Concurrent;
    using BenchmarkDotNet.Jobs;
    using BenchmarkDotNet.Environments;
    using BenchmarkDotNet.Configs;

    using ServiceStack.Net30.Collections.Concurrent;

    public class Program
    {
        static void Main(string[] args)
        {
            //AddRemoveItemsFromDictionary();
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

            BenchmarkRunner.Run<Program>(
                ManualConfig.Create(DefaultConfig.Instance)
                    .With(legacyJob)
                    .With(ryuJitJob));
        }

        [Benchmark]
        public static void AddRemoveItemsFromDictionary()
        {
            var dic = new ConcurrentDictionary<object, object>();
            var addedKeys = new System.Collections.Concurrent.ConcurrentStack<object>();

            var cts = new CancellationTokenSource();

            var adding = new Thread(
                new ThreadStart(() =>
                {
                    Parallel.For(
                        0,
                        100000,
                        _ =>
                        {
                            object key = new object();
                            if (dic.TryAdd(key, key))
                            {
                                addedKeys.Push(key);
                            }
                        });

                    cts.Cancel();
                }));

            adding.Start();
            var token = cts.Token;

            List<Thread> threads = new List<Thread>();
            for (int tt = 0; tt < 10; tt++)
            {
                var thread = new Thread(RemoveThings);
                thread.Start();

                threads.Add(thread);
            }

            // Wait for threads to complete.
            adding.Join(TimeSpan.FromMinutes(5));
            foreach (var t in threads)
            {
                t.Join(1000);
            }

            void RemoveThings()
            {
                while (addedKeys.Any() || !token.IsCancellationRequested)
                {
                    object pp;
                    if (addedKeys.TryPop(out pp))
                    {
                        object v;
                        dic.TryRemove(pp, out v);
                    }
                }
            }
        }
    }
}
