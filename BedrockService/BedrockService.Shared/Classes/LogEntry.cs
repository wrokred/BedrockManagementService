﻿using System;

namespace BedrockService.Shared.Classes {
    public class LogEntry {
        public long TimeStamp { get; set; }
        public string Text { get; set; }


        public LogEntry(string entryText) {
            Text = entryText;
            TimeStamp = DateTime.Now.Ticks;
        }
    }
}
