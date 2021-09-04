﻿using ColorControl;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LgTv
{
    //https://mym.hackpad.com/ep/pad/static/rLlshKkzdNj

    public enum ControlButtons
    {
        Back,Down,Left,Right,
        OK,
        Exit
    }

    public enum PictureMode
    {
        cinema, eco, expert1, expert2, game,
        normal, photo, sports, technicolor, vivid, hdrEffect, hdrCinema,
        hdrCinemaBright, hdrExternal, hdrGame, hdrStandard, hdrTechnicolor,
        hdrVivid, dolbyHdrCinema, dolbyHdrCinemaBright, dolbyHdrDarkAmazon,
        dolbyHdrGame, dolbyHdrStandard, dolbyHdrVivid, dolbyStandard
    }

    public enum OffToHigh
    {
        off,
        low,
        medium,
        high
    }

    public enum LowToAuto
    {
        low,
        medium,
        high,
        auto
    }

    public enum OffToOn
    {
        off,
        on
    }

    public enum OffToAuto2
    {
        off,
        on,
        auto
    }

    public enum OffToAuto
    {
        off,
        on,
        low,
        medium,
        high,
        auto
    }

    public enum ColorGamut
    {
        auto,
        extended,
        wide,
        srgb
    }

    public enum EnergySaving
    {
        auto,
        off,
        min,
        med,
        max,
        screen_off
    }

    public enum DynamicTonemapping
    {
        on,
        off,
        HGIG,
    }

    public enum TruMotionMode
    {
        off,
        user
    }

    public class LgTvApi:IDisposable
    {
        public bool ConnectionClosed { get => _connection?.ConnectionClosed ?? true; }

        private readonly LgTvApiCore _connection;
        private LgWebOsMouseService _mouseConnection;

        private readonly ClientKeyStore _keyStore;

        private List<Channel> _channelList;
        private List<App> _appList;
        private string _ip;
        private string _currentPairKey;
        private string webSocketUri;

        public static async Task<LgTvApi> CreateLgTvApi(string ip, int retries = 1)
        {
            var instance = new LgTvApi(ip, new LgTvApiCore(), new ClientKeyStore(ip));
            while (retries > 0)
            {
                var connected = await instance.Connect();
                if (connected)
                {
                    await instance.MakeHandShake();
                    return instance;
                }
                retries--;
                await Task.Delay(500);
            }
            instance.Dispose();
            return null;
        }

        private LgTvApi(string ip, LgTvApiCore connection, ClientKeyStore keyStore)
        {
            webSocketUri = "ws://" + ip + ":3000";
            _ip = ip;
            _connection = connection;
            _keyStore = keyStore;
        }

        public string GetIpAddress()
        {
            return _ip;
        }

        public async Task MakeHandShake()
        {
            var registerJson = Utils.GetResourceFile("LG_register.json");

            _currentPairKey = _keyStore.GetClientKey();
            if (_currentPairKey != null && !_currentPairKey.All(k => k == '\0') && _keyStore.HasValidHandShake(registerJson))
            {
                var key = registerJson.Replace("CLIENTKEYGOESHERE", _currentPairKey);
                var conn = await _connection.SendCommandAsync(key);
                _keyStore.SaveClientKey((string)conn.clientKey);
                return;
            }

            var registerJsonRemovedKey = registerJson.Replace("CLIENTKEYGOESHERE", string.Empty);
            dynamic result = await _connection.SendCommandAsync(registerJsonRemovedKey);
            _keyStore.SaveClientKey(result.clientKey);
            _keyStore.SaveHandShake(registerJson);
        }
    

        public async Task<bool> Connect()
        {
            var ctx = _connection.Connect(new Uri(webSocketUri));
            return await ctx;
        }

        public async Task<LgWebOsMouseService> GetMouse()
        {
            if (_mouseConnection != null) return _mouseConnection;
            var requestMessage = new RequestMessage("ssap://com.webos.service.networkinput/getPointerInputSocket", new { });
            var s = await _connection.SendCommandAsync(requestMessage);
            var socketPath = (string)s.socketPath;
           
            _mouseConnection = new LgWebOsMouseService(new LgTvApiCore());
            await _mouseConnection.Connect(socketPath);
            return _mouseConnection;
        }

        public async Task<IEnumerable<Channel>> ChannelList()
        {
            _channelList = new List<Channel>();
            var requestMessage = new RequestMessage("channels", "ssap://tv/getChannelList");
            var command = _connection.SendCommandAsync(requestMessage);
            var res = await command;
            foreach (var c in res.channelList)
            {
                _channelList.Add(new Channel
                {
                    Id = c.channelId,
                    Name = c.channelName,
                    Number = int.Parse((string)c.channelNumber)
                });
            }
            return _channelList.OrderBy(e => e.Number);
        }

        public async Task<Channel> GetChannel()
        {
            var requestMessage = new RequestMessage("channels", "ssap://tv/getCurrentChannel");
            var command = _connection.SendCommandAsync(requestMessage);

            var res = await command;
            return new Channel() { Id = res.channelId, Name = res.channelName, Number = int.Parse(res.channelNumber) };
        }
        public async Task ShowToast()
        {
            var requestMessage = new RequestMessage("ssap://system.notifications/createToast",new {message = "Co tam u Ciebie?"});
            await _connection.SendCommandAsync(requestMessage);
        }
       
        //public async Task<string> LaunchApp(string appId,Uri uri)
        //{
        //    var url = uri.ToString();
        //    var requestMessage = new RequestMessage("ssap://system.launcher/launch", new { id = appId, @params={contentTarget } });
        //    var response = await _connection.SendCommandAsync(requestMessage);
        //    return (string)response.sessionId;
        //}

        public async Task VolumeDown()
        {
            var requestMessage = new RequestMessage("volumedown", "ssap://audio/volumeDown");
            await _connection.SendCommandAsync(requestMessage);
        }
        public async Task  VolumeUp()
        {
            var requestMessage = new RequestMessage("volumeup", "ssap://audio/volumeUp");
            await _connection.SendCommandAsync(requestMessage);
        }
        public async Task ChannelInfo()
        {
            var requestMessage = new RequestMessage("programinfo", "ssap://tv/getChannelProgramInfo");
            await _connection.SendCommandAsync(requestMessage);
        }

        public async Task  SetChannel(string channelId)
        {
            var requestMessage = new RequestMessage("ssap://tv/openChannel", new { channelId });
            await _connection.SendCommandAsync(requestMessage);
        }
        public async  Task  SetMute(bool value)
        {
            var requestMessage = new RequestMessage("ssap://audio/setMute", new { mute = value });
            await _connection.SendCommandAsync(requestMessage);
        }
        public async Task ToogleMute()
        {
            await SetMute(!await IsMuted());
        }
        public async Task<bool> IsMuted()
        {
            var requestMessage = new RequestMessage("status", "ssap://audio/getStatus");
            var command = _connection.SendCommandAsync(requestMessage);
            return (bool)(await command).mute;
        }

        public async Task  SetVolume(int value)
        {
            if (value >= 0 && value <= 100)
            {
                var requestMessage = new RequestMessage("ssap://audio/setVolume", new { volume = value });
                await _connection.SendCommandAsync(requestMessage);
            }
        }
        public async Task  TurnOff()
        {
            await _connection.SendCommandAsync(new RequestMessage("","ssap://system/turnOff"));
        }
        public async Task  Play()
        {
            await _connection.SendCommandAsync(new RequestMessage("play", "ssap://media.controls/play"));
        }
        public async Task Pause()
        {
            await _connection.SendCommandAsync(new RequestMessage("pause", "ssap://media.controls/pause"));
        }

        public async Task ChannelUp()
        {
            await _connection.SendCommandAsync(new RequestMessage("channelUp", "ssap://tv/channelUp"));
        }
        public async Task ChannelDown()
        {
            await _connection.SendCommandAsync(new RequestMessage("channelDown", "ssap://tv/channelDown"));
        }

        public async Task  Stop()
        {
            await _connection.SendCommandAsync(new RequestMessage("stop", "ssap://media.controls/stop"));
        }

        public async Task TurnOn3D()
        {
            await _connection.SendCommandAsync(new RequestMessage("3d", "ssap://com.webos.service.tv.display/set3DOn"));
        }
        public async Task TurnOff3D()
        {
            await _connection.SendCommandAsync(new RequestMessage("3d", "ssap://com.webos.service.tv.display/set3DOff"));
        }

        public async Task TurnScreenOff()
        {
            await _connection.SendCommandAsync(new RequestMessage("", "ssap://com.webos.service.tvpower/power/turnOffScreen"));
        }

        public async Task TurnScreenOn()
        {
            await _connection.SendCommandAsync(new RequestMessage("", "ssap://com.webos.service.tvpower/power/turnOnScreen"));
        }

        public async Task<bool> IsTurnedOn3D()
        {
            //Response: { returnValue: true,  status3D: { status: true, pattern: ’2Dto3D’ } }
            var requestMessage = new RequestMessage("status3D", "ssap://com.webos.service.tv.display/get3DStatus");
            var o = await _connection.SendCommandAsync(requestMessage);
           return (bool)o.status3D.status;
        }

        public async Task<int> GetVolume()
        {
            // {
            //     "type": "response",
            //     "id": "status_1",
            //     "payload": {
            //         "muted": false,
            //         "scenario": "mastervolume_tv_speaker",
            //         "active": false,
            //         "action": "requested",
            //         "volume": 7,
            //         "returnValue": true,
            //         "subscribed": true
            //     }
            // }
            var requestMessage = new RequestMessage("status", "ssap://audio/getVolume");
            var o = await _connection.SendCommandAsync(requestMessage);
            return( (bool)o.muted) ? -1 :(int) o.volume;
        }
        public async Task<IEnumerable<ExternalInput>> GetInputList()
        {
           var requestMessage = new RequestMessage("input","ssap://tv/getExternalInputList");
           var results =  await _connection.SendCommandAsync(requestMessage);
            var l = new List<ExternalInput>();
            foreach (var result in results)
            {
                l.Add(new ExternalInput(result.id, result.label)
                {
                    Icon = result.icon
                });
            }
            return l;
        }

        public async Task  SetInput(string id)
        {
            var requestMessage = new RequestMessage("ssap://tv/switchInput", new { inputId = id });
            await _connection.SendCommandAsync(requestMessage);
        }

        public async Task SubscribeVolume(Func<dynamic, bool> callback, dynamic payload = null)
        {
            var requestMessage = new RequestMessage("ssap://audio/getVolume", null, "subscribe");

            await _connection.SubscribeAsync(requestMessage, callback);
        }

        public async Task SubscribePowerState(Func<dynamic, bool> callback, dynamic payload = null)
        {
            var requestMessage = new RequestMessage("ssap://com.webos.service.tvpower/power/getPowerState", null, "subscribe");

            await _connection.SubscribeAsync(requestMessage, callback);
        }

        public async Task SubscribePictureSettings(Func<dynamic, bool> callback, dynamic payload = null)
        {
            if (payload == null)
            {
                var keys = @"[ ""contrast"", ""backlight"" ]";

                var json = @"{ ""category"": ""picture"", ""keys"": " + keys + @", ""subscribe"": true }";

                payload = JObject.Parse(json);
            }

            var requestMessage = new RequestMessage("ssap://settings/getSystemSettings", payload, "subscribe");

            await _connection.SubscribeAsync(requestMessage, callback);
        }

        public async Task<IEnumerable<App>> GetLaunchPoints()
        {
            _appList = new List<App>();
            var requestMessage = new RequestMessage("launcher", "ssap://com.webos.applicationManager/listLaunchPoints");
            var command = _connection.SendCommandAsync(requestMessage);
            var res = await command;
            foreach (var c in res.launchpoints)
            {
                _appList.Add(new App
                {
                    Id = c.id,
                    LaunchPointId = c.launchPointId,
                    Title = c.title,
                    Icon = c.icon
                });
            }
            return _appList.OrderBy(e => e.Title);
        }

        public async Task<IEnumerable<string>> GetServiceList()
        {
            var list = new List<string>();
            var requestMessage = new RequestMessage("channels", "ssap://api/getServiceList");
            var command = _connection.SendCommandAsync(requestMessage);
            var res = await command;
            return list;
        }

        public async Task<IEnumerable<LgApp>> GetApps(bool force = false)
        {
            var appList = new List<LgApp>();

            dynamic res;

            var filename = Path.Combine(Utils.GetDataPath(), _ip + "_listApps.json");
            if (!force && File.Exists(filename))
            {
                var json = File.ReadAllText(filename);
                res = JToken.Parse(json);
            }
            else
            {
                var requestMessage = new RequestMessage("ssap://com.webos.applicationManager/listApps", new { });
                var command = _connection.SendCommandAsync(requestMessage);
                res = await command;

                var json = Convert.ToString(res);
                File.WriteAllText(filename, json);
            }

            foreach (var c in res.apps)
            {
                appList.Add(new LgApp((string)c.id, (string)c.title));
            }

            return appList.OrderBy(e => e.title);
        }

        public async Task<string> LaunchApp(string appId)
        {
            var requestMessage = new RequestMessage("ssap://system.launcher/launch", new { id = appId });
            var response = await _connection.SendCommandAsync(requestMessage);
            return (string)response.sessionId;
        }
        public async Task<string> CloseApp(string appId)
        {
            var requestMessage = new RequestMessage("ssap://system.launcher/close", new { id = appId });
            var response = await _connection.SendCommandAsync(requestMessage);
            return (string)response.sessionId;
        }
        public async Task<string> OpenWebBrowser(Uri uri)
        {
            var url = uri.ToString();
            var requestMessage = new RequestMessage("ssap://system.launcher/open", new { target = url });
            var response = await _connection.SendCommandAsync(requestMessage);
            return (string)response.sessionId;
        }

        public async Task<string> LaunchYouTube(string videoId)
        {
            return await LaunchYouTube(new Uri($"http://www.youtube.com/tv?v={videoId}"));
        }
        public async Task<string> LaunchYouTube(Uri uri)
        {
            var url = uri.ToString();
            var requestMessage = new RequestMessage("ssap://system.launcher/launch", new { id = "youtube.leanback.v4", @params = new { contentTarget = url } });
            var response = await _connection.SendCommandAsync(requestMessage);
            return (string)response.sessionId;
        }

        public async Task SetSystemSettings(string key, object value, string category = "picture")
        {
            string jsonValue;
            var valueType = value.GetType();
            var intType = typeof(int);
            if (valueType.IsArray && intType.IsAssignableFrom(valueType.GetElementType()))
            {
                var values = ((Array)value).Cast<int>();
                jsonValue = "[" + string.Join(", ", values.Select(x => x.ToString()).ToArray()) + ']';
            }
            else
            {
                jsonValue = $"\"{value}\"";
            }

            var lunauri = "luna://com.webos.settingsservice/setSystemSettings";

            var json = @"{ ""category"": """ + category + @""", ""settings"": { """ + key + @""": " + jsonValue + @" } }";

            var @params = JObject.Parse(json);

            await ExecuteRequest(lunauri, @params);
        }

        public async Task SetConfig(string key, string value)
        {
            var lunauri = "luna://com.webos.service.config/setConfigs";

            var @params = JObject.Parse(@"{ ""configs"": { """ + key + @""": """ + value + @""" } }");

            await ExecuteRequest(lunauri, @params);
        }

        public async Task SetConfig(string key, bool value)
        {
            var lunauri = "luna://com.webos.service.config/setConfigs";

            var @params = JObject.Parse(@"{ ""configs"": { """ + key + @""": " + value.ToString().ToLowerInvariant() + @" } }");

            await ExecuteRequest(lunauri, @params);
        }

        private async Task ExecuteRequest(string lunauri, object @params)
        {
            var buttons = new[]
            {
                new {
                    label = "",
                    onClick = lunauri,
                    @params = @params
                }
            };

            var payload = new
            {
                message = "Applying...",
                buttons = buttons,
                onclose = new { uri = lunauri, @params = @params },
                onfail = new { uri = lunauri, @params = @params }
            };

            var requestMessage = new RequestMessage("ssap://system.notifications/createAlert", payload);
            var response = await _connection.SendCommandAsync(requestMessage);

            var alertId = (string)response.alertId;
            if (alertId != null)
            {
                var closeAlert = new
                {
                    alertId = alertId
                };
                requestMessage = new RequestMessage("ssap://system.notifications/closeAlert", closeAlert);
                await _connection.SendCommandAsync(requestMessage);
            }
        }

        public async Task<dynamic> GetSystemInfo(params string[] keys)
        {
            var payload = new
            {
                keys = keys
            };

            var requestMessage = new RequestMessage("ssap://system/getSystemInfo", payload);
            var command = _connection.SendCommandAsync(requestMessage);
            var res = await command;
            return res;
        }

        public async Task<dynamic> GetSystemSettings(string category, params string[] keys)
        {
            var payload = new
            {
                category = category,
                keys = keys
            };

            var requestMessage = new RequestMessage("ssap://settings/getSystemSettings", payload);
            var command = _connection.SendCommandAsync(requestMessage);
            var res = await command;
            return res;
        }

        public void Close()
        {
            _connection.Close();
        }
        public void Dispose()
        {
           _connection?.Dispose();
        }
    }
}