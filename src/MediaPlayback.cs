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
    // Measure encoded frame cadence in the phone clock, independent of network arrival bursts.
    internal sealed class SourceFrameTiming
    {
        readonly object gate=new object();
        long previous=-1,total,minimum=long.MaxValue,maximum;
        int intervals,discontinuities;
        internal void Reset(){lock(gate){previous=-1;Clear();}}
        void Clear(){total=0;minimum=long.MaxValue;maximum=0;intervals=discontinuities=0;}
        internal void Observe(long timestamp)
        {
            lock(gate){
                if(previous>=0){
                    long delta=timestamp-previous;
                    if(delta>0 && delta<=2000000){total+=delta;minimum=Math.Min(minimum,delta);maximum=Math.Max(maximum,delta);intervals++;}
                    else discontinuities++;
                }
                previous=timestamp;
            }
        }
        internal string TakeSummary()
        {
            lock(gate){
                string result=intervals==0?"源时间戳样本不足":
                    "源时间戳约 "+(intervals*1000000.0/total).ToString("F1")+" 帧/秒；帧间隔平均/最小/最大 "+
                    (total/1000.0/intervals).ToString("F2")+"/"+(minimum/1000.0).ToString("F2")+"/"+(maximum/1000.0).ToString("F2")+"ms";
                result+="；时间戳不连续 "+discontinuities;Clear();return result;
            }
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
