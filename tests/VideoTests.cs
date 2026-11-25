using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using HongguoRemote;

static class VideoTests
{
    internal static void Run()
    {
        CheckDecode(1280,720);
        CheckDecode(1920,1080);
        CheckDecode(1080,1920);
        CheckDecode(2560,1440);
        CheckDecode(1440,2560);
        bool rejected=false;
        try { VideoFormat.Parse("H264|1920|1920|60"); } catch(IOException) { rejected=true; }
        if(!rejected)throw new Exception("Oversized format accepted");
        CheckBlockedDecode();
    }
    // The decoder launches this test executable with FFmpeg arguments to exercise a real blocked pipe.
    internal static int RunStalledDecoder()
    {
        string marker=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"decoder-starts.txt");
        File.AppendAllText(marker,Process.GetCurrentProcess().Id+Environment.NewLine);
        if(File.ReadAllLines(marker).Length==1){Thread.Sleep(20000);return 0;}
        Stream input=Console.OpenStandardInput();byte[] data=new byte[1024*1024];int offset=0;
        while(offset<data.Length){int read=input.Read(data,offset,data.Length-offset);if(read<=0)return 1;offset+=read;}
        byte[] pixels=new byte[1280*720*4];pixels[0]=42;
        Stream output=Console.OpenStandardOutput();output.Write(pixels,0,pixels.Length);output.Flush();
        Thread.Sleep(20000);return 0;
    }
    static void CheckBlockedDecode()
    {
        string executable=typeof(VideoTests).Assembly.Location;
        string marker=Path.Combine(Path.GetDirectoryName(executable),"decoder-starts.txt");
        File.WriteAllText(marker,"");
        int requests=0;bool timeout=false,validFrame=false;
        using(var received=new ManualResetEventSlim()) {
            using(var decoder=new H264Decoder(executable)) {
                byte[] data=new byte[1024*1024];data[2]=1;
                decoder.Error=message=>{if(message.Contains("超时"))timeout=true;};
                decoder.RequestKeyframe=()=>{
                    Interlocked.Increment(ref requests);
                    decoder.Enqueue(new EncodedFrame{Timestamp=2000000,Key=true,Data=data});
                };
                decoder.Frame=frame=>{
                    validFrame=frame.Width==1280 && frame.Height==720 && frame.Timestamp==2000000 && frame.Pixels[0]==42;
                    frame.Dispose();received.Set();
                };
                decoder.Configure(new VideoFormat{Width=1280,Height=720,Fps=60});
                decoder.Enqueue(new EncodedFrame{Timestamp=1000000,Key=true,Data=data});
                if(!received.Wait(9000) || !validFrame || !timeout || requests<1 || File.ReadAllLines(marker).Length!=2)
                    throw new Exception("Blocked decoder did not time out, restart and deliver a fresh frame");
            }
        }
        foreach(string id in File.ReadAllLines(marker)) {
            try {using(var child=Process.GetProcessById(Int32.Parse(id)))if(!child.WaitForExit(1500))throw new Exception("Decoder fixture was not stopped");}
            catch(ArgumentException){}
        }
        Console.WriteLine("PASS: stalled decoder write times out, restarts at keyframe, delivers frame and cleans up");
    }
    static void CheckDecode(int width,int height)
    {
        string exe = Path.GetFullPath("tools/ffmpeg/ffmpeg.exe");
        string sample = Path.GetFullPath("tests/out/sample60.h264");
        var info = new ProcessStartInfo(exe, "-hide_banner -loglevel error -y -f lavfi -i testsrc2=size="+width+"x"+height+":rate=60 -frames:v 120 -c:v libx264 -preset ultrafast -tune zerolatency -x264-params aud=1:keyint=60:scenecut=0 -f h264 " + ProcessRunner.Quote(sample)) { UseShellExecute=false, CreateNoWindow=true };
        using(var generator=Process.Start(info)) {
            if(!generator.WaitForExit(15000) || generator.ExitCode!=0) throw new Exception("H264 fixture generation failed");
        }
        byte[] data=File.ReadAllBytes(sample);
        var starts=new List<int>();
        for(int i=0;i+4<data.Length;i++)
            if(data[i]==0 && data[i+1]==0 && data[i+2]==0 && data[i+3]==1 && (data[i+4]&31)==9) starts.Add(i);
        if(starts.Count!=120) throw new Exception("Expected 120 access units");
        starts.Add(data.Length);
        int frames=0; string error=null;
        using(var decoder=new H264Decoder(exe)) {
            decoder.Error=message=>error=message;
            decoder.Frame=frame=>{
                if(frame.Width!=width || frame.Height!=height || frame.Pixels.Length!=width*height*4) error="Invalid decoded dimensions";
                frame.Dispose();Interlocked.Increment(ref frames);
            };
            decoder.Configure(VideoFormat.Parse("H264|"+width+"|"+height+"|60"));
            var elapsed=Stopwatch.StartNew();
            for(int i=0;i<120;i++) {
                while(elapsed.Elapsed.TotalMilliseconds<i*1000.0/60) Thread.Sleep(1);
                byte[] unit=new byte[starts[i+1]-starts[i]];
                Buffer.BlockCopy(data,starts[i],unit,0,unit.Length);
                decoder.Enqueue(new EncodedFrame{Data=unit,Timestamp=i*1000000L/60,Key=i%60==0});
            }
            SpinWait.SpinUntil(()=>frames>=118 || error!=null,2000);
            if(error!=null || frames<118) throw new Exception(width+"x"+height+" 60fps decoder: "+frames+" frames; "+error);
            Console.WriteLine("PASS: "+width+"x"+height+" 60fps real H264 decode, "+frames+"/120 live frames");
        }
    }
}
