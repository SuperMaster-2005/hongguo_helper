package local.hongguo.companion;

import java.util.Arrays;

/** Tests pure sizing/mapping decisions from production classes; Android APIs are not invoked. */
public final class AndroidCompatibilityTests {
    private static int checks;
    private static void check(boolean value,String label) {
        if(!value)throw new AssertionError(label);
        checks++;System.out.println("PASS: "+label);
    }
    private static void size(int sourceWidth,int sourceHeight,int maxWidth,int maxHeight,int aw,int ah) {
        int[] value=VideoEncoder.fitSize(sourceWidth,sourceHeight,maxWidth,maxHeight,aw,ah);
        check(value!=null,"size available for "+sourceWidth+"x"+sourceHeight+" / alignment "+aw+"x"+ah);
        int w=value[0],h=value[1];
        check(w>=64 && h>=64 && w<=sourceWidth && h<=sourceHeight && w<=maxWidth && h<=maxHeight
            && Math.max(w,h)<=3200 && Math.min(w,h)<=1440 && w%aw==0 && h%ah==0 && w%2==0 && h%2==0,
            "hardware and desktop bounds respected: "+w+"x"+h);
        double ratio=(double)sourceWidth/sourceHeight;
        check(Math.abs((double)w/h-ratio)/ratio<0.035,"source aspect ratio retained within alignment rounding");
    }
    private static void bounds(int cw,int ch,int sw,int sh,int[] window,int[] root,int[] expected,String label) {
        check(Arrays.equals(RemoteAccessibility.touchBounds(cw,ch,sw,sh,window,root),expected),label);
    }
    public static void main(String[] args) {
        VideoQueue queue=new VideoQueue();
        for(int i=0;i<13;i++)queue.offer(new VideoFrame(1080,2400,120,i*8333L,i==0,new byte[1]));
        check(queue.size()==13,"120fps short burst retains dependent frames");
        queue.offer(new VideoFrame(1080,2400,120,200000,false,new byte[1]));
        check(queue.size()==0,"stale burst waits for a fresh keyframe");
        queue.offer(new VideoFrame(1080,2400,120,208333,false,new byte[1]));
        check(queue.size()==0,"dependent frame rejected after overflow");
        queue.offer(new VideoFrame(1080,2400,120,216666,true,new byte[1]));
        check(queue.size()==1 && queue.poll().key,"fresh keyframe resumes queue");
        size(1080,2400,4096,4096,2,2);
        size(1440,3200,4096,4096,16,16);
        size(2400,1080,1920,1080,16,16);
        size(1080,2400,1080,1920,16,16);
        size(2208,1840,4096,4096,8,8);
        size(3840,2160,4096,4096,32,16);
        size(722,1603,4096,4096,3,5);
        size(720,1280,720,1280,2,2);
        check(VideoEncoder.fitSize(0,2400,4096,4096,16,16)==null,"unknown source dimensions rejected");
        check(VideoEncoder.fitSize(1080,2400,32,32,16,16)==null,"sizes below desktop minimum rejected");
        check(VideoEncoder.fitSize(1080,2400,4096,4096,0,16)==null,"invalid hardware alignment rejected");
        int[] full={0,0,1080,2400},app={0,80,1080,2320};
        bounds(1080,2240,1080,2400,app,null,app,"portrait app window preserves status/navigation offsets");
        bounds(1080,2240,1080,2400,full,app,app,"root bounds used when window includes system bars");
        bounds(1080,2400,1080,2400,full,app,full,"edge-to-edge app uses whole window");
        int[] landscape={80,0,2320,1080};
        bounds(2240,1080,2400,1080,landscape,null,landscape,"landscape navigation offset preserved");
        int[] folded={0,64,2208,1776};
        bounds(2208,1712,2208,1840,folded,null,folded,"foldable display dimensions supported");
        bounds(1080,2240,1080,2400,new int[]{0,0,1080,1800},null,null,"wrong aspect ratio rejected");
        bounds(2240,1080,1080,2400,app,null,null,"stale bounds during rotation rejected");
        bounds(1080,2240,1080,2400,new int[]{0,80,1080,2480},null,null,"off-screen window rejected");
        bounds(0,0,1080,2400,app,null,null,"unknown captured region rejects input");
        bounds(1080,2240,1080,2400,null,null,null,"missing window bounds do not guess a full screen");
        check(RemoteAccessibility.coordinate("0",1080)==0 && RemoteAccessibility.coordinate("10000",1080)==1079,
            "touch endpoints remain inside window");
        check(RemoteAccessibility.coordinate("5000",2240)==1120,"normalized midpoint maps to app region");
        boolean invalid=false;try{RemoteAccessibility.coordinate("5000",0);}catch(IllegalArgumentException expected){invalid=true;}
        check(invalid,"empty touch dimension rejected");
        check(RemoteAccessibility.textTargetAllowed("com.phoenix.read","com.phoenix.read",7,7),"text accepts matching red-fruit window and node");
        check(!RemoteAccessibility.textTargetAllowed("other.app","other.app",7,7),"text rejects another active app");
        check(!RemoteAccessibility.textTargetAllowed("com.phoenix.read","other.app",7,7),"text rejects a foreign focused node");
        check(!RemoteAccessibility.textTargetAllowed("com.phoenix.read","com.phoenix.read",7,8),"text rejects focus from a different window");
        check(!RemoteAccessibility.textTargetAllowed(null,"com.phoenix.read",7,7),"text rejects unknown root package");
        check(!RemoteAccessibility.textTargetAllowed("com.phoenix.read",null,7,7),"text rejects unknown focused package");
        check(!RemoteAccessibility.textTargetAllowed("com.phoenix.read","com.phoenix.read",-1,-1),"text rejects unknown window identity");
        System.out.println("Passed "+checks+" Android compatibility checks.");
    }
}
