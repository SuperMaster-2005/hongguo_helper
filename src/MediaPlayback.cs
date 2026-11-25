using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace HongguoRemote
{
    // Both streams use the phone's monotonic microsecond clock. A small shared buffer absorbs jitter.
    public sealed class MediaClock
    {
        readonly Stopwatch clock=Stopwatch.StartNew();
        readonly object gate=new object();
        double offset=Double.NaN;
        public void Reset(){lock(gate)offset=Double.NaN;}
        public void Observe(long timestamp)
        {
            lock(gate){double candidate=clock.Elapsed.TotalMilliseconds-timestamp/1000.0;if(Double.IsNaN(offset)||candidate<offset)offset=candidate;}
        }
        public double Delay(long timestamp)
        {
            lock(gate){return Double.IsNaN(offset)?0:timestamp/1000.0+offset+70-clock.Elapsed.TotalMilliseconds;}
        }
    }
    internal sealed class TimedAudioPlayer : IDisposable
    {
        sealed class Chunk { internal long Time;internal byte[] Data;internal int Generation; }
        readonly Queue<Chunk> queue=new Queue<Chunk>();
        readonly object gate=new object();
        readonly PcmPlayer player;
        readonly MediaClock clock;
        readonly Thread worker;
        volatile bool stopped;
        int generation;
        internal void Reset(){lock(gate){generation++;queue.Clear();player.Reset();clock.Reset();}}
        internal Action<string> Error;
        internal TimedAudioPlayer(MediaClock clock,PcmPlayer player){this.clock=clock;this.player=player;worker=new Thread(Run){IsBackground=true,Name="Audio playback clock"};worker.Start();}
        internal void Add(long timestamp,byte[] data){clock.Observe(timestamp);lock(gate){if(queue.Count>=8)queue.Dequeue();queue.Enqueue(new Chunk{Time=timestamp,Data=data,Generation=generation});}}
        void Run(){
            try{while(!stopped){
                Chunk next=null;lock(gate){if(queue.Count>0)next=queue.Peek();}
                if(next==null){Thread.Sleep(3);continue;}
                double delay=clock.Delay(next.Time);
                if(delay>player.PendingMilliseconds+3){Thread.Sleep(2);continue;}
                lock(gate){if(queue.Count==0||queue.Peek()!=next)continue;queue.Dequeue();}
                lock(gate){if(next.Generation!=generation)continue;if(delay < -120){player.Reset();continue;}player.Play(next.Data);}
            }}catch(Exception e){if(!stopped&&Error!=null)Error(e.Message);}
        }
        public void Dispose(){stopped=true;worker.Join(500);player.Dispose();}
    }
}
