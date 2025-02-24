﻿using BedrockService.Service.Networking.Interfaces;
using BedrockService.Shared.SerializeModels;
using Newtonsoft.Json;
using System.Text;

namespace BedrockService.Service.Networking.NetworkStrategies {
    public class ServerStatusRequest : IMessageParser {
        private readonly IBedrockService _service;
        private readonly IServiceConfiguration _serviceConfiguration;

        public ServerStatusRequest(IBedrockService service, IServiceConfiguration serviceConfiguration) {
            _service = service;
            _serviceConfiguration = serviceConfiguration;
        }

        public (byte[] data, byte srvIndex, NetworkMessageTypes type) ParseMessage(byte[] data, byte serverIndex) {
            StatusUpdateModel model = new();
            model.ServiceStatusModel = _service.GetServiceStatus();
            byte[] serializeToBytes = Array.Empty<byte>();
            if (serverIndex != 255) {
                model.ServerStatusModel = _service.GetBedrockServerByIndex(serverIndex).GetServerStatus();
            }
            serializeToBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(model, Formatting.Indented, SharedStringBase.GlobalJsonSerialierSettings));
            return (serializeToBytes, serverIndex, NetworkMessageTypes.ServerStatusRequest);
        }
    }
}
