﻿using BedrockService.Shared.Interfaces;
using BedrockService.Shared.SerializeModels;
using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using static BedrockService.Shared.Classes.SharedStringBase;

namespace BedrockService.Shared.Classes {
    public class MinecraftServerLogger : IServerLogger {
        private readonly IBedrockConfiguration _serviceConfiguration;
        private readonly IProcessInfo _processInfo;
        private readonly IServerConfiguration _serverConfiguration;
        [NonSerialized]
        private StreamWriter _logWriter;
        private bool _logToFile = false;
        private string _logPath;
        private string _logOwner = "Service";
        private Logger _nLogger;

        public MinecraftServerLogger(IProcessInfo processInfo, IBedrockConfiguration serviceConfiguration, IServerConfiguration serverConfiguration = null) {
            _serviceConfiguration = serviceConfiguration;
            _serverConfiguration = serverConfiguration;
            _processInfo = processInfo;
            _nLogger = NLogManager.Instance.GetLogger("BMSLogger");
        }

        [JsonConstructor]
        public MinecraftServerLogger() {
            _logToFile = false;
        }

        public void Initialize() {
            _logPath = $@"{_processInfo.GetDirectory()}\Logs\{_processInfo.DeclaredType()}";
            _logToFile = _serviceConfiguration.GetProp(ServicePropertyKeys.LogApplicationOutput).GetBoolValue();
            _logOwner = _processInfo.DeclaredType();
            if (_processInfo.DeclaredType() == "Service") {
                if (_serverConfiguration != null && _serviceConfiguration.GetProp(ServicePropertyKeys.LogServerOutput).GetBoolValue()) {
                    _logOwner = _serverConfiguration.GetServerName();
                    string serverLogPath = $@"{_processInfo.GetDirectory()}\Logs\Servers\{_logOwner}";
                    if (!Directory.Exists(serverLogPath))
                        Directory.CreateDirectory(serverLogPath);
                    _logWriter = new StreamWriter($@"{serverLogPath}\ServerLog-{DateTime.Now:yyyyMMdd_HHmmssff}.log", true);
                    return;
                }
            }
            if (_logWriter == null) {
                if (!Directory.Exists(_logPath))
                    Directory.CreateDirectory(_logPath);
                _logWriter = new StreamWriter($@"{_logPath}\{_processInfo.DeclaredType()}Log-{DateTime.Now:yyyyMMdd_HHmmssff}.log", true);
            }

        }

        public void AppendLine(string text) {
            string newText = $"{_logOwner}: {text}";
            LogEntry entry = new(newText);
            if (_serviceConfiguration.GetAllProps().Count > 0 && _serviceConfiguration.GetProp(ServicePropertyKeys.TimestampLogEntries).GetBoolValue()) {
                newText = $"[{entry.TimeStamp:G}] {newText}";
            }
            if (_processInfo.IsDebugEnabled()) {
                _nLogger.Info(newText);
            } else {
                Console.WriteLine(newText);
            }
            try {
                if (_serverConfiguration != null) {
                    _serverConfiguration.GetLog().Add(entry);
                } else {
                    if (_serviceConfiguration != null) {
                        _serviceConfiguration.GetLog().Add(entry);
                    }
                }
                if (_logToFile && _logWriter != null) {
                    _logWriter.WriteLine(newText);
                    _logWriter.Flush();
                }
            } catch {
            }
        }

        public void AppendErrorFromException(Exception exception) {
            if (_processInfo.IsDebugEnabled()) {
                string addText = $"{exception.GetType().Name} occured in {exception.TargetSite.DeclaringType.Name}:{exception.TargetSite.Name}\n{exception.Message}\n{exception.StackTrace}";
                AppendLine(addText);
            }
        }

        public int Count() {
            return _serverConfiguration != null ?
            _serverConfiguration.GetLog().Count :
            _serviceConfiguration.GetLog().Count;
        }

        public string FromIndex(int index) {
            return _serverConfiguration != null ?
            _serverConfiguration.GetLog()[index].Text :
            _serviceConfiguration.GetLog()[index].Text;
        }

        public override string ToString() {
            if (_serverConfiguration != null) {
                return ProcessText(_serverConfiguration.GetLog());
            }
            return ProcessText(_serviceConfiguration.GetLog());
        }

        public LogFactory GetNLogFactory() => NLogManager.Instance;

        private string ProcessText(List<LogEntry> list) {
            if (_serviceConfiguration.GetProp(ServicePropertyKeys.TimestampLogEntries).GetBoolValue()) {
                return string.Join("\r\n", list.Select(x => $"[{x.TimeStamp:G}] {x.Text}").ToList());
            }
            return string.Join("\r\n", list.Select(x => x.Text).ToList());
        }
    }


    internal class NLogManager {
        // A Logger dispenser for the current assembly (Remember to call Flush on application exit)
        public static LogFactory Instance { get { return _instance.Value; } }
        private static Lazy<LogFactory> _instance = new Lazy<LogFactory>(BuildLogFactory);

        // 
        // Use a config file located next to our current assembly dll 
        // eg, if the running assembly is c:\path\to\MyComponent.dll 
        // the config filepath will be c:\path\to\MyComponent.nlog 
        // 
        // WARNING: This will not be appropriate for assemblies in the GAC 
        // 
        private static LogFactory BuildLogFactory() {
            // Use name of current assembly to construct NLog config filename 
            Assembly thisAssembly = Assembly.GetExecutingAssembly();
            var config = new NLog.Config.LoggingConfiguration();

            // Targets where to log to: File and Console
            string logDir = $@"{Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName)}\PreServiceLog.log";
            var logfile = new NLog.Targets.FileTarget("logfile") { FileName = logDir };
            var logconsole = new NLog.Targets.ConsoleTarget("logconsole");


            // Rules for mapping loggers to targets            
            config.AddRule(LogLevel.Info, LogLevel.Fatal, logconsole);
            config.AddRule(LogLevel.Info, LogLevel.Fatal, logfile);

            // Apply config           
            NLog.LogManager.Configuration = config;

            LogFactory logFactory = new() {
                Configuration = config
            };
            return logFactory;
        }
    }
}
