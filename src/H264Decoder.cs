using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
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
            if(parts.Length!=4||parts[0]!="H264"||!Int32.TryParse(parts[1],out w)||!Int32.TryParse(parts[2],out h)||!Int32.TryParse(parts[3],out f)||w<64||h<64||w>3200||h>3200||Math.Min(w,h)>1440||(long)w*h>3200*1440||w%2!=0||h%2!=0||(f!=30&&f!=60))throw new IOException("手机视频格式不受支持。");
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
                if(queue.Count>=6||(queue.Count>0&&frame.Timestamp-queue.Peek().Timestamp>120000)){queue.Clear();waitingKey=true;restart=true;generation++;request=true;}
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
            readonly Process process;
            readonly VideoFormat format;
            readonly Queue<long> timestamps=new Queue<long>();
            readonly object sync=new object();
            readonly ConcurrentBag<byte[]> recycled=new ConcurrentBag<byte[]>();
            readonly Action<DecodedFrame> onFrame;
            readonly Action<string> error;
            readonly Timer writeWatchdog;
            const int WriteTimeoutMilliseconds=5000;
            long writeStarted;
            bool writing;
            volatile bool writeTimedOut;
            volatile bool closed;
            public Pipeline(string exe,VideoFormat value,Action<DecodedFrame> frame,Action<string> error,Action<string> warning)
            {
                format=value;onFrame=frame;this.error=error;
                var info=new ProcessStartInfo(exe,"-hide_banner -loglevel error -probesize 32 -analyzeduration 0 -flags low_delay -threads 1 -f h264 -i pipe:0 -an -sn -filter_threads 1 -threads 1 -flush_packets 1 -fps_mode passthrough -pix_fmt bgra -f rawvideo pipe:1") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
                process=new Process{StartInfo=info};process.ErrorDataReceived+=(s,e)=>{if(!closed&&!String.IsNullOrWhiteSpace(e.Data))warning("解码器："+e.Data);};
                process.Start();process.BeginErrorReadLine();
                writeWatchdog=new Timer(CheckWrite,null,250,250);
                Task.Run((Action)ReadFrames);
            }
            public void Push(EncodedFrame frame)
            {
                lock(sync){if(closed)return;timestamps.Enqueue(frame.Timestamp);writeStarted=Stopwatch.GetTimestamp();writing=true;}
                try{process.StandardInput.BaseStream.Write(frame.Data,0,frame.Data.Length);process.StandardInput.BaseStream.Flush();}
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
                    Stream output=process.StandardOutput.BaseStream;
                    while(!closed){
                        byte[] pixels;if(!recycled.TryTake(out pixels))pixels=new byte[checked(format.Width*format.Height*4)];int offset=0;
                        while(offset<pixels.Length){int n=output.Read(pixels,offset,pixels.Length-offset);if(n<=0)throw new EndOfStreamException("视频解码器已退出。");offset+=n;}
                        long timestamp;lock(sync){if(timestamps.Count==0)throw new IOException("视频帧时间戳缺失。");timestamp=timestamps.Dequeue();}
                        onFrame(new DecodedFrame{Width=format.Width,Height=format.Height,Pixels=pixels,Timestamp=timestamp,ReturnBuffer=buffer=>{if(!closed&&recycled.Count<12)recycled.Add(buffer);}});
                    }
                }catch(Exception e){if(!closed)error(e.Message);}
            }
            public void Dispose(){
                lock(sync){if(closed)return;closed=true;}
                writeWatchdog.Dispose();
                try{if(!process.HasExited)process.Kill();}
                catch(InvalidOperationException){}
                catch(System.ComponentModel.Win32Exception e){error("无法结束视频解码器："+e.Message);}
            }
        }
    }
}
