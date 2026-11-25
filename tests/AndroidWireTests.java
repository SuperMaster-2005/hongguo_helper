package local.hongguo.companion;

import java.io.*;
import java.net.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicInteger;

/** Runs the production write watchdog on the host JVM; no Android runtime required. */
public final class AndroidWireTests {
    private static void check(boolean value,String message) {
        if(!value)throw new AssertionError(message);
        System.out.println("PASS: "+message);
    }
    public static void main(String[] args) throws Exception {
        AtomicInteger closes=new AtomicInteger();
        try(Wire.WriteWatchdog watchdog=new Wire.WriteWatchdog(()->closes.incrementAndGet(),80)) {
            Thread.sleep(200);
            check(closes.get()==0,"idle approval wait does not time out");
            for(int i=0;i<10;i++){watchdog.begin();watchdog.end();}
            Thread.sleep(200);
            check(closes.get()==0,"completed writes cancel their deadline");
        }
        Thread.sleep(150);
        check(closes.get()==0,"closing watchdog cancels monitoring without closing peer");

        CountDownLatch expired=new CountDownLatch(1);
        try(Wire.WriteWatchdog watchdog=new Wire.WriteWatchdog(expired::countDown,80)) {
            watchdog.begin();
            check(expired.await(2,TimeUnit.SECONDS),"unfinished write closes peer");
            boolean rejected=false;
            try{watchdog.begin();}catch(IOException expected){rejected=true;}
            check(rejected,"expired connection rejects further writes");
            watchdog.end();
        }

        try(ServerSocket listener=new ServerSocket(0,1,InetAddress.getLoopbackAddress());
            Socket sender=new Socket(InetAddress.getLoopbackAddress(),listener.getLocalPort());
            Socket peer=listener.accept();
            Wire.WriteWatchdog watchdog=new Wire.WriteWatchdog(sender,200)) {
            sender.setSendBufferSize(1024);
            peer.setReceiveBufferSize(1024);
            CountDownLatch released=new CountDownLatch(1);
            Thread writer=new Thread(()->{
                try {
                    watchdog.begin();
                    byte[] data=new byte[65536];
                    while(true)sender.getOutputStream().write(data);
                }catch(IOException expected){released.countDown();}
                finally{watchdog.end();}
            },"blocked-write-test");
            writer.setDaemon(true);writer.start();
            check(released.await(3,TimeUnit.SECONDS),"non-reading TCP peer releases blocked writer");
            writer.join(1000);
            check(!writer.isAlive() && sender.isClosed(),"timed-out writer exits and socket closes");
        }
    }
}
