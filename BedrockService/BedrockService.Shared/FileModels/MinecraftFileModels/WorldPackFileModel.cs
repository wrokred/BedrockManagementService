﻿using BedrockService.Shared.Classes;
using BedrockService.Shared.JsonModels.MinecraftJsonModels;
using System.Collections.Generic;

namespace BedrockService.Shared.FileModels.MinecraftFileModels {
    public class WorldPackFileModel : BaseJsonFile {
        public List<WorldPackEntryJsonModel> Contents { get; set; } = new();

        public WorldPackFileModel(string fullPath) : base(fullPath) {
            Contents = LoadJsonFile<List<WorldPackEntryJsonModel>>();
            if (Contents == null) {
                Contents = new();
            }
        }

        public WorldPackFileModel() { }

        public void SaveFile() => SaveToFile(Contents);
    }
}
