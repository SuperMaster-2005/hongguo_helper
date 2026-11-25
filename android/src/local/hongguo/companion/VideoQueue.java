package local.hongguo.companion;

import java.util.ArrayDeque;

/** H.264 P frames cannot be dropped independently: resume only at a fresh keyframe. */
final class VideoQueue {
    private final ArrayDeque<VideoFrame> queue=new ArrayDeque<>();
    private boolean waitingKey=true;
    private int width,height;
    synchronized boolean offer(VideoFrame frame){
        boolean request=false;
        if(width!=frame.width || height!=frame.height){queue.clear();waitingKey=true;width=frame.width;height=frame.height;request=true;}
        if(queue.size()>=6 || (!queue.isEmpty() && frame.pts-queue.peek().pts>120_000)){queue.clear();waitingKey=true;request=true;}
        if(waitingKey && !frame.key)return true;
        waitingKey=false;queue.add(frame);return request;
    }
    synchronized VideoFrame poll(){return queue.poll();}
    synchronized void reset(){queue.clear();waitingKey=true;}
    synchronized int size(){return queue.size();}
}
