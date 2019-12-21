using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Prism.Events;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using ShareCash.Core;
using System.IO.Pipes;
using System.IO.MemoryMappedFiles;
using System.Runtime.Intrinsics.X86;
using System.Collections.Concurrent;
using System.Management;
using System.Diagnostics;

namespace ShareCash
{
    public static class App
    {
        public const int DiskCapacityCheckInterval = 10 * 1000;
        private const string AppDirectoryName = "ShareCash";
        private const long TargetPoolCapacity = 150L * 1024 * 1024 * 1024 * 1024 * 1024;
        private const long AverageIndividualCapacity = 60L * 1024 * 1024 * 1024;
        private const int NonceSize = 262144;
        private const int PlotFileNameNonceIndex = 1;
        private const int PlotFileNameNumberOfNoncesIndex = 2;
        private const ulong ShareCashAccountNumber = 12209047155150467438;

        private static ILogger _logger;

        public static void SetLogger(ILogger log)
        {
            _logger = log;

            BurstCoinMiner.SetLogger(log);
        }

        private static void RunAvailablePlotSpaceMonitor(DriveInfo[] disks, EventAggregator bus, CancellationToken stoppingToken)
        {
            _ = Task.Run(async () =>
            {
                // keep checking until free disk space goes above the size of a small plot file plus the minimum free disk space 
                while (!stoppingToken.IsCancellationRequested)
                {
                    foreach (var disk in disks)
                    {
                        if (disk.AvailableFreeSpace - GetSmallPlotSize(disk) >= GetMinFreeDiskSpace(disk))
                        {
                            bus.GetEvent<PubSubEvent<AvailablePlotSpaceNotification>>().Publish(new AvailablePlotSpaceNotification(disk));
                        }
                    }

                    await Task.Delay(DiskCapacityCheckInterval, stoppingToken);
                }
            });
        }

        private static void RunInsufficientPlotSpaceMonitor(DriveInfo[] disks, EventAggregator bus, CancellationToken stoppingToken)
        {
            _ = Task.Run(async () =>
            {
                // keep checking until free disk space goes above the size of a small plot file plus the minimum free disk space 
                while (!stoppingToken.IsCancellationRequested)
                {
                    foreach (var disk in disks)
                    {
                        if (disk.AvailableFreeSpace < GetMinFreeDiskSpace(disk))
                        {
                            bus.GetEvent<PubSubEvent<InsufficientPlotSpaceNotification>>().Publish(new InsufficientPlotSpaceNotification(disk));
                        }
                    }

                    await Task.Delay(DiskCapacityCheckInterval, stoppingToken);
                }
            }
            , stoppingToken);
        }

        private static async Task RunHighDiskUtilizationMonitorAsync(DriveInfo[] disks, EventAggregator bus, CancellationToken stoppingToken)
        {
            const int DriveInfoVolumeLetterIndex = 0;
            const int MaxDiskQueueLength = 1;

            var performanceCounterCategories = PerformanceCounterCategory.GetCategories().ToArray();
            var diskPerformance = performanceCounterCategories.FirstOrDefault(c => c.CategoryName == "PhysicalDisk");
            var diskQueueLengthResults = diskPerformance.GetInstanceNames()
                .Select(diskPerformanceInstanceName => diskPerformance.GetCounters(diskPerformanceInstanceName))
                .Select(diskPerformanceCounters => (diskQueueLength: diskPerformanceCounters.FirstOrDefault(c => c.CounterName == "Current Disk Queue Length"), disk: disks.FirstOrDefault(d => diskPerformanceCounters.First().InstanceName.Contains(d.Name[DriveInfoVolumeLetterIndex]))))
                .Where(diskQueueLengthResult => diskQueueLengthResult.diskQueueLength != null && diskQueueLengthResult.disk != null).ToArray();

            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var (diskQueueLength, disk) in diskQueueLengthResults)
                {
                    if (diskQueueLength.RawValue > MaxDiskQueueLength)
                    {
                        bus.Publish(new HighDiskActivityNotification(disk));
                    }
                    else
                    {
                        bus.Publish(new LowDiskActivityNotification(disk));
                    }
                }

                await Task.Delay(5000, stoppingToken);
            }            
        }

        private static void RunPlotAvailabilityMonitor(DriveInfo disk, EventAggregator bus)
        {
            var plotSpaceAvailable = false;
            var lowDiskActivity = false;

            void PublishDiskAvailableForPlotting()
            {
                if (lowDiskActivity && plotSpaceAvailable)
                {
                    bus.GetEvent<PubSubEvent<DiskAvailableForPlottingNotification>>().Publish(new DiskAvailableForPlottingNotification(disk));
                }
            }

            void OnHighDiskUtilization(HighDiskActivityNotification e)
            {
                lowDiskActivity = false;

                PublishDiskAvailableForPlotting();
            }

            void OnLowDiskUtilization(LowDiskActivityNotification e)
            {
                lowDiskActivity = true;

                PublishDiskAvailableForPlotting();
            }

            void OnPlotSpaceAvailable(AvailablePlotSpaceNotification e)
            {
                plotSpaceAvailable = true;

                PublishDiskAvailableForPlotting();
            }

            void OnInsufficentPlotSpace(InsufficientPlotSpaceNotification e)
            {
                plotSpaceAvailable = false;

                PublishDiskAvailableForPlotting();
            }

            bus.Subscribe<HighDiskActivityNotification>(OnHighDiskUtilization, e => e.Disk == disk);
            bus.Subscribe<LowDiskActivityNotification>(OnLowDiskUtilization, e => e.Disk == disk);
            bus.GetEvent<PubSubEvent<AvailablePlotSpaceNotification>>().Subscribe(OnPlotSpaceAvailable, ThreadOption.PublisherThread, true, e => e.Disk == disk);
            bus.GetEvent<PubSubEvent<InsufficientPlotSpaceNotification>>().Subscribe(OnInsufficentPlotSpace, ThreadOption.PublisherThread, true, e => e.Disk == disk);
        }

        private static void RunDriveUnavailableForMiningMonitor(EventAggregator bus)
        {
            void OnInsufficentPlotSpace(InsufficientPlotSpaceNotification e)
            {
                bus.GetEvent<PubSubEvent<MiningUnavailableForDriveNotification>>().Publish(new MiningUnavailableForDriveNotification(e.Disk));
            }

            void OnPlottingInProgress(PlottingInProgressNotfication e)
            {
                bus.GetEvent<PubSubEvent<MiningUnavailableForDriveNotification>>().Publish(new MiningUnavailableForDriveNotification(e.Disk));
            }

            bus.GetEvent<PubSubEvent<InsufficientPlotSpaceNotification>>().Subscribe(OnInsufficentPlotSpace, true);
            bus.GetEvent<PubSubEvent<PlottingInProgressNotfication>>().Subscribe(OnPlottingInProgress, true);
        }

        private static void StartMonitoredMining(EventAggregator bus, CancellationToken stoppingToken)
        {
            void OnMineRestart(RestartMiningNotification e)
            {
                if(e.DisksToMine.Any())
                {
                    _logger.LogInformation($"{nameof(OnMineRestart)}: {Environment.StackTrace.Split(Environment.NewLine).Where(line => line.Contains("ShareCash.App")).Aggregate((lines, nextLine) => $"{lines}{Environment.NewLine}{nextLine}")}");

                    _ = MineAsync(e.DisksToMine, GetMiningCancellationToken(bus, stoppingToken));
                }
            }

            bus.GetEvent<PubSubEvent<RestartMiningNotification>>().Subscribe(OnMineRestart, true);
        }

        private static void RunMineRestartMonitor(EventAggregator bus)
        {
            object sync = new object();

            var disksToMine = new DriveInfo[0];

            void OnPlottingCompleted(DiskPlotCompleteNotfication e)
            {
                lock(sync)
                {
                    disksToMine = disksToMine.Union(new[] { e.Disk }).ToArray();
                }

                bus.GetEvent<PubSubEvent<RestartMiningNotification>>().Publish(new RestartMiningNotification(disksToMine));
            }

            void OnMiningUnavailableForDrive(MiningUnavailableForDriveNotification e)
            {
                var restartMining = false;

                lock (sync)
                {
                    if(disksToMine.Contains(e.Disk))
                    {
                        restartMining = true;
                        disksToMine = disksToMine.Except(new[] { e.Disk }).ToArray();
                    }
                }

                if(restartMining)
                {
                    bus.GetEvent<PubSubEvent<RestartMiningNotification>>().Publish(new RestartMiningNotification(disksToMine));
                }
            }

            bus.GetEvent<PubSubEvent<DiskPlotCompleteNotfication>>().Subscribe(OnPlottingCompleted, true);
            bus.GetEvent<PubSubEvent<MiningUnavailableForDriveNotification>>().Subscribe(OnMiningUnavailableForDrive, true);
        }

        private static CancellationToken GetPlotCancellationToken(DriveInfo disk, EventAggregator bus, CancellationToken stoppingToken)
        {
            var cancelDueToInvalidPlottingState = new CancellationTokenSource();
            var cancelPlotting = CancellationTokenSource.CreateLinkedTokenSource(cancelDueToInvalidPlottingState.Token, stoppingToken);

            void DestroyCancellationMonitor()
            {
                bus.GetEvent<PubSubEvent<DiskPlotCompleteNotfication>>().Unsubscribe(OnDiskPlotCompleted);
                bus.GetEvent<PubSubEvent<HighDiskActivityNotification>>().Unsubscribe(OnHighDiskUtilization);
                bus.GetEvent<PubSubEvent<InsufficientPlotSpaceNotification>>().Unsubscribe(OnInsufficentPlotSpace);

                cancelDueToInvalidPlottingState.Dispose();
                cancelPlotting.Dispose();
            }

            void OnHighDiskUtilization(HighDiskActivityNotification e)
            {
                _logger.LogDebug("********cancelling plot due to high disk activity");

                bus.GetEvent<PubSubEvent<HighDiskActivityNotification>>().Unsubscribe(OnHighDiskUtilization);

                cancelDueToInvalidPlottingState.Cancel();
            }

            void OnInsufficentPlotSpace(InsufficientPlotSpaceNotification e)
            {
                _logger.LogDebug("********cancelling plot due to insufficent plot space");

                bus.GetEvent<PubSubEvent<InsufficientPlotSpaceNotification>>().Unsubscribe(OnInsufficentPlotSpace);

                cancelDueToInvalidPlottingState.Cancel();
            }

            void OnDiskPlotCompleted(DiskPlotCompleteNotfication e)
            {
                DestroyCancellationMonitor();
            }

            bus.Subscribe<HighDiskActivityNotification>(OnHighDiskUtilization, e => e.Disk == disk);
            bus.GetEvent<PubSubEvent<InsufficientPlotSpaceNotification>>().Subscribe(OnInsufficentPlotSpace, ThreadOption.PublisherThread, true, e => e.Disk == disk);
            bus.GetEvent<PubSubEvent<DiskPlotCompleteNotfication>>().Subscribe(OnDiskPlotCompleted, ThreadOption.PublisherThread, true, e => e.Disk == disk);

            cancelPlotting.Token.Register(() => DestroyCancellationMonitor());

            return cancelPlotting.Token;
        }

        private static CancellationToken GetMiningCancellationToken(EventAggregator bus, CancellationToken stoppingToken)
        {
            SubscriptionToken mineRestartSubToken = null;

            var cancelDueToMineRestart = new CancellationTokenSource();
            var cancelMining = CancellationTokenSource.CreateLinkedTokenSource(cancelDueToMineRestart.Token, stoppingToken);

            void OnMineRestart(RestartMiningNotification e)
            {
                if(mineRestartSubToken != null)
                {
                    mineRestartSubToken.Dispose();
                    mineRestartSubToken = null;

                    cancelDueToMineRestart.Cancel();
                    cancelDueToMineRestart.Dispose();

                    cancelMining.Dispose();
                }
            }

            mineRestartSubToken = bus.GetEvent<PubSubEvent<RestartMiningNotification>>().Subscribe(OnMineRestart, true);

            stoppingToken.Register(() => mineRestartSubToken?.Dispose());

            return cancelMining.Token;
        }

        private static void OnInsufficientDiskSpace(InsufficientPlotSpaceNotification e)
        {
           DeletePlotFilesUntilFreeSpaceAboveMinimum(e.Disk);
        }

        public static void Run(CancellationToken stoppingToken)
        {
            var bus = new EventAggregator();
            var fixedDrives = DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed).ToArray();

             try
            {
                RunAvailablePlotSpaceMonitor(fixedDrives, bus, stoppingToken);
                RunInsufficientPlotSpaceMonitor(fixedDrives, bus, stoppingToken);
                _ = RunHighDiskUtilizationMonitorAsync(fixedDrives, bus, stoppingToken);
                RunDriveUnavailableForMiningMonitor(bus);
                RunMineRestartMonitor(bus);

                bus.GetEvent<PubSubEvent<InsufficientPlotSpaceNotification>>().Subscribe(OnInsufficientDiskSpace, ThreadOption.BackgroundThread);

                StartMonitoredMining(bus, stoppingToken);

                // plot each drive in case the plot did not complete
                foreach (var fixedDrive in fixedDrives)
                {
                    _ = Task.Run(async () => 
                    {
                        await InitialDiskPlotAsync(fixedDrive, bus, stoppingToken);

                        RunPlotAvailabilityMonitor(fixedDrive, bus);

                        bus.GetEvent<PubSubEvent<DiskAvailableForPlottingNotification>>().Subscribe(
                            e => _ = PlotDiskAsync(fixedDrive, bus, GetPlotCancellationToken(fixedDrive, bus, stoppingToken)),
                            ThreadOption.PublisherThread,
                            true,
                            e => e.Disk == fixedDrive);
                    });
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error running ShareCash");
            }
        }

        private static async Task InitialDiskPlotAsync(DriveInfo disk, EventAggregator bus, CancellationToken stoppingToken)
        {
            Task plotTask = null;

            do
            {
                try
                {
                    _logger.LogDebug($"{nameof(InitialDiskPlotAsync)} - {disk.Name}: waiting for low disk activity...");
                    await WaitForLowDiskActivityAsync(disk, bus);
                    _logger.LogDebug($"{nameof(InitialDiskPlotAsync)} - {disk.Name}: low disk activity.");

                    _logger.LogDebug($"{nameof(InitialDiskPlotAsync)} - {disk.Name}: starting initial plot...");
                    plotTask = PlotDiskAsync(disk, bus, GetPlotCancellationToken(disk, bus, stoppingToken));
                    await plotTask;
                }
                catch (TaskCanceledException) { }
                catch (Exception e) 
                {
                    _logger.LogError(e, $"Error during initial plot:  {disk.Name}");
                }
            }
            while (!stoppingToken.IsCancellationRequested && (plotTask == null || !plotTask.IsCompletedSuccessfully));

            _logger.LogDebug($"{nameof(InitialDiskPlotAsync)} - {disk.Name}: initial plot done.");
        }

        private static Task WaitForLowDiskActivityAsync(DriveInfo disk, EventAggregator bus)
        {
            var t = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnLowDiskActivity(LowDiskActivityNotification e)
            {
                bus.Unsubscribe<LowDiskActivityNotification>(OnLowDiskActivity);

                t.SetResult(null);
            }

            bus.Subscribe<LowDiskActivityNotification>(OnLowDiskActivity, e => e.Disk == disk);

            return t.Task;
        }

        private static async Task<DriveInfo> PlotDiskAsync(DriveInfo disk, EventAggregator bus, CancellationToken stoppingToken)
        {
            bus.GetEvent<PubSubEvent<PlottingInProgressNotfication>>().Publish(new PlottingInProgressNotfication(disk));

            // plot big files
            await PlotFilesOfSizeAsync(disk, GetBigPlotSize(disk), stoppingToken);

            // plot small files
            await PlotFilesOfSizeAsync(disk, GetSmallPlotSize(disk), stoppingToken);

            bus.GetEvent<PubSubEvent<DiskPlotCompleteNotfication>>().Publish(new DiskPlotCompleteNotfication(disk));

            return disk;
        }

        private static long GetBigPlotSize(DriveInfo disk) => disk.TotalSize / 10;

        public static long GetSmallPlotSize(DriveInfo disk) => disk.TotalSize / 100;

        public static long GetMinFreeDiskSpace(DriveInfo disk) => disk.TotalSize * 15/100;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="diskToPlot"></param>
        /// <param name="plotFileSize"></param>
        /// <param name="stopPlottingToken"></param>
        private static async Task PlotFilesOfSizeAsync(DriveInfo diskToPlot, long plotFileSize, CancellationToken stopPlottingToken)
        {
            var expectedFreeDiskSpace = diskToPlot.AvailableFreeSpace - plotFileSize;

            // create plot directory if it does not exist
            var plotDirectory = GetPlotDirectory(diskToPlot);
            if (!Directory.Exists(plotDirectory))
            {
                Directory.CreateDirectory(plotDirectory);
            }

            // re-plot existing plot files, in case any are corrupt or incomplete
            foreach (var existingPlotFile in Directory.GetFiles(plotDirectory))
            {
                var numberOfNonces = long.Parse(existingPlotFile.Split('_')[PlotFileNameNumberOfNoncesIndex]);
                var startingNonce = long.Parse(existingPlotFile.Split('_')[PlotFileNameNonceIndex]);
                var memoryGb = Math.Max(Process.GetProcesses().Max(process => process.PeakWorkingSet64) / 1024 / 1024 / 1024, 1);

                await BurstCoinMiner.QueuePlotAsync(
                    diskToPlot,
                    ShareCashAccountNumber,
                    numberOfNonces,
                    startingNonce,
                    GetPlotDirectory(diskToPlot),
                    Environment.ProcessorCount,
                    memoryGb,
                    stopPlottingToken);
            }

            // plot until we exceed the minimum disk space limit
            while (!stopPlottingToken.IsCancellationRequested && expectedFreeDiskSpace > GetMinFreeDiskSpace(diskToPlot))
            {
                var nextStartingNonce = GetLastCompletedNonce(diskToPlot) + 1 ?? GetRandomStartingNonce();
                var memoryGb = Math.Max(Process.GetProcesses().Max(process => process.PeakWorkingSet64) / 1024 / 1024 / 1024, 1);

                await BurstCoinMiner.QueuePlotAsync(
                    diskToPlot,
                    ShareCashAccountNumber,
                    plotFileSize / NonceSize,
                    nextStartingNonce,
                    GetPlotDirectory(diskToPlot),
                    Environment.ProcessorCount,
                    memoryGb,
                    stopPlottingToken);

                expectedFreeDiskSpace = diskToPlot.AvailableFreeSpace - plotFileSize;
            }
        }

        private static async Task MineAsync(DriveInfo[] drivesToMine, CancellationToken cancelMiningToken)
        {
            var baseConfig = Resource1.config_base;
            var yaml = new YamlStream();
            using var reader = new StringReader(baseConfig);
            var finalConfigPath = Path.GetTempFileName();
            var serializer = new Serializer();
            var plotPaths = drivesToMine.Select(GetPlotDirectory);

            yaml.Load(reader);

            var mapping = (YamlMappingNode)yaml.Documents[0].RootNode;
            var plotDirectories = (YamlSequenceNode)mapping.Children[new YamlScalarNode("plot_dirs")];

            plotDirectories.Children.Clear();
            foreach (var plotPath in plotPaths)
            {
                plotDirectories.Add(new YamlScalarNode(plotPath) { Style = ScalarStyle.SingleQuoted });
            }

            await using (var writer = new StreamWriter(finalConfigPath))
            {
                serializer.Serialize(writer, yaml.Documents[0].RootNode);
            }

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = $@"{Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)}\chocolatey\lib\scavenger\scavenger.exe",
                    Arguments = $"--config {finalConfigPath}",
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            _logger.LogInformation($"Starting mine with config '{finalConfigPath}'");

            _logger.LogDebug($"********************starting mining - {cancelMiningToken.GetHashCode()}");

            if (proc.Start())
            {
                _logger.LogDebug($"********************cancelled: {cancelMiningToken.IsCancellationRequested}, hash: {cancelMiningToken.GetHashCode()}");

                await using var _ = cancelMiningToken.Register(() => proc.Kill());

                var logDelayTask = Task.Delay(10 * 1000);

                string standardOutput;
                while ((standardOutput = await proc.StandardOutput.ReadLineAsync()) != null)
                {
                    if (logDelayTask.IsCompleted)
                    {
                        _logger.LogInformation(standardOutput);
                        logDelayTask = Task.Delay(10 * 1000);
                    }
                }

                await Task.Run(() => proc.WaitForExit(), cancelMiningToken);
            }
        }

        private static string GetPlotDirectory(DriveInfo diskInfo) => $@"{diskInfo.Name}\{AppDirectoryName}\plots";

        private static long GetRandomStartingNonce() => new Random().Next(0, (int) (TargetPoolCapacity / AverageIndividualCapacity));

        private static long? GetLastCompletedNonce(DriveInfo diskInfo)
        {
            long? lastCompletedNonce = null;

            var newestPlotFile = GetNewestPlotFile(diskInfo);
            
            if(newestPlotFile != null)
            {
                var newestPlotFileComponents = newestPlotFile.Split('_');
                lastCompletedNonce = long.Parse(newestPlotFileComponents[PlotFileNameNonceIndex]) + long.Parse(newestPlotFileComponents[PlotFileNameNumberOfNoncesIndex]) - 1;
            }

            return lastCompletedNonce;
        }
        
        private static string GetNewestPlotFile(DriveInfo diskInfo)
        {
            string newestPlotFile = null;
            
            var plotFiles = Directory.GetFiles(GetPlotDirectory(diskInfo));

            if (plotFiles.Any())
            {
                var largestStartingNonce = plotFiles.Max(plotFile => long.Parse(plotFile.Split('_')[PlotFileNameNonceIndex])).ToString();
                newestPlotFile = plotFiles.FirstOrDefault(plotFile => plotFile.Contains($"_{largestStartingNonce}_"));
            }

            return newestPlotFile;
        }

        private static void DeletePlotFilesUntilFreeSpaceAboveMinimum(DriveInfo diskInfo)
        {
            string newestPlotFile;

            while (diskInfo.AvailableFreeSpace < GetMinFreeDiskSpace(diskInfo) && (newestPlotFile = GetNewestPlotFile(diskInfo)) != null)
            {
                try
                {
                    File.Delete(newestPlotFile);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to delete pot file.");
                }
            }
        }

        private static void Publish<TPayload>(this EventAggregator bus, TPayload payload)
        {
            bus.GetEvent<PubSubEvent<TPayload>>().Publish(payload);
        }

        private static void Subscribe<TPayload>(this EventAggregator bus, Action<TPayload> handler)
        {
            bus.GetEvent<PubSubEvent<TPayload>>().Subscribe(handler, true);
        }

        private static void Subscribe<TPayload>(this EventAggregator bus, Action<TPayload> handler, Predicate<TPayload> filter)
        {
            bus.GetEvent<PubSubEvent<TPayload>>().Subscribe(handler, ThreadOption.PublisherThread, true, filter);
        }

        private static void Unsubscribe<TPayload>(this EventAggregator bus, Action<TPayload> handler)
        {
            bus.GetEvent<PubSubEvent<TPayload>>().Unsubscribe(handler);
        }

        private class InsufficientPlotSpaceNotification
        {
            public InsufficientPlotSpaceNotification(DriveInfo disk)
            {
                this.Disk = disk;
            }

            public DriveInfo Disk { get; }
        }

        private class AvailablePlotSpaceNotification
        {
            public AvailablePlotSpaceNotification(DriveInfo disk)
            {
                this.Disk = disk;
            }

            public DriveInfo Disk { get; }
        }

        private class DiskPlotCompleteNotfication
        {
            public DiskPlotCompleteNotfication(DriveInfo disk)
            {
                this.Disk = disk;
            }

            public DriveInfo Disk { get; }
        }

        private class PlottingInProgressNotfication
        {
            public PlottingInProgressNotfication(DriveInfo disk)
            {
                this.Disk = disk;
            }

            public DriveInfo Disk { get; }
        }

        private class HighDiskActivityNotification 
        {
            public HighDiskActivityNotification(DriveInfo disk)
            {
                this.Disk = disk;
            }

            public DriveInfo Disk { get; }
        }

        private class LowDiskActivityNotification
        {
            public LowDiskActivityNotification(DriveInfo disk)
            {
                this.Disk = disk;
            }

            public DriveInfo Disk { get; }
        }

        private class MiningUnavailableForDriveNotification
        {
            public MiningUnavailableForDriveNotification(DriveInfo disk)
            {
                this.Disk = disk;
            }

            public DriveInfo Disk { get; }
        }

        private class DiskAvailableForPlottingNotification
        {
            public DiskAvailableForPlottingNotification(DriveInfo disk)
            {
                this.Disk = disk;
            }

            public DriveInfo Disk { get; }
        }

        private class RestartMiningNotification
        {
            public RestartMiningNotification(DriveInfo[] disksToMine)
            {
                this.DisksToMine = disksToMine;
            }

            public DriveInfo[] DisksToMine { get; }
        }
    }
}
