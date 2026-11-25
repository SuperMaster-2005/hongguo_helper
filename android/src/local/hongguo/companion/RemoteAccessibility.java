package local.hongguo.companion;

import android.accessibilityservice.*;
import android.graphics.Path;
import android.os.Bundle;
import android.view.WindowManager;
import android.graphics.Rect;
import android.view.accessibility.*;

public class RemoteAccessibility extends AccessibilityService {
    public static volatile RemoteAccessibility instance;
    static final String TARGET_PACKAGE="com.phoenix.read";
    private volatile boolean targetActive;
    static volatile String waitingReason="等待手机端打开红果短剧";
    static boolean isTargetActive() {
        RemoteAccessibility service=instance;
        return service!=null && service.targetActive;
    }
    private final android.os.Handler monitor=new android.os.Handler(android.os.Looper.getMainLooper());
    private final Runnable check=new Runnable(){public void run(){refreshTarget();monitor.postDelayed(this,100);}};
    private void refreshTarget(){
        boolean allowed=false;String reason="等待手机端打开红果短剧";
        android.app.KeyguardManager keyguard=(android.app.KeyguardManager)getSystemService(KEYGUARD_SERVICE);
        android.os.PowerManager power=(android.os.PowerManager)getSystemService(POWER_SERVICE);
        try { if(power.isInteractive() && !keyguard.isKeyguardLocked()){
            AccessibilityNodeInfo root=getRootInActiveWindow();
            if(root!=null){try{
                allowed=TARGET_PACKAGE.contentEquals(root.getPackageName()==null?"":root.getPackageName());
                if(allowed){
                    if(allowed){
                        java.util.List<AccessibilityWindowInfo> windows=getWindows();
                        if(windows.isEmpty()){allowed=false;reason="无法读取应用窗口，保持等待。请检查电脑控制权限。";}
                        for(AccessibilityWindowInfo window:windows){
                            try{
                                if(window.getType()!=AccessibilityWindowInfo.TYPE_APPLICATION)continue;
                                AccessibilityNodeInfo other=window.getRoot();
                                try{if(other==null || !TARGET_PACKAGE.contentEquals(other.getPackageName()==null?"":other.getPackageName())){allowed=false;reason="请退出分屏或悬浮应用，仅在前台打开红果短剧。";}}
                                finally{if(other!=null)other.recycle();}
                            }finally{window.recycle();}
                        }
                    }
                }
            }finally{root.recycle();}}
        }
        }catch(RuntimeException ignored){allowed=false;}
        waitingReason=reason;targetActive=allowed;
    }
    @Override protected void onServiceConnected() { instance = this; monitor.post(check); }
    @Override public void onAccessibilityEvent(AccessibilityEvent event) { targetActive=false; refreshTarget(); }
    @Override public void onInterrupt() { targetActive=false; }
    @Override public void onDestroy() { targetActive=false;monitor.removeCallbacks(check);instance = null; super.onDestroy(); }
    public boolean command(String command) {
        refreshTarget();
        if (!isTargetActive() || !CaptureService.hasApprovedConnection()) return false;
        String[] parts = command.split("\\|",-1);
        try {
            if (parts[0].equals("back")) return performGlobalAction(GLOBAL_ACTION_BACK);
            if (parts[0].equals("home")) return performGlobalAction(GLOBAL_ACTION_HOME);
            if (parts[0].equals("text")) {
                AccessibilityNodeInfo root = getRootInActiveWindow();
                if (root == null) return false;
                AccessibilityNodeInfo focus = null;
                try {
                    // Bind input to the nodes obtained now, not a previous foreground snapshot.
                    if(!root.refresh() || !TARGET_PACKAGE.contentEquals(root.getPackageName()==null?"":root.getPackageName()))return false;
                    focus = root.findFocus(AccessibilityNodeInfo.FOCUS_INPUT);
                    if (focus == null || !focus.refresh() || !focus.isEditable() || focus.isPassword()) return false;
                    if(!textTargetAllowed(root.getPackageName(),focus.getPackageName(),root.getWindowId(),focus.getWindowId())
                        || !CaptureService.canShare() || !CaptureService.hasApprovedConnection())return false;
                    Bundle values = new Bundle(); values.putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE,command.substring(5));
                    return focus.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT,values);
                } finally { if (focus != null) focus.recycle(); root.recycle(); }
            }
            if (!(parts[0].equals("tap") && parts.length == 3) && !(parts[0].equals("swipe") && parts.length == 6)) return false;
            if(!CaptureService.captureRegionKnown)return false;
            int capturedWidth=CaptureService.contentWidth,capturedHeight=CaptureService.contentHeight;
            Rect screen=((WindowManager)getSystemService(WINDOW_SERVICE)).getMaximumWindowMetrics().getBounds();
            Rect rootBounds=new Rect(),windowBounds=new Rect();
            AccessibilityNodeInfo root=getRootInActiveWindow();if(root==null)return false;
            try {
                if(!TARGET_PACKAGE.contentEquals(root.getPackageName()==null?"":root.getPackageName()))return false;
                root.getBoundsInScreen(rootBounds);
                AccessibilityWindowInfo appWindow=root.getWindow();
                if(appWindow!=null)try{
                    if(appWindow.getDisplayId()!=android.view.Display.DEFAULT_DISPLAY)return false;
                    appWindow.getBoundsInScreen(windowBounds);
                }finally{appWindow.recycle();}
            }finally{root.recycle();}
            // Prefer the actual app window; use root content when system bars are excluded from capture.
            int[] mapped=touchBounds(capturedWidth,capturedHeight,screen.width(),screen.height(),
                new int[]{windowBounds.left,windowBounds.top,windowBounds.right,windowBounds.bottom},
                new int[]{rootBounds.left,rootBounds.top,rootBounds.right,rootBounds.bottom});
            if(mapped==null || !CaptureService.captureRegionKnown || capturedWidth!=CaptureService.contentWidth || capturedHeight!=CaptureService.contentHeight)return false;
            Rect bounds=new Rect(mapped[0],mapped[1],mapped[2],mapped[3]);
            Path path = new Path(); path.moveTo(bounds.left+coordinate(parts[1],bounds.width()),bounds.top+coordinate(parts[2],bounds.height()));
            int duration = 60;
            if (parts[0].equals("swipe")) {
                path.lineTo(bounds.left+coordinate(parts[3],bounds.width()),bounds.top+coordinate(parts[4],bounds.height()));
                duration = Math.max(100,Math.min(1500,Integer.parseInt(parts[5])));
            }
            return dispatchGesture(new GestureDescription.Builder().addStroke(new GestureDescription.StrokeDescription(path,0,duration)).build(),null,null);
        } catch (RuntimeException e) { return false; }
    }
    static boolean textTargetAllowed(CharSequence rootPackage,CharSequence focusPackage,int rootWindow,int focusWindow) {
        return rootPackage!=null && focusPackage!=null && TARGET_PACKAGE.contentEquals(rootPackage)
            && TARGET_PACKAGE.contentEquals(focusPackage) && rootWindow>=0 && rootWindow==focusWindow;
    }
    static int[] touchBounds(int capturedWidth,int capturedHeight,int screenWidth,int screenHeight,int[] window,int[] root) {
        if(capturedWidth<1 || capturedHeight<1 || screenWidth<1 || screenHeight<1)return null;
        for(int[] candidate:new int[][]{window,root}) {
            if(candidate==null || candidate.length!=4 || candidate[0]<0 || candidate[1]<0 || candidate[2]>screenWidth || candidate[3]>screenHeight)continue;
            int width=candidate[2]-candidate[0],height=candidate[3]-candidate[1];
            if(width<1 || height<1)continue;
            double a=(double)width*capturedHeight,b=(double)height*capturedWidth;
            // Allow rounding, but do not guess coordinates across rotation or a different capture region.
            if(Math.abs(a-b)<=Math.max(a,b)*0.01)return candidate.clone();
        }
        return null;
    }
    static float coordinate(String value,int dimension) {
        int normalized = Integer.parseInt(value);
        if (dimension<1 || normalized < 0 || normalized > 10000) throw new IllegalArgumentException("Coordinate out of bounds");
        return Math.min(dimension-1,normalized*dimension/10000f);
    }
}
