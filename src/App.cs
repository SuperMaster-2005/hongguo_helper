using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace HongguoRemote
{
    internal static class App
    {
        [STAThread]
        static void Main()
        {
            DiagnosticLog.Current.Write("INFO", "程序启动；版本 " + typeof(App).Assembly.GetName().Version + "; Windows " + Environment.OSVersion + "; CLR " + Environment.Version);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => DiagnosticLog.Current.Write("ERROR", "未处理异常：" + e.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (s, e) => DiagnosticLog.Current.Write("ERROR", "后台任务异常：" + e.Exception);
            var application = new Application();
            application.Exit += (s, e) => DiagnosticLog.Current.Write("INFO", "程序退出");
            application.DispatcherUnhandledException += (s, e) => {
                DiagnosticLog.Current.Write("ERROR", e.Exception.ToString());
                MessageBox.Show(e.Exception.Message, "红果遥控端", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            try { new Controller(application).Run(); }
            catch (Exception e) { DiagnosticLog.Current.Write("ERROR", e.ToString()); MessageBox.Show(e.Message + "\r\n日志目录：" + DiagnosticLog.Current.DirectoryPath, "启动失败"); }
        }
    }

    internal sealed class Controller
    {
        readonly Application app;
        readonly Window window;
        readonly DeviceBridge bridge;
        readonly StringBuilder logs = new StringBuilder();
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        readonly Forms.Panel panel = new Forms.Panel { BackColor = System.Drawing.Color.FromArgb(8, 9, 13) };
        readonly WindowsFormsHost host;
        Native.InputHooks hooks;
        Process mirror;
        IntPtr video;
        string activeSerial;
        bool busy, inputBusy, closing, fullscreen, embedded;
        bool automaticDiscovery=true;
        DateTime nextAutomaticDiscovery;
        WindowState previousState;
        Rect previousBounds;
        bool connectionPanelOpen = true;
        volatile bool contentVisible;
        bool companionControlEnabled;
        int attachTries;
        DateTime startedAt;
        CompanionClient companion;
        PcmPlayer pcm;
        int framePending;
        bool audioFailed;
        Point? touchStart;
        DateTime touchTime;
        H264Decoder h264;
        TimedAudioPlayer timedAudio;
        MediaClock mediaClock;
        readonly object decodedLock=new object();
        readonly Queue<DecodedFrame> decoded=new Queue<DecodedFrame>();
        WriteableBitmap videoBitmap;
        readonly VideoCrop crop;
        DecodedFrame pendingVideoFrame;
        TimeSpan lastVideoRenderingTime=TimeSpan.MinValue;
        readonly Stopwatch playbackStats=Stopwatch.StartNew();
        int shownFrames,receivedFrames,decodedFrames;
        readonly SourceFrameTiming sourceTiming=new SourceFrameTiming();
        long statsWindows;
        int skippedVideoFrames,busyVideoFrames;
        double submitMilliseconds,maxSubmitMilliseconds,maxSubmitGap;
        long lastSubmit;
        DateTime previousVideoPerformanceLog;
        long lastSyncRequest;

        T Get<T>(string name) where T : FrameworkElement { return (T)window.FindName(name); }
        void Text(string name, string value) { Get<TextBlock>(name).Text = value; }
        void Click(string name, Action action) { Get<Button>(name).Click += (s, e) => action(); }
        Device Selected { get { return Get<ComboBox>("Devices").SelectedItem as Device; } }
        bool Watching { get { return Get<CheckBox>("WatchMode").IsChecked == true; } }
        bool Running { get { return (companion != null && companion.Active) || (mirror != null && !mirror.HasExited); } }
        bool Controllable { get { return (companion != null && companion.Connected && contentVisible && companionControlEnabled) || video != IntPtr.Zero; } }

        internal Controller(Application application)
        {
            app = application;
            using (var stream = typeof(App).Assembly.GetManifestResourceStream("MainWindow.xaml"))
                window = (Window)XamlReader.Load(stream);
            string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            bridge = new DeviceBridge(Path.Combine(root, "tools", "scrcpy-win64-v5.0.1"));
            host = new WindowsFormsHost { Child = panel };
            Get<Border>("VideoHost").Child = host;
            panel.Resize += (s, e) => ResizeVideo();
            panel.Click += (s, e) => { if (video != IntPtr.Zero) Native.SetFocus(video); };
            Click("Refresh", () => Work(Refresh));
            Click("AutoFind", () => Work(FindCompanion, false));
            Click("FindAdb", () => Work(AutoFind));
            Click("AutoPair", () => Work(AutoPair));
            Get<ComboBox>("WirelessChoices").SelectionChanged += (s, e) => {
                var service = Get<ComboBox>("WirelessChoices").SelectedItem as WirelessService;
                bool pairing = service != null && service.Pairing;
                Get<PasswordBox>("AutoCode").Clear();
                Get<PasswordBox>("AutoCode").Visibility = Get<TextBlock>("AutoCodeLabel").Visibility = pairing ? Visibility.Visible : Visibility.Collapsed;
                Get<Button>("AutoPair").Content = pairing ? "配对并连接" : "连接这台手机";
            };
            Click("Start", Start);
            Click("Stop", () => { Stop(); SetStatus("投屏已结束，手机可继续使用。"); });
            Click("Pair", () => Work(async () => {
                string code = Get<PasswordBox>("PairCode").Password;
                try { SetStatus(await bridge.Pair(Get<TextBox>("PairAddress").Text, code)); }
                finally { Get<PasswordBox>("PairCode").Clear(); }
            }));
            Click("Connect", () => Work(async () => {
                string endpoint = DeviceBridge.ValidateEndpoint(Get<TextBox>("ConnectAddress").Text);
                string result = await bridge.Connect(endpoint);
                await Refresh();
                var devices = Get<ComboBox>("Devices");
                foreach (Device device in devices.Items) if (device.Serial == endpoint) devices.SelectedItem = device;
                SetStatus(result + "；选中无线设备后点击开始投屏。");
            }));
            Click("EnableTcp", () => Work(async () => {
                Device device = ReadyDevice();
                if (device.Wireless) throw new InvalidOperationException("请先选择已授权的 USB 设备。");
                if (Running) throw new InvalidOperationException("请先结束投屏，再切换连接方式。");
                string result = await bridge.EnableTcp(device.Serial);
                SetStatus(result + "。请输入手机当前 IP:5555，连接成功后再拔 USB。仅在可信网络使用，结束后可恢复 USB 模式。");
                await Refresh(false);
            }));
            Click("RestoreUsb", () => Work(async () => {
                if (Running) throw new InvalidOperationException("请先结束投屏。");
                string result = await bridge.RestoreUsb(ReadyDevice().Serial);
                await Refresh(false); SetStatus(result + "；5555 监听已请求关闭，USB 线连接后刷新设备。");
            }));
            Click("Fullscreen", ToggleFullscreen);
            Click("ConnectionToggle", () => { connectionPanelOpen=!connectionPanelOpen; UpdateLayoutMode(); });
            window.SizeChanged += (s,e) => UpdateLayoutMode();
            window.Activated += (s,e) => UpdateLayoutMode();
            window.Deactivated += (s,e) => Get<System.Windows.Controls.Primitives.Popup>("ExitFullPopup").IsOpen=false;
            Click("ExitFull", ToggleFullscreen);
            Click("Help", Help);
            Click("Logs", ShowLogs);
            var companionImage = Get<System.Windows.Controls.Image>("CompanionImage");
            crop = new VideoCrop(window, companionImage);
            Click("Crop", () => crop.Edit(true));
            companionImage.MouseLeftButtonDown += (s,e) => {
                if (companion == null || !companion.Connected || !contentVisible) return;
                touchStart = ImagePoint(e.GetPosition(companionImage)); touchTime = DateTime.UtcNow;
                if (touchStart.HasValue) { companionImage.Focus(); companionImage.CaptureMouse(); e.Handled = true; }
            };
            companionImage.MouseLeftButtonUp += (s,e) => {
                Point? end = ImagePoint(e.GetPosition(companionImage)); companionImage.ReleaseMouseCapture();
                if (touchStart.HasValue && end.HasValue) {
                    Point first = touchStart.Value, last = end.Value;
                    string command = (first-last).Length < 100 ? "tap|"+N(first.X)+"|"+N(first.Y) : "swipe|"+N(first.X)+"|"+N(first.Y)+"|"+N(last.X)+"|"+N(last.Y)+"|"+Math.Max(100,Math.Min(1500,(int)(DateTime.UtcNow-touchTime).TotalMilliseconds));
                    SendCompanion(command);
                }
                touchStart = null; e.Handled = true;
            };
            companionImage.MouseWheel += (s,e) => { Swipe(e.Delta < 0); e.Handled=true; };
            timer.Tick += (s, e) => Tick();
            window.PreviewKeyDown += (s, e) => {
                if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; }
                else if (e.Key == Key.Escape && fullscreen) { ToggleFullscreen(); e.Handled = true; }
                else if (companionImage.IsKeyboardFocused && companion != null && companion.Connected && contentVisible && Watching && Keyboard.Modifiers == ModifierKeys.None) {
                    if (e.Key == Key.Up || e.Key == Key.Down) { Swipe(e.Key == Key.Down); e.Handled = true; }
                }
            };
            System.Windows.Media.CompositionTarget.Rendering+=RenderVideo;
            window.Closing += (s, e) => { closing = true; Get<System.Windows.Controls.Primitives.Popup>("ExitFullPopup").IsOpen=false; System.Windows.Media.CompositionTarget.Rendering-=RenderVideo; timer.Stop(); if (hooks != null) hooks.Dispose(); Stop(); };
            window.Loaded += (s, e) => {
                try {
                    hooks = new Native.InputHooks { Target = () => video, Watch = () => Watching,
                        Key = key => window.Dispatcher.BeginInvoke(new Action(() => HandleKey(key))),
                        Wheel = next => window.Dispatcher.BeginInvoke(new Action(() => Input(() => bridge.Swipe(activeSerial, next)))) };
                } catch (Exception error) { DiagnosticLog.Current.Write("ERROR", error.ToString()); Log(error.Message); }
                SetStatus("打开手机助手并开始共享，电脑会自动查找并请求连接。"); timer.Start();
            };
            UpdateControls();
        }
        internal void Run() { app.Run(window); }

        static string N(double n) { return ((int)Math.Max(0,Math.Min(10000,n))).ToString(System.Globalization.CultureInfo.InvariantCulture); }
        Point? ImagePoint(Point point)
        {
            var image = Get<System.Windows.Controls.Image>("CompanionImage"); var source=image.Source as BitmapSource;
            if (source==null || image.ActualWidth<=0 || image.ActualHeight<=0) return null;
            double scale=Math.Min(image.ActualWidth/source.PixelWidth,image.ActualHeight/source.PixelHeight);
            double width=source.PixelWidth*scale,height=source.PixelHeight*scale;
            double x=point.X-(image.ActualWidth-width)/2,y=point.Y-(image.ActualHeight-height)/2;
            if (x<0 || y<0 || x>width || y>height) return null;
            return crop.Map(new Point(x/width*10000,y/height*10000));
        }
        Task FindCompanion() { return FindCompanion(false); }
        async Task FindCompanion(bool automatic)
        {
            Get<StackPanel>("AutoPairPanel").Visibility=Visibility.Collapsed;
            if(!automatic) DiscoveryStatus("正在寻找手机助手…请在手机上点开始共享。");
            var devices=await CompanionClient.Discover();
            if (closing || (automatic && !automaticDiscovery) || Running) return;
            var choices=Get<ComboBox>("CompanionChoices"); choices.ItemsSource=devices;
            choices.Visibility=devices.Count>1 ? Visibility.Visible : Visibility.Collapsed;
            choices.SelectedIndex=devices.Count==1 ? 0 : -1;
            nextAutomaticDiscovery=DateTime.UtcNow.AddSeconds(5);
            if (devices.Count==0) {
                if(!automatic) DiscoveryStatus("未找到手机助手。请先安装并打开手机 App，点开始共享；电脑保持连接手机热点，再重试。");
            }
            else if (devices.Count == 1) {
                automaticDiscovery=false;
                DiscoveryStatus("已找到 " + devices[0].Model + "，正在请求连接。");
                StartCompanion(devices[0]);
            } else {
                automaticDiscovery=false;
                DiscoveryStatus("找到多台手机，请选择你的手机后开始投屏。");
            }
        }
        void Ui(Action action) { if (!closing && !window.Dispatcher.HasShutdownStarted) window.Dispatcher.BeginInvoke(new Action(() => { if (!closing) action(); })); }
        void StartCompanion(CompanionDevice device)
        {
            Stop(); pcm=new PcmPlayer(); audioFailed=false;
            mediaClock=new MediaClock();var timeline=mediaClock;var sound=pcm;
            timedAudio=new TimedAudioPlayer(timeline,sound);var audioPlayback=timedAudio;
            var session=new CompanionClient(); companion=session;sourceTiming.Reset();
            var decoderPath=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","tools","ffmpeg","ffmpeg.exe"));
            try {h264=new H264Decoder(decoderPath);}catch(Exception e){DiagnosticLog.Current.Write("ERROR",e.ToString());Stop();SetStatus(e.Message);return;}
            var decoder=h264;
            decoder.RequestKeyframe=()=>{
                long now=DateTime.UtcNow.Ticks;
                if(now-System.Threading.Interlocked.Read(ref lastSyncRequest)>TimeSpan.TicksPerMillisecond*500){System.Threading.Interlocked.Exchange(ref lastSyncRequest,now);session.Send("keyframe").ContinueWith(t=>{var ignored=t.Exception;},System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);}
            };
            long lastWarning = 0;
            decoder.Warning = message => {
                long now = Environment.TickCount & Int32.MaxValue;
                if (lastWarning != 0 && now >= lastWarning && now-lastWarning < 1000) return;
                lastWarning = now;
                Ui(()=>{ if(companion==session) Log(message); });
                decoder.RequestKeyframe();
            };
            int firstPicture = 0;
            decoder.Error=message=>Ui(()=>{if(companion==session){Log(message);SetStatus(message);}});
            decoder.Frame=frame=>{
                if(System.Threading.Interlocked.Exchange(ref firstPicture,1)==0) Ui(()=>Log("已解码第一帧"));
                lock(decodedLock){if(companion!=session||!contentVisible){frame.Dispose();return;}while(decoded.Count>=18)decoded.Dequeue().Dispose();decoded.Enqueue(frame);System.Threading.Interlocked.Increment(ref decodedFrames);}
            };
            session.VideoFormatChanged=format=>{
                sourceTiming.Reset();
                Ui(()=>Log("收到视频格式 "+format.Width+"×"+format.Height+" @ "+format.Fps));
                decoder.Configure(format);
                Ui(()=>{if(companion==session){lock(decodedLock){while(decoded.Count>0)decoded.Dequeue().Dispose();}if(pendingVideoFrame!=null){pendingVideoFrame.Dispose();pendingVideoFrame=null;}videoBitmap=null;Text("VideoStats","H.264 · "+format.Width+"×"+format.Height+" · 目标 "+format.Fps+" 帧");}});
            };
            session.Video=frame=>{if(contentVisible){System.Threading.Interlocked.Increment(ref receivedFrames);sourceTiming.Observe(frame.Timestamp);timeline.Observe(frame.Timestamp);decoder.Enqueue(frame);}};
            session.TimedAudio=(timestamp,data)=>{if(contentVisible)audioPlayback.Add(timestamp,data);};
            session.Sharing=visible=>{
                if(companion!=session)return;
                contentVisible=visible; decoder.Reset(); audioPlayback.Reset(); sourceTiming.Reset();
                lock(decodedLock){while(decoded.Count>0)decoded.Dequeue().Dispose();}
                Ui(()=>{if(companion!=session || contentVisible!=visible)return;
                    shownFrames=0;System.Threading.Interlocked.Exchange(ref receivedFrames,0);System.Threading.Interlocked.Exchange(ref decodedFrames,0);statsWindows=0;lastSubmit=0;skippedVideoFrames=busyVideoFrames=0;submitMilliseconds=maxSubmitMilliseconds=maxSubmitGap=0;playbackStats.Restart();
                    if(pendingVideoFrame!=null){pendingVideoFrame.Dispose();pendingVideoFrame=null;}videoBitmap=null; crop.Display(null);
                    Get<System.Windows.Controls.Image>("CompanionImage").Visibility=visible?Visibility.Visible:Visibility.Collapsed;
                    Get<StackPanel>("EmptyState").Visibility=visible?Visibility.Collapsed:Visibility.Visible;
                    Text("EmptyTitle","等待手机端打开红果短剧");Text("EmptyHint","连接已保留，打开红果后自动恢复播放");
                    Text("PlaybackFps",visible?"正在接收画面":"已暂停共享"); UpdateControls();
                });
            };
            audioPlayback.Error=message=>Ui(()=>{if(companion==session)SetStatus(message);});
            shownFrames=0;System.Threading.Interlocked.Exchange(ref receivedFrames,0);System.Threading.Interlocked.Exchange(ref decodedFrames,0);statsWindows=0;lastSubmit=0;skippedVideoFrames=busyVideoFrames=0;submitMilliseconds=maxSubmitMilliseconds=maxSubmitGap=0;playbackStats.Restart();
            session.ControlEnabled=enabled=>Ui(()=>{
                if(companion!=session)return;
                companionControlEnabled=enabled;
                Text("ConnectionStatus",(enabled?"手机助手已连接 · ":"仅观看 · ")+device.Model);
                UpdateControls();
            });
            session.Status=message=>Ui(()=> {
                if (companion!=session) return;
                DiscoveryStatus(message);
                if(message.Contains("请在手机助手中允许")) Text("EmptyTitle","请在手机助手中点「允许本次」");
            });
            session.Accepted=()=>Ui(()=> {
                if (companion!=session) return;
                connectionPanelOpen=false;UpdateLayoutMode();
                Text("EmptyTitle","等待手机端打开红果短剧");
                Text("EmptyHint","连接已保留，打开红果后自动恢复播放");
                Text("ConnectionStatus","手机助手已连接 · "+device.Model); UpdateControls();
            });
            session.Frame=bytes=> {
                if(companion!=session || !contentVisible)return;
                if (System.Threading.Interlocked.CompareExchange(ref framePending,1,0)!=0) return;
                try {
                    BitmapImage frame=new BitmapImage();
                    using (var data=new MemoryStream(bytes)) { frame.BeginInit(); frame.CacheOption=BitmapCacheOption.OnLoad; frame.StreamSource=data; frame.EndInit(); frame.Freeze(); }
                    Ui(()=> { try { if(companion==session && contentVisible) crop.Display(frame); } finally { System.Threading.Interlocked.Exchange(ref framePending,0); } });
                } catch { System.Threading.Interlocked.Exchange(ref framePending,0); throw; }
            };
            session.Audio=bytes=> { if(companion==session && contentVisible && !audioFailed) try { sound.Play(bytes); } catch(Exception e) {audioFailed=true; Ui(()=>SetStatus(e.Message));} };
            session.Ended=()=>Ui(()=> { if(companion==session) { Stop(); Text("ConnectionStatus","手机助手已断开"); } });
            Text("EmptyTitle","正在连接手机助手"); Text("EmptyHint","回到红果手机助手，点击「允许本次」。\n连接后再打开红果。");
            session.Start(device,true);
            SetStatus("正在建立连接，手机收到请求后会提示授权。"); UpdateControls();
        }
        void RenderVideo(object sender,EventArgs e)
        {
            if(companion==null||!companion.Connected||!contentVisible||mediaClock==null)return;
            var rendering=e as System.Windows.Media.RenderingEventArgs;
            // WPF may raise multiple callbacks for the same composition frame.
            if(rendering!=null){if(rendering.RenderingTime==lastVideoRenderingTime)return;lastVideoRenderingTime=rendering.RenderingTime;}
            DecodedFrame frame=pendingVideoFrame; pendingVideoFrame=null;
            lock(decodedLock){while(decoded.Count>0&&mediaClock.Delay(decoded.Peek().Timestamp)<=3){if(frame!=null){frame.Dispose();skippedVideoFrames++;}frame=decoded.Dequeue();}}
            if(frame!=null){
                try{
                    if(mediaClock.Delay(frame.Timestamp)>=-150){
                        if(videoBitmap==null||videoBitmap.PixelWidth!=frame.Width||videoBitmap.PixelHeight!=frame.Height){videoBitmap=new WriteableBitmap(frame.Width,frame.Height,96,96,System.Windows.Media.PixelFormats.Bgr32,null);crop.Display(videoBitmap);}
                        long submitStarted=Stopwatch.GetTimestamp();
                        // Bound UI waiting; retain the newest due frame if the compositor is still busy.
                        if(crop.Present(frame)){
                            frame=null;shownFrames++;
                            if(lastSubmit!=0)maxSubmitGap=Math.Max(maxSubmitGap,(submitStarted-lastSubmit)*1000.0/Stopwatch.Frequency);
                            lastSubmit=submitStarted;
                            double elapsed=(Stopwatch.GetTimestamp()-submitStarted)*1000.0/Stopwatch.Frequency;
                            submitMilliseconds+=elapsed;maxSubmitMilliseconds=Math.Max(maxSubmitMilliseconds,elapsed);
                        }else {busyVideoFrames++;pendingVideoFrame=frame;frame=null;}
                    }else skippedVideoFrames++;
                }finally{if(frame!=null)frame.Dispose();}
            }
            if(playbackStats.ElapsedMilliseconds>=1000){
                double seconds=playbackStats.Elapsed.TotalSeconds;
                string stats="接收 "+(System.Threading.Interlocked.Exchange(ref receivedFrames,0)/seconds).ToString("F0")+" · 解码 "+(System.Threading.Interlocked.Exchange(ref decodedFrames,0)/seconds).ToString("F0")+" · 播放 "+(shownFrames/seconds).ToString("F0")+" 帧/秒";
                string sourceStats=sourceTiming.TakeSummary();
                Text("PlaybackFps",stats);
                if(++statsWindows==1 || (DateTime.UtcNow-previousVideoPerformanceLog).TotalSeconds>=10){
                    DiagnosticLog.Current.Write("INFO","视频性能 BGR32："+stats+"；跳过 "+skippedVideoFrames+"；合成器忙 "+busyVideoFrames+"；提交平均/最大 "+(submitMilliseconds/Math.Max(1,shownFrames)).ToString("F2")+"/"+maxSubmitMilliseconds.ToString("F2")+"ms；最大提交间隔 "+maxSubmitGap.ToString("F1")+"ms；"+sourceStats);
                    previousVideoPerformanceLog=DateTime.UtcNow;
                }
                skippedVideoFrames=busyVideoFrames=0;submitMilliseconds=maxSubmitMilliseconds=maxSubmitGap=0;
                shownFrames=0;playbackStats.Restart();
            }
        }
        void SendCompanion(string command) { if(companion!=null) { var session=companion; Input(()=>session.Send(command)); } }
        void Swipe(bool next)
        {
            if(companion!=null) SendCompanion(next ? "swipe|5000|7200|5000|2800|280" : "swipe|5000|2800|5000|7200|280");
            else Input(()=>bridge.Swipe(activeSerial,next));
        }
        void DiscoveryStatus(string message)
        {
            Text("DiscoveryHint", message); SetStatus(message);
        }
        async Task AutoFind()
        {
            Get<ComboBox>("CompanionChoices").ItemsSource=null;
            Get<StackPanel>("AutoPairPanel").Visibility = Visibility.Collapsed;
            Get<PasswordBox>("AutoCode").Clear();
            DiscoveryStatus("正在查找手机，请保持手机的配对码页面打开…");
            await Refresh(false);
            if (closing) return;
            var ready = Get<ComboBox>("Devices").Items.Cast<Device>().Where(d => d.Ready && d.Wireless).ToList();
            if (ready.Count > 0)
            {
                if (ready.Count == 1) Get<ComboBox>("Devices").SelectedItem = ready[0];
                DiscoveryStatus(ready.Count == 1 ? "手机已连接！打开手机上的红果，然后点击开始投屏。" : "发现多台已连接手机，请在设备列表中选择要投屏的手机。");
                return;
            }
            var services = new List<WirelessService>();
            for (int i = 0; i < 3; i++)
            {
                services = await bridge.Discover();
                if (closing) return;
                if (services.Count > 0) break;
                if (i < 2) await Task.Delay(1200);
            }
            if (closing) return;
            // 同一手机同时公布配对和连接服务时，首次授权优先展示配对入口。
            var candidates = services.Where(s => s.Pairing || !services.Any(p => p.Pairing && p.Endpoint.Split(':')[0] == s.Endpoint.Split(':')[0])).ToList();
            if (candidates.Count > 0)
            {
                var choices = Get<ComboBox>("WirelessChoices");
                choices.ItemsSource = candidates;
                choices.SelectedIndex = candidates.Count == 1 ? 0 : -1;
                choices.Visibility = candidates.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                Get<StackPanel>("AutoPairPanel").Visibility = Visibility.Visible;
                DiscoveryStatus(candidates.Count > 1 ? "找到多台手机，请选择你的手机后继续。" : candidates[0].Pairing ? "找到手机了！只需输入手机上的 6 位配对码。" : "找到无线手机，点击「连接这台手机」。首次使用请先在手机打开配对码页面，再点自动查找。");
                return;
            }
            List<string> gateways;
            try { gateways = DeviceBridge.WifiGateways(); }
            catch (NetworkInformationException) { gateways = new List<string>(); }
            if (gateways.Count == 1 && await DeviceBridge.HasLegacyPort(gateways[0]))
            {
                if (closing) return;
                await ConnectDiscovered(gateways[0] + ":5555");
                return;
            }
            if (!closing) DiscoveryStatus("未找到手机。请在手机打开无线调试的配对码页面后重试。若提示必须连接 Wi-Fi，则当前系统不支持仅靠热点开启。");
        }
        async Task AutoPair()
        {
            var selected = Get<ComboBox>("WirelessChoices").SelectedItem as WirelessService;
            if (selected == null) throw new InvalidOperationException("请先选择你的手机。");
            if (!selected.Pairing) { await ConnectDiscovered(selected.Endpoint); return; }
            string code = Get<PasswordBox>("AutoCode").Password;
            try { await bridge.Pair(selected.Endpoint, code); }
            finally { Get<PasswordBox>("AutoCode").Clear(); }
            if (closing) return;
            DiscoveryStatus("配对成功，正在自动连接…");
            string ip = selected.Endpoint.Split(':')[0];
            for (int i = 0; i < 5; i++)
            {
                await Refresh(false);
                if (closing) return;
                var device = Get<ComboBox>("Devices").Items.Cast<Device>().FirstOrDefault(d => d.Ready && d.Serial.StartsWith(ip + ":", StringComparison.Ordinal));
                if (device != null)
                {
                    Get<ComboBox>("Devices").SelectedItem = device;
                    Get<StackPanel>("AutoPairPanel").Visibility = Visibility.Collapsed;
                    DiscoveryStatus("手机已连接！打开红果后点击开始投屏。"); return;
                }
                var services = await bridge.Discover();
                if (closing) return;
                var matching = services.Where(s => !s.Pairing && s.Endpoint.Split(':')[0] == ip).ToList();
                if (matching.Count == 1) { await ConnectDiscovered(matching[0].Endpoint); return; }
                if (i < 4) await Task.Delay(1000);
            }
            DiscoveryStatus("配对已完成，暂未发现连接服务。手机返回无线调试主页面，再点「自动查找手机」，无需重新输入地址。");
        }
        async Task ConnectDiscovered(string endpoint)
        {
            DiscoveryStatus("正在连接找到的手机…");
            await bridge.Connect(endpoint);
            for (int i = 0; i < 3; i++)
            {
                await Refresh(false);
                if (closing) return;
                var device = Get<ComboBox>("Devices").Items.Cast<Device>().FirstOrDefault(d => d.Ready && d.Serial == endpoint);
                if (device != null)
                {
                    Get<ComboBox>("Devices").SelectedItem = device;
                    Get<StackPanel>("AutoPairPanel").Visibility = Visibility.Collapsed;
                    DiscoveryStatus("手机已连接！打开手机上的红果，点击开始投屏。"); return;
                }
                if (i < 2) await Task.Delay(700);
            }
            DiscoveryStatus("手机尚未授权或就绪。首次连接请在手机打开配对码页面，再点自动查找。");
        }

        void Log(string message)
        {
            DiagnosticLog.Current.Write(message != null && (message.Contains("失败") || message.Contains("异常") || message.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0) ? "ERROR" : "INFO", message);
            message = DiagnosticLog.Sanitize(message);
            if (closing || window.Dispatcher.HasShutdownStarted) return;
            if (!window.Dispatcher.CheckAccess()) { window.Dispatcher.BeginInvoke(new Action(() => AppendLog(message))); return; }
            AppendLog(message);
        }
        void AppendLog(string message)
        {
            if (logs.Length > 18000) logs.Remove(0, logs.Length - 12000);
            logs.AppendLine(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
            if (message.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("WARN", StringComparison.OrdinalIgnoreCase) >= 0)
                Text("Status", "投屏提示：" + message + "（详见使用说明与诊断）");
        }
        void SetStatus(string message) { Text("Status", message); Get<TextBlock>("Status").ToolTip = message; Log(message); }
        async void Work(Func<Task> action, bool requireAdb = true, bool reportErrors = true)
        {
            if (busy || closing) return;
            if(requireAdb) automaticDiscovery=false;
            busy = true; UpdateControls();
            try {
                if (requireAdb && !bridge.Available) throw new FileNotFoundException("缺少投屏组件，请保留程序旁的 tools/scrcpy-win64-v5.0.1 目录。详见 README。");
                await action();
            } catch (Exception e) { automaticDiscovery=false; DiagnosticLog.Current.Write("ERROR", e.ToString()); if (!closing) { SetStatus(e.Message); if(reportErrors) MessageBox.Show(window, e.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Information); } }
            finally { busy = false; if (!closing) UpdateControls(); }
        }
        Task Refresh() { return Refresh(true); }
        async Task Refresh(bool report)
        {
            string previous = Selected == null ? null : Selected.Serial;
            List<Device> found = await bridge.Devices();
            if (closing) return;
            var box = Get<ComboBox>("Devices");
            box.ItemsSource = found;
            box.SelectedItem = found.FirstOrDefault(d => d.Serial == previous) ?? found.FirstOrDefault(d => d.Ready) ?? found.FirstOrDefault();
            if (!Running) Text("ConnectionStatus", found.Any(d => d.Ready) ? "设备已就绪 · 尚未投屏" : "等待连接手机");
            if (report)
            {
                if (found.Count == 0) SetStatus("未检测到手机。请确认数据线支持传输、已开启 USB 调试，并在手机上授权。");
                else if (!found.Any(d => d.Ready)) SetStatus("检测到设备，但尚未就绪。请查看设备列表状态，在手机上允许调试后刷新。");
                else SetStatus("已检测到可用设备。请在手机上打开红果，然后点击开始投屏。");
            }
        }
        Device ReadyDevice()
        {
            Device device = Selected;
            if (device == null) throw new InvalidOperationException("请先连接并选择手机，然后刷新设备。");
            if (!device.Ready) throw new InvalidOperationException("设备未就绪：" + device + "。请解锁手机，允许 USB 调试后刷新。");
            return device;
        }
        void Start()
        {
            automaticDiscovery=false;
            var deviceFromApp=Get<ComboBox>("CompanionChoices").SelectedItem as CompanionDevice;
            if(deviceFromApp!=null) { if(!Running && !busy) StartCompanion(deviceFromApp); return; }
            if(Get<ComboBox>("CompanionChoices").Items.Count>0) {SetStatus("请选择要连接的手机。");return;}
            Work(async () => {
                Device device = ReadyDevice();
                Stop(); activeSerial = device.Serial;
                mirror = bridge.StartMirror(activeSerial, Get<ComboBox>("Quality").SelectedIndex == 1,
                    Get<CheckBox>("Audio").IsChecked == true, "红果实时画面", Log);
                startedAt = DateTime.UtcNow; attachTries = 0;
                Text("EmptyTitle", "正在连接手机…");
                Text("EmptyHint", "请保持手机解锁，检查手机上的调试授权提示。");
                SetStatus("正在启动投屏。首次连接请查看手机授权提示。");
                Text("ConnectionStatus", "正在连接 · " + device.Model);
                await Task.FromResult(0);
            });
        }
        void Tick()
        {
            if(automaticDiscovery && !busy && !Running && !closing && DateTime.UtcNow>=nextAutomaticDiscovery)
                Work(()=>FindCompanion(true),false,false);
            if (mirror == null || closing) return;
            if (mirror.HasExited)
            {
                int code = mirror.ExitCode;
                Stop();
                Text("ConnectionStatus", "投屏已断开");
                SetStatus("投屏已退出（" + code + "）。检查手机连接和授权；详情见诊断，可刷新设备后重新开始。");
                return;
            }
            if (video == IntPtr.Zero)
            {
                IntPtr found = Native.FindWindow(mirror.Id);
                if (found != IntPtr.Zero)
                {
                    video = found;
                    Get<Border>("VideoHost").Visibility = Visibility.Visible;
                    window.UpdateLayout();
                    Native.SetLastError(0);
                    Native.SetParent(video, panel.Handle);
                    int error = Marshal.GetLastWin32Error();
                    if (error == 0)
                    {
                        int style = Native.GetWindowLong(video, -16);
                        Native.SetWindowLong(video, -16, (style & ~unchecked((int)0x80C40000)) | 0x40000000);
                        embedded = true; UpdateLayoutMode(); ResizeVideo();
                        Get<StackPanel>("EmptyState").Visibility = Visibility.Collapsed;
                        Native.SetFocus(video);
                        SetStatus("投屏已连接 · 可直接点击、拖动画面。搜索和选集时可关闭观看快捷键。");
                    }
                    else
                    {
                        embedded = false;
                        Get<Border>("VideoHost").Visibility = Visibility.Collapsed;
                        Text("EmptyTitle", "画面已在独立窗口打开");
                        Text("EmptyHint", "当前系统不支持嵌入窗口。请在「红果实时画面」窗口中操作手机。");
                        SetStatus("嵌入失败（Windows " + error + "），已保留独立投屏窗口。");
                    }
                    Text("ConnectionStatus", "正在投屏 · " + activeSerial);
                    UpdateControls();
                }
                else if (++attachTries > 75 || (DateTime.UtcNow - startedAt).TotalSeconds > 35)
                {
                    Stop(); SetStatus("投屏启动超时。请检查手机授权、连接，或关闭音频转发后重试。详情见诊断。");
                }
            }
        }
        void ResizeVideo()
        {
            if (!embedded || video == IntPtr.Zero || panel.ClientSize.Width < 1) return;
            Native.SetWindowPos(video, IntPtr.Zero, 0, 0, panel.ClientSize.Width, panel.ClientSize.Height, 0x34);
        }
        void Stop()
        {
            automaticDiscovery=false;
            if (fullscreen && !closing) ToggleFullscreen();
            contentVisible=false;
            companionControlEnabled=false;
            var previousCompanion=companion; companion=null;
            if(previousCompanion!=null) previousCompanion.Dispose();
            if(h264!=null){h264.Dispose();h264=null;}
            if(timedAudio!=null){timedAudio.Dispose();timedAudio=null;}
            lock(decodedLock){while(decoded.Count>0)decoded.Dequeue().Dispose();}
            if(pendingVideoFrame!=null){pendingVideoFrame.Dispose();pendingVideoFrame=null;}videoBitmap=null;mediaClock=null;
            if(pcm!=null) {pcm.Dispose();pcm=null;}
            System.Threading.Interlocked.Exchange(ref framePending,0);
            video = IntPtr.Zero; embedded = false;
            Process old = mirror; mirror = null; activeSerial = null;
            if (old != null)
            {
                try { if (!old.HasExited) { old.CloseMainWindow(); if (!old.WaitForExit(800)) old.Kill(); } }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception e) { Log("结束投屏失败：" + e.Message); }
                finally { old.Dispose(); }
            }
            if (closing) return;
            Text("VideoStats", "高清 · 最高 120 帧");Text("PlaybackFps", "帧率以实际播放为准");
            crop.Display(null);
            Get<System.Windows.Controls.Image>("CompanionImage").Visibility=Visibility.Collapsed;
            Get<Border>("VideoHost").Visibility = Visibility.Collapsed;
            Get<StackPanel>("EmptyState").Visibility = Visibility.Visible;
            Text("EmptyTitle", "把手机里的故事，搬到大屏");
            Text("EmptyHint", "手机助手开始共享 → 电脑查找并连接\n手机允许本次连接");
            Text("ConnectionStatus", "尚未投屏");
            UpdateControls();
        }
        async void Input(Func<Task> operation)
        {
            if (!Running || inputBusy || !Controllable) return;
            inputBusy = true;
            try { await operation(); }
            catch (Exception e) { if (!closing) SetStatus(e.Message); }
            finally { inputBusy = false; }
        }
        void HandleKey(int key)
        {
            if (closing) return;
            if (key == 122) ToggleFullscreen();
            else if (key == 27) { if (fullscreen) ToggleFullscreen(); }
            else if (key == 38 || key == 40) Input(() => bridge.Swipe(activeSerial, key == 40));
        }
        void UpdateLayoutMode()
        {
            var body=Get<Grid>("Body");
            bool show=connectionPanelOpen&&!fullscreen;
            // On narrow windows the connection panel overlays the player instead of squeezing it.
            bool compact=window.ActualWidth<860;
            Get<Border>("LeftPanel").Visibility=show?Visibility.Visible:Visibility.Collapsed;
            body.ColumnDefinitions[0].Width=new GridLength(show&&!compact?280:0);
            var left=Get<Border>("LeftPanel");
            Grid.SetColumnSpan(left,compact?2:1);Panel.SetZIndex(left,2);
            if(embedded)Get<Border>("VideoHost").Visibility=show&&compact?Visibility.Collapsed:Visibility.Visible;
            left.Width=compact?Math.Min(300,Math.Max(240,window.ActualWidth-40)):Double.NaN;
            left.HorizontalAlignment=HorizontalAlignment.Left;
            if(!compact)left.HorizontalAlignment=HorizontalAlignment.Stretch;
            Get<Border>("Header").Visibility=fullscreen?Visibility.Collapsed:Visibility.Visible;
            Get<Border>("Footer").Visibility=fullscreen?Visibility.Collapsed:Visibility.Visible;
            ((Grid)window.Content).RowDefinitions[0].Height=new GridLength(fullscreen?0:68);
            Get<Grid>("PlayerArea").Margin=new Thickness(fullscreen?0:8);
            Get<Button>("ExitFull").Visibility=fullscreen?Visibility.Visible:Visibility.Collapsed;
            var exitPopup=Get<System.Windows.Controls.Primitives.Popup>("ExitFullPopup");
            exitPopup.PlacementTarget=window;
            exitPopup.HorizontalOffset=Math.Max(0,window.ActualWidth-150);
            exitPopup.IsOpen=fullscreen&&window.IsActive;
        }
        void ToggleFullscreen()
        {
            if(!fullscreen){
                previousState=window.WindowState;
                previousBounds=window.WindowState==WindowState.Normal?new Rect(window.Left,window.Top,window.Width,window.Height):window.RestoreBounds;
                fullscreen=true;
                window.WindowState=WindowState.Normal;
                window.WindowStyle=WindowStyle.None;
                window.ResizeMode=ResizeMode.NoResize;
                window.WindowState=WindowState.Maximized;
            }else{
                fullscreen=false;
                window.WindowState=WindowState.Normal;
                window.WindowStyle=WindowStyle.SingleBorderWindow;
                window.ResizeMode=ResizeMode.CanResize;
                window.Left=previousBounds.Left;window.Top=previousBounds.Top;
                window.Width=previousBounds.Width;window.Height=previousBounds.Height;
                window.WindowState=previousState;
            }
            UpdateLayoutMode();window.UpdateLayout();ResizeVideo();
            if(video!=IntPtr.Zero)Native.SetFocus(video);
            else Get<System.Windows.Controls.Image>("CompanionImage").Focus();
            if(fullscreen && companion!=null && contentVisible && crop.NeedsAutomaticCrop) crop.Edit(true);
        }
        void UpdateControls()
        {
            foreach (string name in new[] { "Refresh", "Pair", "Connect", "EnableTcp", "RestoreUsb", "AutoFind", "AutoPair", "FindAdb" }) Get<Button>(name).IsEnabled = !busy && !Running;
            Get<Button>("Start").IsEnabled = !busy && !Running;
            Get<Button>("Stop").IsEnabled = Running;
            Get<ComboBox>("Devices").IsEnabled = !busy && !Running;
            Get<ComboBox>("Quality").IsEnabled = !Running;
            Get<CheckBox>("Audio").IsEnabled = !Running;
            Get<ComboBox>("CompanionChoices").IsEnabled=!busy && !Running;
            Get<System.Windows.Shapes.Ellipse>("StatusDot").Fill = (Controllable || contentVisible) ? DotLive : (Running ? DotWait : DotIdle);
        }
        static System.Windows.Media.SolidColorBrush Dot(byte r, byte g, byte b) { var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }
        static readonly System.Windows.Media.SolidColorBrush DotLive = Dot(0x3D, 0xD6, 0x8C);
        static readonly System.Windows.Media.SolidColorBrush DotWait = Dot(0xF5, 0xA6, 0x23);
        static readonly System.Windows.Media.SolidColorBrush DotIdle = Dot(0x5B, 0x60, 0x70);
        void ShowLogs()
        {
            var dialog = new Window { Owner = window, Title = "运行日志", Width = 840, Height = 600, MinWidth = 500, MinHeight = 320,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = window.Background, Foreground = window.Foreground };
            var layout = new DockPanel { Margin = new Thickness(16) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(actions, Dock.Top); layout.Children.Add(actions);
            var refresh = new Button { Content = "刷新日志", Margin = new Thickness(0,0,8,8) };
            var export = new Button { Content = "导出日志…", Margin = new Thickness(0,0,8,8) };
            refresh.Style = export.Style = window.FindResource(typeof(Button)) as Style;
            actions.Children.Add(refresh); actions.Children.Add(export);
            var hint = new TextBlock { Text = "复现问题后导出日志，将生成的文本文件发给开发者。日志可能包含设备名称和局域网地址。\n保存目录：" + DiagnosticLog.Current.DirectoryPath,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,10) };
            DockPanel.SetDock(hint, Dock.Top); layout.Children.Add(hint);
            var box = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = window.Background, Foreground = window.Foreground, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Padding = new Thickness(8) };
            Action reload = () => { box.Text = DiagnosticLog.Current.Read(); box.ScrollToEnd(); };
            refresh.Click += (s,e) => reload();
            export.Click += (s,e) => {
                var save = new Microsoft.Win32.SaveFileDialog { Title = "导出运行日志", Filter = "文本日志 (*.txt)|*.txt", DefaultExt = ".txt",
                    FileName = "红果日志-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt" };
                if (save.ShowDialog(dialog) != true) return;
                try { File.WriteAllText(save.FileName, DiagnosticLog.Current.Read(), new UTF8Encoding(true)); MessageBox.Show(dialog, "日志已导出，请将文件发给开发者。", "导出完成"); }
                catch (Exception error) { DiagnosticLog.Current.Write("ERROR", "导出日志失败：" + error); MessageBox.Show(dialog, error.Message, "导出失败"); }
            };
            layout.Children.Add(box); dialog.Content = layout; reload(); dialog.ShowDialog();
        }
        void Help()
        {
            var help = new Window { Owner = window, Title = "使用说明与诊断", Width = 760, Height = 620, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x15, 0x1C)) };
            var box = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16), FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x15, 0x1C)),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE4, 0xE5, 0xED)),
                BorderThickness = new Thickness(0), Padding = new Thickness(4) };
            box.Text = "首次使用\r\n1. 手机上开启 USB 调试，用数据线连接，在手机弹窗中允许调试。\r\n2. 点击刷新设备，选择已就绪的手机，打开手机上的红果，再开始投屏。\r\n3. 鼠标点击和拖动对应手机触摸。观看快捷键开启时，画面内滚轮/↑↓发送滑动；搜索选集时关闭观看快捷键。\r\n4. 中文可在电脑复制，在手机输入框聚焦后 Ctrl+V 粘贴；不自动同步手机剪贴板。\r\n\r\n红米 / 小米\r\n若能显示不能点击，请检查开发者选项中的「USB 调试（安全设置）」，按系统要求授权。\r\n没有 USB 提示时，先换支持数据传输的线和电脑 USB 口。\r\n\r\n无线 / 热点\r\n电脑与手机在同一网络。Android 11+ 的无线调试中，先用配对码弹窗的地址配对，再用无线调试主页面的地址连接；两个端口通常不同。热点是否支持无线调试由手机系统决定。\r\n不支持配对时，可先接 USB，点 USB 转无线，再输入手机 IP:5555 连接。无线连接成功后才拔线。结束后可点恢复 USB 模式关闭 5555；无线调试开关需在手机设置中关闭。\r\n\r\n注意\r\n播放操作请点击手机画面内的按钮。快捷滑动不等于保证切换下一集。账号、选集、收藏和付费仍由手机红果处理。音频与受保护画面以手机和应用实际支持为准。\r\n本工具是本地遥控工具，非红果官方客户端。\r\n\r\n诊断（仅本次会话，不含配对码）\r\n" + logs;
            box.Text = "热点 / Wi-Fi 快捷连接（推荐）\r\n电脑连接手机热点后，手机开启无线调试 → 使用配对码配对设备。电脑点「自动查找手机」，找到后只输入 6 位码，配对后自动连接。地址和端口自动发现，不需要手填。\r\n若手机提示必须连接 Wi-Fi，当前系统不支持仅用热点开启无线调试。自动发现依赖手机广播服务；被网络隔离时请查看高级连接。\r\n\r\n" + box.Text;
            box.Text="手机助手模式（无需调试）\r\n手机安装 HongguoCompanion.apk，电脑连手机热点。手机点开始共享，电脑启动后自动查找并请求连接，手机允许本次连接；也可手动点查找重试。仅观看可跳过无障碍控制并记住选择；需要遥控时，在手机点开启电脑控制进入系统设置，返回后继续共享。中文搜索请在手机输入。声音转发需手机音频授权，且红果允许捕获。\r\n\r\n"+box.Text;
            help.Content = box; help.ShowDialog();
        }
    }
}
