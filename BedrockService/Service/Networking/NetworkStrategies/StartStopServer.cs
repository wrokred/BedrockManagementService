﻿using BedrockService.Service.Networking.Interfaces;
using BedrockService.Service.Server.Interfaces;
using BedrockService.Shared.SerializeModels;

namespace BedrockService.Service.Networking.NetworkStrategies {
    public class StartStopServer : IMessageParser {
        private readonly IBedrockService _service;

        public StartStopServer(IBedrockService service) {
            _service = service;
        }

        public (byte[] data, byte srvIndex, NetworkMessageTypes type) ParseMessage(byte[] data, byte serverIndex) {
            IServerController server = _service.GetBedrockServerByIndex(serverIndex);
            if (server.GetServerStatus().ServerStatus == ServerStatus.Started) {
                server.ServerStop(true).Wait();
            } else if (server.GetServerStatus().ServerStatus == ServerStatus.Stopped) {
                server.ServerStart().Wait();
                server.StartWatchdog();
            }
            return (Array.Empty<byte>(), 0, NetworkMessageTypes.UICallback);
        }
    }
}
