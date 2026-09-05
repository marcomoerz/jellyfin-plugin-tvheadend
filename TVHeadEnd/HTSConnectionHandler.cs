using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using TVHeadEnd.DataHelper;
using TVHeadEnd.Helper;
using TVHeadEnd.HTSP;


namespace TVHeadEnd
{
    public class HTSConnectionHandler : HTSConnectionListener
    {
        private static volatile HTSConnectionHandler? _instance;
        private static object _syncRoot = new Object();

        /// <summary>Serialises connection setup so a burst of callers builds exactly one connection.</summary>
        private readonly SemaphoreSlim _connectionGate = new SemaphoreSlim(1, 1);

        private readonly TimeSpan _requestTimeout = TimeSpan.FromMinutes(1);
        private readonly TimeSpan _initialLoadTimeout = TimeSpan.FromMinutes(15);

        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<HTSConnectionHandler> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        /// <summary>
        /// Completed once TVHeadend has finished its initial sync. Replaced by a fresh instance on
        /// every reconnect, because a new connection has sent us nothing yet.
        /// </summary>
        private volatile TaskCompletionSource _initialLoadCompleted =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        private volatile Boolean _configured = false;
        private volatile Boolean _configurationChangeHooked = false;

        /// <summary>Volatile: read on the fast path outside <see cref="_connectionGate"/>.</summary>
        private volatile HTSConnectionAsync? _htsConnection;
        private int _priority;
        private string _profile = string.Empty;
        private string _channelType = string.Empty;
        private string _tvhServerName = string.Empty;
        private int _httpPort;
        private int _htspPort;
        private string _webRoot = string.Empty;
        private string _userName = string.Empty;
        private string _password = string.Empty;
        private bool _enableSubsMaudios;
        private bool _forceDeinterlace;

        // Data helpers
        private readonly ChannelDataHelper _channelDataHelper;
        private readonly DvrDataHelper _dvrDataHelper;
        private readonly AutorecDataHelper _autorecDataHelper;

        private LiveTvService? _liveTvService;

        private Dictionary<string, string> _headers = new Dictionary<string, string>();

        public HTSConnectionHandler(ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<HTSConnectionHandler>();
            _httpClientFactory = httpClientFactory;
            _liveTvService = null;

            //System.Diagnostics.StackTrace t = new System.Diagnostics.StackTrace();
            _logger.LogDebug("[TVHclient] HTSConnectionHandler");

            _channelDataHelper = new ChannelDataHelper(loggerFactory.CreateLogger<ChannelDataHelper>());
            _dvrDataHelper = new DvrDataHelper(loggerFactory.CreateLogger<DvrDataHelper>());
            _autorecDataHelper = new AutorecDataHelper(loggerFactory.CreateLogger<AutorecDataHelper>());

            _channelDataHelper.SetChannelType4Other(_channelType);
        }

        public static HTSConnectionHandler GetInstance(ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory)
        {
            if (_instance == null)
            {
                lock (_syncRoot)
                {
                    if (_instance == null)
                    {
                        _instance = new HTSConnectionHandler(loggerFactory, httpClientFactory);
                    }
                }
            }
            return _instance;
        }

        public void setLiveTvService(LiveTvService liveTvService)
        {
            _liveTvService = liveTvService;
        }

        public LiveTvService? getLiveTvService()
        {
            return _liveTvService;
        }

        /// <summary>
        /// Waits until TVHeadend has delivered its initial metadata sync.
        /// </summary>
        /// <returns><c>true</c> when the data is ready, <c>false</c> when the wait timed out.</returns>
        public async Task<bool> WaitForInitialLoadAsync(CancellationToken cancellationToken)
        {
            await GetConnectionAsync(cancellationToken).ConfigureAwait(false);

            // Read the field once: a reconnect swaps it, and the timeout should apply to the sync
            // we actually asked for.
            Task completed = _initialLoadCompleted.Task;

            try
            {
                await completed.WaitAsync(_initialLoadTimeout, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                _logger.LogError(
                    "[TVHclient] HTSConnectionHandler: TVHeadend did not finish its initial sync within {timeout}",
                    _initialLoadTimeout);
                return false;
            }
        }

        private void init()
        {
            if(_configured == true)
            {
                return ;
            }
            _logger.LogDebug("[TVHclient] HTSConnectionHandler - Init()");

            var config = Plugin.Instance.Configuration;

            _logger.LogDebug("[TVHclient] HTSConnectionHandler - Config initialized");

            if (string.IsNullOrEmpty(config.TVH_ServerName))
            {
                string message = "[TVHclient] HTSConnectionHandler.ensureConnection: TVH server name must be configured";
                _logger.LogError(message);
                throw new InvalidOperationException(message);
            }

            if (string.IsNullOrEmpty(config.Username))
            {
                string message = "[TVHclient] HTSConnectionHandler.ensureConnection: username must be configured";
                _logger.LogError(message);
                throw new InvalidOperationException(message);
            }

            if (string.IsNullOrEmpty(config.Password))
            {
                string message = "[TVHclient] HTSConnectionHandler.ensureConnection: password must be configured";
                _logger.LogError(message);
                throw new InvalidOperationException(message);
            }

            _priority = config.Priority;
            _profile = config.Profile.Trim();
            _channelType = config.ChannelType.Trim();
            _enableSubsMaudios = config.EnableSubsMaudios;
            _forceDeinterlace = config.ForceDeinterlace;

            if (_priority < 0 || _priority > 4)
            {
                _priority = 2;
                _logger.LogDebug("[TVHclient] HTSConnectionHandler.ensureConnection: priority was out of range [0-4] - set to 2");
            }

            _tvhServerName = config.TVH_ServerName.Trim();
            _httpPort = config.HTTP_Port;
            _htspPort = config.HTSP_Port;
            _webRoot = config.WebRoot;
            if (_webRoot.EndsWith("/"))
            {
                _webRoot = _webRoot.Substring(0, _webRoot.Length - 1);
            }
            _userName = config.Username.Trim();
            _password = config.Password.Trim();

            string authInfo = _userName + ":" + _password;
            authInfo = Convert.ToBase64String(Encoding.Default.GetBytes(authInfo));
            _headers["Authorization"] = "Basic " + authInfo;
            _configured = true;

            if (!_configurationChangeHooked)
            {
                Plugin.Instance.ConfigurationChanged += OnPluginConfigurationChanged;
                _configurationChangeHooked = true;
            }
        }

        private void OnPluginConfigurationChanged(object? sender, BasePluginConfiguration e)
        {
            _logger.LogInformation(
                "[TVHclient] HTSConnectionHandler: plugin configuration changed, re-reading it on next use");
            _configured = false;
        }

        public string? GetChannelImageUrl(string channelId)
        {
            init();

            _logger.LogDebug("[TVHclient] HTSConnectionHandler.GetChannelImage: channelId: {id}", channelId);

            String channelIcon = _channelDataHelper.GetChannelIcon4ChannelId(channelId);

            if (string.IsNullOrEmpty(channelIcon))
            {
                return null;
            }

            if (channelIcon.StartsWith("http"))
            {
                return _channelDataHelper.GetChannelIcon4ChannelId(channelId);
            }
            else
            {
                return "http://" + _userName + ":" + _password + "@" +_tvhServerName + ":" + _httpPort + _webRoot + "/" + channelIcon;
            }
        }

        public Dictionary<string, string> GetHeaders()
        {
            init();
            return new Dictionary<string, string>(_headers);
        }

        //private static Stream ImageToPNGStream(Image image)
        //{
        //    Stream stream = new System.IO.MemoryStream();
        //    image.Save(stream, ImageFormat.Png);
        //    stream.Position = 0;
        //    return stream;
        //}

        /// <summary>
        /// Returns a usable connection, building one if the current is missing or faulted.
        /// </summary>
        /// <remarks>
        /// A faulted connection is never repaired, only replaced. Reconnecting is deliberately
        /// pulled out of the failure path and into the next caller: when a socket dies, every
        /// pump fails at once, and letting each of them reconnect is what produced connection
        /// storms and torn-down replacements.
        /// </remarks>
        private async Task<HTSConnectionAsync> GetConnectionAsync(CancellationToken cancellationToken)
        {
            init();

            HTSConnectionAsync? current = _htsConnection;
            if (current != null && !current.IsFaulted)
            {
                return current;
            }

            await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Someone may have rebuilt it while we queued on the gate.
                current = _htsConnection;
                if (current != null && !current.IsFaulted)
                {
                    return current;
                }

                if (current != null)
                {
                    _htsConnection = null;
                    await current.DisposeAsync().ConfigureAwait(false);
                }

                // A fresh connection has told us nothing yet. Anything cached describes a server
                // state we are no longer subscribed to.
                _initialLoadCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _channelDataHelper.Clean();
                _dvrDataHelper.clean();
                _autorecDataHelper.clean();

                _logger.LogDebug("[TVHclient] HTSConnectionHandler: connecting to " +
                    "TVH Server = '{servername}'; HTTP Port = '{httpport}'; HTSP Port = '{htspport}'; Web-Root = '{webroot}'; " +
                    "User = '{user}'; Password set = '{passexists}'",
                    _tvhServerName, _httpPort, _htspPort, _webRoot, _userName, _password.Length > 0);

                Version? version = Assembly.GetEntryAssembly()?.GetName().Version;
                HTSConnectionAsync connection = new HTSConnectionAsync(
                    this, "TVHclient4Emby-" + version, "" + HTSMessage.HTSP_VERSION, _loggerFactory);

                try
                {
                    await connection.ConnectAsync(_tvhServerName, _htspPort, cancellationToken).ConfigureAwait(false);

                    Result<Unit, HtspError> authentication = await connection
                        .AuthenticateAsync(_userName, _password, _requestTimeout, cancellationToken)
                        .ConfigureAwait(false);

                    if (!authentication.IsSuccess)
                    {
                        throw new InvalidOperationException(
                            $"TVHeadend rejected the connection: {authentication.Error.Describe()}");
                    }
                }
                catch
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                _htsConnection = connection;
                _logger.LogDebug("[TVHclient] HTSConnectionHandler: connection established");
                return connection;
            }
            finally
            {
                _connectionGate.Release();
            }
        }

        /// <summary>Sends a request over the current connection and awaits its reply.</summary>
        /// <remarks>
        /// Passes the result through unchanged. Translating it into an exception here would throw
        /// away the category the callers act on.
        /// </remarks>
        public async Task<Result<HTSMessage, HtspError>> SendRequestAsync(HTSMessage message, TimeSpan timeout, CancellationToken cancellationToken)
        {
            HTSConnectionAsync connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
            return await connection.SendRequestAsync(message, timeout, cancellationToken).ConfigureAwait(false);
        }

        public Task<IEnumerable<ChannelInfo>> BuildChannelInfos(CancellationToken cancellationToken)
        {
            return _channelDataHelper.BuildChannelInfos(cancellationToken);
        }

        public int GetPriority()
        {
            init();
            return _priority;
        }

        public String GetProfile()
        {
            init();
            return _profile;
        }

        public String GetHttpBaseUrlWithCredentials()
        {
            init();
            return "http://" + _userName + ":" + _password + "@" + _tvhServerName + ":" + _httpPort + _webRoot;
        }

        public String GetHttpBaseUrlWithoutCredentials()
        {
            init();
            return "http://" + _tvhServerName + ":" + _httpPort + _webRoot;
        }

        public bool GetEnableSubsMaudios()
        {
            init();
            return _enableSubsMaudios;
        }

        public bool GetForceDeinterlace()
        {
            init();
            return _forceDeinterlace;
        }

        public Task<IEnumerable<MyRecordingInfo>> BuildDvrInfos(CancellationToken cancellationToken)
        {
            return _dvrDataHelper.buildDvrInfos(cancellationToken);
        }

        public Task<IEnumerable<SeriesTimerInfo>> BuildAutorecInfos(CancellationToken cancellationToken)
        {
            return _autorecDataHelper.buildAutorecInfos(cancellationToken);
        }

        public Task<IEnumerable<TimerInfo>> BuildPendingTimersInfos(CancellationToken cancellationToken)
        {
            return _dvrDataHelper.buildPendingTimersInfos(cancellationToken);
        }

        /// <summary>
        /// Reports that the current connection died. Fired at most once per connection.
        /// </summary>
        /// <remarks>
        /// This deliberately does not reconnect. The connection has marked itself faulted, so the
        /// next caller that needs it rebuilds it through <see cref="GetConnectionAsync"/> — on its
        /// own thread, under the gate, and only if anyone still cares.
        /// </remarks>
        public void onError(Exception ex)
        {
            _logger.LogError(ex, "[TVHclient] HTSConnectionHandler: HTSP connection lost, reconnecting on next use");
        }

        public void onMessage(HTSMessage response)
        {
            if (response != null)
            {
                switch (response.Method)
                {
                    case "tagAdd":
                    case "tagUpdate":
                    case "tagDelete":
                        //_logger.LogCritical("[TVHclient] tad add/update/delete {resp}", response.ToString());
                        break;

                    case "channelAdd":
                    case "channelUpdate":
                        _channelDataHelper.Add(response);
                        break;

                    case "dvrEntryAdd":
                        _dvrDataHelper.dvrEntryAdd(response);
                        break;
                    case "dvrEntryUpdate":
                        _dvrDataHelper.dvrEntryUpdate(response);
                        break;
                    case "dvrEntryDelete":
                        _dvrDataHelper.dvrEntryDelete(response);
                        break;

                    case "autorecEntryAdd":
                        _autorecDataHelper.autorecEntryAdd(response);
                        break;
                    case "autorecEntryUpdate":
                        _autorecDataHelper.autorecEntryUpdate(response);
                        break;
                    case "autorecEntryDelete":
                        _autorecDataHelper.autorecEntryDelete(response);
                        break;

                    case "eventAdd":
                    case "eventUpdate":
                    case "eventDelete":
                        // should not happen as we don't subscribe for this events.
                        break;

                    //case "subscriptionStart":
                    //case "subscriptionGrace":
                    //case "subscriptionStop":
                    //case "subscriptionSkip":
                    //case "subscriptionSpeed":
                    //case "subscriptionStatus":
                    //    _logger.LogCritical("[TVHclient] subscription events {resp}", response.ToString());
                    //    break;

                    //case "queueStatus":
                    //    _logger.LogCritical("[TVHclient] queueStatus event {resp}", response.ToString());
                    //    break;

                    //case "signalStatus":
                    //    _logger.LogCritical("[TVHclient] signalStatus event {resp}", response.ToString());
                    //    break;

                    //case "timeshiftStatus":
                    //    _logger.LogCritical("[TVHclient] timeshiftStatus event {resp}", response.ToString());
                    //    break;

                    //case "muxpkt": // streaming data
                    //    _logger.LogCritical("[TVHclient] muxpkt event {resp}", response.ToString());
                    //    break;

                    case "initialSyncCompleted":
                        _initialLoadCompleted.TrySetResult();
                        break;

                    default:
                        //_logger.LogCritical("[TVHclient] Method '{method}' not handled in LiveTvService.cs", response.Method);
                        break;
                }
            }
        }
    }
}
