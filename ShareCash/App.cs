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
using System.ComponentModel.DataAnnotations;
using System.Management;
using System.Diagnostics;
using System.Reflection.Metadata;

namespace ShareCash
{
    public static class App
    {
        public const int DiskCapacityCheckInterval = 10 * 1000;
        private const string AppDirectoryName = "ShareCash";
        private const long TargetPoolCapacity = 150L * 1024 * 1024 * 1024 * 1024 * 1024;
        private const long AverageMinerCapacity = 60L * 1024 * 1024 * 1024;
        private const int NonceSize = 262144;
        private const int PlotFileNameStartingNonceIndex = 1;
        private const int PlotFileNameNumberOfNoncesIndex = 2;
        private const ulong ShareCashAccountNumber = 12209047155150467438;

        private static readonly string MineConfigFile = Path.GetTempFileName();

        private static ILogger _logger;
        private static float _currentCpuUtilization;

        public static void SetLogger(ILogger log)
        {
            _logger = log;

            BurstCoinMiner.SetLogger(log);
        }

        private static async Task RunCpuMonitorAsync(CancellationToken stoppingToken)
        {
            var cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");

            while (!stoppingToken.IsCancellationRequested)
            {
                _currentCpuUtilization = cpuCounter.NextValue();

                await Task.Delay(1000, stoppingToken);
            }
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
            const int MaxDiskQueueLengthWhilePlotting = 2;

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
                    try
                    {
                        var diskPlot = Process.GetProcesses()
                            .FirstOrDefault(proc => proc.ProcessName == BurstCoinMiner.GetXplotterAppName() && proc.GetCommandLine().Contains(GetIncompletePlotsDirectory(disk)));

                        // if plotting & over max queue length then report high disk activity
                        if (diskPlot != null && diskQueueLength.RawValue > MaxDiskQueueLengthWhilePlotting)
                        {
                            _logger.LogDebug($"*************************{disk} - high disk activity detected");
                            bus.Publish(new HighDiskActivityNotification(disk));
                        }
                        // if not plotting & no disk activity for a certain time
                        else if (diskPlot == null && await WaitForNoDiskActivityAsync(60 * 1000, diskQueueLength))
                        {
                            bus.Publish(new LowDiskActivityNotification(disk));
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "error");
                    }
                }

                await Task.Delay(1000, stoppingToken);
            }            
        }

        private static async Task<bool> WaitForNoDiskActivityAsync(int durationOfNoDiskActivity, PerformanceCounter diskQueueLength)
        {
            var cancelDueToTimeOut = new CancellationTokenSource();
            var timeout = Task.Delay(durationOfNoDiskActivity);
            var diskInactiveTask = Task.Run(async () =>
            {
                while (diskQueueLength.RawValue == 0)
                {
                    await Task.Delay(1000);
                }
            },
            cancelDueToTimeOut.Token);

            _ = timeout.ContinueWith(previousTask =>
            {
                cancelDueToTimeOut.Cancel();
                cancelDueToTimeOut.Dispose();
            });

            // the disk was inactive the whole time if it was cancelled before reading any disk activity
            return timeout == await Task.WhenAny(timeout, diskInactiveTask);
        }

        private static Task WaitForLowDiskActivityAsync(DriveInfo disk, EventAggregator bus, CancellationToken stoppingToken)
        {
            var tcs = new TaskCompletionSource<object>();

            void OnLowDiskActivity(LowDiskActivityNotification e)
            {
                bus.Unsubscribe<LowDiskActivityNotification>(OnLowDiskActivity);

                tcs.SetResult(null);
            }

            var cancellationRegistration = stoppingToken.Register(() =>
            {
                tcs.SetCanceled();
            });

            tcs.Task.ContinueWith(previousTask => cancellationRegistration.Dispose());

            bus.Subscribe<LowDiskActivityNotification>(OnLowDiskActivity, e => e.Disk == disk);

            return tcs.Task;
        }

        private static string GetCommandLine(this Process process)
        {
            using var searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + process.Id);
            using var objects = searcher.Get();
           
            return objects.Cast<ManagementBaseObject>().SingleOrDefault()?["CommandLine"]?.ToString() ?? string.Empty;
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

            void OnInsufficientPlotSpace(InsufficientPlotSpaceNotification e)
            {
                plotSpaceAvailable = false;

                PublishDiskAvailableForPlotting();
            }

            bus.Subscribe<HighDiskActivityNotification>(OnHighDiskUtilization, e => e.Disk == disk);
            bus.Subscribe<LowDiskActivityNotification>(OnLowDiskUtilization, e => e.Disk == disk);
            bus.GetEvent<PubSubEvent<AvailablePlotSpaceNotification>>().Subscribe(OnPlotSpaceAvailable, ThreadOption.PublisherThread, true, e => e.Disk == disk);
            bus.GetEvent<PubSubEvent<InsufficientPlotSpaceNotification>>().Subscribe(OnInsufficientPlotSpace, ThreadOption.PublisherThread, true, e => e.Disk == disk);
        }

        private static void RunDriveUnavailableForMiningMonitor(EventAggregator bus)
        {
            void OnInsufficientPlotSpace(InsufficientPlotSpaceNotification e)
            {
                bus.GetEvent<PubSubEvent<MiningUnavailableForDriveNotification>>().Publish(new MiningUnavailableForDriveNotification(e.Disk));
            }

            bus.GetEvent<PubSubEvent<InsufficientPlotSpaceNotification>>().Subscribe(OnInsufficientPlotSpace, true);
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

            void OnCompletedPlotFile(CompletedPlotFileNotification e)
            {
                lock(sync)
                {
                    disksToMine = disksToMine.Union(new[] { e.Disk }).ToArray();
                }

                _logger.LogDebug($"mining disks: {disksToMine.Select(d => d.Name).Aggregate((disks, next) => $"{disks}, {next}")}");

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

            bus.GetEvent<PubSubEvent<CompletedPlotFileNotification>>().Subscribe(OnCompletedPlotFile, true);
            bus.GetEvent<PubSubEvent<MiningUnavailableForDriveNotification>>().Subscribe(OnMiningUnavailableForDrive, true);
        }

        private static CancellationToken GetPlotCancellationToken(DriveInfo disk, EventAggregator bus, CancellationToken stoppingToken)
        {
            var cancelDueToInvalidPlottingState = new CancellationTokenSource();
            var cancelPlotting = CancellationTokenSource.CreateLinkedTokenSource(cancelDueToInvalidPlottingState.Token, stoppingToken);

            void DestroyCancellationMonitor()
            {
                _logger.LogDebug($"********{disk} - {nameof(DestroyCancellationMonitor)} - {cancelPlotting.GetHashCode()}");
                
                bus.GetEvent<PubSubEvent<CompletedPlotFileNotification>>().Unsubscribe(OnCompletedPlotFile);
                bus.GetEvent<PubSubEvent<HighDiskActivityNotification>>().Unsubscribe(OnHighDiskUtilization);
                bus.GetEvent<PubSubEvent<InsufficientPlotSpaceNotification>>().Unsubscribe(OnInsufficientPlotSpace);

                cancelDueToInvalidPlottingState.Dispose();
                cancelPlotting.Dispose();
            }

            void OnHighDiskUtilization(HighDiskActivityNotification e)
            {
                _logger.LogDebug($"********{e.Disk} - cancelling plot due to high disk activity");

                bus.GetEvent<PubSubEvent<HighDiskActivityNotification>>().Unsubscribe(OnHighDiskUtilization);

                cancelDueToInvalidPlottingState.Cancel();
            }

            void OnInsufficientPlotSpace(InsufficientPlotSpaceNotification e)
            {
                _logger.LogDebug($"********{e.Disk} - cancelling plot due to insufficient plot space");

                bus.GetEvent<PubSubEvent<InsufficientPlotSpaceNotification>>().Unsubscribe(OnInsufficientPlotSpace);

                cancelDueToInvalidPlottingState.Cancel();
            }

            void OnCompletedPlotFile(CompletedPlotFileNotification e)
            {
                DestroyCancellationMonitor();
            }

            bus.Subscribe<HighDiskActivityNotification>(OnHighDiskUtilization, e => e.Disk == disk);
            bus.GetEvent<PubSubEvent<InsufficientPlotSpaceNotification>>().Subscribe(OnInsufficientPlotSpace, ThreadOption.PublisherThread, true, e => e.Disk == disk);
            bus.GetEvent<PubSubEvent<CompletedPlotFileNotification>>().Subscribe(OnCompletedPlotFile, ThreadOption.PublisherThread, true, e => e.Disk == disk);

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

        public static void Run(CancellationToken stoppingToken)
        {
            var bus = new EventAggregator();
            var fixedDrives = DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed).ToArray();

             try
             {
                 _ = RunCpuMonitorAsync(stoppingToken);
                RunAvailablePlotSpaceMonitor(fixedDrives, bus, stoppingToken);
                RunInsufficientPlotSpaceMonitor(fixedDrives, bus, stoppingToken);
                _ = RunHighDiskUtilizationMonitorAsync(fixedDrives, bus, stoppingToken);
                RunDriveUnavailableForMiningMonitor(bus);
                RunMineRestartMonitor(bus);

                async void OnPlotSpaceAvailable(AvailablePlotSpaceNotification e)
                {
                    bus.Unsubscribe<AvailablePlotSpaceNotification>(OnPlotSpaceAvailable);

                    await EnsureDiskPlotAsync(e.Disk, bus, stoppingToken);

                    bus.Subscribe<AvailablePlotSpaceNotification>(OnPlotSpaceAvailable, ThreadOption.BackgroundThread, newNotification => newNotification.Disk == e.Disk);
                }

                async void OnInsufficientDiskSpace(InsufficientPlotSpaceNotification e)
                {
                    // delete plot files to free up minimum required free space
                    await DeletePlotFilesUntilFreeSpaceAboveMinimumAsync(e.Disk);

                    // restart mining if any plot files left
                    if (Directory.GetFiles(GetPlotDirectory(e.Disk)).Any())
                    {
                        bus.Publish(new CompletedPlotFileNotification(e.Disk));
                    }
                }

                bus.Subscribe<InsufficientPlotSpaceNotification>(OnInsufficientDiskSpace, ThreadOption.BackgroundThread);

                StartMonitoredMining(bus, stoppingToken);

                // plot each drive in case the plot did not complete
                foreach (var fixedDrive in fixedDrives)
                {
                    _ = Task.Run(async () =>
                    {
                        await EnsureDiskPlotAsync(fixedDrive, bus, stoppingToken);

                        RunPlotAvailabilityMonitor(fixedDrive, bus);

                        bus.Subscribe<AvailablePlotSpaceNotification>(OnPlotSpaceAvailable, ThreadOption.BackgroundThread, e => e.Disk == fixedDrive);
                    });
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error running ShareCash");
            }
        }

        private static async Task EnsureDiskPlotAsync(DriveInfo disk, EventAggregator bus, CancellationToken stoppingToken)
        {
            Task plotTask = null;

            do
            {
                try
                {
                    var plotDirectory = GetPlotDirectory(disk);
                    if (!Directory.Exists(plotDirectory))
                    {
                        Directory.CreateDirectory(plotDirectory);
                    }

                    // create plot cache directory if it does not exist
                    var plotCacheDirectory = GetIncompletePlotsDirectory(disk);
                    if (!Directory.Exists(plotCacheDirectory))
                    {
                        Directory.CreateDirectory(plotCacheDirectory);
                    }

                    _logger.LogDebug($"{nameof(EnsureDiskPlotAsync)} - {disk.Name}: starting initial plot...");
                    plotTask = PlotDiskAsync(disk, bus, stoppingToken);
                    await plotTask;
                }
                catch (TaskCanceledException) { }
                catch (Exception e) 
                {
                    _logger.LogError(e, $"Error during ensured plot:  {disk.Name}");
                }
            }
            while (!stoppingToken.IsCancellationRequested && (plotTask == null || !plotTask.IsCompletedSuccessfully));

            _logger.LogDebug($"{nameof(EnsureDiskPlotAsync)} - {disk.Name}: ensured plot done.");
        }

        private static async Task<DriveInfo> PlotDiskAsync(DriveInfo disk, EventAggregator bus, CancellationToken stoppingToken)
        {
            _logger.LogDebug($"***************{nameof(PlotDiskAsync)} - {disk.Name} - {Environment.StackTrace}");

            var completedPlotFileNames = Directory.GetFiles(GetPlotDirectory(disk)).Select(completedPlotFilePath => Path.GetFileName(completedPlotFilePath)).ToArray();
            var incompletePlotFileNames = Directory.GetFiles(GetIncompletePlotsDirectory(disk)).Select(incompletePlotFilePath => Path.GetFileName(incompletePlotFilePath));
            var duplicatePlotFileNames = completedPlotFileNames.Intersect(incompletePlotFileNames);
            var unfinishedPlotFiles =
                Directory.GetFiles(GetIncompletePlotsDirectory(disk))
                    .OrderBy(plotFile => long.Parse(plotFile.Split('_')[PlotFileNameStartingNonceIndex]));

            // Delete duplicate plots
            foreach (var duplicatePlotFileName in duplicatePlotFileNames)
            {
                File.Delete(Path.Combine(GetPlotDirectory(disk), duplicatePlotFileName));    
            }

            // notify of completed plots
            if (completedPlotFileNames.Any())
            {
                bus.Publish(new CompletedPlotFileNotification(disk));
            }

            // wait for low disk activity
            _logger.LogDebug($"***************{nameof(PlotDiskAsync)} - {disk.Name} - waiting for low disk activity");
            await WaitForLowDiskActivityAsync(disk, bus, stoppingToken);
            _logger.LogDebug($"***************{nameof(PlotDiskAsync)} - {disk.Name} - notified of low disk activity");

            // complete unfinished plots
            foreach (var unfinishedPlotFile in unfinishedPlotFiles)
            {
                await ReplotFileAsync(disk, unfinishedPlotFile, bus, stoppingToken);
            }

            // plot big files
            await PlotFilesOfSizeAsync(disk, GetBigPlotSize(disk), bus, stoppingToken);

            // plot small files
            await PlotFilesOfSizeAsync(disk, GetSmallPlotSize(disk), bus, stoppingToken);

            return disk;
        }

        private static long GetBigPlotSize(DriveInfo disk) => disk.TotalSize / 10;

        public static long GetSmallPlotSize(DriveInfo disk) => disk.TotalSize / 100;

        public static long GetMinFreeDiskSpace(DriveInfo disk) => disk.TotalSize * 15/100;

        private static async Task ReplotFileAsync(DriveInfo diskToPlot, string plotFile, EventAggregator bus, CancellationToken stopPlottingToken)
        {
            var numberOfNonces = long.Parse(plotFile.Split('_')[PlotFileNameNumberOfNoncesIndex]);
            var startingNonce = long.Parse(plotFile.Split('_')[PlotFileNameStartingNonceIndex]);
            var memoryGb = Math.Max(Process.GetProcesses().Max(process => process.PeakWorkingSet64) / 1024 / 1024 / 1024, 1);

            var expectedFreeDiskSpace = diskToPlot.AvailableFreeSpace - (numberOfNonces * NonceSize - new FileInfo(plotFile).Length);

            // delete file if it will causes us to go under the minimum free disk space
            if (expectedFreeDiskSpace <= GetMinFreeDiskSpace(diskToPlot))
            {
                File.Delete(plotFile);
            }
            else
            {
                var inProgressPlotFile = Path.Combine(GetIncompletePlotsDirectory(diskToPlot), Path.GetFileName(plotFile));
                var completedPlotFile = Path.Combine(GetPlotDirectory(diskToPlot), Path.GetFileName(plotFile));

                // move file to plot cache directory
                File.Move(plotFile, inProgressPlotFile);

                // plot
                await BurstCoinMiner.QueuePlotAsync(
                    diskToPlot,
                    ShareCashAccountNumber,
                    numberOfNonces,
                    startingNonce,
                    GetIncompletePlotsDirectory(diskToPlot),
                    GetNumberOfThreadsPerPlot(),
                    memoryGb,
                    GetPlotCancellationToken(diskToPlot, bus, stopPlottingToken));

                // move plotted file to plot directory
                try
                {
                    File.Move(inProgressPlotFile, completedPlotFile);
                }
                catch (Exception e)
                {
                    throw;
                }

                // notify of plot completion
                bus.Publish(new CompletedPlotFileNotification(diskToPlot));
            }
        }

        private static async Task PlotFilesOfSizeAsync(DriveInfo diskToPlot, long plotFileSize, EventAggregator bus, CancellationToken stopPlottingToken)
        {
            var expectedFreeDiskSpace = diskToPlot.AvailableFreeSpace - plotFileSize;

            // plot until we exceed the minimum disk space limit
            while (!stopPlottingToken.IsCancellationRequested && expectedFreeDiskSpace > GetMinFreeDiskSpace(diskToPlot))
            {
                var nextStartingNonce = GetLastCompletedNonce(diskToPlot) + 1 ?? GetRandomStartingNonce();
                var memoryGb = Math.Max(Process.GetProcesses().Max(process => process.PeakWorkingSet64) / 1024 / 1024 / 1024, 1);
                var numberOfNonces = plotFileSize / NonceSize;
                var plotFileName = $"{ShareCashAccountNumber}_{nextStartingNonce}_{numberOfNonces}";

                await BurstCoinMiner.QueuePlotAsync(
                    diskToPlot,
                    ShareCashAccountNumber,
                    numberOfNonces,
                    nextStartingNonce,
                    GetIncompletePlotsDirectory(diskToPlot),
                    GetNumberOfThreadsPerPlot(),
                    memoryGb,
                    GetPlotCancellationToken(diskToPlot, bus, stopPlottingToken));

                // move plotted file to plot directory
                try
                {
                    File.Move(Path.Combine(GetIncompletePlotsDirectory(diskToPlot), plotFileName), Path.Combine(GetPlotDirectory(diskToPlot), plotFileName));
                }
                catch (Exception e)
                {
                    throw;
                }

                // notify of plot completion
                bus.Publish(new CompletedPlotFileNotification(diskToPlot));

                expectedFreeDiskSpace = diskToPlot.AvailableFreeSpace - plotFileSize;
            }
        }

        private static async Task MineAsync(DriveInfo[] drivesToMine, CancellationToken cancelMiningToken)
        {
            var baseConfig = Resource1.config_base;
            var yaml = new YamlStream();
            using var reader = new StringReader(baseConfig);
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

            await using (var writer = new StreamWriter(MineConfigFile))
            {
                serializer.Serialize(writer, yaml.Documents[0].RootNode);
            }

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = $@"{Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)}\chocolatey\lib\scavenger\scavenger.exe",
                    Arguments = $"--config {MineConfigFile}",
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            _logger.LogInformation($"Starting mine with config '{MineConfigFile}'");

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

        private static int GetNumberOfThreadsPerPlot() => Math.Max(1, (int)((1.0f - _currentCpuUtilization/100.0f) * Environment.ProcessorCount));

        private static string GetPlotDirectory(DriveInfo diskInfo) => $@"{diskInfo.Name}\{AppDirectoryName}\plots";

        private static string GetIncompletePlotsDirectory(DriveInfo diskInfo) => $@"{diskInfo.Name}\{AppDirectoryName}\IncompletePlots";

        private static long GetRandomStartingNonce() => new Random().Next(0, (int) (TargetPoolCapacity / AverageMinerCapacity));

        private static long? GetLastCompletedNonce(DriveInfo diskInfo)
        {
            long? lastCompletedNonce = null;

            var newestPlotFile = GetNewestPlotFile(GetPlotDirectory(diskInfo));
            
            if(newestPlotFile != null)
            {
                var newestPlotFileComponents = newestPlotFile.Split('_');
                lastCompletedNonce = long.Parse(newestPlotFileComponents[PlotFileNameStartingNonceIndex]) + long.Parse(newestPlotFileComponents[PlotFileNameNumberOfNoncesIndex]) - 1;
            }

            return lastCompletedNonce;
        }
        
        private static string GetNewestPlotFile(string directoryPath)
        {
            string newestPlotFile = null;
            
            var plotFiles = Directory.GetFiles(directoryPath);

            if (plotFiles.Any())
            {
                var largestStartingNonce = plotFiles.Max(plotFile => long.Parse(plotFile.Split('_')[PlotFileNameStartingNonceIndex])).ToString();
                newestPlotFile = plotFiles.FirstOrDefault(plotFile => plotFile.Contains($"_{largestStartingNonce}_"));
            }

            return newestPlotFile;
        }

        private static async Task DeletePlotFilesUntilFreeSpaceAboveMinimumAsync(DriveInfo diskInfo)
        {
            string newestPlotFile;

            while (diskInfo.AvailableFreeSpace < GetMinFreeDiskSpace(diskInfo) && (newestPlotFile = (GetNewestPlotFile(GetIncompletePlotsDirectory(diskInfo)) ?? GetNewestPlotFile(GetPlotDirectory(diskInfo)))) != null)
            {
                try
                {
                    File.Delete(newestPlotFile);
                    await Task.Delay(5000);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to delete plot file.");
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

        private static void Subscribe<TPayload>(this EventAggregator bus, Action<TPayload> handler, ThreadOption threadOption)
        {
            bus.GetEvent<PubSubEvent<TPayload>>().Subscribe(handler, threadOption, true);
        }

        private static SubscriptionToken Subscribe<TPayload>(this EventAggregator bus, Action<TPayload> handler, Predicate<TPayload> filter)
        {
            return bus.Subscribe(handler, ThreadOption.PublisherThread, filter);
        }

        private static SubscriptionToken Subscribe<TPayload>(this EventAggregator bus, Action<TPayload> handler, ThreadOption threadOption, Predicate<TPayload> filter)
        {
            return bus.GetEvent<PubSubEvent<TPayload>>().Subscribe(handler, threadOption, true, filter);
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

        private class CompletedPlotFileNotification
        {
            public CompletedPlotFileNotification(DriveInfo disk)
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
