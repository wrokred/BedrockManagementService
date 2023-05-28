﻿using BedrockService.Service.Server.Interfaces;
using BedrockService.Shared.PackParser;
using BedrockService.Shared.SerializeModels;
using NCrontab;
using System.IO.Compression;
using System.Timers;
using static BedrockService.Shared.Classes.SharedStringBase;

namespace BedrockService.Service.Server {
    public class BedrockServer : IBedrockServer {
        private Task? _serverTask;
        private Task? _watchdogTask;
        private CancellationTokenSource _serverCanceler = new();
        private CancellationTokenSource _watchdogCanceler = new();
        private StreamWriter _stdInStream;
        private Process? _serverProcess;
        private ServerStatus _currentServerStatus;
        private readonly IServerConfiguration _serverConfiguration;
        private readonly IServiceConfiguration _serviceConfiguration;
        private readonly IConfigurator _configurator;
        private readonly IBedrockLogger _logger;
        private readonly IProcessInfo _processInfo;
        private readonly IUpdater _updater;
        private readonly IPlayerManager _playerManager;
        private readonly FileUtilities _fileUtils;
        private readonly BackupManager _backupManager;
        private System.Timers.Timer? _backupTimer { get; set; }
        private CrontabSchedule? _backupCron { get; set; }
        private CrontabSchedule? _updaterCron { get; set; }
        private System.Timers.Timer? _updaterTimer { get; set; }
        private IBedrockLogger _serverLogger;
        private List<IPlayer> _connectedPlayers = new();
        private DateTime _startTime;
        private const string _startupMessage = "INFO] Server started.";
        private bool _AwaitingStopSignal = true;
        private bool _serverModifiedFlag = true;
        private bool _LiteLoadedServer = false;

        public BedrockServer(IServerConfiguration serverConfiguration, IConfigurator configurator, IBedrockLogger logger, IServiceConfiguration serviceConfiguration, IProcessInfo processInfo, FileUtilities fileUtils, IPlayerManager servicePlayerManager) {
            _fileUtils = fileUtils;
            _serverConfiguration = serverConfiguration;
            _processInfo = processInfo;
            _serviceConfiguration = serviceConfiguration;
            _playerManager = serviceConfiguration.GetProp("GlobalizedPlayerDatabase").GetBoolValue() ? servicePlayerManager : new ServerPlayerManager(serverConfiguration);
            _configurator = configurator;
            _logger = logger;
            _backupManager = new BackupManager(_logger, this, serverConfiguration, serviceConfiguration);
            _updater = new Updater(_logger, _serviceConfiguration);
        }

        public void Initialize() {
            _serverLogger = new BedrockLogger(_processInfo, _serviceConfiguration, _serverConfiguration);
            _serverLogger.Initialize();
            _updater.Initialize();
            if (_serverConfiguration.GetSettingsProp(ServerPropertyKeys.CheckUpdates).GetBoolValue()) {
                _updater.CheckLatestVersion().Wait();
            }
            _updater.CheckLiteLiteLoaderVersion();
        }

        public void CheckUpdates() {
            _updater.CheckLatestVersion().Wait();
        }

        public void StartWatchdog() {
            _watchdogCanceler = new CancellationTokenSource();
            _watchdogTask = null;
            _watchdogTask = ApplicationWatchdogMonitor();
            _watchdogTask.Start();
        }

        public Task AwaitableServerStart() {
            return Task.Run(() => {
                string exeName = _serverConfiguration.GetSettingsProp(ServerPropertyKeys.ServerExeName).ToString();
                string appName = exeName.Substring(0, exeName.Length - 4);
                if (ProcessUtilities.MonitoredAppExists(appName)) {
                    ProcessUtilities.KillProcessList(Process.GetProcessesByName(appName));
                    Task.Delay(500).Wait();
                }
                StartServerTask();
                InitializeBackupTimer();
                InitializeUpdateTimer();
                while (_currentServerStatus != ServerStatus.Started) {
                    Task.Delay(10).Wait();
                }
                _startTime = DateTime.Now;
            });
        }

        public Task AwaitableServerStop(bool stopWatchdog) {
            return Task.Run(() => {
                while (_backupManager.BackupRunning()) {
                    Task.Delay(100).Wait();
                }
                if (stopWatchdog) {
                    StopWatchdog().Wait();
                }
                if (_backupTimer != null) {
                    _backupTimer.Stop();
                    _backupTimer = null;
                }
                if (_updaterTimer != null) {
                    _updaterTimer.Stop();
                    _updaterTimer = null;
                }
                if (_currentServerStatus != ServerStatus.Started) {
                    if (_serverProcess != null) {
                        _serverProcess.Kill();
                        Task.Delay(500).Wait();
                    }
                    _currentServerStatus = ServerStatus.Stopped;
                } else {
                    _currentServerStatus = ServerStatus.Stopping;
                    WriteToStandardIn("stop");
                    while (_AwaitingStopSignal) {
                        Task.Delay(100).Wait();
                    }
                    _currentServerStatus = ServerStatus.Stopped;
                    _AwaitingStopSignal = true;
                    Task.Delay(500).Wait();
                }
            });
        }
        public Task RestartServer() {
            return Task.Run(() => {
                AwaitableServerStop(false).Wait();
                AwaitableServerStart().Wait();
            });
        }

        public void ForceKillServer() => _serverProcess.Kill();

        public string GetServerName() => _serverConfiguration.GetServerName();

        public void WriteToStandardIn(string command) {
            if (_stdInStream != null) {
                _stdInStream.WriteLine(command);
            }
        }

        private void InitializeBackupTimer() {
            _backupCron = CrontabSchedule.TryParse(_serverConfiguration.GetSettingsProp(ServerPropertyKeys.BackupCron).ToString());
            if (_serverConfiguration.GetSettingsProp(ServerPropertyKeys.BackupEnabled).GetBoolValue() && _backupCron != null) {
                double interval = (_backupCron.GetNextOccurrence(DateTime.Now) - DateTime.Now).TotalMilliseconds;
                if (interval >= 0) {
                    if (_backupTimer != null) {
                        _backupTimer.Stop();
                        _backupTimer = null;
                    }
                    _backupTimer = new System.Timers.Timer(interval);
                    _logger.AppendLine($"Automatic backups for server {GetServerName()} enabled, next backup at: {_backupCron.GetNextOccurrence(DateTime.Now):G}.");
                    _backupTimer.Elapsed += BackupTimer_Elapsed;
                    _backupTimer.AutoReset = false;
                    _backupTimer.Start();
                }
            }
            if (!_serverConfiguration.GetSettingsProp(ServerPropertyKeys.BackupEnabled).GetBoolValue()) {
                if (_backupTimer != null) {
                    _backupTimer.Stop();
                    _backupTimer = null;
                }
            }
        }

        private void BackupTimer_Elapsed(object sender, ElapsedEventArgs e) {
            try {
                bool shouldBackup = _serverConfiguration.GetSettingsProp(ServerPropertyKeys.IgnoreInactiveBackups).GetBoolValue();
                if ((shouldBackup && _serverModifiedFlag) || !shouldBackup) {
                    _backupManager.InitializeBackup();
                } else {
                    _logger.AppendLine($"Backup for server {GetServerName()} was skipped due to inactivity.");
                }
                ((System.Timers.Timer)sender).Stop();
                InitializeBackupTimer();
            } catch (Exception ex) {
                ((System.Timers.Timer)sender).Stop();
                _logger.AppendLine($"Error in BackupTimer_Elapsed {ex}");
            }
        }

        private void InitializeUpdateTimer() {
            _updaterCron = CrontabSchedule.TryParse(_serverConfiguration.GetSettingsProp(ServerPropertyKeys.UpdateCron).ToString());
            if (_serverConfiguration.GetSettingsProp(ServerPropertyKeys.CheckUpdates).GetBoolValue() && _updaterCron != null) {
                double interval = (_updaterCron.GetNextOccurrence(DateTime.Now) - DateTime.Now).TotalMilliseconds;
                if (_updaterTimer != null) {
                    _updaterTimer.Stop();
                    _updaterTimer = null;
                }
                if (interval >= 0) {
                    _updaterTimer = new System.Timers.Timer(interval);
                    _updaterTimer.Elapsed += UpdateTimer_Elapsed;
                    _logger.AppendLine($"Automatic updates Enabled, will be checked at: {_updaterCron.GetNextOccurrence(DateTime.Now):G}.");
                    _updaterTimer.Start();
                }
            }
            if (!_serverConfiguration.GetSettingsProp(ServerPropertyKeys.CheckUpdates).GetBoolValue()) {
                if (_updaterTimer != null) {
                    _updaterTimer.Stop();
                    _updaterTimer = null;
                }
            }
        }

        private void UpdateTimer_Elapsed(object sender, ElapsedEventArgs e) {
            try {
                _updater.CheckLatestVersion().Wait();
                if (_serverConfiguration.GetSettingsProp(ServerPropertyKeys.AutoDeployUpdates).GetBoolValue() && _serverConfiguration.GetSettingsProp(ServerPropertyKeys.DeployedVersion).StringValue != _serviceConfiguration.GetLatestBDSVersion() && !_serverConfiguration.ServerHasSetVersion()) {
                    _logger.AppendLine("Version change detected! Restarting server(s) to apply update...");
                    RestartServer().Wait();
                }
                ((System.Timers.Timer)sender).Stop();
                InitializeUpdateTimer();
            } catch (Exception ex) {
                ((System.Timers.Timer)sender).Stop();
                _logger.AppendLine($"Error in UpdateTimer_Elapsed {ex}");
            }
        }

        public bool RollbackToBackup(byte serverIndex, string zipFilePath) {
            IServerConfiguration server = _serviceConfiguration.GetServerInfoByIndex(serverIndex);
            DirectoryInfo worldsDir = new($@"{server.GetSettingsProp(ServerPropertyKeys.ServerPath)}\worlds");
            FileInfo backupZipFileInfo = new($@"{server.GetSettingsProp(ServerPropertyKeys.BackupPath)}\{server.GetServerName()}\{zipFilePath}");
            DirectoryInfo backupPacksDir = new($@"{worldsDir.FullName}\InstalledPacks");
            AwaitableServerStop(false).Wait();
            try {
                _fileUtils.DeleteFilesFromDirectory(worldsDir, true).Wait();
                _logger.AppendLine($"Deleted world folder \"{worldsDir.Name}\"");
                ZipFile.ExtractToDirectory(backupZipFileInfo.FullName, worldsDir.FullName);
                _logger.AppendLine($"Copied files from backup \"{backupZipFileInfo.Name}\" to server worlds directory.");
                MinecraftPackParser parser = new();
                foreach (FileInfo file in backupPacksDir.GetFiles()) {
                    _fileUtils.ClearTempDir().Wait();
                    ZipFile.ExtractToDirectory(file.FullName, $@"{Path.GetTempPath()}\BMSTemp\PackTemp", true);
                    parser.FoundPacks.Clear();
                    parser.ParseDirectory($@"{Path.GetTempPath()}\BMSTemp\PackTemp");
                    if (parser.FoundPacks[0].ManifestType == "data") {
                        string folderPath = $@"{server.GetSettingsProp(ServerPropertyKeys.ServerPath)}\development_behavior_packs\{file.Name.Substring(0, file.Name.Length - file.Extension.Length)}";
                        Task.Run(() => _fileUtils.DeleteFilesFromDirectory(folderPath, false)).Wait();
                        ZipFile.ExtractToDirectory(file.FullName, folderPath, true);
                    }
                    if (parser.FoundPacks[0].ManifestType == "resources") {
                        string folderPath = $@"{server.GetSettingsProp(ServerPropertyKeys.ServerPath)}\development_resource_packs\{file.Name.Substring(0, file.Name.Length - file.Extension.Length)}";
                        Task.Run(() => _fileUtils.DeleteFilesFromDirectory(folderPath, false)).Wait();
                        ZipFile.ExtractToDirectory(file.FullName, folderPath, true);
                    }
                }
                AwaitableServerStart().Wait();
                return true;
            } catch (IOException e) {
                _logger.AppendLine($"Error deleting selected backups! {e.Message}");
            }
            return false;
        }

        public ServerStatusModel GetServerStatus() => new() {
            ServerUptime = _startTime,
            ServerStatus = _currentServerStatus,
            ActivePlayerList = _connectedPlayers,
            ServerIndex = _serviceConfiguration.GetServerIndex(_serverConfiguration),
            TotalBackups = _serverConfiguration.GetStatus().TotalBackups,
            TotalSizeOfBackups = _serverConfiguration.GetStatus().TotalSizeOfBackups,
            DeployedVersion = _serverConfiguration.GetServerVersion()
        };

        public List<IPlayer> GetActivePlayerList() => _connectedPlayers;

        public IBedrockLogger GetLogger() => _serverLogger;

        public bool IsServerModified() => _serverModifiedFlag;

        public void SetServerModified(bool isModified) => _serverModifiedFlag = isModified;

        public bool ServerAutostartEnabled() => _serverConfiguration.GetSettingsProp(ServerPropertyKeys.ServerAutostartEnabled).GetBoolValue();

        public bool IsPrimaryServer() {
            return _serverConfiguration.GetProp("server-port").StringValue == "19132" ||
            _serverConfiguration.GetProp("server-port").StringValue == "19133" ||
            _serverConfiguration.GetProp("server-portv6").StringValue == "19132" ||
            _serverConfiguration.GetProp("server-portv6").StringValue == "19133";
        }

        public IPlayerManager GetPlayerManager() => _playerManager;

        private Task StopWatchdog() {
            return Task.Run(() => {
                _watchdogCanceler.Cancel();
                while (_watchdogTask != null && !_watchdogTask.IsCompleted) {
                    Task.Delay(200);
                }
            });
        }

        private void StartServerTask() {
            _logger.AppendLine($"Recieved start signal for server {_serverConfiguration.GetServerName()}.");
            _serverCanceler = new CancellationTokenSource();
            _serverTask = RunServer();
            _serverTask.Start();
        }

        private void StdOutToLog(object sender, DataReceivedEventArgs e) {
            if (e.Data != null && !e.Data.Contains("Running AutoCompaction...")) {
                ConsoleFilterStrategyClass consoleFilter = new ConsoleFilterStrategyClass(_logger, _configurator, _serverConfiguration, this, _serviceConfiguration);
                string input = e.Data;
                string logFileText = "NO LOG FILE! - ";
                if (input.StartsWith(logFileText))
                    input = input.Substring(logFileText.Length, input.Length - logFileText.Length);
                _serverLogger.AppendLine(input);
                if (e.Data != null) {
                    if (input.Equals("Quit correctly")) {
                        _logger.AppendLine($"Server {GetServerName()} received quit signal.");
                        _AwaitingStopSignal = false;
                    }
                    if (input.Contains("[PreLoader]")) {
                        _LiteLoadedServer = true;
                    }
                    if (input.Contains("Changes to the world are resumed")) {
                        _backupManager.SetBackupComplete();
                    }
                    foreach (KeyValuePair<string, IConsoleFilter> filter in consoleFilter.FilterList) {
                        if (input.Contains(filter.Key)) {
                            filter.Value.Filter(input);
                            break;
                        }
                    }
                }
            }
        }

        private Task ApplicationWatchdogMonitor() {
            return new Task(() => {
                while (true) {
                    int procId = _serverConfiguration.GetRunningPid();
                    bool appExists = ProcessUtilities.MonitoredAppExists(procId);
                    if (!appExists && _currentServerStatus == ServerStatus.Started && !_watchdogCanceler.IsCancellationRequested) {
                        _logger.AppendLine($"Started application {_serverConfiguration.GetSettingsProp(ServerPropertyKeys.FileName)} was not found in running processes... Resarting.");
                        _currentServerStatus = ServerStatus.Stopped;
                        AwaitableServerStart().Wait();
                    }
                    if (_watchdogCanceler.IsCancellationRequested) {
                        break;
                    }
                    Task.Delay(1000).Wait();
                }
            }, _watchdogCanceler.Token);
        }

        private Task RunServer() {
            return new Task(() => {
                string exeName = _serverConfiguration.GetSettingsProp(ServerPropertyKeys.ServerExeName).ToString();
                string appName = exeName.Substring(0, exeName.Length - 4);
                _configurator.WriteJSONFiles(_serverConfiguration);
                MinecraftFileUtilities.WriteServerPropsFile(_serverConfiguration);

                try {
                    if (File.Exists(GetServerFilePath(BdsFileNameKeys.BmsServer_Name, _serverConfiguration, _serverConfiguration.GetServerName()))) {
                        if (ProcessUtilities.MonitoredAppExists(appName)) {
                            ProcessUtilities.KillProcessList(Process.GetProcessesByName(appName));
                        }
                        CreateProcess();
                    } else {
                        _logger.AppendLine($"The Bedrock Server is not accessible at {$@"{_serverConfiguration.GetSettingsProp(ServerPropertyKeys.ServerPath)}\{_serverConfiguration.GetSettingsProp(ServerPropertyKeys.ServerExeName)}"}\r\nCheck if the file is at that location and that permissions are correct.");
                    }
                } catch (Exception e) {
                    _logger.AppendLine($"Error Running Bedrock Server: {e.Message}\n{e.StackTrace}");
                }
            }, _serverCanceler.Token);
        }

        private void CreateProcess() {
            string fileName = $@"{_serverConfiguration.GetSettingsProp(ServerPropertyKeys.ServerPath).StringValue}\{_serverConfiguration.GetSettingsProp(ServerPropertyKeys.ServerExeName).StringValue} ";
            ProcessStartInfo processStartInfo = new() {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                FileName = fileName
            };
            _serverProcess = Process.Start(processStartInfo);
            if (_serverProcess != null) {
                _serverConfiguration.SetRunningPid(_serverProcess.Id);
                _serverProcess.PriorityClass = ProcessPriorityClass.High;
                _serverProcess.OutputDataReceived += StdOutToLog;
                _serverProcess.BeginOutputReadLine();
                _stdInStream = _serverProcess.StandardInput;
                _serverProcess.EnableRaisingEvents = false;
            }
        }

        public void RunStartupCommands() {
            foreach (StartCmdEntry cmd in _serverConfiguration.GetStartCommands()) {
                _stdInStream.WriteLine(cmd.Command.Trim());
                Thread.Sleep(1000);
            }
        }

        public bool LiteLoadedServer() => _LiteLoadedServer;

        public BackupManager GetBackupManager() => _backupManager;

        public void SetStartupStatus(ServerStatus status) => _currentServerStatus = status;
    }
}
