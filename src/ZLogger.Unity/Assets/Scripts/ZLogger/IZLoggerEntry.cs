using Cysharp.Text;
using System;
using System.Buffers;
using System.Text.Json;

namespace ZLogger
{
    public interface IZLoggerEntry
    {
        LogInfo LogInfo { get; set; }
        void FormatUtf8(IBufferWriter<byte> writer, ZLoggerOptions options, Utf8JsonWriter? jsonWriter);
        void SwitchCasePayload<TPayload>(Action<IZLoggerEntry, TPayload, object?> payloadCallback, object? state);
        object? GetPayload();
        void Return();
    }

    public static class ZLoggerEntryExtensions
    {
        public static string FormatToString(this IZLoggerEntry entry, ZLoggerOptions options)
        {
            var boxedBuilder = (IBufferWriter<byte>)ZString.CreateUtf8StringBuilder();
            try
            {
                var value = entry;

                if (options.EnableStructuredLogging)
                {
                    var jsonWriter = options.GetThreadStaticUtf8JsonWriter(boxedBuilder);
                    var info = value.LogInfo;
                    var payload = value.GetPayload();
                    if (payload is ILogEvent logEvent)
                        value.LogInfo
                            = new LogInfo(info.LogId, info.CategoryName, info.Timestamp, info.LogLevel,
                                logEvent.GetEventId(), info.Exception);
                    
                    try
                    {
                        jsonWriter.WriteStartObject();

                        value.FormatUtf8(boxedBuilder, options, jsonWriter);

                        jsonWriter.WriteEndObject();
                        jsonWriter.Flush();
                    }
                    finally
                    {
                        jsonWriter.Reset();
                    }
                }
                else
                {
                    value.FormatUtf8(boxedBuilder, options, null);
                }
                return boxedBuilder.ToString()!;
            }
            finally
            {
                ((Utf8ValueStringBuilder)boxedBuilder).Dispose();
            }
        }
    }
}