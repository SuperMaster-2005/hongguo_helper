package local.hongguo.companion;

import android.app.*;
import android.content.*;
import android.content.pm.ServiceInfo;
import android.content.res.Configuration;
import android.hardware.display.*;
import android.media.*;
import android.media.projection.*;
import android.net.wifi.WifiManager;
import android.os.*;
import android.util.*;
import android.view.*;
import java.io.*;
import java.net.*;
import java.nio.ByteBuffer;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;
import javax.net.ssl.*;

public class CaptureService extends Service {
    public static volatile boolean running;
    static volatile int contentWidth,contentHeight;
    private static volatile boolean projectionVisible;
    static volatile boolean captureRegionKnown;
    static boolean canShare(){return Build.VERSION.SDK_INT>=34 && captureRegionKnown && projectionVisible && (RemoteAccessibility.instance==null || RemoteAccessibility.isTargetActive());}
    public static volatile String status = "尚未共享。电脑连接手机热点后，点击开始共享。";
    public static volatile Session pending;
    private static volatile Session connected;
    private final Handler main = new Handler(Looper.getMainLooper());
    private HandlerThread captureThread;
    private Handler captureHandler;
    private MediaProjection projection;
    private VirtualDisplay display;
    private VideoEncoder encoder;
    private SSLServerSocket server;
    private DatagramSocket discovery;
    private WifiManager.MulticastLock multicast;
    private final Object networkLock = new Object();
    private volatile int frameWidth, frameHeight;
    private int recoveryLongEdge=3200;
    private int recoveryMaxFps=60;
    private String captureFailure;
    private boolean wantsAudio;
    private volatile boolean alive;
    private final AtomicBoolean sessionSlot = new AtomicBoolean();
    private volatile Session slotSession;
    private NotificationManager notifications;

    public static boolean hasApprovedConnection() {
        Session s = connected; return running && s != null && s.approved && !s.closed;
    }
    @Override public IBinder onBind(Intent intent) { return null; }
    @Override public int onStartCommand(Intent intent,int flags,int id) {
        if (intent != null && "STOP".equals(intent.getAction())) { stopSelf(); return START_NOT_STICKY; }
        if (running) return START_NOT_STICKY;
        if(Build.VERSION.SDK_INT<34){stopSelf();return START_NOT_STICKY;}
        if (intent == null || (!intent.hasExtra("capture") || !intent.getBooleanExtra("redFruitSelected",false))) { stopSelf(); return START_NOT_STICKY; }
        try {
            notifications = (NotificationManager)getSystemService(NOTIFICATION_SERVICE);
            notifications.createNotificationChannel(new NotificationChannel("share","屏幕共享",NotificationManager.IMPORTANCE_DEFAULT));
            startForeground(1,notification("正在准备共享"),ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);
            running = alive = true; projectionVisible=false; captureRegionKnown=false;contentWidth=contentHeight=0; status = "正在准备安全连接…";
            wantsAudio = intent.getBooleanExtra("audio",false);
            captureThread = new HandlerThread("screen-capture"); captureThread.start(); captureHandler = new Handler(captureThread.getLooper());
            projection = ((MediaProjectionManager)getSystemService(MEDIA_PROJECTION_SERVICE)).getMediaProjection(Activity.RESULT_OK,intent.getParcelableExtra("capture"));
            projection.registerCallback(new MediaProjection.Callback() {
                @Override public void onStop() { if(alive)fail("安卓已结束屏幕共享，请重新开始共享。"); }
                @Override public void onCapturedContentVisibilityChanged(boolean visible) { projectionVisible=visible; }
                @Override public void onCapturedContentResize(int width,int height) { if (alive && width>0 && height>0) {captureRegionKnown=false;captureRegionKnown=resize(width,height);} }
            },captureHandler);
            captureHandler.post(() -> {
                try {
                    if (!alive) return;
                    if(contentWidth>0 && contentHeight>0)resize(contentWidth,contentHeight);
                    else {DisplayMetrics metrics = metrics(); resize(metrics.widthPixels,metrics.heightPixels);}
                    new Thread(this::serve,"companion-server").start();
                } catch (Exception e) { fail("共享启动失败：" + e.getMessage()); }
            });
        } catch (Exception e) {
            Log.w("HongguoCapture","系统未允许启动共享服务",e);
            fail("系统未允许启动共享。请回到助手重新授权，并检查系统是否限制了本应用运行。");
        }
        return START_NOT_STICKY;
    }
    private DisplayMetrics metrics() {
        DisplayMetrics m = new DisplayMetrics();
        m.setTo(getResources().getDisplayMetrics());
        android.graphics.Rect bounds=((WindowManager)getSystemService(WINDOW_SERVICE)).getMaximumWindowMetrics().getBounds();
        m.widthPixels=bounds.width();m.heightPixels=bounds.height();
        return m;
    }
    private boolean resize(int realWidth,int realHeight) {
        if(realWidth<1||realHeight<1||!alive)return false;
        contentWidth=realWidth;contentHeight=realHeight;
        float factor=Math.min(1f,Math.min((float)recoveryLongEdge/Math.max(realWidth,realHeight),1440f/Math.min(realWidth,realHeight)));
        int width=Math.max(2,Math.round(realWidth*factor)/2*2),height=Math.max(2,Math.round(realHeight*factor)/2*2);
        if(encoder!=null&&frameWidth==width&&frameHeight==height)return true;
        try {
            if(display!=null)display.setSurface(null);
            if(encoder!=null){encoder.close();encoder=null;}
            Session session=connected;if(session!=null)session.videoQueue.reset();
            encoder=VideoEncoder.create(width,height,recoveryMaxFps,captureHandler,new VideoEncoder.Sink(){
                public void frame(VideoFrame frame){Session current=connected;if(current!=null&&current.approved&&!current.closed&&current.sharing&&canShare()&&current.videoQueue.offer(frame))requestKeyframe();}
                public void error(String message){captureHandler.post(()->recoverEncoder(message));}
            });
            if(display==null)display=projection.createVirtualDisplay("HongguoShare",encoder.width,encoder.height,metrics().densityDpi,DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,encoder.surface,null,captureHandler);
            else{display.resize(encoder.width,encoder.height,metrics().densityDpi);display.setSurface(encoder.surface);}
            frameWidth=width;frameHeight=height;
            status="共享已开启：H.264 硬件编码，目标 "+encoder.fps+" 帧。等待电脑连接。";
            return true;
        }catch(Exception e){fail("无法开启 H.264 共享："+e.getMessage());return false;}
    }
    private void recoverEncoder(String message) {
        if(!alive || encoder==null)return;
        int edge=Math.max(encoder.width,encoder.height);
        if(edge<=960){
            if(encoder.fps<=30){fail("硬件编码异常，请重新共享："+message);return;}
            recoveryLongEdge=edge;
            recoveryMaxFps=30;
        }else{
            recoveryLongEdge=edge>2560?2560:edge>1920?1920:edge>1280?1280:960;
            recoveryMaxFps=encoder.fps;
        }
        frameWidth=frameHeight=0;
        Log.w("HongguoCapture","编码运行异常，降档至长边 "+recoveryLongEdge+": "+message);
        resize(contentWidth,contentHeight);
        if(encoder!=null){
            Session session=connected;
            if(session!=null)session.controlResult="编码出现异常，已降至 "+encoder.width+"×"+encoder.height+" / "+encoder.fps+" 帧，继续投屏。";
            requestKeyframe();
        }
    }
    private void requestKeyframe(){if(captureHandler!=null)captureHandler.post(()->{if(alive&&encoder!=null)encoder.requestKeyframe();});}
    @Override public void onConfigurationChanged(Configuration c) {
        super.onConfigurationChanged(c);
        if (Build.VERSION.SDK_INT >= 34 && !captureRegionKnown && captureHandler != null) captureHandler.post(() -> { if (alive && !captureRegionKnown) { DisplayMetrics m=metrics(); resize(m.widthPixels,m.heightPixels); } });
    }
    private void serve() {
        try {
            Wire.Identity identity = Wire.identity();
            synchronized(networkLock) {
                if (!alive) return;
                server = (SSLServerSocket)identity.context.getServerSocketFactory().createServerSocket();
                server.setReuseAddress(true); server.bind(new InetSocketAddress(Wire.PORT)); server.setEnabledProtocols(new String[]{"TLSv1.2"});
                server.setEnabledCipherSuites(new String[]{"TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256"});
                WifiManager wifi = (WifiManager)getApplicationContext().getSystemService(WIFI_SERVICE);
                if (wifi != null) { multicast = wifi.createMulticastLock("hongguo-discovery"); multicast.setReferenceCounted(false); multicast.acquire(); }
                discovery = new DatagramSocket(null); discovery.setReuseAddress(true); discovery.bind(new InetSocketAddress(Wire.DISCOVERY));
                new Thread(() -> discover(identity.fingerprint),"companion-discovery").start();
                status = "共享已开启，等待电脑。请在电脑点「自动查找手机」，然后在手机允许连接。"; notifyStatus(status);
            }
            while (alive) {
                SSLSocket socket = (SSLSocket)server.accept();
                synchronized(networkLock) {
                    if (!alive) { socket.close(); break; }
                    if (!socket.getInetAddress().isSiteLocalAddress() && !socket.getInetAddress().isLoopbackAddress()) {socket.close();continue;}
                    if (!sessionSlot.compareAndSet(false,true)) { socket.close(); continue; }
                    Session session = new Session(socket); slotSession = session;
                    new Thread(() -> session.run(),"companion-session").start();
                }
            }
        } catch (Exception e) { if (alive) fail("连接服务启动失败：" + e.getMessage()); }
        finally { closeNetwork(); }
    }
    private static void cleanup(String resource,Runnable action) {
        try { action.run(); } catch(RuntimeException e) { Log.w("HongguoCapture","释放"+resource+"失败",e); }
    }
    private void closeNetwork() {
        synchronized(networkLock) {
            try {if(server!=null)server.close();}catch(IOException e){Log.w("HongguoCapture","关闭连接监听失败",e);}
            if(discovery!=null)cleanup("发现端口",()->discovery.close());
            if(multicast!=null)cleanup("多播锁",()->{if(multicast.isHeld())multicast.release();});
        }
    }
    private void discover(String fingerprint) {
        byte[] buffer = new byte[512];
        while (alive) {
            try {
                DatagramPacket request = new DatagramPacket(buffer,buffer.length); discovery.receive(request);
                if (!request.getAddress().isSiteLocalAddress()) continue;
                String value = new String(request.getData(),0,request.getLength(),java.nio.charset.StandardCharsets.UTF_8);
                if (!value.matches("HGR_DISCOVER_V1\\|[a-f0-9]{32}")) continue;
                String model = Base64.encodeToString(Wire.utf8(Build.MODEL),Base64.NO_WRAP);
                byte[] response = Wire.utf8("HGR1|" + value.substring(16) + "|" + Wire.PORT + "|" + model + "|" + fingerprint);
                discovery.send(new DatagramPacket(response,response.length,request.getAddress(),request.getPort()));
            } catch (IOException e) { if (running) status = "自动发现暂不可用，请重新开始共享。"; break; }
        }
    }
    private Notification notification(String text) {
        PendingIntent open = PendingIntent.getActivity(this,0,new Intent(this,MainActivity.class),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
        PendingIntent stop = PendingIntent.getService(this,1,new Intent(this,CaptureService.class).setAction("STOP"),PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this,"share").setSmallIcon(android.R.drawable.ic_menu_view).setContentTitle("红果手机助手 · 共享中")
            .setContentText(text).setContentIntent(open).setOngoing(true).addAction(new Notification.Action.Builder(null,"停止共享",stop).build()).build();
    }
    private void notifyStatus(String text) { if (running && notifications != null) notifications.notify(1,notification(text)); }
    private void fail(String message) { captureFailure=message; status=message; main.post(this::stopSelf); }

    public final class Session {
        final SSLSocket socket;
        public String name = "电脑", address;
        public volatile boolean approved, closed;
        private final CountDownLatch consent = new CountDownLatch(1);
        private final ArrayBlockingQueue<byte[]> audioQueue = new ArrayBlockingQueue<>(8);
        private final VideoQueue videoQueue=new VideoQueue();
        private volatile AudioRecord audioRecord;
        private volatile boolean sharing;
        private Wire.WriteWatchdog writeWatchdog;
        private volatile int savedVolume = -1;
        private synchronized void silencePhone() {
            if (closed || savedVolume >= 0) return;
            AudioManager audio=(AudioManager)getSystemService(AUDIO_SERVICE);
            int volume=audio.getStreamVolume(AudioManager.STREAM_MUSIC);
            try {
                audio.setStreamVolume(AudioManager.STREAM_MUSIC,0,0);
                savedVolume=volume;
            }catch(RuntimeException e){
                controlResult="系统未允许静音，投屏将继续。";
                Log.w("HongguoCapture","无法设置手机媒体音量",e);
            }
        }
        private synchronized void restoreVolume() {
            if (savedVolume < 0) return;
            AudioManager audio=(AudioManager)getSystemService(AUDIO_SERVICE);
            try {
                // Preserve a manual volume change made by the user during sharing.
                if(audio.getStreamVolume(AudioManager.STREAM_MUSIC)==0)
                    audio.setStreamVolume(AudioManager.STREAM_MUSIC,savedVolume,0);
            } catch(RuntimeException e) { android.util.Log.w("HongguoSession","恢复媒体音量失败",e); }
            savedVolume=-1;
        }
        private volatile String controlResult;
        private final java.util.concurrent.atomic.AtomicReference<String> audioStatus =
            new java.util.concurrent.atomic.AtomicReference<>();
        private void reportAudio(String message) {
            audioStatus.set(message);
            status=message;
        }
        Session(SSLSocket socket) { this.socket=socket; address=socket.getInetAddress().getHostAddress(); }
        private void send(DataOutputStream out,int type,byte[] payload) throws IOException {
            writeWatchdog.begin();
            try { Wire.send(out,type,payload); } finally { writeWatchdog.end(); }
        }
        public void decide(boolean allowed) { approved = allowed && alive && !closed; consent.countDown(); }
        void run() {
            String failure = null;
            String stage = "TLS 握手";
            try {
                writeWatchdog=new Wire.WriteWatchdog(socket,15000);
                socket.setTcpNoDelay(true); socket.setSoTimeout(12000); socket.startHandshake();
                DataInputStream in = new DataInputStream(socket.getInputStream());
                DataOutputStream out = new DataOutputStream(socket.getOutputStream());
                stage = "读取电脑请求";
                String hello = Wire.readCommand(in);
                if (!hello.startsWith("hello|") || hello.length() > 100) throw new IOException("Invalid hello");
                if(!hello.endsWith("|h264-v5")){send(out,5,Wire.utf8("请更新电脑端后再连接仅红果共享版手机助手。"));return;}
                name = hello.substring(6,hello.length()-8).replaceAll("[\\p{Cntrl}]","");
                stage = "等待手机确认";
                pending = this; status = "电脑「" + name + "」请求连接，请回到手机助手允许。"; notifyStatus(status);
                send(out,4,Wire.utf8("请在手机助手中允许这台电脑连接。"));
                if (!consent.await(60,TimeUnit.SECONDS) || !approved || !alive || closed) { send(out,5,Wire.utf8("手机未允许连接。请重试并在手机上确认。")); return; }
                stage = "传输画面";
                pending = null; connected = this;
                status = "已连接电脑：" + name + "。可打开红果；随时回到这里停止共享。"; notifyStatus(status);
                send(out,1,Wire.utf8(Build.MODEL));
                send(out,4,Wire.utf8(RemoteAccessibility.instance == null ? "仅观看，电脑控制未开启。" : "等待手机端打开红果短剧"));
                socket.setSoTimeout(20000);
                new Thread(() -> receive(in),"companion-controls").start();
                if (wantsAudio) startAudio();
                else reportAudio("手机端未开启声音转发。可停止共享，勾选声音转发后重新连接。");
                send(out,9,new byte[]{0});
                requestKeyframe();int sentWidth=0,sentHeight=0,sentFps=0;
                long activeSince=0,lastHeartbeat=0;String lastWaitingReason=null;
                Boolean lastControlEnabled=null;
                while(alive&&!closed&&approved){
                    boolean controlEnabled=RemoteAccessibility.instance!=null;
                    if(lastControlEnabled==null || lastControlEnabled!=controlEnabled){
                        send(out,11,new byte[]{(byte)(controlEnabled?1:0)});
                        lastControlEnabled=controlEnabled;
                    }
                    boolean target=canShare();
                    long now=SystemClock.elapsedRealtime();
                    // Keep the receive watchdog alive even while no app content is being sent.
                    if(now-lastHeartbeat>=5000){send(out,10,new byte[0]);lastHeartbeat=now;}
                    if(!target)activeSince=0;else if(activeSince==0)activeSince=now;
                    // Resume only after the foreground has settled, discarding transition frames.
                    boolean allowed=target && now-activeSince>=350;
                    if(sharing!=allowed){
                        sharing=allowed;videoQueue.reset();audioQueue.clear();sentWidth=sentHeight=sentFps=0;
                        if(sharing)requestKeyframe();else restoreVolume();
                        send(out,9,new byte[]{(byte)(sharing?1:0)});
                    }
                    String soundStatus=audioStatus.getAndSet(null);
                    if(soundStatus!=null)send(out,4,Wire.utf8(soundStatus));
                    if(!sharing){
                        String reason=controlEnabled?RemoteAccessibility.waitingReason:"等待手机端打开红果短剧";
                        if(!reason.equals(lastWaitingReason)){send(out,4,Wire.utf8(reason));status=reason;notifyStatus(reason);lastWaitingReason=reason;}
                        videoQueue.reset();audioQueue.clear();Thread.sleep(20);continue;
                    }
                    lastWaitingReason=null;
                    byte[] sound;
                    for(int i=0;i<8 && canShare() && (sound=audioQueue.poll())!=null;i++)send(out,8,sound);
                    VideoFrame frame=videoQueue.poll();
                    if(frame!=null && canShare()){
                        if(frame.width!=sentWidth||frame.height!=sentHeight||frame.fps!=sentFps){
                            send(out,6,Wire.utf8("H264|"+frame.width+"|"+frame.height+"|"+frame.fps));sentWidth=frame.width;sentHeight=frame.height;sentFps=frame.fps;
                        }
                        ByteBuffer packet=ByteBuffer.allocate(12+frame.data.length);packet.putLong(frame.pts).putInt(frame.key?1:0).put(frame.data);
                        send(out,7,packet.array());
                    }
                    String message = controlResult;
                    if (message != null) { controlResult=null; send(out,4,Wire.utf8(message)); }
                    if(frame==null)Thread.sleep(2);
                }
            } catch (Exception e) { if (alive && !closed) { failure = stage + "失败：" + e; android.util.Log.w("HongguoSession",stage,e); } }
            finally {
                if(writeWatchdog!=null)writeWatchdog.close();
                close();
                if (connected == this) connected=null;
                if (pending == this) pending=null;
                if (slotSession == this) slotSession=null;
                videoQueue.reset(); sessionSlot.set(false);
                if (alive) { status=failure == null ? "电脑已断开，可重新查找连接，或停止共享。" : failure; notifyStatus(status); }
            }
        }
        private void receive(DataInputStream in) {
            try {
                while (alive && !closed) {
                    String command=Wire.readCommand(in);
                    if (command.equals("ping")) continue;
                    if (command.equals("stop")) break;
                    if(command.equals("keyframe")){videoQueue.reset();requestKeyframe();continue;}
                    main.post(() -> {
                        if (closed || connected != this || !approved || !sharing || !canShare() || !RemoteAccessibility.isTargetActive()) return;
                        AudioManager audio=(AudioManager)getSystemService(AUDIO_SERVICE);
                        if (command.equals("volumeup") || command.equals("volumedown")) { if(savedVolume>=0){controlResult="手机已静音，请使用电脑系统音量调节声音。";return;} audio.adjustStreamVolume(AudioManager.STREAM_MUSIC,command.equals("volumeup")?1:-1,0); return; }
                        if (command.equals("pause")) { audio.dispatchMediaKeyEvent(new KeyEvent(KeyEvent.ACTION_DOWN,KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE)); audio.dispatchMediaKeyEvent(new KeyEvent(KeyEvent.ACTION_UP,KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE)); return; }
                        RemoteAccessibility access=RemoteAccessibility.instance;
                        if (access == null || !access.command(command)) controlResult="手机未执行操作，请检查电脑控制权限、输入框或当前页面。";
                    });
                }
            } catch (IOException e) { }
            finally { close(); }
        }
        private void startAudio() {
            try {
                AudioFormat format=new AudioFormat.Builder().setSampleRate(48000).setChannelMask(AudioFormat.CHANNEL_IN_MONO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build();
                AudioPlaybackCaptureConfiguration config=new AudioPlaybackCaptureConfiguration.Builder(projection)
                    .addMatchingUid(getPackageManager().getApplicationInfo(RemoteAccessibility.TARGET_PACKAGE,0).uid)
                    .addMatchingUsage(AudioAttributes.USAGE_MEDIA).addMatchingUsage(AudioAttributes.USAGE_GAME).addMatchingUsage(AudioAttributes.USAGE_UNKNOWN).build();
                int minimum=AudioRecord.getMinBufferSize(48000,AudioFormat.CHANNEL_IN_MONO,AudioFormat.ENCODING_PCM_16BIT);
                final AudioRecord record=new AudioRecord.Builder().setAudioFormat(format).setBufferSizeInBytes(Math.max(19200,minimum*2)).setAudioPlaybackCaptureConfig(config).build();
                audioRecord=record;
                record.startRecording();
                if(record.getRecordingState()!=AudioRecord.RECORDSTATE_RECORDING)
                    throw new IllegalStateException("系统未启动播放音频捕获");
                reportAudio("音频捕获已启动，等待红果播放声音。");
                new Thread(() -> {
                    byte[] buffer=new byte[1920];long totalFrames=0;AudioTimestamp stamp=new AudioTimestamp();
                    long lastSignal=0, audibleSince=0, waitingSince=0;
                    boolean warned=false, keepPhoneAudio=false;
                    try {while(alive&&!closed){
                        int read=record.read(buffer,0,buffer.length);
                        if(read<0)throw new IOException("音频读取错误码 "+read);
                        if(read==0)continue;
                        long pts=System.nanoTime()/1000-read/2*1_000_000L/48000;
                        if(record.getTimestamp(stamp,AudioTimestamp.TIMEBASE_MONOTONIC)==AudioRecord.SUCCESS)pts=stamp.nanoTime/1000+(totalFrames-stamp.framePosition)*1_000_000L/48000;
                        totalFrames+=read/2;
                        if(!sharing || !canShare()){
                            audioQueue.clear();
                            waitingSince=audibleSince=lastSignal=0;
                            warned=false;
                            continue;
                        }
                        long now=SystemClock.elapsedRealtime();
                        if(waitingSince==0)waitingSince=now;
                        boolean audible=false;
                        for(int i=0;i+1<read;i+=2){
                            int sample=(short)((buffer[i]&255)|(buffer[i+1]<<8));
                            if(Math.abs(sample)>=32){audible=true;break;}
                        }
                        if(audible){
                            lastSignal=now;
                            if(audibleSince==0)audibleSince=now;
                            if(warned){reportAudio("已收到红果声音，正在转发到电脑。");warned=false;}
                            if(!keepPhoneAudio && savedVolume<0 && now-audibleSince>=250) {
                                silencePhone();
                                if(savedVolume<0)keepPhoneAudio=true;
                            }
                        }else{
                            audibleSince=0;
                            if(savedVolume>0 && now-lastSignal>=2500){
                                restoreVolume();
                                keepPhoneAudio=true;
                                reportAudio("静音后暂未捕获到红果声音，已恢复手机音量，本次共享保留手机声音。");
                            }else if(lastSignal==0 && !warned && now-waitingSince>=4000){
                                warned=true;
                                reportAudio("没有捕获到红果声音。请确认短剧正在有声播放，应用或系统也可能限制声音捕获。");
                            }
                        }
                        ByteBuffer packet=ByteBuffer.allocate(8+read);packet.putLong(pts).put(buffer,0,read);byte[] bytes=packet.array();
                        if(!audioQueue.offer(bytes)){audioQueue.poll();audioQueue.offer(bytes);}
                    }}
                    catch (Exception e) { if(!closed && alive)reportAudio("红果声音捕获中断："+e.getMessage()); }
                    finally { audioRecord=null; restoreVolume(); synchronized(record) { try {record.stop();}catch(RuntimeException e){} record.release(); } }
                },"companion-audio").start();
            } catch (Exception e) { restoreVolume(); AudioRecord failed=audioRecord; audioRecord=null; if(failed!=null){try{failed.stop();}catch(RuntimeException ignored){} failed.release();} reportAudio("无法捕获红果声音："+e.getClass().getSimpleName()+" · "+e.getMessage()); }
        }
        void close() {
            if (closed) return;
            closed=true; approved=false; consent.countDown();
            try {socket.close();}catch(IOException e){}
            cleanup("手机音量",this::restoreVolume);
            AudioRecord record=audioRecord;
            if (record != null) synchronized(record) { try { record.stop(); } catch(RuntimeException e){} }
        }
    }
    @Override public void onDestroy() {
        running=alive=false;
        projectionVisible=captureRegionKnown=false;
        // End the system projection even if later connection/display/codec cleanup fails.
        if (projection!=null) cleanup("屏幕共享",()->projection.stop());
        Session current;
        synchronized(networkLock) { current=slotSession; }
        if (current!=null) cleanup("电脑连接",current::close);
        pending=null; connected=null;
        closeNetwork();
        Runnable releaseCapture=()->{
            if (display!=null) cleanup("虚拟显示",()->display.release());
            if (encoder!=null) cleanup("编码器",()->encoder.close());
            if (captureThread!=null) captureThread.quitSafely();
        };
        if (captureHandler==null || !captureHandler.post(releaseCapture)) releaseCapture.run();
        status=captureFailure==null?"共享已停止。需要时可再次开始共享。":captureFailure;
        stopForeground(STOP_FOREGROUND_REMOVE); super.onDestroy();
    }
}
