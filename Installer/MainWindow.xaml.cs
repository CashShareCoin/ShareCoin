using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Path = System.IO.Path;

namespace ShareCoin
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly StreamWriter _log;

        public MainWindow()
        {
            InitializeComponent();

            _log = new StreamWriter(Path.Combine(Path.GetTempPath(), "ShareCashInstall.txt"), true) { AutoFlush = true };
        }

        private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
        {
            // ensure running as admin

            // install Chocolatey
            await this.OutputUpdateAsync("Installing Chocolatey...");
            
            var resourceDirectory = Path.GetTempPath();

            await this.OutputUpdateAsync($"Package directory: {resourceDirectory}");

            var installScriptPath = Path.Combine(resourceDirectory, "installChocolatey.bat");
            var psScriptPath = Path.Combine(resourceDirectory, "install.ps1");
            var script = Installer.Resource1.installChocolatey.Replace(@".\install.ps1", $@"""{psScriptPath}""");
            var shareCashPackagePath = Path.Combine(resourceDirectory, $"{nameof(Installer.Resource1.sharecash_0_2_5).Replace('_', '.')}.nupkg");

            File.WriteAllText(installScriptPath, script);
            File.WriteAllText(psScriptPath, Installer.Resource1.install);
            File.WriteAllBytes(shareCashPackagePath, Installer.Resource1.sharecash_0_2_5);

            using var chocolateyInstallProc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {installScriptPath}",
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                }
            };

            chocolateyInstallProc.Start();

            string chocoInstallOutput;

            while ((chocoInstallOutput = await chocolateyInstallProc.StandardOutput.ReadLineAsync()) != null)
            {
                await this.OutputUpdateAsync(chocoInstallOutput);
            }

            await Task.Run(() => chocolateyInstallProc.WaitForExit());

            await this.OutputUpdateAsync($"Installed Chocolatey. {Environment.NewLine} Installing ShareCash...");

            using var shareCoinInstallProc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = $@"{Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)}\chocolatey\choco.exe",
                    Arguments = $@"install -y ShareCash -s ""{resourceDirectory};https://chocolatey.org/api/v2""",
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            shareCoinInstallProc.Start();

            await this.OutputUpdateAsync($"Chocolatey process id: {shareCoinInstallProc.Id}");

            var shareCoinInstallTask = Task.Run(() => shareCoinInstallProc.WaitForExit());

            while (!shareCoinInstallTask.IsCompleted)
            {
                var readLineTask = shareCoinInstallProc.StandardOutput.ReadLineAsync();

                if (readLineTask == await Task.WhenAny(shareCoinInstallTask, readLineTask))
                {
                    await this.OutputUpdateAsync(readLineTask.Result);
                }
                else
                {
                    break;
                }
            }
           
            await this.OutputUpdateAsync("Installed ShareCash");

            Application.Current.Shutdown();
        }

        private async Task OutputUpdateAsync(string output)
        {
            await this.textBox.Dispatcher.InvokeAsync(() => 
            {
                this.textBox.Text += Environment.NewLine + output;
                this.textBox.ScrollToEnd();
            });

            await _log.WriteLineAsync(output);
        }

        private async void Window_Unloaded(object sender, RoutedEventArgs e)
        {
            await _log.DisposeAsync();
        }
    }
}
