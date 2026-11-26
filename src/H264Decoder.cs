using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Threading;
using System.Threading.Tasks;

namespace HongguoRemote
{
    public sealed class VideoFormat
    {
        public int Width, Height, Fps;
        public static VideoFormat Parse(string text)
        {
            string[] parts=text.Split('|');int w,h,f;
            if(parts.Length!=4||parts[0]!="H264"||!Int32.TryParse(parts[1],out w)||!Int32.TryParse(parts[2],out h)||!Int32.TryParse(parts[3],out f)||w<64||h<64||w>3200||h>3200||Math.Min(w,h)>1440||(long)w*h>3200*1440||w%2!=0||h%2!=0||(f!=30&&f!=60&&f!=90&&f!=120))throw new IOException("手机视频格式不受支持。");
            return new VideoFormat {Width=w,Height=h,Fps=f};
        }
    }
    public sealed class EncodedFrame { public long Timestamp; public bool Key; public byte[] Data; }
    public sealed class DecodedFrame : IDisposable { public long Timestamp; public int Width,Height; public byte[] Pixels; internal Action<byte[]> ReturnBuffer;int returned; public void Dispose(){if(Interlocked.Exchange(ref returned,1)==0&&ReturnBuffer!=null)ReturnBuffer(Pixels);} }

    // FFmpeg is an isolated decoder process; the UI only keeps the newest decoded frame.
    public sealed class H264Decoder : IDisposable
    {
        readonly string executable;
        readonly object gate=new object();
        readonly Queue<EncodedFrame> queue=new Queue<EncodedFrame>();
        readonly AutoResetEvent changed=new AutoResetEvent(false);
        readonly Thread worker;
        volatile bool stopped;
        VideoFormat format;
        volatile Pipeline pipeline;
        bool waitingKey=true,restart=true;
        int generation;
        public Action<DecodedFrame> Frame;
        public Action<string> Error, Warning;
        public Action RequestKeyframe;

        public H264Decoder(string executable)
        {
            if(!File.Exists(executable))throw new FileNotFoundException("缺少视频解码器，请保留 tools/ffmpeg/ffmpeg.exe。",executable);
            this.executable=executable;worker=new Thread(Run){IsBackground=true,Name="H264 decode input"};worker.Start();
        }
        public void Reset()
        {
            lock(gate){queue.Clear();waitingKey=true;restart=true;generation++;}
        }
        public void Configure(VideoFormat value)
        {
            lock(gate){format=value;queue.Clear();waitingKey=true;restart=true;generation++;}
        }
        public void Enqueue(EncodedFrame frame)
        {
            bool request=false;
            lock(gate){
                if(stopped)return;
                if(format==null)throw new IOException("先收到视频帧，缺少视频格式。");
                if(queue.Count>=18||(queue.Count>0&&frame.Timestamp-queue.Peek().Timestamp>180000)){queue.Clear();waitingKey=true;restart=true;generation++;request=true;}
                if(waitingKey&&!frame.Key){request=true;}
                else{waitingKey=false;queue.Enqueue(frame);changed.Set();}
            }
            if(request&&RequestKeyframe!=null)RequestKeyframe();
        }
        void Recover(int serial,string message)
        {
            lock(gate){
                if(stopped || serial!=generation)return;
                queue.Clear();waitingKey=true;restart=true;generation++;
            }
            if(Error!=null)Error("视频解码异常，等待画面恢复："+message);
            if(RequestKeyframe!=null)RequestKeyframe();
        }
        void Run()
        {
            try{
                while(!stopped){
                    EncodedFrame frame=null;VideoFormat current=null;bool reset=false;int serial=0;
                    lock(gate){if(queue.Count>0){frame=queue.Dequeue();current=format;reset=restart;restart=false;serial=generation;}}
                    if(frame==null){changed.WaitOne(50);continue;}
                    try {
                        if(reset||pipeline==null){
                            if(pipeline!=null)pipeline.Dispose();
                            int version=serial;
                            pipeline=new Pipeline(executable,current,decoded=>{if(!stopped&&version==generation&&Frame!=null)Frame(decoded);else decoded.Dispose();},message=>Recover(version,message),message=>{if(!stopped&&version==generation&&Warning!=null)Warning(message);});
                        }
                        if(stopped){pipeline.Dispose();break;}
                        pipeline.Push(frame);
                    }catch(Exception e){Recover(serial,e.Message);}
                }
            }catch(Exception e){if(!stopped&&Error!=null)Error("视频解码失败："+e.Message);}
            finally{if(pipeline!=null)pipeline.Dispose();}
        }
        public void Dispose(){stopped=true;changed.Set();if(pipeline!=null)pipeline.Dispose();if(Thread.CurrentThread!=worker)worker.Join(1500);}

        sealed class Pipeline : IDisposable
        {
            readonly BufferedDecoderProcess process;
            readonly VideoFormat format;
            readonly Queue<long> timestamps=new Queue<long>();
            readonly object sync=new object();
            readonly ConcurrentBag<byte[]> recycled=new ConcurrentBag<byte[]>();
            readonly Action<DecodedFrame> onFrame;
            readonly Action<string> error;
            readonly Timer writeWatchdog;
            const int WriteTimeoutMilliseconds=5000;
            long writeStarted,backlogStarted;
            bool writing;
            volatile bool writeTimedOut;
            volatile bool closed;
            public Pipeline(string exe,VideoFormat value,Action<DecodedFrame> frame,Action<string> error,Action<string> warning)
            {
                format=value;onFrame=frame;this.error=error;
                process=new BufferedDecoderProcess(exe,"-hide_banner -loglevel error -probesize 32 -analyzeduration 0 -flags low_delay -threads 2 -thread_type slice -f h264 -i pipe:0 -an -sn -filter_threads 2 -threads 1 -flush_packets 1 -fps_mode passthrough -pix_fmt bgr0 -f rawvideo pipe:1");
                Task.Run(()=>{try{string line;while((line=process.Error.ReadLine())!=null)if(!closed&&!String.IsNullOrWhiteSpace(line))warning("解码器："+line);}catch(IOException){}catch(ObjectDisposedException){}});
                writeWatchdog=new Timer(CheckWrite,null,250,250);
                Task.Run((Action)ReadFrames);
            }
            public void Push(EncodedFrame frame)
            {
                lock(sync){
                    if(closed)return;
                    // The pipe can accept frames faster than FFmpeg outputs them; bound that hidden backlog too.
                    if(timestamps.Count>=18 || (timestamps.Count>0 && frame.Timestamp-timestamps.Peek()>180000)){
                        long now=Stopwatch.GetTimestamp();
                        if(backlogStarted==0)backlogStarted=now;
                        else if((now-backlogStarted)*1000.0/Stopwatch.Frequency>=1000)
                            throw new IOException("视频解码持续积压，重新等待关键帧。");
                    }else backlogStarted=0;
                    timestamps.Enqueue(frame.Timestamp);writeStarted=Stopwatch.GetTimestamp();writing=true;
                }
                try{process.Input.Write(frame.Data,0,frame.Data.Length);process.Input.Flush();}
                catch(Exception){if(writeTimedOut)throw new TimeoutException("视频解码器写入超时。");throw;}
                finally{lock(sync)writing=false;}
            }
            void CheckWrite(object state)
            {
                lock(sync){
                    if(closed || !writing || (Stopwatch.GetTimestamp()-writeStarted)*1000.0/Stopwatch.Frequency<WriteTimeoutMilliseconds)return;
                    writeTimedOut=true;
                }
                // The input worker may be blocked in Write; only another thread can release it.
                Dispose();
                error("视频解码器写入超时。");
            }
            void ReadFrames()
            {
                try{
                    Stream output=process.Output;
                    while(!closed){
                        byte[] pixels;if(!recycled.TryTake(out pixels))pixels=new byte[checked(format.Width*format.Height*4)];int offset=0;
                        while(offset<pixels.Length){int n=output.Read(pixels,offset,pixels.Length-offset);if(n<=0)throw new EndOfStreamException("视频解码器已退出。");offset+=n;}
                        long timestamp;lock(sync){if(timestamps.Count==0)throw new IOException("视频帧时间戳缺失。");timestamp=timestamps.Dequeue();}
                        onFrame(new DecodedFrame{Width=format.Width,Height=format.Height,Pixels=pixels,Timestamp=timestamp,ReturnBuffer=buffer=>{if(!closed&&recycled.Count<20)recycled.Add(buffer);}});
                    }
                }catch(Exception e){if(!closed)error(e.Message);}
            }
            public void Dispose(){
                lock(sync){if(closed)return;closed=true;}
                writeWatchdog.Dispose();
                try{process.Dispose();}
                catch(Win32Exception e){error("无法结束视频解码器："+e.Message);}
            }
        }

        // Process.Start uses a small anonymous stdout pipe on Windows. A 1080x2400
        // BGR frame is about 10 MB; give that pipe a bounded 1 MB transfer buffer
        // instead of forcing thousands of writer/reader switches per frame.
        sealed class BufferedDecoderProcess : IDisposable
        {
            internal readonly Stream Input,Output;
            internal readonly StreamReader Error;
            readonly Process child;
            [StructLayout(LayoutKind.Sequential)]
            struct SecurityAttributes { internal int Length;internal IntPtr Descriptor;[MarshalAs(UnmanagedType.Bool)]internal bool Inherit; }
            [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
            struct StartupInfo {
                internal int Size;internal string Reserved,Desktop,Title;
                internal int X,Y,Width,Height,CountX,CountY,Fill,Flags;
                internal short Show,ReservedSize;internal IntPtr ReservedData,Input,Output,Error;
            }
            [StructLayout(LayoutKind.Sequential)]
            struct ProcessInfo { internal IntPtr Process,Thread;internal int Id,ThreadId; }
            [StructLayout(LayoutKind.Sequential)]
            struct ExtendedStartupInfo { internal StartupInfo Startup;internal IntPtr Attributes; }
            [DllImport("kernel32.dll",SetLastError=true)]
            [return:MarshalAs(UnmanagedType.Bool)]
            static extern bool CreatePipe(out IntPtr read,out IntPtr write,ref SecurityAttributes attributes,int size);
            [DllImport("kernel32.dll",SetLastError=true)]
            [return:MarshalAs(UnmanagedType.Bool)]
            static extern bool SetHandleInformation(IntPtr handle,int mask,int flags);
            [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
            [return:MarshalAs(UnmanagedType.Bool)]
            static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,
                [MarshalAs(UnmanagedType.Bool)]bool inherit,int flags,IntPtr environment,string directory,ref ExtendedStartupInfo startup,out ProcessInfo info);
            [DllImport("kernel32.dll",SetLastError=true)]
            [return:MarshalAs(UnmanagedType.Bool)]
            static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref IntPtr size);
            [DllImport("kernel32.dll",SetLastError=true)]
            [return:MarshalAs(UnmanagedType.Bool)]
            static extern bool UpdateProcThreadAttribute(IntPtr list,int flags,IntPtr attribute,IntPtr value,IntPtr size,IntPtr previous,IntPtr returnedSize);
            [DllImport("kernel32.dll")]
            static extern void DeleteProcThreadAttributeList(IntPtr list);
            [DllImport("kernel32.dll")]
            [return:MarshalAs(UnmanagedType.Bool)]
            static extern bool CloseHandle(IntPtr handle);
            static void Close(ref IntPtr handle){if(handle!=IntPtr.Zero){CloseHandle(handle);handle=IntPtr.Zero;}}
            static FileStream Take(ref IntPtr handle,FileAccess access){var safe=new SafeFileHandle(handle,true);handle=IntPtr.Zero;try{return new FileStream(safe,access,4096,false);}catch{safe.Dispose();throw;}}
            internal BufferedDecoderProcess(string executable,string arguments)
            {
                IntPtr inputRead=IntPtr.Zero,inputWrite=IntPtr.Zero,outputRead=IntPtr.Zero,outputWrite=IntPtr.Zero,errorRead=IntPtr.Zero,errorWrite=IntPtr.Zero;
                var attributes=new SecurityAttributes{Length=Marshal.SizeOf(typeof(SecurityAttributes)),Inherit=true};
                ProcessInfo info=new ProcessInfo();
                IntPtr attributeList=IntPtr.Zero,handles=IntPtr.Zero;bool initialized=false;
                try {
                    if(!CreatePipe(out inputRead,out inputWrite,ref attributes,4096)||
                       !CreatePipe(out outputRead,out outputWrite,ref attributes,1024*1024)||
                       !CreatePipe(out errorRead,out errorWrite,ref attributes,4096)||
                       !SetHandleInformation(inputWrite,1,0)||!SetHandleInformation(outputRead,1,0)||!SetHandleInformation(errorRead,1,0))throw new Win32Exception(Marshal.GetLastWin32Error());
                    // Inherit only these three pipe ends, including when other processes
                    // are being started concurrently by the application.
                    IntPtr size=IntPtr.Zero;
                    InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref size);
                    attributeList=Marshal.AllocHGlobal(size);
                    if(!InitializeProcThreadAttributeList(attributeList,1,0,ref size))throw new Win32Exception(Marshal.GetLastWin32Error());
                    initialized=true;handles=Marshal.AllocHGlobal(3*IntPtr.Size);
                    Marshal.WriteIntPtr(handles,0,inputRead);Marshal.WriteIntPtr(handles,IntPtr.Size,outputWrite);Marshal.WriteIntPtr(handles,2*IntPtr.Size,errorWrite);
                    if(!UpdateProcThreadAttribute(attributeList,0,new IntPtr(0x20002),handles,new IntPtr(3*IntPtr.Size),IntPtr.Zero,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());
                    var startup=new ExtendedStartupInfo{Startup=new StartupInfo{Size=Marshal.SizeOf(typeof(ExtendedStartupInfo)),Flags=0x100,Input=inputRead,Output=outputWrite,Error=errorWrite},Attributes=attributeList};
                    if(!CreateProcess(executable,new StringBuilder("\""+executable+"\" "+arguments),IntPtr.Zero,IntPtr.Zero,true,0x08080000,IntPtr.Zero,null,ref startup,out info))throw new Win32Exception(Marshal.GetLastWin32Error());
                    child=Process.GetProcessById(info.Id);
                    Input=Take(ref inputWrite,FileAccess.Write);Output=Take(ref outputRead,FileAccess.Read);
                    Error=new StreamReader(Take(ref errorRead,FileAccess.Read),Encoding.UTF8);
                }catch{if(child!=null)Dispose();throw;}
                finally {
                    if(initialized)DeleteProcThreadAttributeList(attributeList);
                    if(attributeList!=IntPtr.Zero)Marshal.FreeHGlobal(attributeList);
                    if(handles!=IntPtr.Zero)Marshal.FreeHGlobal(handles);
                    Close(ref inputRead);Close(ref inputWrite);Close(ref outputRead);Close(ref outputWrite);Close(ref errorRead);Close(ref errorWrite);
                    Close(ref info.Process);Close(ref info.Thread);
                }
            }
            public void Dispose()
            {
                try{if(!child.HasExited)child.Kill();}catch(InvalidOperationException){}
                finally{if(Input!=null)Input.Dispose();if(Output!=null)Output.Dispose();if(Error!=null)Error.Dispose();child.Dispose();}
            }
        }
    }
}
