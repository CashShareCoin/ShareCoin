using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;

namespace ShareCash
{
    public static class BurstCoinMiner
    {
        private static readonly Dictionary<DriveInfo, ActionBlock<(DriveInfo diskInfo, ulong accountNumber, long numberOfNonces, long startingNonce, string plotDirectory, int numberOfThreads, long memoryGB, CancellationToken stoppingToken, TaskCompletionSource<object> plotCompletion)>> _plotQueue = new Dictionary<DriveInfo, ActionBlock<(DriveInfo diskInfo, ulong accountNumber, long numberOfNonces, long startingNonce, string plotDirectory, int numberOfThreads, long memoryGB, CancellationToken stoppingToken, TaskCompletionSource<object> plotCompletion)>>();

        private static ILogger _logger;

        public static void SetLogger(ILogger log)
        {
            _logger = log;
        }

        public static Task QueuePlotAsync(DriveInfo diskInfo, ulong accountNumber, long numberOfNonces, long startingNonce, string plotDirectory, int numberOfThreads, long memoryGb, CancellationToken stoppingToken)
        {
            if (!_plotQueue.ContainsKey(diskInfo))
            {
                _plotQueue.TryAdd(
                    diskInfo, 
                    new ActionBlock<(DriveInfo diskInfo, ulong accountNumber, long numberOfNonces, long startingNonce, string plotDirectory, int numberOfThreads, long memoryGB, CancellationToken stoppingToken, TaskCompletionSource<object> plotCompletion)>(
                        async args =>
                        {
                            var plot = PlotFileAsync(
                                args.diskInfo, 
                                args.accountNumber, 
                                args.numberOfNonces,
                                args.startingNonce, 
                                args.plotDirectory,
                                args.numberOfThreads,
                                args.memoryGB,
                                args.stoppingToken);

                            _ = plot.ContinueWith(t =>
                                {
                                    if (t.IsCanceled)
                                    {
                                        args.plotCompletion.SetCanceled();
                                    }
                                    else if (t.IsFaulted)
                                    {
                                        args.plotCompletion.SetException(t.Exception);
                                    }
                                    else
                                    {
                                        args.plotCompletion.SetResult(t);
                                    }
                                }, 
                                TaskContinuationOptions.RunContinuationsAsynchronously);
                            
                            await plot;
                        }));
            }

            var plotCompletion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            _plotQueue[diskInfo].Post((diskInfo, accountNumber, numberOfNonces, startingNonce, plotDirectory, numberOfThreads, memoryGb, stoppingToken, plotCompletion));

            return plotCompletion.Task;
        }

        public static void QueuePlot(DriveInfo diskInfo, ulong accountNumber, long numberOfNonces, long startingNonce, string plotDirectory, int numberOfThreads, long memoryGb, CancellationToken stoppingToken)
        {
            _ = QueuePlotAsync(diskInfo, accountNumber, numberOfNonces, startingNonce, plotDirectory, numberOfThreads, memoryGb, stoppingToken);
        }

        private static string GetXplotterAppName() => Avx2.IsSupported ? "XPlotter_avx2" : Avx.IsSupported ? "XPlotter_avx" : "XPlotter_sse";

        private static async Task PlotFileAsync(DriveInfo diskInfo, ulong accountNumber, long numberOfNonces, long startingNonce, string plotDirectory, int numberOfThreads, long memoryGb, CancellationToken stopPlottingToken)
        {
            var args = $@"-id {accountNumber} -sn {startingNonce} -n {numberOfNonces} -t {numberOfThreads} -path {plotDirectory} -mem {memoryGb}G";

            _logger.LogInformation($"Plotting with arguments: {args}");

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = $@"{Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)}\chocolatey\lib\xplotter\{GetXplotterAppName()}.exe",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            if (proc.Start())
            {
                stopPlottingToken.Register(() => { try { proc.Kill(); } catch (Exception) { } });

                proc.PriorityClass = ProcessPriorityClass.Idle;

                var logDelayTask = Task.Delay(10 * 1000, stopPlottingToken);

                string standardOutput;
                while ((standardOutput = await proc.StandardOutput.ReadLineAsync()) != null)
                {
                    if (logDelayTask.IsCompleted)
                    {
                        _logger.LogInformation($"{diskInfo.Name} SN: {startingNonce}, {standardOutput}");
                        logDelayTask = Task.Delay(10 * 1000, stopPlottingToken);
                    }
                }

                await Task.Run(() => proc.WaitForExit(), stopPlottingToken);
            }
        }
    }
}
