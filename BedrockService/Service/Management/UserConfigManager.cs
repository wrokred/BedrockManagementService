﻿using BedrockService.Shared.Classes;
using BedrockService.Shared.Classes.Configurations;
using BedrockService.Shared.Classes.Updaters;
using BedrockService.Shared.FileModels.MinecraftFileModels;
using BedrockService.Shared.Interfaces;
using BedrockService.Shared.JsonModels.LiteLoaderJsonModels;
using BedrockService.Shared.JsonModels.MinecraftJsonModels;
using BedrockService.Shared.SerializeModels;
using System.Globalization;
using System.Xml.Linq;
using static BedrockService.Shared.Classes.SharedStringBase;

namespace BedrockService.Service.Management {
    public class ConfigManager : IConfigurator {
        private static readonly object _fileLock = new();
        private readonly IServiceConfiguration _serviceConfiguration;
        private readonly IProcessInfo _processInfo;
        private readonly IServerLogger _logger;
        private readonly FileUtilities FileUtilities;
        private BedrockUpdater _updater;
        private List<string> serviceConfigExcludeList = new() { "MinecraftType", "ServerName", "ServerExeName", "FileName", "ServerPath", "ServerVersion" };

        public ConfigManager(IProcessInfo processInfo, IServiceConfiguration serviceConfiguration, IServerLogger logger, FileUtilities fileUtilities) {
            _processInfo = processInfo;
            _serviceConfiguration = serviceConfiguration;
            _logger = logger;
            FileUtilities = fileUtilities;
            _updater = new(_logger, _serviceConfiguration);
        }

        public Task LoadGlobals() => Task.Run(() => {
            _serviceConfiguration.InitializeDefaults();
            if (File.Exists(GetServiceFilePath(BmsFileNameKeys.ServiceConfig))) {
                _serviceConfiguration.ProcessUserConfiguration(File.ReadAllLines(GetServiceFilePath(BmsFileNameKeys.ServiceConfig)));
                _logger.AppendLine("Loaded Service props.");
                return;
            }
            _logger.AppendLine("Service.conf was not found. Loaded defaults and saved to file.");
            SaveGlobalFile();
        });

        public Task LoadServerConfigurations() => Task.Run(() => {
            IServerConfiguration serverInfo;
            EnumTypeLookup typeLookup = new(_logger, _serviceConfiguration);
            _serviceConfiguration.GetServerList().Clear();
            string[] files = Directory.GetFiles(GetServiceDirectory(BmsDirectoryKeys.ServerConfigs), "*.conf");
            foreach (string file in files) {
                FileInfo FInfo = new(file);
                string[] fileEntries = File.ReadAllLines(file);
                string[] archType = fileEntries[0].Split('=');
                if (archType[0] != "MinecraftType") {
                    serverInfo = new BedrockConfiguration(_processInfo, _logger, _serviceConfiguration);
                } else {
                    serverInfo = typeLookup.PrepareNewServerByArchName(archType[1], _processInfo, _logger, _serviceConfiguration);
                }
                if (serverInfo.InitializeDefaults()) {
                    serverInfo.ProcessUserConfiguration(fileEntries);
                    serverInfo.UpdateServerProps(serverInfo.GetDeployedVersion() == "None" ? _serviceConfiguration.GetLatestVersion(serverInfo.GetServerArch()) : serverInfo.GetServerVersion());
                    _logger.AppendLine($"Loaded config for server {serverInfo.GetServerName()}.");
                }
                _serviceConfiguration.AddNewServerInfo(serverInfo);
                SaveServerConfiguration(serverInfo);
            }
            if (_serviceConfiguration.GetServerList().Count == 0) {
                serverInfo = new BedrockConfiguration(_processInfo, _logger, _serviceConfiguration);
                if (!serverInfo.InitializeDefaults()) {
                    _logger.AppendLine("Error creating default server!");
                }
                SaveServerConfiguration(serverInfo);
                _serviceConfiguration.AddNewServerInfo(serverInfo);
                _logger.AppendLine("Successfully created and saved Default Server.");
            }
        });

        public void SaveGlobalFile() {
            string[] output = new string[_serviceConfiguration.GetAllProps().Count + 3];
            int index = 0;
            output[index++] = "#Globals";
            output[index++] = string.Empty;
            foreach (Property prop in _serviceConfiguration.GetAllProps()) {
                output[index++] = $"{prop.KeyName}={prop}";
            }
            output[index++] = string.Empty;

            File.WriteAllLines(GetServiceFilePath(BmsFileNameKeys.ServiceConfig), output);
        }

        public void SavePlayerDatabase(IServerConfiguration server) {
            string dbPath = GetServiceFilePath(BmsFileNameKeys.ServerPlayerTelem_Name, server.GetServerName());
            string regPath = GetServiceFilePath(BmsFileNameKeys.ServerPlayerRegistry_Name, server.GetServerName());
            List<IPlayer> playerList = server.GetPlayerList();
            if (_serviceConfiguration.GetProp(ServicePropertyKeys.GlobalizedPlayerDatabase).GetBoolValue()) {
                dbPath = GetServiceFilePath(BmsFileNameKeys.GlobalPlayerTelem);
                regPath = GetServiceFilePath(BmsFileNameKeys.GlobalPlayerRegistry);
                playerList = _serviceConfiguration.GetPlayerList();
            }
            lock (_fileLock) {
                TextWriter writer = new StreamWriter(dbPath);
                foreach (Player entry in playerList) {
                    writer.WriteLine(entry.ToString("Known"));
                }
                writer.Flush();
                writer.Close();
            }
            lock (_fileLock) {
                TextWriter writer = new StreamWriter(regPath);
                writer.WriteLine("# Registered player list");
                writer.WriteLine("# Register player entries: PlayerEntry=xuid,username,permission,isWhitelisted,ignoreMaxPlayers");
                writer.WriteLine("# Example: 1234111222333444,TestUser,visitor,false,false");
                writer.WriteLine("");
                foreach (IPlayer player in playerList) {
                    if (!player.IsDefaultRegistration())
                        writer.WriteLine(player.ToString("Registered"));
                }
                writer.Flush();
                writer.Close();
            }
        }

        public void SaveServerConfiguration(IServerConfiguration server) {
            int index = 0;
            if (server.GetSettingsProp(ServerPropertyKeys.ServerPath).ToString() == null) {
                server.GetSettingsProp(ServerPropertyKeys.ServerPath).SetValue(server.GetSettingsProp(ServerPropertyKeys.ServerPath).DefaultValue);
            }
            string serverPath = server.GetSettingsProp(ServerPropertyKeys.ServerPath).ToString();
            string[] output = new string[11 + server.GetSettingsList().Count + server.GetAllProps().Count + server.GetStartCommands().Count];

            output[index++] = $"MinecraftType={MinecraftArchStrings[server.GetServerArch()]}";
            output[index++] = "#Service";
            server.GetSettingsList().ForEach(prop => {
                if (serviceConfigExcludeList.Contains(prop.KeyName)) {
                    if (server.GetServerArch() == MinecraftServerArch.Java) {
                        if (prop.KeyName != ServerPropertyStrings[ServerPropertyKeys.ServerName]) {
                            return;
                        }
                    } else {
                        return;
                    }
                }
                output[index++] = prop.PropFileFormatString();
            });
            output[index++] = string.Empty;

            output[index++] = "#Server";
            server.GetAllProps().ForEach(prop => {
                output[index++] = prop.PropFileFormatString();
            });
            if (!Directory.Exists(serverPath)) {
                Directory.CreateDirectory(serverPath);
            }
            output[index++] = string.Empty;

            output[index++] = "#StartCmds";
            foreach (StartCmdEntry startCmd in server.GetStartCommands()) {
                output[index++] = $"AddStartCmd={startCmd.Command}";
            }
            output[index++] = string.Empty;
            output[index++] = "#Persist - Do not modify";
            output[index++] = $"ServerVersion={server.GetSettingsProp(ServerPropertyKeys.ServerVersion)}";
            output[index++] = string.Empty;

            File.WriteAllLines(GetServiceFilePath(BmsFileNameKeys.ServerConfig_Name, server.GetServerName()), output);
            Task.Delay(500).Wait();
        }

        public void WriteJSONFiles(IServerConfiguration server) {
            string serverVer = server.GetServerVersion();
            System.Version allowStartVersion = System.Version.Parse("1.18.11.01");
            string whitelistFilePath = string.Empty;
            if (server.GetServerVersion() != "None" && System.Version.Parse(serverVer) >= allowStartVersion) {
                whitelistFilePath = GetServerFilePath(BdsFileNameKeys.AllowList, server);
                if (File.Exists(GetServerFilePath(BdsFileNameKeys.WhiteList, server))) {
                    File.Delete(GetServerFilePath(BdsFileNameKeys.WhiteList, server));
                }
            } else {
                whitelistFilePath = GetServerFilePath(BdsFileNameKeys.WhiteList, server);
            }
            string permFilePath = GetServerFilePath(BdsFileNameKeys.PermList, server);
            PermissionsFileModel permissionsFile = new() { FilePath = permFilePath };
            WhitelistFileModel whitelistFile = new() { FilePath = whitelistFilePath };
            if (_serviceConfiguration.GetProp(ServicePropertyKeys.GlobalizedPlayerDatabase).GetBoolValue()) {
                _serviceConfiguration.GetPlayerList()
                    .Where(x => x.IsPlayerWhitelisted())
                    .Select(x => (xuid: x.GetPlayerID(), userName: x.GetUsername(), ignoreLimits: x.PlayerIgnoresLimit()))
                    .ToList().ForEach(x => {
                        whitelistFile.Contents.Add(new WhitelistEntryJsonModel(x.ignoreLimits, x.xuid, x.userName));
                    });
                _serviceConfiguration.GetPlayerList()
                    .Where(x => !x.IsDefaultRegistration())
                    .Select(x => (xuid: x.GetPlayerID(), permLevel: x.GetPermissionLevel()))
                    .ToList().ForEach(x => {
                        permissionsFile.Contents.Add(new PermissionsEntryJsonModel(x.permLevel, x.xuid));
                    });
            } else {
                server.GetPlayerList()
                    .Where(x => x.IsPlayerWhitelisted())
                    .Select(x => (xuid: x.GetPlayerID(), userName: x.GetUsername(), ignoreLimits: x.PlayerIgnoresLimit()))
                    .ToList().ForEach(x => {
                        whitelistFile.Contents.Add(new WhitelistEntryJsonModel(x.ignoreLimits, x.xuid, x.userName));
                    });
                server.GetPlayerList()
                    .Where(x => !x.IsDefaultRegistration())
                    .Select(x => (xuid: x.GetPlayerID(), permLevel: x.GetPermissionLevel()))
                    .ToList().ForEach(x => {
                        permissionsFile.Contents.Add(new PermissionsEntryJsonModel(x.permLevel, x.xuid));
                    });
            }
            permissionsFile.SaveToFile(permissionsFile.Contents);
            whitelistFile.SaveToFile(whitelistFile.Contents);
        }

        private class BackupComparer : IComparer<BackupInfoModel> {
            public int Compare(BackupInfoModel? x, BackupInfoModel? y) {
                if (x != null && y != null) {
                    DateTime xTime = DateTime.ParseExact(x.Filename.Substring(7, 17), "yyyyMMdd_HHmmssff", CultureInfo.InvariantCulture);
                    DateTime yTime = DateTime.ParseExact(y.Filename.Substring(7, 17), "yyyyMMdd_HHmmssff", CultureInfo.InvariantCulture);
                    if (xTime > yTime) {
                        return 1;
                    } else if (yTime > xTime) {
                        return -1;
                    }
                }
                return 0;
            }
        }

        public Task<List<BackupInfoModel>> EnumerateBackupsForServer(byte serverIndex) {
            return Task.Run(() => {
                IServerConfiguration server = _serviceConfiguration.GetServerInfoByIndex(serverIndex);
                _serviceConfiguration.CalculateTotalBackupsAllServers().Wait();
                List<BackupInfoModel> newList = new();
                try {
                    foreach (FileInfo dir in new DirectoryInfo($@"{server.GetSettingsProp(ServerPropertyKeys.BackupPath)}\{server.GetServerName()}").GetFiles()) {
                        newList.Add(new BackupInfoModel(dir));
                    }
                    newList.Sort(new BackupComparer());
                    newList.Reverse();
                } catch (IOException) {
                    return newList;
                }
                return newList;
            });
        }

        public void RemoveServerConfigs(IServerConfiguration serverInfo, NetworkMessageFlags flag) {
            try {
                _logger.AppendLine("Beginning removal of selected options. Please wait!");
                Task.Delay(3000).Wait();
                switch (flag) {
                    case NetworkMessageFlags.RemoveBckPly:
                        if (DeleteAllBackups(serverInfo))
                            _logger.AppendLine($"Deleted Backups for server {serverInfo.GetServerName()}");
                        if (DeletePlayerFiles(serverInfo))
                            _logger.AppendLine($"Deleted Player files for server {serverInfo.GetServerName()}");
                        break;
                    case NetworkMessageFlags.RemoveBckSrv:
                        if (DeleteAllBackups(serverInfo))
                            _logger.AppendLine($"Deleted Backups for server {serverInfo.GetServerName()}");
                        if (DeleteServerFiles(serverInfo))
                            _logger.AppendLine($"Deleted server directory for server {serverInfo.GetServerName()}");
                        break;
                    case NetworkMessageFlags.RemovePlySrv:
                        if (DeletePlayerFiles(serverInfo))
                            _logger.AppendLine($"Deleted Player files for server {serverInfo.GetServerName()}");
                        if (DeleteServerFiles(serverInfo))
                            _logger.AppendLine($"Deleted server directory for server {serverInfo.GetServerName()}");
                        break;
                    case NetworkMessageFlags.RemoveSrv:
                        if (DeleteServerFiles(serverInfo))
                            _logger.AppendLine($"Deleted server directory for server {serverInfo.GetServerName()}");
                        break;
                    case NetworkMessageFlags.RemovePlayers:
                        if (DeletePlayerFiles(serverInfo))
                            _logger.AppendLine($"Deleted Player files for server {serverInfo.GetServerName()}");
                        break;
                    case NetworkMessageFlags.RemoveBackups:
                        if (DeleteAllBackups(serverInfo))
                            _logger.AppendLine($"Deleted Backups for server {serverInfo.GetServerName()}");
                        break;
                    case NetworkMessageFlags.RemoveAll:
                        if (DeleteAllBackups(serverInfo))
                            _logger.AppendLine($"Deleted Backups for server {serverInfo.GetServerName()}");
                        if (DeletePlayerFiles(serverInfo))
                            _logger.AppendLine($"Deleted Player files for server {serverInfo.GetServerName()}");
                        if (DeleteServerFiles(serverInfo))
                            _logger.AppendLine($"Deleted server directory for server {serverInfo.GetServerName()}");
                        break;
                    case NetworkMessageFlags.None:
                        break;
                }
                _serviceConfiguration.RemoveServerInfo(serverInfo);
            } catch { }
        }

        public void DeleteBackupForServer(byte serverIndex, string backupName) {
            string serverName = _serviceConfiguration.GetServerInfoByIndex(serverIndex).GetServerName();
            DirectoryInfo serverBackupDir = new($@"{_serviceConfiguration.GetServerInfoByIndex(serverIndex).GetSettingsProp(ServerPropertyKeys.BackupPath)}\{serverName}");
            try {
                foreach (FileInfo file in serverBackupDir.GetFiles())
                    if (file.Name == backupName || backupName == "-RemoveAll-") {
                        file.Delete();
                        _logger.AppendLine($"Deleted backup {file.Name}.");
                    }
                _serviceConfiguration.CalculateTotalBackupsAllServers().Wait();
            } catch (IOException e) {
                _logger.AppendLine($"Error deleting selected backups! {e.Message}");
            }
        }

        private void LoadPlayerDatabase(IServerConfiguration server) {
            if (_serviceConfiguration.GetProp(ServicePropertyKeys.GlobalizedPlayerDatabase).GetBoolValue()) {
                string dbPath = GetServiceFilePath(BmsFileNameKeys.GlobalPlayerTelem);
                string regPath = GetServiceFilePath(BmsFileNameKeys.GlobalPlayerRegistry);
                List<string[]> playerDbEntries = MinecraftFileUtilities.FilterLinesFromPlayerDbFile(dbPath);
                List<string[]> playerRegEntries = MinecraftFileUtilities.FilterLinesFromPlayerDbFile(regPath);
                playerDbEntries.ForEach(x => {
                    _serviceConfiguration.GetOrCreatePlayer(x[0]).UpdatePlayerFromDbStrings(x);
                });
                playerRegEntries.ForEach(x => {
                    _serviceConfiguration.GetOrCreatePlayer(x[0]).UpdatePlayerFromRegStrings(x);
                });

            } else {
                string serverName = server.GetServerName();
                string dbPath = GetServiceFilePath(BmsFileNameKeys.ServerPlayerTelem_Name, server.GetServerName());
                string regPath = GetServiceFilePath(BmsFileNameKeys.ServerPlayerRegistry_Name, server.GetServerName());
                List<string[]> playerDbEntries = MinecraftFileUtilities.FilterLinesFromPlayerDbFile(dbPath);
                List<string[]> playerRegEntries = MinecraftFileUtilities.FilterLinesFromPlayerDbFile(regPath);
                playerDbEntries.ForEach(x => {
                    server.GetOrCreatePlayer(x[0]).UpdatePlayerFromDbStrings(x);
                });
                playerRegEntries.ForEach(x => {
                    server.GetOrCreatePlayer(x[0]).UpdatePlayerFromRegStrings(x);
                });
            }
        }

        private bool DeleteAllBackups(IServerConfiguration serverInfo) {
            try {
                string configBackupPath = serverInfo.GetSettingsProp(ServerPropertyKeys.BackupPath).ToString();
                DirectoryInfo backupDirInfo = new($@"{configBackupPath}\{serverInfo.GetServerName()}");
                foreach (FileInfo dir in backupDirInfo.EnumerateFiles()) {
                    dir.Delete();
                }
                return true;
            } catch (Exception e) { 
                _logger.AppendLine($"Error deleting server backups: {e.Message}");
                return false;
            }
        }

        private bool DeleteServerFiles(IServerConfiguration serverInfo) {
            try {
                string serverPath = serverInfo.GetSettingsProp(ServerPropertyKeys.ServerPath).StringValue;
                DirectoryInfo serverDirInfo = new DirectoryInfo(serverPath);
                FileUtilities.DeleteFilesFromDirectory(serverDirInfo, true).Wait();
                return true;
            } catch (Exception e) {
                _logger.AppendLine($"Error deleting server files: {e.Message}");
                return false;
            }
        }

        private bool DeletePlayerFiles(IServerConfiguration serverInfo) {
            try {
                DirectoryInfo configDirInfo = new(GetServiceDirectory(BmsDirectoryKeys.ServerConfigs));
                foreach (DirectoryInfo dir in configDirInfo.GetDirectories()) {
                    if (dir.Name == "KnownPlayers" || dir.Name == "RegisteredPlayers") {
                        foreach (FileInfo file in dir.GetFiles()) {
                            if (file.Name.Contains($"{serverInfo.GetServerName()}")) {
                                file.Delete();
                            }
                        }
                    }
                }
                return true;
            } catch (Exception e) {
                _logger.AppendLine($"Error deleting server player files: {e.Message}");
                return false; 
            }
        }
    }
}

