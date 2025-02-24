using BedrockService.Shared.Classes;
using BedrockService.Shared.JsonModels.MinecraftJsonModels;
using BedrockService.Shared.Utilities;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace BedrockService.Shared.PackParser {
    public class MinecraftPackParser {
        public string PackExtractDirectory;
        public List<MinecraftPackContainer> FoundPacks = new();

        [JsonConstructor]
        public MinecraftPackParser() {
        }

        public MinecraftPackParser(byte[] fileContents) {
            PackExtractDirectory = $"{Path.GetTempPath()}\\BMSTemp";
            FileUtilities.ClearTempDir().Wait();
            using (MemoryStream fileStream = new(fileContents, 5, fileContents.Length - 5)) {
                using ZipArchive zipArchive = new(fileStream, ZipArchiveMode.Read);
                zipArchive.ExtractToDirectory(PackExtractDirectory);
            }
            ParseDirectory(PackExtractDirectory);
        }

        public MinecraftPackParser(string[] files, string extractDir) {
            PackExtractDirectory = extractDir;
            FileUtilities.ClearTempDir().Wait();
            if (Directory.Exists($@"{PackExtractDirectory}\ZipTemp")) {
                Directory.CreateDirectory($@"{PackExtractDirectory}\ZipTemp");
            }
            foreach (string file in files) {
                FileInfo fInfo = new(file);
                string zipFilePath = $@"{PackExtractDirectory}\{fInfo.Name.Replace(fInfo.Extension, "")}";
                ZipFile.ExtractToDirectory(file, zipFilePath);
                foreach (FileInfo extractedFile in new DirectoryInfo(zipFilePath).GetFiles()) {
                    if (extractedFile.Extension == ".mcpack") {
                        Directory.CreateDirectory($@"{zipFilePath}\{extractedFile.Name.Replace(extractedFile.Extension, "")}");
                        ZipFile.ExtractToDirectory(extractedFile.FullName, $@"{zipFilePath}\{fInfo.Name.Replace(fInfo.Extension, "")}_{extractedFile.Name.Replace(extractedFile.Extension, "")}");
                    }
                }
            }
            ParseDirectory(PackExtractDirectory);
        }

        public void ParseDirectory(string directoryToParse) {
            DirectoryInfo directoryInfo = new(directoryToParse);
            if (directoryInfo.Exists) {
                foreach (FileInfo file in directoryInfo.GetFiles("*", SearchOption.AllDirectories)) {
                    if (file.Name == "levelname.txt") {
                        byte[] iconBytes;
                        try {
                            if (File.Exists($@"{file.Directory.FullName}\world_icon.jpeg"))
                                iconBytes = File.ReadAllBytes($@"{file.Directory.FullName}\world_icon.jpeg");
                            else
                                iconBytes = File.ReadAllBytes($@"{file.Directory.FullName}\world_icon.png");
                        } catch {
                            iconBytes = null;
                        }

                        FoundPacks.Add(new MinecraftPackContainer {
                            JsonManifest = null,
                            PackContentLocation = file.Directory.FullName,
                            ManifestType = "WorldPack",
                            FolderName = file.Directory.Name,
                            IconBytes = iconBytes
                        });
                        return;
                    }
                    if (file.Name == "manifest.json") {
                        byte[] iconBytes = File.Exists($@"{file.Directory.FullName}\pack_icon.jpeg") ?
                            File.ReadAllBytes($@"{file.Directory.FullName}\pack_icon.jpeg") :
                            File.Exists($@"{file.Directory.FullName}\pack_icon.png") ?
                            File.ReadAllBytes($@"{file.Directory.FullName}\pack_icon.png") :
                            null;
                        if (File.ReadAllText(file.FullName).Contains("-beta")) {
                            break;
                        }
                        MinecraftPackContainer container = new() {
                            JsonManifest = new(),
                            PackContentLocation = file.Directory.FullName,
                            ManifestType = "",
                            FolderName = file.Directory.Name,
                            IconBytes = iconBytes
                        };
                        container.JsonManifest = System.Text.Json.JsonSerializer.Deserialize<PackManifestJsonModel>(File.ReadAllText(file.FullName));
                        container.JsonManifest = JsonConvert.DeserializeObject<PackManifestJsonModel>(File.ReadAllText(file.FullName), SharedStringBase.GlobalJsonSerialierSettings);
                        container.ManifestType = container.JsonManifest.modules[0].type;
                        FoundPacks.Add(container);
                    }
                }
            }
        }
    }
}
