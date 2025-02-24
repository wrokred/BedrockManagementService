﻿using BedrockService.Service.Networking.Interfaces;
using Newtonsoft.Json;
using System.Text;

namespace BedrockService.Service.Networking.NetworkStrategies {
    public class PlayerRequest : IMessageParser {

        private readonly IBedrockService _service;

        public PlayerRequest(IBedrockService service) {

            _service = service;
        }

        public (byte[] data, byte srvIndex, NetworkMessageTypes type) ParseMessage(byte[] data, byte serverIndex) {
            byte[] serializeToBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(_service.GetBedrockServerByIndex(serverIndex).GetPlayerManager().GetPlayerList(), Formatting.Indented, SharedStringBase.GlobalJsonSerialierSettings));
            return (serializeToBytes, serverIndex, NetworkMessageTypes.PlayersRequest);
        }
    }
}

