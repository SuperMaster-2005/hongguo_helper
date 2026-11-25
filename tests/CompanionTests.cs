using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HongguoRemote;

static class CompanionTests
{
    static int count;
    static void Check(bool condition,string label) { if(!condition)throw new Exception("FAIL: "+label); count++;Console.WriteLine("PASS: "+label); }
    static byte[] Packet(int kind,byte[] payload) {
        using(var output=new MemoryStream()) { output.WriteByte((byte)kind); int size=payload.Length; byte[] head={(byte)(size>>24),(byte)(size>>16),(byte)(size>>8),(byte)size};output.Write(head,0,4);output.Write(payload,0,size);return output.ToArray(); }
    }
    static void Send(Stream stream,int type,string text) {byte[] packet=Packet(type,Encoding.UTF8.GetBytes(text));stream.Write(packet,0,packet.Length);stream.Flush();}
    sealed class Fragmented : MemoryStream {
        internal Fragmented(byte[] bytes):base(bytes){}
        public override int Read(byte[] b,int offset,int size) {return base.Read(b,offset,Math.Min(size,2));}
    }
    internal static int Run()
    {
        string nonce="00112233445566778899aabbccddeeff",hash=new string('a',64);
        string response="HGR1|"+nonce+"|24681|"+Convert.ToBase64String(Encoding.UTF8.GetBytes("Redmi K70"))+"|"+hash;
        var device=CompanionClient.ParseDiscovery(response,nonce,"10.48.67.185");
        Check(device!=null&&device.Model=="Redmi K70"&&device.Port==24681,"companion discovery response");
        Check(CompanionClient.ParseDiscovery(response,"wrong","10.0.0.1")==null,"reject stale discovery nonce");
        Check(CompanionClient.ParseDiscovery(response.Replace("24681","65536"),nonce,"10.0.0.1")==null,"reject invalid companion port");
        Check(CompanionClient.ParseDiscovery(response.Replace(hash,"bad"),nonce,"10.0.0.1")==null,"reject invalid certificate fingerprint");
        Check(CompanionClient.ParseDiscovery("HGR1|"+nonce+"|24681|invalid!|"+hash,nonce,"10.0.0.1")==null,"reject invalid model encoding");
        using(var stream=new Fragmented(Packet(4,Encoding.UTF8.GetBytes("手机确认")))) {int type;byte[] data=CompanionClient.ReadPacket(stream,out type);Check(type==4&&Encoding.UTF8.GetString(data)=="手机确认","fragmented network packet reads");}
        bool oversized=false;try {int type;CompanionClient.ReadPacket(new MemoryStream(new byte[]{2,127,255,255,255}),out type);}catch(IOException){oversized=true;}
        Check(oversized,"reject oversized image before allocation");
        bool truncated=false;try {int type;CompanionClient.ReadPacket(new MemoryStream(new byte[]{4,0,0,0,3,1}),out type);}catch(EndOfStreamException){truncated=true;}
        Check(truncated,"reject truncated network frame");
        using(var certificate=new X509Certificate2(Path.GetFullPath("tools/companion-local.p12"),"android",X509KeyStorageFlags.UserKeySet))
        {
            TestSession(certificate,"accept"); TestSession(certificate,"idle"); TestSession(certificate,"deny"); TestSession(certificate,"badcert"); TestSession(certificate,"earlyframe"); TestSession(certificate,"earlysharing"); TestSession(certificate,"badsharing");
        }
        Console.WriteLine("Passed "+count+" companion checks.");return count;
    }
    static void TestSession(X509Certificate2 certificate,string mode)
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
        string fingerprint;using(var sha=SHA256.Create())fingerprint=BitConverter.ToString(sha.ComputeHash(certificate.RawData)).Replace("-","").ToLowerInvariant();
        var ended=new ManualResetEventSlim();var waiting=new ManualResetEventSlim();var allow=new ManualResetEventSlim();
        bool accepted=false,frame=false,audio=false;int controlState=-1;int sharingStates=0;string received=null;
        var server=Task.Run(()=> {
            using(var socket=listener.AcceptTcpClient())using(var tls=new SslStream(socket.GetStream(),false)) {
                socket.ReceiveTimeout=8000;socket.SendTimeout=8000;
                try {tls.AuthenticateAsServer(certificate,false,SslProtocols.Tls12,false);}catch(AuthenticationException){if(mode=="badcert")return;throw;}
                if(mode=="badcert") { try {int ignored;CompanionClient.ReadPacket(tls,out ignored);throw new Exception("Unexpected data after rejected certificate");}catch(IOException){} return; }
                int type;string hello=Encoding.UTF8.GetString(CompanionClient.ReadPacket(tls,out type));
                if(type!=16||!hello.StartsWith("hello|"))throw new Exception("Invalid hello");
                Send(tls,4,"waiting for phone");
                if(!allow.Wait(3000))throw new Exception("Test approval timed out");
                if(mode=="deny"){Send(tls,5,"denied by phone");return;}
                if(mode=="earlyframe"){Send(tls,2,"unauthorized frame");return;}
                if(mode=="earlysharing" || mode=="badsharing"){
                    if(mode=="badsharing")Send(tls,1,"Test phone");
                    byte[] invalid=Packet(9,new byte[]{(byte)(mode=="badsharing"?2:1)});tls.Write(invalid,0,invalid.Length);tls.Flush();return;
                }
                Send(tls,1,"Test phone");
                byte[] control=Packet(11,new byte[]{(byte)(mode=="idle"?1:0)});tls.Write(control,0,control.Length);tls.Flush();
                foreach(byte state in new byte[]{0,1,0}){byte[] update=Packet(9,new byte[]{state});tls.Write(update,0,update.Length);tls.Flush();}
                if(mode=="idle"){
                    for(int i=0;i<4;i++){
                        Thread.Sleep(4500);
                        byte[] pulse=Packet(10,new byte[0]);tls.Write(pulse,0,pulse.Length);tls.Flush();
                    }
                    byte[] resume=Packet(9,new byte[]{1});tls.Write(resume,0,resume.Length);tls.Flush();
                }
                Send(tls,2,"frame");Send(tls,3,"ab");
                do {received=Encoding.UTF8.GetString(CompanionClient.ReadPacket(tls,out type));}while(received=="ping");
            }
        });
        using(var client=new CompanionClient()) {
            client.ControlEnabled=enabled=>Interlocked.Exchange(ref controlState,enabled?1:0);
            client.Sharing=visible=>{int index=Interlocked.Increment(ref sharingStates);if(visible!=(index==2 || (mode=="idle" && index==4)))throw new Exception("Wrong sharing transition");};
            client.Accepted=()=>{accepted=true;};client.Frame=bytes=>{frame=true;};client.Audio=bytes=>{audio=true;};
            client.Status=text=>{if(text=="waiting for phone")waiting.Set();else Console.WriteLine("TLS test status ("+mode+"): "+text);};client.Ended=()=>ended.Set();
            client.Start(new CompanionDevice {Address="127.0.0.1",Port=port,Model="test",Fingerprint=mode=="badcert"?new string('0',64):fingerprint},true);
            try {
                if(mode!="badcert") {Check(waiting.Wait(5000),mode+": receives waiting status");Check(!accepted&&!frame&&!audio,mode+": no media before phone approval");allow.Set();}
                if(mode=="accept" || mode=="idle") {
                    Check(SpinWait.SpinUntil(()=>accepted&&frame&&audio,mode=="idle"?24000:4000),"approved TLS stream receives video and audio");
                    Check(controlState==(mode=="idle"?1:0),"control availability delivered with media");
                    Check(sharingStates==(mode=="idle"?4:3),mode=="idle"?"18-second waiting survives receive timeout and resumes":"TLS waiting, resume, waiting transitions");
                    client.Send("tap|5000|5000").GetAwaiter().GetResult();
                }
                Check(ended.Wait(5000),mode+": disconnect is observed");
                Check(server.Wait(3000),mode+": server finishes");
                if(mode=="accept" || mode=="idle")Check(received=="tap|5000|5000","control command uses encrypted connection");
                else if(mode=="badsharing")Check(accepted && sharingStates==0 && !frame && !audio,"invalid sharing state rejected");
                else Check(!accepted&&!frame&&!audio && sharingStates==0,mode+": no unauthorized media delivered");
            } finally {client.Dispose();listener.Stop();allow.Set();if(server.IsFaulted)Console.WriteLine(server.Exception.GetBaseException().Message);}
        }
    }
}
