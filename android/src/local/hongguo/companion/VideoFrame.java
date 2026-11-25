package local.hongguo.companion;

final class VideoFrame {
    final int width,height,fps;
    final long pts;
    final boolean key;
    final byte[] data;
    VideoFrame(int w,int h,int fps,long pts,boolean key,byte[] data){width=w;height=h;this.fps=fps;this.pts=pts;this.key=key;this.data=data;}
}
