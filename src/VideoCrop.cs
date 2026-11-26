using System;
using System.IO;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace HongguoRemote
{
    // Cropping affects presentation only; input is mapped back to the original frame.
    internal sealed class VideoCrop
    {
        readonly Image target;
        readonly Window owner;
        readonly string path;
        BitmapSource source;
        Rect region = new Rect(0, 0, 1, 1);
        double aspect;
        bool editing;
        bool automaticOffered;
        WriteableBitmap croppedFrame;
        Int32Rect pixels;
        DecodedFrame latestFrame;
        bool sourceNeedsUpdate;
        public bool NeedsAutomaticCrop { get { return !automaticOffered && source!=null; } }
        public VideoCrop(Window owner, Image target)
        {
            this.owner = owner; this.target = target;
            path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crop.txt");
            try {
                if (File.Exists(path)) {
                    string[] p = File.ReadAllText(path).Split('|');
                    var c = CultureInfo.InvariantCulture;
                    Rect r = new Rect(double.Parse(p[0],c),double.Parse(p[1],c),double.Parse(p[2],c),double.Parse(p[3],c));
                    double a = double.Parse(p[4],c);
                    if (Valid(r) && a > 0 && !double.IsInfinity(a)) { region=r; aspect=a; automaticOffered=true; }
                }
            } catch(Exception e) { DiagnosticLog.Current.Write("WARN", "读取裁剪设置失败："+e.Message); }
        }
        static bool Valid(Rect r) { return !r.IsEmpty && r.X>=0 && r.Y>=0 && r.Width>=0.05 && r.Height>=0.05 && r.Right<=1 && r.Bottom<=1; }
        Rect Current { get { return source!=null && aspect>0 && Math.Abs((double)source.PixelWidth/source.PixelHeight/aspect-1)<0.02 ? region : new Rect(0,0,1,1); } }
        public void Display(BitmapSource frame)
        {
            if(!Object.ReferenceEquals(source,frame)) {
                if(latestFrame!=null){latestFrame.Dispose();latestFrame=null;}
                sourceNeedsUpdate=false;
            } else UpdateSource();
            source=frame;
            if(frame==null){target.Source=null;croppedFrame=null;return;}
            Rect r=Current;
            if(r==new Rect(0,0,1,1)){croppedFrame=null;target.Source=frame;}
            else {
                int x=(int)(r.X*frame.PixelWidth), y=(int)(r.Y*frame.PixelHeight);
                int w=Math.Max(1,Math.Min(frame.PixelWidth-x,(int)(r.Width*frame.PixelWidth)));
                int h=Math.Max(1,Math.Min(frame.PixelHeight-y,(int)(r.Height*frame.PixelHeight)));
                pixels=new Int32Rect(x,y,w,h);
                croppedFrame=new WriteableBitmap(w,h,96,96,frame.Format,null);
                target.Source=croppedFrame;
                Refresh();
            }
        }
        // Success transfers ownership of the pooled frame to this presenter. Retaining one
        // frame allows full-image snapshots on demand without a second full copy every tick.
        public bool Present(DecodedFrame frame)
        {
            var destination=croppedFrame ?? source as WriteableBitmap;
            if(destination==null || source==null || frame.Width!=source.PixelWidth || frame.Height!=source.PixelHeight)return false;
            if(!destination.TryLock(new Duration(TimeSpan.FromMilliseconds(2))))return false;
            try {
                Int32Rect area=croppedFrame==null?new Int32Rect(0,0,frame.Width,frame.Height):pixels;
                int stride=checked(frame.Width*4), rowBytes=checked(area.Width*4);
                if(area.X==0 && area.Width==frame.Width && destination.BackBufferStride==stride)
                    System.Runtime.InteropServices.Marshal.Copy(frame.Pixels,area.Y*stride,destination.BackBuffer,area.Height*stride);
                else for(int row=0;row<area.Height;row++)
                    System.Runtime.InteropServices.Marshal.Copy(frame.Pixels,(area.Y+row)*stride+area.X*4,
                        IntPtr.Add(destination.BackBuffer,row*destination.BackBufferStride),rowBytes);
                destination.AddDirtyRect(new Int32Rect(0,0,area.Width,area.Height));
            }finally{destination.Unlock();}
            if(latestFrame!=null)latestFrame.Dispose();
            latestFrame=frame;sourceNeedsUpdate=croppedFrame!=null;
            return true;
        }
        void UpdateSource()
        {
            if(!sourceNeedsUpdate || latestFrame==null)return;
            var bitmap=source as WriteableBitmap;
            if(bitmap==null)return;
            bitmap.WritePixels(new Int32Rect(0,0,latestFrame.Width,latestFrame.Height),latestFrame.Pixels,latestFrame.Width*4,0);
            sourceNeedsUpdate=false;
        }
        // A CroppedBitmap can expose fresh pixels without invalidating the displayed image.
        // Explicit dirty rectangles keep WPF presentation in step with decoded frames.
        public void Refresh()
        {
            if(croppedFrame==null || source==null)return;
            UpdateSource();
            if(!croppedFrame.TryLock(new Duration(TimeSpan.FromMilliseconds(2))))return;
            try {
                source.CopyPixels(pixels,croppedFrame.BackBuffer,croppedFrame.BackBufferStride*croppedFrame.PixelHeight,croppedFrame.BackBufferStride);
                croppedFrame.AddDirtyRect(new Int32Rect(0,0,croppedFrame.PixelWidth,croppedFrame.PixelHeight));
            }finally{croppedFrame.Unlock();}
        }
        public Point Map(Point normalized)
        {
            if(croppedFrame==null || source==null)return normalized;
            Int32Rect r=pixels;
            return new Point((r.X+normalized.X/10000*r.Width)/source.PixelWidth*10000,
                (r.Y+normalized.Y/10000*r.Height)/source.PixelHeight*10000);
        }
        BitmapSource Snapshot()
        {
            if(source==null)return null;
            UpdateSource();
            double scale=Math.Min(1,480.0/Math.Max(source.PixelWidth,source.PixelHeight));
            var small=new TransformedBitmap(source,new ScaleTransform(scale,scale));
            var converted=new FormatConvertedBitmap(small,PixelFormats.Bgr32,null,0);
            int stride=converted.PixelWidth*4;
            byte[] data=new byte[stride*converted.PixelHeight]; converted.CopyPixels(data,stride,0);
            var result=BitmapSource.Create(converted.PixelWidth,converted.PixelHeight,96,96,PixelFormats.Bgr32,null,data,stride);
            result.Freeze();return result;
        }
        internal static Rect Detect(BitmapSource frame)
        {
            int w=frame.PixelWidth,h=frame.PixelHeight,stride=w*4;
            byte[] data=new byte[stride*h];frame.CopyPixels(data,stride,0);
            Func<int,int,bool> black=(x,y)=>{int i=y*stride+x*4;return data[i]<=24 && data[i+1]<=24 && data[i+2]<=24;};
            Func<int,bool,bool> line=(n,row)=>{
                int count=row?w:h,dark=0;
                for(int i=0;i<count;i++)if(row?black(i,n):black(n,i))dark++;
                return dark>=count*0.99;
            };
            int top=0,bottom=h,left=0,right=w;
            while(top<h/3 && line(top,true))top++;
            while(bottom>h-h/3 && line(bottom-1,true))bottom--;
            while(left<w/3 && line(left,false))left++;
            while(right>w-w/3 && line(right-1,false))right--;
            // Reject dark scenes and keep a small margin around the detected content.
            if(top==h/3 || bottom==h-h/3 || left==w/3 || right==w-w/3)return new Rect(0,0,1,1);
            top=Math.Max(0,top-2);left=Math.Max(0,left-2);bottom=Math.Min(h,bottom+2);right=Math.Min(w,right+2);
            return new Rect((double)left/w,(double)top/h,(double)(right-left)/w,(double)(bottom-top)/h);
        }
        public void Edit(bool automatic)
        {
            if(editing)return;
            if(source==null){MessageBox.Show(owner,"请先连接手机并等待有效画面，再使用裁剪。","画面裁剪");return;}
            automaticOffered=true;
            editing=true;
            Window dialog=null;
            try {
                double originalAspect=(double)source.PixelWidth/source.PixelHeight;
                dialog=new Window {Owner=owner,Title="画面裁剪",Width=760,Height=650,MinWidth=500,MinHeight=400,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brushes.Black,Foreground=Brushes.White};
                var layout=new DockPanel {Margin=new Thickness(12)};
                var hint=new TextBlock {Text="正在采样画面，请保持红果视频播放…",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)};
                DockPanel.SetDock(hint,Dock.Top);layout.Children.Add(hint);
                var actions=new WrapPanel();DockPanel.SetDock(actions,Dock.Bottom);layout.Children.Add(actions);
                var view=new Grid {Background=Brushes.Black,ClipToBounds=true};layout.Children.Add(view);
                var picture=new Image {Stretch=Stretch.Uniform};view.Children.Add(picture);
                var overlay=new Canvas {Background=Brushes.Transparent};view.Children.Add(overlay);
                var box=new Rectangle {Stroke=Brushes.OrangeRed,StrokeThickness=2,Fill=new SolidColorBrush(Color.FromArgb(35,255,255,255)),IsHitTestVisible=false};overlay.Children.Add(box);
                Rect selected=new Rect(0,0,1,1); BitmapSource snapshot=Snapshot();picture.Source=snapshot;
                bool manual=false,ready=false,closed=false;Point? start=null;
                Func<Rect> bounds=()=>{double s=Math.Min(view.ActualWidth/snapshot.PixelWidth,view.ActualHeight/snapshot.PixelHeight);double w=snapshot.PixelWidth*s,h=snapshot.PixelHeight*s;return new Rect((view.ActualWidth-w)/2,(view.ActualHeight-h)/2,w,h);};
                Action draw=()=>{if(!manual){box.Visibility=Visibility.Collapsed;return;}Rect b=bounds();box.Visibility=Visibility.Visible;Canvas.SetLeft(box,b.X+selected.X*b.Width);Canvas.SetTop(box,b.Y+selected.Y*b.Height);box.Width=selected.Width*b.Width;box.Height=selected.Height*b.Height;};
                Action preview=()=>{manual=false;box.Visibility=Visibility.Collapsed;int x=(int)(selected.X*snapshot.PixelWidth),y=(int)(selected.Y*snapshot.PixelHeight);picture.Source=new CroppedBitmap(snapshot,new Int32Rect(x,y,Math.Max(1,Math.Min(snapshot.PixelWidth-x,(int)(selected.Width*snapshot.PixelWidth))),Math.Max(1,Math.Min(snapshot.PixelHeight-y,(int)(selected.Height*snapshot.PixelHeight)))));hint.Text="裁剪效果预览。保存后应用；不满意可手动裁剪，取消保留原设置。";};
                Func<string,Action,Button> button=(title,action)=>{var b=new Button {Content=title,Margin=new Thickness(4),Padding=new Thickness(10,7,10,7)};b.Click+=(s,e)=>action();actions.Children.Add(b);return b;};
                var save=button("保存裁剪",()=>{
                    if(!ready || !Valid(selected))return;
                    if(source==null || Math.Abs((double)source.PixelWidth/source.PixelHeight/originalAspect-1)>0.02){MessageBox.Show(dialog,"画面已停止或方向改变，请取消后重新裁剪。","画面裁剪");return;}
                    try { var c=CultureInfo.InvariantCulture;File.WriteAllText(path,string.Join("|",new[]{selected.X.ToString("R",c),selected.Y.ToString("R",c),selected.Width.ToString("R",c),selected.Height.ToString("R",c),originalAspect.ToString("R",c)}));region=selected;aspect=originalAspect;Display(source);dialog.Close(); }
                    catch(Exception e){MessageBox.Show(dialog,"保存失败："+e.Message,"画面裁剪");}
                });save.IsEnabled=false;
                var hand=button("手动裁剪",()=>{manual=true;picture.Source=snapshot;hint.Text="在完整原画面上拖动框选保留区域，然后预览或保存。";draw();});hand.IsEnabled=false;
                var show=button("预览",()=>{if(ready)preview();});show.IsEnabled=false;
                var reset=button("恢复完整画面",()=>{selected=new Rect(0,0,1,1);preview();});reset.IsEnabled=false;
                button("取消",()=>dialog.Close());
                Func<Point,Point> normalized=p=>{Rect b=bounds();return new Point(Math.Max(0,Math.Min(1,(p.X-b.X)/b.Width)),Math.Max(0,Math.Min(1,(p.Y-b.Y)/b.Height)));};
                overlay.MouseLeftButtonDown+=(s,e)=>{if(!manual)return;start=normalized(e.GetPosition(view));overlay.CaptureMouse();e.Handled=true;};
                overlay.MouseMove+=(s,e)=>{if(!start.HasValue)return;Point p=normalized(e.GetPosition(view));selected=new Rect(start.Value,p);draw();};
                overlay.MouseLeftButtonUp+=(s,e)=>{start=null;overlay.ReleaseMouseCapture();save.IsEnabled=Valid(selected);show.IsEnabled=Valid(selected);};
                view.SizeChanged+=(s,e)=>draw();dialog.Closed+=(s,e)=>{closed=true;};dialog.Content=layout;
                dialog.Loaded+=async(s,e)=>{
                    try {
                        Rect detected=new Rect(0,0,1,1);bool first=true;
                        for(int i=0;i<(automatic?6:1);i++){
                            if(closed)return;
                            if(source==null || Math.Abs((double)source.PixelWidth/source.PixelHeight/originalAspect-1)>0.02){hint.Text="画面已停止或方向改变，请关闭后重新裁剪。";return;}
                            snapshot=Snapshot();Rect r=Detect(snapshot);
                            // Only crop margins that remain black throughout all sampled frames.
                            if(first){detected=r;first=false;}else detected.Union(r);
                            if(automatic)await Task.Delay(400);
                        }
                        if(closed)return;
                        ready=true;selected=automatic?detected:Current;
                        save.IsEnabled=hand.IsEnabled=show.IsEnabled=reset.IsEnabled=true;
                        preview();if(!automatic)hand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }catch(Exception error){if(!closed)hint.Text="检测失败，请关闭后重试："+error.Message;}
                };
                dialog.ShowDialog();
            }catch(Exception error){MessageBox.Show(owner,"裁剪失败："+error.Message,"画面裁剪");}
            finally{editing=false;}
        }
    }
}
