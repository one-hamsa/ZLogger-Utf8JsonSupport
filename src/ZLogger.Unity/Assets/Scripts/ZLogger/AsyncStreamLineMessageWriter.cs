using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UnityEngine;
using ZLogger.Entries;
using Debug = UnityEngine.Debug;

namespace ZLogger
{
    public class AsyncStreamLineMessageWriter : IAsyncLogProcessor, IAsyncDisposable
    {
        readonly byte[] newLine;
        readonly bool crlf;
        readonly byte newLine1;
        readonly byte newLine2;

        readonly Stream stream;
        readonly Channel<IZLoggerEntry> channel;
        readonly Task writeLoop;
        readonly Task                    summaryWriteLoop;
        readonly ZLoggerOptions options;
        readonly CancellationTokenSource cancellationTokenSource;

        public AsyncStreamLineMessageWriter(Stream stream, ZLoggerOptions options)
        {
            this.newLine = Encoding.UTF8.GetBytes(Environment.NewLine);
            this.cancellationTokenSource = new CancellationTokenSource();
            if (newLine.Length == 1)
            {
                // cr or lf
                this.newLine1 = newLine[0];
                this.newLine2 = default;
                this.crlf = false;
            }
            else
            {
                // crlf(windows)
                this.newLine1 = newLine[0];
                this.newLine2 = newLine[1];
                this.crlf = true;
            }

            this.options = options;
            this.stream = stream;
            this.channel = Channel.CreateUnbounded<IZLoggerEntry>(new UnboundedChannelOptions
            {
                AllowSynchronousContinuations = false, // always should be in async loop.
                SingleWriter = false,
                SingleReader = true,
            });

            this.writeLoop = Task.Run(WriteLoop).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    // Log but don't crash
                    Debug.LogException(t.Exception);
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
            this.summaryWriteLoop = Task.Run(SummaryWriteLoop).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    // Log but don't crash
                    Debug.LogException(t.Exception);
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private const bool ENABLE_SPAM_DROPPER  = true;
        private const bool DEBUG_SPAM           = false;
        private const uint LIMIT_IN             = 200;
        private const uint LIMIT_OUT            = 150;
        private const uint POSTS_SECONDS_WINDOW = 5;
        private       bool isSpamming;
        private       bool didDrop;

        private readonly ConcurrentDictionary<LogLevel, int> dropSummary = new(new KeyValuePair<LogLevel, int>[]
        {
            new(LogLevel.Trace, 0),
            new(LogLevel.Debug, 0),
            new(LogLevel.Information, 0),
            new(LogLevel.Warning, 0),
            new(LogLevel.Error, 0),
            new(LogLevel.Critical, 0),
            new(LogLevel.None, 0),
        });

        private readonly ConcurrentQueue<DateTimeOffset> postTimesQ                  = new();
        private readonly ConcurrentQueue<DateTimeOffset> postTimesTempQ              = new();
        private readonly ArrayBufferWriter<byte>         bufferWriterForDebugSpamMsg = new();

        private static readonly object lockObject = new();

        [MethodImpl(MethodImplOptions.AggressiveInlining), HideInCallstack]
        public void Post(IZLoggerEntry log)
        {
#pragma warning disable 162
            if (!ENABLE_SPAM_DROPPER)
            {
                channel.Writer.TryWrite(log);
                return;
            }
#pragma warning restore 162

            lock (lockObject)
            {
                CheckPostsTimesAndSetIsSpamming();

                if (isSpamming)
                {
                    if (DEBUG_SPAM)
                    {
#pragma warning disable 162
                        var logInfo = new LogInfo(
                            log.LogInfo.LogId,
                            "ZLogger",
                            log.LogInfo.Timestamp,
                            log.LogInfo.LogLevel,
                            log.LogInfo.EventId,
                            log.LogInfo.Exception
                        );

                        bufferWriterForDebugSpamMsg.Clear();
                        bufferWriterForDebugSpamMsg.Write(Encoding.UTF8.GetBytes("\""));
                        log.FormatUtf8(bufferWriterForDebugSpamMsg, new(), null);
                        bufferWriterForDebugSpamMsg.Write(Encoding.UTF8.GetBytes("\""));
                        string logStr = Encoding.UTF8.GetString(bufferWriterForDebugSpamMsg.WrittenSpan);

                        IZLoggerEntry? entry =
                            FormatLogState<object, int, int, int, int, int, int, int, bool, int, string>.Factory(
                                new(
                                    null,
                                    "LOG DROPPED {9}, postTimes.Count {8} did Drop: {7}. Dropping: ({0}) Trace. ({1}) Debug. ({2})  Information. ({3})  Warning. ({4})  Error. ({5})  Critical. ({6})  None",
                                    dropSummary[LogLevel.Trace],
                                    dropSummary[LogLevel.Debug],
                                    dropSummary[LogLevel.Information],
                                    dropSummary[LogLevel.Warning],
                                    dropSummary[LogLevel.Error],
                                    dropSummary[LogLevel.Critical],
                                    dropSummary[LogLevel.None],
                                    didDrop,
                                    postTimesQ.Count,
                                    logStr
                                ),
                                logInfo
                            );

                        channel.Writer.TryWrite(entry);
#pragma warning restore 162
                    }

                    if (log.LogInfo.LogLevel is not LogLevel.Trace) {
                        dropSummary[log.LogInfo.LogLevel]++;
                        didDrop = true;
                    }
                }
                else
                {
                    if (channel.Writer.TryWrite(log))
                        if (log.LogInfo.LogLevel is not LogLevel.Trace)
                            postTimesQ.Enqueue(log.LogInfo.Timestamp);
                }
            }
        }

        private bool CheckPostsTimesAndSetIsSpamming()
        {
            // fixes the issue where seeing summary without the actual logs
            // maybe we can put the lock further down the PostTimesCheck method
            lock (lockObject)
            {
                bool? spamming = PostsTimesCheck();
                if (spamming.HasValue)
                    lock (lockObject)
                        isSpamming = spamming.Value;

                return isSpamming;
            }
        }

        /// <summary>
        /// Check if the count in the <see cref="postTimesQ"/> are valid and within the time window <see cref="POSTS_SECONDS_WINDOW"/>
        /// Clears the <see cref="postTimesQ"/> from invalid entries (older than the time window)
        /// </summary>
        /// <returns>
        /// <ul>
        /// <li> True if passed the spamming rate top limit <see cref="LIMIT_IN"/> </li>
        /// <li> False if under the minimum to get out of spamming mode <see cref="LIMIT_OUT"/> </li>
        /// <li> Null if still in the gray area (not spamming, but not out of spamming mode) </li>
        /// </ul>
        /// </returns>
        /// <exception cref="Exception">Only when <see cref="DEBUG_SPAM"/> is true</exception>
        private bool? PostsTimesCheck()
        {
            DateTimeOffset currentDateTime = DateTimeOffset.Now;

#pragma warning disable 162
            if (DEBUG_SPAM && postTimesTempQ.Count > 0)
                throw new Exception("postTimesQH.Count > 0");
#pragma warning restore 162

            postTimesTempQ.Clear(); // technically redundant

            while (postTimesQ.TryDequeue(out var postTime))
            {
                TimeSpan timeDiff = currentDateTime - postTime;

                if (timeDiff <= TimeSpan.FromSeconds(POSTS_SECONDS_WINDOW))
                {
                    postTimesTempQ.Enqueue(postTime);
                }
            }

#pragma warning disable 162
            if (DEBUG_SPAM && postTimesQ.Count > 0)
                throw new Exception("postTimesQ.Count > 0");
#pragma warning restore 162

            postTimesQ.Clear(); // technically redundant
            while (postTimesTempQ.TryDequeue(out var postTime))
            {
                postTimesQ.Enqueue(postTime);
            }

            if (postTimesQ.Count >= LIMIT_IN) return true;

            if (postTimesQ.Count <= LIMIT_OUT) return false;

            return null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void AppendLine(StreamBufferWriter writer)
        {
            if (writer.TryGetForNewLine(out var buffer, out var index))
            {
                if (crlf)
                {
                    buffer[index] = newLine1;
                    buffer[index + 1] = newLine2;
                    writer.Advance(2);
                }
                else
                {
                    buffer[index] = newLine1;
                    writer.Advance(1);
                }
            }
            else
            {
                var span = writer.GetSpan(newLine.Length);
                newLine.CopyTo(span);
            }
        }

        private static object allThreadsLock = new();

        async Task WriteLoop()
        {
            var writer = new StreamBufferWriter(stream);
            var reader = channel.Reader;
            var sw = Stopwatch.StartNew();
            IZLoggerEntry? value = default;
            try
            {
                while (await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    LogInfo info = default;
                    try
                    {
                        while (reader.TryRead(out value))
                        {
                            // XXX this solves the problem of corruption when alternating threads but I'm not sure why (OP)
                            lock (allThreadsLock)
                            {
                                info = value.LogInfo;

                                // OP: try to rewrite event id from payload if exists (see ILogEvent.cs)
                                var payload = value.GetPayload();
                                if (payload is ILogEvent logEvent)
                                    value.LogInfo
                                        = new LogInfo(info.LogId, info.CategoryName, info.Timestamp, info.LogLevel,
                                            logEvent.GetEventId(), info.Exception);

                                if (options.EnableStructuredLogging)
                                {
                                    var jsonWriter = options.GetThreadStaticUtf8JsonWriter(writer);
                                    try
                                    {
                                        jsonWriter.WriteStartObject();

                                        value.FormatUtf8(writer, options, jsonWriter);
                                        value.Return();

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
                                    value.FormatUtf8(writer, options, null);
                                    value.Return();
                                }

                                AppendLine(writer);
                            }

                            info = default;

                            if (options.FlushRate != null && !cancellationTokenSource.IsCancellationRequested)
                            {
                                sw.Stop();
                                var sleepTime = options.FlushRate.Value - sw.Elapsed;
                                if (sleepTime > TimeSpan.Zero)
                                {
                                    try
                                    {
                                        await Task.Delay(sleepTime, cancellationTokenSource.Token)
                                            .ConfigureAwait(false);
                                    }
                                    catch (OperationCanceledException)
                                    {
                                    }
                                }
                            }

                            writer.Flush(); // flush before wait.

                            sw.Reset();
                            sw.Start();
                        }
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            if (options.InternalErrorLogger != null)
                            {
                                options.InternalErrorLogger(info, ex, value);
                            }
                            else
                            {
                                Console.WriteLine(ex);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                try
                {
                    writer.Flush();
                }
                catch
                {
                    // ignored
                }
            }
        }

        private async Task SummaryWriteLoop()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationTokenSource.Token)
                        .ConfigureAwait(false);

                    lock (lockObject)
                    {
                        if (!didDrop) continue;

                        try
                        {
                            CheckPostsTimesAndSetIsSpamming();
                        }
                        catch
                        {
                            // ignored
                        }

                        if (isSpamming) continue;

                        // create summary log entry
                        try
                        {
                            int logId = Interlocked.Increment(ref AsyncProcessZLogger.globalLogId);

                            Exception? exception = null; // can add an exception new Exception("Log spamming detected")
                            var logInfo = new LogInfo(
                                logId,
                                "ZLogger",
                                DateTimeOffset.Now,
                                LogLevel.Critical,
                                new EventId(0),
                                exception
                            );

                            int droppedCount = dropSummary[LogLevel.Critical]
                                               + dropSummary[LogLevel.Error]
                                               + dropSummary[LogLevel.Warning]
                                               + dropSummary[LogLevel.Information]
                                               + dropSummary[LogLevel.Debug]
                                               + dropSummary[LogLevel.Trace];

                            IZLoggerEntry? entry =
                                FormatLogState<object, int, int, int, int, int, int, int, uint, uint>.Factory(
                                    new
                                    (
                                        null,
                                        "Truncated {0} log messages (Critical: {1}, Error: {2}, Warning: {3}, Information: {4}, Debug: {5}, Trace: {6}) because had more than {7} logs in {8} seconds",
                                        droppedCount,
                                        dropSummary[LogLevel.Critical],
                                        dropSummary[LogLevel.Error],
                                        dropSummary[LogLevel.Warning],
                                        dropSummary[LogLevel.Information],
                                        dropSummary[LogLevel.Debug],
                                        dropSummary[LogLevel.Trace],
                                        LIMIT_IN,
                                        POSTS_SECONDS_WINDOW
                                    ),
                                    logInfo
                                );

                            channel.Writer.TryWrite(entry);

                            // reset drop summary
                            foreach (var key in dropSummary.Keys)
                            {
                                dropSummary[key] = 0;
                            }
                        }
                        catch
                        {
                            // ignored
                        }
                        finally
                        {
                            didDrop = false;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected when disposing
            }
            catch (Exception ex)
            {
                try
                {
                    if (options.InternalErrorLogger != null)
                    {
                        options.InternalErrorLogger(default, ex, null);
                    }
                    else
                    {
                        Console.WriteLine(ex);
                    }
                }
                catch { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                channel.Writer.Complete();
                cancellationTokenSource.Cancel();
                await writeLoop.ConfigureAwait(false);
                await summaryWriteLoop.ConfigureAwait(false);
            }
            finally
            {
                this.stream.Dispose();
            }
        }
    }
}
