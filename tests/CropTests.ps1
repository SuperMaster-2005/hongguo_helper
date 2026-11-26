$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot '../bin/HongguoRemote.exe'))
$type=$assembly.GetType('HongguoRemote.VideoCrop')
$detect=$type.GetMethod('Detect',[Reflection.BindingFlags]'Static,NonPublic')
function Frame([int]$left,[int]$top,[int]$right,[int]$bottom,[bool]$dark=$false) {
 $pixels=New-Object byte[] (120*180*4)
 for($y=$top;$y -lt (180-$bottom);$y++){for($x=$left;$x -lt (120-$right);$x++){if(!$dark){$i=($y*120+$x)*4;$pixels[$i]=180;$pixels[$i+1]=100;$pixels[$i+2]=50}}}
 return [Windows.Media.Imaging.BitmapSource]::Create(120,180,96,96,[Windows.Media.PixelFormats]::Bgr32,$null,$pixels,480)
}
function Check($condition,$label){if(!$condition){throw "FAIL: $label"};Write-Output "PASS: $label"}
$r=$detect.Invoke($null,@((Frame 0 20 0 15)))
Check ($r.Top -gt 0.09 -and $r.Bottom -lt 0.94 -and $r.Width -eq 1) 'top and bottom borders'
$r=$detect.Invoke($null,@((Frame 12 0 10 0)))
Check ($r.Left -gt 0.07 -and $r.Right -lt 0.95 -and $r.Height -eq 1) 'left and right borders'
$r=$detect.Invoke($null,@((Frame 0 0 0 0)))
Check ($r.Width -eq 1 -and $r.Height -eq 1) 'full frame preserved'
$r=$detect.Invoke($null,@((Frame 0 0 0 0 $true)))
Check ($r.Width -eq 1 -and $r.Height -eq 1) 'black frame rejected'
$window=New-Object Windows.Window
$image=New-Object Windows.Controls.Image
$crop=$type.GetConstructors()[0].Invoke([object[]]@($window.PSObject.BaseObject,$image.PSObject.BaseObject))
$type.GetField('region',[Reflection.BindingFlags]'Instance,NonPublic').SetValue($crop,[Windows.Rect]::new(0.1,0.1,0.8,0.8))
$type.GetField('aspect',[Reflection.BindingFlags]'Instance,NonPublic').SetValue($crop,(120.0/180))
$bitmap=New-Object Windows.Media.Imaging.WriteableBitmap 120,180,96,96,([Windows.Media.PixelFormats]::Bgr32),$null
$type.GetMethod('Display').Invoke($crop,@($bitmap.PSObject.BaseObject))
Check ($image.Source.PixelWidth -eq 96 -and $image.Source.PixelHeight -eq 144) 'display dimensions'
$point=$type.GetMethod('Map').Invoke($crop,@([Windows.Point]::new(0,0)))
Check ([Math]::Abs($point.X-1000) -lt 1 -and [Math]::Abs($point.Y-1000) -lt 1) 'touch mapping to original frame'
$data=New-Object byte[] (120*180*4);for($i=0;$i -lt $data.Length;$i+=4){$data[$i]=200}
$bitmap.WritePixels([Windows.Int32Rect]::new(0,0,120,180),$data,480,0)
$type.GetMethod('Refresh').Invoke($crop,@())
$read=New-Object byte[] (96*144*4);$image.Source.CopyPixels($read,384,0)
Check ($read[0] -eq 200) 'cropped image updates with live bitmap'
$type.GetMethod('Display').Invoke($crop,@((Frame 0 0 0 0)))
$landscape=[Windows.Media.Imaging.BitmapSource]::Create(180,120,96,96,[Windows.Media.PixelFormats]::Bgr32,$null,(New-Object byte[] (180*120*4)),720)
$type.GetMethod('Display').Invoke($crop,@($landscape.PSObject.BaseObject))
Check ($image.Source.PixelWidth -eq 180) 'orientation change restores full frame'
$reader=New-Object IO.StreamReader (Join-Path $PSScriptRoot '../src/MainWindow.xaml')
try{$parsed=[Windows.Markup.XamlReader]::Parse($reader.ReadToEnd());Check ($null -ne $parsed.FindName('Crop')) 'XAML crop entry loads'}finally{$reader.Dispose()}



# Render actual WPF output across repeated updates, rather than only reading source pixels.
$type.GetMethod('Display').Invoke($crop,@($bitmap.PSObject.BaseObject))
$image.Width=96;$image.Height=144
$image.Measure([Windows.Size]::new(96,144));$image.Arrange([Windows.Rect]::new(0,0,96,144))
for($frameNumber=1;$frameNumber -le 20;$frameNumber++){
 $color=[byte]($frameNumber*10)
 for($i=0;$i -lt $data.Length;$i+=4){$data[$i]=$color}
 $bitmap.WritePixels([Windows.Int32Rect]::new(0,0,120,180),$data,480,0)
 $type.GetMethod('Refresh').Invoke($crop,@())
 $render=New-Object Windows.Media.Imaging.RenderTargetBitmap 96,144,96,96,([Windows.Media.PixelFormats]::Pbgra32)
 $render.Render($image)
 $output=New-Object byte[] (96*144*4);$render.CopyPixels($output,384,0)
 if($output[0] -ne $color){throw "FAIL: rendered frame $frameNumber remained stale"}
}
Check $true '20 consecutive rendered cropped frames update'
$type.GetField('automaticOffered',[Reflection.BindingFlags]'Instance,NonPublic').SetValue($crop,$false)
Check $type.GetProperty('NeedsAutomaticCrop').GetValue($crop,$null) 'first fullscreen requests automatic crop'
$type.GetField('automaticOffered',[Reflection.BindingFlags]'Instance,NonPublic').SetValue($crop,$true)
Check (!$type.GetProperty('NeedsAutomaticCrop').GetValue($crop,$null)) 'subsequent fullscreen skips automatic crop'
$type.GetMethod('Display').Invoke($crop,[object[]]@($null))
Check (!$type.GetProperty('NeedsAutomaticCrop').GetValue($crop,$null)) 'disconnected frame cannot trigger crop'

# Exercise the direct decoded-buffer path used by the real player.
$decodedType=$assembly.GetType('HongguoRemote.DecodedFrame')
$present=$type.GetMethod('Present')
$type.GetMethod('Display').Invoke($crop,@($bitmap.PSObject.BaseObject))
$image.Measure([Windows.Size]::new(96,144));$image.Arrange([Windows.Rect]::new(0,0,96,144))
$previousFrame=$null
for($frameNumber=1;$frameNumber -le 20;$frameNumber++){
 $decoded=[Activator]::CreateInstance($decodedType)
 $decoded.Width=120;$decoded.Height=180
 $decoded.Pixels=New-Object byte[] (120*180*4)
 for($y=0;$y -lt 180;$y++){for($x=0;$x -lt 120;$x++){
  $i=($y*120+$x)*4;$decoded.Pixels[$i]=[byte]$frameNumber;$decoded.Pixels[$i+1]=[byte]$x;$decoded.Pixels[$i+2]=[byte]$y
 }}
 if(!$present.Invoke($crop,@($decoded))){throw "FAIL: direct frame $frameNumber not accepted"}
 if($null -ne $previousFrame -and $decodedType.GetField('returned',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($previousFrame) -ne 1){throw 'FAIL: previous pooled frame not released'}
 $previousFrame=$decoded
 $render=New-Object Windows.Media.Imaging.RenderTargetBitmap 96,144,96,96,([Windows.Media.PixelFormats]::Pbgra32)
 $render.Render($image);$output=New-Object byte[] (96*144*4);$render.CopyPixels($output,384,0)
 if($output[0] -ne $frameNumber -or $output[1] -ne 12 -or $output[2] -ne 18){throw "FAIL: direct cropped render or top-left offset: $($output[0]),$($output[1]),$($output[2])"}
 $last=(96*144-1)*4
 if($output[$last+1] -ne 107 -or $output[$last+2] -ne 161){throw 'FAIL: bottom-right crop offset'}
}
Check $true '20 direct frames render with correct crop offsets'
$snapshot=$type.GetMethod('Snapshot',[Reflection.BindingFlags]'Instance,NonPublic').Invoke($crop,@())
$full=New-Object byte[] (120*180*4);$snapshot.CopyPixels($full,480,0)
Check ($full[0] -eq 20 -and $full[(179*120+119)*4+1] -eq 119) 'on-demand original snapshot stays current'
$type.GetMethod('Display').Invoke($crop,[object[]]@($null))
Check ($decodedType.GetField('returned',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($previousFrame) -eq 1) 'disconnect releases retained pooled frame'

$timingType=$assembly.GetType('HongguoRemote.SourceFrameTiming')
$timing=[Activator]::CreateInstance($timingType,$true)
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$observe=$timingType.GetMethod('Observe',$flags);$summary=$timingType.GetMethod('TakeSummary',$flags);$reset=$timingType.GetMethod('Reset',$flags)
foreach($rate in @(30,60,120)){
 $reset.Invoke($timing,@())
 for($i=0;$i -le 120;$i++){$observe.Invoke($timing,@([long][Math]::Round($i*1000000.0/$rate)))}
 $text=$summary.Invoke($timing,@());Check ($text.Contains($rate.ToString('F1'))) "phone timestamp rate $rate"
}
$reset.Invoke($timing,@());$observe.Invoke($timing,@([long]1000000));$observe.Invoke($timing,@([long]900000))
Check ($timingType.GetField('discontinuities',$flags).GetValue($timing) -eq 1) 'backward timestamp recorded without bogus rate'
$reset.Invoke($timing,@());Check ($timingType.GetField('previous',$flags).GetValue($timing) -eq -1) 'format and sharing reset discard timestamp history'

# Cover contiguous rows as well as the side-cropped row-copy path.
foreach($selection in @([Windows.Rect]::new(0,0,1,1),[Windows.Rect]::new(0,0.1,1,0.8))){
 $type.GetField('region',[Reflection.BindingFlags]'Instance,NonPublic').SetValue($crop,$selection)
 $type.GetMethod('Display').Invoke($crop,@($bitmap.PSObject.BaseObject))
 $decoded=[Activator]::CreateInstance($decodedType);$decoded.Width=120;$decoded.Height=180;$decoded.Pixels=$data
 Check ($present.Invoke($crop,@($decoded))) 'contiguous full-width frame presented'
 $actual=New-Object byte[] ($image.Source.PixelWidth*$image.Source.PixelHeight*4)
 $image.Source.CopyPixels($actual,$image.Source.PixelWidth*4,0)
 Check ($actual[0] -eq 200 -and $actual[$actual.Length-4] -eq 200) 'contiguous frame includes first and last pixel'
 $type.GetMethod('Display').Invoke($crop,[object[]]@($null))
}
