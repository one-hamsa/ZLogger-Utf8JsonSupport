using Microsoft.Extensions.Logging;
using System;
using ZLogger.Bridge;
using System.Threading;

namespace ZLogger
{
    public readonly struct LogInfo
    {
        public readonly int LogId;
        public readonly string CategoryName;
        public readonly DateTimeOffset Timestamp;
        public readonly LogLevel LogLevel;
        public readonly EventId EventId;
        public readonly Exception? Exception;

        // Pre-captured exception chain, snapshotted at log-call time on the calling thread.
        // Accessing ex.StackTrace on the WriteLoop background thread triggers
        // mono_get_generic_context_from_stack_frame which SIGSEGV-crashes Mono when
        // the original call stack is no longer intact.
        readonly CapturedExceptionInfo? capturedExceptionInfo;

        public LogInfo(int logId, string categoryName, DateTimeOffset timestamp, LogLevel logLevel, EventId eventId,
            Exception? exception)
        {
            LogId = logId;
            EventId = eventId;
            CategoryName = categoryName;
            Timestamp = timestamp;
            LogLevel = logLevel;
            Exception = exception;
            capturedExceptionInfo = exception != null ? new CapturedExceptionInfo(exception) : null;
        }

        /// Returns a copy with a different EventId, preserving the pre-captured exception info.
        /// Use this instead of calling the public constructor when the exception was already captured
        /// on the original thread — avoids re-accessing ex.StackTrace on the background WriteLoop.
        internal LogInfo WithEventId(EventId eventId)
        {
            return new LogInfo(LogId, CategoryName, Timestamp, LogLevel, eventId, Exception, capturedExceptionInfo);
        }

        LogInfo(int logId, string categoryName, DateTimeOffset timestamp, LogLevel logLevel, EventId eventId,
            Exception? exception, CapturedExceptionInfo? existingCapturedInfo)
        {
            LogId = logId;
            EventId = eventId;
            CategoryName = categoryName;
            Timestamp = timestamp;
            LogLevel = logLevel;
            Exception = exception;
            capturedExceptionInfo = existingCapturedInfo;
        }

        static readonly JsonEncodedText CategoryNameText = JsonEncodedText.Encode(nameof(CategoryName));
        static readonly JsonEncodedText TimestampText = JsonEncodedText.Encode(nameof(Timestamp));
        static readonly JsonEncodedText LogLevelText = JsonEncodedText.Encode(nameof(LogLevel));
        static readonly JsonEncodedText EventIdText = JsonEncodedText.Encode(nameof(EventId));
        static readonly JsonEncodedText EventIdNameText = JsonEncodedText.Encode("EventIdName");
        static readonly JsonEncodedText ExceptionText = JsonEncodedText.Encode(nameof(Exception));
        static readonly JsonEncodedText LogIdText = JsonEncodedText.Encode("LogId");
        static readonly JsonEncodedText NameText = JsonEncodedText.Encode("Name");
        static readonly JsonEncodedText MessageText = JsonEncodedText.Encode("Message");
        static readonly JsonEncodedText StackTraceText = JsonEncodedText.Encode("StackTrace");
        static readonly JsonEncodedText InnerExceptionText = JsonEncodedText.Encode("InnerException");

        static readonly JsonEncodedText Trace = JsonEncodedText.Encode(nameof(LogLevel.Trace));
        static readonly JsonEncodedText Debug = JsonEncodedText.Encode(nameof(LogLevel.Debug));
        static readonly JsonEncodedText Information = JsonEncodedText.Encode(nameof(LogLevel.Information));
        static readonly JsonEncodedText Warning = JsonEncodedText.Encode(nameof(LogLevel.Warning));
        static readonly JsonEncodedText Error = JsonEncodedText.Encode(nameof(LogLevel.Error));
        static readonly JsonEncodedText Critical = JsonEncodedText.Encode(nameof(LogLevel.Critical));
        static readonly JsonEncodedText None = JsonEncodedText.Encode(nameof(LogLevel.None));

        public void WriteToJsonWriter(Utf8JsonWriter writer)
        {
            writer.WriteNumber(LogIdText, LogId);
            writer.WriteString(CategoryNameText, CategoryName);
            writer.WriteString(LogLevelText, LogLevelToEncodedText(LogLevel));
            writer.WriteNumber(EventIdText, EventId.Id);
            writer.WriteString(EventIdNameText, EventId.Name);
            writer.WriteDateTimeOffset(TimestampText, Timestamp);
            writer.WritePropertyName(ExceptionText);
            WriteException(writer, Exception, capturedExceptionInfo);
        }

        static void WriteException(Utf8JsonWriter writer, Exception? ex, CapturedExceptionInfo? captured)
        {
            if (ex == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartObject();
                {
                    writer.WriteString(NameText, ex.GetType().FullName);
                    writer.WriteString(MessageText, ex.Message);
                    // Use pre-captured stack trace to avoid calling ex.StackTrace on the background
                    // WriteLoop thread, which triggers Mono's native stack walker and can SIGSEGV.
                    writer.WriteString(StackTraceText, captured?.StackTrace);
                    writer.WritePropertyName(InnerExceptionText);
                    {
                        WriteException(writer, ex.InnerException, captured?.Inner);
                    }
                }
                writer.WriteEndObject();
            }
        }

        static JsonEncodedText LogLevelToEncodedText(LogLevel logLevel)
        {
            switch (logLevel)
            {
                case LogLevel.Trace:
                    return Trace;
                case LogLevel.Debug:
                    return Debug;
                case LogLevel.Information:
                    return Information;
                case LogLevel.Warning:
                    return Warning;
                case LogLevel.Error:
                    return Error;
                case LogLevel.Critical:
                    return Critical;
                case LogLevel.None:
                    return None;
                default:
                    return JsonEncodedText.Encode(((int)logLevel).ToString());
            }
        }

        // Eagerly captures the StackTrace string from an exception chain at log-call time,
        // while the calling thread's stack is still intact. This is a plain class so it
        // can hold a nullable reference to the inner chain without boxing in the struct.
        private sealed class CapturedExceptionInfo
        {
            public readonly string? StackTrace;
            public readonly CapturedExceptionInfo? Inner;

            public CapturedExceptionInfo(Exception ex)
            {
                try
                {
                    StackTrace = ex.StackTrace;
                }
                catch
                {
                    StackTrace = "<stack trace unavailable>";
                }

                Inner = ex.InnerException != null ? new CapturedExceptionInfo(ex.InnerException) : null;
            }
        }
    }
}
