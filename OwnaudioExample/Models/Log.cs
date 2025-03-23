using System;

namespace OwnaAvalonia.Models
{
    public readonly struct Log
    {
        public Log(string message, LogType type)
        {
            Date = DateTime.Now;
            Message = message;
            Type = type;
        }

        public string Message { get; }

        public DateTime Date { get; }

        public LogType Type { get; }

        public override string ToString()
        {
            return $"[{Type}] [{Date:MM/dd/yyyy HH:mm:ss}] - {Message}";
        }

        public enum LogType
        {
            Info,
            Warning,
            Error
        }
    }
}
