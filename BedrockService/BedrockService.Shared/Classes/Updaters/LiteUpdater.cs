﻿using BedrockService.Shared.Interfaces;
using BedrockService.Shared.JsonModels.LiteLoaderJsonModels;
using BedrockService.Shared.JsonModels.MinecraftJsonModels;
using BedrockService.Shared.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static BedrockService.Shared.Classes.SharedStringBase;

namespace BedrockService.Shared.Classes.Updaters {
    public class LiteUpdater : BedrockUpdater, IUpdater {
        private IServerLogger _logger;
        private readonly IServiceConfiguration _serviceConfiguration;
        private readonly IServerConfiguration _serverConfiguration;
        private readonly MinecraftServerArch _serverArch = MinecraftServerArch.LiteLoader;
        private string _version;

        public LiteUpdater(IServerLogger logger, IServiceConfiguration serviceConfiguration) : base(logger, serviceConfiguration) {
            _serviceConfiguration = serviceConfiguration;
            _logger = logger;
            _version = "None";
        }

        public LiteUpdater(IServerLogger logger, IServiceConfiguration serviceConfiguration, IServerConfiguration serverConfiguration) : base(logger, serviceConfiguration, serverConfiguration) {
            _serviceConfiguration = serviceConfiguration;
            _serverConfiguration = serverConfiguration;
            _logger = logger;
            _version = "None";
        }

        public new void Initialize() {
            if (!File.Exists(GetServiceFilePath(BmsFileNameKeys.LatestVerIni_Name, MinecraftArchStrings[_serverArch]))) {
                _logger.AppendLine("Version ini file missing, creating and fetching build...");
                File.Create(GetServiceFilePath(BmsFileNameKeys.LatestVerIni_Name, MinecraftArchStrings[_serverArch])).Close();
            }
            _version = File.ReadAllText(GetServiceFilePath(BmsFileNameKeys.LatestVerIni_Name, MinecraftArchStrings[_serverArch]));
            CheckLatestVersion().Wait();
        }

        public override Task CheckLatestVersion() => Task.Run(() => {
            string result = HTTPHandler.FetchHTTPContent(BmsUrlStrings[BmsUrlKeys.LLReleasesJson]).Result;
            if (result != null) {
                List<LiteLoaderVersionManifest> manifestList = JsonSerializer.Deserialize<List<LiteLoaderVersionManifest>>(result);
                manifestList.Reverse();
                LiteLoaderVersionManifest latestLLVersion = manifestList.FirstOrDefault();
                if (latestLLVersion == null) {
                    _logger.AppendLine("Could not parse LiteLoader manifest! Check link; Contact support.");
                    return;
                }
                latestLLVersion = manifestList.First();
                if (!File.Exists(GetServiceFilePath(BmsFileNameKeys.BdsUpdatePackage_Ver, latestLLVersion.BDSVersion))) {
                    base.FetchBuild(latestLLVersion.BDSVersion).Wait();
                }
                _logger.AppendLine($"Latest LiteLoader version found: \"{latestLLVersion.Version}\"");
                if (!File.Exists(GetServiceFilePath(BmsFileNameKeys.LLUpdatePackage_Ver, latestLLVersion.Version))) {
                    FetchBuild(latestLLVersion.Version).Wait();
                }

                (bool hasModule, BmsUrlKeys targetUrlPattern) verDetails = GetLiteLoaderVersionDetails(latestLLVersion.Version);
                if (verDetails.hasModule && !File.Exists(GetServiceFilePath(BmsFileNameKeys.LLModUpdatePackage_Ver, latestLLVersion.Version))) {
                    FetchBuild(latestLLVersion.Version).Wait();
                }
                _serviceConfiguration.SetLatestVersion(_serverArch, latestLLVersion.Version);
                _serviceConfiguration.SetServerDefaultPropList(_serverArch, MinecraftFileUtilities.GetDefaultPropListFromFile(GetServiceFilePath(BmsFileNameKeys.BedrockStockProps_Ver, latestLLVersion.BDSVersion)));
            }
        });

        public new Task<bool> FetchBuild(string version) =>
            Task.Run(() => {
                _logger.AppendLine($"Now downloading LiteLoader version {version}, please wait...");
                (bool hasModule, BmsUrlKeys targetUrlPattern) verDetails = GetLiteLoaderVersionDetails(version);
                string llFetchUrl = string.Format(BmsUrlStrings[verDetails.targetUrlPattern], version);
                string llZipPath = GetServiceFilePath(BmsFileNameKeys.LLUpdatePackage_Ver, version);
                string llModFetchUrl = string.Format(BmsUrlStrings[BmsUrlKeys.LLModPackage_Ver], version);
                string llModZipPath = GetServiceFilePath(BmsFileNameKeys.LLModUpdatePackage_Ver, version);
                new FileInfo(llZipPath).Directory.Create();
                new FileInfo(llModZipPath).Directory.Create();
                if (HTTPHandler.RetrieveFileFromUrl(llFetchUrl, llZipPath).Result) {
                    if (verDetails.hasModule) {
                        HTTPHandler.RetrieveFileFromUrl(llModFetchUrl, llModZipPath).Wait(); // If this doesn't exist, it's AIO.
                    }
                    return true;
                }
                return false;
            });

        private (bool hasModule, BmsUrlKeys targetUrlPattern) GetLiteLoaderVersionDetails(string inputVersion) {
            System.Version outputVer;
            string testVer;
            BmsUrlKeys targetUrlPattern = BmsUrlKeys.LLPackage_Ver;
            bool hasModule = false;
            if (inputVersion.Contains('-')) {
                testVer = inputVersion.Split('-')[0];
            } else {
                testVer = inputVersion;
            }
            if (inputVersion == "2.10.0-beta.1") {
                _logger.AppendLine("LL version 2.10.0-beta.x is not supported! BMS Will deploy 2.10.1 instead.");
                inputVersion = "2.10.1";
            }
            if (System.Version.TryParse(testVer, out outputVer)) {
                if (outputVer < System.Version.Parse("2.10.0")) {
                    targetUrlPattern = BmsUrlKeys.LLPackageOld_Ver;
                    hasModule = false;
                } else if (outputVer < System.Version.Parse("2.12.2")) {
                    targetUrlPattern = BmsUrlKeys.LLPackage_Ver;
                    hasModule = true;
                } else {
                    targetUrlPattern = BmsUrlKeys.LLPackage_Ver;
                    hasModule = false;
                }
            }
            return (hasModule, targetUrlPattern);
        }

        public static Task<LiteLoaderVersionManifest> GetLiteLoaderVersionManifest(string version) {
            return Task.Run(() => {
                string result = HTTPHandler.FetchHTTPContent(BmsUrlStrings[BmsUrlKeys.LLReleasesJson]).Result;
                if (result != null) {
                    List<LiteLoaderVersionManifest> manifestList = JsonSerializer.Deserialize<List<LiteLoaderVersionManifest>>(result);
                    return manifestList.Where(x => x.Version == version).FirstOrDefault();
                }
                return null;
            });
        }

        public new string GetBaseVersion(string modVersion) {
            return GetLiteLoaderVersionManifest(modVersion).Result.BDSVersion;
        }

        public async override Task ReplaceServerBuild(string versionOverride = "") {
            await Task.Run(() => {
                string version = versionOverride == "" ? _serverConfiguration.GetServerVersion() : versionOverride;
                FileInfo originalExeInfo = new(GetServerFilePath(BdsFileNameKeys.VanillaBedrock, _serverConfiguration));
                FileInfo bmsExeInfo = new($@"{_serverConfiguration.GetSettingsProp(ServerPropertyKeys.ServerPath)}\{_serverConfiguration.GetSettingsProp(ServerPropertyKeys.ServerExeName)}");
                if (!bmsExeInfo.Directory.Exists) {
                    bmsExeInfo.Directory.Create();
                }
                LiteLoaderVersionManifest selectedVersion = GetLiteLoaderVersionManifest(version).Result;
                if (selectedVersion == null) {
                    selectedVersion = GetLiteLoaderVersionManifest(_serviceConfiguration.GetLatestVersion(MinecraftServerArch.LiteLoader)).Result;
                }
                string liteVersion = selectedVersion?.Version;
                base.ReplaceServerBuild(selectedVersion.BDSVersion).Wait();

                try {
                    if (!File.Exists(GetServiceFilePath(BmsFileNameKeys.LLUpdatePackage_Ver, liteVersion))) {
                        if (!FetchBuild(liteVersion).Result) {
                            throw new FileNotFoundException($"Service could not locate file \"Update_{version}.zip\" and version was not found in LiteLoader manifest!");
                        }
                    }
                    Progress<double> progress = new(percent => {
                        _logger.AppendLine($"Extracting LiteLoader files for server {_serverConfiguration.GetServerName()}, {(int)percent}% completed...");
                    });
                    FileUtilities.ExtractZipToDirectory(GetServiceFilePath(BmsFileNameKeys.LLUpdatePackage_Ver, liteVersion), GetServerDirectory(BdsDirectoryKeys.ServerRoot, _serverConfiguration), progress).Wait();
                    if (File.Exists(GetServiceFilePath(BmsFileNameKeys.LLModUpdatePackage_Ver, liteVersion))) {
                        progress = new(percent => {
                            _logger.AppendLine($"Extracting LiteLoader Module files for server {_serverConfiguration.GetServerName()}, {(int)percent}% completed...");
                        });
                        FileUtilities.ExtractZipToDirectory(GetServiceFilePath(BmsFileNameKeys.LLModUpdatePackage_Ver, liteVersion), GetServerDirectory(BdsDirectoryKeys.ServerRoot, _serverConfiguration) + "\\plugins", progress).Wait();
                    }
                    LiteLoaderPECore.BuildLLExe(_serverConfiguration);
                    MinecraftFileUtilities.CreateDefaultLoaderConfigFile(_serverConfiguration);
                    if (_serverConfiguration.GetSettingsProp(ServerPropertyKeys.AutoDeployUpdates).GetBoolValue()) {
                        _serverConfiguration.SetServerVersion(liteVersion);
                    }
                    File.WriteAllText(GetServerFilePath(BdsFileNameKeys.DeployedINI, _serverConfiguration), liteVersion);
                } catch (IOException e) {
                    if (e.Message.Contains("because it is being used by another process.")) {
                        ProcessUtilities.KillProcessList(Process.GetProcessesByName(bmsExeInfo.Name[..^bmsExeInfo.Extension.Length]));
                        File.Copy(GetServerFilePath(BdsFileNameKeys.VanillaBedrock, _serverConfiguration), GetServerFilePath(BdsFileNameKeys.BmsServer_Name, _serverConfiguration, _serverConfiguration.GetServerName()), true);
                    }
                }
                _logger.AppendLine($"Extraction of files for {_serverConfiguration.GetServerName()} completed.");
            });
        }
        public List<SimpleVersionModel> GetVersionList() {
            List<SimpleVersionModel> result = new List<SimpleVersionModel>();
            string content = HTTPHandler.FetchHTTPContent(BmsUrlStrings[BmsUrlKeys.LLReleasesJson]).Result;
            List<LiteLoaderVersionManifest> versionList = JsonSerializer.Deserialize<List<LiteLoaderVersionManifest>>(content);
            versionList.Reverse();
            if (content == null)
                return new List<SimpleVersionModel>();
            versionList.Reverse();
            foreach (var version in versionList) {
                result.Add(new(version.Version, version.IsBeta.ToLower() == "true"));
            }
            return result;
        }

        public void SetNewLogger(IServerLogger logger) => _logger = logger;
    }
}
