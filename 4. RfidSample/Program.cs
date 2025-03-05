using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NidRpc;
using NurApiDotNet;

namespace RfidSample
{
    class Application
    {
        class TagEntry
        {
            public string epc;
            public byte antennaId;
            public sbyte rssi;
            public short phaseDiff;
            public uint timesSeen;
            public DateTime firstSeenTime;
            public DateTime lastSeenTime;
        }

        // HttpClient for sending tag data to remote URL
        private static readonly HttpClient _httpClient = new HttpClient();
        private const string TagEndpoint = "https://app.quickracing.net/api/v1/timing-agents/brady/laptimes";
        private const string PingEndpoint = "https://app.quickracing.net/api/v1/timing-agents/brady/ping";
        // JWT token for authorization
        private const string JwtToken = ""; // Replace with your actual JWT token
        // Timer for regular pings
        private System.Threading.Timer _pingTimer;
        public class ByteArrayComparer : IEqualityComparer<byte[]>
        {
            public bool Equals(byte[] lhs, byte[] rhs)
            {
                if (lhs == null || rhs == null)
                {
                    return lhs == rhs;
                }
                return lhs.SequenceEqual(rhs);
            }
            public int GetHashCode(byte[] key)
            {
                if (key == null)
                    return 0;
                return key.Sum(b => b);
            }
        }

        readonly Plugin _rpc;
        readonly NurApi _nur;
        readonly ManualResetEventSlim _backGroundResetEvent = new ManualResetEventSlim();

        readonly SemaphoreSlim _lock = new SemaphoreSlim(1);
        bool _connected = false;
        string _connectError = null;
        NurApi.ReaderInfo? _readerInfo = null;
        bool _streamEnabled = false;
        DateTime? _lastStreamEvent = null;
        uint _nStreamEvents = 0;
        readonly Dictionary<byte[], TagEntry> _tagsSeen = new Dictionary<byte[], TagEntry>(new ByteArrayComparer());

        async public static Task<Application> CreateInstanceAsync(string appName)
        {
            var rpc = new Plugin("application", appName);
            var application = new Application(rpc);
            await rpc.ConnectAsync();
            return application;
        }

        Application(Plugin rpc)
        {
            try
            {
                _nur = new NurApi();
                _nur.ConnectedEvent += NurConnectedEvent;
                _nur.DisconnectedEvent += NurDisconnectedEvent;
                _nur.InventoryStreamEvent += new EventHandler<NurApi.InventoryStreamEventArgs>(OnInventoryStreamEvent);
            }
            catch (NurApiException ex)
            {
                Console.WriteLine(ex.Message);
                throw ex;
            }

            _rpc = rpc;
            _rpc["/rfid/connected"].CallbackReceived += RfidConnected;
            _rpc["/rfid/connect"].CallbackReceived += RfidConnect;
            _rpc["/rfid/disconnect"].CallbackReceived += RfidDisconnect;
            _rpc["/rfid/readerinfo"].CallbackReceived += RfidReaderInfo;

            _rpc["/tags/startStream"].CallbackReceived += TagsStartStreamEvent;
            _rpc["/tags/stopStream"].CallbackReceived += TagsStopStream;

            _rpc["/inventory/get"].CallbackReceived += InventoryGet;
        }

        public void Run()
        {
            BackgroundConnect();
            TagsStartStream();
            // Start the ping timer to send pings every 30 seconds
            StartPingTimer();
            while (true)
            {
                _backGroundResetEvent.Wait();
            }
        }

        #region RPC_CALLBACKS
        async Task<JObject> RfidConnected(object sender, CallbackEventArgs args)
        {
            await _lock.WaitAsync();
            var connected = _connected ? "true" : "false";
            var connectError = _connectError;
            _lock.Release();

            var ret = JObject.Parse($"{{'connected': {connected}}}");
            if (connectError != null)
            {
                ret["connectError"] = connectError;
            }
            return ret;
        }

        void BackgroundConnect()
        {
            var thread = new Thread(() =>
            {
                try
                {
                    _nur.ConnectSocket("127.0.0.1", 4333);
                    _lock.Wait();
                    _connectError = null;
                    _lock.Release();
                }
                catch (NurApiException ex)
                {
                    _lock.Wait();
                    _connectError = ex.Message;
                    _lock.Release();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        async Task<JObject> RfidConnect(object sender, CallbackEventArgs args)
        {
            BackgroundConnect();
            return await Task.FromResult(JObject.Parse("{}"));
        }

        async Task<JObject> RfidDisconnect(object sender, CallbackEventArgs args)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    _nur.Disconnect();
                }
                catch (NurApiException ex)
                {
                    _lock.Wait();
                    _connectError = ex.Message;
                    _lock.Release();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await Task.FromResult(JObject.Parse("{}"));
        }

        private async Task<JObject> RfidReaderInfo(object sender, CallbackEventArgs args)
        {
            await _lock.WaitAsync();
            var readerInfo = _readerInfo;
            _lock.Release();
            try
            {
                if (readerInfo.HasValue)
                {
                    return JObject.Parse(JsonConvert.SerializeObject(readerInfo));
                }
                else
                {
                    return JObject.Parse("{'error': 'No reader info available'}");
                }
            }
            catch (Exception)
            {
                return JObject.Parse("{'error': 'Error serializing reader info'}");
            }
        }


        private void TagsStartStreamEvent(object sender, CallbackEventArgs args)
        {
            await TagsStartStream();
        }

        private async Task<JObject> TagsStartStream()
        {
            await _lock.WaitAsync();
            try
            {
                _streamEnabled = true;
                _tagsSeen.Clear();
                _nStreamEvents = 0;
            }
            finally
            {
                _lock.Release();
            }
            var thread = new Thread(() =>
            {
                try
                {
                    _nur.ClearTagsEx();
                    StartTagStream();
                }
                catch (NurApiException ex)
                {
                    Console.WriteLine($"Failed to start tag reading {ex.Message}");
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await Task.FromResult(JObject.Parse("{}"));
        }

        private async Task<JObject> TagsStopStream(object sender, CallbackEventArgs args)
        {
            await _lock.WaitAsync();
            _streamEnabled = false;
            _lock.Release();
            var thread = new Thread(() =>
            {
                try
                {
                    _nur.StopInventoryStream();
                }
                catch (NurApiException ex)
                {
                    Console.WriteLine($"Failed to stop tag reading {ex.Message}");
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await Task.FromResult(JObject.Parse("{}"));
        }

        private async Task<JObject> InventoryGet(object sender, CallbackEventArgs args)
        {
            int count;
            uint nStreamEvents;
            string streamEnabled = _streamEnabled ? "true" : "false";
            var tags = new List<TagEntry>();
            await _lock.WaitAsync();
            try
            {
                count = _tagsSeen.Count;
                nStreamEvents = _nStreamEvents;
                foreach (TagEntry tagEntry in _tagsSeen.Values)
                {
                    tags.Add(tagEntry);
                }
            }
            finally
            {
                _lock.Release();
            }

            var jsonTxt = $"{{'count': {count}, 'nInventories': {nStreamEvents}, 'updateEnabled': {streamEnabled}, 'tags': {JsonConvert.SerializeObject(tags)}}}";
            var ret = await Task.FromResult(JObject.Parse(jsonTxt));
            if (_lastStreamEvent.HasValue)
            {
                ret["timestamp"] = _lastStreamEvent.Value.ToString("yyyy-MM-dd HH\\:mm\\:ss");
            }
            return ret;
        }

        #endregion

        #region NUR_CALLBACKS
        void NurConnectedEvent(object sender, NurApi.NurEventArgs e)
        {
            _lock.Wait();
            _connected = true;
            _lock.Release();

            var thread = new Thread(() =>
            {
                NurApi.ReaderInfo? readerInfo = null;
                try
                {
                    readerInfo = _nur.GetReaderInfo();
                }
                catch (NurApiException ex)
                {
                    Console.WriteLine($"Failed to get reader info {ex.Message}");
                }
                _lock.Wait();
                _readerInfo = readerInfo;
                _lock.Release();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        void NurDisconnectedEvent(object sender, NurApi.NurEventArgs e)
        {
            _lock.Wait();
            try
            {
                _connected = false;
                _readerInfo = null;
                _streamEnabled = false;
                _tagsSeen.Clear();
                _nStreamEvents = 0;
            }
            finally
            {
                _lock.Release();
            }
        }

        async void OnInventoryStreamEvent(object sender, NurApi.InventoryStreamEventArgs ev)
        {
            NurApi.TagStorage nurStorage = _nur.GetTagStorage();
            _lock.Wait();
            try
            {
                if (!_streamEnabled)
                {
                    return;
                }
                // need to lock access to the tag storage object to
                // prevent NurApi from updating it in the background
                lock (nurStorage)
                {
                    foreach (NurApi.Tag tag in nurStorage)
                    {
                        if (_tagsSeen.TryGetValue(tag.epc, out TagEntry value))
                        {
                            value.antennaId = tag.antennaId;
                            value.rssi = tag.rssi;
                            value.phaseDiff = (short)tag.timestamp;
                            value.timesSeen += 1;
                            
                            // Calculate time since last seen
                            DateTime currentTime = DateTime.Now;
                            TimeSpan timeDifference = currentTime - value.lastSeenTime;
                            value.lastSeenTime = currentTime;
                            
                            // Only send to remote URL if:  
                            // 1. This is at least the second time we've seen the tag
                            // 2. It's been at least 30 seconds since we last saw this tag
                            if (value.timesSeen > 1 && timeDifference.TotalSeconds >= 30)
                            {
                                // Send tag data to remote URL with time difference
                                SendTagToRemoteUrl(value.epc, (int)timeDifference.TotalMilliseconds);
                                Console.WriteLine($"Tag {value.epc} sent after {timeDifference.TotalSeconds:F2} seconds");
                            }
                            else if (value.timesSeen > 1)
                            {
                                Console.WriteLine($"Tag {value.epc} seen again after {timeDifference.TotalSeconds:F2} seconds (less than 30s threshold, not sending)");
                            }
                        }
                        else
                        {
                            _tagsSeen[tag.epc] = new TagEntry() {
                                epc = BitConverter.ToString(tag.epc).Replace("-", ""),
                                antennaId = tag.antennaId,
                                rssi = tag.rssi,
                                phaseDiff = (short)tag.timestamp,
                                timesSeen = 1,
                                firstSeenTime = DateTime.Now,
                                lastSeenTime = DateTime.Now
                            };
                            
                            // Don't send to remote URL on first observation
                        }
                    }
                    // Clear NurApi internal tag storage so that we only get new tags next next time
                    nurStorage.Clear();
                }
                // NurApi may disable the stream to prevent unnecessarily powering the radio
                // (in case the application has stopped); start it again if that is the case
                if (_streamEnabled && ev.data.stopped)
                {
                    StartTagStream();
                }
                _lastStreamEvent = DateTime.Now;
                _nStreamEvents++;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            finally
            {
                _lock.Release();
            }
        }

        private async void SendTagToRemoteUrl(string tagEpc, int timeDifferenceMs = 0)
        {
            try
            {
                // Create the JSON payload with the requested format
                var payload = new 
                { 
                    technical_id = tagEpc, 
                    value = timeDifferenceMs 
                };
                var jsonPayload = JsonConvert.SerializeObject(payload);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                
                // Create request message to add Authorization header
                var request = new HttpRequestMessage(HttpMethod.Post, TagEndpoint)
                {
                    Content = content
                };
                
                // Add JWT token as bearer token in Authorization header
                request.Headers.Add("Authorization", $"Bearer {JwtToken}");
                
                // Send POST request to the endpoint
                var response = await _httpClient.SendAsync(request);
                
                // Log sending activity
                Console.WriteLine($"Sent tag {tagEpc} to {TagEndpoint} with time value {timeDifferenceMs}ms");
                
                // Check if request was successful
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Error sending tag to remote URL: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception while sending tag to remote URL: {ex.Message}");
            }
        }

        private void StartPingTimer()
        {
            // Create and start a timer that sends pings every 30 seconds
            // First parameter is the callback, second is state (null),
            // third is delay before first execution (0 = start immediately),
            // fourth is period (30000 ms = 30 seconds)
            _pingTimer = new System.Threading.Timer(
                async (state) => await SendPingAsync(),
                null,
                0,  // Start immediately
                30000);  // Then repeat every 30 seconds
            
            Console.WriteLine("Ping timer started - will send pings every 30 seconds");
        }

        private async Task SendPingAsync()
        {
            try
            {
                // Create request with empty body
                var request = new HttpRequestMessage(HttpMethod.Post, PingEndpoint);
                
                // Add JWT token as bearer token in Authorization header
                request.Headers.Add("Authorization", $"Bearer {JwtToken}");
                
                // Send POST request to the ping endpoint
                var response = await _httpClient.SendAsync(request);
                
                // Log ping activity
                Console.WriteLine($"Ping sent to {PingEndpoint} with status: {response.StatusCode}");
                
                // Check if request was successful
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Error sending ping: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception while sending ping: {ex.Message}");
            }
        }

        #endregion
        void StartTagStream()
        {
            // TODO: fix when NUR_OPFLAGS_EN_PHASE_DIFF/NUR_DC_PHASEDIFF has been added to NurApiDotNet
            // tag phase diff support (NUR_OPFLAGS_EN_PHASE_DIFF = (1 << 17)) isn't yet available in
            // NurApiDotNet; just assume it is supported in the NUR module and turn it on
            _nur.OpFlags |= (1 << 17);
            _nur.StartInventoryStream();
        }
    }

    class Program
    {
        static async Task Main()
        {
            var application = await Application.CreateInstanceAsync("RfidQR");
            application.Run();
        }
    }
}