package local.hongguo.companion;

import android.media.*;
import android.os.*;
import android.view.Surface;
import java.io.*;
import java.nio.ByteBuffer;

/** Surface -> hardware AVC. Call lifecycle methods only on the capture handler. */
final class VideoEncoder implements AutoCloseable {
    interface Sink { void frame(VideoFrame frame); void error(String message); }
    final int width,height,fps;
    final String codecName;
    final Surface surface;
    private final MediaCodec codec;
    private final Sink sink;
    private byte[] config = new byte[0];
    private boolean closed;
    private long lastRequest;
    static VideoEncoder create(int requestedWidth,int requestedHeight,int maxFps,Handler handler,Sink sink) throws IOException {
        Exception lastFailure=null;
        MediaCodecInfo[] codecs=new MediaCodecList(MediaCodecList.REGULAR_CODECS).getCodecInfos();
        for (int rate : new int[]{60,30}) {
            if(rate>maxFps)continue;
            for(int longEdge : new int[]{3200,2560,1920,1280,960}) {
                int sourceEdge=Math.max(requestedWidth,requestedHeight);
                float scale=Math.min(1f,(float)longEdge/sourceEdge);
                if(longEdge!=3200 && sourceEdge<=longEdge)continue;
                for(MediaCodecInfo info:codecs) {
                    if(!info.isEncoder() || !info.isHardwareAccelerated() || info.isAlias()) continue;
                    try {
                        boolean avc=false;
                        for(String type:info.getSupportedTypes())if(MediaFormat.MIMETYPE_VIDEO_AVC.equalsIgnoreCase(type))avc=true;
                        if(!avc)continue;
                        MediaCodecInfo.CodecCapabilities caps=info.getCapabilitiesForType(MediaFormat.MIMETYPE_VIDEO_AVC);
                        boolean surface=false;
                        for(int color:caps.colorFormats)if(color==MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)surface=true;
                        if(!surface)continue;
                        MediaCodecInfo.VideoCapabilities video=caps.getVideoCapabilities();
                        int[] size=fitSize(Math.max(1,(int)(requestedWidth*scale)),Math.max(1,(int)(requestedHeight*scale)),
                            video.getSupportedWidths().getUpper(),video.getSupportedHeights().getUpper(),video.getWidthAlignment(),video.getHeightAlignment());
                        if(size==null || !video.getSupportedWidths().contains(size[0]))continue;
                        // Some encoders have coupled width/height limits (for example, a pixel budget).
                        size=fitSize(Math.max(1,(int)(requestedWidth*scale)),Math.max(1,(int)(requestedHeight*scale)),
                            size[0],video.getSupportedHeightsFor(size[0]).getUpper(),video.getWidthAlignment(),video.getHeightAlignment());
                        if(size==null)continue;
                        int w=size[0],h=size[1];
                        if(!video.areSizeAndRateSupported(w,h,rate))continue;
                        // Report success only after configure, Surface creation and start all succeed.
                        try {
                            return new VideoEncoder(info,w,h,rate,handler,sink,true);
                        } catch(IOException | RuntimeException e) {
                            android.util.Log.i("HongguoEncoder","以基本参数重试 "+info.getName(),e);
                            return new VideoEncoder(info,w,h,rate,handler,sink,false);
                        }
                    }catch(IOException | RuntimeException e){
                        lastFailure=e;
                        android.util.Log.w("HongguoEncoder","尝试下一硬件编码配置："+info.getName()+" / "+longEdge+" / "+rate,e);
                    }
                }
            }
        }
        throw new IOException("所有 H.264 硬件编码配置均启动失败。",lastFailure);
    }
    // Preserve the source aspect ratio while respecting hardware alignment and desktop protocol limits.
    static int[] fitSize(int sourceWidth,int sourceHeight,int maxWidth,int maxHeight,int alignWidth,int alignHeight) {
        if(sourceWidth<1 || sourceHeight<1 || maxWidth<1 || maxHeight<1 || alignWidth<1 || alignHeight<1)return null;
        long aw=alignWidth%2==0?alignWidth:2L*alignWidth,ah=alignHeight%2==0?alignHeight:2L*alignHeight;
        double scale=Math.min(1,Math.min((double)maxWidth/sourceWidth,(double)maxHeight/sourceHeight));
        scale=Math.min(scale,Math.min(3200.0/Math.max(sourceWidth,sourceHeight),1440.0/Math.min(sourceWidth,sourceHeight)));
        int w=(int)((long)Math.floor(sourceWidth*scale)/aw*aw),h=(int)((long)Math.floor(sourceHeight*scale)/ah*ah);
        return w<64 || h<64?null:new int[]{w,h};
    }
    private VideoEncoder(MediaCodecInfo selected,int w,int h,int rate,Handler handler,Sink sink,boolean preferred) throws IOException {
        this.sink=sink;
        width=w;height=h;fps=rate;codecName=selected.getName();
        codec=MediaCodec.createByCodecName(codecName);
        Surface created=null;
        try {
            MediaFormat format=MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC,width,height);
            format.setInteger(MediaFormat.KEY_COLOR_FORMAT,MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
            int bitrate=(int)Math.max(6_000_000L,Math.min(32_000_000L,18_000_000L*width*height*fps/(1920L*1080*60)));
            android.util.Range<Integer> rates=selected.getCapabilitiesForType(MediaFormat.MIMETYPE_VIDEO_AVC).getVideoCapabilities().getBitrateRange();
            format.setInteger(MediaFormat.KEY_BIT_RATE,rates.clamp(bitrate));
            format.setInteger(MediaFormat.KEY_FRAME_RATE,fps);

            format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL,1);
            format.setInteger(MediaFormat.KEY_MAX_B_FRAMES,0);
            if(preferred){
                format.setFloat(MediaFormat.KEY_MAX_FPS_TO_ENCODER,(float)fps);
                format.setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER,250000L);
                for(MediaCodecInfo.CodecProfileLevel profile:selected.getCapabilitiesForType(MediaFormat.MIMETYPE_VIDEO_AVC).profileLevels)
                    if(profile.profile==MediaCodecInfo.CodecProfileLevel.AVCProfileBaseline){format.setInteger(MediaFormat.KEY_PROFILE,profile.profile);break;}
            }
            codec.setCallback(new MediaCodec.Callback() {
                public void onInputBufferAvailable(MediaCodec c,int index){}
                public void onError(MediaCodec c,MediaCodec.CodecException e){if(!closed)sink.error(e.getMessage());}
                public void onOutputFormatChanged(MediaCodec c,MediaFormat f){
                    ByteArrayOutputStream bytes=new ByteArrayOutputStream();
                    for(String key:new String[]{"csd-0","csd-1"})if(f.containsKey(key)){ByteBuffer b=f.getByteBuffer(key).duplicate();byte[] data=new byte[b.remaining()];b.get(data);bytes.write(data,0,data.length);}
                    config=bytes.toByteArray();
                }
                public void onOutputBufferAvailable(MediaCodec c,int index,MediaCodec.BufferInfo info){
                    try {
                        if(closed || info.size<=0)return;
                        ByteBuffer buffer=c.getOutputBuffer(index);if(buffer==null)return;
                        buffer.position(info.offset);buffer.limit(info.offset+info.size);
                        byte[] bytes=new byte[info.size];buffer.get(bytes);
                        if((info.flags&MediaCodec.BUFFER_FLAG_CODEC_CONFIG)!=0){config=bytes;return;}
                        boolean key=(info.flags&MediaCodec.BUFFER_FLAG_KEY_FRAME)!=0;
                        if(key){byte[] combined=new byte[config.length+bytes.length];System.arraycopy(config,0,combined,0,config.length);System.arraycopy(bytes,0,combined,config.length,bytes.length);bytes=combined;}
                        sink.frame(new VideoFrame(width,height,fps,info.presentationTimeUs,key,bytes));
                    }catch(RuntimeException e){if(!closed)sink.error(e.getMessage());}
                    finally {try{c.releaseOutputBuffer(index,false);}catch(IllegalStateException ignored){}}
                }
            },handler);
            codec.configure(format,null,null,MediaCodec.CONFIGURE_FLAG_ENCODE);
            created=codec.createInputSurface();surface=created;codec.start();
        }catch(Exception e){
            closed=true;
            try{if(created!=null)created.release();}catch(RuntimeException cleanup){e.addSuppressed(cleanup);}
            try{codec.release();}catch(RuntimeException cleanup){e.addSuppressed(cleanup);}
            throw new IOException("硬件编码器启动失败："+e.getMessage(),e);
        }
    }
    void requestKeyframe(){
        if(closed)return;
        long now=SystemClock.elapsedRealtime();if(now-lastRequest<300)return;lastRequest=now;
        Bundle params=new Bundle();params.putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME,0);
        try{codec.setParameters(params);}catch(IllegalStateException ignored){}
    }
    public void close(){
        if(closed)return;
        closed=true;
        try{try{codec.stop();}catch(IllegalStateException ignored){}}
        finally{try{codec.release();}finally{surface.release();}}
    }
}
