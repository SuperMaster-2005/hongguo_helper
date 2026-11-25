using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HongguoRemote
{
    public sealed class CompanionDevice
    {
        public string Address, Model, Fingerprint;
        public int Port;
        public override string ToString() { return Model + " · 手机助手"; }
    }
    public sealed class CompanionClient : IDisposable
    {
        public const int DiscoveryPort = 24680;
        public volatile bool Active, Connected;
        TcpClient tcp;
        SslStream stream;
        readonly object writeLock = new object();
        Timer heartbeat;
        public Action<byte[]> Frame, Audio;
        public Action<bool> Sharing, ControlEnabled;
        public Action<VideoFormat> VideoFormatChanged;
        public Action<EncodedFrame> Video;
        public Action<long,byte[]> TimedAudio;
        public Action<string> Status;
        public Action Accepted, Ended;
        bool disposed;

        public static CompanionDevice ParseDiscovery(string text, string nonce, string address)
        {
            string[] p = text.Split('|'); int port;
            if (p.Length != 5 || p[0] != "HGR1" || p[1] != nonce || !Int32.TryParse(p[2], out port) || port < 1 || port > 65535 || !Regex.IsMatch(p[4], "^[a-f0-9]{64}$")) return null;
            try {
                string model = Encoding.UTF8.GetString(Convert.FromBase64String(p[3]));
                if (model.Length == 0 || model.Length > 100 || model.Any(Char.IsControl)) return null;
                return new CompanionDevice { Address = address, Port = port, Model = model, Fingerprint = p[4] };
            } catch (FormatException) { return null; }
        }
        public static Task<List<CompanionDevice>> Discover()
        {
            return Task.Run(() => {
                var found = new List<CompanionDevice>();
                string nonce = Guid.NewGuid().ToString("N");
                byte[] request = Encoding.UTF8.GetBytes("HGR_DISCOVER_V1|" + nonce);
                var targets = new HashSet<string> { "255.255.255.255" };
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && (n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || n.NetworkInterfaceType == NetworkInterfaceType.Ethernet)))
                {
                    var props = adapter.GetIPProperties();
                    foreach (var gateway in props.GatewayAddresses.Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) targets.Add(gateway.Address.ToString());
                    foreach (var unicast in props.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask != null))
                    {
                        byte[] ip = unicast.Address.GetAddressBytes(), mask = unicast.IPv4Mask.GetAddressBytes();
                        targets.Add(new IPAddress(ip.Select((b,i) => (byte)(b | ~mask[i])).ToArray()).ToString());
                    }
                }
                using (var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
                {
                    udp.EnableBroadcast = true; udp.Client.ReceiveTimeout = 250;
                    for (int round = 0; round < 3; round++)
                    {
                        foreach (string ip in targets) { try { udp.Send(request,request.Length,new IPEndPoint(IPAddress.Parse(ip),DiscoveryPort)); } catch (SocketException) { } }
                        DateTime until = DateTime.UtcNow.AddMilliseconds(900);
                        while (DateTime.UtcNow < until)
                        {
                            try {
                                IPEndPoint remote = new IPEndPoint(IPAddress.Any,0);
                                byte[] bytes = udp.Receive(ref remote);
                                if (bytes.Length > 1024) continue;
                                var device = ParseDiscovery(Encoding.UTF8.GetString(bytes),nonce,remote.Address.ToString());
                                if (device != null && !found.Any(d => d.Address == device.Address && d.Port == device.Port)) found.Add(device);
                            } catch (SocketException e) { if (e.SocketErrorCode != SocketError.TimedOut) throw; }
                        }
                    }
                }
                return found;
            });
        }
        public void Start(CompanionDevice device, bool audio)
        {
            if (Active || disposed) throw new InvalidOperationException("连接已启动或已关闭。");
            Active = true;
            Task.Run(() => {
                string stage = "连接手机";
                try {
                    tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 75000, SendTimeout = 4000 };
                    if (disposed) return;
                    var connect = tcp.ConnectAsync(device.Address,device.Port);
                    if (!connect.Wait(5000)) throw new TimeoutException("连接手机超时，请确认手机仍在共享。");
                    stream = new SslStream(tcp.GetStream(),false,(sender, cert, chain, errors) => {
                        if (cert == null) return false;
                        using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(cert.GetRawCertData())).Replace("-","").ToLowerInvariant() == device.Fingerprint;
                    });
                    stage = "TLS 握手";
                    stream.AuthenticateAsClient("HongguoCompanion",null,SslProtocols.Tls12,false);
                    stage = "等待手机授权";
                    SendNow("hello|" + Environment.MachineName + "|h264-v5");
                    while (Active && !disposed)
                    {
                        int type; byte[] data = ReadPacket(stream,out type);
                        if (type == 1)
                        {
                            if (Connected) throw new IOException("重复连接确认。");
                            Connected = true; stage = "已授权，等待媒体";
                            tcp.ReceiveTimeout = 15000;
                            heartbeat = new Timer(s => { try { SendNow("ping"); } catch (Exception) { Dispose(); } },null,1000,5000);
                            if (Accepted != null) Accepted();
                        }
                        else if(type==10 && Connected){if(data.Length!=0)throw new IOException("无效心跳消息。");}
                        else if(type==11 && Connected){
                            if(data.Length!=1 || data[0]>1)throw new IOException("无效遥控状态。");
                            if(ControlEnabled!=null)ControlEnabled(data[0]==1);
                        }
                        else if(type==9 && Connected){
                            if(data.Length!=1 || data[0]>1)throw new IOException("无效共享状态。");
                            if(Sharing!=null)Sharing(data[0]==1);
                        }
                        else if (type == 2 && Connected) { if (Frame != null) Frame(data); }
                        else if (type == 3 && Connected && audio) { if (Audio != null) Audio(data); }
                        else if(type==6&&Connected){stage="读取视频格式";if(VideoFormatChanged!=null)VideoFormatChanged(VideoFormat.Parse(Encoding.UTF8.GetString(data)));}
                        else if(type==7&&Connected){stage="读取视频帧";if(Video!=null)Video(ParseVideo(data));}
                        else if(type==8&&Connected){long timestamp;byte[] pcm=ParseAudio(data,out timestamp);if(audio&&TimedAudio!=null)TimedAudio(timestamp,pcm);}
                        else if (type == 4) { if (Status != null) Status(Encoding.UTF8.GetString(data)); }
                        else if (type == 5) throw new IOException(Encoding.UTF8.GetString(data));
                        else if (type != 3) throw new IOException("手机发送了未知消息。");
                    }
                }
                catch (Exception e) { if (!disposed && Status != null) Status("手机连接结束（"+stage+"）："+e.GetBaseException().Message); }
                finally { Dispose(); if (Ended != null) Ended(); }
            });
        }
        public static byte[] ReadPacket(Stream source,out int type)
        {
            type = source.ReadByte();
            if (type < 0) throw new EndOfStreamException("手机已断开。");
            byte[] header = ReadExactly(source,4);
            int size = (header[0]<<24)|(header[1]<<16)|(header[2]<<8)|header[3];
            int maximum = type == 2 || type == 7 ? 4*1024*1024 : type == 3 || type == 8 ? 19208 : 8192;
            if (size < 0 || size > maximum) throw new IOException("消息长度超出限制。");
            return ReadExactly(source,size);
        }
        static long Timestamp(byte[] data){long value=0;for(int i=0;i<8;i++)value=(value<<8)|data[i];if(value<0)throw new IOException("无效媒体时间戳。");return value;}
        public static EncodedFrame ParseVideo(byte[] data){
            if(data.Length<17)throw new IOException("视频包过短。");
            int flags=(data[8]<<24)|(data[9]<<16)|(data[10]<<8)|data[11];if(flags!=0&&flags!=1)throw new IOException("无效视频标志。");
            byte[] payload=new byte[data.Length-12];Buffer.BlockCopy(data,12,payload,0,payload.Length);
            if(payload[0]!=0||payload[1]!=0||(payload[2]!=1&&(payload[2]!=0||payload[3]!=1)))throw new IOException("不是 Annex B 视频流。");
            return new EncodedFrame{Timestamp=Timestamp(data),Key=flags==1,Data=payload};
        }
        public static byte[] ParseAudio(byte[] data,out long timestamp){if(data.Length<10||(data.Length-8)%2!=0)throw new IOException("音频包长度无效。");timestamp=Timestamp(data);byte[] payload=new byte[data.Length-8];Buffer.BlockCopy(data,8,payload,0,payload.Length);return payload;}
        static byte[] ReadExactly(Stream source,int count)
        {
            byte[] result = new byte[count]; int offset = 0;
            while (offset < count) { int n = source.Read(result,offset,count-offset); if (n <= 0) throw new EndOfStreamException(); offset += n; }
            return result;
        }
        public Task Send(string command) { return Task.Run(() => SendNow(command)); }
        void SendNow(string command)
        {
            byte[] data = Encoding.UTF8.GetBytes(command);
            if (data.Length > 8192) throw new ArgumentException("输入文字过长，请分段输入。");
            lock (writeLock) {
                if (stream == null || disposed) throw new IOException("手机连接已关闭。");
                byte[] header = {16,(byte)(data.Length>>24),(byte)(data.Length>>16),(byte)(data.Length>>8),(byte)data.Length};
                stream.Write(header,0,header.Length); stream.Write(data,0,data.Length); stream.Flush();
            }
        }
        public void Dispose()
        {
            disposed = true; Active = Connected = false;
            if (heartbeat != null) heartbeat.Dispose();
            if (tcp != null) tcp.Close();
        }
    }
}
