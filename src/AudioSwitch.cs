// Stream Deck plugin: switches the Windows default playback device (one key per device, or one
// key toggling between two devices) and opens the classic "Sound" control panel.
//
// Also works from a command line (handy for testing):
//   AudioSwitch.exe list
//   AudioSwitch.exe set <device>
//   AudioSwitch.exe toggle <device A> <device B>
//   AudioSwitch.exe panel
// <device> is a device id or part of its name, as printed by "list".
//
// Written for the C# 5 compiler that ships with Windows (.NET Framework 4.x); see build.ps1.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AudioSwitch
{
    static class Program
    {
        static int Main(string[] args)
        {
            // Stream Deck starts plugins with: -port <n> -pluginUUID <id> -registerEvent <name> -info <json>
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i].StartsWith("-")) options[args[i].Substring(1)] = args[++i];

            string port;
            if (!options.TryGetValue("port", out port)) return Cli.Run(args);

            Log.Open(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AudioSwitch.log"));
            try
            {
                new Plugin().Run(int.Parse(port), options["pluginUUID"], options["registerEvent"]).Wait();
                return 0;
            }
            catch (Exception e)
            {
                Log.Write("stopped: " + e.GetBaseException().Message);
                return 1;
            }
        }
    }

    // ------------------------------------------------------------------ Stream Deck plugin

    sealed class Plugin
    {
        // Headphones/speakers keys switch to their one device and light up while it plays;
        // the toggle key flips between two devices and shows the one playing.
        const string HeadphonesAction = "com.nisemonox.audioswitch.headphones";
        const string SpeakersAction = "com.nisemonox.audioswitch.speakers";
        const string ToggleAction = "com.nisemonox.audioswitch.toggle";
        const string PanelAction = "com.nisemonox.audioswitch.panel";
        const int LongPressMs = 500;

        // Toggle key image when the default device is neither of the two chosen ones.
        // Colors are rgb() because '#' is not safe inside a data URI.
        const string OtherImage = "data:image/svg+xml;charset=utf8," +
            "<svg xmlns='http://www.w3.org/2000/svg' width='144' height='144' viewBox='0 0 144 144'>" +
            "<g fill='none' stroke='rgb(140,140,140)' stroke-width='6' stroke-linecap='round' stroke-linejoin='round'>" +
            "<path d='M38 56h14l20-16v52L52 76H38z'/><path d='M85 54a14 14 0 0 1 0 24'/><path d='M95 44a28 28 0 0 1 0 44'/>" +
            "</g></svg>";

        sealed class Key
        {
            public string Action;
            public bool Visible;                  // on the deck, not inside a multi action
            public Dictionary<string, object> Settings = new Dictionary<string, object>();
            public string Shown;                  // what the key shows ("A", "on", "unset", ...); null forces a redraw
            public bool Overridden = true;        // image/title may currently be the "other" look
            public int Press;                     // bumped on every key event to cancel stale long presses
            public bool Down, LongPressed;
            public bool InspectorOpen;
        }

        readonly ClientWebSocket socket = new ClientWebSocket();
        readonly BlockingCollection<string> outbox = new BlockingCollection<string>();
        readonly Dictionary<string, Key> keys = new Dictionary<string, Key>();
        readonly object gate = new object();
        Timer audioChanged;
        DeviceWatcher watcher;

        public async Task Run(int port, string pluginUuid, string registerEvent)
        {
            await socket.ConnectAsync(new Uri("ws://127.0.0.1:" + port), CancellationToken.None);
            Task sending = Task.Factory.StartNew(SendLoop, TaskCreationOptions.LongRunning);
            Post(new Dictionary<string, object> { { "event", registerEvent }, { "uuid", pluginUuid } });
            Log.Write("connected on port " + port);

            // Windows reports one change per device role, so coalesce bursts into a single refresh.
            audioChanged = new Timer(_ => Guard(() => Refresh(true)));
            watcher = new DeviceWatcher(() => audioChanged.Change(150, Timeout.Infinite));

            try { await ReceiveLoop(); }
            finally
            {
                watcher.Dispose();
                outbox.CompleteAdding();
            }
            await sending;
            Log.Write("disconnected");
        }

        async Task ReceiveLoop()
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                string json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                Guard(() => Handle(json));
            }
        }

        void SendLoop()
        {
            foreach (string json in outbox.GetConsumingEnumerable())
            {
                try { socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, CancellationToken.None).Wait(); }
                catch (Exception e) { Log.Write("send failed: " + e.GetBaseException().Message); }
            }
        }

        void Post(Dictionary<string, object> message)
        {
            try { outbox.Add(Json.Serialize(message)); }
            catch (InvalidOperationException) { }   // shutting down
        }

        static Dictionary<string, object> Event(string name, string context, Dictionary<string, object> payload)
        {
            var message = new Dictionary<string, object> { { "event", name }, { "context", context } };
            if (payload != null) message["payload"] = payload;
            return message;
        }

        void Handle(string json)
        {
            Dictionary<string, object> message = Json.Parse(json);
            string name = Json.Str(message, "event");
            string context = Json.Str(message, "context");
            string action = Json.Str(message, "action");
            Dictionary<string, object> payload = Json.Obj(message, "payload");

            switch (name)
            {
                case "willAppear":
                    lock (gate)
                    {
                        Key key = GetKey(context, action);
                        key.Visible = !Json.Bool(payload, "isInMultiAction");
                        key.Settings = Json.Obj(payload, "settings");
                        key.Shown = null;
                        key.Overridden = true;
                    }
                    Log.Write("key " + action + " at " + Position(payload));
                    Refresh(false);
                    break;

                case "willDisappear":
                    lock (gate) keys.Remove(context);
                    break;

                case "didReceiveSettings":
                    lock (gate)
                    {
                        Key key = GetKey(context, action);
                        key.Settings = Json.Obj(payload, "settings");
                        key.Shown = null;
                    }
                    Refresh(false);
                    break;

                case "keyDown":
                    if (action == PanelAction) Task.Run(() => Guard(SoundPanel.Open));
                    else if (IsSwitch(action)) SwitchKeyDown(context, action);
                    break;

                case "keyUp":
                    if (IsSwitch(action)) SwitchKeyUp(context, action, Json.Obj(payload, "settings"));
                    break;

                case "propertyInspectorDidAppear":
                    lock (gate) GetKey(context, action).InspectorOpen = true;
                    SendDevices(context, action);
                    break;

                case "propertyInspectorDidDisappear":
                    lock (gate) GetKey(context, action).InspectorOpen = false;
                    break;

                case "sendToPlugin":
                    if (Json.Str(payload, "command") == "getDevices") SendDevices(context, action);
                    break;

                case "systemDidWakeUp":
                    lock (gate) foreach (Key key in keys.Values) key.Shown = null;
                    Refresh(true);
                    break;
            }
        }

        // Callers hold the gate.
        Key GetKey(string context, string action)
        {
            Key key;
            if (!keys.TryGetValue(context, out key)) keys[context] = key = new Key { Action = action };
            return key;
        }

        static bool IsSwitch(string action)
        {
            return action == HeadphonesAction || action == SpeakersAction || action == ToggleAction;
        }

        static string Position(Dictionary<string, object> payload)
        {
            object column, row;
            Dictionary<string, object> coordinates = Json.Obj(payload, "coordinates");
            coordinates.TryGetValue("column", out column);
            coordinates.TryGetValue("row", out row);
            return column + "," + row;
        }

        void SwitchKeyDown(string context, string action)
        {
            Key key;
            int press;
            lock (gate)
            {
                key = GetKey(context, action);
                press = ++key.Press;
                key.Down = true;
                key.LongPressed = false;
            }
            // Holding a switch key opens the Sound panel instead of switching.
            Task.Delay(LongPressMs).ContinueWith(_ =>
            {
                lock (gate)
                {
                    if (key.Press != press || !key.Down) return;
                    key.LongPressed = true;
                }
                Guard(SoundPanel.Open);
            });
        }

        void SwitchKeyUp(string context, string action, Dictionary<string, object> settings)
        {
            bool longPressed;
            lock (gate)
            {
                Key key = GetKey(context, action);
                key.Press++;
                key.Down = false;
                longPressed = key.LongPressed;
                key.LongPressed = false;
            }
            if (longPressed) return;
            if (action == ToggleAction) Task.Run(() => Guard(() => Toggle(context, settings)));
            else Task.Run(() => Guard(() => SwitchTo(context, Resolve(settings, "", Audio.GetOutputDevices()))));
        }

        void Toggle(string context, Dictionary<string, object> settings)
        {
            List<AudioDevice> devices = Audio.GetOutputDevices();
            string current = Audio.GetDefaultOutputId();
            string a = Resolve(settings, "A", devices), b = Resolve(settings, "B", devices);

            // A playing -> B; anything else -> A (or B when A is unplugged).
            SwitchTo(context, Same(current, a) ? b : Same(current, b) ? a : a ?? b);
        }

        void SwitchTo(string context, string device)
        {
            if (device == null)
            {
                Log.Write("switch: the device is not set or not connected");
                Post(Event("showAlert", context, null));
                return;
            }
            try { Audio.SetDefaultOutput(device); }
            catch (Exception e)
            {
                Log.Write("switch: " + e.Message);
                Post(Event("showAlert", context, null));
                return;
            }
            Refresh(false);
        }

        // Redraws keys whose device changed; optionally pushes the device list to open inspectors.
        void Refresh(bool pushDevices)
        {
            lock (gate)
            {
                List<AudioDevice> devices = Audio.GetOutputDevices();
                string current = Audio.GetDefaultOutputId();
                foreach (KeyValuePair<string, Key> entry in keys)
                {
                    if (IsSwitch(entry.Value.Action) && entry.Value.Visible) Render(entry.Key, entry.Value, devices, current);
                    if (pushDevices && entry.Value.InspectorOpen) PostDevices(entry.Key, entry.Value.Action, devices, current);
                }
            }
        }

        void Render(string context, Key key, List<AudioDevice> devices, string current)
        {
            if (key.Action == ToggleAction) RenderToggle(context, key, devices, current);
            else RenderDevice(context, key, devices, current);
        }

        // State 1 (lit) while the key's device is playing, state 0 otherwise.
        void RenderDevice(string context, Key key, List<AudioDevice> devices, string current)
        {
            string shown = !IsSet(key.Settings, "") ? "unset" : Same(current, Resolve(key.Settings, "", devices)) ? "on" : "off";
            if (shown == key.Shown) return;
            key.Shown = shown;

            Post(Event("setState", context, new Dictionary<string, object> { { "state", shown == "on" ? 1 : 0 } }));
            if (shown == "unset")
            {
                Post(Event("setTitle", context, new Dictionary<string, object> { { "title", "未设置" } }));
                key.Overridden = true;
            }
            else if (key.Overridden)
            {
                Post(Event("setTitle", context, new Dictionary<string, object>()));
                key.Overridden = false;
            }
        }

        void RenderToggle(string context, Key key, List<AudioDevice> devices, string current)
        {
            string shown;
            if (!IsSet(key.Settings, "A") || !IsSet(key.Settings, "B")) shown = "unset";
            else if (Same(current, Resolve(key.Settings, "A", devices))) shown = "A";
            else if (Same(current, Resolve(key.Settings, "B", devices))) shown = "B";
            else shown = "other";
            if (shown == key.Shown) return;
            key.Shown = shown;

            if (shown == "A" || shown == "B")
            {
                Post(Event("setState", context, new Dictionary<string, object> { { "state", shown == "A" ? 0 : 1 } }));
                if (key.Overridden)
                {
                    // Leaving out image/title restores the state's own image and title.
                    Post(Event("setImage", context, new Dictionary<string, object>()));
                    Post(Event("setTitle", context, new Dictionary<string, object>()));
                    key.Overridden = false;
                }
            }
            else
            {
                Post(Event("setImage", context, new Dictionary<string, object> { { "image", OtherImage } }));
                Post(Event("setTitle", context, new Dictionary<string, object> { { "title", shown == "unset" ? "未设置" : "其他设备" } }));
                key.Overridden = true;
            }
        }

        void SendDevices(string context, string action)
        {
            lock (gate) PostDevices(context, action, Audio.GetOutputDevices(), Audio.GetDefaultOutputId());
        }

        void PostDevices(string context, string action, List<AudioDevice> devices, string current)
        {
            List<object> list = devices.Select(d => (object)new Dictionary<string, object>
            {
                { "id", d.Id }, { "name", d.Name }, { "isDefault", Same(d.Id, current) }
            }).ToList();
            var message = Event("sendToPropertyInspector", context, new Dictionary<string, object> { { "type", "devices" }, { "devices", list } });
            message["action"] = action;
            Post(message);
        }

        // Settings keep both the device id and its name, so a device that comes back with a new id is still found.
        static string Resolve(Dictionary<string, object> settings, string slot, List<AudioDevice> devices)
        {
            string id = Json.Str(settings, "device" + slot), name = Json.Str(settings, "device" + slot + "Name");
            AudioDevice device = devices.FirstOrDefault(d => Same(d.Id, id)) ?? devices.FirstOrDefault(d => name != null && d.Name == name);
            return device == null ? null : device.Id;
        }

        static bool IsSet(Dictionary<string, object> settings, string slot)
        {
            return !string.IsNullOrEmpty(Json.Str(settings, "device" + slot));
        }

        static bool Same(string x, string y)
        {
            return x != null && y != null && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        }

        static void Guard(Action work)
        {
            try { work(); }
            catch (Exception e) { Log.Write(e.ToString()); }
        }
    }

    // ------------------------------------------------------------------ Command line

    static class Cli
    {
        public static int Run(string[] args)
        {
            Native.UseParentConsole();
            try
            {
                string command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
                if (command == "list" && args.Length == 1) { }
                else if (command == "set" && args.Length == 2) Audio.SetDefaultOutput(Find(args[1]).Id);
                else if (command == "toggle" && args.Length == 3)
                {
                    AudioDevice a = Find(args[1]), b = Find(args[2]);
                    bool onA = string.Equals(Audio.GetDefaultOutputId(), a.Id, StringComparison.OrdinalIgnoreCase);
                    Audio.SetDefaultOutput(onA ? b.Id : a.Id);
                }
                else if (command == "panel" && args.Length == 1) { SoundPanel.Open(); return 0; }
                else
                {
                    Console.WriteLine("usage: AudioSwitch.exe list | set <device> | toggle <device A> <device B> | panel");
                    Console.WriteLine("       <device> is a device id or part of its name");
                    return 2;
                }

                string current = Audio.GetDefaultOutputId();
                foreach (AudioDevice device in Audio.GetOutputDevices())
                {
                    bool isDefault = string.Equals(device.Id, current, StringComparison.OrdinalIgnoreCase);
                    Console.WriteLine((isDefault ? "* " : "  ") + device.Name + "    " + device.Id);
                }
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine("error: " + e.Message);
                return 1;
            }
        }

        static AudioDevice Find(string query)
        {
            List<AudioDevice> devices = Audio.GetOutputDevices();
            AudioDevice exact = devices.FirstOrDefault(d =>
                string.Equals(d.Id, query, StringComparison.OrdinalIgnoreCase) || string.Equals(d.Name, query, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            List<AudioDevice> matches = devices.Where(d => d.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (matches.Count == 1) return matches[0];
            throw new ArgumentException(matches.Count == 0
                ? "no active playback device matches \"" + query + "\""
                : "\"" + query + "\" matches several devices: " + string.Join(", ", matches.Select(d => d.Name)));
        }
    }

    // ------------------------------------------------------------------ Sound control panel

    static class SoundPanel
    {
        static readonly object gate = new object();
        static Process lastPanel;

        public static void Open()
        {
            lock (gate)
            {
                IntPtr window = lastPanel != null && !lastPanel.HasExited ? Native.FindDialog(lastPanel.Id) : IntPtr.Zero;
                if (window == IntPtr.Zero)
                {
                    if (lastPanel != null) lastPanel.Dispose();
                    // Same as "control mmsys.cpl,,0"; the 0 selects the Playback tab. ShellExecute keeps the
                    // panel from inheriting our handles (e.g. Stream Deck's output pipes).
                    lastPanel = Process.Start(new ProcessStartInfo(
                        Path.Combine(Environment.SystemDirectory, "rundll32.exe"), "shell32.dll,Control_RunDLL mmsys.cpl,,0") { UseShellExecute = true });
                    Stopwatch clock = Stopwatch.StartNew();
                    while (window == IntPtr.Zero && clock.ElapsedMilliseconds < 5000 && !lastPanel.HasExited)
                    {
                        Thread.Sleep(30);
                        window = Native.FindDialog(lastPanel.Id);
                    }
                    // rundll32 exits right away when a Sound panel opened elsewhere is already running.
                    if (window == IntPtr.Zero) window = FindOtherPanel();
                }
                if (window != IntPtr.Zero) Native.BringToFront(window);
            }
        }

        static IntPtr FindOtherPanel()
        {
            using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'rundll32.exe'"))
            {
                foreach (ManagementBaseObject process in searcher.Get())
                {
                    string commandLine = process["CommandLine"] as string;
                    if (commandLine == null || commandLine.IndexOf("mmsys.cpl", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    IntPtr window = Native.FindDialog((int)(uint)process["ProcessId"]);
                    if (window != IntPtr.Zero) return window;
                }
            }
            return IntPtr.Zero;
        }
    }

    static class Native
    {
        delegate bool EnumWindowsProc(IntPtr window, IntPtr param);

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int handle);
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);

        const int SW_RESTORE = 9;
        const byte VK_MENU = 0x12;
        const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;

        // The visible dialog window ("#32770") owned by the given process.
        public static IntPtr FindDialog(int processId)
        {
            IntPtr found = IntPtr.Zero;
            var className = new StringBuilder(64);
            EnumWindows((window, param) =>
            {
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner != processId || !IsWindowVisible(window)) return true;
                GetClassName(window, className, className.Capacity);
                if (className.ToString() != "#32770") return true;
                found = window;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        public static void BringToFront(IntPtr window)
        {
            if (IsIconic(window)) ShowWindow(window, SW_RESTORE);
            if (GetForegroundWindow() == window || (SetForegroundWindow(window) && GetForegroundWindow() == window)) return;
            // Windows keeps background processes from taking focus; an ALT key press lifts that lock.
            keybd_event(VK_MENU, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
            SetForegroundWindow(window);
            keybd_event(VK_MENU, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        // This is a windowless exe, so borrow the caller's console for command line output.
        public static void UseParentConsole()
        {
            if (GetStdHandle(-11) == IntPtr.Zero) AttachConsole(-1);
            if (Console.IsOutputRedirected)
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        }
    }

    // ------------------------------------------------------------------ Core Audio

    sealed class AudioDevice
    {
        public string Id;
        public string Name;   // e.g. "スピーカー (iFi (by AMR) HD+ USB Audio)"
    }

    static class Audio
    {
        const int DeviceStateActive = 1;
        const int StgmRead = 0;
        const ushort VtLpwstr = 31;
        static readonly PropertyKey FriendlyNameKey = new PropertyKey("a45c254e-df1c-4efd-8020-67d146a850e0", 14);

        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);

        internal static IMMDeviceEnumerator NewEnumerator()
        {
            return (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        }

        public static List<AudioDevice> GetOutputDevices()
        {
            var devices = new List<AudioDevice>();
            IMMDeviceEnumerator enumerator = NewEnumerator();
            try
            {
                IMMDeviceCollection collection;
                Check(enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceStateActive, out collection));
                int count;
                Check(collection.GetCount(out count));
                for (int i = 0; i < count; i++)
                {
                    IMMDevice device;
                    if (collection.Item(i, out device) < 0) continue;
                    string id;
                    if (device.GetId(out id) >= 0) devices.Add(new AudioDevice { Id = id, Name = GetName(device) ?? id });
                    Marshal.ReleaseComObject(device);
                }
                Marshal.ReleaseComObject(collection);
            }
            finally { Marshal.ReleaseComObject(enumerator); }
            devices.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase));
            return devices;
        }

        // Null when there is no playback device at all.
        public static string GetDefaultOutputId()
        {
            IMMDeviceEnumerator enumerator = NewEnumerator();
            try
            {
                IMMDevice device;
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device) < 0) return null;
                string id;
                int hr = device.GetId(out id);
                Marshal.ReleaseComObject(device);
                return hr < 0 ? null : id;
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }

        // Like "Set Default" in the Sound panel: default device and default communication device.
        public static void SetDefaultOutput(string id)
        {
            var policy = (IPolicyConfig)new PolicyConfigComObject();
            try
            {
                foreach (ERole role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
                    Check(policy.SetDefaultEndpoint(id, role));
            }
            finally { Marshal.ReleaseComObject(policy); }
        }

        static string GetName(IMMDevice device)
        {
            IPropertyStore store;
            if (device.OpenPropertyStore(StgmRead, out store) < 0) return null;
            try
            {
                PropertyKey key = FriendlyNameKey;
                PropVariant value;
                if (store.GetValue(ref key, out value) < 0) return null;
                try { return value.VarType == VtLpwstr ? Marshal.PtrToStringUni(value.Pointer) : null; }
                finally { PropVariantClear(ref value); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }

        static void Check(int hr)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }
    }

    // Calls back (on a system thread) whenever playback devices or the default device change.
    sealed class DeviceWatcher : IMMNotificationClient, IDisposable
    {
        readonly Action changed;
        readonly IMMDeviceEnumerator enumerator = Audio.NewEnumerator();

        public DeviceWatcher(Action changed)
        {
            this.changed = changed;
            Marshal.ThrowExceptionForHR(enumerator.RegisterEndpointNotificationCallback(this));
        }

        public void Dispose() { enumerator.UnregisterEndpointNotificationCallback(this); }

        public void OnDeviceStateChanged(string deviceId, int newState) { changed(); }
        public void OnDeviceAdded(string deviceId) { changed(); }
        public void OnDeviceRemoved(string deviceId) { changed(); }
        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string defaultDeviceId) { if (flow == EDataFlow.Render) changed(); }
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    }

    static class Json
    {
        public static Dictionary<string, object> Parse(string json)
        {
            return new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        public static string Serialize(object value)
        {
            return new JavaScriptSerializer().Serialize(value);
        }

        public static string Str(Dictionary<string, object> map, string key)
        {
            object value;
            return map.TryGetValue(key, out value) ? value as string : null;
        }

        public static bool Bool(Dictionary<string, object> map, string key)
        {
            object value;
            return map.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> map, string key)
        {
            object value;
            return (map.TryGetValue(key, out value) ? value as Dictionary<string, object> : null) ?? new Dictionary<string, object>();
        }
    }

    static class Log
    {
        static readonly object gate = new object();
        static string path;

        public static void Open(string file)
        {
            path = file;
            try { if (File.Exists(file) && new FileInfo(file).Length > 256 * 1024) File.Delete(file); }
            catch (Exception) { }
        }

        public static void Write(string message)
        {
            if (path == null) return;
            lock (gate)
            {
                try { File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  ") + message + Environment.NewLine, Encoding.UTF8); }
                catch (Exception) { }
            }
        }
    }

    // ------------------------------------------------------------------ COM interop

    enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;

        public PropertyKey(string formatId, int propertyId)
        {
            FormatId = new Guid(formatId);
            PropertyId = propertyId;
        }
    }

    // PROPVARIANT: 16 bytes on x86, 24 on x64. Only the string pointer is read.
    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant
    {
        public ushort VarType;
        public ushort Reserved1, Reserved2, Reserved3;
        public IntPtr Pointer;
        public IntPtr Pointer2;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorComObject { }

    // Undocumented, but what the Sound control panel itself uses; stable since Windows 7.
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMNotificationClient
    {
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    // Only SetDefaultEndpoint is called; the other slots keep the vtable layout.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
    }
}
