using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HongguoRemote
{
    // 手机发送 48 kHz、单声道、16 位 PCM。限制缓冲，网络慢时丢旧声音而不无限延迟。
    internal sealed class PcmPlayer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, Pack=2)] struct Format {
            public ushort Tag, Channels; public uint Rate, BytesPerSecond; public ushort Align, Bits, Extra;
        }
        [StructLayout(LayoutKind.Sequential)] struct Header {
            public IntPtr Data; public uint Length, Recorded; public UIntPtr User;
            public uint Flags, Loops; public IntPtr Next; public UIntPtr Reserved;
        }
        [DllImport("winmm.dll")] static extern uint waveOutOpen(out IntPtr device,uint id,ref Format format,IntPtr callback,IntPtr instance,uint flags);
        [DllImport("winmm.dll")] static extern uint waveOutPrepareHeader(IntPtr device,IntPtr header,uint size);
        [DllImport("winmm.dll")] static extern uint waveOutWrite(IntPtr device,IntPtr header,uint size);
        [DllImport("winmm.dll")] static extern uint waveOutUnprepareHeader(IntPtr device,IntPtr header,uint size);
        [DllImport("winmm.dll")] static extern uint waveOutReset(IntPtr device);
        [DllImport("winmm.dll")] static extern uint waveOutClose(IntPtr device);
        IntPtr device;
        readonly List<IntPtr> buffers = new List<IntPtr>();
        readonly object gate = new object();
        readonly uint size = (uint)Marshal.SizeOf(typeof(Header));
        bool disposed;
        internal double PendingMilliseconds {get {lock(gate){Reap();long bytes=0;foreach(IntPtr pointer in buffers)bytes+=((Header)Marshal.PtrToStructure(pointer,typeof(Header))).Length;return bytes/96.0;}}}
        internal void Reset(){lock(gate){if(device!=IntPtr.Zero){waveOutReset(device);Reap();}}}
        internal void Play(byte[] pcm)
        {
            lock (gate) {
                if (disposed || pcm.Length == 0 || pcm.Length % 2 != 0) return;
                if (device == IntPtr.Zero) {
                    var format = new Format { Tag=1, Channels=1, Rate=48000, BytesPerSecond=96000, Align=2, Bits=16 };
                    if (waveOutOpen(out device,UInt32.MaxValue,ref format,IntPtr.Zero,IntPtr.Zero,0) != 0) throw new InvalidOperationException("电脑音频输出不可用。");
                }
                Reap();
                if (buffers.Count >= 6) return;
                IntPtr data=Marshal.AllocHGlobal(pcm.Length), pointer=Marshal.AllocHGlobal((int)size);
                Marshal.Copy(pcm,0,data,pcm.Length);
                Marshal.StructureToPtr(new Header { Data=data, Length=(uint)pcm.Length },pointer,false);
                uint result=waveOutPrepareHeader(device,pointer,size);
                if (result == 0) result=waveOutWrite(device,pointer,size);
                if (result != 0) {
                    waveOutUnprepareHeader(device,pointer,size); Marshal.FreeHGlobal(data); Marshal.FreeHGlobal(pointer);
                    throw new InvalidOperationException("电脑音频播放失败：" + result);
                }
                buffers.Add(pointer);
            }
        }
        void Reap()
        {
            for (int i=buffers.Count-1;i>=0;i--) {
                Header header=(Header)Marshal.PtrToStructure(buffers[i],typeof(Header));
                if ((header.Flags & 1)==0) continue;
                waveOutUnprepareHeader(device,buffers[i],size); Marshal.FreeHGlobal(header.Data); Marshal.FreeHGlobal(buffers[i]); buffers.RemoveAt(i);
            }
        }
        public void Dispose()
        {
            lock (gate) {
                disposed=true;
                if (device!=IntPtr.Zero) {
                    waveOutReset(device); Reap(); waveOutClose(device); device=IntPtr.Zero;
                }
            }
        }
    }
}
