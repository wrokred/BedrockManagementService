﻿using BedrockService.Service.Networking.Interfaces;
using BedrockService.Shared.JsonModels.MinecraftJsonModels;
using BedrockService.Shared.PackParser;
using Newtonsoft.Json;
using System.Text;
using static BedrockService.Shared.Classes.SharedStringBase;

namespace BedrockService.Service.Networking.NetworkStrategies {
    public class PackList : IMessageParser {

        private readonly IServiceConfiguration _serviceConfiguration;
        private readonly IProcessInfo _processInfo;
        private readonly IServerLogger _logger;

        public PackList(IProcessInfo processInfo, IServiceConfiguration serviceConfiguration, IServerLogger logger) {
            _logger = logger;
            _serviceConfiguration = serviceConfiguration;
            _processInfo = processInfo;
        }

        public (byte[] data, byte srvIndex, NetworkMessageTypes type) ParseMessage(byte[] data, byte serverIndex) {
            string knownPackFileLocation = $@"{_serviceConfiguration.GetServerInfoByIndex(serverIndex).GetSettingsProp(ServerPropertyKeys.ServerPath)}\valid_known_packs.json";
            string pathToWorldFolder = $@"{_serviceConfiguration.GetServerInfoByIndex(serverIndex).GetSettingsProp(ServerPropertyKeys.ServerPath)}\worlds\{_serviceConfiguration.GetServerInfoByIndex(serverIndex).GetProp(BmsDependServerPropKeys.LevelName)}";
            MinecraftKnownPacksClass knownPacks = new(knownPackFileLocation, pathToWorldFolder);
            List<MinecraftPackContainer> list = new();
            foreach (KnownPacksJsonModel pack in knownPacks.InstalledPacks.Contents) {
                MinecraftPackParser currentParser = new();
                if (_serviceConfiguration.GetServerInfoByIndex(serverIndex).GetServerArch() == MinecraftServerArch.LiteLoader) {
                    pack.path = pack.path.Insert(0, "development_");
                }
                string packDir = $@"{_serviceConfiguration.GetServerInfoByIndex(serverIndex).GetSettingsProp(ServerPropertyKeys.ServerPath)}\{pack.path.Replace(@"/", @"\")}";
                currentParser.ParseDirectory(packDir);
                list.AddRange(currentParser.FoundPacks);
            }
            string arrayString = JsonConvert.SerializeObject(list);
            return (Encoding.UTF8.GetBytes(arrayString), 0, NetworkMessageTypes.PackList);
        }
    }
}