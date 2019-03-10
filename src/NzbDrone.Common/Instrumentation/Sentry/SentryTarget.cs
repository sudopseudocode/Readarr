using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Data.SQLite;
using NLog;
using NLog.Common;
using NLog.Targets;
using NzbDrone.Common.EnvironmentInfo;
using System.Globalization;
using Sentry;
using Sentry.Protocol;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Common.Instrumentation.Sentry
{
    [Target("Sentry")]
    public class SentryTarget : TargetWithLayout
    {
        // don't report uninformative SQLite exceptions
        // busy/locked are benign https://forums.sonarr.tv/t/owin-sqlite-error-5-database-is-locked/5423/11
        // The others will be user configuration problems and silt up Sentry
        private static readonly HashSet<SQLiteErrorCode> FilteredSQLiteErrors = new HashSet<SQLiteErrorCode> {
            SQLiteErrorCode.Busy,
            SQLiteErrorCode.Locked,
            SQLiteErrorCode.Perm,
            SQLiteErrorCode.ReadOnly,
            SQLiteErrorCode.IoErr,
            SQLiteErrorCode.Corrupt,
            SQLiteErrorCode.Full,
            SQLiteErrorCode.CantOpen,
            SQLiteErrorCode.Auth
        };

        // use string and not Type so we don't need a reference to the project
        // where these are defined
        private static readonly HashSet<string> FilteredExceptionTypeNames = new HashSet<string> {
            // UnauthorizedAccessExceptions will just be user configuration issues
            "UnauthorizedAccessException",
            // Filter out people stuck in boot loops
            "CorruptDatabaseException"
        };

        private static readonly List<string> FilteredExceptionMessages = new List<string> {
            // Swallow the many, many exceptions flowing through from Jackett
            "Jackett.Common.IndexerException",
            // Fix openflixr being stupid with permissions
            "openflixr"
        };
        
        private static readonly IDictionary<LogLevel, SentryLevel> LoggingLevelMap = new Dictionary<LogLevel, SentryLevel>
        {
            {LogLevel.Debug, SentryLevel.Debug},
            {LogLevel.Error, SentryLevel.Error},
            {LogLevel.Fatal, SentryLevel.Fatal},
            {LogLevel.Info, SentryLevel.Info},
            {LogLevel.Trace, SentryLevel.Debug},
            {LogLevel.Warn, SentryLevel.Warning},
        };

        private static readonly IDictionary<LogLevel, BreadcrumbLevel> BreadcrumbLevelMap = new Dictionary<LogLevel, BreadcrumbLevel>
        {
            {LogLevel.Debug, BreadcrumbLevel.Debug},
            {LogLevel.Error, BreadcrumbLevel.Error},
            {LogLevel.Fatal, BreadcrumbLevel.Critical},
            {LogLevel.Info, BreadcrumbLevel.Info},
            {LogLevel.Trace, BreadcrumbLevel.Debug},
            {LogLevel.Warn, BreadcrumbLevel.Warning},
        };

        private readonly IDisposable _sdk;
        private bool _disposed;

        private readonly SentryDebounce _debounce;
        private bool _unauthorized;

        public bool FilterEvents { get; set; }
        
        public SentryTarget(string dsn)
        {
            _sdk = SentrySdk.Init(o =>
                                  {
                                      o.Dsn = new Dsn(dsn);
                                      o.AttachStacktrace = true;
                                      o.MaxBreadcrumbs = 200;
                                      o.SendDefaultPii = true;
                                      o.Debug = false;
                                      o.DiagnosticsLevel = SentryLevel.Debug;
                                      o.Environment = RuntimeInfo.IsProduction ? "production" : "development";
                                      o.Release = BuildInfo.Release;
                                      o.BeforeSend = x => SentryCleanser.CleanseEvent(x);
                                      o.BeforeBreadcrumb = x => SentryCleanser.CleanseBreadcrumb(x);
                                  });

            SentrySdk.ConfigureScope(scope =>
                                     {
                                         scope.User = new User {
                                             Username = HashUtil.AnonymousToken()
                                         };
                                         
                                         scope.SetTag("osfamily", OsInfo.Os.ToString());
                                         scope.SetTag("runtime", PlatformInfo.PlatformName);
                                         scope.SetTag("culture", Thread.CurrentThread.CurrentCulture.Name);
                                         scope.SetTag("branch", BuildInfo.Branch);
                                         scope.SetTag("version", BuildInfo.Version.ToString());
                                     });
            
            _debounce = new SentryDebounce();
        }

        private void OnError(Exception ex)
        {
            var webException = ex as WebException;

            if (webException != null)
            {
                var response = webException.Response as HttpWebResponse;
                var statusCode = response?.StatusCode;
                if (statusCode == HttpStatusCode.Unauthorized)
                {
                    _unauthorized = true;
                    _debounce.Clear();
                }
            }

            InternalLogger.Error(ex, "Unable to send error to Sentry");
        }

        private static List<string> GetFingerPrint(LogEventInfo logEvent)
        {
            if (logEvent.Properties.ContainsKey("Sentry"))
            {
                return ((string[])logEvent.Properties["Sentry"]).ToList();
            }

            var fingerPrint = new List<string>
            {
                logEvent.Level.Ordinal.ToString(),
                logEvent.LoggerName
            };

            var ex = logEvent.Exception;

            if (ex != null)
            {
                var exception = ex.GetType().Name;

                if (ex.InnerException != null)
                {
                    exception += ex.InnerException.GetType().Name;
                }

                fingerPrint.Add(exception);
            }

            return fingerPrint;
        }

        private bool IsSentryMessage(LogEventInfo logEvent)
        {
            if (logEvent.Properties.ContainsKey("Sentry"))
            {
                return logEvent.Properties["Sentry"] != null;
            }

            if (logEvent.Level >= LogLevel.Error && logEvent.Exception != null)
            {
                if (FilterEvents)
                {
                    var sqlEx = logEvent.Exception as SQLiteException;
                    if (sqlEx != null && FilteredSQLiteErrors.Contains(sqlEx.ResultCode))
                    {
                        return false;
                    }

                    if (FilteredExceptionTypeNames.Contains(logEvent.Exception.GetType().Name))
                    {
                        return false;
                    }
                    
                    if (FilteredExceptionMessages.Any(x => logEvent.Exception.Message.Contains(x)))
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }


        protected override void Write(LogEventInfo logEvent)
        {
            if (_unauthorized)
            {
                return;
            }

            try
            {
                SentrySdk.AddBreadcrumb(logEvent.FormattedMessage, logEvent.LoggerName, level: BreadcrumbLevelMap[logEvent.Level]);

                // don't report non-critical events without exceptions
                if (!IsSentryMessage(logEvent))
                {
                    return;
                }

                var fingerPrint = GetFingerPrint(logEvent);
                if (!_debounce.Allowed(fingerPrint))
                {
                    return;
                }

                var extras = logEvent.Properties.ToDictionary(x => x.Key.ToString(), x => (object)x.Value.ToString());
                extras.Remove("Sentry");

                if (logEvent.Exception != null)
                {
                    foreach (DictionaryEntry data in logEvent.Exception.Data)
                    {
                        extras.Add(data.Key.ToString(), data.Value.ToString());
                    }
                }

                var sentryEvent = new SentryEvent(logEvent.Exception)
                {
                    Level = LoggingLevelMap[logEvent.Level],
                    Logger = logEvent.LoggerName,
                    Message = logEvent.FormattedMessage,
                };

                var sentryFingerprint = new List<string> {
                        logEvent.Level.ToString(),
                        logEvent.LoggerName,
                        logEvent.Message
                };
                
                sentryEvent.SetExtras(extras);

                if (logEvent.Exception != null)
                {
                    sentryFingerprint.Add(logEvent.Exception.GetType().FullName);
                    sentryFingerprint.Add(logEvent.Exception.TargetSite.ToString());

                    // only try to use the exeception message to fingerprint if there's no inner
                    // exception and the message is short, otherwise we're in danger of getting a
                    // stacktrace which will break the grouping
                    if (logEvent.Exception.InnerException == null)
                    {
                        string message = null;

                        // bodge to try to get the exception message in English
                        // https://stackoverflow.com/questions/209133/exception-messages-in-english
                        // There may still be some localization but this is better than nothing.
                        var t = new Thread(() => {
                                message = logEvent.Exception?.Message;
                            });
                        t.CurrentCulture = CultureInfo.InvariantCulture;
                        t.CurrentUICulture = CultureInfo.InvariantCulture;
                        t.Start();
                        t.Join();

                        if (message.IsNotNullOrWhiteSpace() && message.Length < 200)
                        {
                            // Windows gives a trailing '.' for NullReferenceException but mono doesn't
                            sentryFingerprint.Add(message.TrimEnd('.'));
                        }
                    }
                }

                if (logEvent.Properties.ContainsKey("Sentry"))
                {
                    sentryFingerprint = ((string[])logEvent.Properties["Sentry"]).ToList();
                }
                
                sentryEvent.SetFingerprint(sentryFingerprint);

                // this can't be in the constructor as at that point OsInfo won't have
                // populated these values yet
                var osName = Environment.GetEnvironmentVariable("OS_NAME");
                var osVersion = Environment.GetEnvironmentVariable("OS_VERSION");
                var runTimeVersion = Environment.GetEnvironmentVariable("RUNTIME_VERSION");

                sentryEvent.SetTag("os_name", osName);
                sentryEvent.SetTag("os_version", $"{osName} {osVersion}");
                sentryEvent.SetTag("runtime_version", $"{PlatformInfo.PlatformName} {runTimeVersion}");

                SentrySdk.CaptureEvent(sentryEvent);
            }
            catch (Exception e)
            {
                OnError(e);
            }
        }

        // https://stackoverflow.com/questions/2496311/implementing-idisposable-on-a-subclass-when-the-parent-also-implements-idisposab
        protected override void Dispose(bool disposing)
        {
            // Only do something if we're not already disposed
            if (_disposed)
            {
                // If disposing == true, we're being called from a call to base.Dispose().  In this case, we Dispose() our logger
                // If we're being called from a finalizer, our logger will get finalized as well, so no need to do anything.
                if (disposing)
                {
                    _sdk?.Dispose();
                }
                // Flag us as disposed.  This allows us to handle multiple calls to Dispose() as well as ObjectDisposedException
                _disposed = true;
            }

            // This should always be safe to call multiple times!
            // We could include it in the check for disposed above, but I left it out to demonstrate that it's safe
            base.Dispose(disposing);
        }
    }
}
