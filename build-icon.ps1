param([string]$Source="$PSScriptRoot/src/PS7Studio.App/Assets/Studio.png",[string]$Destination="$PSScriptRoot/src/PS7Studio.App/Assets/Studio.ico")
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$image=[Drawing.Image]::FromFile($Source)
$sizes=@(16,20,24,32,40,48,64,128,256)
$frames=New-Object 'System.Collections.Generic.List[byte[]]'
try {
    foreach($size in $sizes){
        $bitmap=New-Object Drawing.Bitmap($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics=[Drawing.Graphics]::FromImage($bitmap)
        $stream=New-Object IO.MemoryStream
        try {
            $graphics.CompositingMode=[Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($image,0,0,$size,$size)
            $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($stream.ToArray())
        }finally{$graphics.Dispose();$bitmap.Dispose();$stream.Dispose()}
    }
}finally{$image.Dispose()}
$output=[IO.File]::Create($Destination)
$writer=New-Object IO.BinaryWriter($output)
try {
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
    $offset=6+16*$sizes.Count
    for($i=0;$i -lt $sizes.Count;$i++){
        $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
        $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([uint16]0)
        $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frames[$i].Length);$writer.Write([uint32]$offset)
        $offset+=$frames[$i].Length
    }
    foreach($frame in $frames){$writer.Write($frame)}
}finally{$writer.Dispose();$output.Dispose()}
Write-Output "Generated Windows icon with $($sizes.Count) sizes: $Destination"
