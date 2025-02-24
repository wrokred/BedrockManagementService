﻿using BedrockService.Service.Networking.Interfaces;
using System.Text;

namespace BedrockService.Service.Networking.NetworkStrategies {
    public class ConsoleLogUpdate : IMessageParser {

        private readonly IServerLogger _logger;
        private readonly IBedrockService _service;
        private readonly IServiceConfiguration _serviceConfiguration;

        public ConsoleLogUpdate(IServerLogger logger, IServiceConfiguration serviceConfiguration, IBedrockService service) {
            _service = service;
            _logger = logger;

            _serviceConfiguration = serviceConfiguration;
        }

        public (byte[] data, byte srvIndex, NetworkMessageTypes type) ParseMessage(byte[] data, byte serverIndex) {
            string stringData = Encoding.UTF8.GetString(data, 5, data.Length - 5);
            StringBuilder srvString = new();
            string[] split = stringData.Split('|');
            for (int i = 0; i < split.Length; i++) {
                string[] dataSplit = split[i].Split(';');
                string srvName = dataSplit[0];
                int srvTextLen;
                int clientCurLen;
                int loop;
                IServerLogger srvText;
                if (srvName != "Service") {
                    try {
                        srvText = _service.GetBedrockServerByName(srvName).GetLogger();
                    } catch (NullReferenceException) {
                        break;
                    }
                    srvTextLen = srvText.Count();
                    clientCurLen = int.Parse(dataSplit[1]);
                    loop = clientCurLen;
                    while (loop < srvTextLen) {
                        srvString.Append($"{srvName};{srvText.FromIndex(loop)};{loop}|");
                        loop++;
                    }

                } else {
                    srvTextLen = _serviceConfiguration.GetLog().Count;
                    clientCurLen = int.Parse(dataSplit[1]);
                    loop = clientCurLen;
                    while (loop < srvTextLen) {
                        srvString.Append($"{srvName};{_logger.FromIndex(loop)};{loop}|");
                        loop++;
                    }
                }
            }
            if (srvString.Length > 1) {
                srvString.Remove(srvString.Length - 1, 1);
                return (Encoding.UTF8.GetBytes(srvString.ToString()), 0, NetworkMessageTypes.ConsoleLogUpdate);
            }
            return (Array.Empty<byte>(), 0, 0);
        }
    }
}

