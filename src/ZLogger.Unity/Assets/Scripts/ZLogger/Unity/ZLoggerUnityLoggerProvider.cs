#nullable disable

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Options;
using System;
using System.Threading.Tasks;
using UnityEngine;
using ZLogger.Providers;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using Object = UnityEngine.Object;

namespace ZLogger.Providers
{
    [ProviderAlias("ZLoggerUnity")]
    public class ZLoggerUnityLoggerProvider : ILoggerProvider
    {
        // HACK: pass context and then check it in IsProviderContext to avoid echoing custom logs twice
        //. (because we also promote custom logs that are > Debug to Unity Debug.Log)
        private static Object _providerContext;
        private static Object GetProviderContext()
        {
            if (ReferenceEquals(_providerContext, null))
                _providerContext = new Object();
            return _providerContext;
        }
        public static bool IsProviderContext(Object context)
        {
            return ReferenceEquals(context, _providerContext);
        }

        UnityDebugLogProcessor debugLogProcessor;

        public ZLoggerUnityLoggerProvider(IOptions<ZLoggerOptions> options)
        {
            this.debugLogProcessor = new UnityDebugLogProcessor(options.Value, GetProviderContext());
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new AsyncProcessZLogger(categoryName, debugLogProcessor, false);
        }

        public void Dispose()
        {
        }
    }

    public class UnityDebugLogProcessor : IAsyncLogProcessor
    {
        readonly ZLoggerOptions options;
        readonly Object providerContext;

        public UnityDebugLogProcessor(ZLoggerOptions options, Object providerContext)
        {
            this.options = options;
            this.providerContext = providerContext;
        }

        public ValueTask DisposeAsync()
        {
            return default;
        }

        [HideInCallstack]
        public void Post(IZLoggerEntry log)
        {
            try
            {
                var msg = log.FormatToString(options, null);
                switch (log.LogInfo.LogLevel)
                {
                    case LogLevel.Trace:
                    case LogLevel.Debug:
                    case LogLevel.Information:
                        UnityEngine.Debug.Log(msg, providerContext);
                        break;
                    case LogLevel.Warning:
                        UnityEngine.Debug.LogWarning(msg, providerContext);
                        break;
                    case LogLevel.Error:
                    case LogLevel.Critical:
                        if (log.LogInfo.Exception != null)
                        {
                            UnityEngine.Debug.LogException(log.LogInfo.Exception, providerContext);
                        }
                        else
                        {
                            UnityEngine.Debug.LogError(msg, providerContext);
                        }

                        break;
                    case LogLevel.None:
                        break;
                    default:
                        break;
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e, providerContext);
            }
            finally
            {
                log.Return();
            }
        }
    }
}

namespace ZLogger
{
    public static class ZLoggerUnityExtensions
    {
        public static ILoggingBuilder AddZLoggerUnityDebug(this ILoggingBuilder builder)
        {
            builder.AddConfiguration();

            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, ZLoggerUnityLoggerProvider>(x => new ZLoggerUnityLoggerProvider(x.GetService<IOptions<ZLoggerOptions>>())));
            LoggerProviderOptions.RegisterProviderOptions<ZLoggerOptions, ZLoggerUnityLoggerProvider>(builder.Services);

            return builder;
        }

        public static ILoggingBuilder AddZLoggerUnityDebug(this ILoggingBuilder builder, Action<ZLoggerOptions> configure)
        {
            if (configure == null)
            {
                throw new ArgumentNullException(nameof(configure));
            }

            builder.AddZLoggerUnityDebug();
            builder.Services.Configure(configure);

            return builder;
        }
    }
}
